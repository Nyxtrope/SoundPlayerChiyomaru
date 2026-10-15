using System.Collections.Specialized;
using SoundPlayerChiyomaru.Core.Playlists;

namespace SoundPlayerChiyomaru.Core.Tests;

public class PlaylistTests
{
    private readonly Playlist playlist = new();
    private int changed;

    public PlaylistTests()
    {
        playlist.Changed += (_, _) => changed++;
    }

    private string Paths => string.Join(",", playlist.Items.Select(item => item.Path));

    private PlaylistItem[] Pick(params string[] paths) =>
        paths.Select(path => playlist.Items.First(item => item.Path == path)).ToArray();

    [Fact]
    public void 追加は末尾に入り_追加した項目を返す()
    {
        playlist.Add(["a", "b"]);
        IReadOnlyList<PlaylistItem> added = playlist.Add(["c"]);

        Assert.Equal("a,b,c", Paths);
        Assert.Equal("c", Assert.Single(added).Path);
        Assert.Equal(2, changed);
    }

    [Theory]
    [InlineData(1, "a,x,y,b")]
    [InlineData(0, "x,y,a,b")]
    [InlineData(-5, "x,y,a,b")]
    [InlineData(99, "a,b,x,y")]
    public void 挿入位置は範囲内に丸められる(int index, string expected)
    {
        playlist.Add(["a", "b"]);

        playlist.Insert(index, ["x", "y"]);

        Assert.Equal(expected, Paths);
    }

    [Fact]
    public void 空の追加は何も変えず_元に戻す対象にもならない()
    {
        playlist.Add([]);

        Assert.Equal(0, changed);
        Assert.False(playlist.CanUndo);
    }

    [Fact]
    public void 同じファイルを複数回入れても_指定した行だけを削除する()
    {
        playlist.Add(["a", "b", "a"]);

        playlist.Remove([playlist.Items[2]]);

        Assert.Equal("a,b", Paths);
    }

    [Theory]
    [InlineData(new[] { "a" }, 3, "b,c,a,d,e")]
    [InlineData(new[] { "d" }, 1, "a,d,b,c,e")]
    [InlineData(new[] { "b", "d" }, 0, "b,d,a,c,e")]
    [InlineData(new[] { "b", "d" }, 5, "a,c,e,b,d")]
    [InlineData(new[] { "d", "b" }, 3, "a,c,b,d,e")]
    [InlineData(new[] { "b", "c" }, 99, "a,d,e,b,c")]
    public void 移動は元の並び順を保ってまとめて行う(string[] targets, int insertIndex, string expected)
    {
        playlist.Add(["a", "b", "c", "d", "e"]);

        playlist.Move(Pick(targets), insertIndex);

        Assert.Equal(expected, Paths);
    }

    [Fact]
    public void 並びが変わらない移動や削除は_変更として扱わない()
    {
        playlist.Add(["a", "b", "c"]);
        changed = 0;

        playlist.Move(Pick("b"), 1);
        playlist.Move(Pick("b"), 2);
        playlist.Remove([]);
        playlist.Remove([new PlaylistItem("other")]);

        Assert.Equal(0, changed);
        playlist.Undo();
        Assert.Equal("", Paths);
    }

    [Theory]
    [InlineData(PlaylistMove.Up, new[] { "c" }, "a,c,b,d,e")]
    [InlineData(PlaylistMove.Up, new[] { "c", "d" }, "a,c,d,b,e")]
    [InlineData(PlaylistMove.Up, new[] { "e", "c" }, "a,c,e,b,d")]
    [InlineData(PlaylistMove.Up, new[] { "a", "c" }, "a,c,b,d,e")]
    [InlineData(PlaylistMove.Down, new[] { "c" }, "a,b,d,c,e")]
    [InlineData(PlaylistMove.Down, new[] { "b", "c" }, "a,d,b,c,e")]
    [InlineData(PlaylistMove.Down, new[] { "a", "c" }, "b,d,a,c,e")]
    [InlineData(PlaylistMove.Down, new[] { "c", "e" }, "a,b,d,c,e")]
    [InlineData(PlaylistMove.Top, new[] { "c", "e" }, "c,e,a,b,d")]
    [InlineData(PlaylistMove.Bottom, new[] { "a", "c" }, "b,d,e,a,c")]
    [InlineData(PlaylistMove.AfterCurrent, new[] { "a" }, "b,a,c,d,e")]
    [InlineData(PlaylistMove.AfterCurrent, new[] { "e", "d" }, "a,b,d,e,c")]
    [InlineData(PlaylistMove.AfterCurrent, new[] { "a", "b", "d" }, "b,a,d,c,e")]
    public void 行き先を指定した移動は_選んだ曲をまとめて動かす(PlaylistMove move, string[] targets, string expected)
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        PlaylistItem current = Pick("b")[0];

