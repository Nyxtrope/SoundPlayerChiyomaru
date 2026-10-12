using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundPlayerChiyomaru.Core.Instance;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;

namespace SoundPlayerChiyomaru.Core.Settings;

/// <summary>画面の配色。Chiyomaru の付くものはこのアプリ独自の配色、付かないものは Windows の色に従う。</summary>
public enum AppTheme
{
    /// <summary>独自の配色。明るい・暗いは Windows の設定に従う。</summary>
    ChiyomaruSystem,
    ChiyomaruLight,
    ChiyomaruDark,

    /// <summary>Windows の配色。明るい・暗いは Windows の設定に従う。</summary>
    WindowsSystem,
    WindowsLight,
    WindowsDark,
}

/// <summary>設定ファイルに保存する内容。</summary>
public sealed class AppSettings
{
    /// <summary>0.0 から 1.0。</summary>
    public double Volume { get; set; } = 1.0;

    public PlayMode PlayMode { get; set; }

    /// <summary>出力に使うデバイスの ID。null は Windows の既定のデバイスに従う。</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>WASAPI 排他モードで出力するか（ビットパーフェクト再生用）。</summary>
    public bool ExclusiveMode { get; set; }

    /// <summary>出力のバッファの長さ（ミリ秒）。</summary>
    public int BufferMilliseconds { get; set; } = Output.OutputDeviceSelection.DefaultBufferMilliseconds;

    /// <summary>排他モードでデバイスへ渡すビット深度の上限。null は上限なし（自動）。</summary>
    public int? MaxOutputBitDepth { get; set; }

    /// <summary>プレイリストに曲を追加するとき、すでにある曲（同じパス）を追加しないか。</summary>
    public bool ExcludeDuplicates { get; set; }

    /// <summary>ReplayGain の使い方。有効な間はビットパーフェクトではなくなる。</summary>
    public ReplayGainMode ReplayGain { get; set; }

    /// <summary>プレイリストを隠して、曲の情報と操作だけの小さなウィンドウにするか。</summary>
    public bool MiniMode { get; set; }

    /// <summary>他のウィンドウより常に手前に表示するか。</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// 前回終了時のウィンドウの位置と大きさ。ミニモードで終了した場合も、高さは通常の表示に戻したときのものを入れる。
    /// 一度も保存していなければ null。
    /// </summary>
    public WindowBounds? Window { get; set; }

    /// <summary>画面の配色。</summary>
    public AppTheme Theme { get; set; }

    /// <summary>
    /// プレイリストの列。表示する順に並べる。読んだままでは欠けや重複がありうるので、
    /// <see cref="ColumnLayout.Normalize"/> を通してから使うこと。
    /// </summary>
    public List<ColumnSetting>? Columns { get; set; }

    /// <summary>起動済みのインスタンスにファイルを渡されたときの動作。</summary>
    public ExternalOpenAction ExternalOpenAction { get; set; }

    /// <summary>プレイリストを保存するとき、曲を相対パスで書くか。</summary>
    public bool SaveRelativePaths { get; set; }

    /// <summary>作業中プレイリストの読み込み元のファイル（「上書き保存」の書き戻し先）。</summary>
    public string? PlaylistFilePath { get; set; }

    /// <summary>前回終了時に選ばれていた曲の、作業中プレイリスト内での位置。なければ -1。</summary>
    public int CurrentIndex { get; set; } = -1;
}

// Native AOT ではリフレクションによる JSON の読み書きが使えないので、ソース生成を使う。
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// 設定と作業中プレイリストを、指定したフォルダ（ポータブル運用では実行ファイルの隣）に保存する。
/// </summary>
public sealed class SettingsStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string settingsPath;

    public SettingsStore(string directory)
    {
        settingsPath = Path.Combine(directory, "settings.json");
        WorkingPlaylistPath = Path.Combine(directory, "playlist.m3u8");
    }

    /// <summary>作業中プレイリストの自動保存先。利用者が名前を付けて保存するファイルとは別。</summary>
    public string WorkingPlaylistPath { get; }

    /// <summary>設定を読む。ファイルがない、または壊れている場合は既定値を返す。</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                using FileStream stream = File.OpenRead(settingsPath);
                return JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 読めない設定で起動できなくなるより、既定値で起動するほうがよい。
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings) => AtomicFile.WriteAllText(
        settingsPath,
        JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings),
        Utf8NoBom);

    /// <summary>作業中プレイリストの曲を読む。保存されていない、または読めない場合は空。</summary>
    public IReadOnlyList<PlaylistEntry> LoadWorkingPlaylist()
    {
        try
        {
            return File.Exists(WorkingPlaylistPath) ? PlaylistFile.ReadEntries(WorkingPlaylistPath) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void SaveWorkingPlaylist(IReadOnlyList<PlaylistItem> items) =>
        PlaylistFile.Write(WorkingPlaylistPath, items, relativePaths: false);
}
