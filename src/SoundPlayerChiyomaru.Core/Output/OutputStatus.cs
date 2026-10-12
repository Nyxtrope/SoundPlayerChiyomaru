namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>画面に出す、ビットパーフェクト再生の印。</summary>
public enum BitPerfectBadge
{
    /// <summary>何も出さない（再生していない、排他モードを選んでいない、利用者が音量や ReplayGain で通常経路にしている）。</summary>
    None,

    /// <summary>ビットパーフェクトで再生している。</summary>
    BitPerfect,

    /// <summary>排他モードを選んでいて、音量も ReplayGain も妨げていないのに、ビットパーフェクトにできていない。</summary>
    Unavailable,
}

/// <summary>今の出力の状態を、画面に出す言葉にする（SPEC 3.2）。</summary>
public static class OutputStatus
{
    /// <summary>ビットパーフェクト再生の印と、その説明（印を出さない場合は null）。</summary>
    /// <param name="exclusiveWanted">利用者が排他モードを選んでいるか。</param>
    /// <param name="output">今の出力。出力していなければ null。</param>
    /// <param name="isBitPerfect">今、サンプル値を変えずにデバイスへ渡しているか。</param>
    /// <param name="volumeIsMax">音量が最大か。</param>
    /// <param name="replayGain">ReplayGain が有効か。</param>
    public static (BitPerfectBadge Badge, string? Description) Badge(
        bool exclusiveWanted,
        OutputInfo? output,
        bool isBitPerfect,
        bool volumeIsMax,
        bool replayGain)
    {
        if (output is null)
        {
            return (BitPerfectBadge.None, null);
        }
        if (isBitPerfect && output.DeviceFormat is not null)
        {
            return (
                BitPerfectBadge.BitPerfect,
                $"ビットパーフェクトで出力しています。\n（{ExclusiveFormatResolver.Describe(output.DeviceFormat)}）");
        }
        if (!exclusiveWanted)
        {
            return (BitPerfectBadge.None, null);
        }
        if (!output.Exclusive || output.DeviceFormat is null)
        {
            return (BitPerfectBadge.Unavailable, output.Notice ?? "排他モードで出力できていません。共有モードで再生しています。");
        }
        if (!volumeIsMax || replayGain)
        {
            // 利用者が自分で通常経路にしている。知らせることはない。
            return (BitPerfectBadge.None, null);
        }

        string reason = output.ResampledFrom is { } from
            ? $"デバイスがこの曲のサンプルレートに対応していないので、リサンプリングしています（{from} → {output.DeviceFormat.SampleRate} Hz）。"
            : output.NormalPathReason is { } why
                ? $"ビットパーフェクトにできません（{why}）。"
                : "ビットパーフェクトにできません。";
        return (BitPerfectBadge.Unavailable, reason);
    }

    /// <summary>
    /// モードと処理経路、出力の形式、デバイスの説明。「出力」メニューに 1 項目ずつ出す
    /// （1 行にまとめるとメニューの幅に収まらない）。
    /// </summary>
    public static IReadOnlyList<string> Describe(OutputInfo output, string? deviceName, bool isBitPerfect, bool replayGain)
    {
        var lines = new List<string>(3);
        if (!output.Exclusive || output.DeviceFormat is null)
        {
            lines.Add("WASAPI 共有（通常経路）");
        }
        else
        {
            string route = isBitPerfect
                ? "ビットパーフェクト"
                : output.ResampledFrom is { } from
                    ? $"通常経路（リサンプリング {from} → {output.DeviceFormat.SampleRate} Hz）"
                    : output.BitPerfectCapable
                        ? replayGain ? "通常経路（ReplayGain）" : "通常経路（音量調整）"
                        : output.NormalPathReason is { } reason ? $"通常経路（{reason}）" : "通常経路";
            lines.Add($"WASAPI 排他  ·  {route}");
            lines.Add($"出力 {ExclusiveFormatResolver.Describe(output.DeviceFormat)}");
        }

        if (!string.IsNullOrEmpty(deviceName))
        {
            lines.Add(deviceName);
        }
        return lines;
    }
}
