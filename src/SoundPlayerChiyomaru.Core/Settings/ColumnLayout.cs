namespace SoundPlayerChiyomaru.Core.Settings;

/// <summary>プレイリストの列の種類。</summary>
public enum PlaylistColumn
{
    TrackNumber,
    Title,
    Artist,
    Album,
    AlbumArtist,
    Duration,
    FileName,

    /// <summary>曲順の番号（<see cref="Playlists.PlaylistItem.Order"/>）。</summary>
    Order,
}

/// <summary>プレイリストの 1 列の設定。設定ファイルには、表示する順に並べて保存する。</summary>
public sealed class ColumnSetting
{
    public PlaylistColumn Id { get; set; }

    /// <summary>
    /// 幅。曲順・トラック番号・時間はピクセル（拡大率を掛ける前）。それ以外は、残りの幅を分け合う比率
    /// （<see cref="ColumnLayout.IsProportional"/>）。
    /// </summary>
    public double Width { get; set; }

    public bool Visible { get; set; }

    public ColumnSetting Clone() => new() { Id = Id, Width = Width, Visible = Visible };
}

/// <summary>プレイリストの列の並び・幅・表示の操作。</summary>
public static class ColumnLayout
{
    /// <summary>ドラッグで縮められる最小の幅（ピクセル）。これより狭いと、つかんで広げ直すのが難しい。</summary>
    public const double MinWidth = 24;

    private const double MaxPixelWidth = 2000;

    /// <summary>比率として受け付ける範囲。極端に小さい値や大きい値は、比率をそろえる計算で無限大になる。</summary>
    private const double MinWeight = 0.000001;
    private const double MaxWeight = 1_000_000;

    public static int Count { get; } = Enum.GetValues<PlaylistColumn>().Length;

    public static List<ColumnSetting> CreateDefault() =>
    [
        new() { Id = PlaylistColumn.Order, Width = 36, Visible = true },
        new() { Id = PlaylistColumn.TrackNumber, Width = 36, Visible = false },
        new() { Id = PlaylistColumn.Title, Width = 3, Visible = true },
        new() { Id = PlaylistColumn.Artist, Width = 2, Visible = true },
        new() { Id = PlaylistColumn.Album, Width = 2, Visible = true },
        new() { Id = PlaylistColumn.AlbumArtist, Width = 2, Visible = false },
        new() { Id = PlaylistColumn.Duration, Width = 60, Visible = true },
        new() { Id = PlaylistColumn.FileName, Width = 2, Visible = true },
    ];

    /// <summary>
    /// 残りの幅を比率で分け合う列か。中身の長さが決まっている曲順・トラック番号・時間はピクセルで持ち、ウィンドウの幅を変えても変わらない。
    /// </summary>
    public static bool IsProportional(PlaylistColumn id) =>
        id is not (PlaylistColumn.Order or PlaylistColumn.TrackNumber or PlaylistColumn.Duration);

    /// <summary>
    /// 設定ファイルから読んだ内容を、使える形に直す。知らない列や重複は捨て、足りない列は既定の設定で末尾に足し、
    /// 壊れた幅は既定に戻す。タイトルの列は常に表示する（すべての列を隠すと、一覧に何も出なくなる）。
    /// </summary>
    public static List<ColumnSetting> Normalize(IEnumerable<ColumnSetting?>? saved)
    {
        List<ColumnSetting> defaults = CreateDefault();
        var result = new List<ColumnSetting>(Count);
        foreach (ColumnSetting? column in saved ?? [])
        {
            if (column is null || !Enum.IsDefined(column.Id) || result.Exists(c => c.Id == column.Id))
            {
                continue;
            }

            (double min, double max) = IsProportional(column.Id) ? (MinWeight, MaxWeight) : (1, MaxPixelWidth);
            bool valid = double.IsFinite(column.Width) && column.Width >= min && column.Width <= max;
            result.Add(new ColumnSetting
            {
                Id = column.Id,
                Width = valid ? column.Width : defaults.Find(c => c.Id == column.Id)!.Width,
                Visible = column.Visible || column.Id == PlaylistColumn.Title,
            });
        }

        // 読んだ比率は単位が決まっていない（ドラッグのたびにピクセルの値で置き換える）。これから足す列の既定の比率と
        // 釣り合うよう、読んだ列の比率の合計を、同じ列の既定の比率の合計にそろえておく。
        double savedWeight = 0;
        double defaultWeight = 0;
        foreach (ColumnSetting column in result)
        {
            if (IsProportional(column.Id))
            {
                savedWeight += column.Width;
                defaultWeight += defaults.Find(c => c.Id == column.Id)!.Width;
            }
        }
        foreach (ColumnSetting column in result)
        {
            if (IsProportional(column.Id))
            {
                column.Width *= defaultWeight / savedWeight;
            }
        }

        bool anySaved = result.Count > 0;
        foreach (ColumnSetting column in defaults)
        {
            if (!result.Exists(c => c.Id == column.Id))
            {
                // 保存された一覧にない列（後のバージョンで増えた列）は、勝手に出さない。
                column.Visible = column.Id == PlaylistColumn.Title || (column.Visible && !anySaved);
                result.Add(column);
            }
        }

        RescaleProportional(result);
        return result;
    }

