using System.Globalization;

namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>
/// 開く・ドロップ・引数で渡されたパス（ファイル、フォルダ、プレイリストファイル）を、曲のファイルの並びに展開する。
/// </summary>
public sealed class PathExpander
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly HashSet<string> audioExtensions;

    /// <param name="audioExtensions">曲として扱う拡張子（ドット付き）。</param>
    public PathExpander(IEnumerable<string> audioExtensions)
    {
        this.audioExtensions = new HashSet<string>(audioExtensions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// フォルダは中の曲をファイル名順に、プレイリストファイルはその中身に展開する。
    /// フォルダの中と、複数まとめて渡されたファイルからは、曲の拡張子のものだけを拾う。
    /// ファイルを 1 個だけ渡された場合は、拡張子を問わずそのまま返す（未知の形式でも再生を試せるように）。
    /// </summary>
    public IReadOnlyList<string> Expand(IReadOnlyList<string> paths)
    {
        var result = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                result.AddRange(EnumerateDirectory(path));
            }
            else if (PlaylistFile.IsPlaylistFile(path))
            {
                result.AddRange(ReadPlaylist(path));
            }
            else if (paths.Count == 1 || audioExtensions.Contains(Path.GetExtension(path)))
            {
                result.Add(Path.GetFullPath(path));
            }
        }
        return result;
    }

    private IEnumerable<string> EnumerateDirectory(string directory)
    {
        var comparer = StringComparer.Create(
            CultureInfo.CurrentCulture,
            CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

        try
        {
            return Directory.EnumerateFiles(Path.GetFullPath(directory), "*", Recursive)
                .Where(file => audioExtensions.Contains(Path.GetExtension(file)))
                .OrderBy(file => file, comparer)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadPlaylist(string path)
    {
        try
        {
            return PlaylistFile.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
