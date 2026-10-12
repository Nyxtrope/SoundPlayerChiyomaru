using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>MP4（M4A）のタグ。<c>moov/udta/meta/ilst</c> の中の項目と、<c>moov/mvhd</c> の長さを読む。</summary>
internal static class Mp4Tags
{
    /// <summary>1 つの階層で読む箱の数の上限。壊れたファイルで、中身のない箱を延々と読み続けないため。</summary>
    private const int MaxAtoms = 4096;

    private readonly record struct Atom(uint Type, long ContentStart, long End);

    public static bool Read(Stream stream, TagData data, bool wantPicture)
    {
        // 先頭の箱の種類で MP4 かどうかを見分ける。
        if (ReadAtom(stream, stream.Length) is not { } first
            || first.Type is not (Type.Ftyp or Type.Moov or Type.Mdat or Type.Free or Type.Skip or Type.Wide or Type.Styp))
        {
            return false;
        }

        stream.Position = 0;
        for (int i = 0; i < MaxAtoms && ReadAtom(stream, stream.Length) is { } atom; i++)
        {
            if (atom.Type == Type.Moov)
            {
                ReadMovie(stream, atom, data, wantPicture);
                break;
            }
            stream.Position = atom.End;
        }
        return true;
    }

    private static void ReadMovie(Stream stream, Atom movie, TagData data, bool wantPicture)
    {
        for (int i = 0; i < MaxAtoms && ReadAtom(stream, movie.End) is { } atom; i++)
        {
            switch (atom.Type)
            {
                case Type.Mvhd:
                    ReadMovieHeader(stream, atom, data);
                    break;
                case Type.Udta:
                    for (int j = 0; j < MaxAtoms && ReadAtom(stream, atom.End) is { } child; j++)
                    {
                        if (child.Type == Type.Meta)
                        {
                            ReadMeta(stream, child, data, wantPicture);
                        }
                        stream.Position = child.End;
                    }
                    break;
                case Type.Meta:
                    ReadMeta(stream, atom, data, wantPicture);
                    break;
            }
            stream.Position = atom.End;
        }
    }

