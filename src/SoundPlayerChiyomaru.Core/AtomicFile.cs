using System.Text;

namespace SoundPlayerChiyomaru.Core;

internal static class AtomicFile
{
    /// <summary>
    /// 一時ファイルに書いてから置き換える。書き込み中に異常終了しても、元のファイルが壊れた状態で残らない。
    /// 失敗した場合は例外を投げ、元のファイルはそのまま残る。
    /// </summary>
    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, encoding);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // 置き換えられなかった（書き込み先が読み取り専用、ディスクがいっぱいなど）。一時ファイルを残さない。
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 元の失敗を伝えるほうが大事なので、後始末の失敗は無視する。
            }
            throw;
        }
    }
}
