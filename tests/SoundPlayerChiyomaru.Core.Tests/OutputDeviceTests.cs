using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Playback;

namespace SoundPlayerChiyomaru.Core.Tests;

public class OutputDeviceSelectionTests
{
    [Theory]
    [InlineData(200, 200)]
    [InlineData(50, 50)]
    [InlineData(500, 500)]
    [InlineData(150, 200)]
    [InlineData(1000, 200)]
    [InlineData(0, 200)]
    [InlineData(-5, 200)]
    [InlineData(100000, 200)]
    public void 読んだバッファの長さは_選べる値以外なら既定に戻す(int saved, int expected)
    {
        Assert.Equal(expected, OutputDeviceSelection.NormalizeBuffer(saved));
        Assert.Contains(OutputDeviceSelection.DefaultBufferMilliseconds, OutputDeviceSelection.BufferChoices);
        Assert.All(OutputDeviceSelection.BufferChoices, value => Assert.Equal(value, OutputDeviceSelection.NormalizeBuffer(value)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    [InlineData(32, null)]
    [InlineData(0, null)]
    [InlineData(-16, null)]
    public void 読んだビット深度の上限は_選べる値以外なら上限なしにする(int? saved, int? expected)
    {
        Assert.Equal(expected, OutputDeviceSelection.NormalizeBitDepth(saved));
    }

    private readonly FakeDeviceService devices = new() { DefaultDeviceId = "speakers" };
    private readonly OutputDeviceSelection selection;

    public OutputDeviceSelectionTests()
    {
        devices.Devices.Add(new AudioDeviceInfo("speakers", "Speakers"));
        devices.Devices.Add(new AudioDeviceInfo("dac", "USB DAC"));
        selection = new OutputDeviceSelection(devices);
    }

    [Fact]
    public void 選んでいなければ既定のデバイスを使う()
    {
        Assert.Equal("speakers", selection.ResolveDeviceId());

        devices.DefaultDeviceId = "dac";
        Assert.Equal("dac", selection.ResolveDeviceId());
    }

    [Fact]
    public void 選んだデバイスが使えればそれを使い_使えない間は既定のデバイスを使う()
    {
        selection.PreferredDeviceId = "dac";
        Assert.Equal("dac", selection.ResolveDeviceId());

        devices.Devices.RemoveAll(device => device.Id == "dac");
        Assert.Equal("speakers", selection.ResolveDeviceId());

        devices.Devices.Add(new AudioDeviceInfo("dac", "USB DAC"));
        Assert.Equal("dac", selection.ResolveDeviceId());
    }

    [Fact]
    public void 使えるデバイスが1つもなければnull()
    {
        devices.Devices.Clear();
        devices.DefaultDeviceId = null;
        selection.PreferredDeviceId = "dac";

        Assert.Null(selection.ResolveDeviceId());
    }

    [Fact]
    public void 選択が変わったときだけ通知する()
    {
        int changed = 0;
        selection.PreferredChanged += (_, _) => changed++;

        selection.PreferredDeviceId = "dac";
        selection.PreferredDeviceId = "dac";
        selection.PreferredDeviceId = null;

        Assert.Equal(2, changed);
    }
}

public class MultichannelFormatTests
{
    private sealed class Source(WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> buffer)
        {
            buffer.Fill(7);
            return buffer.Length - 1;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ステレオ以下はそのまま返す(int channels)
    {
        var source = new Source(WaveFormat.CreateIeeeFloatWaveFormat(48000, channels));

        Assert.Same(source, MultichannelFormat.Apply(source));
    }

    [Fact]
    public void 既に配置つきの形式ならそのまま返す()
    {
        var source = new Source(new WaveFormatExtensible(48000, 32, 6, true, 32, 0x3F));

        Assert.Same(source, MultichannelFormat.Apply(source));
    }

    [Theory]
    [InlineData(3, 0x7)]
    [InlineData(4, 0x33)]
    [InlineData(5, 0x37)]
    [InlineData(6, 0x3F)]
    [InlineData(7, 0x70F)]
    [InlineData(8, 0x63F)]
    public void 三チャンネル以上は_標準の配置を持つfloatの形式になる(int channels, int mask)
    {
        IWaveProvider result = MultichannelFormat.Apply(new Source(WaveFormat.CreateIeeeFloatWaveFormat(96000, channels)));

        var format = Assert.IsType<WaveFormatExtensible>(result.WaveFormat);
        Assert.Equal(mask, format.ChannelMask);
        Assert.Equal(channels, format.Channels);
        Assert.Equal(96000, format.SampleRate);
        Assert.Equal(32, format.BitsPerSample);
        Assert.Equal(32, format.ValidBitsPerSample);
        Assert.Equal(new Guid("00000003-0000-0010-8000-00aa00389b71"), format.SubFormat);
        Assert.Equal(channels * 4, format.BlockAlign);
    }

    [Fact]
    public void 整数の形式は整数のまま配置を付ける()
    {
        IWaveProvider result = MultichannelFormat.Apply(new Source(new WaveFormat(48000, 24, 6)));

        var format = Assert.IsType<WaveFormatExtensible>(result.WaveFormat);
        Assert.Equal(new Guid("00000001-0000-0010-8000-00aa00389b71"), format.SubFormat);
        Assert.Equal(24, format.BitsPerSample);
        Assert.Equal(24, format.ValidBitsPerSample);
    }

    [Theory]
    [InlineData(9, 0x1FF)]
    [InlineData(18, 0x3FFFF)]
    [InlineData(24, 0x3FFFF)]
    public void 九チャンネル以上は_定義のある配置を下から順に割り当てる(int channels, int mask)
    {
        Assert.Equal(mask, MultichannelFormat.DefaultChannelMask(channels));
    }

    [Fact]
    public void サンプルには手を加えず_読めた長さもそのまま返す()
    {
        IWaveProvider result = MultichannelFormat.Apply(new Source(WaveFormat.CreateIeeeFloatWaveFormat(48000, 6)));
        byte[] buffer = new byte[48];

        Assert.Equal(47, result.Read(buffer));
        Assert.All(buffer, b => Assert.Equal(7, b));
    }
}

public class DeviceLostDetectionTests
{
    [Theory]
    [InlineData(unchecked((int)0x88890004), true)]
    [InlineData(unchecked((int)0x88890026), true)]
    [InlineData(unchecked((int)0x8889000A), false)]
    [InlineData(unchecked((int)0x80070057), false)]
    public void デバイスが失われたことを表すエラーコードだけを見分ける(int hresult, bool expected)
    {
        var exception = new System.Runtime.InteropServices.COMException("error", hresult);

        Assert.Equal(expected, WasapiSharedOutput.IsDeviceLost(exception));
    }

    [Fact]
    public void COMのエラーでない例外や_例外なしは_デバイスの喪失ではない()
    {
        Assert.False(WasapiSharedOutput.IsDeviceLost(new IOException("x")));
        Assert.False(WasapiSharedOutput.IsDeviceLost(null));
    }
}

public class OutputRestartTests
{
    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly ManualTime time = new();
    private readonly PlaybackService service;
    private readonly List<Exception> failures = [];
    private int ended;
    private int outputChanged;

    public OutputRestartTests()
    {
        service = new PlaybackService(opener, outputs, context: null, time);
        service.PlaybackFailed += (_, e) => failures.Add(e.Exception);
        service.TrackEnded += (_, _) => ended++;
        service.OutputChanged += (_, _) => outputChanged++;
    }

    private void PlayFrom(double seconds)
    {
        service.Open("a.mp3");
        service.Seek(TimeSpan.FromSeconds(seconds));
        service.Play();
    }

    [Fact]
    public void 再生中に出力を作り直すと_状態と位置を保ったまま新しい出力で続く()
    {
        PlayFrom(4);
        FakeOutput first = outputs.Last;
        var states = new List<PlayerState>();
        service.StateChanged += (_, _) => states.Add(service.State);
        outputs.ResolveDeviceId = () => "other";

        service.RestartOutput();

        Assert.True(first.Disposed);
        Assert.Equal(2, outputs.Created.Count);
        Assert.True(outputs.Last.Playing);
        Assert.Equal("other", service.OutputDeviceId);
        Assert.Equal("Fake other", service.OutputDeviceName);
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
        Assert.Empty(states);
        Assert.Empty(failures);
    }

    [Fact]
    public void 作り直した後は_古い出力からの通知を無視し_新しい出力の終端で曲が終わる()
    {
        PlayFrom(4);
        FakeOutput first = outputs.Last;
        service.RestartOutput();

        first.RaiseStopped(null);
        first.RaiseDeviceLost();
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(2, outputs.Created.Count);

        outputs.Last.DrainAndStop();
        Assert.Equal(1, ended);
        Assert.Equal(PlayerState.Stopped, service.State);
    }

    [Fact]
    public void 一時停止中に作り直すと出力を手放し_再生を押したときに新しい出力が作られる()
    {
        PlayFrom(4);
        service.Pause();

        service.RestartOutput();

        Assert.True(outputs.Last.Disposed);
        Assert.Null(service.OutputDeviceId);
        Assert.Equal(PlayerState.Paused, service.State);
        Assert.Single(outputs.Created);

        service.Play();
        Assert.Equal(2, outputs.Created.Count);
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 停止中や何も開いていないときの作り直しは何もしない()
    {
        service.RestartOutput();
        service.Open("a.mp3");
        service.RestartOutput();

        Assert.Empty(outputs.Created);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.Equal(0, outputChanged);
    }

    [Fact]
    public void 作り直せなければ_位置を保って一時停止にして失敗を1回通知し_デバイスが使えるようになると再開する()
    {
        PlayFrom(4);
        outputs.CreateException = new InvalidOperationException("no device");

        service.RestartOutput();

        Assert.Equal(PlayerState.Paused, service.State);
        Assert.True(service.IsWaitingForDevice);
        Assert.Null(service.OutputDeviceId);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
        Assert.Single(failures);

        service.RestartOutput();
        Assert.Single(failures);
        Assert.Equal(PlayerState.Paused, service.State);

        outputs.CreateException = null;
        service.RestartOutput();

        Assert.Equal(PlayerState.Playing, service.State);
        Assert.False(service.IsWaitingForDevice);
        Assert.True(outputs.Last.Playing);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 再生中にデバイスを失うと_出力を作り直して同じ位置から続ける()
    {
        PlayFrom(4);
        FakeOutput first = outputs.Last;
        outputs.ResolveDeviceId = () => "fallback";

        first.RaiseDeviceLost();

        Assert.True(first.Disposed);
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal("fallback", service.OutputDeviceId);
        Assert.True(outputs.Last.Playing);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
        Assert.Empty(failures);
        Assert.Equal(0, ended);
    }

    [Fact]
    public void 作り直した出力も続けて失われる場合は_繰り返さずに待つ()
    {
        PlayFrom(4);

        outputs.Last.RaiseDeviceLost();
        time.Advance(TimeSpan.FromMilliseconds(200));
        outputs.Last.RaiseDeviceLost();
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(3, outputs.Created.Count);

        time.Advance(TimeSpan.FromMilliseconds(200));
        outputs.Last.RaiseDeviceLost();

        Assert.Equal(3, outputs.Created.Count);
        Assert.Equal(PlayerState.Paused, service.State);
        Assert.True(service.IsWaitingForDevice);
        Assert.Single(failures);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 回数は最初の喪失から10秒の間で数え_それを過ぎたら新しい障害として数え直す()
    {
        PlayFrom(4);

        // 0 秒、9 秒、18 秒、27 秒…: どの 10 秒の間にも 2 回までしか起きていない。
        for (int i = 0; i < 6; i++)
        {
            outputs.Last.RaiseDeviceLost();
            time.Advance(TimeSpan.FromSeconds(9));
        }

        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(7, outputs.Created.Count);
        Assert.Empty(failures);
    }

    [Fact]
    public void 待ちから再開できた後は_前の障害の回数を引き継がない()
    {
        PlayFrom(4);
        outputs.Last.RaiseDeviceLost();
        outputs.Last.RaiseDeviceLost();
        outputs.Last.RaiseDeviceLost();
        Assert.True(service.IsWaitingForDevice);

        service.RestartOutput();
        Assert.Equal(PlayerState.Playing, service.State);
        outputs.Last.RaiseDeviceLost();

        Assert.Equal(PlayerState.Playing, service.State);
        Assert.False(service.IsWaitingForDevice);
        Assert.Single(failures);
    }

    [Fact]
    public void 利用者が再生を押した後も_前の障害の回数を引き継がない()
    {
        PlayFrom(4);
        outputs.Last.RaiseDeviceLost();
        outputs.Last.RaiseDeviceLost();
        outputs.Last.RaiseDeviceLost();

        service.Play();
        outputs.Last.RaiseDeviceLost();

        Assert.Equal(PlayerState.Playing, service.State);
        Assert.False(service.IsWaitingForDevice);
    }

    [Fact]
    public void 喪失の通知が届く前に出力を作り直していたら_古い出力からの通知は無視する()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context, time);
        int failed = 0;
        queued.PlaybackFailed += (_, _) => failed++;
        queued.Open("a.mp3");
        queued.Seek(TimeSpan.FromSeconds(4));
        queued.Play();
        FakeOutput first = outputs.Last;

        // 出力が切断を検知して通知を投げたが、処理される前にデバイスの変化で出力が作り直された。
        first.RaiseDeviceLost();
        first.RaiseStopped(null);
        Assert.Equal(2, context.Pending);
        queued.RestartOutput();
        int created = outputs.Created.Count;

        context.RunAll();

        Assert.Equal(created, outputs.Created.Count);
        Assert.Equal(PlayerState.Playing, queued.State);
        Assert.False(outputs.Last.Disposed);
        Assert.Equal(0, failed);
        Assert.Equal(4, queued.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 喪失の通知が届く前に一時停止していたら_一時停止のままで待ちにならない()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context, time);
        queued.Open("a.mp3");
        queued.Play();
        outputs.Last.RaiseDeviceLost();

        queued.Pause();
        context.RunAll();

        Assert.Equal(PlayerState.Paused, queued.State);
        Assert.False(queued.IsWaitingForDevice);
        Assert.Null(queued.OutputDeviceId);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void デバイスを失ったときに出力を作れなければ_待ちに入り_使えるようになると再開する()
    {
        PlayFrom(4);
        outputs.CreateException = new InvalidOperationException("no device");

        outputs.Last.RaiseDeviceLost();

        Assert.Equal(PlayerState.Paused, service.State);
        Assert.True(service.IsWaitingForDevice);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));

        outputs.CreateException = null;
        service.RestartOutput();
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 一時停止中にデバイスを失っても_一時停止のままで_待ちにはならない()
    {
        PlayFrom(4);
        service.Pause();

        outputs.Last.RaiseDeviceLost();

        Assert.Equal(PlayerState.Paused, service.State);
        Assert.False(service.IsWaitingForDevice);
        Assert.Empty(failures);
        Assert.Single(outputs.Created);

        service.Play();
        Assert.Equal(PlayerState.Playing, service.State);
        Assert.Equal(4, service.Position.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("open")]
    [InlineData("close")]
    public void 待っている間に利用者が操作したら_デバイスが戻っても勝手に再開しない(string action)
    {
        PlayFrom(4);
        outputs.CreateException = new InvalidOperationException("no device");
        service.RestartOutput();

        switch (action)
        {
            case "stop": service.Stop(); break;
            case "pause": service.Pause(); break;
            case "open": service.Open("b.mp3"); break;
            case "close": service.Close(); break;
        }
        outputs.CreateException = null;
        service.RestartOutput();

        Assert.False(service.IsWaitingForDevice);
        Assert.NotEqual(PlayerState.Playing, service.State);
        Assert.Empty(outputs.Created.Skip(1));
    }

    [Fact]
    public void 待っている間に再生を押して_まだデバイスがなければ例外になり停止する()
    {
        PlayFrom(4);
        outputs.CreateException = new InvalidOperationException("no device");
        service.RestartOutput();

        Assert.Throws<InvalidOperationException>(service.Play);

        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.False(service.IsWaitingForDevice);
    }

    [Fact]
    public void 出力を作るたびと手放すたびに通知する()
    {
        service.Open("a.mp3");
        service.Play();
        Assert.Equal(1, outputChanged);

        service.RestartOutput();
        Assert.Equal(3, outputChanged);

        service.Stop();
        Assert.Equal(4, outputChanged);
    }
}

public class OutputDeviceFollowerTests : IDisposable
{
    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly FakeDeviceService devices = new() { DefaultDeviceId = "speakers" };
    private readonly OutputDeviceSelection selection;
    private readonly PlaybackService playback;
    private readonly OutputDeviceFollower follower;

    public OutputDeviceFollowerTests()
    {
        devices.Devices.Add(new AudioDeviceInfo("speakers", "Speakers"));
        devices.Devices.Add(new AudioDeviceInfo("dac", "USB DAC"));
        selection = new OutputDeviceSelection(devices);
        outputs.ResolveDeviceId = selection.ResolveDeviceId;
        playback = new PlaybackService(opener, outputs, context: null);
        follower = new OutputDeviceFollower(playback, selection, devices);

        playback.Open("a.mp3");
        playback.Seek(TimeSpan.FromSeconds(4));
    }

    public void Dispose() => follower.Dispose();

    private void Unplug(string id)
    {
        devices.Devices.RemoveAll(device => device.Id == id);
        devices.RaiseChanged();
    }

    private void Plug(string id)
    {
        devices.Devices.Add(new AudioDeviceInfo(id, id));
        devices.RaiseChanged();
    }

    [Fact]
    public void 再生中にデバイスを選び直すと_そのデバイスで続く()
    {
        playback.Play();
        Assert.Equal("speakers", playback.OutputDeviceId);

        selection.PreferredDeviceId = "dac";

        Assert.Equal("dac", playback.OutputDeviceId);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal(4, playback.Position.TotalSeconds, 3);
        Assert.Equal(2, outputs.Created.Count);
    }

    [Fact]
    public void 使うべきデバイスが変わらない通知では_出力を作り直さない()
    {
        playback.Play();

        devices.RaiseChanged();
        Plug("headset");
        Unplug("dac");
        selection.PreferredDeviceId = "speakers";

        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 既定のデバイスに従っているときは_既定が変わると付いていく()
    {
        playback.Play();

        devices.DefaultDeviceId = "dac";
        devices.RaiseChanged();

        Assert.Equal("dac", playback.OutputDeviceId);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void デバイスを選んでいるときは_既定が変わっても付いていかない()
    {
        selection.PreferredDeviceId = "dac";
        playback.Play();

        devices.DefaultDeviceId = "headset";
        Plug("headset");

        Assert.Equal("dac", playback.OutputDeviceId);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 選んだデバイスが抜かれたら既定のデバイスで続け_戻ってきたら選んだデバイスに戻る()
    {
        selection.PreferredDeviceId = "dac";
        playback.Play();

        Unplug("dac");
        Assert.Equal("speakers", playback.OutputDeviceId);
        Assert.Equal(PlayerState.Playing, playback.State);

        Plug("dac");
        Assert.Equal("dac", playback.OutputDeviceId);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal(4, playback.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 抜かれた通知より先に出力が切断を検知しても_同じ結果になる()
    {
        selection.PreferredDeviceId = "dac";
        playback.Play();
        FakeOutput dac = outputs.Last;

        // Windows の一覧がまだ更新されていないので、作り直した出力も同じデバイスになる。
        dac.RaiseDeviceLost();
        Assert.Equal("dac", playback.OutputDeviceId);

        Unplug("dac");
        Assert.Equal("speakers", playback.OutputDeviceId);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void 通知より先に出力が喪失を検知して正しいデバイスで作り直していたら_通知でもう一度作り直さない()
    {
        selection.PreferredDeviceId = "dac";
        playback.Play();
        FakeOutput dac = outputs.Last;

        // Windows の一覧は更新済みで、通知だけがまだ届いていない。
        devices.Devices.RemoveAll(device => device.Id == "dac");
        dac.RaiseDeviceLost();
        Assert.Equal("speakers", playback.OutputDeviceId);
        int created = outputs.Created.Count;

        devices.RaiseChanged();
        devices.RaiseChanged();

        Assert.Equal(created, outputs.Created.Count);
        Assert.Equal(PlayerState.Playing, playback.State);
    }

    [Fact]
    public void デバイスが1つもなくなって待っている間に_デバイスが現れたら再開する()
    {
        playback.Play();
        devices.Devices.Clear();
        devices.DefaultDeviceId = null;
        outputs.Last.RaiseDeviceLost();
        Assert.True(playback.IsWaitingForDevice);

        devices.RaiseChanged();
        Assert.True(playback.IsWaitingForDevice);

        devices.DefaultDeviceId = "headset";
        Plug("headset");

        Assert.False(playback.IsWaitingForDevice);
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal("headset", playback.OutputDeviceId);
        Assert.Equal(4, playback.Position.TotalSeconds, 3);
    }

    [Fact]
    public void 停止中や一時停止中は_デバイスが変わっても出力を作らない()
    {
        selection.PreferredDeviceId = "dac";
        Assert.Empty(outputs.Created);

        playback.Play();
        playback.Pause();
        selection.PreferredDeviceId = null;

        Assert.Single(outputs.Created);
        Assert.Null(playback.OutputDeviceId);
        Assert.Equal(PlayerState.Paused, playback.State);

        playback.Play();
        Assert.Equal("speakers", playback.OutputDeviceId);
    }

    [Fact]
    public void 代わりのデバイスで鳴らしている間は_デバイスの通知が来ても作り直しを繰り返さない()
    {
        // 選んだデバイス（dac）では出力を作れず、既定のデバイスで鳴らしている状況。
        selection.PreferredDeviceId = "dac";
        outputs.ResolveDeviceId = () => "speakers";
        playback.Play();
        Assert.Equal("speakers", playback.OutputDeviceId);

        devices.RaiseChanged();
        Plug("headset");
        devices.RaiseChanged();

        Assert.Single(outputs.Created);
        Assert.Equal(PlayerState.Playing, playback.State);

        // 使うべきデバイスが変われば、1 回だけ作り直す。
        devices.DefaultDeviceId = "headset";
        selection.PreferredDeviceId = null;
        devices.RaiseChanged();
        Assert.Equal(2, outputs.Created.Count);
    }

    [Fact]
    public void 破棄した後は追従しない()
    {
        playback.Play();
        follower.Dispose();

        selection.PreferredDeviceId = "dac";
        devices.RaiseChanged();

        Assert.Equal("speakers", playback.OutputDeviceId);
    }
}
