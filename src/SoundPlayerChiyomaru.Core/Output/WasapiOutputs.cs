using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Localization;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>
/// 指定した出力デバイスへ WASAPI で出力する。共有モードと排他モードで共通の部分。
/// </summary>
public abstract class WasapiOutput : IAudioOutput
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private WasapiPlayer? player;

    /// <exception cref="Exception">デバイスが見つからない。</exception>
    protected WasapiOutput(string deviceId)
    {
        try
        {
            device = enumerator.GetDevice(deviceId);
            DeviceId = device.ID;
            DeviceName = device.FriendlyName;
        }
        catch
        {
            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;

    public string DeviceId { get; }

    public string DeviceName { get; }

    public abstract OutputInfo Info { get; }

    /// <summary>デバイスが使えなくなったことを表す例外か（抜かれた、無効にされた、形式が変えられた）。</summary>
    public static bool IsDeviceLost(Exception? exception) =>
        exception is COMException { HResult: AudioClientErrorCode.DeviceInvalidated or AudioClientErrorCode.ResourcesInvalidated };

    public virtual void Play() => player!.Play();

    public virtual void Pause() => player!.Pause();

    public void Dispose()
    {
        if (player is not null)
        {
            player.PlaybackStopped -= OnPlaybackStopped;
            player.Dispose();
        }
        device.Dispose();
        enumerator.Dispose();
    }

    /// <summary>出力を開く。派生クラスのコンストラクターから 1 回だけ呼ぶ。失敗したら後始末をして例外を投げる。</summary>
    /// <param name="configure">共有モード・排他モードなどの設定。デバイスの指定は済んでいる。</param>
    protected void Open(Func<WasapiPlayerBuilder, WasapiPlayerBuilder> configure, Func<WasapiPlayer, IWaveProvider> createSource)
    {
        WasapiPlayer? newPlayer = null;
        try
        {
            newPlayer = configure(new WasapiPlayerBuilder().WithDevice(device)).Build();
            newPlayer.Init(createSource(newPlayer));
        }
        catch
        {
            newPlayer?.Dispose();
            device.Dispose();
            enumerator.Dispose();
            throw;
        }

        player = newPlayer;
        player.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// 出力は動かしたまま、再生元が終端に達したことを知らせる。出力スレッドから呼んでよい。
    /// </summary>
    protected void RaiseSourceEnded() =>
        Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(exception: null, stillRunning: true));

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) =>
        Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(e.Exception, IsDeviceLost(e.Exception)));
}

/// <summary>WASAPI 共有モードで出力する。形式の違いは Windows 側が吸収するので、常に通常経路（float）になる。</summary>
public sealed class WasapiSharedOutput : WasapiOutput
{
    /// <param name="notice">代わりの出力として作られた場合の説明。</param>
    /// <exception cref="Exception">デバイスが見つからない、または開けない。</exception>
    /// <param name="bufferMilliseconds">出力のバッファの長さ。</param>
    public WasapiSharedOutput(string deviceId, IPlaybackSource source, string? notice, int bufferMilliseconds)
        : base(deviceId)
    {
        Info = new OutputInfo(
            Exclusive: false, DeviceFormat: null, BitPerfectCapable: false, ResampledFrom: null, NormalPathReason: null, notice);
        Open(builder => builder.WithSharedMode().WithLatency(bufferMilliseconds), _ => SharedModePipeline.Create(source));
    }

    public override OutputInfo Info { get; }
}

/// <summary>
/// WASAPI 排他モードで出力する。デバイスが受け付ける形式を自分で決め、その形式どおりのバイト列を渡す。
/// </summary>
/// <remarks>
/// 形式の決定と詰め替えを NAudio に任せないのは、形式が合わないときに黙って格下げされるため
/// （24bit をそのまま渡すと 16bit に変換される。Rubix 24 で確認）。
/// </remarks>
public sealed class WasapiExclusiveOutput : WasapiOutput
{
    private readonly OutputInfo info;
    private ExclusiveModePipeline? pipeline;

