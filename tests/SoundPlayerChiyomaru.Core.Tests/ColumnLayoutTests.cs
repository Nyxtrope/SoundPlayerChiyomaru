using SoundPlayerChiyomaru.Core.Settings;
using Xunit;

namespace SoundPlayerChiyomaru.Core.Tests;

public class ColumnLayoutTests : TempDirectoryTest
{
    private static ColumnSetting Column(PlaylistColumn id, double width, bool visible = true) =>
        new() { Id = id, Width = width, Visible = visible };

    /// <summary>
    /// 幅と順番の計算を確かめるための並び:（トラック番号）タイトル アーティスト アルバム（アルバムアーティスト）時間 ファイル名（曲順）。
    /// かっこは隠している列。
    /// </summary>
    private static List<ColumnSetting> Classic() => ColumnLayout.Normalize(
    [
        Column(PlaylistColumn.TrackNumber, 36, visible: false),
        Column(PlaylistColumn.Title, 3),
        Column(PlaylistColumn.Artist, 2),
        Column(PlaylistColumn.Album, 2),
        Column(PlaylistColumn.AlbumArtist, 2, visible: false),
        Column(PlaylistColumn.Duration, 60),
        Column(PlaylistColumn.FileName, 2),
    ]);

    private static PlaylistColumn[] Order(IEnumerable<ColumnSetting> columns) => [.. columns.Select(c => c.Id)];

    /// <summary>グリッドと同じ計算で、ウィンドウの幅から各列の実際の幅を求める。</summary>
    private static double[] Layout(IReadOnlyList<ColumnSetting> columns, double total)
    {
        double pixels = columns.Where(c => c.Visible && !ColumnLayout.IsProportional(c.Id)).Sum(c => c.Width);
        double weight = columns.Where(c => c.Visible && ColumnLayout.IsProportional(c.Id)).Sum(c => c.Width);
        return [.. columns.Select(c => !c.Visible ? 0 : ColumnLayout.IsProportional(c.Id) ? (total - pixels) * c.Width / weight : c.Width)];
    }

    [Fact]
    public void 既定では曲順を左端に出し_トラック番号とアルバムアーティストを隠し_全種類の列を持つ()
    {
        List<ColumnSetting> columns = ColumnLayout.CreateDefault();

        Assert.Equal(Enum.GetValues<PlaylistColumn>().Order(), Order(columns).Order());
        Assert.Equal(
            [
                PlaylistColumn.Order, PlaylistColumn.Title, PlaylistColumn.Artist, PlaylistColumn.Album,
                PlaylistColumn.Duration, PlaylistColumn.FileName,
            ],
            Order(columns.Where(c => c.Visible)));
        Assert.All(
            [PlaylistColumn.Order, PlaylistColumn.TrackNumber, PlaylistColumn.Duration],
            id => Assert.False(ColumnLayout.IsProportional(id)));
    }

    [Fact]
    public void 保存がなければ既定になる()
    {
        Assert.Equal(Order(ColumnLayout.CreateDefault()), Order(ColumnLayout.Normalize(null)));
        Assert.Equal(
            ColumnLayout.CreateDefault().Select(c => c.Visible),
            ColumnLayout.Normalize([]).Select(c => c.Visible));
    }

