using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;

namespace SoundPlayerChiyomaru.Core.Tests;

/// <summary>曲順の番号、シャッフル、リナンバリング。</summary>
public class PlaylistOrderTests : TempDirectoryTest
{
    private readonly Playlist playlist = new();

    private string Paths => string.Join(",", playlist.Items.Select(item => item.Path));

    private string Orders => string.Join(",", playlist.Items.Select(item => item.Order));

    private PlaylistItem Item(string path) => playlist.Items.First(item => item.Path == path);

    [Fact]
    public void 追加した曲には_どこに入れても最大の番号の続きを振る()
    {
        playlist.Add(["a", "b", "c"]);
        Assert.Equal("1,2,3", Orders);
        Assert.Equal("2", Item("b").OrderText);

        playlist.Insert(1, ["x", "y"]);

        Assert.Equal("a,x,y,b,c", Paths);
        Assert.Equal("1,4,5,2,3", Orders);
    }

    [Fact]
    public void 削除や移動や並べ替えでは番号が変わらず_番号順に並べ替えると元の順に戻る()
    {
        playlist.Add(["c", "a", "d", "b"]);

        playlist.Remove([Item("a")]);
        playlist.Move([Item("b")], 0);
        Assert.Equal("b,c,d", Paths);
        Assert.Equal("4,1,3", Orders);

        // 削除で空いた番号は詰めない。追加は最大の続きから。
        playlist.Add(["e"]);
        Assert.Equal("4,1,3,5", Orders);

        // すでにパスの昇順に並んでいるので、降順になる。
        playlist.Sort(PlaylistSortKey.Path);
        Assert.Equal("e,d,c,b", Paths);
        playlist.Sort(PlaylistSortKey.Order);
        Assert.Equal("c,d,b,e", Paths);
        Assert.Equal("1,3,4,5", Orders);
    }

    [Fact]
    public void すべて削除した後に追加すると_番号は1から始まる()
    {
        playlist.Add(["a", "b"]);
        playlist.Clear();

        playlist.Add(["c"]);

        Assert.Equal("1", Orders);
    }

    [Fact]
    public void シャッフルは並びだけを変え_番号は曲に付いたまま_指定した曲が先頭に来る()
    {
        playlist.Add(Enumerable.Range(0, 50).Select(i => $"t{i:00}"));
        var orders = playlist.Items.ToDictionary(item => item.Path, item => item.Order);
        PlaylistItem first = Item("t30");

        playlist.Shuffle(new Random(7), first);

        Assert.Same(first, playlist.Items[0]);
        Assert.NotEqual(string.Join(",", orders.Keys), Paths);
        Assert.Equal(orders.Keys.Order(), playlist.Items.Select(item => item.Path).Order());
        Assert.All(playlist.Items, item => Assert.Equal(orders[item.Path], item.Order));

        playlist.Undo();
        Assert.Equal(string.Join(",", orders.Keys), Paths);
        Assert.All(playlist.Items, item => Assert.Equal(orders[item.Path], item.Order));
    }

    [Fact]
    public void シャッフルは_先頭に置く曲がなくてもプレイリストになくても動く()
    {
        playlist.Shuffle(new Random(1), null);
        Assert.False(playlist.CanUndo);

        playlist.Add(["a", "b", "c", "d", "e", "f"]);
        playlist.Shuffle(new Random(1), new PlaylistItem("x"));
        playlist.Shuffle(new Random(2), null);

        Assert.Equal("a,b,c,d,e,f", string.Join(",", playlist.Items.Select(item => item.Path).Order()));
    }

    [Fact]
    public void リナンバリングは今の並びで1から振り直し_元に戻せる()
    {
        playlist.Add(["a", "b", "c"]);
        playlist.Move([Item("c")], 0);
        int changed = 0;
        playlist.Changed += (_, _) => changed++;
        var notified = new List<string?>();
        Item("c").PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        playlist.Renumber();

        Assert.Equal("c,a,b", Paths);
        Assert.Equal("1,2,3", Orders);
        Assert.Equal(1, changed);
        Assert.Contains(nameof(PlaylistItem.OrderText), notified);

        playlist.Undo();
        Assert.Equal("c,a,b", Paths);
        Assert.Equal("3,1,2", Orders);
        playlist.Redo();
        Assert.Equal("1,2,3", Orders);
    }

    [Fact]
    public void 番号が並びのとおりなら_リナンバリングは何もしない()
    {
        playlist.Add(["a", "b"]);
        int changed = 0;
        playlist.Changed += (_, _) => changed++;

        playlist.Renumber();
        Assert.Equal(0, changed);

        // 元に戻す対象にもなっていないので、元に戻すと追加の前に戻る。
        playlist.Undo();
        Assert.Equal("", Paths);
    }