    /// <exception cref="Exception">
    /// デバイスが見つからない、排他モードで開けない（他のアプリが使用中、許可されていない）、
    /// またはデバイスがこの曲に使える形式を 1 つも受け付けない。
    /// </exception>
    /// <param name="bufferMilliseconds">出力のバッファの長さ。</param>
    /// <param name="maxBitDepth">デバイスへ渡すビット深度の上限。null は上限なし。</param>
    public WasapiExclusiveOutput(string deviceId, IPlaybackSource source, string? notice, int bufferMilliseconds, int? maxBitDepth)
        : base(deviceId)
    {
        OutputInfo? opened = null;
        Open(
            builder => builder.WithExclusiveMode().WithLatency(bufferMilliseconds),
            player =>
            {
                WaveFormat sourceFormat = source.Format;
                ExclusiveFormatChoice choice = ExclusiveFormatResolver.Resolve(sourceFormat, new Probe(player), player.DeviceMixFormat, maxBitDepth)
                    ?? throw new NotSupportedException(UiLanguage.Text(
                        $"デバイスが排他モードで {sourceFormat.SampleRate} Hz / {sourceFormat.Channels} ch の音声を受け付けません",
                        $"the device does not accept {sourceFormat.SampleRate} Hz / {sourceFormat.Channels} ch audio in exclusive mode"));

                // 終端の後に流す無音は、出力のバッファの長さの 2 倍。渡し済みの音が鳴り終わるのを待つため。
                pipeline = new ExclusiveModePipeline(
                    source, choice, TimeSpan.FromMilliseconds(player.LatencyMilliseconds * 2));

                // 排他モードでは、再生元の終端を出力（NAudio）に伝えない。代わりにここで「再生元が終端に達した」と知らせる。
                pipeline.Ended += RaiseSourceEnded;

                // 渡した形式を NAudio がそのまま使うこと（変換を挟まないこと）を確かめる。挟まれる場合はビットパーフェクトとは言えない。
                bool passesThrough = player.GetPlaybackCapability(pipeline.WaveFormat).Conversions.Count == 0;
                opened = new OutputInfo(
                    Exclusive: true,
                    choice.DeviceFormat,
                    BitPerfectCapable: pipeline.BitPerfectCapable && passesThrough,
                    ResampledFrom: choice.SampleRateChanged ? sourceFormat.SampleRate : null,
                    NormalPathReason: passesThrough
                        ? pipeline.NormalPathReason
                        : UiLanguage.Text("出力側で形式を変換", "format converted by the output"),
                    notice);
                return pipeline;
            });
        info = opened!;
    }

    public override OutputInfo Info => info;

    public override void Play()
    {
        pipeline!.Paused = false;
        base.Play();
    }

    /// <summary>
    /// 出力は止めずに、無音を渡す状態にする（理由は <see cref="ExclusiveModePipeline.Paused"/>）。
    /// 渡し済みの音（最大で出力のバッファの長さ）は、鳴り終わってから無音になる。
    /// </summary>
    public override void Pause() => pipeline!.Paused = true;

    private sealed class Probe(WasapiPlayer player) : IExclusiveFormatProbe
    {
        public bool IsSupported(WaveFormat format) => player.IsFormatSupported(format);
    }
}

/// <summary>
/// 設定に従って出力を作る。希望どおりに作れなければ、排他モード → 共有モード → 既定のデバイス、の順に代わりを試す。
/// </summary>
public sealed class WasapiOutputFactory(OutputDeviceSelection selection) : IAudioOutputFactory
{
    public IAudioOutput Create(IPlaybackSource source)
    {
        string wanted = selection.ResolveDeviceId()
            ?? throw new InvalidOperationException(UiLanguage.Text("使える出力デバイスがありません。", "No output device is available."));

        int buffer = selection.BufferMilliseconds;
        int? maxBitDepth = selection.MaxBitDepth;
        var attempts = new List<OutputAttempt>();
        if (selection.PreferExclusive)
        {
            attempts.Add(new OutputAttempt(
                UiLanguage.Text("排他モードを使えません", "Cannot use exclusive mode"),
                UiLanguage.Text("排他モードで再生しています", "Playing in exclusive mode"),
                notice => new WasapiExclusiveOutput(wanted, source, notice, buffer, maxBitDepth)));
        }
        attempts.Add(new OutputAttempt(
            UiLanguage.Text("このデバイスを使えません", "Cannot use this device"),
            UiLanguage.Text("共有モードで再生しています", "Playing in shared mode"),
            notice => new WasapiSharedOutput(wanted, source, notice, buffer)));

        if (selection.DefaultDeviceId is { } fallback && fallback != wanted)
        {
            attempts.Add(new OutputAttempt(
                UiLanguage.Text("既定のデバイスを使えません", "Cannot use the default device"),
                UiLanguage.Text("既定のデバイスで再生しています", "Playing on the default device"),
                notice => new WasapiSharedOutput(fallback, source, notice, buffer)));
        }

        return OutputFallback.Create(attempts);
    }
}
