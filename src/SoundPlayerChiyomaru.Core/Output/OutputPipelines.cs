using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundPlayerChiyomaru.Core.Localization;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>出力が今どの形式・どの経路で鳴らしているか。画面に表示するための情報。</summary>
/// <param name="Exclusive">WASAPI 排他モードか（false は共有モード）。</param>
/// <param name="DeviceFormat">デバイスへ渡している形式。共有モードでは null（Windows 側がミックス形式に変換する）。</param>
/// <param name="BitPerfectCapable">
/// 音量が最大のとき、デコードしたサンプル値を変えずにデバイスへ渡せる構成か。
/// 実際にビットパーフェクトになるのは、これが true で、かつ音量が最大で ReplayGain が無効の間だけ。
/// </param>
/// <param name="ResampledFrom">リサンプリングしている場合の、元のサンプルレート。していなければ null。</param>
/// <param name="NormalPathReason">
/// 音量が最大でもビットパーフェクトにできない場合の、リサンプリング以外の理由（「モノラルをステレオに変換」など）。なければ null。
/// </param>
/// <param name="Notice">
/// 希望どおりの出力を作れず、代わりの出力で鳴らしている場合の説明（排他モードを使えず共有モードにした、など）。問題がなければ null。
/// </param>
public sealed record OutputInfo(
    bool Exclusive,
    WaveFormat? DeviceFormat,
    bool BitPerfectCapable,
    int? ResampledFrom,
    string? NormalPathReason,
    string? Notice);

/// <summary>再生元を float で読み、音量を掛ける。通常経路の入口。</summary>
internal sealed class VolumeAppliedSource(IPlaybackSource source) : ISampleProvider
{
    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(source.Format.SampleRate, source.Format.Channels);

    public int Read(Span<float> buffer)
    {
        int read = source.ReadFloat(buffer);
        float volume = source.Volume;
        if (volume < 1f)
        {
            Span<float> samples = buffer[..read];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= volume;
            }
        }
        return read;
    }
}

/// <summary>
/// 共有モード用。float のまま Windows に渡す（サンプルレート・ビット深度・チャンネル数の違いは Windows 側が吸収する）。
/// </summary>
public static class SharedModePipeline
{
    public static IWaveProvider Create(IPlaybackSource source)
    {
        // 出力の形式は最初の曲で決まる。それと同じサンプルレート・チャンネル数の曲だけを、出力を止めずに続けられる。
        source.CanContinueWith = null;
        return MultichannelFormat.Apply(new VolumeAppliedSource(source).ToWaveProvider());
    }
}

/// <summary>
/// 排他モード用。デバイスの形式に合わせたバイト列を作る。バッファごとに、次の 2 つの経路のどちらかを通る。
/// <list type="bullet">
/// <item>ビットパーフェクト経路: デコード結果の整数 PCM を、値を変えずにデバイスの入れ物へ詰め替えるだけ（モノラルの曲をステレオで出すときは、左右に同じ値を入れる）。float を経由しない。</item>
/// <item>通常経路: float にして音量を掛け、必要ならチャンネル数とサンプルレートを合わせてから、整数 PCM に戻す。</item>
/// </list>
/// 経路は音量と ReplayGain の有無で決まる。どちらも同じ再生元から読み、同じデバイスの形式で書くので、切り替えても出力は途切れない。
/// </summary>
public sealed class ExclusiveModePipeline : IWaveProvider
{
    private readonly IPlaybackSource source;
    private readonly ISampleProvider normalPath;
    private readonly bool bitPerfectCapable;
    private readonly int copiesPerSample;
    private readonly int deviceSampleBytes;
    private readonly int validBits;
    private readonly bool deviceIsFloat;
    private readonly long drainBytes;
    private byte[] pcmBuffer = [];
    private float[] floatBuffer = [];
    private long silentBytes;
    private bool endedRaised;
    private volatile bool paused;

    /// <param name="drain">
    /// 再生元が終端に達してから <see cref="Ended"/> を発生させるまでに流す無音の長さ。
    /// デバイスに渡し済みの音が鳴り終わるまでの時間（出力のバッファの長さ）以上にする。
    /// </param>
    public ExclusiveModePipeline(IPlaybackSource source, ExclusiveFormatChoice choice, TimeSpan drain)
    {
        this.source = source;
        WaveFormat = choice.DeviceFormat;
        drainBytes = (long)(drain.TotalSeconds * WaveFormat.SampleRate) * WaveFormat.BlockAlign;
        deviceSampleBytes = WaveFormat.BitsPerSample / 8;
        validBits = PcmConversion.ValidBitsOf(WaveFormat);
        deviceIsFloat = !PcmConversion.IsIntegerPcm(WaveFormat);

        ISampleProvider stage = new VolumeAppliedSource(source);
        if (choice.ChannelsChanged)
        {
            stage = new MonoToStereoSampleProvider(stage);
        }
        if (choice.SampleRateChanged)
        {
            stage = new WdlResamplingSampleProvider(stage, WaveFormat.SampleRate);
        }
        normalPath = stage;

        bitPerfectCapable = PcmConversion.CanWidenLosslessly(source.Format, WaveFormat);

        // 出力を止めずに続ける曲は、チャンネル数が同じものに限られる（GaplessChain）。左右に複製するかは、ここで一度決めればよい。
        copiesPerSample = PcmConversion.CopiesPerSample(source.Format, WaveFormat);
        NormalPathReason = DescribeNormalPathReason(source.Format.AsStandardWaveFormat(), choice);

        // 出力を止めずに続けられるのは、同じ経路でそのまま読み継げる曲だけ。ビットパーフェクトにできる構成なら、
        // 次の曲も値を変えずに詰め替えられること。そうでなければ、サンプルレートとチャンネル数が同じであること。
        WaveFormat first = source.Format;
        source.CanContinueWith = next => bitPerfectCapable
            ? PcmConversion.CanWidenLosslessly(next, WaveFormat)
            : next.SampleRate == first.SampleRate && next.Channels == first.Channels;
    }

