using System.Buffers.Binary;
using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>整数 PCM の詰め替えと、float から整数 PCM への変換。</summary>
public static class PcmConversion
{
    /// <summary>
    /// その形式のサンプルを、値を変えずにデバイスの形式へ詰め替えられるか。
    /// 16 / 24 / 32bit の整数 PCM で、ビット数がデバイスの有効ビット数以下、サンプルレートとチャンネル数が同じ場合。
    /// </summary>
    public static bool CanWidenLosslessly(WaveFormat source, WaveFormat device)
    {
        WaveFormat standard = source.AsStandardWaveFormat();
        return standard.Encoding == WaveFormatEncoding.Pcm
            && standard.BitsPerSample is 16 or 24 or 32
            && IsIntegerPcm(device)
            && standard.BitsPerSample <= ValidBitsOf(device)
            && standard.SampleRate == device.SampleRate
            && standard.Channels == device.Channels;
    }

    /// <summary>デバイスの形式が整数 PCM か。</summary>
    public static bool IsIntegerPcm(WaveFormat format) =>
        format.AsStandardWaveFormat().Encoding == WaveFormatEncoding.Pcm;

    /// <summary>
    /// 有効ビット数。32bit の入れ物に有効 24bit（24-in-32）のように、入れ物より少ない場合がある。
    /// </summary>
    public static int ValidBitsOf(WaveFormat format) =>
        format is WaveFormatExtensible { ValidBitsPerSample: > 0 } extensible
            ? extensible.ValidBitsPerSample
            : format.BitsPerSample;

    /// <summary>
    /// 整数 PCM のサンプルを、より大きい入れ物へ上位ビット側に詰めて移す。下位は 0 で埋める。丸めも補間もしないので値は変わらない
    /// （24bit を 32bit の入れ物に入れる「24-in-32」など）。
    /// </summary>
    /// <param name="source">リトルエンディアンの整数 PCM。長さは <paramref name="sourceBytes"/> の倍数。</param>
    /// <param name="sourceBytes">変換元の 1 サンプルのバイト数。</param>
    /// <param name="destination">変換先。サンプル数 × <paramref name="destinationBytes"/> 以上の長さが必要。</param>
    /// <param name="destinationBytes">変換先の 1 サンプルのバイト数。<paramref name="sourceBytes"/> 以上。</param>
    /// <returns>書き込んだバイト数。</returns>
    public static int Widen(ReadOnlySpan<byte> source, int sourceBytes, Span<byte> destination, int destinationBytes)
    {
        if (destinationBytes < sourceBytes)
        {
            throw new ArgumentException("変換先の入れ物が変換元より小さくなっています。", nameof(destinationBytes));
        }

        int samples = source.Length / sourceBytes;
        if (destinationBytes == sourceBytes)
        {
            source[..(samples * sourceBytes)].CopyTo(destination);
            return samples * sourceBytes;
        }

        int padding = destinationBytes - sourceBytes;
        for (int i = 0; i < samples; i++)
        {
            Span<byte> target = destination.Slice(i * destinationBytes, destinationBytes);
            target[..padding].Clear();
            source.Slice(i * sourceBytes, sourceBytes).CopyTo(target[padding..]);
        }
        return samples * destinationBytes;
    }

    /// <summary>
    /// float（-1.0 から 1.0）を整数 PCM にする。範囲外は最大・最小に丸める。値を変える変換なので、ビットパーフェクト経路では使わない。
    /// </summary>
    /// <param name="containerBytes">1 サンプルの入れ物のバイト数（2 / 3 / 4）。</param>
    /// <param name="validBits">有効ビット数。入れ物より少なければ上位ビット側に詰める。</param>
    /// <returns>書き込んだバイト数。</returns>
    public static int FloatToPcm(ReadOnlySpan<float> source, Span<byte> destination, int containerBytes, int validBits)
    {
        double scale = Math.Pow(2, validBits - 1);
        double max = scale - 1;
        double min = -scale;
        int shift = containerBytes * 8 - validBits;

        for (int i = 0; i < source.Length; i++)
        {
            double scaled = Math.Clamp(Math.Round(source[i] * scale), min, max);
            int value = (int)scaled << shift;
            Span<byte> target = destination.Slice(i * containerBytes, containerBytes);
            switch (containerBytes)
            {
                case 2:
                    BinaryPrimitives.WriteInt16LittleEndian(target, (short)value);
                    break;
                case 3:
                    target[0] = (byte)value;
                    target[1] = (byte)(value >> 8);
                    target[2] = (byte)(value >> 16);
                    break;
                default:
                    BinaryPrimitives.WriteInt32LittleEndian(target, value);
                    break;
            }
        }
        return source.Length * containerBytes;
    }
}
