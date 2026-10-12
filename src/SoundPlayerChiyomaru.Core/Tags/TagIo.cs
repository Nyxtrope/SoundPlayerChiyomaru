using System.Buffers.Binary;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>
/// タグを読むためのストリームの読み取り。壊れたファイルに書かれた大きさをそのまま信じないよう、
/// 読む前に必ずファイルに残っている長さと上限を確かめる。
/// </summary>
internal static class TagIo
{
    /// <summary>文字列の項目として読む大きさの上限。これより大きい項目は読み飛ばす。</summary>
    public const int MaxText = 1 << 20;

    /// <summary>画像として読む大きさの上限。</summary>
    public const int MaxPicture = 32 << 20;

    public static long Remaining(this Stream stream) => stream.Length - stream.Position;

    /// <exception cref="InvalidDataException">ファイルに残っている長さ、または上限より大きい。</exception>
    public static byte[] ReadBytes(this Stream stream, long count)
    {
        if (count < 0 || count > stream.Remaining() || count > MaxPicture)
        {
            throw new InvalidDataException();
        }

        byte[] bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <exception cref="InvalidDataException">ファイルに残っている長さより大きい。</exception>
    public static void Skip(this Stream stream, long count)
    {
        if (count < 0 || count > stream.Remaining())
        {
            throw new InvalidDataException();
        }
        stream.Seek(count, SeekOrigin.Current);
    }

    public static uint ReadUInt32BigEndian(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static uint ReadUInt32LittleEndian(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static ulong ReadUInt64BigEndian(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    public static ulong ReadUInt64LittleEndian(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public static ushort ReadUInt16LittleEndian(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }
}
