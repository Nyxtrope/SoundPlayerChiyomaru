using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>WMA（ASF）のタグ。ヘッダーの中の、内容の説明・拡張された内容の説明・メタデータの各オブジェクトを読む。</summary>
internal static class AsfTags
{
    private const int ObjectHeaderSize = 24;

    private static readonly byte[] Header = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray();
    private static readonly byte[] FileProperties = new Guid("8CABDCA1-A947-11CF-8EE4-00C00C205365").ToByteArray();
    private static readonly byte[] ContentDescription = new Guid("75B22633-668E-11CF-A6D9-00AA0062CE6C").ToByteArray();
    private static readonly byte[] ExtendedContentDescription = new Guid("D2D0A440-E307-11D2-97F0-00A0C95EA850").ToByteArray();
    private static readonly byte[] HeaderExtension = new Guid("5FBF03B5-A92E-11CF-8EE3-00C00C205365").ToByteArray();
    private static readonly byte[] Metadata = new Guid("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA").ToByteArray();
    private static readonly byte[] MetadataLibrary = new Guid("44231C94-9498-49D1-A141-1D134E457054").ToByteArray();

    public static bool Read(Stream stream, TagData data, bool wantPicture)
    {
        Span<byte> header = stackalloc byte[ObjectHeaderSize];
        if (stream.Remaining() < ObjectHeaderSize + 6)
        {
            return false;
        }
        stream.ReadExactly(header);
        if (!header[..16].SequenceEqual(Header))
        {
            return false;
        }

        // ヘッダーの直後は、オブジェクトの数（4 バイト）と予備（2 バイト）。
        long end = Math.Min(stream.Length, (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(header[16..]), long.MaxValue));
        stream.Skip(6);
        ReadObjects(stream, end, data, wantPicture, inExtension: false);
        return true;
    }

    private static void ReadObjects(Stream stream, long end, TagData data, bool wantPicture, bool inExtension)
    {
        Span<byte> header = stackalloc byte[ObjectHeaderSize];
        while (end - stream.Position >= ObjectHeaderSize)
        {
            long start = stream.Position;
            stream.ReadExactly(header);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            if (size < ObjectHeaderSize || size > (ulong)(end - start))
            {
                break;
            }
            long next = start + (long)size;

            ReadOnlySpan<byte> id = header[..16];
            if (id.SequenceEqual(FileProperties))
            {
                ReadFileProperties(stream, next, data);
            }
            else if (id.SequenceEqual(ContentDescription))
            {
                ReadContentDescription(stream, next, data);
            }
            else if (id.SequenceEqual(ExtendedContentDescription))
            {
                ReadDescriptors(stream, next, data, wantPicture, library: false);
            }
            else if (id.SequenceEqual(Metadata) || id.SequenceEqual(MetadataLibrary))
            {
                ReadDescriptors(stream, next, data, wantPicture, library: true);
            }
            else if (id.SequenceEqual(HeaderExtension) && !inExtension && next - stream.Position >= 22)
            {
                // 予備（GUID と 2 バイト）、中身の長さ（4 バイト）の後ろに、オブジェクトが入れ子で並ぶ。
                stream.Skip(22);
                ReadObjects(stream, next, data, wantPicture, inExtension: true);
            }
            stream.Position = next;
        }
    }

    private static void ReadFileProperties(Stream stream, long end, TagData data)
    {
        // ファイルの ID（16）、大きさ（8）、作成日時（8）、パケット数（8）、再生時間（8、100 ナノ秒単位）、
        // 送出時間（8）、プリロール（8、ミリ秒）。再生時間にはプリロールが含まれている。
        if (end - stream.Position < 72)
        {
            return;
        }
        stream.Skip(40);
        ulong playDuration = stream.ReadUInt64LittleEndian();
        stream.Skip(8);
        ulong preroll = stream.ReadUInt64LittleEndian();

        double seconds = (playDuration / 10_000_000.0) - (preroll / 1000.0);
        if (seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
        {
            data.Duration = TimeSpan.FromSeconds(seconds);
        }
    }

    private static void ReadContentDescription(Stream stream, long end, TagData data)
    {
        // 題名・作者・著作権・説明・評価の長さ（各 2 バイト）の後ろに、UTF-16 の文字列がその順に並ぶ。
        if (end - stream.Position < 10)
        {
            return;
        }
        Span<byte> lengths = stackalloc byte[10];
        stream.ReadExactly(lengths);
        int titleLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths);
        int authorLength = BinaryPrimitives.ReadUInt16LittleEndian(lengths[2..]);
        if (end - stream.Position < titleLength + authorLength)
        {
            return;
        }

        data.OfferTitle(TagText.Utf16LittleEndian(stream.ReadBytes(titleLength)).TrimEnd('\0'));
        data.OfferArtist(TagText.Utf16LittleEndian(stream.ReadBytes(authorLength)).TrimEnd('\0'));
    }

