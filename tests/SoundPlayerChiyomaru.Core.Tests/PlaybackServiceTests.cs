using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Playback;

namespace SoundPlayerChiyomaru.Core.Tests;

public class PlaybackServiceTests
{
    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly PlaybackService service;

    public PlaybackServiceTests()
    {
        service = new PlaybackService(opener, outputs, context: null);
    }

    [Fact]
    public void 何も開いていないときの操作は何もしない()
    {
        service.Play();
        service.Pause();
        service.Stop();
        service.Seek(TimeSpan.FromSeconds(5));

        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal(TimeSpan.Zero, service.Position);
        Assert.Equal(TimeSpan.Zero, service.Duration);
        Assert.Null(service.SourceFormat);
        Assert.Empty(outputs.Created);
    }

    [Fact]
    public void 開いただけでは再生は始まらない()
    {
        service.Open("a.mp3");

        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal("a.mp3", service.CurrentPath);
        Assert.Equal(TimeSpan.FromSeconds(10), service.Duration);
        Assert.Equal(44100, service.SourceFormat!.SampleRate);
        Assert.Empty(outputs.Created);
    }

    [Fact]
    public void 再生と一時停止と再開で同じ出力を使い続ける()
    {
        service.Open("a.mp3");

        service.Play();
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.True(outputs.Last.Playing);

        service.Pause();
        Assert.Equal(PlayerState.Paused, service.State);
        Assert.False(outputs.Last.Playing);

        service.Play();
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 出力へは32bit_floatで渡す()
    {
        service.Open("a.mp3");
        service.Play();

        WaveFormat format = outputs.Last.Source!.WaveFormat;
        Assert.Equal(WaveFormatEncoding.IeeeFloat, format.Encoding);
        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(2, format.Channels);
    }

    [Fact]
    public void 停止すると出力を破棄して先頭に戻る()
    {
        service.Open("a.mp3");
        service.Play();
        service.Seek(TimeSpan.FromSeconds(4));

        service.Stop();

        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal(TimeSpan.Zero, service.Position);
        Assert.True(outputs.Last.Disposed);
    }

    [Fact]
    public void シークは曲の範囲に丸められる()
    {
        service.Open("a.mp3");

        service.Seek(TimeSpan.FromSeconds(3));
        Assert.Equal(3, service.Position.TotalSeconds, 3);

        service.Seek(TimeSpan.FromSeconds(-1));
        Assert.Equal(TimeSpan.Zero, service.Position);

        service.Seek(TimeSpan.FromSeconds(99));
        Assert.Equal(10, service.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 停止中にシークした位置から再生が始まる()
    {
        service.Open("a.mp3");
        service.Seek(TimeSpan.FromSeconds(3));

        service.Play();

        Assert.Equal(3, service.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 曲の終端まで再生すると停止して先頭に戻り通知する()
    {
        int ended = 0;
        service.TrackEnded += (_, _) => ended++;
        service.Open("a.mp3");
        service.Play();
        FakeOutput output = outputs.Last;

        output.DrainAndStop();

        Assert.Equal(1, ended);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal(TimeSpan.Zero, service.Position);
        Assert.True(output.Disposed);
    }

    [Fact]
    public void 曲の終端で止まった後にもう一度再生できる()
    {
        service.Open("a.mp3");
        service.Play();
        outputs.Last.DrainAndStop();

        service.Play();

        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(2, outputs.Created.Count);
        Assert.True(outputs.Last.Playing);
    }

    [Fact]
    public void 出力がエラーで止まると停止して失敗を通知し位置は保つ()
    {
        Exception? failure = null;
        int ended = 0;
        service.PlaybackFailed += (_, e) => failure = e.Exception;
        service.TrackEnded += (_, _) => ended++;
        service.Open("a.mp3");
        service.Play();
        service.Seek(TimeSpan.FromSeconds(4));
        var error = new InvalidOperationException("device lost");

        outputs.Last.RaiseStopped(error);

        Assert.Same(error, failure);
        Assert.Equal(0, ended);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
        Assert.True(outputs.Last.Disposed);
    }

    [Fact]
    public void 終端を読み切ってから停止通知が届くまでの間にシークすると_シーク先から再生を続ける()
    {
        int ended = 0;
        service.TrackEnded += (_, _) => ended++;
        service.Open("a.mp3");
        service.Play();
        FakeOutput first = outputs.Last;
        first.Drain();

        service.Seek(TimeSpan.FromSeconds(4));
        first.RaiseStopped(null);

        Assert.Equal(0, ended);
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
        Assert.True(first.Disposed);
        Assert.Equal(2, outputs.Created.Count);
        Assert.True(outputs.Last.Playing);

        outputs.Last.DrainAndStop();
        Assert.Equal(1, ended);
        Assert.Equal(PlayerState.Stopped, service.State);
    }

    [Fact]
    public void 終端後のシークで出力を作り直せなければ失敗を通知して停止する()
    {
        Exception? failure = null;
        service.PlaybackFailed += (_, e) => failure = e.Exception;
        service.Open("a.mp3");
        service.Play();
        FakeOutput first = outputs.Last;
        first.Drain();
        service.Seek(TimeSpan.FromSeconds(4));
        outputs.NextPlayException = new InvalidOperationException("no device");

        first.RaiseStopped(null);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.True(outputs.Last.Disposed);
    }

    [Fact]
    public void 停止中に停止しても先頭に戻して通知する()
    {
        service.Open("a.mp3");
        service.Seek(TimeSpan.FromSeconds(4));
        int notified = 0;
        service.StateChanged += (_, _) => notified++;

        service.Stop();

        Assert.Equal(1, notified);
        Assert.Equal(TimeSpan.Zero, service.Position);
    }

    [Fact]
    public void 停止通知はコンテキストに投げ直してから処理する()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context);
        int ended = 0;
        queued.TrackEnded += (_, _) => ended++;
        queued.Open("a.mp3");
        queued.Play();

        outputs.Last.DrainAndStop();

        Assert.Equal(PlayerState.Playing, queued.State);
        Assert.False(outputs.Last.Disposed);

        context.RunAll();

        Assert.Equal(1, ended);
        Assert.Equal(PlayerState.Stopped, queued.State);
        Assert.True(outputs.Last.Disposed);
    }

    [Fact]
    public void 届く前に停止して再生し直した古い出力の停止通知は無視する()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context);
        int ended = 0;
        queued.TrackEnded += (_, _) => ended++;
        queued.Open("a.mp3");
        queued.Play();
        outputs.Last.DrainAndStop();
        Assert.Equal(1, context.Pending);

        queued.Stop();
        queued.Play();
        context.RunAll();

        Assert.Equal(0, ended);
        Assert.Equal(PlayerState.Playing, queued.State);
        Assert.False(outputs.Last.Disposed);
        Assert.True(outputs.Last.Playing);
    }

    [Fact]
    public void 届く前に別の曲を開いた場合や閉じた場合も古い停止通知は無視する()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context);
        int ended = 0;
        int failed = 0;
        queued.TrackEnded += (_, _) => ended++;
        queued.PlaybackFailed += (_, _) => failed++;
        queued.Open("a.mp3");
        queued.Play();
        outputs.Last.RaiseStopped(new InvalidOperationException("late"));

