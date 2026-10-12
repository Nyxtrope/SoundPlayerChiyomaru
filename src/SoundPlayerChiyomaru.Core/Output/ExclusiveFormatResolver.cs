using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>デバイスが排他モードで形式を受け付けるかを問い合わせる。</summary>
public interface IExclusiveFormatProbe
{
    bool IsSupported(WaveFormat format);
}

/// <summary>排他モードで使う形式。</summary>
/// <param name="DeviceFormat">デバイスへ渡す形式。</param>
/// <param name="SampleRateChanged">曲と違うサンプルレートになった（リサンプリングが要る）。</param>
/// <param name="ChannelsChanged">曲と違うチャンネル数になった（モノラルをステレオに広げる）。</param>
public sealed record ExclusiveFormatChoice(WaveFormat DeviceFormat, bool SampleRateChanged, bool ChannelsChanged);

/// <summary>
/// 曲の形式に対して、排他モードでデバイスへ渡す形式を決める（SPEC 3.2「排他モードのフォーマット解決」）。
/// </summary>
public static class ExclusiveFormatResolver
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>リサンプリングが要るときに試すサンプルレート。</summary>
    private static readonly int[] StandardRates = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000];

    /// <summary>
    /// 形式を決める。優先するのは、(1) 曲と同じサンプルレートで値を変えずに収まる形式、(2) 同じサンプルレートでビット数が減る形式、
    /// (3) 別のサンプルレート（整数倍、次に高いほう、最後に低いほう）、の順。モノラルの曲は、サンプルレートごとに、
    /// モノラルが受け付けられなければステレオで探す。
    /// </summary>
    /// <param name="mixFormat">デバイスの共有モードの形式。そのサンプルレートを最後の候補として使う。</param>
    /// <param name="maxBitDepth">
    /// 有効ビット数の上限。null は上限なし。指定すると、それより大きい整数の形式と float の形式は試さない
    /// （デバイスによって音が出ないときの切り分けに使う。曲のビット数より小さければ、値が変わる）。
    /// </param>
    /// <returns>使える形式。デバイスがどの候補も受け付けなければ null（排他モードでは再生できない）。</returns>
    public static ExclusiveFormatChoice? Resolve(
        WaveFormat source, IExclusiveFormatProbe probe, WaveFormat? mixFormat, int? maxBitDepth = null)
    {
        WaveFormat standard = source.AsStandardWaveFormat();
        int[] channelOptions = standard.Channels == 1 ? [1, 2] : [standard.Channels];

        // サンプルレートを外側で回す。リサンプリングは音を変えるので、モノラルのままリサンプリングするより、
        // 同じサンプルレートのままステレオに広げるほうを先に選ぶ。
        foreach (int rate in CandidateRates(standard.SampleRate, mixFormat?.SampleRate))
        {
            foreach (int channels in channelOptions)
            {
                foreach (WaveFormat candidate in CandidateFormats(standard, rate, channels, maxBitDepth))
                {
                    if (probe.IsSupported(candidate))
                    {
                        return new ExclusiveFormatChoice(
                            candidate,
                            SampleRateChanged: rate != standard.SampleRate,
                            ChannelsChanged: channels != standard.Channels);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>デバイスへ渡す形式の内訳（「24-in-32」など）。画面に出すための表現。</summary>
    public static string Describe(WaveFormat format)
    {
        int valid = PcmConversion.ValidBitsOf(format);
        string depth = !PcmConversion.IsIntegerPcm(format)
            ? $"{format.BitsPerSample} bit float"
            : valid == format.BitsPerSample ? $"{format.BitsPerSample} bit" : $"{valid}-in-{format.BitsPerSample}";
        return $"{format.SampleRate} Hz / {depth}";
    }

    internal static IEnumerable<int> CandidateRates(int sourceRate, int? mixRate)
    {
        var rates = new List<int> { sourceRate, sourceRate * 2, sourceRate * 4 };
        rates.AddRange(StandardRates.Where(rate => rate > sourceRate));
        rates.AddRange(StandardRates.Where(rate => rate < sourceRate).OrderDescending());
        if (mixRate is { } mix)
        {
            rates.Add(mix);
        }
        return rates.Distinct();
    }

    /// <summary>
    /// そのサンプルレートとチャンネル数で試す形式。値を変えずに収まるもの（曲と同じビット数、次により大きい入れ物）が先で、
    /// ビット数が減るものは後。同じ内容なら、配置の情報がない通常の形式を先に試し、拒否されたら配置つきの形式を試す。
    /// </summary>
    internal static IEnumerable<WaveFormat> CandidateFormats(WaveFormat source, int rate, int channels, int? maxBitDepth = null)
    {
        int mask = MultichannelFormat.DefaultChannelMask(channels);
        bool isFloat = source.Encoding == WaveFormatEncoding.IeeeFloat;
        if (isFloat && maxBitDepth is null)
        {
            yield return WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            yield return new WaveFormatExtensible(rate, 32, channels, FloatSubFormat, 32, mask);
        }

        // (入れ物のビット数, 有効ビット数)。float の曲と 8bit の曲は、どの整数の形式でも値が変わるので、大きい順に試す。
        int sourceBits = isFloat || source.BitsPerSample < 16 ? 32 : source.BitsPerSample;
        (int Container, int Valid)[] depths = [(16, 16), (24, 24), (32, 24), (32, 32)];
        if (maxBitDepth is { } max)
        {
            depths = [.. depths.Where(depth => depth.Valid <= max)];
        }
        IEnumerable<(int Container, int Valid)> ordered = depths
            .Where(depth => depth.Valid >= sourceBits)
            .Concat(depths.Where(depth => depth.Valid < sourceBits).Reverse());

        foreach ((int container, int valid) in ordered)
        {
            if (container == valid)
            {
                yield return new WaveFormat(rate, container, channels);
            }
            yield return new WaveFormatExtensible(rate, container, channels, PcmSubFormat, valid, mask);
        }
    }
}
