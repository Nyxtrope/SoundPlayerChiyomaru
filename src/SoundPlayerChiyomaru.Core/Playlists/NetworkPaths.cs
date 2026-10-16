namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>
/// ネットワーク上のパス（UNC パス）の判定。ファイルにもネットワークにも触らず、文字列だけで判定する。
/// </summary>
public static class NetworkPaths
{
    /// <summary>UNC パスの書き方。この後ろにサーバー名が続く。</summary>
    private static readonly string[] UncPrefixes =
    [
        @"\\?\UNC\",
        @"\\.\UNC\",
        @"\??\UNC\",
        @"\\?\GLOBALROOT\Device\Mup\",
        @"\\.\GLOBALROOT\Device\Mup\",
        @"\??\GLOBALROOT\Device\Mup\",
    ];

    /// <summary>ドライブ文字の前に付けられる書き方（<c>\\?\C:\</c> など）。</summary>
    private static readonly string[] DevicePrefixes = [@"\\?\", @"\\.\", @"\??\"];

    /// <summary>
    /// UNC パス（<c>\\server\share\...</c>、<c>\\?\UNC\server\share\...</c> など）のサーバー名。分からなければ null。
    /// </summary>
    public static string? ServerOf(string path)
    {
        ReadOnlySpan<char> rest;
        if (UncPrefixes.FirstOrDefault(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } unc)
        {
            rest = path.AsSpan(unc.Length);
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2 && path[2] is not ('?' or '.'))
        {
            rest = path.AsSpan(2);
        }
        else
        {
            return null;
        }

        int end = rest.IndexOfAny('\\', '/');
        ReadOnlySpan<char> server = end < 0 ? rest : rest[..end];
        return server.IsEmpty ? null : server.ToString();
    }

    /// <summary>
    /// このコンピューターのドライブ文字のパス（<c>C:\...</c>、<c>\\?\C:\...</c>）か。
    /// <c>Z:\</c> のように割り当てたドライブも含む（利用者が自分で接続したサーバーなので）。
    /// 確かめる対象を漏らさないよう、これに当たらない書き方は、すべてネットワーク上かもしれないものとして扱う。
    /// </summary>
    public static bool IsOnLocalDrive(string path)
    {
        ReadOnlySpan<char> rest = path;
        if (DevicePrefixes.FirstOrDefault(prefix => path.StartsWith(prefix, StringComparison.Ordinal)) is { } device)
        {
            rest = rest[device.Length..];
        }
        return rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] is '\\' or '/';
    }
}