    [Fact]
    public void 読んだ内容の順番と表示を保ち_重複や知らない列は捨て_足りない列は隠して末尾に足す()
    {
        List<ColumnSetting> columns = ColumnLayout.Normalize(
        [
            Column(PlaylistColumn.Duration, 80),
            null,
            Column(PlaylistColumn.Title, 5, visible: false),
            Column(PlaylistColumn.Duration, 99),
            Column((PlaylistColumn)99, 5),
            Column(PlaylistColumn.Artist, 5, visible: false),
        ]);

        Assert.Equal(
            [
                PlaylistColumn.Duration, PlaylistColumn.Title, PlaylistColumn.Artist, PlaylistColumn.Order,
                PlaylistColumn.TrackNumber, PlaylistColumn.Album, PlaylistColumn.AlbumArtist, PlaylistColumn.FileName,
            ],
            Order(columns));
        Assert.Equal(80, columns[0].Width);

        // タイトルは隠せない。保存された一覧になかった列は、既定で表示する列でも出さない。
        Assert.Equal([PlaylistColumn.Duration, PlaylistColumn.Title], Order(columns.Where(c => c.Visible)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    [InlineData(1e-310)]
    public void 壊れた幅は既定に戻す(double width)
    {
        List<ColumnSetting> columns = ColumnLayout.Normalize(
            [Column(PlaylistColumn.Duration, width), Column(PlaylistColumn.Title, width), Column(PlaylistColumn.Artist, 2)]);

        Assert.Equal(60, columns[0].Width);
        Assert.All(columns, c => Assert.True(double.IsFinite(c.Width) && c.Width > 0));

        // タイトルは既定の比率 3 に戻り、アーティストの 2 との比が保たれる。
        Assert.Equal(1.5, columns[1].Width / columns[2].Width, 6);
    }

    [Fact]
    public void 比率は合計をそろえて保存し_読み直しても変わらない()
    {
        List<ColumnSetting> first = ColumnLayout.Normalize(
            [Column(PlaylistColumn.Title, 600), Column(PlaylistColumn.Artist, 300), Column(PlaylistColumn.Duration, 70)]);
        List<ColumnSetting> second = ColumnLayout.Normalize(first);

        Assert.Equal(2, first[0].Width / first[1].Width, 6);
        Assert.Equal(70, first[2].Width);
        Assert.Equal(100, first.Where(c => ColumnLayout.IsProportional(c.Id)).Sum(c => c.Width), 6);
        Assert.All(first.Zip(second), pair => Assert.Equal(pair.First.Width, pair.Second.Width, 9));

        // 保存になかった列は、既定の比率で足す（既定はタイトル 3、アーティスト 2、アルバム 2）。
        Assert.Equal(2.0 / 5, first.Single(c => c.Id == PlaylistColumn.Album).Width / (first[0].Width + first[1].Width), 6);
    }

    [Fact]
    public void 比率の列どうしの境目を動かすと_その二列だけが変わる()
    {
        List<ColumnSetting> columns = Classic();
        double[] before = Layout(columns, 960);

        Assert.True(ColumnLayout.Resize(columns, before, left: 1, delta: 50));
        double[] after = Layout(columns, 960);

        Assert.Equal(before[1] + 50, after[1], 6);
        Assert.Equal(before[2] - 50, after[2], 6);
        Assert.Equal(before[3], after[3], 6);
        Assert.Equal(before[5], after[5], 6);
        Assert.Equal(before[6], after[6], 6);
    }

    [Fact]
    public void ピクセルの列と比率の列の境目を動かしても_他の列は変わらない()
    {
        List<ColumnSetting> columns = Classic();
        double[] before = Layout(columns, 960);

        // アルバム（比率）と時間（ピクセル）の境目。間のアルバムアーティストは隠れている。
        Assert.True(ColumnLayout.Resize(columns, before, left: 3, delta: -30));
        double[] after = Layout(columns, 960);

        Assert.Equal(before[3] - 30, after[3], 6);
        Assert.Equal(90, after[5], 6);
        Assert.Equal(before[1], after[1], 6);
        Assert.Equal(before[2], after[2], 6);
        Assert.Equal(before[6], after[6], 6);
    }

    [Fact]
    public void 隠している列の比率は_表示中の列との比を保つ()
    {
        List<ColumnSetting> columns = Classic();
        double ratio = columns[4].Width / columns[6].Width;

        Assert.True(ColumnLayout.Resize(columns, Layout(columns, 960), left: 1, delta: 50));

        Assert.Equal(ratio, columns[4].Width / columns[6].Width, 6);
    }

    [Theory]
    [InlineData(-10000)]
    [InlineData(10000)]
    public void 最小の幅より狭くはならない(double delta)
    {
        List<ColumnSetting> columns = Classic();
        double[] before = Layout(columns, 960);

        Assert.True(ColumnLayout.Resize(columns, before, left: 1, delta));
        double[] after = Layout(columns, 960);

        Assert.Equal(ColumnLayout.MinWidth, Math.Min(after[1], after[2]), 6);
        Assert.Equal(before[1] + before[2], after[1] + after[2], 6);
    }

    [Fact]
    public void 右に表示中の列がない境目や_隠れている列や_狭すぎる列は動かさない()
    {
        List<ColumnSetting> columns = Classic();
        double[] actual = Layout(columns, 960);
        double[] widths = [.. columns.Select(c => c.Width)];

        Assert.False(ColumnLayout.Resize(columns, actual, left: 6, delta: 10));
        Assert.False(ColumnLayout.Resize(columns, actual, left: 0, delta: 10));
        Assert.False(ColumnLayout.Resize(columns, actual, left: -1, delta: 10));
        Assert.False(ColumnLayout.Resize(columns, actual, left: 7, delta: 10));
        Assert.False(ColumnLayout.Resize(columns, actual, left: 1, delta: double.NaN));
        Assert.False(ColumnLayout.Resize(columns, [1, 2], left: 1, delta: 10));
        Assert.False(ColumnLayout.Resize(columns, new double[7], left: 1, delta: 10));

        Assert.Equal(widths, columns.Select(c => c.Width));
    }

    [Fact]
    public void ピクセルの列は読める上限を超えて広げられない()
    {
        List<ColumnSetting> columns = Classic();
        double[] before = Layout(columns, 12000);

        Assert.True(ColumnLayout.Resize(columns, before, left: 3, delta: -5000));
        double[] after = Layout(columns, 12000);

        Assert.Equal(2000, after[5], 6);
        Assert.Equal(before[3] + before[5], after[3] + after[5], 6);
        Assert.Equal(2000, ColumnLayout.Normalize(columns).Single(c => c.Id == PlaylistColumn.Duration).Width, 6);
    }

    [Fact]
    public void 隠している列だけをまたぐ動かし方はしない()
    {
        // アルバム（3）の次のアルバムアーティスト（4）は隠れていて、その次が時間（5）。
        List<ColumnSetting> columns = Classic();
        PlaylistColumn[] order = Order(columns);

        Assert.False(ColumnLayout.Move(columns, 3, 5));
        Assert.False(ColumnLayout.Move(columns, 5, 4));
        Assert.False(ColumnLayout.Move(columns, 1, 0));
        Assert.Equal(order, Order(columns));

        Assert.True(ColumnLayout.Move(columns, 3, 6));
        Assert.Equal(PlaylistColumn.Album, columns[5].Id);
    }

    [Fact]
    public void 隠している列を_指定した列の左や右に出せる()
    {
        // 既定: (トラック番号) タイトル アーティスト アルバム (アルバムアーティスト) 時間 ファイル名
        List<ColumnSetting> columns = Classic();

        // トラック番号を、時間（位置 5）の右へ。
        Assert.True(ColumnLayout.Show(columns, PlaylistColumn.TrackNumber, 6));
        Assert.Equal(
            [PlaylistColumn.Title, PlaylistColumn.Artist, PlaylistColumn.Album, PlaylistColumn.Duration, PlaylistColumn.TrackNumber, PlaylistColumn.FileName],
            Order(columns.Where(c => c.Visible)));

        // アルバムアーティストは、隠れたままアルバムのすぐ右にある。アルバムの右へ出すときは、動かさずに表示するだけでよい。
        int album = columns.FindIndex(c => c.Id == PlaylistColumn.Album);
        Assert.True(ColumnLayout.Show(columns, PlaylistColumn.AlbumArtist, album + 1));
        Assert.Equal(
            [
                PlaylistColumn.Title, PlaylistColumn.Artist, PlaylistColumn.Album, PlaylistColumn.AlbumArtist,
                PlaylistColumn.Duration, PlaylistColumn.TrackNumber, PlaylistColumn.FileName,
            ],
            Order(columns.Where(c => c.Visible)));

        // すでに表示していて場所も同じなら、何も変わらない。
        Assert.False(ColumnLayout.Show(columns, PlaylistColumn.AlbumArtist, album + 1));
        Assert.False(ColumnLayout.Show(columns, (PlaylistColumn)99, 0));
    }

    [Fact]
    public void 前後の表示中の列を探せる()
    {
        List<ColumnSetting> columns = Classic();

        Assert.Equal(-1, ColumnLayout.PreviousVisible(columns, 1));
        Assert.Equal(3, ColumnLayout.PreviousVisible(columns, 5));
        Assert.Equal(6, ColumnLayout.PreviousVisible(columns, 99));
        Assert.Equal(5, ColumnLayout.NextVisible(columns, 3));
        Assert.Equal(-1, ColumnLayout.NextVisible(columns, 6));
    }

    [Fact]
    public void 既定に戻すと_表示する列も幅も順番も既定になる()
    {
        List<ColumnSetting> columns = Classic();
        columns[0].Visible = true;
        columns[2].Visible = false;
        columns[1].Width = 77;
        ColumnLayout.Move(columns, 6, 0);

        ColumnLayout.Reset(columns);

        List<ColumnSetting> defaults = ColumnLayout.CreateDefault();
        Assert.Equal(Order(defaults), Order(columns));
        Assert.Equal(defaults.Select(c => c.Width), columns.Select(c => c.Width));
        Assert.Equal(defaults.Select(c => c.Visible), columns.Select(c => c.Visible));
        Assert.Equal(
            [PlaylistColumn.Order, PlaylistColumn.Title, PlaylistColumn.Artist, PlaylistColumn.Album, PlaylistColumn.Duration, PlaylistColumn.FileName],
            Order(columns.Where(c => c.Visible)));
    }

    [Theory]
    [InlineData(0, 3, new[] { 1, 2, 0, 3, 4 })]
    [InlineData(0, 5, new[] { 1, 2, 3, 4, 0 })]
    [InlineData(4, 0, new[] { 4, 0, 1, 2, 3 })]
    [InlineData(3, 1, new[] { 0, 3, 1, 2, 4 })]
    public void 列を別の列の前へ動かせる(int from, int before, int[] expected)
    {
        List<ColumnSetting> columns = [.. Enumerable.Range(0, 5).Select(i => Column((PlaylistColumn)i, 1))];

        Assert.True(ColumnLayout.Move(columns, from, before));

        Assert.Equal(expected, columns.Select(c => (int)c.Id));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(-1, 2)]
    [InlineData(5, 2)]
    [InlineData(2, 6)]
    [InlineData(2, -1)]
    public void 同じ場所や範囲外へは動かさない(int from, int before)
    {
        List<ColumnSetting> columns = [.. Enumerable.Range(0, 5).Select(i => Column((PlaylistColumn)i, 1))];

        Assert.False(ColumnLayout.Move(columns, from, before));

        Assert.Equal([0, 1, 2, 3, 4], columns.Select(c => (int)c.Id));
    }

    [Fact]
    public void 列の設定を保存して読み戻せる()
    {
        var store = new SettingsStore(Root);
        List<ColumnSetting> columns = Classic();
        ColumnLayout.Move(columns, 5, 0);
        columns[0].Width = 72.5;

        store.Save(new AppSettings { Columns = columns });
        List<ColumnSetting>? loaded = new SettingsStore(Root).Load().Columns;

        Assert.NotNull(loaded);
        Assert.Equal(Order(columns), Order(loaded));
        Assert.Equal(columns.Select(c => (c.Width, c.Visible)), loaded.Select(c => (c.Width, c.Visible)));
        Assert.Contains("\"Duration\"", File.ReadAllText(At("settings.json")));
        Assert.Null(new AppSettings().Columns);
    }
}
