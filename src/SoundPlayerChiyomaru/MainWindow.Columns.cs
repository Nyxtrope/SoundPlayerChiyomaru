using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundPlayerChiyomaru.Core.Localization;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Settings;

namespace SoundPlayerChiyomaru;

// プレイリストの列: 設定の反映と、見出しをドラッグして幅と順番を変える操作。
public partial class MainWindow
{
    /// <summary>見出しの区切り線は、列の境目からこれだけ左に描かれている（XAML の Border.header の余白と合わせること）。</summary>
    private const double SeparatorOffset = 4;

    /// <summary>区切り線の左右この距離までを、幅を変えるためにつかめる範囲にする。</summary>
    private const double GripHalfWidth = 5;

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>見出しをクリックしたときの並べ替えの鍵。並びは <see cref="PlaylistColumn"/> の値の順。</summary>
    private static readonly PlaylistSortKey[] ColumnSortKeys =
    [
        PlaylistSortKey.TrackNumber, PlaylistSortKey.Title, PlaylistSortKey.Artist, PlaylistSortKey.Album,
        PlaylistSortKey.AlbumArtist, PlaylistSortKey.Duration, PlaylistSortKey.FileName, PlaylistSortKey.Order,
    ];

    /// <summary>見出しの元の文字（XAML に書いたもの）。並べ替えの印（▲▼）を付け外しするときに使う。</summary>
    private string[]? headerTexts;

    private ColumnDrag? columnDrag;

    /// <summary>見出しの上でボタンを押してから離すまでの状態。</summary>
    private sealed class ColumnDrag
    {
        /// <summary>幅を変えているなら境目の左の列、順番を変えているなら動かす列の位置。</summary>
        public required int Index { get; init; }

        public required double StartX { get; init; }

        /// <summary>順番を変えている場合の、動かしている列の見出し。</summary>
        public Control? Header { get; init; }

        /// <summary>幅を変えている場合の、押したときの各列の設定の幅と実際の幅。順番を変えている場合は null。</summary>
        public double[]? StartWidths { get; init; }

        public double[]? ActualWidths { get; init; }

        /// <summary>順番を変えるドラッグが始まった（押してから十分に動かした）。</summary>
        public bool Moving { get; set; }

