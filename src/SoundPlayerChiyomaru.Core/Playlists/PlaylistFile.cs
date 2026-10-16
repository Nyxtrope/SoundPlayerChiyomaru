using System.Globalization;
using System.Text;

namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>プレイリストファイルの読み書き。書き込みは M3U8（UTF-8）、読み込みは M3U8 / M3U / PLS。</summary>
public static class PlaylistFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 曲順の番号（<see cref="PlaylistItem.Order"/>）を書く行の始まり。次の曲の行にかかる。
    /// 「#」で始まるので、他のプレイヤーはコメントとして読み飛ばす。
    /// </summary>
    private const string OrderTag = "#CHIYOMARU-ORDER:";

    public static IReadOnlyList<string> Extensions { get; } = [".m3u8", ".m3u", ".pls"];

    public static bool IsPlaylistFile(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// 曲のパスを絶対パスで返す。相対パスはプレイリストファイルの場所を基準に解決する。
    /// 解釈できない行と、ファイル以外（http など）の項目は読み飛ばす。
    /// </summary>
    public static IReadOnlyList<string> Read(string path) => [.. ReadEntries(path).Select(entry => entry.Path)];

    /// <summary><see cref="Read"/> と同じ。このアプリが書いた曲順の番号があれば、それも返す。</summary>
    public static IReadOnlyList<PlaylistEntry> ReadEntries(string path)
    {
        string text = Decode(File.ReadAllBytes(path));
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        bool isPls = Path.GetExtension(path).Equals(".pls", StringComparison.OrdinalIgnoreCase);

        var result = new List<PlaylistEntry>();
        int? order = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!isPls && line.StartsWith(OrderTag, StringComparison.Ordinal))
            {
                order = int.TryParse(line.AsSpan(OrderTag.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
                    ? value
                    : null;
                continue;
            }

            string? entry = isPls ? ParsePlsLine(line) : ParseM3uLine(line);
            if (entry is null)
            {
                continue;
            }
            if (Resolve(entry, baseDirectory) is { } resolved)
            {
                result.Add(new PlaylistEntry(resolved, order));
            }
            order = null;
        }
        return result;
    }

    /// <summary>
    /// プレイリストに、プレイリストファイル自身とは別のサーバーにあるかもしれない曲が書かれているか。
    /// そうした曲を開くと、Windows がそのサーバーへ接続し、利用者の資格情報を送ることがある。開く前に確かめるために使う。
    /// このコンピューターのドライブの曲と、プレイリストファイルと同じサーバーの曲は数えない
    /// （後者は、ファイルを読んだ時点でそのサーバーに接続している）。どちらとも言えない書き方の曲は数える。
    /// </summary>
    /// <param name="playlistPath">プレイリストファイルの絶対パス。</param>
    /// <param name="entries">プレイリストに書かれた曲（<see cref="ReadEntries"/> で解決済みの絶対パス）。</param>
    public static bool ReachesOtherServers(string playlistPath, IEnumerable<string> entries)
    {
        string? own = NetworkPaths.ServerOf(playlistPath);
        return entries.Any(entry =>
            !NetworkPaths.IsOnLocalDrive(entry)
            && !(own is not null && string.Equals(NetworkPaths.ServerOf(entry), own, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>M3U8（UTF-8、BOM なし）で書く。一時ファイル経由で置き換える。</summary>
    /// <param name="relativePaths">プレイリストファイルと同じドライブにある曲を、相対パスで書くか。</param>
    public static void Write(string path, IReadOnlyList<PlaylistItem> items, bool relativePaths)
    {
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var text = new StringBuilder("#EXTM3U\n");

        // 曲順の番号が並びのとおり（1, 2, 3, ...）なら、書かなくても読んだときに同じ番号になる。
        // シャッフルや並べ替えで並びと番号がずれているときだけ書く。番号のない曲（0）は、あっても書かない。
        bool writeOrder = false;
        for (int i = 0; i < items.Count && !writeOrder; i++)
        {
            writeOrder = items[i].Order > 0 && items[i].Order != i + 1;
        }

        foreach (PlaylistItem item in items)
        {
            if (writeOrder && item.Order > 0)
            {
                text.Append(OrderTag).Append(item.Order.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (item.Duration is { } duration)
            {
                string label = string.IsNullOrWhiteSpace(item.Artist)
                    ? item.DisplayTitle
                    : $"{item.Artist} - {item.DisplayTitle}";
                text.Append("#EXTINF:")
                    .Append(((long)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture))
                    .Append(',')
                    .Append(label.ReplaceLineEndings(" "))
                    .Append('\n');
            }
            text.Append(relativePaths ? ToRelative(item.Path, baseDirectory) : item.Path).Append('\n');
        }

        AtomicFile.WriteAllText(path, text.ToString(), Utf8NoBom);
    }

    private static string ToRelative(string path, string baseDirectory)
    {
        // 別ドライブや UNC の別共有には相対パスで届かないので、絶対パスのままにする。
        string relative = Path.GetRelativePath(baseDirectory, path);
        if (Path.IsPathRooted(relative))
        {
            return path;
        }

        // 「#」で始まる行はコメント、行頭の空白は読むときに削られる。そういうファイル名でも読み戻せるよう、".\" を付ける。
        return relative.StartsWith('#') || char.IsWhiteSpace(relative[0]) ? @".\" + relative : relative;
    }

    private static string? ParseM3uLine(string line) =>
        line.Length == 0 || line[0] == '#' ? null : line;

    private static string? ParsePlsLine(string line)
    {
        if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 曲の行は "File1=..." の形。"File" と "=" の間が数字だけでなければ曲の行ではない。
        int equals = line.IndexOf('=');
        if (equals <= 4)
        {
            return null;
        }
        ReadOnlySpan<char> number = line.AsSpan(4, equals - 4).Trim();
        if (number.IsEmpty || number.ContainsAnyExceptInRange('0', '9'))
        {
            return null;
        }

        string value = line[(equals + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? Resolve(string entry, string baseDirectory)
    {
        try
        {
            if (entry.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(entry).LocalPath;
            }
            if (entry.Contains("://", StringComparison.Ordinal))
            {
                return null;
            }
            return Path.GetFullPath(entry, baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return Utf8NoBom.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 古い M3U / PLS はシステムの文字コード（日本語環境ならシフト JIS）で書かれている。
            // コードページ 0 は「システムの既定」。表示言語（CurrentCulture）とは別の設定なので、そちらからは決めない。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0).GetString(bytes);
        }
    }
}
