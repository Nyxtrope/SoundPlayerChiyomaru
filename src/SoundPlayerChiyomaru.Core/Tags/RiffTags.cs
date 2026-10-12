using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>WAV のタグ。ID3v2 を入れたチャンクと LIST/INFO チャンクを、この優先順で読む。長さは形式とデータの大きさから求める。</summary>
internal static class RiffTags
{
    /// <summary>読むチャンクの数の上限。壊れたファイルで、中身のないチャンクを延々と読み続けないため。</summary>
    private const int MaxChunks = 4096;

    public static bool Read(Stream stream, TagData data, TagText text, bool wantPicture)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.Remaining() < header.Length)
        {
            return false;
        }
        stream.ReadExactly(header);
        if (!(header.StartsWith("RIFF"u8) || header.StartsWith("RF64"u8)) || !header[8..].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        // チャンクはファイルに現れた順に読むが、優先順は ID3v2、INFO の順。別々に読んでおき、最後に合わせる。
        var id3 = new TagData();
        var info = new TagData();
        uint bytesPerSecond = 0;
        long dataLength = -1;

        Span<byte> chunk = stackalloc byte[8];
        for (int i = 0; i < MaxChunks && stream.Remaining() >= 8; i++)
        {
            stream.ReadExactly(chunk);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long start = stream.Position;

            long available = Math.Min(size, stream.Length - start);
            ReadOnlySpan<byte> id = chunk[..4];
            if (id.SequenceEqual("fmt "u8) && available >= 16)
            {
                stream.Skip(8);
                bytesPerSecond = stream.ReadUInt32LittleEndian();
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (size is 0 or uint.MaxValue)
                {
                    // 録音しながら書かれたファイルなどでは、大きさが 0 や最大値のままになっている。
                    // 残りはすべて音声とみなし、この後ろにチャンクは探さない。
                    dataLength = stream.Length - start;
                    break;
                }
                dataLength = available;
            }
            else if (id.SequenceEqual("LIST"u8) && available >= 4)
            {
                ReadList(stream, start + available, info, text);
            }
            else if (id.SequenceEqual("id3 "u8) || id.SequenceEqual("ID3 "u8))
            {
                Id3v2.Read(stream, id3, text, wantPicture);
            }

            // チャンクは偶数バイトの境界に揃えられる。
            long next = start + size + (size & 1);
            if (next > stream.Length)
            {
                break;
            }
            stream.Position = next;
        }

        if (bytesPerSecond > 0 && dataLength > 0)
        {
            data.Duration = TimeSpan.FromSeconds((double)dataLength / bytesPerSecond);
        }
        data.FillFrom(id3);
        data.FillFrom(info);
        return true;
    }

    private static void ReadList(Stream stream, long end, TagData data, TagText text)
    {
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header[..4]);
        if (!header[..4].SequenceEqual("INFO"u8))
        {
            return;
        }

        for (int i = 0; i < MaxChunks && end - stream.Position >= 8; i++)
        {
            stream.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long next = stream.Position + size + (size & 1);
            if (stream.Position + size > end)
            {
                break;
            }

            ReadOnlySpan<byte> id = header[..4];
            bool title = id.SequenceEqual("INAM"u8);
            bool artist = id.SequenceEqual("IART"u8);
            bool album = id.SequenceEqual("IPRD"u8);
            bool track = id.SequenceEqual("ITRK"u8) || id.SequenceEqual("IPRT"u8) || id.SequenceEqual("TRCK"u8);
            if ((title || artist || album || track) && size <= TagIo.MaxText)
            {
                // 文字コードの決まりがない。UTF-8 か、地域の文字コードで書かれている。
                string value = text.Legacy(TagText.UntilNul(stream.ReadBytes(size)));
                if (title)
                {
                    data.OfferTitle(value);
                }
                else if (artist)
                {
                    data.OfferArtist(value);
                }
                else if (album)
                {
                    data.OfferAlbum(value);
                }
                else
                {
                    data.OfferTrackNumber(value);
                }
            }
            stream.Position = Math.Min(next, end);
        }
    }
}
