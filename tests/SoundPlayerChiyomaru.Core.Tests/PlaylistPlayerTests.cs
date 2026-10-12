using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;

namespace SoundPlayerChiyomaru.Core.Tests;

public class PlaylistPlayerTests
{
    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(10);

    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly Playlist playlist = new();
    private readonly PlaybackService playback;
    private readonly PlaylistPlayer player;
    private readonly List<string> failed = [];
    private readonly List<string?> currentLog = [];

    public PlaylistPlayerTests()
    {
        // "bad" で始まる曲は開けない。"48k" で始まる曲は形式が違うので、出力を止めずには続けられない。
        opener.Create = path => path switch
        {
            _ when path.StartsWith("bad") => throw new FileNotFoundException(path),
            _ when path.StartsWith("48k") => new FakeDecoder(TrackLength) { WaveFormat = new WaveFormat(48000, 16, 2) },
            _ => new FakeDecoder(TrackLength),
        };
        playback = new PlaybackService(opener, outputs, context: null);
        player = new PlaylistPlayer(playlist, playback, new Random(1));
        player.TrackFailed += (_, e) => failed.Add(e.Item.Path);
        player.CurrentChanged += (_, _) => currentLog.Add(player.Current?.Path);
    }

    private PlaylistItem Item(string path) => playlist.Items.First(item => item.Path == path);

    private string? Current => player.Current?.Path;

    /// <summary>今の曲を最後まで再生し、次の曲の頭まで進める。出力が止まった場合はその通知まで行う。</summary>
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