    [Fact]
    public void 追加を元に戻してやり直しても_番号は同じ()
    {
        playlist.Add(["a", "b"]);
        playlist.Add(["c"]);

        playlist.Undo();
        playlist.Add(["d"]);
        Assert.Equal("1,2,3", Orders);
        playlist.Undo();
        playlist.Undo();
        playlist.Redo();

        Assert.Equal("a,b", Paths);
        Assert.Equal("1,2", Orders);
    }

    [Fact]
    public void 読み込みでは_書かれた番号を使い_書かれていない曲には最大の続きを振る()
    {
        playlist.Load([new PlaylistEntry("a"), new PlaylistEntry("b")], null);
        Assert.Equal("1,2", Orders);

        playlist.Load(["a", "b", "c"], null);
        Assert.Equal("1,2,3", Orders);

        playlist.Load([new PlaylistEntry("a", 7), new PlaylistEntry("b"), new PlaylistEntry("c", 2), new PlaylistEntry("d", -4)], null);
        Assert.Equal("7,8,2,9", Orders);

        playlist.Load([new PlaylistEntry("a", int.MaxValue), new PlaylistEntry("b")], null);
        Assert.Equal($"{int.MaxValue},{int.MaxValue}", Orders);
        playlist.Add(["c"]);
        Assert.All(playlist.Items, item => Assert.True(item.Order > 0));
    }

    [Fact]
    public void 番号が並びとずれているときだけファイルに書き_読み戻せる()
    {
        string file = At("list.m3u8");
        playlist.Add([@"C:\a.mp3", @"C:\b.mp3", @"C:\c.mp3"]);

        PlaylistFile.Write(file, playlist.Items, relativePaths: false);
        Assert.DoesNotContain("CHIYOMARU", File.ReadAllText(file));
        Assert.All(PlaylistFile.ReadEntries(file), entry => Assert.Null(entry.Order));

        playlist.Move([playlist.Items[2]], 0);
        PlaylistFile.Write(file, playlist.Items, relativePaths: false);

        Assert.Equal(
            ["#EXTM3U", "#CHIYOMARU-ORDER:3", @"C:\c.mp3", "#CHIYOMARU-ORDER:1", @"C:\a.mp3", "#CHIYOMARU-ORDER:2", @"C:\b.mp3"],
            File.ReadAllLines(file));
        Assert.Equal(
            [new PlaylistEntry(@"C:\c.mp3", 3), new PlaylistEntry(@"C:\a.mp3", 1), new PlaylistEntry(@"C:\b.mp3", 2)],
            PlaylistFile.ReadEntries(file));
        Assert.Equal([@"C:\c.mp3", @"C:\a.mp3", @"C:\b.mp3"], PlaylistFile.Read(file));

        var loaded = new Playlist();
        loaded.Load(PlaylistFile.ReadEntries(file), file);
        Assert.Equal([3, 1, 2], loaded.Items.Select(item => item.Order));
    }

    [Fact]
    public void 壊れた番号の行は無視し_番号は次の曲の行だけにかかる()
    {
        string file = At("list.m3u8");
        File.WriteAllText(file, string.Join('\n',
            "#EXTM3U",
            "#CHIYOMARU-ORDER:5",
            "#EXTINF:10,title",
            @"C:\a.mp3",
            @"C:\b.mp3",
            "#CHIYOMARU-ORDER:abc",
            @"C:\c.mp3",
            "#CHIYOMARU-ORDER:-3",
            @"C:\d.mp3",
            "#CHIYOMARU-ORDER:99999999999999",
            @"C:\e.mp3",
            "#CHIYOMARU-ORDER:8",
            "http://example.com/stream",
            @"C:\f.mp3"));

        Assert.Equal(
            [
                new PlaylistEntry(@"C:\a.mp3", 5), new PlaylistEntry(@"C:\b.mp3"), new PlaylistEntry(@"C:\c.mp3"),
                new PlaylistEntry(@"C:\d.mp3"), new PlaylistEntry(@"C:\e.mp3"), new PlaylistEntry(@"C:\f.mp3"),
            ],
            PlaylistFile.ReadEntries(file));
    }
}