    /// <summary>名前と値の組の並び。</summary>
    /// <param name="library">
    /// メタデータのオブジェクトの形か。こちらは値の長さが 4 バイトで、大きな値（画像）を入れられる。
    /// </param>
    private static void ReadDescriptors(Stream stream, long end, TagData data, bool wantPicture, bool library)
    {
        if (end - stream.Position < 2)
        {
            return;
        }

        int count = stream.ReadUInt16LittleEndian();
        for (int i = 0; i < count; i++)
        {
            int nameLength;
            int type;
            long valueLength;
            if (library)
            {
                // 言語（2）、ストリーム（2）、名前の長さ（2）、値の種類（2）、値の長さ（4）、名前、値。
                if (end - stream.Position < 12)
                {
                    return;
                }
                stream.Skip(4);
                nameLength = stream.ReadUInt16LittleEndian();
                type = stream.ReadUInt16LittleEndian();
                valueLength = stream.ReadUInt32LittleEndian();
                if (end - stream.Position < nameLength + valueLength)
                {
                    return;
                }
                ReadDescriptor(stream, TagText.Utf16LittleEndian(stream.ReadBytes(nameLength)), type, valueLength, data, wantPicture);
            }
            else
            {
                // 名前の長さ（2）、名前、値の種類（2）、値の長さ（2）、値。
                if (end - stream.Position < 2)
                {
                    return;
                }
                nameLength = stream.ReadUInt16LittleEndian();
                if (end - stream.Position < nameLength + 4)
                {
                    return;
                }
                string name = TagText.Utf16LittleEndian(stream.ReadBytes(nameLength));
                type = stream.ReadUInt16LittleEndian();
                valueLength = stream.ReadUInt16LittleEndian();
                if (end - stream.Position < valueLength)
                {
                    return;
                }
                ReadDescriptor(stream, name, type, valueLength, data, wantPicture);
            }
        }
    }

    private static void ReadDescriptor(Stream stream, string name, int type, long length, TagData data, bool wantPicture)
    {
        long next = stream.Position + length;
        name = name.TrimEnd('\0');

        if (name.Equals("WM/Picture", StringComparison.OrdinalIgnoreCase))
        {
            if (wantPicture && type == 1 && length <= TagIo.MaxPicture)
            {
                ReadPicture(stream.ReadBytes(length), data);
            }
        }
        else if (length <= TagIo.MaxText && IsWanted(name))
        {
            byte[] bytes = stream.ReadBytes(length);
            string? value = type switch
            {
                0 => TagText.Utf16LittleEndian(bytes).TrimEnd('\0'),
                3 when bytes.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };

            if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferTitle(value);
            }
            else if (name.Equals("Author", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferArtist(value);
            }
            else if (name.Equals("WM/AlbumTitle", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferAlbum(value);
            }
            else if (name.Equals("WM/AlbumArtist", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferAlbumArtist(value);
            }
            else if (name.Equals("WM/TrackNumber", StringComparison.OrdinalIgnoreCase))
            {
                data.OfferTrackNumber(value);
            }
            else
            {
                data.OfferReplayGain(ReplayGainName(name), value);
            }
        }
        stream.Position = next;
    }

    private static bool IsWanted(string name) =>
        name.Equals("Title", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Author", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WM/AlbumTitle", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WM/TrackNumber", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WM/AlbumArtist", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("replaygain", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ReplayGain の名前を、他の形式と同じ書き方に直す。WMA では「replaygain_track_gain」の形のほかに、
    /// 「ReplayGain/Track」の形で書くソフトがある。
    /// </summary>
    private static string ReplayGainName(string name)
    {
        if (name.Equals("ReplayGain/Track", StringComparison.OrdinalIgnoreCase))
        {
            return "replaygain_track_gain";
        }
        if (name.Equals("ReplayGain/Track Peak", StringComparison.OrdinalIgnoreCase))
        {
            return "replaygain_track_peak";
        }
        if (name.Equals("ReplayGain/Album", StringComparison.OrdinalIgnoreCase))
        {
            return "replaygain_album_gain";
        }
        if (name.Equals("ReplayGain/Album Peak", StringComparison.OrdinalIgnoreCase))
        {
            return "replaygain_album_peak";
        }
        return name;
    }

    private static void ReadPicture(ReadOnlySpan<byte> value, TagData data)
    {
        // 種別（1）、画像の長さ（4）、MIME（UTF-16、NUL 終端）、説明（UTF-16、NUL 終端）、画像。
        if (value.Length < 5)
        {
            return;
        }
        int type = value[0];
        uint imageLength = BinaryPrimitives.ReadUInt32LittleEndian(value[1..]);
        value = value[5..];

        for (int i = 0; i < 2; i++)
        {
            int end = -1;
            for (int j = 0; j + 1 < value.Length; j += 2)
            {
                if (value[j] == 0 && value[j + 1] == 0)
                {
                    end = j + 2;
                    break;
                }
            }
            if (end < 0)
            {
                return;
            }
            value = value[end..];
        }

        if (imageLength <= value.Length)
        {
            data.OfferPicture(type, value[..(int)imageLength].ToArray());
        }
    }
}
