namespace SoundPlayerChiyomaru.Core.Playlists;

public interface ITrackInfoReader
{
    /// <summary>タグを読む。対応していない形式や壊れたファイルでは null を返す（例外は投げない）。</summary>
    TrackInfo? Read(string path);
}