public class PlaylistDuplicatesTests
{
    [Fact]
    public void すでにある曲と_追加する中で重なる曲を除き_順番は保つ()
    {
        var playlist = new Playlist();
        playlist.Add([@"C:\a.mp3", @"C:\b.mp3"]);

        IReadOnlyList<string> result = PlaylistDuplicates.Exclude(
            [@"C:\c.mp3", @"C:\A.MP3", @"C:\d.mp3", @"C:\c.mp3", @"C:\b.mp3", @"C:\D.mp3", @"C:\e.mp3"],
            playlist.Items);

        Assert.Equal([@"C:\c.mp3", @"C:\d.mp3", @"C:\e.mp3"], result);
    }

    [Fact]
    public void 何もないプレイリストへは_追加する中の重なりだけを除く()
    {
        Assert.Equal(["a", "b"], PlaylistDuplicates.Exclude(["a", "b", "a"], []));
        Assert.Empty(PlaylistDuplicates.Exclude([], []));
    }
}

/// <summary>「次にこの曲から再生」。</summary>
public class PlayAfterCurrentTests
{
    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(10);

    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly Playlist playlist = new();
    private readonly PlaybackService playback;
    private readonly PlaylistPlayer player;

    public PlayAfterCurrentTests()
    {
        opener.Create = path => path.StartsWith("48k")
            ? new FakeDecoder(TrackLength) { WaveFormat = new WaveFormat(48000, 16, 2) }
            : new FakeDecoder(TrackLength);
        playback = new PlaybackService(opener, outputs, context: null);
        player = new PlaylistPlayer(playlist, playback, new Random(1));
    }

    private PlaylistItem Item(string path) => playlist.Items.First(item => item.Path == path);

    private string? Current => player.Current?.Path;

    private void PlayToEnd()
    {
        FakeOutput output = outputs.Last;
        TimeSpan remaining = playback.Duration - playback.Position;
        float[] samples = output.Read(remaining + TimeSpan.FromSeconds(1));
        if (samples.Length < (int)((remaining.TotalSeconds + 1) * playback.SourceFormat!.SampleRate) * 2)
        {
            output.RaiseStopped(null);
        }
    }

    [Theory]
    [InlineData(PlayMode.Normal)]
    [InlineData(PlayMode.RepeatAll)]
    [InlineData(PlayMode.RepeatOne)]
    [InlineData(PlayMode.OneShot)]
    [InlineData(PlayMode.Random)]
    public void 今の曲が終わったら_再生モードに関係なく指定した曲へ出力を止めずに進む(PlayMode mode)
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        player.Mode = mode;
        player.Play(Item("a"));

        player.PlayAfterCurrent(Item("d"));
        Assert.Equal("a", Current);
        PlayToEnd();

        Assert.Equal("d", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 進んだ後は_その曲からプレイリストの順に続く()
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        player.Play(Item("a"));
        player.PlayAfterCurrent(Item("d"));

        PlayToEnd();
        PlayToEnd();

        Assert.Equal("e", Current);
    }

    [Fact]
    public void 形式が違って出力を作り直す曲へも進む()
    {
        playlist.Add(["a", "b", "48k-c"]);
        player.Play(Item("a"));
        player.PlayAfterCurrent(Item("48k-c"));

        PlayToEnd();

        Assert.Equal("48k-c", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 次の曲の操作でも指定した曲へ進み_その後は順に戻る()
    {
        playlist.Add(["a", "b", "c", "d"]);
        player.Play(Item("a"));
        player.PlayAfterCurrent(Item("c"));

        player.Next();
        Assert.Equal("c", Current);
        player.Next();
        Assert.Equal("d", Current);
    }

    [Fact]
    public void 別の曲を自分で選んだら_指定は取り消される()
    {
        playlist.Add(["a", "b", "c", "d"]);
        player.Play(Item("a"));
        player.PlayAfterCurrent(Item("d"));

        player.Play(Item("b"));
        PlayToEnd();

        Assert.Equal("c", Current);
    }

    [Fact]
    public void 指定した曲が削除されたら_指定は取り消される()
    {
        playlist.Add(["a", "b", "c", "d"]);
        player.Play(Item("a"));
        player.PlayAfterCurrent(Item("d"));

        playlist.Remove([Item("d")]);
        PlayToEnd();

        Assert.Equal("b", Current);
    }

    [Fact]
    public void 何も開いていなければ_すぐに再生する()
    {
        playlist.Add(["a", "b", "c"]);

        player.PlayAfterCurrent(Item("b"));

        Assert.Equal("b", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 一時停止中に指定しても_再開して曲が終わってから進む()
    {
        playlist.Add(["a", "b", "c"]);
        player.Play(Item("a"));
        player.PlayPause();

        player.PlayAfterCurrent(Item("c"));
        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Paused, playback.State);
        player.PlayPause();
        PlayToEnd();

        Assert.Equal("c", Current);
    }
}
