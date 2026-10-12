using System.Globalization;

namespace SoundPlayerChiyomaru;

/// <summary>
/// コマンドライン引数。オプション以外はすべて、開くファイル・フォルダ・プレイリストとして扱う。
/// <c>--bench</c> は起動時間とメモリの実測用で、指定秒数だけ再生した後に結果を書き出して終了する。
/// </summary>
sealed record StartupOptions(IReadOnlyList<string> FilePaths, string? BenchOutputPath, int BenchSeconds)
{
    private const int DefaultBenchSeconds = 5;

    public static StartupOptions Parse(string[] args)
    {
        var filePaths = new List<string>();
        string? benchOutputPath = null;
        int benchSeconds = DefaultBenchSeconds;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bench" when i + 1 < args.Length:
                    benchOutputPath = args[++i];
                    break;
                case "--bench-seconds" when i + 1 < args.Length
                    && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                    && seconds > 0:
                    benchSeconds = seconds;
                    i++;
                    break;
                default:
                    filePaths.Add(ToFullPath(args[i]));
                    break;
            }
        }

        return new StartupOptions(filePaths, benchOutputPath, benchSeconds);
    }

    /// <summary>
    /// 相対パスを、このプロセスの作業フォルダを基準に絶対パスにする。
    /// 起動済みのインスタンスへ渡す場合、受け取る側は作業フォルダが違うので、渡す前に解決しておく必要がある。
    /// </summary>
    private static string ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // パスとして解釈できない引数。そのまま渡して、開く側のエラー表示に任せる。
            return path;
        }
    }
}
