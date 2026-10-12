namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>
/// 曲のアートワーク（画像ファイルの中身）を探す。タグに埋め込まれた画像を使い、なければ曲と同じフォルダの folder.jpg を使う。
/// </summary>
public sealed class ArtworkFinder(IArtworkReader reader)
{
    private const string FolderImageName = "folder.jpg";

    /// <summary>画像ファイルとして読む大きさの上限。</summary>
    private const long MaxFileSize = 32 << 20;

    /// <returns>画像がなければ null（例外は投げない）。</returns>
    public byte[]? Find(string path) => reader.ReadArtwork(path) ?? ReadFolderImage(path);

    private static byte[]? ReadFolderImage(string path)
    {
        try
        {
            if (Path.GetDirectoryName(path) is not { Length: > 0 } directory)
            {
                return null;
            }

            var file = new FileInfo(Path.Combine(directory, FolderImageName));
            return file.Exists && file.Length is > 0 and <= MaxFileSize ? File.ReadAllBytes(file.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 画像が読めなくても再生には関係ない。なしとして扱う。
            return null;
        }
    }
}
