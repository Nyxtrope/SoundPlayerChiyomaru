using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundPlayerChiyomaru.Core.Instance;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Settings;

namespace SoundPlayerChiyomaru;

public partial class MainWindow : Window
{
    /// <summary>アートワークを表示する大きさ（拡大率を掛ける前）。XAML の枠の大きさと合わせること。</summary>
    private const int ArtworkSize = 160;

    /// <summary>再生モードのアイコンの名前。並びは <see cref="PlayMode"/> の値の順。</summary>
    private static readonly string[] ModeIconKeys =
        ["ModeNormalIcon", "ModeRepeatOneIcon", "ModeRepeatAllIcon", "ModeRandomIcon", "ModeOneShotIcon"];

    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);
    private const double VolumeStep = 5;

    /// <summary>押したままこの距離（ピクセル）を超えて動かしたら、クリックではなくドラッグとみなす。</summary>
    private const double DragThreshold = 6;

    /// <summary>一覧の行をこの数以上作り直したら、スクロールが止まった後にヒープを詰める（少し動かしただけでは詰めない）。</summary>
    private const int CompactAfterPreparedRows = 200;

    /// <summary>行の作り直しが止まってから、ヒープを詰めるまでの時間。</summary>
    private static readonly TimeSpan CompactDelay = TimeSpan.FromSeconds(1.5);

    private static readonly DataFormat<string> ReorderFormat =
        DataFormat.CreateInProcessFormat<string>("SoundPlayerChiyomaru.PlaylistReorder");

    private readonly MainWindowViewModel viewModel;
    private readonly StartupOptions options;
    private readonly StartupBench? bench;
    private readonly SingleInstance? instance;
    private SystemMediaControls? mediaControls;
    private bool closed;
    private bool ready;
    private readonly List<(IReadOnlyList<string> Paths, long Arrived)> pendingRequests = [];
    private WindowState stateBeforeMinimize = WindowState.Normal;
    private bool miniApplied;
    private SettingsWindow? settingsWindow;

    /// <summary>ミニモードから戻すときに、画面に収めるためにウィンドウを動かした場合の、動かす前と後の位置。</summary>
    private (PixelPoint From, PixelPoint To)? movedForExpand;
    private double normalHeight;
    private double normalMinHeight;

    /// <summary>
    /// 最大化も最小化もしていないときの位置と大きさ。終了時に保存する。最大化したまま終了しても元の大きさを保存できるよう、
    /// 変わるたびに覚えておく。ミニモードの間は、高さだけ通常の表示のときのものを残す。
    /// </summary>
    private WindowBounds? restoreBounds;
    private bool fitAfterOpen;

    /// <summary>押されたままになっているキー。押しっぱなしによる繰り返しを見分けるために使う。</summary>
    private readonly HashSet<Key> heldKeys = [];

    /// <summary>Alt を押している間に、Alt+↑ / Alt+↓ で曲を動かしたか（Alt を離したときにメニューを選ばせないために覚えておく）。</summary>
    private bool movedWithAlt;

    private Point? pressPoint;
    private PointerPressedEventArgs? pressEvent;
    private PlaylistItem? pressedItem;
    private bool selectOnRelease;
    private IReadOnlyList<PlaylistItem>? reorderItems;

    private int preparedSinceCompact;
    private DispatcherTimer? compactTimer;

    /// <summary>右クリックで押された曲（曲のない場所なら null）と、右クリックされたかどうか。メニューを閉じたら消す。</summary>
    private PlaylistItem? menuItem;
    private bool menuOpenedByPointer;

    /// <param name="instance">このプロセスが最初のインスタンスである印。他のプロセスからのファイルを受け取るために使う。</param>
    internal MainWindow(MainWindowViewModel viewModel, StartupOptions options, SingleInstance? instance)
    {
        this.viewModel = viewModel;
        this.options = options;
        this.instance = instance;
        bench = options.BenchOutputPath is null ? null : new StartupBench(options.BenchOutputPath);

        InitializeComponent();
        DataContext = viewModel;
        RestoreBounds();
        PositionChanged += (_, _) => TrackBounds();
        Resized += (_, _) => TrackBounds();

        // 部品の境目を越えた直後は DragOver ではなく DragEnter だけが届くので、同じ処理で受ける。
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 行の選択処理より先に受け取る必要があるので、トンネルで受ける。
        PlaylistBox.AddHandler(PointerPressedEvent, OnPlaylistPointerPressed, RoutingStrategies.Tunnel);
        PlaylistBox.AddHandler(PointerMovedEvent, OnPlaylistPointerMoved, RoutingStrategies.Tunnel);
        PlaylistBox.AddHandler(PointerReleasedEvent, OnPlaylistPointerReleased, RoutingStrategies.Tunnel);
        PlaylistBox.ContainerPrepared += OnRowPrepared;

        // スライダーより先に受け取る必要があるので、トンネルで受ける。
        SeekBar.AddHandler(PointerPressedEvent, OnSliderPointerPressed, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, RoutingStrategies.Tunnel);

        viewModel.CurrentItemChanged += OnCurrentItemChanged;
        viewModel.ColumnsChanged += OnColumnsChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyColumns(ColumnHeader);
        UpdateModeIcon();

        if (viewModel.AlwaysOnTop)
        {
            Topmost = true;
        }

        // 一覧にフォーカスがあっても効くよう、空白キーなどは一覧より先に受け取る。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Win32Properties.AddWndProcHookCallback(this, OnWindowMessage);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        // メニューを閉じたら、フォーカスを一覧へ戻す。メニューの見出しに残ったままだと、矢印キーが見出しの移動に使われて、
        // シークなどのキーが効かない。メニューが自分でフォーカスを動かした後に行うため、後回しにする。
        MainMenu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!MainMenu.IsOpen && MainMenu.IsKeyboardFocusWithin && PlaylistBox.IsVisible)
            {
                FocusPlaylist();
            }
        });

        // メニューのキーの表示は、キーの名前（OemComma）がそのまま出る。他の項目と同じ見た目のまま、文字だけを差し替える。
        SettingsMenuItem.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } gesture)
            {
                gesture.Text = "Ctrl+,";
            }
        };

        // 離したことを受け取れないまま別のウィンドウへ移った場合に、押されたままの扱いを残さない。
        Deactivated += (_, _) =>
        {
            heldKeys.Clear();
            movedWithAlt = false;
        };

        // 別の拡大率のモニターへ移したら、アートワークをその大きさで読み直す。シークバーの実際のピクセル数も変わる。
        ScalingChanged += (_, _) =>
        {
            viewModel.ArtworkPixelSize = (int)Math.Ceiling(ArtworkSize * RenderScaling);
            UpdateSeekBarPixels();
        };

        // 受信は早く始める。後から起動されたプロセスは、こちらが受信を始めるまで待たされるため。
        // 届いた時刻は受信用のスレッドで記録し（処理が遅れても「続けて届いた」の判定がずれないように）、処理は UI スレッドへ渡す。
        instance?.StartListening(paths =>
        {
            long arrived = TimeProvider.System.GetTimestamp();
            Dispatcher.UIThread.Post(() => OnPathsReceived(paths, arrived));
        });
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        bench?.MarkShown();
        ScrollToCurrent();
        viewModel.ArtworkPixelSize = (int)Math.Ceiling(ArtworkSize * RenderScaling);
        TrackBounds();
        if (viewModel.MiniMode)
        {
            ApplyMiniMode();
        }
        else if (fitAfterOpen)
        {
            MoveOntoScreen();
        }

        if (options.FilePaths.Count > 0)
        {
            viewModel.OpenStartupPaths(options.FilePaths, Program.StartTimestamp);
            if (viewModel.IsPlaying)
            {
                bench?.MarkPlayCalled();
            }
        }

        // 起動時のファイルを開く前に届いていた要求を、届いた順に処理する。
        ready = true;
        foreach ((IReadOnlyList<string> paths, long arrived) in pendingRequests)
        {
            OnPathsReceived(paths, arrived);
        }
        pendingRequests.Clear();

        // 最初の描画を遅らせないよう、メディアキーの連携は表示が済んでから始める。
        Dispatcher.UIThread.Post(StartMediaControls, DispatcherPriority.Background);

        if (bench is not null)
        {
            DispatcherTimer.RunOnce(
                () =>
                {
                    string output = string.Join("  ·  ", [viewModel.FormatText, .. viewModel.DescribeOutput()]);
                    bench.Write(viewModel.PlaybackPosition, viewModel.ErrorText, output, viewModel.OutputNotice, viewModel.IsBadgeVisible ? viewModel.BadgeText : "", viewModel.HasArtwork);
                    Close();
                },
                TimeSpan.FromSeconds(options.BenchSeconds));
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && restoreBounds is { } bounds)
        {
            WindowState state = WindowState == WindowState.Minimized ? stateBeforeMinimize : WindowState;
            viewModel.WindowBounds = bounds with { Maximized = state == WindowState.Maximized };
        }
    }

    /// <summary>前回終了時の位置と大きさに戻す。表示する前に呼ぶこと。</summary>
    private void RestoreBounds()
    {
        if (viewModel.WindowBounds is not { } saved)
        {
            return;
        }

        // 保存した後でモニターを外したり解像度を変えたりしていると、そのままでは画面の外に出る。
        Avalonia.Platform.Screen? primary = Screens.Primary;
        var screens = new List<ScreenArea>();
        foreach (Avalonia.Platform.Screen screen in Screens.All)
        {
            PixelRect area = screen.WorkingArea;
            var item = new ScreenArea(area.X, area.Y, area.Width, area.Height, screen.Scaling);
            if (ReferenceEquals(screen, primary))
            {
                screens.Insert(0, item);
            }
            else
            {
                screens.Add(item);
            }
        }
        if (WindowPlacement.Fit(saved, screens) is not { } bounds)
        {
            return;
        }

        Width = Math.Max(MinWidth, bounds.Width);
        Height = Math.Max(MinHeight, bounds.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(bounds.X, bounds.Y);

        // 位置を直したときは、中身の大きさで見積もっている。枠を含めた大きさが分かる表示後に、もう一度収める。
        fitAfterOpen = (bounds.X, bounds.Y) != (saved.X, saved.Y);
        restoreBounds = bounds with { Width = Width, Height = Height };

        // ミニモードは最大化できない。ミニモードで起動する場合は、通常の大きさのまま始める。
        if (bounds.Maximized && !viewModel.MiniMode)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void TrackBounds()
    {
        // 最大化・最小化の途中の通知は、WindowState が変わる前に届くことがある。Windows に直接たずねて見分ける。
        if (!IsVisible
            || TryGetPlatformHandle()?.Handle is not { } handle
            || handle == IntPtr.Zero
            || IsZoomed(handle)
            || IsIconic(handle))
        {
            return;
        }

        Size size = ClientSize;
        double height = miniApplied && restoreBounds is { } known ? known.Height : size.Height;
        restoreBounds = new WindowBounds(Position.X, Position.Y, size.Width, height, Maximized: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        // これ以降に届く要求は処理できない。受け取ってから捨てることにならないよう、まず受信を止める
        // （送った側は失敗を検知して、自分が次のインスタンスになる）。
        instance?.StopListening();

        viewModel.CurrentItemChanged -= OnCurrentItemChanged;
        viewModel.ColumnsChanged -= OnColumnsChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        mediaControls?.Dispose();
        mediaControls = null;
        compactTimer?.Stop();
        closed = true;
        base.OnClosed(e);
    }

    private void StartMediaControls()
    {
        if (!closed && TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero)
        {
            mediaControls = SystemMediaControls.TryCreate(handle, viewModel);
        }
    }

    /// <summary>後から起動されたプロセスがファイルを渡してきた。ファイルなしで起動された場合は、前面に出すだけ。</summary>
    private void OnPathsReceived(IReadOnlyList<string> paths, long arrived)
    {
        if (closed)
        {
            return;
        }
        if (!ready)
        {
            // 起動時のファイルをまだ開いていない。先に処理すると、その内容を起動時のファイルが置き換えて消してしまう。
            pendingRequests.Add((paths, arrived));
            return;
        }

        viewModel.OpenFromOtherInstance(paths, arrived);
        if (WindowState == WindowState.Minimized)
        {
            WindowState = stateBeforeMinimize;
        }
        Activate();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 最小化から戻すときに、最大化していたならその状態へ戻せるよう、最小化する前の状態を覚えておく。
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() != WindowState.Minimized)
        {
            stateBeforeMinimize = change.GetNewValue<WindowState>();
        }
    }

    private void OnOutputMenuOpened(object? sender, RoutedEventArgs e)
    {
        // 下の階層のメニューが開いたときの通知もここへ上がってくる。出力メニュー自身が開いたときだけ作り直す。
        if (!ReferenceEquals(e.Source, OutputMenu))
        {
            return;
        }

        OutputMenu.Items.Clear();

        // 処理経路・出力の形式・デバイスは画面に出していないので、ここで確かめられるようにする。
        IReadOnlyList<string> status = viewModel.DescribeOutput();
        foreach (string line in status.Count > 0 ? status : ["出力していません"])
        {
            OutputMenu.Items.Add(new MenuItem { Header = line.Replace("_", "__"), IsEnabled = false });
        }
        if (viewModel.OutputNotice is { } notice)
        {
            OutputMenu.Items.Add(new MenuItem { Header = notice.Replace("_", "__"), IsEnabled = false });
        }
        OutputMenu.Items.Add(new Separator());

        // 排他モードの間は他のアプリの音が鳴らなくなるので、選ぶ場所に注記しておく（SPEC 3.2）。
        var exclusive = new MenuItem
        {
            Header = "排他モード（他のアプリの音は鳴らなくなります）",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = viewModel.ExclusiveMode,
        };
        exclusive.Click += (_, _) => viewModel.ExclusiveMode = !viewModel.ExclusiveMode;
        OutputMenu.Items.Add(exclusive);

        var replayGain = new MenuItem { Header = "ReplayGain" };
        foreach ((string header, ReplayGainMode mode) in new[]
        {
            ("オフ", ReplayGainMode.Off),
            ("トラック（曲ごとに揃える）", ReplayGainMode.Track),
            ("アルバム（アルバムの中の音量差を保つ）", ReplayGainMode.Album),
        })
        {
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = viewModel.ReplayGainMode == mode,
            };
            item.Click += (_, _) => viewModel.ReplayGainMode = mode;
            replayGain.Items.Add(item);
        }
        OutputMenu.Items.Add(replayGain);
        OutputMenu.Items.Add(new Separator());

        foreach (OutputDeviceChoice choice in viewModel.GetOutputDevices())
        {
            var item = new MenuItem
            {
                Header = choice.Name.Replace("_", "__"),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = choice.IsSelected,
            };
            item.Click += (_, _) => viewModel.SelectOutputDevice(choice.Id);
            OutputMenu.Items.Add(item);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.MiniMode))
        {
            ApplyMiniMode();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.ModeIndex))
        {
            UpdateModeIcon();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.AlwaysOnTop))
        {
            Topmost = viewModel.AlwaysOnTop;
        }
    }

    /// <summary>
    /// ミニモードでは、メニュー・プレイリスト・状態の行を隠し（XAML 側で切り替える）、ウィンドウの高さを残った中身に合わせる。
    /// 通常の表示に戻すときは、元の高さに戻す。
    /// </summary>
    private void ApplyMiniMode()
    {
        if (viewModel.MiniMode == miniApplied)
        {
            return;
        }
        bool entering = viewModel.MiniMode;
        if (entering)
        {
            normalMinHeight = MinHeight;
        }

        // 最大化したままでは高さを変えられない。
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        miniApplied = entering;
        if (miniApplied)
        {
            normalHeight = Height;

            // タイトルバーと枠のボタンをなくす（細い枠だけを残す）。動かすのはヘッダーをつかんで、戻す・閉じるのは右クリックのメニューで行う。
            // 枠だけにしても、上辺にはシステムが描く帯が残る。中身をその領域まで広げて、配色と違う色の帯が見えないようにする。
            WindowDecorations = WindowDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            CanMaximize = false;

            // 広げた分だけ中身が上に寄るので、上の余白を足して左右とそろえる。
            HeaderPanel.Padding = new Thickness(0, 6, 0, 0);

            // 高さは常に中身に合わせる。曲が変わって 2 行目が出たり消えたりしたときも追従する。
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;

            // 前に通常の表示へ戻したとき、画面に収めるために動かしていたら、ミニモードで置いてあった位置へ戻す
            // （その後で利用者がウィンドウを動かしていなければ）。
            if (movedForExpand is { } moved && Position == moved.To)
            {
                Position = moved.From;
            }
            movedForExpand = null;
        }
        else
        {
            // ミニモードの間に利用者が高さを変えると、高さを中身に合わせる設定は自動で外れている。
            // どちらの場合も、元の高さと最小の高さに戻す。
            HeaderPanel.Padding = default;
            ExtendClientAreaToDecorationsHint = false;
            WindowDecorations = WindowDecorations.Full;
            CanMaximize = true;
            SizeToContent = SizeToContent.Manual;
            MinHeight = normalMinHeight;
            Height = normalHeight;

            // ミニモードは画面の端に置かれることが多い。広げた結果が画面からはみ出さないよう、大きさが決まってから位置を直す。
            Dispatcher.UIThread.Post(KeepExpandedWindowOnScreen, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// ミニモードの間、枠をつかんで大きさを変えようとしても、高さは変えさせない（幅は変えられる）。
    /// Windows は、ドラッグの途中で「この大きさにしてよいか」を問い合わせてくる。そこで今の高さのままの大きさを返す。
    /// いったん変えさせてから戻す方式は操作中に画面が乱れ、最小・最大の高さで固定する方式は、
    /// 中身を枠の領域まで広げた状態では枠の分だけ高さがずれた。
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint Sizing = 0x0214;
        if (message != Sizing || !miniApplied || !GetWindowRect(window, out NativeRect current))
        {
            return IntPtr.Zero;
        }

        // lParam は、これからなろうとしている大きさ（左・上・右・下の順の 32 ビット整数）。
        // wParam は、つかまれている辺（3〜5 は上辺を含む）。動かされていない側を基準に、高さを今のままにする。
        const int TopOffset = 4;
        const int BottomOffset = 12;
        int height = current.Bottom - current.Top;
        if ((int)wParam is 3 or 4 or 5)
        {
            Marshal.WriteInt32(lParam, TopOffset, Marshal.ReadInt32(lParam, BottomOffset) - height);
        }
        else
        {
            Marshal.WriteInt32(lParam, BottomOffset, Marshal.ReadInt32(lParam, TopOffset) + height);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    /// <summary>通常の表示に戻したウィンドウが作業領域（タスクバーを除いた画面）からはみ出していたら、収まる位置まで動かす。</summary>
    private void KeepExpandedWindowOnScreen()
    {
        if (!closed && !miniApplied)
        {
            movedForExpand = MoveOntoScreen();
        }
    }

    /// <summary>ウィンドウが作業領域からはみ出していたら、収まる位置まで動かす。</summary>
    /// <returns>動かした場合は、動かす前と後の位置。</returns>
    private (PixelPoint From, PixelPoint To)? MoveOntoScreen()
    {
        if (WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen)
        {
            return null;
        }

        PixelRect area = screen.WorkingArea;
        Size size = FrameSize ?? ClientSize;
        var frame = PixelSize.FromSize(size, RenderScaling);
        PixelPoint from = Position;
        int x = Math.Max(area.X, Math.Min(from.X, area.Right - frame.Width));
        int y = Math.Max(area.Y, Math.Min(from.Y, area.Bottom - frame.Height));
        var to = new PixelPoint(x, y);
        if (to == from)
        {
            return null;
        }
        Position = to;
        return (from, to);
    }

    /// <summary>ミニモードではタイトルバーがないので、ヘッダーの何もないところをつかんでウィンドウを動かせるようにする。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // ボタンなどの上で押された場合は動かさない。押したことを自分で処理しない部品もあり、
        // ここで動かし始めると、その部品が「離した」を受け取れずに反応しなくなる。
        if (viewModel.MiniMode
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && e.Source is Visual source
            && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<Slider>(includeSelf: true) is null)
        {
            BeginMoveDrag(e);
        }
    }

    private void UpdateModeIcon()
    {
        int index = Math.Clamp(viewModel.ModeIndex, 0, ModeIconKeys.Length - 1);
        if (this.TryFindResource(ModeIconKeys[index], out object? icon) && icon is Geometry geometry)
        {
            ModeIcon.Data = geometry;
        }
    }

    /// <summary>再生モードの選択肢を、ボタンの下に出す。</summary>
    private void OnModeButtonClick(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        for (int i = 0; i < viewModel.ModeNames.Count; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = viewModel.ModeNames[i],
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = viewModel.ModeIndex == i,
            };
            item.Click += (_, _) => viewModel.ModeIndex = index;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(ModeButton);
    }

    /// <summary>「再生」メニューの再生モードを開いた。今のモードに印を付ける（ボタンから変えた場合もあるので、開くたびに合わせる）。</summary>
    private void OnModeMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || !ReferenceEquals(e.Source, menu))
        {
            return;
        }
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is string tag && int.Parse(tag, CultureInfo.InvariantCulture) == viewModel.ModeIndex;
        }
    }

    private void OnModeMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
        {
            viewModel.ModeIndex = int.Parse(tag, CultureInfo.InvariantCulture);
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>設定画面を開く。すでに開いていれば、それを手前に出す。</summary>
    private void ShowSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return;
        }

        settingsWindow = new SettingsWindow(viewModel);
        settingsWindow.Closed += (_, _) =>
        {
            settingsWindow = null;

            // 設定画面の部品は、開いている間に古い世代へ移るので、閉じても若い世代の GC では回収されず、
            // 開閉を繰り返すとその分ずつメモリが増える（15 秒ずつ 4 回で約 10MB。SPEC 8.3）。描画のキャッシュも残る。
            // 閉じた後に 1 回だけ、キャッシュを捨ててヒープを詰める。閉じた直後はウィンドウがまだ解放されていないので、少し待ってから行う。
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (settingsWindow is null && !closed)
                    {
                        SkiaSharp.SKGraphics.PurgeResourceCache();
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    }
                },
                TimeSpan.FromSeconds(1));
        };
        settingsWindow.Show(this);
    }

    private void OnSeekBarSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateSeekBarPixels();

    /// <summary>シークバーの上でホイールを回した。1 段で「再生」メニューの「5 秒進む / 戻る」と同じだけ動かす（下へ回すと進む）。</summary>
    private void OnSeekWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            // 下へ回すと Delta.Y は負になる。
            viewModel.SeekBy(-Math.Sign(e.Delta.Y) * SeekStep);
            e.Handled = true;
        }
    }

    /// <summary>
    /// スライダーは、左以外のボタンでもつまみを動かす（右ボタンでドラッグしてもシークする）。左ボタン以外の押下はスライダーに渡さない。
    /// 右ボタンを離したときのメニュー（ミニモードの右クリックのメニュー）は、押下とは別に届くので、このままでも開く。
    /// </summary>
    private static void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Visual slider && !e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// シークバーの幅を ViewModel に渡す。つまみが動ける幅はこれよりつまみの分だけ狭いが、広めに渡しておけば、
    /// 間引きすぎて動きが粗くなることはない（1 ピクセル未満の動きで更新することがあるだけ）。
    /// </summary>
    private void UpdateSeekBarPixels() => viewModel.SeekBarPixels = SeekBar.Bounds.Width * RenderScaling;

    private void OnMiniModeClick(object? sender, RoutedEventArgs e) => viewModel.MiniMode = !viewModel.MiniMode;

    private void OnAlwaysOnTopClick(object? sender, RoutedEventArgs e) => viewModel.AlwaysOnTop = !viewModel.AlwaysOnTop;

    private void OnSeekForwardClick(object? sender, RoutedEventArgs e) => viewModel.SeekBy(SeekStep);

    private void OnSeekBackClick(object? sender, RoutedEventArgs e) => viewModel.SeekBy(-SeekStep);

    private void OnVolumeUpClick(object? sender, RoutedEventArgs e) => viewModel.ChangeVolume(VolumeStep);

    private void OnMuteClick(object? sender, RoutedEventArgs e) => viewModel.IsMuted = !viewModel.IsMuted;

    /// <summary>音量のスライダーの上でホイールを回した。1 段で Ctrl+↑ / Ctrl+↓ と同じだけ変える。</summary>
    private void OnVolumeWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            viewModel.ChangeVolume(Math.Sign(e.Delta.Y) * VolumeStep);
            e.Handled = true;
        }
    }

    private void OnVolumeDownClick(object? sender, RoutedEventArgs e) => viewModel.ChangeVolume(-VolumeStep);

    /// <summary>
    /// 再生の操作のキー。一覧や再生モードの選択欄にフォーカスがあると、空白キーや矢印キーはそちらに使われてしまうので、先に受け取る。
    /// メニューや選択欄を開いている間は、それらの操作を優先する。
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Visual source)
        {
            if (!ReferenceEquals(TopLevel.GetTopLevel(source), this)
                || MainMenu.IsOpen
                || source.FindAncestorOfType<ComboBox>(includeSelf: true) is { IsDropDownOpen: true })
            {
                return;
            }

            // メニューを閉じた後も、フォーカスはメニューの見出しに残ることがある。その間も空白キーは再生の操作に使う。
            // 矢印キーなどは、Alt キーで見出しを選んで移動する操作のために、メニューへ渡す。
            // Ctrl を押しながらのキーは見出しの移動には使われないので、ここで受ける（渡すと、設定画面を開くキーなどが効かない）。
            if (e.Key != Key.Space
                && e.KeyModifiers != KeyModifiers.Control
                && source.FindAncestorOfType<MenuItem>(includeSelf: true) is not null)
            {
                return;
            }
        }

        // 押しっぱなしで繰り返してよいのは、シークと音量と、曲を 1 つ上・下へ動かす操作だけ。再生/一時停止や表示の切り替えが繰り返されると、
        // 押している間じゅう状態が行ったり来たりする。
        bool repeated = !heldKeys.Add(e.Key);
        bool repeatable = (e.Key, e.KeyModifiers)
            is (Key.Left or Key.Right, KeyModifiers.None)
            or (Key.Up or Key.Down, KeyModifiers.Control or KeyModifiers.Alt);
        if (repeated && !repeatable)
        {
            e.Handled = IsPlaybackKey(e.Key, e.KeyModifiers);
            return;
        }

        e.Handled = true;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Space, KeyModifiers.None):
                viewModel.TogglePlayPause();
                break;
            case (Key.Space, KeyModifiers.Control):
                viewModel.Stop();
                break;
            case (Key.Right, KeyModifiers.Control):
                viewModel.Next();
                break;
            case (Key.Left, KeyModifiers.Control):
                viewModel.Previous();
                break;
            case (Key.Right, KeyModifiers.None):
                viewModel.SeekBy(SeekStep);
                break;
            case (Key.Left, KeyModifiers.None):
                viewModel.SeekBy(-SeekStep);
                break;
            case (Key.Up, KeyModifiers.Control):
                viewModel.ChangeVolume(VolumeStep);
                break;
            case (Key.Down, KeyModifiers.Control):
                viewModel.ChangeVolume(-VolumeStep);
                break;
            // 一覧にフォーカスがあると、Alt を押していても矢印キーは選択の移動に使われてしまうので、ここで受ける。
            // ミニモードでは一覧が見えないので、動かさない（OnKeyDown の、一覧を編集するキーと同じ）。
            case (Key.Up, KeyModifiers.Alt) when !viewModel.MiniMode:
                movedWithAlt = true;
                MoveSelected(PlaylistMove.Up);
                break;
            case (Key.Down, KeyModifiers.Alt) when !viewModel.MiniMode:
                movedWithAlt = true;
                MoveSelected(PlaylistMove.Down);
                break;
            case (Key.M, KeyModifiers.None):
                viewModel.IsMuted = !viewModel.IsMuted;
                break;
            case (Key.M, KeyModifiers.Control):
                viewModel.MiniMode = !viewModel.MiniMode;
                break;
            case (Key.T, KeyModifiers.Control):
                viewModel.AlwaysOnTop = !viewModel.AlwaysOnTop;
                break;
            case (Key.OemComma, KeyModifiers.Control):
                ShowSettings();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private static bool IsPlaybackKey(Key key, KeyModifiers modifiers) => (key, modifiers) switch
    {
        (Key.Space, KeyModifiers.None or KeyModifiers.Control) => true,
        (Key.Left or Key.Right, KeyModifiers.None or KeyModifiers.Control) => true,
        (Key.Up or Key.Down or Key.M or Key.T or Key.OemComma, KeyModifiers.Control) => true,
        (Key.M, KeyModifiers.None) => true,
        _ => false,
    };

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        heldKeys.Remove(e.Key);
        if (e.Key is Key.LeftAlt or Key.RightAlt && movedWithAlt)
        {
            // Alt だけを押して離すと、メニューバーが選ばれる。Alt+↑ / Alt+↓ をここで受けた場合も、Alt を離したときに同じことが起きて、
            // 続けて押した矢印キーがメニューに取られる。メニューが選ばれるのはこの後のこともあるので、少し後で閉じる。
            // 閉じてもフォーカスは一覧に戻らない（Alt を押したときにフォーカスのあった行は、並びを変えたときに作り直されている）ので、
            // 動かした曲の行へ戻す。
            movedWithAlt = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (MainMenu.IsOpen)
                {
                    MainMenu.Close();
                }
                if (!viewModel.MiniMode && !PlaylistBox.IsKeyboardFocusWithin)
                {
                    FocusPlaylist();
                }
            });
        }
    }

    /// <summary>
    /// フォーカスを一覧の選択中の行へ移す。一覧そのもの（ListBox）はフォーカスを受け取らないので、行に渡す。
    /// 選択中の行がない、または画面の外にあって作られていない場合は、フォーカスを外す
    /// （どこにもフォーカスがなければキーはウィンドウが受けるので、再生の操作のキーは効く）。
    /// </summary>
    private void FocusPlaylist()
    {
        if (PlaylistBox.SelectedIndex is >= 0 and int index
            && PlaylistBox.ContainerFromIndex(index) is { } row
            && row.Focus())
        {
            return;
        }
        FocusManager?.Focus(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        bool inPlaylist = PlaylistBox.IsKeyboardFocusWithin;
        e.Handled = true;

        // ミニモードでは一覧が見えない。見えないまま曲を消したり選んだりしないよう、一覧を編集するキーは受け付けない。
        bool listVisible = !viewModel.MiniMode;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.O, KeyModifiers.Control):
                OnOpenClick(this, e);
                break;
            case (Key.S, KeyModifiers.Control):
                OnSaveClick(this, e);
                break;
            case (Key.S, KeyModifiers.Control | KeyModifiers.Shift):
                OnSaveAsClick(this, e);
                break;
            case (Key.Z, KeyModifiers.Control) when listVisible:
                viewModel.Undo();
                break;
            case (Key.Y, KeyModifiers.Control) when listVisible:
                viewModel.Redo();
                break;
            case (Key.A, KeyModifiers.Control) when listVisible:
                PlaylistBox.SelectAll();
                break;
            case (Key.Delete, KeyModifiers.None) when listVisible:
                RemoveSelected();
                break;
            case (Key.Enter, KeyModifiers.None) when listVisible && inPlaylist && PlaylistBox.SelectedItem is PlaylistItem item:
                viewModel.PlayItem(item);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>
    /// 一覧の行に曲を割り当てた（スクロールなどで行を作り直した）。大きなプレイリストを端から端までスクロールすると、
    /// 行の部品や文字の組みの一部が古い世代へ移ってヒープがすき間だらけになり、若い世代の GC だけでは OS に返らない（10,000 曲で約 5MB）。
    /// 多くの行を作り直したら、止まった後に 1 回だけヒープを詰めて返す（SPEC 8.3）。
    /// </summary>
    private void OnRowPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (++preparedSinceCompact < CompactAfterPreparedRows)
        {
            return;
        }
        compactTimer ??= new DispatcherTimer(CompactDelay, DispatcherPriority.Background, (_, _) => CompactAfterScroll());
        compactTimer.Stop();
        compactTimer.Start();
    }

    private void CompactAfterScroll()
    {
        compactTimer?.Stop();
        preparedSinceCompact = 0;
        if (!closed)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private void OnCurrentItemChanged(object? sender, EventArgs e) => ScrollToCurrent();

    private void ScrollToCurrent()
    {
        if (viewModel.CurrentItem is { } item && viewModel.IndexOf(item) is >= 0 and int index)
        {
            PlaylistBox.ScrollIntoView(index);
        }
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFiles("開く") is { Count: > 0 } paths)
        {
            viewModel.OpenPaths(paths);
        }
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (await PickFiles("ファイルを追加") is { Count: > 0 } paths)
        {
            viewModel.AddPaths(paths, onAdded: Select);
        }
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "フォルダを追加", AllowMultiple = true });
            string[] paths = LocalPaths(folders);
            if (paths.Length > 0)
            {
                viewModel.AddPaths(paths, onAdded: Select);
            }
        }
        catch (Exception ex)
        {
            viewModel.ShowError($"フォルダを選べません: {ex.Message}");
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!viewModel.SavePlaylist())
        {
            await SaveAs();
        }
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveAs();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnUndoClick(object? sender, RoutedEventArgs e) => viewModel.Undo();

    private void OnRedoClick(object? sender, RoutedEventArgs e) => viewModel.Redo();

    private void OnRemoveClick(object? sender, RoutedEventArgs e) => RemoveSelected();

    private void OnClearClick(object? sender, RoutedEventArgs e) => viewModel.Clear();

    private void OnShuffleClick(object? sender, RoutedEventArgs e)
    {
        viewModel.Shuffle();
        ScrollToCurrent();
    }

    private void OnRenumberClick(object? sender, RoutedEventArgs e) => viewModel.Renumber();

    private void OnPlayNextClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTargetItem() is { } item)
        {
            viewModel.PlayItemNext(item);
        }
    }

    private void OnPlayFromHereClick(object? sender, RoutedEventArgs e)
    {
        if (MenuTargetItem() is { } item)
        {
            viewModel.PlayItem(item);
        }
    }

    /// <summary>右クリックのメニューの対象の曲。曲の上で開いたならその曲、キーボードで開いたなら選択中の曲。</summary>
    private PlaylistItem? MenuTargetItem() =>
        menuOpenedByPointer ? menuItem : PlaylistBox.SelectedItem as PlaylistItem;

    private void OnPlaylistMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        PlayFromHereItem.IsEnabled = MenuTargetItem() is not null;
        PlayNextItem.IsEnabled = PlayFromHereItem.IsEnabled;
        RemoveItem.IsEnabled = PlaylistBox.SelectedItems?.Count > 0;
        MoveItem.IsEnabled = RemoveItem.IsEnabled;
    }

    private void OnPlaylistMenuClosed(object? sender, RoutedEventArgs e)
    {
        menuOpenedByPointer = false;
        menuItem = null;
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => PlaylistBox.SelectAll();

    /// <summary>「編集」メニューを開いた。曲を選んでいないときは「プレイリストから削除」を選べなくする（右クリックのメニューと同じ）。</summary>
    private void OnEditMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.Source, EditMenu))
        {
            EditRemoveItem.IsEnabled = PlaylistBox.SelectedItems?.Count > 0;
        }
    }

    /// <summary>「移動」を開いたときに、今の選択で並びが変わらない行き先（先頭にある曲の「上へ」など）を選べなくする。</summary>
    private void OnMoveMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || !ReferenceEquals(e.Source, menu))
        {
            return;
        }

        PlaylistItem[] selected = SelectedItems();
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag is string move && viewModel.CanMove(selected, Enum.Parse<PlaylistMove>(move));
        }
    }

    private void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string move })
        {
            MoveSelected(Enum.Parse<PlaylistMove>(move));
        }
    }

    private void MoveSelected(PlaylistMove move)
    {
        PlaylistItem[] selected = SelectedItems();
        if (!viewModel.Move(selected, move))
        {
            return;
        }
        Select(selected);

        // 1 つずつ動かす操作は続けて行うので、動かした曲が見える位置まで追う。
        // 離れた場所へ送る操作では追わない（送り出した後も、同じ場所で作業を続けられるように）。
        if (move is PlaylistMove.Up or PlaylistMove.Down)
        {
            int[] indexes = Array.ConvertAll(selected, viewModel.IndexOf);
            PlaylistBox.ScrollIntoView(move == PlaylistMove.Up ? indexes.Min() : indexes.Max());
        }
    }

    private void OnSortClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key })
        {
            viewModel.Sort(Enum.Parse<PlaylistSortKey>(key));
        }
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => viewModel.TogglePlayPause();

    private void OnStopClick(object? sender, RoutedEventArgs e) => viewModel.Stop();

    private void OnNextClick(object? sender, RoutedEventArgs e) => viewModel.Next();

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => viewModel.Previous();

    private void OnPlaylistDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemAt(e.Source) is { } item)
        {
            viewModel.PlayItem(item);
        }
    }

    private async Task<IReadOnlyList<string>?> PickFiles(string title)
    {
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("音声ファイルとプレイリスト")
                    {
                        Patterns = viewModel.AudioExtensions
                            .Concat(PlaylistFile.Extensions)
                            .Select(extension => "*" + extension)
                            .ToArray(),
                    },
                    FilePickerFileTypes.All,
                ],
            });
            return LocalPaths(files);
        }
        catch (Exception ex)
        {
            viewModel.ShowError($"ファイルを選べません: {ex.Message}");
            return null;
        }
    }

    private async Task SaveAs()
    {
        try
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "プレイリストに名前を付けて保存",
                DefaultExtension = "m3u8",
                SuggestedFileName = "playlist",
                FileTypeChoices = [new FilePickerFileType("M3U8 プレイリスト") { Patterns = ["*.m3u8"] }],
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                viewModel.SavePlaylistAs(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ShowError($"保存先を選べません: {ex.Message}");
        }
    }

    private static string[] LocalPaths(IEnumerable<IStorageItem> items) =>
        items.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();

    private PlaylistItem[] SelectedItems() =>
        PlaylistBox.SelectedItems?.OfType<PlaylistItem>().ToArray() ?? [];

    private void RemoveSelected()
    {
        PlaylistItem[] selected = SelectedItems();
        if (selected.Length == 0)
        {
            return;
        }

        // 続けて削除しやすいよう、削除した先頭の位置に来た行を選ぶ。
        int first = selected.Min(viewModel.IndexOf);
        viewModel.Remove(selected);
        if (viewModel.Items.Count > 0)
        {
            PlaylistBox.SelectedIndex = Math.Clamp(first, 0, viewModel.Items.Count - 1);
        }
    }

    /// <summary>指定した項目だけを選択した状態にする。</summary>
    private void Select(IReadOnlyCollection<PlaylistItem> items)
    {
        var set = new HashSet<PlaylistItem>(items);
        PlaylistBox.Selection.Clear();
        for (int i = 0; i < viewModel.Items.Count; i++)
        {
            if (set.Contains(viewModel.Items[i]))
            {
                PlaylistBox.Selection.Select(i);
            }
        }
    }

    private static PlaylistItem? ItemAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PlaylistItem;

    private void OnPlaylistPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetPress();
        menuOpenedByPointer = false;
        menuItem = null;
        if (e.GetCurrentPoint(PlaylistBox).Properties.IsRightButtonPressed)
        {
            // この後で開く右クリックのメニューのために、どの曲の上で押されたかを覚えておく（曲のない場所なら null）。
            menuOpenedByPointer = true;
            menuItem = ItemAt(e.Source);
            return;
        }
        if (!e.GetCurrentPoint(PlaylistBox).Properties.IsLeftButtonPressed || ItemAt(e.Source) is not { } item)
        {
            return;
        }

        pressedItem = item;
        pressPoint = e.GetPosition(PlaylistBox);
        pressEvent = e;

        // 複数選択した行のどれかを押した時点で選択を 1 行に絞ると、まとめてドラッグできなくなる。
        // ドラッグにならずに離したときに初めて、その 1 行だけの選択にする。
        PlaylistItem[] selected = SelectedItems();
        if (e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 && selected.Length > 1 && selected.Contains(item))
        {
            selectOnRelease = true;
            e.Handled = true;

            // 押したことを行に渡さないので、行はフォーカスを受け取らない。代わりにここで渡す
            // （渡さないと、メニューなどに残ったフォーカスに、この後のキーが取られる）。
            (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.Focus();
        }
    }

    private void OnPlaylistPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (selectOnRelease && pressedItem is not null && reorderItems is null)
        {
            Select([pressedItem]);
        }
        if (reorderItems is null)
        {
            ResetPress();
        }
    }

    private async void OnPlaylistPointerMoved(object? sender, PointerEventArgs e)
    {
        if (pressPoint is not { } start || pressEvent is not { } press || pressedItem is null || reorderItems is not null)
        {
            return;
        }
        if (!e.GetCurrentPoint(PlaylistBox).Properties.IsLeftButtonPressed)
        {
            ResetPress();
            return;
        }

        Point position = e.GetPosition(PlaylistBox);
        if (Math.Abs(position.X - start.X) < DragThreshold && Math.Abs(position.Y - start.Y) < DragThreshold)
        {
            return;
        }

        PlaylistItem[] selected = SelectedItems();
        reorderItems = selected.Contains(pressedItem) ? selected : [pressedItem];
        try
        {
            // 並べ替えの対象は reorderItems で持つ。ドラッグのデータは、ドラッグを成立させるための目印にすぎない。
            // このプロセスの中でしか通じない形式にして、他のアプリに落としても何も起きないようにする。
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(ReorderFormat, ""));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            viewModel.ShowError($"並べ替えできません: {ex.Message}");
        }
        finally
        {
            reorderItems = null;
            ResetPress();
            PlaylistDropMarker.IsVisible = false;
        }
    }

    private void ResetPress()
    {
        pressPoint = null;
        pressEvent = null;
        pressedItem = null;
        selectOnRelease = false;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (reorderItems is not null)
        {
            // 並べ替えは一覧の中でだけ受け付ける。一覧の外（メニューやボタンの上）で離したら取り消しになる。
            e.DragEffects = IsOverPlaylist(e) ? DragDropEffects.Move : DragDropEffects.None;
        }
        else
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        }
        UpdateDropMarker(e);
    }

    /// <summary>
    /// 一覧の中へドラッグしている間、離したときに入る位置（<see cref="DropIndex"/> と同じ位置）に線を出す。
    /// 一覧の外では消す。外で離すとファイルの場合は設定の動作（置き換えなど）になるので、線の有無でどちらになるかが分かる。
    /// </summary>
    private void UpdateDropMarker(DragEventArgs e)
    {
        double? y = null;
        if (e.DragEffects != DragDropEffects.None && IsOverPlaylist(e))
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } row)
            {
                bool after = e.GetPosition(row).Y > row.Bounds.Height / 2;
                y = row.TranslatePoint(new Point(0, after ? row.Bounds.Height : 0), PlaylistBox)?.Y;
            }
            else
            {
                // 行のない場所（最後の行より下）は末尾に入る。最後の行の下端に出す。曲がなければ一覧の上端。
                y = viewModel.Items.Count == 0
                    ? 0
                    : PlaylistBox.ContainerFromIndex(viewModel.Items.Count - 1) is { } last
                        ? last.TranslatePoint(new Point(0, last.Bounds.Height), PlaylistBox)?.Y
                        : null;
            }
        }

        if (y is not { } top)
        {
            PlaylistDropMarker.IsVisible = false;
            return;
        }
        double height = PlaylistDropMarker.Height;
        Thickness margin = PlaylistDropMarker.Margin;
        double clamped = Math.Clamp(top - height / 2, 0, Math.Max(0, PlaylistBox.Bounds.Height - height));
        PlaylistDropMarker.Margin = new Thickness(margin.Left, clamped, margin.Right, 0);
        PlaylistDropMarker.IsVisible = true;
    }

    // 一覧の中の別の部品へ移ったときも、元の部品から「離れた」が届く。その直後に移った先での通知（DragEnter）が来て線を出し直すので、
    // ここでは消すだけでよい。
    private void OnDragLeave(object? sender, DragEventArgs e) => PlaylistDropMarker.IsVisible = false;

    private bool IsOverPlaylist(DragEventArgs e) =>
        e.Source is Visual visual && (ReferenceEquals(visual, PlaylistBox) || PlaylistBox.IsVisualAncestorOf(visual));

    private void OnDrop(object? sender, DragEventArgs e)
    {
        PlaylistDropMarker.IsVisible = false;
        int? index = DropIndex(e);
        if (reorderItems is { } moving)
        {
            if (IsOverPlaylist(e))
            {
                viewModel.Move(moving, index ?? viewModel.Items.Count);
                Select(moving);
            }
        }
        else if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            // 一覧の中に落としたら、その位置に追加する。一覧の外（再生の操作の部分など）に落としたら、
            // 他のアプリから開かれたときと同じく、設定の動作（置き換えて再生 / 末尾に追加 / 追加して再生）に従う。
            if (IsOverPlaylist(e))
            {
                viewModel.AddPaths(LocalPaths(files), index, Select);
            }
            else
            {
                viewModel.OpenDroppedPaths(LocalPaths(files));
            }
        }
    }

    /// <summary>ドロップされた位置に対応する挿入位置。行の上半分ならその行の前、下半分なら後ろ。行の外なら null。</summary>
    private int? DropIndex(DragEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } row)
        {
            return null;
        }

        int index = PlaylistBox.IndexFromContainer(row);
        if (index < 0)
        {
            return null;
        }
        return e.GetPosition(row).Y > row.Bounds.Height / 2 ? index + 1 : index;
    }
}