    private static void ReadMovieHeader(Stream stream, Atom atom, TagData data)
    {
        if (atom.End - atom.ContentStart < 20)
        {
            return;
        }

        // 版 0 は時刻と長さが 32 ビット、版 1 は 64 ビット。
        int version = stream.ReadByte();
        stream.Skip(3);
        uint timescale;
        ulong duration;
        if (version == 1)
        {
            if (atom.End - stream.Position < 28)
            {
                return;
            }
            stream.Skip(16);
            timescale = stream.ReadUInt32BigEndian();
            duration = stream.ReadUInt64BigEndian();
            if (duration == ulong.MaxValue)
            {
                return;
            }
        }
        else
        {
            stream.Skip(8);
            timescale = stream.ReadUInt32BigEndian();
            duration = stream.ReadUInt32BigEndian();
            if (duration == uint.MaxValue)
            {
                return;
            }
        }

        double seconds = timescale == 0 ? 0 : (double)duration / timescale;
        if (seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
        {
            data.Duration = TimeSpan.FromSeconds(seconds);
        }
    }

    private static void ReadMeta(Stream stream, Atom meta, TagData data, bool wantPicture)
    {
        // 規格では先頭に 4 バイトの版とフラグがあるが、それを書かないソフトがある。直後が「hdlr」の箱なら、ないものとして読む。
        if (meta.End - meta.ContentStart >= 8)
        {
            Span<byte> probe = stackalloc byte[8];
            stream.ReadExactly(probe);
            stream.Position = meta.ContentStart
                + (BinaryPrimitives.ReadUInt32BigEndian(probe[4..]) == Type.Hdlr ? 0 : 4);
        }

        for (int i = 0; i < MaxAtoms && ReadAtom(stream, meta.End) is { } atom; i++)
        {
            if (atom.Type == Type.Ilst)
            {
                for (int j = 0; j < MaxAtoms && ReadAtom(stream, atom.End) is { } item; j++)
                {
                    ReadItem(stream, item, data, wantPicture);
                    stream.Position = item.End;
                }
            }
            stream.Position = atom.End;
        }
    }

    private static void ReadItem(Stream stream, Atom item, TagData data, bool wantPicture)
    {
        bool wanted = item.Type switch
        {
            Type.Title or Type.Artist or Type.Album or Type.AlbumArtist or Type.TrackNumber or Type.Freeform => true,
            Type.Cover => wantPicture,
            _ => false,
        };
        if (!wanted)
        {
            return;
        }

        string? name = null;
        List<string>? values = null;
        for (int i = 0; i < MaxAtoms && ReadAtom(stream, item.End) is { } atom; i++)
        {
            long length = atom.End - atom.ContentStart;
            if (atom.Type == Type.Name && length is >= 4 and <= 256 + 4)
            {
                stream.Skip(4);
                name = TagText.Utf8(stream.ReadBytes(length - 4));
            }
            else if (atom.Type == Type.Data && length >= 8)
            {
                // 先頭 8 バイトは、値の種類（下位 24 ビット）と地域の指定。
                int kind = (int)(stream.ReadUInt32BigEndian() & 0x00FF_FFFF);
                stream.Skip(4);
                length -= 8;

                if (item.Type == Type.Cover)
                {
                    if (length <= TagIo.MaxPicture)
                    {
                        data.OfferPicture(TagData.FrontCover, stream.ReadBytes(length));
                    }
                    return;
                }
                if (item.Type == Type.TrackNumber)
                {
                    // 2 バイトの予備、2 バイトのトラック番号、2 バイトの曲数。
                    if (length >= 4)
                    {
                        stream.Skip(2);
                        Span<byte> number = stackalloc byte[2];
                        stream.ReadExactly(number);
                        data.OfferTrackNumber(BinaryPrimitives.ReadUInt16BigEndian(number));
                    }
                    return;
                }
                if (length <= TagIo.MaxText)
                {
                    byte[] bytes = stream.ReadBytes(length);
                    string value = (kind == 2 ? TagText.Utf16BigEndian(bytes) : TagText.Utf8(bytes)).Trim();
                    if (value.Length > 0)
                    {
                        (values ??= []).Add(value);
                    }
                }
            }
            stream.Position = atom.End;
        }

        if (values is null)
        {
            return;
        }
        switch (item.Type)
        {
            case Type.Title:
                data.OfferTitle(values[0]);
                break;
            case Type.Artist:
                data.OfferArtist(string.Join("; ", values));
                break;
            case Type.Album:
                data.OfferAlbum(values[0]);
                break;
            case Type.AlbumArtist:
                data.OfferAlbumArtist(string.Join("; ", values));
                break;
            case Type.Freeform when name is not null:
                data.OfferReplayGain(name, values[0]);
                break;
        }
    }

    /// <summary>今の位置から箱を 1 つ読む。位置は箱の中身の先頭に進む。</summary>
    /// <returns><paramref name="limit"/> までに箱がもうない、または大きさが壊れている場合は null。</returns>
    private static Atom? ReadAtom(Stream stream, long limit)
    {
        long start = stream.Position;
        if (limit > stream.Length || limit - start < 8)
        {
            return null;
        }

        long size = stream.ReadUInt32BigEndian();
        uint type = stream.ReadUInt32BigEndian();
        long headerSize = 8;
        if (size == 1)
        {
            // 大きさを 64 ビットで持つ箱。
            if (limit - start < 16)
            {
                return null;
            }
            ulong large = stream.ReadUInt64BigEndian();
            if (large > long.MaxValue)
            {
                return null;
            }
            size = (long)large;
            headerSize = 16;
        }
        else if (size == 0)
        {
            // 親の終わりまで続く箱。
            size = limit - start;
        }

        if (size < headerSize || size > limit - start)
        {
            return null;
        }
        return new Atom(type, start + headerSize, start + size);
    }

    private static class Type
    {
        public const uint Ftyp = 0x66747970;
        public const uint Styp = 0x73747970;
        public const uint Moov = 0x6D6F6F76;
        public const uint Mdat = 0x6D646174;
        public const uint Free = 0x66726565;
        public const uint Skip = 0x736B6970;
        public const uint Wide = 0x77696465;
        public const uint Mvhd = 0x6D766864;
        public const uint Udta = 0x75647461;
        public const uint Meta = 0x6D657461;
        public const uint Hdlr = 0x68646C72;
        public const uint Ilst = 0x696C7374;
        public const uint Data = 0x64617461;
        public const uint Name = 0x6E616D65;

        /// <summary>「©nam」。</summary>
        public const uint Title = 0xA96E616D;

        /// <summary>「©ART」。</summary>
        public const uint Artist = 0xA9415254;

        /// <summary>「©alb」。</summary>
        public const uint Album = 0xA9616C62;

        /// <summary>「aART」。</summary>
        public const uint AlbumArtist = 0x61415254;

        /// <summary>「trkn」。</summary>
        public const uint TrackNumber = 0x74726B6E;

        /// <summary>「covr」。</summary>
        public const uint Cover = 0x636F7672;

        /// <summary>「----」。名前を自由に付けられる項目で、ReplayGain はここに書かれる。</summary>
        public const uint Freeform = 0x2D2D2D2D;
    }
}