        /// <summary>順番を変えている場合の、今の位置で離したときの入れ先（この位置の列の前）。</summary>
        public int Before { get; set; }
    }

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        ApplyColumns(ColumnHeader);
        // 表示中の行だけでなく、フォーカスを持ったまま画面の外で保持されている行にも適用する
        // （その行は再利用されずに残るので、戻ってきたときに設定し直す機会がない）。
        foreach (ListBoxItem row in PlaylistBox.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (row.FindDescendantOfType<Grid>() is { } grid)
            {
                ApplyColumns(grid);
            }
        }
    }

    private void OnRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            ApplyColumns(grid);
        }
    }

    /// <summary>
    /// 見出しや行の列を、列の設定（順番・幅・表示）に合わせる。グリッドの子は <see cref="PlaylistColumn"/> の値の順に並んでいて、
    /// それぞれを設定の順番の列に置く。隠す列は幅を 0 にして中身も隠す。
    /// 行はたくさんあるので、列の定義を共有せずにそれぞれに設定する。
    /// </summary>
    private void ApplyColumns(Grid grid)
    {
        IReadOnlyList<ColumnSetting> columns = viewModel.Columns;
        if (grid.Children.Count < columns.Count)
        {
            return;
        }

        ColumnDefinitions definitions = grid.ColumnDefinitions;
        if (definitions.Count != columns.Count)
        {
            definitions = [];
            for (int i = 0; i < columns.Count; i++)
            {
                definitions.Add(new ColumnDefinition());
            }
            grid.ColumnDefinitions = definitions;
        }

        int last = columns.Count - 1;
        while (last > 0 && !columns[last].Visible)
        {
            last--;
        }

        // 幅を変えるドラッグの間は動かすたびに呼ばれるので、列の定義は作り直さずに値だけを変える。
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSetting column = columns[i];
            definitions[i].Width = !column.Visible
                ? new GridLength(0)
                : ColumnLayout.IsProportional(column.Id)
                    ? new GridLength(column.Width, GridUnitType.Star)
                    : new GridLength(column.Width);

            Control child = grid.Children[(int)column.Id];
            Grid.SetColumn(child, i);
            child.IsVisible = column.Visible;

            // 見出しの右端の列には、つかめる境目がないので、区切り線を出さない。
            if (child is Border)
            {
                child.Classes.Set("last", i == last);
            }
        }
    }

    private double[] ActualColumnWidths()
    {
        ColumnDefinitions definitions = ColumnHeader.ColumnDefinitions;
        double[] widths = new double[definitions.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = definitions[i].ActualWidth;
        }
        return widths;
    }

    /// <summary>見出しの <paramref name="x"/> の位置でつかめる境目の、左の列の位置。なければ -1。</summary>
    private int ColumnBoundaryAt(double x)
    {
        IReadOnlyList<ColumnSetting> columns = viewModel.Columns;
        double[] widths = ActualColumnWidths();
        if (widths.Length != columns.Count)
        {
            return -1;
        }

        double edge = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            if (!columns[i].Visible)
            {
                continue;
            }
            edge += widths[i];
            if (Math.Abs(x - (edge - SeparatorOffset)) <= GripHalfWidth && ColumnLayout.NextVisible(columns, i) >= 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>見出しの <paramref name="x"/> の位置にある表示中の列の位置。なければ -1。</summary>
    private int ColumnAt(double x)
    {
        IReadOnlyList<ColumnSetting> columns = viewModel.Columns;
        double[] widths = ActualColumnWidths();
        if (widths.Length != columns.Count || x < 0)
        {
            return -1;
        }

        double edge = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Visible && x < (edge += widths[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 見出しを <paramref name="x"/> の位置で離したときの入れ先と、そこに出す目印の位置。
    /// 列の左半分の上ならその列の前、右半分の上なら次の列の前に入れる。
    /// </summary>
    private (int Before, double MarkerX) ColumnDropTarget(double x)
    {
        IReadOnlyList<ColumnSetting> columns = viewModel.Columns;
        double[] widths = ActualColumnWidths();
        double edge = 0;
        if (widths.Length == columns.Count)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (!columns[i].Visible)
                {
                    continue;
                }
                if (x < edge + (widths[i] / 2))
                {
                    return (i, edge);
                }
                edge += widths[i];
            }
        }
        return (columns.Count, edge);
    }

    /// <summary>見出しを右クリックしたときのメニュー。右クリックした列を動かす・隠す、その隣に隠している列を出す。</summary>
    /// <param name="index">右クリックした列の位置。</param>
    private void ShowColumnMenu(int index)
    {
        IReadOnlyList<ColumnSetting> columns = viewModel.Columns;
        if (index < 0 || index >= columns.Count)
        {
            return;
        }

        PlaylistColumn id = columns[index].Id;
        int previous = ColumnLayout.PreviousVisible(columns, index);
        int next = ColumnLayout.NextVisible(columns, index);
        // 曲の右クリックと同じ部品（ContextMenu）を使う。MenuFlyout だと、下の階層がある項目の「>」が右端で切れた。
        var menu = new ContextMenu();
        string[] names = Strings.ColumnNames;

        var moveLeft = new MenuItem { Header = UiLanguage.Text("左へ移動", "Move Left"), IsEnabled = previous >= 0 };
        moveLeft.Click += (_, _) => viewModel.MoveColumn(index, previous);
        menu.Items.Add(moveLeft);

        var moveRight = new MenuItem { Header = UiLanguage.Text("右へ移動", "Move Right"), IsEnabled = next >= 0 };
        moveRight.Click += (_, _) => viewModel.MoveColumn(index, next + 1);
        menu.Items.Add(moveRight);

        // タイトルの列は隠せない（すべての列を隠すと、一覧に何も出なくなる）。
        var hide = new MenuItem { Header = UiLanguage.Text($"「{names[(int)id]}」を隠す", $"Hide \"{names[(int)id]}\""), IsEnabled = id != PlaylistColumn.Title };
        hide.Click += (_, _) => viewModel.SetColumnVisible(id, false);
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());

        PlaylistColumn[] hidden = [.. columns.Where(column => !column.Visible).Select(column => column.Id).Order()];
        foreach ((string header, int before) in new[] { (UiLanguage.Text("左に列を追加", "Add Column to the Left"), index), (UiLanguage.Text("右に列を追加", "Add Column to the Right"), index + 1) })
        {
            var add = new MenuItem { Header = header, IsEnabled = hidden.Length > 0 };
            foreach (PlaylistColumn column in hidden)
            {
                var item = new MenuItem { Header = names[(int)column] };
                item.Click += (_, _) => viewModel.ShowColumn(column, before);
                add.Items.Add(item);
            }
            menu.Items.Add(add);
        }

        menu.Open(ColumnHeader);
    }

    private void OnColumnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // ドラッグの途中で別のボタンを押されても、始め直さない。
        if (columnDrag is not null || !e.GetCurrentPoint(ColumnHeader).Properties.IsLeftButtonPressed)
        {
            return;
        }

        double x = e.GetPosition(ColumnHeader).X;
        int boundary = ColumnBoundaryAt(x);
        if (boundary >= 0)
        {
            columnDrag = new ColumnDrag
            {
                Index = boundary,
                StartX = x,
                StartWidths = [.. viewModel.Columns.Select(column => column.Width)],
                ActualWidths = ActualColumnWidths(),
            };
        }
        else if (ColumnAt(x) is >= 0 and int column)
        {
            columnDrag = new ColumnDrag
            {
                Index = column,
                StartX = x,
                Header = ColumnHeader.Children[(int)viewModel.Columns[column].Id],
            };
        }
        else
        {
            return;
        }

        e.Pointer.Capture(ColumnHeader);
        e.Handled = true;
    }

    private void OnColumnHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        double x = e.GetPosition(ColumnHeader).X;
        if (columnDrag is not { } drag)
        {
            ColumnHeader.Cursor = ColumnBoundaryAt(x) >= 0 ? ResizeCursor : null;
            return;
        }

        if (drag.StartWidths is not null && drag.ActualWidths is not null)
        {
            viewModel.ResizeColumn(drag.StartWidths, drag.ActualWidths, drag.Index, x - drag.StartX);
            return;
        }

        if (!drag.Moving && Math.Abs(x - drag.StartX) < DragThreshold)
        {
            return;
        }

        drag.Moving = true;
        (int before, double markerX) = ColumnDropTarget(x);
        drag.Before = before;
        drag.Header?.Opacity = 0.4;
        ColumnDropMarker.Margin = new Thickness(Math.Max(0, markerX - SeparatorOffset), 0, 0, 0);
        ColumnDropMarker.IsVisible = true;
    }

    private void OnColumnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (columnDrag is null && e.InitialPressMouseButton == MouseButton.Right)
        {
            ShowColumnMenu(ColumnAt(e.GetPosition(ColumnHeader).X));
            e.Handled = true;
            return;
        }
        if (columnDrag is not { } drag || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        // 先に終わらせる。ポインターを手放すと、取り消しの通知（OnColumnHeaderCaptureLost）が来る。
        EndColumnDrag();
        e.Pointer.Capture(null);
        if (drag.Moving)
        {
            viewModel.MoveColumn(drag.Index, drag.Before);
        }
        else if (drag.StartWidths is null && new Rect(ColumnHeader.Bounds.Size).Contains(e.GetPosition(ColumnHeader)))
        {
            // 境目ではない場所を押して、動かさずに見出しの上で離した（クリック）。その列で並べ替える。
            viewModel.Sort(ColumnSortKeys[(int)viewModel.Columns[drag.Index].Id]);
        }
    }

    /// <summary>
    /// 最後に並べ替えた列の見出しに、向きの印（昇順 ▲、降順 ▼）を付ける。並べた後に編集すると消える（Playlist.SortState）。
    /// 印はどの列でも名前の後ろに付ける。右寄せの列（曲順、トラック番号、時間）では、その間だけ見出しの名前が印の分だけ左へずれる。
    /// </summary>
    private void UpdateSortIndicator()
    {
        headerTexts ??= [.. ColumnHeader.Children.Select(child => ((child as Border)?.Child as TextBlock)?.Text ?? "")];
        PlaylistSortState? state = viewModel.SortState;
        for (int i = 0; i < ColumnHeader.Children.Count && i < ColumnSortKeys.Length; i++)
        {
            if ((ColumnHeader.Children[i] as Border)?.Child is not TextBlock header)
            {
                continue;
            }
            string name = headerTexts[i];
            string? arrow = state is { } sorted && sorted.Key == ColumnSortKeys[i] ? (sorted.Descending ? "▼" : "▲") : null;
            header.Text = arrow is null ? name : $"{name} {arrow}";
        }
    }

    /// <summary>ドラッグの途中で別のウィンドウに移ったなどで、離したことを受け取れなかった。順番の変更は取り消す。</summary>
    private void OnColumnHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndColumnDrag();

    private void EndColumnDrag()
    {
        columnDrag?.Header?.Opacity = 1;
        bool resized = columnDrag is { StartWidths: not null };
        columnDrag = null;
        ColumnDropMarker.IsVisible = false;

        // 幅を変えている間は、動かすたびに見えている行の文字を組み直す。その一部が古い世代へ移ってヒープがすき間だらけになり、
        // 若い世代の GC だけでは OS に返らない（約 5MB。SPEC 8.3）。ドラッグを終えた時点で 1 回だけ、ヒープを詰めて返す。
        if (resized)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }
}
