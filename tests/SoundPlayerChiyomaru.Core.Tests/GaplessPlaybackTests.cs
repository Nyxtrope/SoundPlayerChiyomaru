using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Playback;

namespace SoundPlayerChiyomaru.Core.Tests;

public class GaplessPlaybackTests
{
    private const float ValueA = 8192 / 32768f;
    private const float ValueB = 16384 / 32768f;

    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly PlaybackService service;
    private int advanced;
    private int ended;

    public GaplessPlaybackTests()
    {
        opener.Create = path => path switch
        {
            "a" => new FakeDecoder(TimeSpan.FromSeconds(2)) { SampleValue = 8192 },
            "b" => new FakeDecoder(TimeSpan.FromSeconds(3)) { SampleValue = 16384 },
            "48k" => new FakeDecoder(TimeSpan.FromSeconds(3)) { WaveFormat = new WaveFormat(48000, 16, 2) },
            "mono" => new FakeDecoder(TimeSpan.FromSeconds(3)) { WaveFormat = new WaveFormat(44100, 16, 1) },
            "24bit" => new FakeDecoder(TimeSpan.FromSeconds(3)) { WaveFormat = new WaveFormat(44100, 24, 2) },
            _ => throw new FileNotFoundException(path),
        };
        service = Create(context: null);
    }

    private PlaybackService Create(SynchronizationContext? context)
    {
        var created = new PlaybackService(opener, outputs, context);
        created.TrackAdvanced += (_, _) => advanced++;
        created.TrackEnded += (_, _) => ended++;
        return created;
    }

    [Fact]
    public void 次の曲を予約すると_境界でサンプルが欠けも余りもせずに続く()
    {
        service.Open("a");
        service.SetNext("b");
        service.Play();

        float[] samples = outputs.Last.Read(TimeSpan.FromSeconds(3));

        int boundary = 2 * 44100 * 2;
        Assert.Equal(3 * 44100 * 2, samples.Length);
        Assert.All(samples.Take(boundary), s => Assert.Equal(ValueA, s, 5));
        Assert.All(samples.Skip(boundary), s => Assert.Equal(ValueB, s, 5));
    }

    [Fact]
    public void 続けて再生した後は次の曲が今の曲になり_前の曲は破棄される()
    {
        service.Open("a");
        service.SetNext("b");
        service.Play();
        FakeOutput output = outputs.Last;

        output.Read(TimeSpan.FromSeconds(3));

        Assert.Equal(1, advanced);
        Assert.Equal(0, ended);
        Assert.Equal("b", service.CurrentPath);
        Assert.Equal(TimeSpan.FromSeconds(3), service.Duration);
        Assert.Equal(1, service.Position.TotalSeconds, 3);
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.True(opener.Opened[0].Disposed);
        Assert.False(opener.Opened[1].Disposed);
        Assert.Single(outputs.Created);
        Assert.False(output.Disposed);
    }

    [Fact]
    public void 続けて再生した曲の終端では_予約がなければ曲の終わりとして止まる()
    {
        service.Open("a");
        service.SetNext("b");
        service.Play();

        outputs.Last.DrainAndStop();

        Assert.Equal(1, advanced);
        Assert.Equal(1, ended);
        Assert.Equal("b", service.CurrentPath);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal(TimeSpan.Zero, service.Position);
    }

    [Fact]
    public void ビット深度だけが違う曲は続けて再生できる()
    {
        service.Open("a");
        service.SetNext("24bit");
        service.Play();

        outputs.Last.Read(TimeSpan.FromSeconds(3));

        Assert.Equal(1, advanced);
        Assert.Equal("24bit", service.CurrentPath);
    }

    [Theory]
    [InlineData("48k")]
    [InlineData("mono")]
    public void サンプルレートやチャンネル数が違う曲は続けず_今の曲の終わりとして止まる(string next)
    {
        service.Open("a");
        service.SetNext(next);
        service.Play();

        outputs.Last.DrainAndStop();

        Assert.Equal(0, advanced);
        Assert.Equal(1, ended);
        Assert.Equal("a", service.CurrentPath);
        Assert.True(opener.Opened[1].Disposed);
    }