    /// <summary>
    /// 表示中の列の境目をドラッグしたときの幅を求める。境目の左右の 2 列の間で幅をやり取りし、他の列の幅は変えない。
    /// </summary>
    /// <param name="actualWidths">ドラッグを始めたときの、各列の実際の幅（ピクセル）。並びは <paramref name="columns"/> と同じ。</param>
    /// <param name="left">境目の左の列の位置。右の列は、その次の表示中の列。</param>
    /// <param name="delta">境目を動かした量（ピクセル）。右が正。</param>
    /// <returns>幅を変えたか。右に表示中の列がない場合などは変えない。</returns>
    public static bool Resize(IReadOnlyList<ColumnSetting> columns, IReadOnlyList<double> actualWidths, int left, double delta)
    {
        if (left < 0 || left >= columns.Count || actualWidths.Count != columns.Count || !columns[left].Visible || !double.IsFinite(delta))
        {
            return false;
        }
        int right = NextVisible(columns, left);
        if (right < 0)
        {
            return false;
        }

        double pair = actualWidths[left] + actualWidths[right];
        if (!double.IsFinite(pair) || pair < MinWidth * 2)
        {
            return false;
        }
        // ピクセルの列は、設定として読める上限を超えさせない（超えた幅は、次に読むときに壊れた値として既定に戻る）。
        double minLeft = IsProportional(columns[right].Id) ? MinWidth : Math.Max(MinWidth, pair - MaxPixelWidth);
        double maxLeft = IsProportional(columns[left].Id) ? pair - MinWidth : Math.Min(pair - MinWidth, MaxPixelWidth);
        if (minLeft > maxLeft)
        {
            return false;
        }
        double newLeft = Math.Clamp(actualWidths[left] + delta, minLeft, maxLeft);
        double newRight = pair - newLeft;

        // 比率の列は、表示中のものの比率を今のピクセルの幅に置き換える（どれか 1 つの比率だけを変えると、他の列の幅も動く）。
        // 隠している列の比率も、同じ単位にそろえる。
        double proportionalPixels = 0;
        double proportionalWeight = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Visible && IsProportional(columns[i].Id))
            {
                proportionalPixels += actualWidths[i];
                proportionalWeight += columns[i].Width;
            }
        }
        if (proportionalPixels <= 0 || proportionalWeight <= 0)
        {
            return false;
        }
        double pixelsPerWeight = proportionalPixels / proportionalWeight;

        for (int i = 0; i < columns.Count; i++)
        {
            ColumnSetting column = columns[i];
            double target = i == left ? newLeft : i == right ? newRight : actualWidths[i];
            if (!IsProportional(column.Id))
            {
                if (i == left || i == right)
                {
                    column.Width = target;
                }
            }
            else
            {
                column.Width = column.Visible ? Math.Max(target, 1) : column.Width * pixelsPerWeight;
            }
        }
        return true;
    }

    /// <summary>列を、別の列の前へ動かす。</summary>
    /// <param name="from">動かす列の位置。</param>
    /// <param name="before">この位置にある列の前へ入れる。末尾へ動かすときは列の数。</param>
    /// <returns>並びが変わったか。見た目の並びが変わらない動かし方（隠している列だけをまたぐ）はしない。</returns>
    public static bool Move(List<ColumnSetting> columns, int from, int before)
    {
        if (from < 0 || from >= columns.Count || before < 0 || before > columns.Count || before == from)
        {
            return false;
        }

        // 隠している列だけをまたいで動かすと、見た目は変わらないのに、後でその列を表示したときの位置が変わってしまう。
        bool crossesVisible = false;
        for (int i = Math.Min(from, before); i < Math.Max(from, before); i++)
        {
            crossesVisible |= i != from && columns[i].Visible;
        }
        if (!crossesVisible)
        {
            return false;
        }

        ColumnSetting column = columns[from];
        columns.RemoveAt(from);
        columns.Insert(before > from ? before - 1 : before, column);
        return true;
    }

    /// <summary>隠している列を表示して、<paramref name="before"/> の位置にある列の前へ入れる（末尾へは列の数）。</summary>
    /// <returns>表示や並びが変わったか。</returns>
    public static bool Show(List<ColumnSetting> columns, PlaylistColumn id, int before)
    {
        int from = columns.FindIndex(c => c.Id == id);
        if (from < 0)
        {
            return false;
        }

        bool shown = !columns[from].Visible;
        columns[from].Visible = true;

        // 隠している列だけをはさんで、すでに入れたい場所の隣にある場合は、動かさなくても見た目は同じになる。
        return Move(columns, from, before) || shown;
    }

    /// <summary>表示する列、幅、順番をすべて既定に戻す。</summary>
    public static void Reset(List<ColumnSetting> columns)
    {
        columns.Clear();
        columns.AddRange(CreateDefault());
    }

    /// <summary><paramref name="index"/> より前の、最後の表示中の列の位置。なければ -1。</summary>
    public static int PreviousVisible(IReadOnlyList<ColumnSetting> columns, int index)
    {
        for (int i = Math.Min(index, columns.Count) - 1; i >= 0; i--)
        {
            if (columns[i].Visible)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary><paramref name="index"/> より後ろの、最初の表示中の列の位置。なければ -1。</summary>
    public static int NextVisible(IReadOnlyList<ColumnSetting> columns, int index)
    {
        for (int i = index + 1; i < columns.Count; i++)
        {
            if (columns[i].Visible)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 比率の合計を一定にそろえる。幅を変えるたびに比率をピクセルの値で置き換えているので、
    /// 設定ファイルの値がウィンドウの大きさによって変わり続けないようにする。
    /// </summary>
    private static void RescaleProportional(List<ColumnSetting> columns)
    {
        const double Total = 100;
        double sum = 0;
        foreach (ColumnSetting column in columns)
        {
            if (IsProportional(column.Id))
            {
                sum += column.Width;
            }
        }
        if (!double.IsFinite(sum) || sum <= 0)
        {
            return;
        }
        foreach (ColumnSetting column in columns)
        {
            if (IsProportional(column.Id))
            {
                column.Width = Math.Max(column.Width * Total / sum, 0.01);
            }
        }
    }
}