        queued.Open("b.mp3");
        context.RunAll();

        Assert.Equal(0, failed);
        Assert.Equal("b.mp3", queued.CurrentPath);

        queued.Play();
        outputs.Last.DrainAndStop();
        queued.Close();
        context.RunAll();

        Assert.Equal(0, ended);
        Assert.Null(queued.CurrentPath);
        Assert.Equal(PlayerState.Stopped, queued.State);
    }

    [Fact]
    public void 出力を開始できないと例外を投げて停止状態に戻る()
    {
        outputs.NextPlayException = new InvalidOperationException("no device");
        service.Open("a.mp3");

        Assert.Throws<InvalidOperationException>(service.Play);

        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.True(outputs.Last.Disposed);

        outputs.NextPlayException = null;
        service.Play();
        Assert.Equal(PlayerState.Playing, service.State);
    }

    [Fact]
    public void 別のファイルを開くと前の曲と出力を破棄する()
    {
        service.Open("a.mp3");
        service.Play();
        FakeOutput firstOutput = outputs.Last;

        service.Open("b.mp3");

        Assert.True(opener.Opened[0].Disposed);
        Assert.True(firstOutput.Disposed);
        Assert.False(opener.Opened[1].Disposed);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal("b.mp3", service.CurrentPath);
    }

    [Fact]
    public void 開けないファイルでは例外を投げて何も開いていない状態になる()
    {
        service.Open("a.mp3");
        service.Play();
        opener.Create = _ => throw new FileNotFoundException("missing");

        Assert.Throws<FileNotFoundException>(() => service.Open("missing.mp3"));

        Assert.Null(service.CurrentPath);
        Assert.Null(service.SourceFormat);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.True(opener.Opened[0].Disposed);
        service.Play();
        Assert.Equal(PlayerState.Stopped, service.State);
    }

    [Fact]
    public void float変換できない形式のデコーダは破棄して例外を投げる()
    {
        var unsupported = new FakeDecoder(TimeSpan.FromSeconds(1))
        {
            WaveFormat = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Adpcm, 44100, 2, 44100, 4, 4),
        };
        opener.Create = _ => unsupported;

        Assert.ThrowsAny<Exception>(() => service.Open("a.wav"));

        Assert.True(unsupported.Disposed);
        Assert.Null(service.CurrentPath);
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, 0f)]
    public void 音量は0から1に丸められる(float requested, float expected)
    {
        service.Volume = requested;

        Assert.Equal(expected, service.Volume);
    }

    [Fact]
    public void 音量は開く前に設定しても開いた後に変えてもサンプルに反映される()
    {
        opener.Create = _ => new ConstantDecoder();
        service.Volume = 0.5f;
        service.Open("a.wav");
        service.Play();
        var samples = new float[4];
        ISampleProvider provider = outputs.Last.Source!.ToSampleProvider();

        provider.Read(samples);
        Assert.All(samples, s => Assert.Equal(0.25f, s, 3));

        service.Volume = 1f;
        provider.Read(samples);
        Assert.All(samples, s => Assert.Equal(0.5f, s, 3));
    }

    [Fact]
    public void 状態が変わるたびに通知する()
    {
        var states = new List<PlayerState>();
        service.StateChanged += (_, _) => states.Add(service.State);

        service.Open("a.mp3");
        service.Play();
        service.Pause();
        service.Play();
        service.Stop();

        Assert.Equal(
            [PlayerState.Stopped, PlayerState.Playing, PlayerState.Paused, PlayerState.Playing, PlayerState.Stopped],
            states);
    }

    [Fact]
    public void 破棄すると曲と出力を閉じる()
    {
        service.Open("a.mp3");
        service.Play();

        service.Dispose();

        Assert.True(opener.Opened[0].Disposed);
        Assert.True(outputs.Last.Disposed);
    }

    /// <summary>振幅 0.5 の一定値を返し続ける 16bit ステレオのデコーダ。</summary>
    private sealed class ConstantDecoder : SoundPlayerChiyomaru.Core.Decoding.IAudioDecoder
    {
        public WaveFormat WaveFormat { get; } = new(44100, 16, 2);

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public TimeSpan Position { get; set; }

        public int Read(Span<byte> buffer)
        {
            int frames = buffer.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                BitConverter.TryWriteBytes(buffer[(i * 2)..], (short)16384);
            }
            return frames * 2;
        }

        public void Dispose()
        {
        }
    }
}