        Assert.True(playlist.CanMove(Pick(targets), move, current));
        Assert.True(playlist.Move(Pick(targets), move, current));

        Assert.Equal(expected, Paths);
        Assert.Equal("1,2,3,4,5", string.Join(",", playlist.Items.OrderBy(item => item.Path).Select(item => item.Order)));
        playlist.Undo();
        Assert.Equal("a,b,c,d,e", Paths);
    }

    [Theory]
    [InlineData(PlaylistMove.Up, new[] { "a" })]
    [InlineData(PlaylistMove.Up, new[] { "a", "b" })]
    [InlineData(PlaylistMove.Top, new[] { "a", "b" })]
    [InlineData(PlaylistMove.Down, new[] { "e" })]
    [InlineData(PlaylistMove.Down, new[] { "d", "e" })]
    [InlineData(PlaylistMove.Bottom, new[] { "d", "e" })]
    [InlineData(PlaylistMove.AfterCurrent, new[] { "b" })]
    [InlineData(PlaylistMove.AfterCurrent, new[] { "c" })]
    [InlineData(PlaylistMove.AfterCurrent, new[] { "b", "c", "d" })]
    [InlineData(PlaylistMove.Up, new string[0])]
    [InlineData(PlaylistMove.Bottom, new string[0])]
    public void 並びが変わらない行き先への移動は_できない扱いにして_変更にもしない(PlaylistMove move, string[] targets)
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        PlaylistItem current = Pick("b")[0];
        changed = 0;

        Assert.False(playlist.CanMove(Pick(targets), move, current));
        Assert.False(playlist.Move(Pick(targets), move, current));

        Assert.Equal(0, changed);
        Assert.Equal("a,b,c,d,e", Paths);
    }

    [Fact]
    public void 再生中の曲がないか_プレイリストにないときは_その次へは動かせない()
    {
        playlist.Add(["a", "b", "c"]);

        Assert.False(playlist.CanMove(Pick("c"), PlaylistMove.AfterCurrent, null));
        Assert.False(playlist.Move(Pick("c"), PlaylistMove.AfterCurrent, new PlaylistItem("other")));
        Assert.False(playlist.Move([new PlaylistItem("other")], PlaylistMove.Top, null));

        Assert.Equal("a,b,c", Paths);
    }

    [Fact]
    public void ファイル名の並べ替えは数字を数値として比べる()
    {
        playlist.Add([@"C:\m\10.mp3", @"C:\m\2.mp3", @"C:\m\1.mp3", @"C:\m\B.mp3", @"C:\m\a.mp3"]);

        playlist.Sort(PlaylistSortKey.FileName);

        Assert.Equal("1.mp3,2.mp3,10.mp3,a.mp3,B.mp3", string.Join(",", playlist.Items.Select(i => i.FileName)));
    }

    [Fact]
    public void アルバムの並べ替えはトラック番号順になり_タグのない曲はタイトル順でファイル名を使う()
    {
        playlist.Add(["x3", "x1", "x2", "untagged"]);
        playlist.Items[0].ApplyInfo(new TrackInfo("C", "Artist", "Album", 3, null), exists: true);
        playlist.Items[1].ApplyInfo(new TrackInfo("B", "Artist", "Album", 1, null), exists: true);
        playlist.Items[2].ApplyInfo(new TrackInfo("Z", "Artist", "Album", 2, null), exists: true);

        playlist.Sort(PlaylistSortKey.Album);
        Assert.Equal("x1,x2,x3,untagged", Paths);

        playlist.Sort(PlaylistSortKey.Title);
        Assert.Equal("x1,x3,untagged,x2", Paths);
    }

    /// <summary>a〜e にタグを付ける。e はタグなし（値のない曲）。</summary>
    private void AddTagged()
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        playlist.Items[0].ApplyInfo(new TrackInfo("T1", "Beta", "Album Y", 2, TimeSpan.FromSeconds(200), "AA2"), exists: true);
        playlist.Items[1].ApplyInfo(new TrackInfo("T2", "Alpha", "Album X", 1, TimeSpan.FromSeconds(100), "AA1"), exists: true);
        playlist.Items[2].ApplyInfo(new TrackInfo("T3", "Beta", "Album Y", 1, TimeSpan.FromSeconds(300), "AA2"), exists: true);
        playlist.Items[3].ApplyInfo(new TrackInfo("T4", "Alpha", "Album X", 2, null, "AA1"), exists: true);
    }

    [Theory]
    [InlineData(PlaylistSortKey.Artist, "b,d,c,a,e", "c,a,b,d,e")]
    [InlineData(PlaylistSortKey.Album, "b,d,c,a,e", "c,a,b,d,e")]
    [InlineData(PlaylistSortKey.AlbumArtist, "b,d,c,a,e", "c,a,b,d,e")]
    [InlineData(PlaylistSortKey.TrackNumber, "b,c,d,a,e", "d,a,b,c,e")]
    [InlineData(PlaylistSortKey.Duration, "b,a,c,d,e", "c,a,b,d,e")]
    public void 同じ鍵でもう一度並べ替えると降順になり_逆になるのは最初の鍵だけで_値のない曲は常に最後(
        PlaylistSortKey key, string ascending, string descending)
    {
        AddTagged();

        Assert.Equal(new PlaylistSortState(key, false), playlist.Sort(key));
        Assert.Equal(ascending, Paths);
        Assert.Equal(new PlaylistSortState(key, true), playlist.Sort(key));
        Assert.Equal(descending, Paths);
        Assert.Equal(new PlaylistSortState(key, false), playlist.Sort(key));
        Assert.Equal(ascending, Paths);
    }

    [Fact]
    public void 並べた後に崩れていれば_同じ鍵でも昇順で並べ直す()
    {
        playlist.Add(["c", "a", "b"]);
        playlist.Sort(PlaylistSortKey.FileName);
        playlist.Add(["0"]);

        Assert.False(playlist.Sort(PlaylistSortKey.FileName).Descending);
        Assert.Equal("0,a,b,c", Paths);
    }

    [Fact]
    public void 逆にしても変わらない並びは_昇順として扱う()
    {
        playlist.Add(["a"]);

        Assert.False(playlist.Sort(PlaylistSortKey.Title).Descending);
        Assert.False(playlist.Sort(PlaylistSortKey.Title).Descending);
    }

    [Fact]
    public void 並べ替えで並びが変わったときの通知では_新しい並べた状態を読める()
    {
        playlist.Add(["b", "a"]);
        playlist.Sort(PlaylistSortKey.Title);
        var seen = new List<PlaylistSortState?>();
        playlist.Changed += (_, _) => seen.Add(playlist.SortState);

        playlist.Sort(PlaylistSortKey.Path);
        playlist.Sort(PlaylistSortKey.Path);

        // a, b はパスでも昇順なので、1 回目は降順、2 回目は昇順になる。
        Assert.Equal([new PlaylistSortState(PlaylistSortKey.Path, true), new PlaylistSortState(PlaylistSortKey.Path, false)], seen);
    }

    [Fact]
    public void 曲がないときは_並べた状態にしない()
    {
        playlist.Sort(PlaylistSortKey.Title);

        Assert.Null(playlist.SortState);
    }

    [Fact]
    public void 並べた状態は_並びが変わらなくても記録され_編集や元に戻すで消える()
    {
        playlist.Add(["a"]);
        changed = 0;
        playlist.Sort(PlaylistSortKey.FileName);
        Assert.Equal(0, changed);
        Assert.Equal(new PlaylistSortState(PlaylistSortKey.FileName, false), playlist.SortState);

        playlist.Add(["c", "b"]);
        Assert.Null(playlist.SortState);
        playlist.Sort(PlaylistSortKey.FileName);
        playlist.Sort(PlaylistSortKey.FileName);
        Assert.Equal(new PlaylistSortState(PlaylistSortKey.FileName, true), playlist.SortState);
        playlist.Undo();
        Assert.Null(playlist.SortState);

        playlist.Sort(PlaylistSortKey.Path);
        playlist.Remove(Pick("b"));
        Assert.Null(playlist.SortState);

        playlist.Sort(PlaylistSortKey.Order);
        Assert.NotNull(playlist.SortState);
        playlist.Move(Pick("c"), 0);
        Assert.Null(playlist.SortState);
    }

    [Fact]
    public void 編集は順に元に戻せて_やり直せる()
    {
        playlist.Add(["a", "b", "c"]);
        playlist.Remove(Pick("b"));
        playlist.Move(Pick("c"), 0);
        playlist.Clear();

        playlist.Undo();
        Assert.Equal("c,a", Paths);
        playlist.Undo();
        Assert.Equal("a,c", Paths);
        playlist.Undo();
        Assert.Equal("a,b,c", Paths);
        playlist.Undo();
        Assert.Equal("", Paths);
        Assert.False(playlist.CanUndo);
        playlist.Undo();

        playlist.Redo();
        playlist.Redo();
        Assert.Equal("a,c", Paths);
        Assert.True(playlist.CanRedo);
    }

    [Fact]
    public void 元に戻すと同じ項目のオブジェクトが戻る()
    {
        playlist.Add(["a", "b"]);
        PlaylistItem b = playlist.Items[1];

        playlist.Remove([b]);
        playlist.Undo();

        Assert.Same(b, playlist.Items[1]);
    }

    [Fact]
    public void 元に戻した後に編集すると_やり直しはできなくなる()
    {
        playlist.Add(["a"]);
        playlist.Add(["b"]);
        playlist.Undo();

        playlist.Add(["c"]);

        Assert.False(playlist.CanRedo);
        Assert.Equal("a,c", Paths);
    }

    [Fact]
    public void 元に戻せるのは直近50回まで()
    {
        for (int i = 0; i < 60; i++)
        {
            playlist.Add([i.ToString()]);
        }

        int undone = 0;
        while (playlist.CanUndo)
        {
            playlist.Undo();
            undone++;
        }

        Assert.Equal(50, undone);
        Assert.Equal(10, playlist.Count);
    }

    [Fact]
    public void 読み込みは中身と保存先を入れ替え_編集履歴を引き継がない()
    {
        playlist.Add(["a"]);
        playlist.FilePath = "old.m3u8";

        playlist.Load(["x", "y"], "new.m3u8");

        Assert.Equal("x,y", Paths);
        Assert.Equal("new.m3u8", playlist.FilePath);
        Assert.False(playlist.CanUndo);
        Assert.False(playlist.CanRedo);
    }

    [Fact]
    public void 置き換えは元に戻せて_保存先も戻り_やり直せる()
    {
        playlist.Add(["a", "b"]);
        playlist.FilePath = "old.m3u8";
        PlaylistItem[] before = playlist.Items.ToArray();

        playlist.Open([new PlaylistEntry("x", 5), new PlaylistEntry("y")], "new.m3u8");
        Assert.Equal("x,y", Paths);
        Assert.Equal("new.m3u8", playlist.FilePath);
        Assert.Equal("5,6", string.Join(",", playlist.Items.Select(item => item.Order)));

        playlist.Undo();
        Assert.Equal(before, playlist.Items);
        Assert.Equal("old.m3u8", playlist.FilePath);
        Assert.Equal("1,2", string.Join(",", playlist.Items.Select(item => item.Order)));

        playlist.Redo();
        Assert.Equal("x,y", Paths);
        Assert.Equal("new.m3u8", playlist.FilePath);

        // 置き換えより前の編集も、続けて戻せる。
        playlist.Undo();
        Assert.Equal("a,b", Paths);
        playlist.Undo();
        Assert.Equal("", Paths);
    }

    [Fact]
    public void 置き換え以外の編集を戻しても_名前を付けて保存した保存先は変わらない()
    {
        playlist.Add(["a"]);
        playlist.Add(["b"]);
        playlist.FilePath = "saved-as.m3u8";

        playlist.Undo();

        Assert.Equal("a", Paths);
        Assert.Equal("saved-as.m3u8", playlist.FilePath);
    }

    [Fact]
    public void 置き換えを戻せるのは直近の3回までで_それより前の履歴も捨てる()
    {
        playlist.Add(["a"]);
        playlist.Add(["b"]);
        for (int i = 1; i <= 4; i++)
        {
            playlist.Open([new PlaylistEntry($"open{i}")], null);
            playlist.Add([$"add{i}"]);
        }

        var seen = new List<string>();
        while (playlist.CanUndo)
        {
            playlist.Undo();
            seen.Add(Paths);
        }

        // 最初の置き換え（open1）より前の a, b には戻れない。open1 を開いた直後までは戻れる。
        Assert.Equal(["open4", "open3,add3", "open3", "open2,add2", "open2", "open1,add1", "open1"], seen);
    }

    [Fact]
    public void 挿入は範囲の追加として_それ以外は全体の入れ替えとして通知する()
    {
        var actions = new List<(NotifyCollectionChangedAction Action, int Index, int Count)>();
        ((INotifyCollectionChanged)playlist.Items).CollectionChanged +=
            (_, e) => actions.Add((e.Action, e.NewStartingIndex, e.NewItems?.Count ?? 0));
        playlist.Add(["a", "b"]);

        playlist.Insert(1, ["x", "y", "z"]);
        playlist.Remove(Pick("x"));

        Assert.Equal(
            [
                (NotifyCollectionChangedAction.Add, 0, 2),
                (NotifyCollectionChangedAction.Add, 1, 3),
                (NotifyCollectionChangedAction.Reset, -1, 0),
            ],
            actions);
    }
}
