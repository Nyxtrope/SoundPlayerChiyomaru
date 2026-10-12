using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Decoding;
using SoundPlayerChiyomaru.Core.Output;

namespace SoundPlayerChiyomaru.Core.Tests;

/// <summary>無音を返すデコーダ。16bit ステレオ 44.1kHz。</summary>
sealed class FakeDecoder(TimeSpan duration) : IAudioDecoder
{
    private long positionBytes;

    public WaveFormat WaveFormat { get; init; } = new(44100, 16, 2);

    public TimeSpan Duration => duration;

    public bool Disposed { get; private set; }

    private long LengthBytes => (long)(duration.TotalSeconds * WaveFormat.SampleRate) * WaveFormat.BlockAlign;

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds((double)positionBytes / WaveFormat.AverageBytesPerSecond);
        set
        {
            long frames = (long)(Math.Max(0, value.TotalSeconds) * WaveFormat.SampleRate);
            positionBytes = Math.Min(frames * WaveFormat.BlockAlign, LengthBytes);
        }
    }

    /// <summary>すべてのサンプルに入れる値。どの曲のサンプルかを見分けるために使う。</summary>
    public short SampleValue { get; init; }

    public int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, LengthBytes - positionBytes);
        buffer[..count].Clear();
        if (SampleValue != 0 && WaveFormat.BitsPerSample == 16)
        {
            for (int i = 0; i + 1 < count; i += 2)
            {
                BitConverter.TryWriteBytes(buffer[i..], SampleValue);
            }
        }
        positionBytes += count;
        return count;
    }

    public void Dispose() => Disposed = true;
}

sealed class FakeOpener : IAudioDecoderOpener
{
    public List<FakeDecoder> Opened { get; } = [];

    public Func<string, IAudioDecoder> Create { get; set; } = _ => new FakeDecoder(TimeSpan.FromSeconds(10));

    public IAudioDecoder Open(string path)
    {
        IAudioDecoder decoder = Create(path);
        if (decoder is FakeDecoder fake)
        {
            Opened.Add(fake);
        }
        return decoder;
    }
}

sealed class FakeOutput : IAudioOutput
{
    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;

    public string DeviceId { get; init; } = "fake";

    public string DeviceName => "Fake " + DeviceId;

    public IWaveProvider? Source { get; private set; }

    public bool Playing { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? PlayException { get; set; }

    public OutputInfo Info { get; private set; } = new(false, null, false, null, null, null);

    /// <summary>排他モードの変換の筒が「再生元が終わった」と知らせた回数。</summary>
    public int EndedCount { get; private set; }

    /// <summary>
    /// 再生元をつなぐ。<paramref name="exclusiveFormat"/> を指定すると、その形式のデバイスへの排他モードの出力として振る舞う
    /// （指定しなければ共有モード）。本物の出力と同じ変換の筒を通すので、出力に渡るバイト列をそのまま確かめられる。
    /// </summary>
    /// <param name="drain">排他モードで、再生元の終端から「止まった」と通知するまでに流す無音の長さ。</param>
    public void Open(IPlaybackSource source, WaveFormat? exclusiveFormat, TimeSpan drain)
    {
        if (exclusiveFormat is null)
        {
            Source = SharedModePipeline.Create(source);
            return;
        }

        bool resample = exclusiveFormat.SampleRate != source.Format.SampleRate;
        bool widenChannels = exclusiveFormat.Channels != source.Format.Channels;
        var pipeline = new ExclusiveModePipeline(source, new ExclusiveFormatChoice(exclusiveFormat, resample, widenChannels), drain);

        // 本物の排他モードの出力と同じく、再生元の終端を「出力が自分から止まった」として通知する。
        pipeline.Ended += () =>
        {
            EndedCount++;
            Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(exception: null, stillRunning: true));
        };
        Source = pipeline;
        Info = new OutputInfo(true, exclusiveFormat, pipeline.BitPerfectCapable, resample ? source.Format.SampleRate : null, pipeline.NormalPathReason, null);
    }

    /// <summary>指定したフレーム数だけ、出力に渡るバイト列をそのまま読む。</summary>
    public byte[] ReadBytes(int frames)
    {
        byte[] buffer = new byte[frames * Source!.WaveFormat.BlockAlign];
        int read = Source.Read(buffer);
        return buffer[..read];
    }

    public void Play()
    {
        if (PlayException is not null)
        {
            throw PlayException;
        }
        Playing = true;
        (Source as ExclusiveModePipeline)?.Paused = false;
    }

