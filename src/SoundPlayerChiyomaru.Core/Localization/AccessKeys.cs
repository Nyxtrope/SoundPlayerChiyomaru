namespace SoundPlayerChiyomaru.Core.Localization;

/// <summary>メニューの見出しの文字キー（「_」の次の文字。Alt と組み合わせて選ぶ）の表記。</summary>
public static class AccessKeys
{
    /// <summary>
    /// 見出しから文字キーの表記（日本語の「(_F)」、英語の「_」）を取り除く。右クリックのメニューでは文字キーで選べないため。
    /// 名前の中の「_」を表す「__」（デバイス名など）は、そのまま残す。
    /// </summary>
    public static string Remove(string header)
    {
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != '_')
            {
                continue;
            }
            if (i + 1 < header.Length && header[i + 1] == '_')
            {
                i++;
                continue;
            }
            return i > 0 && header[i - 1] == '(' && i + 2 < header.Length && header[i + 2] == ')'
                ? header.Remove(i - 1, 4)
                : header.Remove(i, 1);
        }
        return header;
    }
}
