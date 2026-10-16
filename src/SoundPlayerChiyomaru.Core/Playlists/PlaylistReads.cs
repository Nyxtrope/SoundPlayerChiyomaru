namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>
/// 開く・追加で渡されたパスのうち、プレイリストファイルを 1 回だけ読んだ結果（SPEC 5 章）。
/// 開く前に確かめた中身と、実際に開く中身を同じにするため、展開と開く処理にもこれを渡す（読み直すと、その間に書き換えられうる）。
/// </summary>
public sealed class PlaylistReads
{
    private readonly Dictionary<string, IReadOnlyList<PlaylistEntry>> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> errors = new(StringComparer.Ordinal);

    /// <summary>プレイリストファイルを含まない場合の結果。</summary>
    public static PlaylistReads None { get; } = new();

    /// <summary>
    /// 別のサーバーにあるかもしれない曲が書かれたプレイリストがあるか（<see cref="PlaylistFile.ReachesOtherServers"/>）。
    /// </summary>
    public bool ReachesOtherServers { get; private set; }

    /// <summary>渡されたパスのうち、プレイリストファイル（フォルダを除く）を読む。読めなかったものは理由を覚えておく。</summary>
    public static PlaylistReads Read(IEnumerable<string> paths)
    {
        var result = new PlaylistReads();
        foreach (string path in paths)
        {
            if (!PlaylistFile.IsPlaylistFile(path) || Directory.Exists(path) || result.entries.ContainsKey(path) || result.errors.ContainsKey(path))
            {
                continue;
            }

            try
            {
                IReadOnlyList<PlaylistEntry> read = PlaylistFile.ReadEntries(path);
                result.entries[path] = read;
                result.ReachesOtherServers |= PlaylistFile.ReachesOtherServers(Path.GetFullPath(path), read.Select(entry => entry.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.errors[path] = ex;
            }
        }
        return result;
    }

    /// <summary>そのプレイリストの曲。読んでいない、または読めなかったプレイリストは空。</summary>
    public IReadOnlyList<PlaylistEntry> EntriesOf(string path) => entries.TryGetValue(path, out IReadOnlyList<PlaylistEntry>? read) ? read : [];

    /// <summary>そのプレイリストを読めなかった理由。読めた、または読んでいなければ null。</summary>
    public Exception? ErrorOf(string path) => errors.GetValueOrDefault(path);
}
