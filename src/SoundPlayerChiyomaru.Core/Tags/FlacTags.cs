using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>FLAC のメタデータブロック（STREAMINFO、Vorbis コメント、PICTURE）。</summary>
internal static class FlacTags
{
    private const int StreamInfo = 0;
    private const int VorbisComment = 4;
    private const int Picture = 6;

    /// <summary>読むブロックの数の上限。壊れたファイルで、中身のないブロックを延々と読み続けないため。</summary>
    private const int MaxBlocks = 1024;

    public static bool Read(Stream stream, TagData data, TagText text, bool wantPicture)
    {
        // 先頭に ID3v2 を付けるソフトがある。FLAC 本来のタグを優先し、足りない項目だけを後から埋める。
        var id3 = new TagData();
        Id3v2.Read(stream, id3, text, wantPicture);

        Span<byte> header = stackalloc byte[4];
        if (stream.Remaining() < 4)
        {
            return false;
        }
        stream.ReadExactly(header);
        if (!header.SequenceEqual("fLaC"u8))
        {
            return false;
        }

        bool last = false;
        for (int i = 0; i < MaxBlocks && !last && stream.Remaining() >= 4; i++)
        {
            stream.ReadExactly(header);
            last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = (header[1] << 16) | (header[2] << 8) | header[3];
            long next = stream.Position + length;
            if (next > stream.Length)
            {
                break;
            }

            if (type == StreamInfo && length >= 18)
            {
                ReadStreamInfo(stream.ReadBytes(18), data);
            }
            else if (type == VorbisComment)
            {
                ReadComments(stream, next, data);
            }
            else if (type == Picture && wantPicture)
            {
                ReadPicture(stream.ReadBytes(length), data);
            }
            stream.Position = next;
        }

        data.FillFrom(id3);
        return true;
    }

    private static void ReadStreamInfo(ReadOnlySpan<byte> info, TagData data)
    {
        // 10 バイト目から: サンプルレート 20 ビット、チャンネル数 3 ビット、ビット数 5 ビット、総サンプル数 36 ビット。
        int sampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
        long samples = ((long)(info[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(info[14..]);
        if (sampleRate > 0 && samples > 0)
        {
            data.Duration = TimeSpan.FromSeconds((double)samples / sampleRate);
        }
    }

    /// <summary>
    /// 「名前=値」の並び。同じ名前が複数あれば、アーティストは「; 」でつなぐ。
    /// 画像を文字列にして埋め込んだ巨大なコメントがあることがあるので、まとめて読まずに 1 つずつ読み、大きいものは飛ばす。
    /// </summary>
    private static void ReadComments(Stream stream, long end, TagData data)
    {
        if (end - stream.Position < 4)
        {
            return;
        }
        long vendorLength = stream.ReadUInt32LittleEndian();
        if (end - stream.Position < vendorLength + 4)
        {
            return;
        }
        stream.Skip(vendorLength);
        uint count = stream.ReadUInt32LittleEndian();

        List<string>? artists = null;
        List<string>? albumArtists = null;
        for (uint i = 0; i < count && end - stream.Position >= 4; i++)
        {
            long length = stream.ReadUInt32LittleEndian();
            long next = stream.Position + length;
            if (next > end)
            {
                break;
            }
            if (length > TagIo.MaxText)
            {
                stream.Position = next;
                continue;
            }

            ReadOnlySpan<byte> comment = stream.ReadBytes(length);
            int separator = comment.IndexOf((byte)'=');
            if (separator <= 0)
            {
                continue;
            }

            string name = TagText.Utf8(comment[..separator]);
            if (name.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferTitle(TagText.Utf8(comment[(separator + 1)..]));
            }
            else if (name.Equals("ARTIST", StringComparison.OrdinalIgnoreCase))
            {
                string artist = TagText.Utf8(comment[(separator + 1)..]).Trim();
                if (artist.Length > 0)
                {
                    (artists ??= []).Add(artist);
                }
            }
            else if (name.Equals("ALBUM", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferAlbum(TagText.Utf8(comment[(separator + 1)..]));
            }
            else if (name.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ALBUM ARTIST", StringComparison.OrdinalIgnoreCase))
            {
                string artist = TagText.Utf8(comment[(separator + 1)..]).Trim();
                if (artist.Length > 0)
                {
                    (albumArtists ??= []).Add(artist);
                }
            }
            else if (name.Equals("TRACKNUMBER", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferTrackNumber(TagText.Utf8(comment[(separator + 1)..]));
            }
            else if (name.StartsWith("REPLAYGAIN_", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferReplayGain(name, TagText.Utf8(comment[(separator + 1)..]));
            }
        }

        if (artists is not null)
        {
            data.OfferArtist(string.Join("; ", artists));
        }
        if (albumArtists is not null)
        {
            data.OfferAlbumArtist(string.Join("; ", albumArtists));
        }
    }

    private static void ReadPicture(ReadOnlySpan<byte> block, TagData data)
    {
        // 種別、MIME の長さと文字列、説明の長さと文字列、幅・高さ・色深度・色数、画像の長さと中身。数値はビッグエンディアン。
        if (block.Length < 8)
        {
            return;
        }
        int type = (int)BinaryPrimitives.ReadUInt32BigEndian(block);
        block = block[4..];

        for (int i = 0; i < 2; i++)
        {
            if (block.Length < 4)
            {
                return;
            }
            uint length = BinaryPrimitives.ReadUInt32BigEndian(block);
            if (length > block.Length - 4)
            {
                return;
            }
            block = block[(4 + (int)length)..];
        }

        if (block.Length < 20)
        {
            return;
        }
        uint imageLength = BinaryPrimitives.ReadUInt32BigEndian(block[16..]);
        block = block[20..];
        if (imageLength <= block.Length)
        {
            data.OfferPicture(type, block[..(int)imageLength].ToArray());
        }
    }

}
