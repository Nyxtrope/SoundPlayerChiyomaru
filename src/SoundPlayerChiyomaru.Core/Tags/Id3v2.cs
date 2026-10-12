using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>ID3v2.2 / 2.3 / 2.4 のタグ。MP3 の先頭のほか、FLAC の先頭や WAV のチャンクの中にも置かれる。</summary>
internal static class Id3v2
{
    private const int HeaderSize = 10;

    private enum FrameKind
    {
        None,
        Title,
        Artist,
        Album,
        AlbumArtist,
        Track,
        UserText,
        Picture,
    }

    /// <summary>
    /// ストリームの今の位置に ID3v2 のタグがあれば読む。読んだ後、位置はタグの直後に進む。なければ位置は変わらない。
    /// </summary>
    /// <returns>タグ全体の大きさ（バイト）。タグがなければ 0。</returns>
    public static long Read(Stream stream, TagData data, TagText text, bool wantPicture)
    {
        long start = stream.Position;
        if (stream.Remaining() < HeaderSize)
        {
            return 0;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header.StartsWith("ID3"u8)
            || header[3] == 0xFF
            || header[4] == 0xFF
            || ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0)
        {
            stream.Position = start;
            return 0;
        }

        int version = header[3];
        int flags = header[5];
        bool hasFooter = version >= 4 && (flags & 0x10) != 0;
        long bodyEnd = Math.Min(start + HeaderSize + Syncsafe(header[6..]), stream.Length);
        long tagEnd = Math.Min(bodyEnd + (hasFooter ? HeaderSize : 0), stream.Length);

        try
        {
            if (version is >= 2 and <= 4)
            {
                ReadBody(stream, bodyEnd, version, flags, data, text, wantPicture);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // タグの途中が壊れていても、そこまでに読めた項目と、タグの後ろにある音声の情報は使える。
        }

        stream.Position = tagEnd;
        return tagEnd - start;
    }

    private static void ReadBody(Stream stream, long bodyEnd, int version, int flags, TagData data, TagText text, bool wantPicture)
    {
        Stream body = stream;
        if ((flags & 0x80) != 0 && version < 4)
        {
            // 2.2 と 2.3 の非同期化はタグ全体に掛かっていて、フレームの大きさは元に戻した後のもの。先に全体を戻す。
            byte[] raw = stream.ReadBytes(bodyEnd - stream.Position);
            int length = RemoveUnsynchronization(raw);
            body = new MemoryStream(raw, 0, length, writable: false);
            bodyEnd = length;
        }

        if ((flags & 0x40) != 0)
        {
            if (version == 2)
            {
                // 2.2 ではこのビットは「圧縮」を表すが、圧縮の方式が定められていないので読めない。
                return;
            }

            Span<byte> size = stackalloc byte[4];
            body.ReadExactly(size);
            body.Skip(version == 3
                ? BinaryPrimitives.ReadUInt32BigEndian(size)
                : Math.Max(0, Syncsafe(size) - 4));
        }

        bool allUnsynchronized = version == 4 && (flags & 0x80) != 0;
        int frameHeaderSize = version == 2 ? 6 : 10;
        Span<byte> frame = stackalloc byte[10];
        while (body.Position + frameHeaderSize <= bodyEnd)
        {
            frame = frame[..frameHeaderSize];
            body.ReadExactly(frame);
            ReadOnlySpan<byte> id = frame[..(version == 2 ? 3 : 4)];
            if (!IsFrameId(id))
            {
                // 残りは詰め物（0 で埋めた領域）か、壊れている。
                break;
            }

            long size = version switch
            {
                2 => (frame[3] << 16) | (frame[4] << 8) | frame[5],
                3 => BinaryPrimitives.ReadUInt32BigEndian(frame[4..]),
                _ => FrameSizeV4(body, frame.Slice(4, 4), bodyEnd),
            };
            int frameFlags = version == 2 ? 0 : (frame[8] << 8) | frame[9];
            long next = body.Position + size;
            if (next > bodyEnd)
            {
                break;
            }

            FrameKind kind = Classify(id);
            bool wanted = kind switch
            {
                FrameKind.None => false,
                FrameKind.Picture => wantPicture && size <= TagIo.MaxPicture,
                _ => size <= TagIo.MaxText,
            };
            if (wanted && size > 0)
            {
                Span<byte> content = body.ReadBytes(size);
                if (TryUnwrap(ref content, version, frameFlags, allUnsynchronized))
                {
                    HandleFrame(kind, content, data, text, version);
                }
            }
            body.Position = next;
        }
    }

    /// <summary>フレームのフラグに従って、中身の前に付いた付加情報を外し、非同期化を戻す。</summary>
    /// <returns>読める形になったか。圧縮・暗号化されたフレームは読めない。</returns>
    private static bool TryUnwrap(ref Span<byte> content, int version, int flags, bool allUnsynchronized)
    {
        int prefix = 0;
        bool unsynchronized = false;
        if (version == 3)
        {
            if ((flags & 0xC0) != 0)
            {
                return false;
            }
            prefix = (flags & 0x20) != 0 ? 1 : 0;
        }
        else if (version == 4)
        {
            if ((flags & 0x0C) != 0)
            {
                return false;
            }
            prefix = ((flags & 0x40) != 0 ? 1 : 0) + ((flags & 0x01) != 0 ? 4 : 0);
            unsynchronized = allUnsynchronized || (flags & 0x02) != 0;
        }

        if (prefix > content.Length)
        {
            return false;
        }
        content = content[prefix..];
        if (unsynchronized)
        {
            content = content[..RemoveUnsynchronization(content)];
        }
        return true;
    }

    private static void HandleFrame(FrameKind kind, ReadOnlySpan<byte> content, TagData data, TagText text, int version)
    {
        switch (kind)
        {
            case FrameKind.Title:
                data.OfferTitle(ReadText(content, text));
                break;
            case FrameKind.Artist:
                data.OfferArtist(ReadText(content, text));
                break;
            case FrameKind.Album:
                data.OfferAlbum(ReadText(content, text));
                break;
            case FrameKind.AlbumArtist:
                data.OfferAlbumArtist(ReadText(content, text));
                break;
            case FrameKind.Track:
                data.OfferTrackNumber(ReadText(content, text));
                break;
            case FrameKind.UserText:
                ReadUserText(content, data, text);
                break;
            case FrameKind.Picture:
                ReadPicture(content, data, version);
                break;
        }
    }

    private static string? ReadText(ReadOnlySpan<byte> content, TagText text) =>
        content.Length < 2 ? null : TagText.JoinValues(Decode(content[0], content[1..], text));

    /// <summary>利用者定義のテキスト（説明と値の組）。ReplayGain はここに書かれる。</summary>
    private static void ReadUserText(ReadOnlySpan<byte> content, TagData data, TagText text)
    {
        if (content.Length < 2)
        {
            return;
        }

        string[] parts = Decode(content[0], content[1..], text).Split('\0');
        if (parts.Length < 2)
        {
            return;
        }

        string? value = parts.Skip(1).Select(part => part.Trim('﻿')).FirstOrDefault(part => part.Length > 0);
        data.OfferReplayGain(parts[0].AsSpan().Trim('﻿'), value);
    }

    private static void ReadPicture(ReadOnlySpan<byte> content, TagData data, int version)
    {
        if (content.Length < 2)
        {
            return;
        }

        int encoding = content[0];
        content = content[1..];
        if (version == 2)
        {
            // 2.2 は画像の形式を 3 文字で持つ。
            if (content.Length < 3)
            {
                return;
            }
            content = content[3..];
        }
        else
        {
            int mimeEnd = content.IndexOf((byte)0);
            if (mimeEnd < 0)
            {
                return;
            }
            content = content[(mimeEnd + 1)..];
        }

        if (content.Length < 1)
        {
            return;
        }
        int type = content[0];
        content = content[1..];

        int descriptionEnd = TerminatorEnd(content, wide: encoding is 1 or 2);
        if (descriptionEnd >= 0)
        {
            data.OfferPicture(type, content[descriptionEnd..].ToArray());
        }
    }

    /// <summary>文字列の終端（NUL。UTF-16 では 2 バイトの NUL）の直後の位置。終端がなければ -1。</summary>
    private static int TerminatorEnd(ReadOnlySpan<byte> bytes, bool wide)
    {
        if (!wide)
        {
            int end = bytes.IndexOf((byte)0);
            return end < 0 ? -1 : end + 1;
        }

        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                return i + 2;
            }
        }
        return -1;
    }

    private static string Decode(int encoding, ReadOnlySpan<byte> bytes, TagText text) => encoding switch
    {
        1 => TagText.Utf16(bytes),
        2 => TagText.Utf16BigEndian(bytes),
        3 => TagText.Utf8(bytes),
        _ => text.Legacy(bytes),
    };

    private static FrameKind Classify(ReadOnlySpan<byte> id)
    {
        if (id.SequenceEqual("TIT2"u8) || id.SequenceEqual("TT2"u8))
        {
            return FrameKind.Title;
        }
        if (id.SequenceEqual("TPE1"u8) || id.SequenceEqual("TP1"u8))
        {
            return FrameKind.Artist;
        }
        if (id.SequenceEqual("TALB"u8) || id.SequenceEqual("TAL"u8))
        {
            return FrameKind.Album;
        }
        if (id.SequenceEqual("TPE2"u8) || id.SequenceEqual("TP2"u8))
        {
            return FrameKind.AlbumArtist;
        }
        if (id.SequenceEqual("TRCK"u8) || id.SequenceEqual("TRK"u8))
        {
            return FrameKind.Track;
        }
        if (id.SequenceEqual("TXXX"u8) || id.SequenceEqual("TXX"u8))
        {
            return FrameKind.UserText;
        }
        if (id.SequenceEqual("APIC"u8) || id.SequenceEqual("PIC"u8))
        {
            return FrameKind.Picture;
        }
        return FrameKind.None;
    }

    /// <summary>
    /// 2.4 のフレームの大きさ。規格では 7 ビットずつ詰めた値だが、普通の 32 ビットの値で書くソフト（古い iTunes など）がある。
    /// 2 つの読み方で結果が違う場合は、その大きさだけ進んだ先が次のフレームの先頭らしく見えるほうを採る。
    /// </summary>
    private static long FrameSizeV4(Stream body, ReadOnlySpan<byte> size, long bodyEnd)
    {
        long plain = BinaryPrimitives.ReadUInt32BigEndian(size);
        if (((size[0] | size[1] | size[2] | size[3]) & 0x80) != 0)
        {
            return plain;
        }

        long syncsafe = Syncsafe(size);
        if (syncsafe == plain || EndsAtFrameBoundary(body, body.Position + syncsafe, bodyEnd))
        {
            return syncsafe;
        }
        return EndsAtFrameBoundary(body, body.Position + plain, bodyEnd) ? plain : syncsafe;
    }

    private static bool EndsAtFrameBoundary(Stream body, long position, long bodyEnd)
    {
        if (position == bodyEnd)
        {
            return true;
        }
        if (position + 4 > bodyEnd)
        {
            return false;
        }

        long saved = body.Position;
        Span<byte> id = stackalloc byte[4];
        body.Position = position;
        body.ReadExactly(id);
        body.Position = saved;
        return id[0] == 0 || IsFrameId(id);
    }

    private static bool IsFrameId(ReadOnlySpan<byte> id)
    {
        foreach (byte c in id)
        {
            if (c is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')))
            {
                return false;
            }
        }
        return true;
    }

    private static int Syncsafe(ReadOnlySpan<byte> bytes) =>
        ((bytes[0] & 0x7F) << 21) | ((bytes[1] & 0x7F) << 14) | ((bytes[2] & 0x7F) << 7) | (bytes[3] & 0x7F);

    /// <summary>非同期化（0xFF の後ろに挿入された 0x00）を取り除く。その場で詰め、詰めた後の長さを返す。</summary>
    private static int RemoveUnsynchronization(Span<byte> bytes)
    {
        int write = 0;
        for (int read = 0; read < bytes.Length; read++)
        {
            bytes[write++] = bytes[read];
            if (bytes[read] == 0xFF && read + 1 < bytes.Length && bytes[read + 1] == 0)
            {
                read++;
            }
        }
        return write;
    }
}
