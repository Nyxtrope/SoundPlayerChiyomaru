using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>
/// MP3 のタグと長さ。先頭の ID3v2、末尾の APEv2 と ID3v1 を、この優先順で読む。
/// 長さは最初の音声フレームから求める（VBR の情報があればそれを使い、なければビットレートから計算する）。
/// </summary>
internal static class MpegTags
{
    private const int Id3v1Size = 128;
    private const int ApeFooterSize = 32;

    /// <summary>最初の音声フレームを探す範囲。タグの後ろに詰め物やごみが入っていることがある。</summary>
    private const int SyncSearchLimit = 1 << 20;

    private static readonly int[][] Bitrates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ];

    private static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000],
        [],
        [22050, 24000, 16000],
        [44100, 48000, 32000],
    ];

    private readonly record struct FrameHeader(int Version, int Layer, int Bitrate, int SampleRate, bool Padding, bool Mono, bool Crc)
    {
        public int Length => Layer switch
        {
            1 => ((12 * Bitrate / SampleRate) + (Padding ? 1 : 0)) * 4,
            2 => (144 * Bitrate / SampleRate) + (Padding ? 1 : 0),
            _ => ((Version == 1 ? 144 : 72) * Bitrate / SampleRate) + (Padding ? 1 : 0),
        };

        public int SamplesPerFrame => Layer switch
        {
            1 => 384,
            2 => 1152,
            _ => Version == 1 ? 1152 : 576,
        };

        /// <summary>
        /// フレームの先頭から、VBR の情報（Xing / Info）が置かれる位置まで。副情報の後ろに置かれ、
        /// 誤り検出の符号（CRC）が付いたフレームではその 2 バイト分だけ後ろにずれる。
        /// </summary>
        public int XingOffset => 4 + (Crc ? 2 : 0) + (Version == 1 ? (Mono ? 17 : 32) : (Mono ? 9 : 17));
    }

    /// <returns>MP3 として読めたか（タグか音声フレームのどちらかが見つかった）。</returns>
    public static bool Read(Stream stream, TagData data, TagText text, bool wantPicture)
    {
        bool found = false;

        // ID3v2 が続けて 2 つ書かれているファイルがある。
        for (int i = 0; i < 4 && Id3v2.Read(stream, data, text, wantPicture) > 0; i++)
        {
            found = true;
        }
        long audioStart = stream.Position;
        long audioEnd = stream.Length;

        byte[]? id3v1 = ReadId3v1(stream, audioStart);
        if (id3v1 is not null)
        {
            audioEnd -= Id3v1Size;
            found = true;
        }

        long ape = ReadApe(stream, audioStart, audioEnd, data, wantPicture);
        if (ape > 0)
        {
            audioEnd -= ape;
            found = true;
        }

        if (id3v1 is not null)
        {
            ApplyId3v1(id3v1, data, text);
        }

        if (ReadDuration(stream, audioStart, audioEnd) is { } duration)
        {
            data.Duration = duration;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// 末尾のタグ（APEv2 と ID3v1）だけを読む。生の AAC（ADTS）のように、音声の形式は違うがタグの置き方は MP3 と同じファイル用。
    /// </summary>
    public static bool ReadTagsOnly(Stream stream, TagData data, TagText text, bool wantPicture)
    {
        bool found = Id3v2.Read(stream, data, text, wantPicture) > 0;
        long audioStart = stream.Position;
        long audioEnd = stream.Length;

        byte[]? id3v1 = ReadId3v1(stream, audioStart);
        if (id3v1 is not null)
        {
            audioEnd -= Id3v1Size;
        }
        found |= ReadApe(stream, audioStart, audioEnd, data, wantPicture) > 0;
        if (id3v1 is not null)
        {
            ApplyId3v1(id3v1, data, text);
            found = true;
        }
        return found;
    }

    private static byte[]? ReadId3v1(Stream stream, long audioStart)
    {
        if (stream.Length - audioStart < Id3v1Size)
        {
            return null;
        }

        stream.Position = stream.Length - Id3v1Size;
        byte[] tag = stream.ReadBytes(Id3v1Size);
        return tag.AsSpan().StartsWith("TAG"u8) ? tag : null;
    }

    private static void ApplyId3v1(ReadOnlySpan<byte> tag, TagData data, TagText text)
    {
        // 欄は 30 バイトの固定長で、長い文字列は文字の途中で切られていることがある。
        data.OfferTitle(text.Legacy(TagText.UntilNul(tag.Slice(3, 30)), mayBeCut: true));
        data.OfferArtist(text.Legacy(TagText.UntilNul(tag.Slice(33, 30)), mayBeCut: true));
        data.OfferAlbum(text.Legacy(TagText.UntilNul(tag.Slice(63, 30)), mayBeCut: true));

        // ID3v1.1: コメントの最後の 2 バイトが「0、トラック番号」になっている。
        if (tag[125] == 0 && tag[126] != 0)
        {
            data.OfferTrackNumber(tag[126]);
        }
    }

    /// <summary>音声の末尾（<paramref name="audioEnd"/> の直前）に APEv2 のタグがあれば読む。</summary>
    /// <returns>タグ全体の大きさ（バイト）。タグがなければ 0。</returns>
    private static long ReadApe(Stream stream, long audioStart, long audioEnd, TagData data, bool wantPicture)
    {
        long footerStart = audioEnd - ApeFooterSize;
        if (footerStart < audioStart)
        {
            return 0;
        }

        stream.Position = footerStart;
        Span<byte> footer = stackalloc byte[ApeFooterSize];
        stream.ReadExactly(footer);
        if (!footer.StartsWith("APETAGEX"u8))
        {
            return 0;
        }

        // 大きさは、項目の並びと末尾の 32 バイトを合わせたもの。先頭にも同じ形の 32 バイトが付くことがあり、それは含まれない。
        long size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]);
        long itemsStart = audioEnd - size;
        if (size < ApeFooterSize || itemsStart < audioStart)
        {
            return 0;
        }

        long total = size;
        if ((flags & 0x8000_0000) != 0 && itemsStart - ApeFooterSize >= audioStart)
        {
            total += ApeFooterSize;
        }

        try
        {
            ReadApeItems(stream, itemsStart, footerStart, count, data, wantPicture);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // 項目の並びが壊れていても、そこまでに読めた項目と、音声の情報は使える。
        }
        return total;
    }

    private static void ReadApeItems(Stream stream, long start, long end, uint count, TagData data, bool wantPicture)
    {
        stream.Position = start;
        Span<byte> keyBuffer = stackalloc byte[256];
        for (uint i = 0; i < count && stream.Position + 9 <= end; i++)
        {
            long valueSize = stream.ReadUInt32LittleEndian();
            uint itemFlags = stream.ReadUInt32LittleEndian();

            int keyLength = 0;
            while (true)
            {
                int b = stream.ReadByte();
                if (b <= 0)
                {
                    break;
                }
                if (keyLength == keyBuffer.Length || stream.Position > end)
                {
                    throw new InvalidDataException();
                }
                keyBuffer[keyLength++] = (byte)b;
            }

            long next = stream.Position + valueSize;
            if (next > end)
            {
                throw new InvalidDataException();
            }

            string key = TagText.Utf8(keyBuffer[..keyLength]);
            bool binary = ((itemFlags >> 1) & 3) == 1;
            if (key.Equals("Cover Art (Front)", StringComparison.OrdinalIgnoreCase))
            {
                if (wantPicture && valueSize <= TagIo.MaxPicture)
                {
                    // 中身は「ファイル名、NUL、画像」。
                    byte[] value = stream.ReadBytes(valueSize);
                    int nameEnd = value.AsSpan().IndexOf((byte)0);
                    if (nameEnd >= 0)
                    {
                        data.OfferPicture(TagData.FrontCover, value[(nameEnd + 1)..]);
                    }
                }
            }
            else if (!binary && valueSize <= TagIo.MaxText && IsWanted(key))
            {
                string? value = TagText.JoinValues(TagText.Utf8(stream.ReadBytes(valueSize)));
                if (key.Equals("Title", StringComparison.OrdinalIgnoreCase))
                {
                    data.OfferTitle(value);
                }
                else if (key.Equals("Artist", StringComparison.OrdinalIgnoreCase))
                {
                    data.OfferArtist(value);
                }
                else if (key.Equals("Album", StringComparison.OrdinalIgnoreCase))
                {
                    data.OfferAlbum(value);
                }
                else if (IsAlbumArtist(key))
                {
                    data.OfferAlbumArtist(value);
                }
                else if (key.Equals("Track", StringComparison.OrdinalIgnoreCase))
                {
                    data.OfferTrackNumber(value);
                }
                else
                {
                    data.OfferReplayGain(key, value);
                }
            }
            stream.Position = next;
        }
    }

    private static bool IsWanted(string key) =>
        key.Equals("Title", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Artist", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Album", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Track", StringComparison.OrdinalIgnoreCase)
        || IsAlbumArtist(key)
        || key.StartsWith("replaygain_", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlbumArtist(string key) =>
        key.Equals("Album Artist", StringComparison.OrdinalIgnoreCase)
        || key.Equals("AlbumArtist", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ReadDuration(Stream stream, long audioStart, long audioEnd)
    {
        if (FindFirstFrame(stream, audioStart, audioEnd) is not { } first)
        {
            return null;
        }
        (long frameStart, FrameHeader header) = first;

        // VBR の情報は最初のフレームの中に置かれる。Xing / Info は副情報の後ろ、VBRI は常に 36 バイト目。
        stream.Position = frameStart;
        byte[] frame = stream.ReadBytes(Math.Min(Math.Max(header.XingOffset, 36) + 32, audioEnd - frameStart));
        long frames = 0;
        ReadOnlySpan<byte> xing = frame.AsSpan(Math.Min(header.XingOffset, frame.Length));
        if (xing.Length >= 12 && (xing.StartsWith("Xing"u8) || xing.StartsWith("Info"u8)))
        {
            if ((BinaryPrimitives.ReadUInt32BigEndian(xing[4..]) & 1) != 0)
            {
                frames = BinaryPrimitives.ReadUInt32BigEndian(xing[8..]);
            }
        }
        else if (frame.Length >= 36 + 18 && frame.AsSpan(36).StartsWith("VBRI"u8))
        {
            frames = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(36 + 14));
        }

        double seconds = frames > 0
            ? (double)frames * header.SamplesPerFrame / header.SampleRate
            : (audioEnd - frameStart) * 8.0 / header.Bitrate;
        return seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>
    /// 最初の音声フレームを探す。フレームの先頭を示す並びは音声以外のデータにも偶然現れるので、
    /// その長さだけ進んだ先にも同じ種類のフレームが続いていることを確かめる。
    /// </summary>
    private static (long Start, FrameHeader Header)? FindFirstFrame(Stream stream, long audioStart, long audioEnd)
    {
        long limit = Math.Min(audioEnd, audioStart + SyncSearchLimit);
        byte[] buffer = new byte[4096];
        Span<byte> following = stackalloc byte[4];

        // 少しずつ読む。ほとんどのファイルでは、タグの直後が最初のフレームになっている。
        for (long chunkStart = audioStart; chunkStart + 4 <= limit; chunkStart += buffer.Length - 3)
        {
            stream.Position = chunkStart;
            int length = stream.ReadAtLeast(buffer.AsSpan(0, (int)Math.Min(buffer.Length, limit - chunkStart)), 4, throwOnEndOfStream: false);
            for (int i = 0; i + 4 <= length; i++)
            {
                if (buffer[i] != 0xFF || ParseHeader(buffer.AsSpan(i, 4)) is not { } header)
                {
                    continue;
                }

                long start = chunkStart + i;
                long next = start + header.Length;
                if (next + 4 > audioEnd)
                {
                    // 後ろに続くフレームがなく、確かめようがない。タグの直後から始まる 1 フレームだけの音声としてだけ認める。
                    if (start == audioStart)
                    {
                        return (start, header);
                    }
                    continue;
                }

                stream.Position = next;
                stream.ReadExactly(following);
                if (ParseHeader(following) is { } second
                    && second.Version == header.Version
                    && second.Layer == header.Layer
                    && second.SampleRate == header.SampleRate)
                {
                    return (start, header);
                }
            }
        }
        return null;
    }

    private static FrameHeader? ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes[0] != 0xFF || (bytes[1] & 0xE0) != 0xE0)
        {
            return null;
        }

        int versionBits = (bytes[1] >> 3) & 3;
        int layerBits = (bytes[1] >> 1) & 3;
        int bitrateIndex = bytes[2] >> 4;
        int sampleRateIndex = (bytes[2] >> 2) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
        {
            return null;
        }

        int version = versionBits == 3 ? 1 : 2;
        int layer = 4 - layerBits;
        int table = version == 1 ? layer - 1 : (layer == 1 ? 3 : 4);
        var header = new FrameHeader(
            version,
            layer,
            Bitrates[table][bitrateIndex] * 1000,
            SampleRates[versionBits][sampleRateIndex],
            Padding: (bytes[2] & 2) != 0,
            Mono: (bytes[3] >> 6) == 3,
            Crc: (bytes[1] & 1) == 0);
        return header.Length >= 4 ? header : null;
    }
}