    [Fact]
    public void 開けない曲を予約しても今の曲の再生には影響しない()
    {
        service.Open("a");
        service.SetNext("missing");
        service.Play();

        outputs.Last.DrainAndStop();

        Assert.Equal(0, advanced);
        Assert.Equal(1, ended);
        Assert.Equal("a", service.CurrentPath);
    }

    [Fact]
    public void 予約を取り消すと続けず_予約していた曲は破棄される()
    {
        service.Open("a");
        service.SetNext("b");
        service.SetNext(null);
        service.Play();

        outputs.Last.DrainAndStop();

        Assert.Equal(0, advanced);
        Assert.Equal(1, ended);
        Assert.True(opener.Opened[1].Disposed);
    }

    [Fact]
    public void 予約を入れ替えると前の予約は破棄され_新しい予約へ続く()
    {
        opener.Create = path => new FakeDecoder(TimeSpan.FromSeconds(2));
        service.Open("a");
        service.SetNext("b");
        service.SetNext("c");
        service.Play();

        outputs.Last.Read(TimeSpan.FromSeconds(3));

        Assert.True(opener.Opened[1].Disposed);
        Assert.Equal("c", service.CurrentPath);
    }

    [Fact]
    public void 何も開いていないときの予約は何もしない()
    {
        service.SetNext("b");

        Assert.Empty(opener.Opened);
        Assert.Null(service.CurrentPath);
    }

    [Fact]
    public void 閉じると予約していた曲も破棄される()
    {
        service.Open("a");
        service.SetNext("b");

        service.Close();

        Assert.All(opener.Opened, decoder => Assert.True(decoder.Disposed));
    }

    [Fact]
    public void 別の曲を開くと予約は捨てられる()
    {
        service.Open("a");
        service.SetNext("b");

        service.Open("a");
        service.Play();
        outputs.Last.DrainAndStop();

        Assert.Equal(0, advanced);
        Assert.Equal(1, ended);
        Assert.True(opener.Opened[1].Disposed);
    }

    [Fact]
    public void 停止しても予約は残り_再生し直すと続けて再生される()
    {
        service.Open("a");
        service.SetNext("b");
        service.Play();
        outputs.Last.Read(TimeSpan.FromSeconds(1));

        service.Stop();
        service.Play();
        outputs.Last.Read(TimeSpan.FromSeconds(3));

        Assert.Equal(1, advanced);
        Assert.Equal("b", service.CurrentPath);
    }

    [Fact]
    public void 予約した曲を開くのは_コンテキストに投げ直した後()
    {
        var context = new QueueContext();
        using PlaybackService queued = Create(context);
        queued.Open("a");

        queued.SetNext("b");
        Assert.Single(opener.Opened);

        context.RunAll();
        Assert.Equal(2, opener.Opened.Count);
    }

    [Fact]
    public void 開く前に予約が入れ替わったら_古い予約の曲は開かない()
    {
        var context = new QueueContext();
        using PlaybackService queued = Create(context);
        queued.Open("a");

        queued.SetNext("b");
        queued.SetNext(null);
        context.RunAll();

        Assert.Single(opener.Opened);
    }

    [Fact]
    public void 乗り換えの通知が届く前の操作は_乗り換えた先の曲に対して行われる()
    {
        var context = new QueueContext();
        using PlaybackService queued = Create(context);
        queued.Open("a");
        queued.SetNext("b");
        context.RunAll();
        queued.Play();
        outputs.Last.Read(TimeSpan.FromSeconds(2.5));
        Assert.Equal(0, advanced);

        queued.Seek(TimeSpan.FromSeconds(2));

        Assert.Equal(1, advanced);
        Assert.Equal("b", queued.CurrentPath);
        Assert.Equal(2, queued.Position.TotalSeconds, 3);

        context.RunAll();
        Assert.Equal(1, advanced);
    }

    [Fact]
    public void 乗り換えの通知が届く前に別の曲を開いたら_古い通知は無視する()
    {
        var context = new QueueContext();
        using PlaybackService queued = Create(context);
        queued.Open("a");
        queued.SetNext("b");
        context.RunAll();
        queued.Play();
        outputs.Last.Read(TimeSpan.FromSeconds(2.5));

        queued.Open("a");
        context.RunAll();

        Assert.Equal(0, advanced);
        Assert.Equal("a", queued.CurrentPath);
        Assert.Equal(PlayerState.Stopped, queued.State);
    }
}
