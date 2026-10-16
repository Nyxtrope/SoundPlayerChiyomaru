using System.Globalization;
using System.IO.Enumeration;

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

    /// <summary>フォルダの中のファイル名（文字列を作らずに渡される）で引くための口。</summary>
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> audioExtensionLookup;

    /// <param name="audioExtensions">曲として扱う拡張子（ドット付き）。</param>
    public PathExpander(IEnumerable<string> audioExtensions)
    {
        this.audioExtensions = new HashSet<string>(audioExtensions, StringComparer.OrdinalIgnoreCase);
        audioExtensionLookup = this.audioExtensions.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// フォルダは中の曲をファイル名順に、プレイリストファイルはその中身に展開する。
    /// フォルダの中と、複数まとめて渡されたファイルからは、曲の拡張子のものだけを拾う。
    /// ファイルを 1 個だけ渡された場合は、拡張子を問わずそのまま返す（未知の形式でも再生を試せるように）。
    /// </summary>
    /// <param name="playlists">
    /// すでに読んだプレイリストファイル。渡すと、プレイリストファイルは読み直さずにこの中身を使う（開く前に確かめた中身と同じにするため）。
    /// </param>
    public IReadOnlyList<string> Expand(IReadOnlyList<string> paths, PlaylistReads? playlists = null)
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
                result.AddRange(playlists is null ? ReadPlaylist(path) : playlists.EntriesOf(path).Select(entry => entry.Path));
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
            var files = new FileSystemEnumerable<string>(
                Path.GetFullPath(directory),
                (ref FileSystemEntry entry) => entry.ToFullPath(),
                Recursive)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && audioExtensionLookup.Contains(Path.GetExtension(entry.FileName)),
                ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLinkedDirectory(ref entry),
            };
            return files.OrderBy(file => file, comparer).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// ジャンクションかシンボリックリンクのフォルダか。中へは入らない（SPEC 4.1）。たどると、自分の上のフォルダを指すリンクで
    /// 探索が終わらなくなる（循環が 2 つあると、たどる道が分かれるたびに倍になる）。
    /// OneDrive の「必要なときにダウンロード」のフォルダも同じ属性（リパースポイント）を持つが、リンクではないので中を探す。
    /// </summary>
    private static bool IsLinkedDirectory(ref FileSystemEntry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return entry.ToFileSystemInfo().LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 種類を確かめられないリパースポイント（DFS のフォルダなど）は、これまでどおり中を探す。
            // 循環を作るジャンクションとシンボリックリンクは、種類を確かめられる。
            return false;
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
