using System.Diagnostics;
using System.Globalization;

namespace SoundPlayerChiyomaru;

/// <summary>
/// 非機能要件（起動時間・メモリ・CPU）の実測。時刻はすべてプロセス生成からの経過ミリ秒で記録する。
/// </summary>
sealed class StartupBench
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly string outputPath;
    private double shownMs = -1;
    private double playCalledMs = -1;
    private TimeSpan cpuAtPlay;
    private DateTime playUtc;

    public StartupBench(string outputPath)
    {
        this.outputPath = outputPath;
    }

    public void MarkShown() => shownMs = ElapsedMs(DateTime.UtcNow);

    public void MarkPlayCalled()
    {
        playUtc = DateTime.UtcNow;
        playCalledMs = ElapsedMs(playUtc);
        process.Refresh();
        cpuAtPlay = process.TotalProcessorTime;
    }

    /// <param name="outputText">画面に出している、曲の形式・処理経路・出力形式・デバイスの表示。</param>
    /// <param name="outputNotice">代わりの出力で鳴らしている場合の説明。</param>
    /// <param name="badge">ビットパーフェクト再生の印の表示。</param>
    /// <param name="hasArtwork">アートワークを表示しているか。</param>
    public void Write(TimeSpan sourcePosition, string? error, string outputText, string? outputNotice, string badge, bool hasArtwork)
    {
        DateTime now = DateTime.UtcNow;
        process.Refresh();
        double playedSeconds = playCalledMs < 0 ? 0 : (now - playUtc).TotalSeconds;
        double cpuPercentOfOneCore = playedSeconds > 0
            ? (process.TotalProcessorTime - cpuAtPlay).TotalSeconds / playedSeconds * 100
            : -1;

        // 回収前の値（上の各プロパティ）を取り終えてから、回収後に残る量を測る。
        double workingSetMb = ToMb(process.WorkingSet64);
        double privateMb = ToMb(process.PrivateMemorySize64);
        double managedHeapMb = ToMb(GC.GetTotalMemory(false));
        double gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        double retainedHeapMb = ToMb(GC.GetTotalMemory(forceFullCollection: true));

        var c = CultureInfo.InvariantCulture;
        string json = "{"
            + $"\"shownMs\":{shownMs.ToString("F1", c)},"
            + $"\"playCalledMs\":{playCalledMs.ToString("F1", c)},"
            + $"\"playedSeconds\":{playedSeconds.ToString("F1", c)},"
            + $"\"sourcePositionSeconds\":{sourcePosition.TotalSeconds.ToString("F1", c)},"
            + $"\"cpuPercentOfOneCore\":{cpuPercentOfOneCore.ToString("F2", c)},"
            + $"\"workingSetMb\":{workingSetMb.ToString("F1", c)},"
            + $"\"privateMb\":{privateMb.ToString("F1", c)},"
            + $"\"managedHeapMb\":{managedHeapMb.ToString("F1", c)},"
            + $"\"retainedHeapMb\":{retainedHeapMb.ToString("F1", c)},"
            + $"\"gcPauseMs\":{gcPauseMs.ToString("F1", c)},"
            + $"\"allocatedMb\":{ToMb(GC.GetTotalAllocatedBytes()).ToString("F1", c)},"
            + $"\"gen0Collections\":{GC.CollectionCount(0)},"
            + $"\"output\":{Quote(outputText)},"
            + $"\"outputNotice\":{Quote(outputNotice)},"
            + $"\"badge\":{Quote(badge)},"
            + $"\"hasArtwork\":{(hasArtwork ? "true" : "false")},"
            + $"\"error\":{Quote(error)}"
            + "}";
        File.WriteAllText(outputPath, json);
    }

    private double ElapsedMs(DateTime utc) => (utc - process.StartTime.ToUniversalTime()).TotalMilliseconds;

    private static double ToMb(long bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>JSON の文字列にする。null は JSON の null。</summary>
    private static string Quote(string? text) => text is null
        ? "null"
        : "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").ReplaceLineEndings(" ") + "\"";
}
