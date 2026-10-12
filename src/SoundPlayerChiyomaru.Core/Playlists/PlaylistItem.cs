using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>タグから読み取った曲の情報。</summary>
public sealed record TrackInfo(
    string? Title,
    string? Artist,
    string? Album,
    uint TrackNumber,
    TimeSpan? Duration,
    string? AlbumArtist = null);

/// <summary>プレイリストファイルの 1 曲。</summary>
/// <param name="Order">ファイルに書かれていた曲順の番号。書かれていなければ null。</param>
public readonly record struct PlaylistEntry(string Path, int? Order = null);

/// <summary>
/// プレイリストの 1 行。同じファイルを複数回入れられるので、パスではなくこのオブジェクト自体で行を区別する。
/// </summary>
public sealed class PlaylistItem : INotifyPropertyChanged
{
    private TrackInfo? info;
    private bool isMissing;
    private bool isCurrent;
    private int order;

    public PlaylistItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string FileName { get; }

    /// <summary>
    /// 曲順の番号（1 から）。プレイリストに追加したときに振り、並べ替えやシャッフルをしても変わらない
    /// （元の順を覚えておくためのもの）。振り直すのは <see cref="Playlist.Renumber"/> だけ。
    /// </summary>
    public int Order
    {
        get => order;
        internal set
        {
            if (order != value)
            {
                order = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OrderText));
            }
        }
    }

    public string OrderText => order.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? Title => info?.Title;

    public string? Artist => info?.Artist;

    public string? Album => info?.Album;

    public string? AlbumArtist => info?.AlbumArtist;

    public uint TrackNumber => info?.TrackNumber ?? 0;

    /// <summary>トラック番号。タグになければ空。</summary>
    public string TrackNumberText => TrackNumber == 0 ? "" : TrackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public TimeSpan? Duration => info?.Duration;

    /// <summary>タイトルのタグがなければファイル名（拡張子なし）。</summary>
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title;

    public string DurationText => Duration is { } duration ? FormatDuration(duration) : "";

    /// <summary>「3:05」や「1:02:03」の形にする。プレイリストの合計にも使うので、24 時間を超えても時間の桁のまま数える。</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        long seconds = Math.Max(0, (long)duration.TotalSeconds);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return seconds >= 3600
            ? string.Create(culture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(culture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>ファイルが存在しない（リンク切れ）。</summary>
    public bool IsMissing
    {
        get => isMissing;
        private set => Set(ref isMissing, value);
    }

    /// <summary>再生対象として選ばれている曲か。</summary>
    public bool IsCurrent
    {
        get => isCurrent;
        internal set => Set(ref isCurrent, value);
    }

    /// <summary>情報の読み取りを依頼済みか。二重に読まないための印。</summary>
    internal bool InfoRequested { get; set; }

    internal void ApplyInfo(TrackInfo? newInfo, bool exists)
    {
        info = newInfo;
        IsMissing = !exists;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(AlbumArtist));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(TrackNumberText));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DurationText));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