    [Fact]
    public void 通常モードは順に再生し_最後の曲で止まる()
    {
        playlist.Add(["a", "b", "c"]);
        player.Play(Item("a"));

        PlayToEnd();
        Assert.Equal("b", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
        PlayToEnd();
        Assert.Equal("c", Current);
        PlayToEnd();

        Assert.Equal("c", Current);
        Assert.Equal(PlayerState.Stopped, playback.State);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 全曲リピートは最後の曲の次に最初へ戻る()
    {
        playlist.Add(["a", "b"]);
        player.Mode = PlayMode.RepeatAll;
        player.Play(Item("b"));

        PlayToEnd();

        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 一曲リピートは同じ曲を出力を止めずに繰り返す()
    {
        playlist.Add(["a", "b"]);
        player.Mode = PlayMode.RepeatOne;
        player.Play(Item("a"));

        PlayToEnd();
        PlayToEnd();

        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal(1, playback.Position.TotalSeconds, 3);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void ワンショットは今の曲が終わったら止まる()
    {
        playlist.Add(["a", "b"]);
        player.Mode = PlayMode.OneShot;
        player.Play(Item("a"));

        PlayToEnd();

        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Stopped, playback.State);
    }

    [Fact]
    public void ランダムは止まらずに続き_同じ曲を続けて選ばない()
    {
        playlist.Add(["a", "b", "c", "d"]);
        player.Mode = PlayMode.Random;
        player.Play(Item("a"));
        var played = new List<string?> { Current };

        for (int i = 0; i < 40; i++)
        {
            PlayToEnd();
            played.Add(Current);
        }

        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.All(played.Zip(played.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.Equal(["a", "b", "c", "d"], played.Distinct().Order());
    }

    [Fact]
    public void 再生中にモードを変えると_次に続く曲が変わる()
    {
        playlist.Add(["a", "b"]);
        player.Play(Item("a"));

        player.Mode = PlayMode.OneShot;
        PlayToEnd();

        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Stopped, playback.State);
    }

    [Fact]
    public void 形式が違う曲へは_出力を作り直して進む()
    {
        playlist.Add(["a", "48k", "c"]);
        player.Play(Item("a"));

        PlayToEnd();

        Assert.Equal("48k", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal(48000, playback.SourceFormat!.SampleRate);
        Assert.Equal(2, outputs.Created.Count);
        Assert.True(outputs.Created[0].Disposed);
    }

    [Fact]
    public void 開けない曲は失敗を通知して飛ばす()
    {
        playlist.Add(["a", "bad1", "bad2", "c"]);
        player.Play(Item("a"));

        PlayToEnd();

        Assert.Equal("c", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal(["bad1", "bad2"], failed);
    }

    [Fact]
    public void 次へでも開けない曲は飛ばし_全曲リピートでは最初へ回り込む()
    {
        playlist.Add(["a", "bad1", "bad2"]);
        player.Mode = PlayMode.RepeatAll;
        player.Play(Item("bad1"));
        failed.Clear();

        player.Next();

        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal("a", Current);
        Assert.Equal(["bad2"], failed);
    }

    [Fact]
    public void 指定した曲が開けないときは_他の曲へ進まず失敗を通知する()
    {
        playlist.Add(["a", "bad", "c"]);

        bool result = player.Play(Item("bad"));

        Assert.False(result);
        Assert.Equal(["bad"], failed);
        Assert.Equal("bad", Current);
        Assert.Null(playback.CurrentPath);
    }

    [Fact]
    public void 出力を開始できないときも失敗を通知する()
    {
        playlist.Add(["a"]);
        outputs.NextPlayException = new InvalidOperationException("no device");

        bool result = player.Play(Item("a"));

        Assert.False(result);
        Assert.Equal(["a"], failed);
        Assert.Equal(PlayerState.Stopped, playback.State);
    }

    [Fact]
    public void 次へは並び順で進み_最後の曲では全曲リピートのときだけ最初へ戻る()
    {
        playlist.Add(["a", "b"]);
        player.Play(Item("a"));

        player.Next();
        Assert.Equal("b", Current);
        player.Next();
        Assert.Equal("b", Current);

        player.Mode = PlayMode.RepeatAll;
        player.Next();
        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 一曲リピートやワンショットでも_次へは次の曲に進む()
    {
        playlist.Add(["a", "b", "c"]);
        player.Mode = PlayMode.RepeatOne;
        player.Play(Item("a"));

        player.Next();
        Assert.Equal("b", Current);

        player.Mode = PlayMode.OneShot;
        player.Next();
        Assert.Equal("c", Current);
    }

    [Fact]
    public void 前へは曲の途中なら先頭に戻り_先頭付近なら前の曲へ移る()
    {
        playlist.Add(["a", "b"]);
        player.Play(Item("b"));
        playback.Seek(TimeSpan.FromSeconds(5));

        player.Previous();
        Assert.Equal("b", Current);
        Assert.Equal(TimeSpan.Zero, playback.Position);

        player.Previous();
        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Playing, playback.State);

        player.Previous();
        Assert.Equal("a", Current);
    }

    [Fact]
    public void ランダムの前へは_再生した順をさかのぼる()
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        player.Mode = PlayMode.Random;
        player.Play(Item("a"));
        player.Next();
        string? second = Current;
        player.Next();
        string? third = Current;
        Assert.NotEqual(second, third);

        player.Previous();
        Assert.Equal(second, Current);
        player.Previous();
        Assert.Equal("a", Current);
        player.Previous();
        Assert.Equal("a", Current);
    }

    [Fact]
    public void 何も開いていないときの再生は_最初の曲から始まる()
    {
        playlist.Add(["a", "b"]);

        player.PlayPause();

        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 選んだだけの曲は再生されず_再生を押すとその曲から始まる()
    {
        playlist.Add(["a", "b", "c"]);

        player.Select(Item("b"));
        Assert.Equal("b", Current);
        Assert.True(Item("b").IsCurrent);
        Assert.Null(playback.CurrentPath);

        player.PlayPause();
        Assert.Equal("b", playback.CurrentPath);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 再生と一時停止を切り替えられ_再開後も次の曲へ続く()
    {
        playlist.Add(["a", "b"]);
        player.Play(Item("a"));

        player.PlayPause();
        Assert.Equal(PlayerState.Paused, playback.State);
        player.PlayPause();
        Assert.Equal(PlayerState.Playing, playback.State);

        player.Stop();
        Assert.Equal(PlayerState.Stopped, playback.State);
        player.PlayPause();
        PlayToEnd();
        Assert.Equal("b", Current);
    }

    [Fact]
    public void 空のプレイリストでは何も起きない()
    {
        player.PlayPause();
        player.Next();
        player.Previous();
        player.Stop();

        Assert.Null(Current);
        Assert.Empty(failed);
        Assert.Empty(outputs.Created);
    }

    [Fact]
    public void 今の曲の印は1曲だけに付く()
    {
        playlist.Add(["a", "b", "c"]);
        player.Play(Item("a"));
        PlayToEnd();
        player.Next();

        Assert.Equal(["c"], playlist.Items.Where(item => item.IsCurrent).Select(item => item.Path));
        Assert.Equal(["a", "b", "c"], currentLog);
    }

    [Fact]
    public void 次の曲を並べ替えや削除で変えると_続く曲も変わる()
    {
        playlist.Add(["a", "b", "c"]);
        player.Play(Item("a"));

        playlist.Remove([Item("b")]);
        PlayToEnd();
        Assert.Equal("c", Current);
        Assert.Single(outputs.Created);

        playlist.Insert(0, ["x"]);
        playlist.Move([Item("x")], 99);
        PlayToEnd();
        Assert.Equal("x", Current);
    }

    [Fact]
    public void 再生中の曲を削除しても再生は続き_その位置にあった曲へ進む()
    {
        playlist.Add(["a", "b", "c", "d"]);
        player.Play(Item("b"));
        PlaylistItem b = Item("b");

        playlist.Remove([b]);

        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Same(b, player.Current);
        Assert.False(b.IsCurrent);

        PlayToEnd();
        Assert.Equal("c", Current);
    }

    [Fact]
    public void 再生中の曲の削除を元に戻すと_今の曲の印も戻る()
    {
        playlist.Add(["a", "b"]);
        player.Play(Item("a"));
        PlaylistItem a = Item("a");

        playlist.Remove([a]);
        playlist.Undo();

        Assert.True(a.IsCurrent);
        PlayToEnd();
        Assert.Equal("b", Current);
    }

    [Fact]
    public void プレイリストを空にしても再生中の曲は最後まで再生して止まる()
    {
        playlist.Add(["a", "b"]);
        player.Play(Item("a"));

        playlist.Clear();
        PlayToEnd();

        Assert.Equal(PlayerState.Stopped, playback.State);
        Assert.Empty(failed);
    }

    [Fact]
    public void 再生中の曲と前の曲をまとめて削除しても_後ろにあった曲へ進む()
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        player.Play(Item("d"));

        playlist.Remove([Item("a"), Item("b"), Item("d")]);
        PlayToEnd();

        Assert.Equal("e", Current);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 再生中の曲を削除した後に_引き継ぎ先の曲も削除したら_さらに後ろの曲へ進む()
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        player.Play(Item("b"));

        playlist.Remove([Item("b")]);
        playlist.Insert(0, ["x"]);
        playlist.Remove([Item("a"), Item("c")]);
        PlayToEnd();

        Assert.Equal("d", Current);
    }

    [Fact]
    public void 再生中の曲を削除した後の前へは_引き継ぎ先の1つ前の曲へ移る()
    {
        playlist.Add(["a", "b", "c", "d"]);
        player.Play(Item("c"));

        playlist.Remove([Item("c")]);
        player.Previous();

        Assert.Equal("b", Current);
    }

    [Fact]
    public void ランダムで前へ戻ってから進んだ後の前へは_直前に再生した曲へ戻る()
    {
        playlist.Add(["a", "b", "c", "d", "e", "f"]);
        player.Mode = PlayMode.Random;
        player.Play(Item("a"));
        player.Next();
        string? second = Current;
        player.Next();

        player.Previous();
        Assert.Equal(second, Current);
        player.Next();
        string? afterReturn = Current;
        Assert.NotEqual(second, afterReturn);

        player.Previous();
        Assert.Equal(second, Current);
        player.Previous();
        Assert.Equal("a", Current);
    }

    [Fact]
    public void 出力を開始できないときは_曲のせいではないので他の曲を試さない()
    {
        playlist.Add(["a", "b", "c", "d", "e"]);
        outputs.NextPlayException = new InvalidOperationException("no device");

        player.PlayPause();

        Assert.Equal(["a"], failed);
        Assert.Equal("a", Current);
        Assert.Single(outputs.Created);
        Assert.Equal(PlayerState.Stopped, playback.State);

        failed.Clear();
        player.Next();
        Assert.Equal(["b"], failed);
        Assert.Equal(2, outputs.Created.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 乗り換えの通知が届く前にプレイリストやモードを変えても_乗り換えた先を基準に次の曲を決める(bool changeMode)
    {
        var context = new QueueContext();
        var queuedPlaylist = new Playlist();
        using var queuedPlayback = new PlaybackService(opener, outputs, context);
        using var queuedPlayer = new PlaylistPlayer(queuedPlaylist, queuedPlayback, new Random(1));
        queuedPlaylist.Add(["a", "b", "c"]);
        queuedPlayer.Play(queuedPlaylist.Items[0]);
        context.RunAll();

        // 出力は a から b へ乗り換えたが、その通知はまだ処理されていない。
        outputs.Last.Read(TrackLength + TimeSpan.FromSeconds(1));
        Assert.Equal("a", queuedPlayer.Current?.Path);

        if (changeMode)
        {
            queuedPlayer.Mode = PlayMode.RepeatAll;
        }
        else
        {
            queuedPlaylist.Add(["d"]);
        }
        context.RunAll();
        Assert.Equal("b", queuedPlayer.Current?.Path);

        outputs.Last.Read(TrackLength);
        context.RunAll();

        Assert.Equal("c", queuedPlayer.Current?.Path);
        Assert.Equal(PlayerState.Playing, queuedPlayback.State);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 破棄した後は曲の終わりで次へ進まない()
    {
        playlist.Add(["a", "48k"]);
        player.Play(Item("a"));

        player.Dispose();
        outputs.Last.DrainAndStop();

        Assert.Equal("a", Current);
        Assert.Equal(PlayerState.Stopped, playback.State);
    }
}
