using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>
/// 3 チャンネル以上の音声に、スピーカーの配置を持つ形式（<c>WAVE_FORMAT_EXTENSIBLE</c>）を名乗らせる。サンプルには手を加えない。
/// </summary>
/// <remarks>
/// WASAPI は、3 チャンネル以上を配置の情報がない通常の形式で渡すと、共有モードでも受け付けない（E_INVALIDARG）。
/// 配置を付けて渡せば、デバイスのスピーカー構成に合わせて Windows がまとめる（ステレオのデバイスなら 5.1ch をステレオに）。
/// </remarks>
public sealed class MultichannelFormat : IWaveProvider
{
    private const int FrontLeft = 0x1;
    private const int FrontRight = 0x2;
    private const int FrontCenter = 0x4;
    private const int LowFrequency = 0x8;
    private const int BackLeft = 0x10;
    private const int BackRight = 0x20;
    private const int BackCenter = 0x100;
    private const int SideLeft = 0x200;
    private const int SideRight = 0x400;

    private readonly IWaveProvider source;

    private MultichannelFormat(IWaveProvider source, WaveFormat format)
    {
        this.source = source;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// 必要なら配置つきの形式を名乗らせる。2 チャンネル以下、または既に配置つきの形式なら、そのまま返す。
    /// </summary>
    public static IWaveProvider Apply(IWaveProvider source)
    {
        WaveFormat format = source.WaveFormat;
        if (format.Channels <= 2 || format is WaveFormatExtensible)
        {
            return source;
        }

        var extensible = new WaveFormatExtensible(
            format.SampleRate,
            format.BitsPerSample,
            format.Channels,
            useIeeeFloat: format.Encoding == WaveFormatEncoding.IeeeFloat,
            validBitsPerSample: format.BitsPerSample,
            channelMask: DefaultChannelMask(format.Channels));
        return new MultichannelFormat(source, extensible);
    }

    /// <summary>
    /// チャンネル数から標準の配置を決める。デコード結果には配置の情報が残っていないので、WAV の標準の並びとみなす。
    /// </summary>
    internal static int DefaultChannelMask(int channels) => channels switch
    {
        1 => FrontCenter,
        2 => FrontLeft | FrontRight,
        3 => FrontLeft | FrontRight | FrontCenter,
        4 => FrontLeft | FrontRight | BackLeft | BackRight,
        5 => FrontLeft | FrontRight | FrontCenter | BackLeft | BackRight,
        6 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight,
        7 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackCenter | SideLeft | SideRight,
        8 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight | SideLeft | SideRight,

        // 9 チャンネル以上に標準の並びはない。配置の定義がある 18 個までを下から順に割り当てる。
        _ => (int)((1L << Math.Min(channels, 18)) - 1),
    };

    public int Read(Span<byte> buffer) => source.Read(buffer);
}