    // 本物の排他モードの出力と同じく、一時停止の間は変換の筒に無音を返させる。
    public void Pause()
    {
        Playing = false;
        (Source as ExclusiveModePipeline)?.Paused = true;
    }

    public void Dispose() => Disposed = true;

    /// <summary>再生元を終端まで読み切り、出力が自然に止まった状況を再現する。</summary>
    public void DrainAndStop()
    {
        Drain();
        RaiseStopped(null);
    }

    /// <summary>
    /// 再生元を終端まで読み切るが、停止はまだ通知しない。
    /// 実際の出力は読み切った後もバッファの残りを鳴らしてから通知するので、その間の操作を再現するために使う。
    /// </summary>
    public void Drain()
    {
        byte[] buffer = new byte[4096];
        // 排他モードの変換の筒は終端でも 0 を返さないので、終わったという知らせでも抜ける。
        while (EndedCount == 0 && Source!.Read(buffer) > 0)
        {
        }
    }

    /// <summary>指定した長さだけ再生元を読み、読めた float のサンプルを返す。再生が進んだ状況を再現する。</summary>
    public float[] Read(TimeSpan length)
    {
        WaveFormat format = Source!.WaveFormat;
        byte[] buffer = new byte[(int)(length.TotalSeconds * format.SampleRate) * format.BlockAlign];
        int read = Source.Read(buffer);
        float[] samples = new float[read / sizeof(float)];
        Buffer.BlockCopy(buffer, 0, samples, 0, read);
        return samples;
    }

    public void RaiseStopped(Exception? exception)
    {
        Playing = false;
        Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(exception));
    }

    /// <summary>出力先のデバイスが使えなくなって止まった状況を再現する。</summary>
    public void RaiseDeviceLost()
    {
        Playing = false;
        Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(new IOException("device lost"), deviceLost: true));
    }
}

/// <summary>Post された処理を溜めておき、<see cref="RunAll"/> で実行する。通知が遅れて届く順序を再現するために使う。</summary>
sealed class QueueContext : SynchronizationContext
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();

    public int Pending => queue.Count;

    public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue((d, state));

    public void RunAll()
    {
        while (queue.TryDequeue(out var item))
        {
            item.Callback(item.State);
        }
    }

    /// <summary>溜まっている処理を 1 件だけ実行する。処理の合間に別のスレッドの動きを挟む順序を再現するために使う。</summary>
    public void RunNext()
    {
        if (queue.TryDequeue(out var item))
        {
            item.Callback(item.State);
        }
    }
}

sealed class FakeOutputFactory : IAudioOutputFactory
{
    public List<FakeOutput> Created { get; } = [];

    public Exception? NextPlayException { get; set; }

    /// <summary>出力を作ること自体を失敗させる（使えるデバイスがない状況）。</summary>
    public Exception? CreateException { get; set; }

    /// <summary>作る出力のデバイスを決める関数。指定しなければ固定の ID。</summary>
    public Func<string?>? ResolveDeviceId { get; set; }

    /// <summary>指定すると、その形式のデバイスへの排他モードの出力を作る。指定しなければ共有モード。</summary>
    public WaveFormat? ExclusiveFormat { get; set; }

    /// <summary>排他モードの出力で、再生元の終端から「止まった」と通知するまでに流す無音の長さ。</summary>
    public TimeSpan ExclusiveDrain { get; set; }

    public FakeOutput Last => Created[^1];

    public IAudioOutput Create(IPlaybackSource source)
    {
        if (CreateException is not null)
        {
            throw CreateException;
        }

        string? deviceId = ResolveDeviceId is null ? "fake" : ResolveDeviceId();
        var output = new FakeOutput
        {
            PlayException = NextPlayException,
            DeviceId = deviceId ?? throw new InvalidOperationException("no device"),
        };
        output.Open(source, ExclusiveFormat, ExclusiveDrain);
        Created.Add(output);
        return output;
    }
}

sealed class FakeDeviceService : IAudioDeviceService
{
    public event EventHandler? Changed;

    public List<AudioDeviceInfo> Devices { get; } = [];

    public string? DefaultDeviceId { get; set; }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => Devices.ToArray();

    public bool IsAvailable(string deviceId) => Devices.Any(device => device.Id == deviceId);

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>テストが進めるまで止まっている時計。</summary>
sealed class ManualTime : TimeProvider
{
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => ticks;

    public void Advance(TimeSpan by) => ticks += by.Ticks;
}