    /// <summary>
    /// 再生元が終端に達し、最後の音が鳴り終わるだけの無音を流し終えた。出力スレッドから呼ばれる。
    /// この後も <see cref="Read"/> は無音を返し続けるので、受け取った側が出力を止める。
    /// </summary>
    public event Action? Ended;

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// 一時停止中か。その間は再生元を読まずに無音を返し続け、解除すると続きからそのまま読む。
    /// </summary>
    /// <remarks>
    /// 排他モードでは、一時停止の間も出力（NAudio）を止めずに、ここで無音を渡す。NAudio 3.1.0 の一時停止は、
    /// デバイスのストリームを動かしたままバッファを渡すのをやめるだけなので、排他モードではデバイスが空のバッファを
    /// 読み続ける状態（アンダーラン）になり、再開した後の音にノイズが乗ることがある。
    /// </remarks>
    public bool Paused
    {
        get => paused;
        set => paused = value;
    }

    /// <summary>音量が最大のとき、サンプル値を変えずに渡せる構成か。</summary>
    public bool BitPerfectCapable => bitPerfectCapable;

    /// <summary>
    /// 音量が最大でもビットパーフェクトにできない場合の、リサンプリング以外の理由。なければ null。
    /// </summary>
    public string? NormalPathReason { get; }

    private string? DescribeNormalPathReason(WaveFormat sourceFormat, ExclusiveFormatChoice choice)
    {
        if (choice.ChannelsChanged && !bitPerfectCapable)
        {
            return UiLanguage.Text("モノラルをステレオに変換", "mono converted to stereo");
        }
        if (choice.SampleRateChanged || bitPerfectCapable)
        {
            return null;
        }
        if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return deviceIsFloat ? null : UiLanguage.Text("float を整数に変換", "float converted to integer");
        }
        int bits = sourceFormat.BitsPerSample;
        return bits > validBits
            ? UiLanguage.Text($"{bits} bit を {validBits} bit に変換", $"{bits} bit converted to {validBits} bit")
            : UiLanguage.Text($"{bits} bit は通常経路で処理", $"{bits} bit handled on the normal path");
    }

    public int Read(Span<byte> buffer)
    {
        buffer = buffer[..(buffer.Length - buffer.Length % WaveFormat.BlockAlign)];
        if (paused)
        {
            // 終端の後に流す無音とは別なので、その長さには数えない。
            buffer.Clear();
            return buffer.Length;
        }

        int written = bitPerfectCapable && source.Volume >= 1f && !source.AppliesGain
            ? ReadBitPerfect(buffer)
            : ReadNormal(buffer);
        if (written > 0)
        {
            // 音声が読めた。終端に達した後でも、シークで戻された・次の曲が予約された場合はここへ戻ってくる。
            silentBytes = 0;
            endedRaised = false;
        }
        if (written == buffer.Length)
        {
            return written;
        }

        // 再生元が終端に達した。排他モードでは、ここで短いバッファや 0 を返すと出力がエラーになる
        // （バッファは毎回ちょうど満杯で渡す必要があり、NAudio 3.1.0 は終端の 0 で AUDCLNT_E_BUFFER_SIZE_ERROR になる）。
        // 残りを無音で埋めて満杯で返し続け、デバイスに渡し済みの音が鳴り終わるだけの無音を流してから、終わったことを知らせる。
        buffer[written..].Clear();
        silentBytes += buffer.Length - written;
        if (!endedRaised && silentBytes >= drainBytes)
        {
            endedRaised = true;
            Ended?.Invoke();
        }
        return buffer.Length;
    }

    private int ReadBitPerfect(Span<byte> buffer)
    {
        int written = 0;
        while (written < buffer.Length)
        {
            // 詰め替え元はデバイスの入れ物以下の大きさなので、デバイスの入れ物の大きさで確保しておけば足りる。
            // 左右に複製するときは、曲のサンプルはデバイスのサンプルの半分だけ読む。
            int samplesWanted = (buffer.Length - written) / deviceSampleBytes / copiesPerSample;
            if (pcmBuffer.Length < samplesWanted * deviceSampleBytes)
            {
                pcmBuffer = new byte[samplesWanted * deviceSampleBytes];
            }

            int read = source.ReadPcm(pcmBuffer, samplesWanted, out WaveFormat format);
            if (read == 0)
            {
                break;
            }

            // 曲によってビット数が違うことがある。返ってきた形式で詰め替える。
            written += PcmConversion.Widen(
                pcmBuffer.AsSpan(0, read),
                format.BitsPerSample / 8,
                buffer[written..],
                deviceSampleBytes,
                copiesPerSample);
        }
        return written;
    }

    private int ReadNormal(Span<byte> buffer)
    {
        int samplesWanted = buffer.Length / deviceSampleBytes;
        if (floatBuffer.Length < samplesWanted)
        {
            floatBuffer = new float[samplesWanted];
        }

        int read = normalPath.Read(floatBuffer.AsSpan(0, samplesWanted));
        if (deviceIsFloat)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(floatBuffer.AsSpan(0, read)).CopyTo(buffer);
            return read * sizeof(float);
        }
        return PcmConversion.FloatToPcm(floatBuffer.AsSpan(0, read), buffer, deviceSampleBytes, validBits);
    }
}
