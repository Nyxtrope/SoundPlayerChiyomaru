using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Instance;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Settings;
using SoundPlayerChiyomaru.Core.Shell;

namespace SoundPlayerChiyomaru;

/// <summary>出力デバイスの選択肢。</summary>
/// <param name="Id">デバイスの ID。null は Windows の既定のデバイスに従う。</param>
public sealed record OutputDeviceChoice(string? Id, string Name, bool IsSelected);

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const string AppName = "S:P Chiyomaru";
    private const string EmptyTitle = "ファイルを開くか、ここにドロップしてください";

    /// <summary>このアプリのバージョン（「0.1.0」の形）。プロジェクトファイルの Version から来る。</summary>
    public static string AppVersion { get; } =
        typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    private readonly Playlist playlist;
    private readonly PlaybackService playback;
    private readonly PlaylistPlayer player;
    private readonly PathExpander expander;
    private readonly TrackInfoLoader infoLoader;
    private readonly SettingsStore store;
    private readonly List<ColumnSetting> columns;
    private readonly AppSettings settings;
    private readonly FileAssociationRegistrar fileAssociation;
    private readonly IAudioDeviceService deviceService;
    private readonly OutputDeviceSelection deviceSelection;
    private readonly ArtworkLoader artworkLoader;
    private bool deviceErrorShown;
    private BitPerfectBadge badge;
    private string? badgeToolTip;
    private Bitmap? artwork;
    private int artworkPixelSize;
    private readonly DispatcherTimer positionTimer;
    private readonly DispatcherTimer autoSaveTimer;
    private readonly DispatcherTimer statusTimer;

    // エクスプローラーで複数のファイルを選んで開くと、ファイルごとのプロセスが続けて起動する。
    // プロセスの起動には 1 個あたり 0.1 秒ほどかかるので、それより十分長い間隔までを 1 回の操作とみなす。
    private readonly ExternalOpenPlanner openPlanner = new(TimeSpan.FromMilliseconds(700));
    private PlayerState state;
    private string? noticeText;
    private bool syncingFromPlayback;
    private PlaylistItem? titleSource;

    private string trackTitle = EmptyTitle;
    private string? albumText;
    private string formatText = "";
    private string? errorText;
    private string statusText = "";
    private bool hasTrack;
    private bool hasItems;
    private bool isPlaying;
    private bool canUndo;
    private bool canRedo;
    private double durationSeconds;
    private double positionSeconds;
    private double volumePercent;
    private bool isMuted;

    public MainWindowViewModel(
        Playlist playlist,
        PlaybackService playback,
        PlaylistPlayer player,
        PathExpander expander,
        TrackInfoLoader infoLoader,
        SettingsStore store,
        AppSettings settings,
        IReadOnlyList<string> audioExtensions,
        FileAssociationRegistrar fileAssociation,
        IAudioDeviceService deviceService,
        OutputDeviceSelection deviceSelection,
        ArtworkLoader artworkLoader)
    {
        this.artworkLoader = artworkLoader;
        artworkLoader.Loaded += OnArtworkLoaded;
        this.fileAssociation = fileAssociation;
        this.deviceService = deviceService;
        this.deviceSelection = deviceSelection;
        playback.OutputChanged += OnOutputChanged;
        this.playlist = playlist;
        this.playback = playback;
        this.player = player;
        this.expander = expander;
        this.infoLoader = infoLoader;
        this.store = store;
        this.settings = settings;
        columns = ColumnLayout.Normalize(settings.Columns);
        settings.Columns = columns;
        AudioExtensions = audioExtensions;
        volumePercent = playback.Volume * 100;

        positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        positionTimer.Tick += (_, _) => SyncPosition();

        // 編集のたびにすぐ書くと連続操作で無駄が多いので、操作が落ち着いてから保存する。
        autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        autoSaveTimer.Tick += (_, _) => SaveSession();

        // タグは 1 曲ずつ読み終わる。そのたびに合計時間を数え直すと、数千曲で数千回になるので、少し待ってまとめて直す。
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        statusTimer.Tick += (_, _) =>
        {
            statusTimer.Stop();
            UpdateStatus();
        };
        infoLoader.InfoApplied += OnInfoApplied;

        playlist.Changed += OnPlaylistChanged;
        playback.StateChanged += OnStateChanged;
        playback.PlaybackFailed += OnPlaybackFailed;
        player.CurrentChanged += OnCurrentChanged;
        player.TrackFailed += OnTrackFailed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>出力が作られた、または手放された（設定画面の「今の出力」の表示を直すために使う）。</summary>
    public event EventHandler? OutputStatusChanged;

    /// <summary>再生対象の曲が変わった。画面がその行までスクロールするために使う。</summary>
    public event EventHandler? CurrentItemChanged;

    /// <summary>曲として扱う拡張子（ドット付き）。「開く」ダイアログに出す。</summary>
    public IReadOnlyList<string> AudioExtensions { get; }

    public IReadOnlyList<PlaylistItem> Items => playlist.Items;

    public PlaylistItem? CurrentItem => player.Current;

    /// <summary>再生モードの表示名。並びは <see cref="PlayMode"/> の値の順。</summary>
    public IReadOnlyList<string> ModeNames { get; } = ["通常", "1曲リピート", "全曲リピート", "ランダム", "1Shot"];

    public int ModeIndex
    {
        get => (int)player.Mode;
        set
        {
            if (value >= 0 && value < ModeNames.Count && (int)player.Mode != value)
            {
                player.Mode = (PlayMode)value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModeToolTip));
            }
        }
    }

    public string ModeToolTip => $"再生モード: {ModeNames[(int)player.Mode]}";

    /// <summary>画面の配色。切り替えの適用は画面の側で行う。</summary>
    public AppTheme Theme
    {
        get => Enum.IsDefined(settings.Theme) ? settings.Theme : AppTheme.ChiyomaruSystem;
        set
        {
            if (settings.Theme != value && Enum.IsDefined(value))
            {
                settings.Theme = value;
                OnPropertyChanged();
            }
        }
    }

    public bool SaveRelativePaths
    {
        get => settings.SaveRelativePaths;
        set
        {
            if (settings.SaveRelativePaths != value)
            {
                settings.SaveRelativePaths = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>起動済みのこのアプリにファイルを渡されたときの動作。値は <see cref="ExternalOpenAction"/> の順。</summary>
    public int ExternalOpenActionIndex
    {
        get => (int)settings.ExternalOpenAction;
        set
        {
            if (Enum.IsDefined((ExternalOpenAction)value) && (int)settings.ExternalOpenAction != value)
            {
                settings.ExternalOpenAction = (ExternalOpenAction)value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>再生・一時停止・停止の別。</summary>
    public PlayerState State
    {
        get => state;
        private set => Set(ref state, value);
    }

    /// <summary>エラーではないお知らせ（重複する曲を追加しなかった、など）。</summary>
    public string? NoticeText
    {
        get => noticeText;
        private set
        {
            if (Set(ref noticeText, value))
            {
                OnPropertyChanged(nameof(MessageText));
            }
        }
    }

    /// <summary>画面の下の行に出す文。エラーがあればエラー、なければお知らせ。どちらもなければ null。</summary>
    public string? MessageText => errorText ?? noticeText;

    /// <summary><see cref="MessageText"/> がエラーか（色を変える）。</summary>
    public bool IsMessageError => errorText is not null;

    public string WindowTitle => hasTrack ? $"{trackTitle} | {AppName}" : AppName;

    public string TrackTitle
    {
        get => trackTitle;
        private set
        {
            if (Set(ref trackTitle, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    /// <summary>「アルバム - アルバムアーティスト」。どちらもタグになければ null。</summary>
    public string? AlbumText
    {
        get => albumText;
        private set => Set(ref albumText, value);
    }

    /// <summary>曲の形式と、掛けている ReplayGain のゲイン。</summary>
    public string FormatText
    {
        get => formatText;
        private set => Set(ref formatText, value);
    }

    public string? ErrorText
    {
        get => errorText;
        private set
        {
            if (Set(ref errorText, value))
            {
                OnPropertyChanged(nameof(MessageText));
                OnPropertyChanged(nameof(IsMessageError));
            }
        }
    }

    /// <summary>曲数、合計の再生時間、プレイリストの保存先。</summary>
    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    /// <summary>曲を開いている（シークできる）。</summary>
    public bool HasTrack
    {
        get => hasTrack;
        private set
        {
            if (Set(ref hasTrack, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(CanPlayPause));
            }
        }
    }

    public bool HasItems
    {
        get => hasItems;
        private set
        {
            if (Set(ref hasItems, value))
            {
                OnPropertyChanged(nameof(CanPlayPause));
            }
        }
    }

    /// <summary>
    /// 再生・一時停止を押せるか。プレイリストを空にしても再生中の曲は鳴り続けるので、曲を開いている間は押せる。
    /// </summary>
    public bool CanPlayPause => hasItems || hasTrack;

    public bool IsPlaying
    {
        get => isPlaying;
        private set
        {
            if (Set(ref isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseText));
            }
        }
    }

    public string PlayPauseText => isPlaying ? "一時停止" : "再生";

    public bool CanUndo
    {
        get => canUndo;
        private set => Set(ref canUndo, value);
    }

    public bool CanRedo
    {
        get => canRedo;
        private set => Set(ref canRedo, value);
    }

    public double DurationSeconds
    {
        get => durationSeconds;
        private set
        {
            if (Set(ref durationSeconds, value))
            {
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    /// <summary>シークバーの位置。画面から変更されるとシークする。</summary>
    public double PositionSeconds
    {
        get => positionSeconds;
        set
        {
            if (!Set(ref positionSeconds, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TimeText));
            if (!syncingFromPlayback)
            {
                Run(() => playback.Seek(TimeSpan.FromSeconds(value)), "シークできません");
            }
        }
    }

    public string TimeText => $"{FormatTime(positionSeconds)} / {FormatTime(durationSeconds)}";

    public double VolumePercent
    {
        get => volumePercent;
        set
        {
            if (Set(ref volumePercent, value))
            {
                // 音量を動かしたら、ミュートは解除する（動かしたのに鳴らないままにしない）。
                if (isMuted)
                {
                    isMuted = false;
                    OnPropertyChanged(nameof(IsMuted));
                }
                ApplyVolume();
            }
        }
    }

    /// <summary>
    /// このアプリの音を消しているか（Windows のミュートとは別。再生は止めない）。
    /// 音量の設定はそのまま残すので、解除するとミュートする前の音量に戻る。終了すると解除される。
    /// </summary>
    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (Set(ref isMuted, value))
            {
                ApplyVolume();
            }
        }
    }

    private void ApplyVolume()
    {
        playback.Volume = isMuted ? 0f : (float)(volumePercent / 100);

        // 排他モードでは、音量が最大かどうかでビットパーフェクト経路と通常経路が切り替わる。
        UpdateOutputText();
    }

    /// <summary>
    /// WASAPI 排他モードで出力するか。再生中に切り替えると、位置を保ったまま出力を作り直す。
    /// 排他モードの間は、他のアプリの音（通知音を含む）がそのデバイスから鳴らなくなる。
    /// </summary>
    public bool ExclusiveMode
    {
        get => settings.ExclusiveMode;
        set
        {
            if (settings.ExclusiveMode == value)
            {
                return;
            }

            settings.ExclusiveMode = value;
            deviceSelection.PreferExclusive = value;
            OnPropertyChanged();
            Run(playback.RestartOutput, "出力を切り替えられません");
        }
    }

    /// <summary>
    /// 出力のバッファの長さ（ミリ秒）。再生中に変えると、位置を保ったまま出力を作り直す。
    /// </summary>
    public int BufferMilliseconds
    {
        get => settings.BufferMilliseconds;
        set
        {
            value = OutputDeviceSelection.NormalizeBuffer(value);
            if (settings.BufferMilliseconds == value)
            {
                return;
            }

            settings.BufferMilliseconds = value;
            deviceSelection.BufferMilliseconds = value;
            OnPropertyChanged();
            Run(playback.RestartOutput, "出力を切り替えられません");
        }
    }

    /// <summary>
    /// 排他モードでデバイスへ渡すビット深度の上限。null は上限なし（自動）。再生中に変えると、位置を保ったまま出力を作り直す。
    /// </summary>
    public int? MaxOutputBitDepth
    {
        get => settings.MaxOutputBitDepth;
        set
        {
            value = OutputDeviceSelection.NormalizeBitDepth(value);
            if (settings.MaxOutputBitDepth == value)
            {
                return;
            }

            settings.MaxOutputBitDepth = value;
            deviceSelection.MaxBitDepth = value;
            OnPropertyChanged();

            // 共有モードには効かない設定なので、共有モードを選んでいる間は出力を作り直さない（作り直すと音が一瞬途切れる）。
            if (settings.ExclusiveMode)
            {
                Run(playback.RestartOutput, "出力を切り替えられません");
            }
        }
    }

    /// <summary>プレイリストに曲を追加するとき、すでにある曲（同じパス）を追加しないか。</summary>
    public bool ExcludeDuplicates
    {
        get => settings.ExcludeDuplicates;
        set
        {
            if (settings.ExcludeDuplicates != value)
            {
                settings.ExcludeDuplicates = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// ReplayGain の使い方。再生中に切り替えると、出力を止めずにその場で切り替わる。
    /// 有効な間は通常経路で処理するので、ビットパーフェクトではなくなる。
    /// </summary>
    public ReplayGainMode ReplayGainMode
    {
        get => settings.ReplayGain;
        set
        {
            if (settings.ReplayGain == value || !Enum.IsDefined(value))
            {
                return;
            }

            settings.ReplayGain = value;
            OnPropertyChanged();
            Run(() => playback.ReplayGainMode = value, "ReplayGain を切り替えられません");
            UpdateOutputText();
        }
    }

    /// <summary>ビットパーフェクト再生の印。</summary>
    public BitPerfectBadge Badge
    {
        get => badge;
        private set
        {
            if (Set(ref badge, value))
            {
                OnPropertyChanged(nameof(IsBadgeVisible));
                OnPropertyChanged(nameof(IsBadgeWarning));
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    public bool IsBadgeVisible => badge != BitPerfectBadge.None;

    /// <summary>排他モードを選んでいるのに、ビットパーフェクトにできていない。</summary>
    public bool IsBadgeWarning => badge == BitPerfectBadge.Unavailable;

    public string BadgeText => badge == BitPerfectBadge.Unavailable ? "× BP" : "BP";

    /// <summary>印の説明。ビットパーフェクトにできていない場合は、その理由。</summary>
    public string? BadgeToolTip
    {
        get => badgeToolTip;
        private set => Set(ref badgeToolTip, value);
    }

    /// <summary>
    /// 希望どおりの出力を作れず、代わりの出力で鳴らしている場合の説明（排他モードを使えず共有モードにした、など）。
    /// </summary>
    public string? OutputNotice => playback.OutputInfo?.Notice;

    /// <summary>今の出力のモードと処理経路・出力の形式・デバイス（1 項目ずつ）。出力していなければ空。</summary>
    public IReadOnlyList<string> DescribeOutput() => playback.OutputInfo is { } output
        ? OutputStatus.Describe(output, playback.OutputDeviceName, playback.IsBitPerfect, settings.ReplayGain != ReplayGainMode.Off)
        : [];

    /// <summary>今の曲のアートワーク。なければ null。</summary>
    public Bitmap? Artwork
    {
        get => artwork;
        private set
        {
            if (Set(ref artwork, value))
            {
                OnPropertyChanged(nameof(HasArtwork));
            }
        }
    }

    public bool HasArtwork => artwork is not null;

    /// <summary>
    /// アートワークを表示する大きさ（画面の実際のピクセル数）。この大きさに縮小して読み込む。
    /// 画面の拡大率はウィンドウが表示されるまで分からないので、ウィンドウが設定するまでは読み込まない。
    /// </summary>
    public int ArtworkPixelSize
    {
        get => artworkPixelSize;
        set
        {
            if (artworkPixelSize != value)
            {
                artworkPixelSize = value;
                artworkLoader.Cancel();
                LoadArtwork();
            }
        }
    }

    private void LoadArtwork()
    {
        if (artworkPixelSize > 0)
        {
            artworkLoader.Load(player.Current?.Path, artworkPixelSize);
        }
    }

    /// <summary>プレイリストを隠して、曲の情報と操作だけの小さなウィンドウにするか。</summary>
    public bool MiniMode
    {
        get => settings.MiniMode;
        set
        {
            if (settings.MiniMode != value)
            {
                settings.MiniMode = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>前回終了時のウィンドウの位置と大きさ。画面が、起動時に読み、閉じるときに書く。</summary>
    public WindowBounds? WindowBounds
    {
        get => settings.Window;
        set => settings.Window = value;
    }

    public bool AlwaysOnTop
    {
        get => settings.AlwaysOnTop;
        set
        {
            if (settings.AlwaysOnTop != value)
            {
                settings.AlwaysOnTop = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>プレイリストの列の順番・幅・表示のどれかが変わった。</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>プレイリストの列。表示する順に並んでいる（隠している列も含む）。</summary>
    public IReadOnlyList<ColumnSetting> Columns => columns;

    /// <summary>列を表示する・隠す。タイトルの列は隠せない。</summary>
    public void SetColumnVisible(PlaylistColumn id, bool visible)
    {
        ColumnSetting? column = columns.Find(c => c.Id == id);
        if (column is not null && id != PlaylistColumn.Title && column.Visible != visible)
        {
            column.Visible = visible;
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 列の境目をドラッグしている。ドラッグを始めたときの状態から計算し直すので、途中の丸めが積み重ならない。
    /// </summary>
    /// <param name="startWidths">ドラッグを始めたときの各列の設定の幅。</param>
    /// <param name="actualWidths">ドラッグを始めたときの各列の実際の幅（ピクセル）。</param>
    /// <param name="left">境目の左の列の位置。</param>
    /// <param name="delta">ドラッグを始めてから動かした量（ピクセル）。</param>
    public void ResizeColumn(IReadOnlyList<double> startWidths, IReadOnlyList<double> actualWidths, int left, double delta)
    {
        if (startWidths.Count != columns.Count)
        {
            return;
        }
        for (int i = 0; i < columns.Count; i++)
        {
            columns[i].Width = startWidths[i];
        }
        ColumnLayout.Resize(columns, actualWidths, left, delta);
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>列を、<paramref name="before"/> の位置にある列の前へ動かす（末尾へは列の数）。</summary>
    public void MoveColumn(int from, int before)
    {
        if (ColumnLayout.Move(columns, from, before))
        {
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>列の幅と順番を既定に戻す。どの列を表示するかは変えない。</summary>
    public void ShowColumn(PlaylistColumn id, int before)
    {
        if (ColumnLayout.Show(columns, id, before))
        {
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetColumns()
    {
        ColumnLayout.ResetWidthsAndOrder(columns);
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    public TimeSpan PlaybackPosition => playback.Position;

    /// <summary>
    /// プレイリストを置き換えて、先頭から再生する。プレイリストファイルを 1 個だけ渡された場合は、
    /// その中身を読み込み、「上書き保存」の書き戻し先として覚える。
    /// </summary>
    public void OpenPaths(IReadOnlyList<string> paths)
    {
        Run(() =>
        {
            if (paths.Count == 1 && PlaylistFile.IsPlaylistFile(paths[0]))
            {
                string file = Path.GetFullPath(paths[0]);
                playlist.Load(PlaylistFile.ReadEntries(file), file);
            }
            else
            {
                playlist.Load(expander.Expand(paths), null);
            }

            if (playlist.Count == 0)
            {
                player.Select(null);
                ErrorText = "再生できるファイルがありません";
            }
            else
            {
                player.Play(playlist.Items[0]);
            }
        }, "開けません");
    }

    /// <summary>プレイリストに追加する。空のプレイリストに追加して、何も再生していなければ、追加した曲から再生する。</summary>
    /// <param name="index">挿入位置。null なら末尾。</param>
    /// <returns>追加された項目。</returns>
    public IReadOnlyList<PlaylistItem> AddPaths(IReadOnlyList<string> paths, int? index = null) =>
        AddPaths(paths, index, out _);

    /// <param name="first">
    /// 渡された曲のうち最初の曲に当たる項目。重複の除外で追加されなかった場合は、すでにプレイリストにある同じ曲。
    /// 曲が 1 つもなければ null。
    /// </param>
    private IReadOnlyList<PlaylistItem> AddPaths(IReadOnlyList<string> paths, int? index, out PlaylistItem? first)
    {
        IReadOnlyList<PlaylistItem> added = [];
        PlaylistItem? firstItem = null;
        Run(() =>
        {
            bool startPlaying = playlist.Count == 0 && playback.CurrentPath is null;
            IReadOnlyList<string> expanded = expander.Expand(paths);
            IReadOnlyList<string> wanted = settings.ExcludeDuplicates
                ? PlaylistDuplicates.Exclude(expanded, playlist.Items)
                : expanded;
            added = playlist.Insert(index ?? playlist.Count, wanted);
            if (expanded.Count > 0)
            {
                firstItem = added.FirstOrDefault(item => SamePath(item.Path, expanded[0]))
                    ?? playlist.Items.FirstOrDefault(item => SamePath(item.Path, expanded[0]));
            }
            if (startPlaying && added.Count > 0)
            {
                player.Play(added[0]);
            }
            if (wanted.Count < expanded.Count)
            {
                NoticeText = $"重複する {expanded.Count - wanted.Count} 曲は追加しませんでした。";
            }
        }, "追加できません");
        first = firstItem;
        return added;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>曲を再生する。その前に出ていた通知（重複を追加しなかった、など）は、再生を始められたら出したままにする。</summary>
    private void PlayItemKeepingNotice(PlaylistItem item)
    {
        string? notice = NoticeText;
        PlayItem(item);
        if (ErrorText is null)
        {
            NoticeText = notice;
        }
    }

    /// <summary>起動時に引数で渡されたファイルを開く。プレイリストを置き換えて再生する（SPEC 4.2・5 章）。</summary>
    /// <param name="startTimestamp">このプロセスが起動した時刻（<see cref="TimeProvider.GetTimestamp"/> の値）。</param>
    public void OpenStartupPaths(IReadOnlyList<string> paths, long startTimestamp)
    {
        // 複数のファイルを選んで開かれた場合、残りのファイルは別のプロセスから続けて届く。その起点として記録する。
        openPlanner.RegisterStartup(startTimestamp);
        OpenPaths(paths);
    }

    /// <summary>
    /// 再生の操作の部分（プレイリストの外）にドロップされたファイルを開く。動作は「他から起動したときの動作」の設定に従う
    /// （プレイリストの中へのドロップは、落とした位置への追加。「ファイル」メニューの「開く」は、常に置き換えて再生）。
    /// </summary>
    public void OpenDroppedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        Open(paths, settings.ExternalOpenAction switch
        {
            ExternalOpenAction.Append => ExternalOpenOperation.Add,
            ExternalOpenAction.AppendAndPlay => ExternalOpenOperation.AddAndPlay,
            _ => ExternalOpenOperation.Replace,
        });
    }

    /// <summary>
    /// 後から起動されたプロセスが渡してきたファイルを開く。動作は設定に従う（SPEC 5 章）。
    /// 直前の要求から間を置かずに届いたものは、同じ操作の続きとして追加する。
    /// </summary>
    /// <param name="arrivedTimestamp">要求が届いた時刻（<see cref="TimeProvider.GetTimestamp"/> の値）。</param>
    public void OpenFromOtherInstance(IReadOnlyList<string> paths, long arrivedTimestamp)
    {
        if (paths.Count == 0)
        {
            return;
        }

        Open(paths, openPlanner.Plan(arrivedTimestamp, settings.ExternalOpenAction));
    }

    private void Open(IReadOnlyList<string> paths, ExternalOpenOperation operation)
    {
        switch (operation)
        {
            case ExternalOpenOperation.Replace:
                OpenPaths(paths);
                break;
            case ExternalOpenOperation.Add:
                AddPaths(paths);
                break;
            case ExternalOpenOperation.AddAndPlay:
                // 空のプレイリストに追加した場合は AddPaths が再生を始めているので、重ねて再生し直さない。
                // 重複の除外で追加されなかった場合も、すでにある同じ曲を再生する（開いた曲が鳴らないままにしない）。
                AddPaths(paths, null, out PlaylistItem? first);
                if (first is not null && !(ReferenceEquals(player.Current, first) && isPlaying))
                {
                    PlayItemKeepingNotice(first);
                }
                break;
            case ExternalOpenOperation.AddAndPlayIfIdle:
                AddPaths(paths, null, out PlaylistItem? added);
                if (added is not null && !isPlaying)
                {
                    PlayItemKeepingNotice(added);
                }
                break;
        }
    }

    /// <summary>
    /// 出力デバイスの選択肢。先頭は「Windows の既定のデバイスに従う」。
    /// 選んであるデバイスが今つながっていない場合も、選択中であることが分かるように載せる。
    /// </summary>
    public IReadOnlyList<OutputDeviceChoice> GetOutputDevices()
    {
        string? preferred = deviceSelection.PreferredDeviceId;
        IReadOnlyList<AudioDeviceInfo> devices = deviceService.GetDevices();

        var choices = new List<OutputDeviceChoice> { new(null, "既定のデバイス", preferred is null) };
        choices.AddRange(devices.Select(device => new OutputDeviceChoice(device.Id, device.Name, device.Id == preferred)));
        if (preferred is not null && devices.All(device => device.Id != preferred))
        {
            choices.Add(new OutputDeviceChoice(preferred, "選択中のデバイス（接続されていません）", true));
        }
        return choices;
    }

    /// <summary>出力デバイスを選ぶ。再生中なら、位置を保ったままそのデバイスに切り替わる。</summary>
    /// <param name="deviceId">デバイスの ID。null は Windows の既定のデバイスに従う。</param>
    public void SelectOutputDevice(string? deviceId)
    {
        Run(() =>
        {
            settings.OutputDeviceId = deviceId;
            deviceSelection.PreferredDeviceId = deviceId;
        }, "出力デバイスを切り替えられません");
    }

    /// <summary>このアプリを、曲とプレイリストのファイルを開くアプリの候補として Windows に登録する。</summary>
    /// <returns>結果を知らせる文。設定画面に出す（メインウィンドウには出さない）。</returns>
    public string RegisterFileAssociation()
    {
        try
        {
            fileAssociation.Register();
            FileAssociationRegistrar.NotifyShell();
            return "関連付けを登録しました。既定のアプリにするには、Windows の設定の「既定のアプリ」でこのアプリを選んでください。";
        }
        catch (Exception ex)
        {
            return $"関連付けを登録できません: {ex.Message}";
        }
    }

    /// <returns>結果を知らせる文。</returns>
    public string UnregisterFileAssociation()
    {
        try
        {
            fileAssociation.Unregister();
            FileAssociationRegistrar.NotifyShell();
            return "関連付けを解除しました。";
        }
        catch (Exception ex)
        {
            return $"関連付けを解除できません: {ex.Message}";
        }
    }

    /// <summary>再生中にする、または一時停止にする。既にその状態なら何もしない（メディアキーの「再生」「一時停止」用）。</summary>
    public void SetPlaying(bool playing)
    {
        if (!playing && playback.IsWaitingForDevice)
        {
            // デバイス待ちの間は既に一時停止の状態だが、「一時停止」を押されたら、デバイスが戻っても勝手に再開しないようにする。
            Run(playback.Pause, null);
            return;
        }

        if (playing != isPlaying)
        {
            TogglePlayPause();
        }
    }

    public int IndexOf(PlaylistItem item) => playlist.IndexOf(item);

    public void ShowError(string message) => ErrorText = message;

    public void PlayItem(PlaylistItem item) => Run(() => player.Play(item), null);

    /// <summary>今の曲が終わったら、指定した曲から続ける。</summary>
    public void PlayItemNext(PlaylistItem item) => Run(() => player.PlayAfterCurrent(item), null);

    public void TogglePlayPause() => Run(player.PlayPause, "再生を開始できません");

    public void Stop() => Run(player.Stop, "停止できません");

    public void Next() => Run(player.Next, null);

    /// <summary>今の位置から指定した時間だけ進める（負なら戻す）。</summary>
    public void SeekBy(TimeSpan offset)
    {
        // 長さが分からない曲では、進める先を決められない。
        if (hasTrack && durationSeconds > 0)
        {
            PositionSeconds = Math.Clamp(playback.Position.TotalSeconds + offset.TotalSeconds, 0, durationSeconds);
        }
    }

    /// <summary>音量を指定した分（パーセント）だけ変える。</summary>
    public void ChangeVolume(double deltaPercent) => VolumePercent = Math.Clamp(volumePercent + deltaPercent, 0, 100);

    public void Previous() => Run(player.Previous, null);

    public void Remove(IReadOnlyCollection<PlaylistItem> items) => playlist.Remove(items);

    /// <summary>プレイリストを空にする。再生中の曲は、終わるまでそのまま鳴らす。</summary>
    public void Clear() => playlist.Clear();

    public void Move(IReadOnlyCollection<PlaylistItem> items, int insertIndex) => playlist.Move(items, insertIndex);

    /// <summary>曲をまとめて、指定した先へ動かす（「再生中の曲の次へ」の基準は、今の曲）。</summary>
    /// <returns>並びが変わったか。</returns>
    public bool Move(IReadOnlyCollection<PlaylistItem> items, PlaylistMove move) => playlist.Move(items, move, player.Current);

    public bool CanMove(IReadOnlyCollection<PlaylistItem> items, PlaylistMove move) => playlist.CanMove(items, move, player.Current);

    public void Undo() => playlist.Undo();

    public void Redo() => playlist.Redo();

    public void Sort(PlaylistSortKey key) => playlist.Sort(key);

    /// <summary>プレイリストの並びを無作為に入れ替える。再生中の曲は先頭に置く。</summary>
    public void Shuffle() => playlist.Shuffle(Random.Shared, player.Current);

    /// <summary>今の並びの順に、曲順の番号を振り直す。</summary>
    public void Renumber() => playlist.Renumber();

    /// <summary>読み込み元のプレイリストファイルへ書き戻す。</summary>
    /// <returns>
    /// 書き戻し先がない（まだ保存していない、または読み込み元が書き込みに対応しない形式）場合は false。
    /// 呼び出し側は名前を付けて保存する。
    /// </returns>
    public bool SavePlaylist()
    {
        if (playlist.FilePath is not { } path || !path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SavePlaylistAs(path);
        return true;
    }

    public void SavePlaylistAs(string path)
    {
        Run(() =>
        {
            PlaylistFile.Write(path, playlist.Items, settings.SaveRelativePaths);
            playlist.FilePath = Path.GetFullPath(path);
            UpdateStatus();
        }, "保存できません");
    }

    /// <summary>前回終了時の設定を反映する。</summary>
    /// <param name="restorePlaylist">作業中プレイリストと、選ばれていた曲も復元するか。</param>
    public void RestoreSession(bool restorePlaylist)
    {
        deviceSelection.PreferredDeviceId = settings.OutputDeviceId;
        deviceSelection.PreferExclusive = settings.ExclusiveMode;
        settings.BufferMilliseconds = OutputDeviceSelection.NormalizeBuffer(settings.BufferMilliseconds);
        settings.MaxOutputBitDepth = OutputDeviceSelection.NormalizeBitDepth(settings.MaxOutputBitDepth);
        deviceSelection.BufferMilliseconds = settings.BufferMilliseconds;
        deviceSelection.MaxBitDepth = settings.MaxOutputBitDepth;
        if (!Enum.IsDefined(settings.ReplayGain))
        {
            settings.ReplayGain = ReplayGainMode.Off;
        }
        playback.ReplayGainMode = settings.ReplayGain;
        VolumePercent = Math.Clamp(settings.Volume, 0, 1) * 100;
        ModeIndex = (int)settings.PlayMode;

        if (restorePlaylist)
        {
            playlist.Load(store.LoadWorkingPlaylist(), settings.PlaylistFilePath);
            if (settings.CurrentIndex >= 0 && settings.CurrentIndex < playlist.Count)
            {
                player.Select(playlist.Items[settings.CurrentIndex]);
            }
        }
        UpdateStatus();

        // 復元しただけの内容を書き直す必要はない。
        autoSaveTimer.Stop();
    }

    /// <summary>設定と作業中プレイリストを保存する。終了時と、編集が落ち着いたときに呼ばれる。</summary>
    public void SaveSession()
    {
        autoSaveTimer.Stop();
        // ミュートの間は出力の音量が 0 になっている。保存するのは音量の設定のほう。
        settings.Volume = Math.Clamp(volumePercent / 100, 0, 1);
        settings.PlayMode = player.Mode;
        settings.PlaylistFilePath = playlist.FilePath;
        settings.CurrentIndex = player.Current is { } current ? playlist.IndexOf(current) : -1;

        try
        {
            store.SaveWorkingPlaylist(playlist.Items);
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 実行ファイルの場所に書き込めない（読み取り専用の場所に置かれている）。再生は続けられるので知らせるだけにする。
            ErrorText = $"設定を保存できません: {ex.Message}";
        }
    }

    public void Dispose()
    {
        positionTimer.Stop();
        autoSaveTimer.Stop();
        statusTimer.Stop();
        infoLoader.InfoApplied -= OnInfoApplied;
        WatchTitle(null);
        artworkLoader.Loaded -= OnArtworkLoaded;
        artworkLoader.Cancel();
        Bitmap? shown = artwork;
        Artwork = null;
        shown?.Dispose();
        playlist.Changed -= OnPlaylistChanged;
        playback.StateChanged -= OnStateChanged;
        playback.OutputChanged -= OnOutputChanged;
        playback.PlaybackFailed -= OnPlaybackFailed;
        player.CurrentChanged -= OnCurrentChanged;
        player.TrackFailed -= OnTrackFailed;
    }

    private void Run(Action action, string? errorPrefix)
    {
        ErrorText = null;
        NoticeText = null;

        // 利用者が操作したので、デバイス待ちの表示はここで消えた。後で別のエラー表示を消してしまわないよう、印も下ろす。
        deviceErrorShown = false;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorText = errorPrefix is null ? ex.Message : $"{errorPrefix}: {ex.Message}";
            SyncFromPlayback();
        }

        // 操作の後もデバイス待ちが続いている（デバイスや排他モードを切り替えても、まだ出力を作れない）。
        // 待ちが続いているのに表示だけ消えた状態にしない。
        if (playback.IsWaitingForDevice && ErrorText is null)
        {
            deviceErrorShown = true;
            ErrorText = "出力デバイスを使えません。使えるようになると再生を再開します。";
        }
    }

    private void OnPlaylistChanged(object? sender, EventArgs e)
    {
        infoLoader.Enqueue(playlist.Items);
        HasItems = playlist.Count > 0;
        CanUndo = playlist.CanUndo;
        CanRedo = playlist.CanRedo;
        UpdateStatus();

        autoSaveTimer.Stop();
        autoSaveTimer.Start();
    }

    private void OnInfoApplied(object? sender, EventArgs e) => statusTimer.IsEnabled = true;

    private void OnStateChanged(object? sender, EventArgs e) => SyncFromPlayback();

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        WatchTitle(player.Current);
        LoadArtwork();
        SyncFromPlayback();
        OnPropertyChanged(nameof(CurrentItem));
        CurrentItemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackFailed(object? sender, PlaybackFailedEventArgs e)
    {
        deviceErrorShown = playback.IsWaitingForDevice;
        ErrorText = deviceErrorShown
            ? $"出力デバイスが使えなくなりました。使えるようになると再生を再開します: {e.Exception.Message}"
            : $"再生が中断されました: {e.Exception.Message}";
    }

    private void OnOutputChanged(object? sender, EventArgs e)
    {
        // デバイスが使えるようになって出力を作り直せたら、「使えなくなりました」の表示を消す。
        if (deviceErrorShown && playback.OutputDeviceId is not null)
        {
            deviceErrorShown = false;
            ErrorText = null;
        }
        SyncFromPlayback();
        OutputStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrackFailed(object? sender, TrackFailedEventArgs e) =>
        ErrorText = $"{e.Item.FileName}: {e.Exception.Message}";

    private void OnArtworkLoaded(Bitmap? image)
    {
        Bitmap? previous = artwork;
        Artwork = image;
        previous?.Dispose();
    }

    /// <summary>タグは後から読み込まれるので、今の曲のタイトルの変化を追う。</summary>
    private void WatchTitle(PlaylistItem? item)
    {
        if (titleSource is not null)
        {
            titleSource.PropertyChanged -= OnTitleSourceChanged;
        }
        titleSource = item;
        if (titleSource is not null)
        {
            titleSource.PropertyChanged += OnTitleSourceChanged;
        }
    }

    private void OnTitleSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaylistItem.DisplayTitle)
            or nameof(PlaylistItem.Artist)
            or nameof(PlaylistItem.Album)
            or nameof(PlaylistItem.AlbumArtist))
        {
            TrackTitle = Describe(titleSource);
            AlbumText = DescribeAlbum(titleSource);

            // 同じ曲のまま中身（タグ）が変わった。タイトルの文字列が変わらない場合でも、曲の情報を表示している側が更新できるようにする。
            OnPropertyChanged(nameof(CurrentItem));
        }
    }

    private void SyncFromPlayback()
    {
        // 曲の長さが変わるとシークバーが値を範囲内に丸めて書き戻してくる。それをシーク操作と取り違えないようにする。
        syncingFromPlayback = true;
        try
        {
            PositionSeconds = playback.Position.TotalSeconds;
            DurationSeconds = playback.Duration.TotalSeconds;
            TrackTitle = Describe(player.Current);
            AlbumText = DescribeAlbum(player.Current);
            UpdateOutputText();
            HasTrack = playback.CurrentPath is not null;
            IsPlaying = playback.State == PlayerState.Playing;
            State = playback.State;
        }
        finally
        {
            syncingFromPlayback = false;
        }

        positionTimer.IsEnabled = playback.State == PlayerState.Playing;
    }

    private void SyncPosition()
    {
        syncingFromPlayback = true;
        try
        {
            PositionSeconds = playback.Position.TotalSeconds;
        }
        finally
        {
            syncingFromPlayback = false;
        }
    }

    private void UpdateStatus()
    {
        string status = $"{playlist.Count} 曲";

        // タグを読んでいる途中は、読めた分までの合計になる（読み進むにつれて増える）。
        TimeSpan total = playlist.GetTotalDuration();
        if (total > TimeSpan.Zero)
        {
            status += $"  ·  {PlaylistItem.FormatDuration(total)}";
        }
        StatusText = playlist.FilePath is { } path ? $"{status}  ·  {Path.GetFileName(path)}" : status;
    }

    private static string Describe(PlaylistItem? item) => item switch
    {
        null => EmptyTitle,
        { Artist: { Length: > 0 } artist } => $"{item.DisplayTitle} - {artist}",
        _ => item.DisplayTitle,
    };

    private static string? DescribeAlbum(PlaylistItem? item) => (item?.Album, item?.AlbumArtist) switch
    {
        ({ Length: > 0 } album, { Length: > 0 } artist) => $"{album} - {artist}",
        ({ Length: > 0 } album, _) => album,
        (_, { Length: > 0 } artist) => artist,
        _ => null,
    };

    /// <summary>
    /// 曲の形式と、ビットパーフェクト再生の印を表示に反映する。処理経路・出力の形式・デバイスは画面には出さず、
    /// 「出力」メニューで確かめられるようにしている（<see cref="DescribeOutput"/>）。
    /// </summary>
    private void UpdateOutputText()
    {
        bool replayGain = settings.ReplayGain != ReplayGainMode.Off;
        (Badge, BadgeToolTip) = OutputStatus.Badge(
            settings.ExclusiveMode,
            playback.OutputInfo,
            playback.IsBitPerfect,
            playback.Volume >= 1f,
            replayGain);

        if (playback.SourceFormat is not { } format)
        {
            FormatText = "";
            return;
        }

        string depth = format.Encoding == WaveFormatEncoding.IeeeFloat
            ? $"{format.BitsPerSample} bit float"
            : $"{format.BitsPerSample} bit";
        string text = $"{format.SampleRate} Hz / {depth} / {format.Channels} ch";
        if (replayGain)
        {
            text += playback.ReplayGainDb is { } gain
                ? string.Create(CultureInfo.InvariantCulture, $"  ·  ReplayGain {gain:+0.0;-0.0;0.0} dB")
                : "  ·  ReplayGain タグなし";
        }
        FormatText = text;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
