namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>プレイリストへ追加する曲から、重複を除く（設定で「重複を除外」を選んでいる場合）。</summary>
public static class PlaylistDuplicates
{
    /// <summary>
    /// すでにプレイリストにある曲と、追加する曲の中で 2 回目以降に出てくる曲を除く。順番は保つ。
    /// 同じ曲かどうかはパスで決め、大文字と小文字は区別しない（Windows のファイル名と同じ扱い）。
    /// </summary>
    /// <param name="existing">すでにプレイリストにある曲。</param>
    public static IReadOnlyList<string> Exclude(IEnumerable<string> paths, IEnumerable<PlaylistItem> existing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistItem item in existing)
        {
            seen.Add(item.Path);
        }

        var result = new List<string>();
        foreach (string path in paths)
        {
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }
        return result;
    }
}
