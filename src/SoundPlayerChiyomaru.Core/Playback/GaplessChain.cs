using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Decoding;
using SoundPlayerChiyomaru.Core.Output;

namespace SoundPlayerChiyomaru.Core.Playback;

/// <summary>開いている 1 曲。デコーダと、それを float で読むための変換をまとめる。</summary>
internal sealed class TrackSource : IDisposable
{
    private readonly IAudioDecoder decoder;
    private volatile float gain = 1f;
    private ReplayGainTags? replayGain;
    private bool replayGainLoaded;

    private TrackSource(string path, IAudioDecoder decoder)
    {
        this.decoder = decoder;
        Path = path;
        Source = new DecoderSource(decoder);
        Samples = Source.ToSampleProvider();
    }

    public string Path { get; }

    /// <summary>デコード結果のバイト列をそのまま読む口。</summary>
    public DecoderSource Source { get; }

    /// <summary>32bit float に変換して読む口。<see cref="Source"/> と同じデコーダを読むので、どちらで読んでも位置は共通。</summary>
    public ISampleProvider Samples { get; }

    public WaveFormat Format => decoder.WaveFormat;

    public TimeSpan Duration => decoder.Duration;

    /// <summary>float で読んだ音声に掛ける倍率（ReplayGain）。掛けない場合は 1。出力スレッドから読まれる。</summary>
    public float Gain => gain;

    /// <summary>掛けているゲイン（dB）。掛けていなければ null。</summary>
    public double? GainDb { get; private set; }

    /// <summary>この曲の ReplayGain のタグ。最初に呼ばれたときにファイルから読み、以後は覚えている値を返す。</summary>
    public ReplayGainTags? LoadReplayGain(IReplayGainReader reader)
    {
        if (!replayGainLoaded)
        {
            replayGain = reader.ReadReplayGain(Path);
            replayGainLoaded = true;
        }
        return replayGain;
    }

    public void SetGain(double? db)
    {
        GainDb = db;
        gain = db is { } value ? ReplayGainTags.ToScale(value) : 1f;
    }

    /// <exception cref="Exception">開けない、または float に変換できない形式。</exception>
    public static TrackSource Open(IAudioDecoderOpener decoders, string path)
    {
        IAudioDecoder decoder = decoders.Open(path);
        try
        {
            return new TrackSource(path, decoder);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public void Dispose() => decoder.Dispose();
}

/// <summary>
/// 出力に渡す再生元。今の曲が終端に達したとき、次の曲が予約されていて出力がそれを受け入れられれば、
/// 出力を止めずにそのまま読み継ぐ（ギャップレス再生）。
/// 読み取りは出力スレッド、それ以外は UI スレッドから呼ばれる。
/// </summary>
internal sealed class GaplessChain : IPlaybackSource
{
    private readonly Lock gate = new();
    private readonly List<TrackSource> finished = [];
    private TrackSource active;
    private TrackSource? next;
    private volatile float volume = 1f;
    private volatile Func<WaveFormat, bool>? canContinueWith;
    private volatile bool appliesGain;

    public GaplessChain(TrackSource first)
    {
        active = first;
    }

    /// <summary>次の曲へ乗り換えた。出力スレッドから呼ばれる。</summary>
    public event Action<GaplessChain>? Advanced;

    public WaveFormat Format => Active.Format;

    public float Volume
    {
        get => volume;
        set => volume = value;
    }

    public bool AppliesGain
    {
        get => appliesGain;
        set => appliesGain = value;
    }

    public Func<WaveFormat, bool>? CanContinueWith
    {
        get => canContinueWith;
        set => canContinueWith = value;
    }

    /// <summary>出力が今読んでいる曲。</summary>
    public TrackSource Active
    {
        get
        {
            lock (gate)
            {
                return active;
            }
        }
    }

    /// <summary>
    /// 今の曲からそのまま読み継げる形式か（サンプルレートとチャンネル数が同じ）。
    /// 違う曲は、どの出力でも続けては再生できないので、予約しても使われない。
    /// </summary>
    public bool CanChain(TrackSource track)
    {
        WaveFormat current = Active.Format;
        return track.Format.SampleRate == current.SampleRate && track.Format.Channels == current.Channels;
    }

    /// <summary>これから読まれる曲（今の曲と、予約してある次の曲）。</summary>
    public TrackSource[] PendingTracks()
    {
        lock (gate)
        {
            return next is null ? [active] : [active, next];
        }
    }

    /// <summary>次の曲の予約を入れ替える。</summary>
    /// <returns>それまで予約されていて、まだ再生が始まっていなかった曲。呼び出し側が破棄する。</returns>
    public TrackSource? ReplaceNext(TrackSource? track)
    {
        lock (gate)
        {
            TrackSource? previous = next;
            next = track;
            return previous;
        }
    }

    /// <summary>読み終えて乗り換え済みの曲を取り出す。呼び出し側が破棄する。</summary>
    public TrackSource[] TakeFinished()
    {
        lock (gate)
        {
            TrackSource[] result = [.. finished];
            finished.Clear();
            return result;
        }
    }

    /// <summary>抱えている曲をすべて破棄する。出力を止めてから呼ぶこと。</summary>
    public void DisposeTracks()
    {
        lock (gate)
        {
            active.Dispose();
            next?.Dispose();
            next = null;
            foreach (TrackSource track in finished)
            {
                track.Dispose();
            }
            finished.Clear();
        }
    }

    public int ReadFloat(Span<float> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            TrackSource track = Active;
            int read = track.Samples.Read(buffer[total..]);
            if (read > 0)
            {
                // ゲインは曲ごとに違う。読んだ曲の値を掛けるので、曲の境界でちょうど切り替わる。
                float gain = track.Gain;
                if (gain != 1f)
                {
                    Span<float> samples = buffer.Slice(total, read);
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] *= gain;
                    }
                }
                total += read;
                continue;
            }

            if (!TryAdvance())
            {
                break;
            }
        }
        return total;
    }

    public int ReadPcm(Span<byte> buffer, int maxSamples, out WaveFormat format)
    {
        while (true)
        {
            TrackSource track = Active;
            format = track.Format;
            int bytes = maxSamples * (format.BitsPerSample / 8);
            int read = track.Source.Read(buffer[..bytes]);
            if (read > 0)
            {
                return read;
            }

            if (!TryAdvance())
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 予約してある次の曲へ乗り換える。予約がない、または出力が受け入れられない形式なら乗り換えない
    /// （その場合、出力は今の曲の終端で止まり、次の曲は出力を作り直して再生される）。
    /// </summary>
    private bool TryAdvance()
    {
        lock (gate)
        {
            if (next is null || !Accepts(next.Format))
            {
                return false;
            }
            finished.Add(active);
            active = next;
            next = null;
        }
        Advanced?.Invoke(this);
        return true;
    }

    private bool Accepts(WaveFormat nextFormat)
    {
        // サンプルレートかチャンネル数が違う曲は、どの出力でもそのままは続けられない。
        WaveFormat current = active.Format;
        if (nextFormat.SampleRate != current.SampleRate || nextFormat.Channels != current.Channels)
        {
            return false;
        }
        return canContinueWith?.Invoke(nextFormat) ?? true;
    }
}
