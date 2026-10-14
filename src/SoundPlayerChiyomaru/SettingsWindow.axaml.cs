using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Settings;

namespace SoundPlayerChiyomaru;

/// <summary>
/// 設定画面。変更はその場で反映する（保存はメインウィンドウの設定と同じく、終了時と編集が落ち着いたとき）。
/// 設定の入口はメインウィンドウの ViewModel と共有するので、メニューから変えた内容と食い違わない。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>この設定が他の場所（メニューやショートカット）から変えられたら、表示を合わせ直す。</summary>
    private static readonly HashSet<string> WatchedProperties =
    [
        nameof(MainWindowViewModel.ExclusiveMode),
        nameof(MainWindowViewModel.ReplayGainMode),
        nameof(MainWindowViewModel.BufferMilliseconds),
        nameof(MainWindowViewModel.MaxOutputBitDepth),
        nameof(MainWindowViewModel.ExcludeDuplicates),
        nameof(MainWindowViewModel.SaveRelativePaths),
        nameof(MainWindowViewModel.ExternalOpenActionIndex),
        nameof(MainWindowViewModel.Theme),
        nameof(MainWindowViewModel.AlwaysOnTop),
    ];

    /// <summary>バージョン情報のページに出す文書の、埋め込みリソースの名前。並びは選択欄の順。</summary>
    private static readonly string[] Documents = ["RELEASE_NOTES.md", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt"];

    private readonly MainWindowViewModel viewModel;
    private IReadOnlyList<OutputDeviceChoice> devices = [];

    /// <summary>設定から部品へ値を入れている最中。その間の変更の通知は、利用者の操作ではないので設定へ書き戻さない。</summary>
    private bool loading;

    internal SettingsWindow(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();

        BufferBox.ItemsSource = OutputDeviceSelection.BufferChoices
            .Select(value => value == OutputDeviceSelection.DefaultBufferMilliseconds ? $"{value} ミリ秒（既定）" : $"{value} ミリ秒")
            .ToArray();
        BitDepthBox.ItemsSource = new[] { "自動（既定、曲に合わせる）" }
            .Concat(OutputDeviceSelection.BitDepthLimits.Select(bits => $"{bits} bit まで"))
            .ToArray();
        VersionText.Text = $"バージョン {MainWindowViewModel.AppVersion}";

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ColumnsChanged += OnColumnsChanged;
        viewModel.OutputStatusChanged += OnOutputStatusChanged;

        // 開いている間にデバイスが抜き差しされても、選び直すときには今の一覧になっているようにする。
        DeviceBox.DropDownOpened += (_, _) => LoadDevices();
        Activated += (_, _) => LoadDevices();

        PageList.SelectedIndex = 0;
        LoadDevices();
        LoadAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.ColumnsChanged -= OnColumnsChanged;
        viewModel.OutputStatusChanged -= OnOutputStatusChanged;
        (AboutImage.Source as Bitmap)?.Dispose();
        AboutImage.Source = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// Esc で閉じる。選択欄を開いているときは、選択欄が先に受け取って閉じる（処理済みになる）ので、ウィンドウは閉じない。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && WatchedProperties.Contains(name))
        {
            LoadAll();
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.Badge) or nameof(MainWindowViewModel.FormatText))
        {
            // 曲が変わった、出力が作り直された。操作中の選択欄を閉じてしまわないよう、出力の状態の表示だけを直す。
            LoadOutputStatus();
        }
    }

    // 再生を始めた・止めた、出力を作り直した。共有モードでは印も形式の表示も変わらないので、出力からの通知で直す。
    private void OnOutputStatusChanged(object? sender, EventArgs e) => LoadOutputStatus();

    // 列の幅をドラッグしている間は、動かすたびに呼ばれる。列のチェックだけを直す。
    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        loading = true;
        try
        {
            LoadColumns();
        }
        finally
        {
            loading = false;
        }
    }

    private void LoadColumns()
    {
        foreach (CheckBox box in ColumnBoxes.Children.OfType<CheckBox>())
        {
            if (box.Tag is string name && Enum.TryParse(name, out PlaylistColumn id))
            {
                box.IsChecked = viewModel.Columns.Any(column => column.Id == id && column.Visible);
            }
        }
    }

    /// <summary>
    /// デバイスの一覧以外の部品を、今の設定に合わせる。デバイスの一覧は Windows への問い合わせが要るので、
    /// 必要なとき（開いたとき、選び直すとき）だけ <see cref="LoadDevices"/> で読む。
    /// </summary>
    private void LoadAll()
    {
        loading = true;
        try
        {
            SharedRadio.IsChecked = !viewModel.ExclusiveMode;
            ExclusiveRadio.IsChecked = viewModel.ExclusiveMode;
            ReplayGainBox.SelectedIndex = (int)viewModel.ReplayGainMode;
            BufferBox.SelectedIndex = IndexOf(OutputDeviceSelection.BufferChoices, viewModel.BufferMilliseconds);
            BitDepthBox.SelectedIndex = viewModel.MaxOutputBitDepth is { } bits
                ? IndexOf(OutputDeviceSelection.BitDepthLimits, bits) + 1
                : 0;

            LoadOutputStatus();

            ExcludeDuplicatesBox.IsChecked = viewModel.ExcludeDuplicates;
            RelativePathsBox.IsChecked = viewModel.SaveRelativePaths;
            LoadColumns();

            string external = viewModel.ExternalOpenActionIndex.ToString(CultureInfo.InvariantCulture);
            foreach (RadioButton radio in ExternalOpenRadios.Children.OfType<RadioButton>())
            {
                radio.IsChecked = (radio.Tag as string) == external;
            }

            ThemeBox.SelectedIndex = (int)viewModel.Theme;
            AlwaysOnTopBox.IsChecked = viewModel.AlwaysOnTop;
        }
        finally
        {
            loading = false;
        }
    }

    private void LoadOutputStatus()
    {
        IReadOnlyList<string> status = viewModel.DescribeOutput();
        OutputStatusText.Text = status.Count == 0
            ? "出力していません（再生を始めると表示されます）"
            : string.Join("\n", viewModel.OutputNotice is { } notice ? [.. status, notice] : status);
    }

    private void LoadDevices()
    {
        bool wasLoading = loading;
        loading = true;
        try
        {
            devices = viewModel.GetOutputDevices();
            DeviceBox.ItemsSource = devices.Select(device => device.Name).ToArray();
            DeviceBox.SelectedIndex = IndexOf(devices.Select(device => device.IsSelected).ToArray(), true);
        }
        finally
        {
            loading = wasLoading;
        }
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], value))
            {
                return i;
            }
        }
        return -1;
    }

    private void OnPageChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 初期化の途中（ページの部品がまだできていない間）にも呼ばれる。
        if (Pages is null)
        {
            return;
        }

        for (int i = 0; i < Pages.Children.Count; i++)
        {
            Pages.Children[i].IsVisible = i == PageList.SelectedIndex;
        }

        // バージョン情報の画像と文書は、そのページを開いたときに初めて読む。
        if (PageList.SelectedIndex == Pages.Children.Count - 1 && AboutImage.Source is null)
        {
            using Stream? image = OpenResource("about.png");
            if (image is not null)
            {
                AboutImage.Source = new Bitmap(image);
            }
            DocumentBox.SelectedIndex = 0;
        }
    }

    private void OnDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!loading && DeviceBox.SelectedIndex >= 0 && DeviceBox.SelectedIndex < devices.Count)
        {
            viewModel.SelectOutputDevice(devices[DeviceBox.SelectedIndex].Id);
            LoadOutputStatus();
        }
    }

    private void OnOutputModeChanged(object? sender, RoutedEventArgs e)
    {
        // 選ばれた側と外れた側の両方から通知が来る。選ばれた側だけを見る。
        if (!loading && sender is RadioButton { IsChecked: true })
        {
            viewModel.ExclusiveMode = ReferenceEquals(sender, ExclusiveRadio);
        }
    }

    private void OnReplayGainChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!loading && ReplayGainBox.SelectedIndex >= 0)
        {
            viewModel.ReplayGainMode = (ReplayGainMode)ReplayGainBox.SelectedIndex;
        }
    }

    private void OnBufferChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!loading && BufferBox.SelectedIndex >= 0)
        {
            viewModel.BufferMilliseconds = OutputDeviceSelection.BufferChoices[BufferBox.SelectedIndex];
        }
    }

    private void OnBitDepthChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!loading && BitDepthBox.SelectedIndex >= 0)
        {
            viewModel.MaxOutputBitDepth = BitDepthBox.SelectedIndex == 0
                ? null
                : OutputDeviceSelection.BitDepthLimits[BitDepthBox.SelectedIndex - 1];
        }
    }

    private void OnExcludeDuplicatesChanged(object? sender, RoutedEventArgs e)
    {
        if (!loading)
        {
            viewModel.ExcludeDuplicates = ExcludeDuplicatesBox.IsChecked == true;
        }
    }

    private void OnColumnChanged(object? sender, RoutedEventArgs e)
    {
        if (!loading && sender is CheckBox { Tag: string name } box && Enum.TryParse(name, out PlaylistColumn id))
        {
            viewModel.SetColumnVisible(id, box.IsChecked == true);
        }
    }

    private void OnResetColumnsClick(object? sender, RoutedEventArgs e) => viewModel.ResetColumns();

    private void OnRelativePathsChanged(object? sender, RoutedEventArgs e)
    {
        if (!loading)
        {
            viewModel.SaveRelativePaths = RelativePathsBox.IsChecked == true;
        }
    }

    private void OnExternalOpenChanged(object? sender, RoutedEventArgs e)
    {
        if (!loading && sender is RadioButton { IsChecked: true, Tag: string index })
        {
            viewModel.ExternalOpenActionIndex = int.Parse(index, CultureInfo.InvariantCulture);
        }
    }

    private void OnRegisterAssociationClick(object? sender, RoutedEventArgs e) =>
        AssociationStatusText.Text = viewModel.RegisterFileAssociation();

    private void OnUnregisterAssociationClick(object? sender, RoutedEventArgs e) =>
        AssociationStatusText.Text = viewModel.UnregisterFileAssociation();

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!loading && ThemeBox.SelectedIndex >= 0 && Application.Current is { } app)
        {
            var theme = (AppTheme)ThemeBox.SelectedIndex;
            viewModel.Theme = theme;
            ThemeManager.Apply(app, theme);
        }
    }

    private void OnAlwaysOnTopChanged(object? sender, RoutedEventArgs e)
    {
        if (!loading)
        {
            viewModel.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        }
    }

    private void OnDocumentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DocumentBox.SelectedIndex < 0 || DocumentBox.SelectedIndex >= Documents.Length)
        {
            return;
        }

        using Stream? stream = OpenResource(Documents[DocumentBox.SelectedIndex]);
        if (stream is null)
        {
            DocumentList.ItemsSource = null;
            return;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        DocumentList.ItemsSource = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');
        DocumentList.ScrollIntoView(0);
    }

    /// <summary>実行ファイルに埋め込んだファイルを開く（名前はプロジェクトファイルの LogicalName）。</summary>
    private static Stream? OpenResource(string name) => typeof(SettingsWindow).Assembly.GetManifestResourceStream(name);
}
