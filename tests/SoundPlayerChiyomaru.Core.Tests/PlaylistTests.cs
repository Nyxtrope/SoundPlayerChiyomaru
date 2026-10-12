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
        Assert.Equal("untagged,x1,x2,x3", Paths);

        playlist.Sort(PlaylistSortKey.Title);
        Assert.Equal("x1,x3,untagged,x2", Paths);
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
