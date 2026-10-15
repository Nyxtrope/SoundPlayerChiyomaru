using System.Diagnostics;
using Avalonia;
using SoundPlayerChiyomaru.Core.Instance;

namespace SoundPlayerChiyomaru;

static class Program
{
    /// <summary>
    /// 最初のインスタンスになるか、起動済みのインスタンスにファイルを渡すかが決まるまで待つ時間。
    /// 相手が起動処理や終了処理の途中でも、どちらかに落ち着くまで待てる長さにする。
    /// </summary>
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendAttemptTimeout = TimeSpan.FromMilliseconds(500);

    public static StartupOptions Options { get; private set; } = StartupOptions.Parse([]);

    /// <summary>このプロセスが起動した時刻（<see cref="TimeProvider.GetTimestamp"/> の値）。</summary>
    public static long StartTimestamp { get; private set; }

    /// <summary>このプロセスが最初のインスタンスである印。単独で動かしている場合（計測用の起動など）は null。</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>終了した後に、このアプリをもう一度起動するか（設定画面の「今すぐ再起動」）。</summary>
    public static bool RestartRequested { get; set; }

    [STAThread]
    static void Main(string[] args)
    {
        StartTimestamp = TimeProvider.System.GetTimestamp();
        Options = StartupOptions.Parse(args);

        // 計測用の起動は、利用者が普段使っているインスタンスと混ざらないよう単独で動かす。
        if (Options.BenchOutputPath is null && !TryBecomeFirstInstance())
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance?.Dispose();
        }

        // 設定を保存し、印を手放した後に起動する。先に起動すると、新しいプロセスは起動済みのこちらへ
        // 「ファイルなし」を渡して終わってしまう（このプロセスも終了するので、何も残らない）。
        if (RestartRequested && Environment.ProcessPath is { } path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 画面はもう閉じているので知らせる場所がない。利用者が起動し直せば同じ状態に戻る。
            }
        }
    }

    /// <summary>
    /// 最初のインスタンスになる。起動済みのインスタンスにファイルを渡せた場合は false（このプロセスは終了する）。
    /// </summary>
    private static bool TryBecomeFirstInstance()
    {
        string name = SingleInstance.DefaultName;
        var elapsed = Stopwatch.StartNew();
        do
        {
            Instance = SingleInstance.TryAcquire(name);
            if (Instance is not null)
            {
                return true;
            }
            if (SingleInstance.TrySend(name, Options.FilePaths, SendAttemptTimeout))
            {
                return false;
            }

            // 相手は印を持っているのに受け取らない。終了処理の途中なら、まもなく印が空くので取り直す。
        }
        while (elapsed.Elapsed < HandOffTimeout);

        // どちらにもならなかった（相手が応答しない、権限が違って渡せないなど）。印を持たない単独のインスタンスとして起動する。
        return true;
    }

    // GPU 描画（既定）は小さなウィンドウでも起動に約 150ms、メモリに 100MB 以上を余分に使う（SPEC 8.2 の実測）。
    // 画面の更新が少ないプレーヤーには不要なので、CPU で描画する。
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
}
