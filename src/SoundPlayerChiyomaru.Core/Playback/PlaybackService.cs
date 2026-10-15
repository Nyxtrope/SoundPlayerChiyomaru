using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Decoding;
using SoundPlayerChiyomaru.Core.Localization;
using SoundPlayerChiyomaru.Core.Output;

namespace SoundPlayerChiyomaru.Core.Playback;

public enum PlayerState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// 曲を開いて再生する。次の曲を予約しておくと、形式が合えば出力を止めずに続けて再生する。
/// メソッドとプロパティは 1 つのスレッド（UI スレッド）から呼ぶこと。イベントも同じスレッドで発生する。
/// </summary>
public sealed class PlaybackService : IDisposable
{
    private readonly IAudioDecoderOpener decoders;
    private readonly IAudioOutputFactory outputs;
    private readonly SynchronizationContext? context;
    private readonly IReplayGainReader? replayGain;
    private ReplayGainMode replayGainMode;

    private TrackSource? current;
    private GaplessChain? chain;
    private IAudioOutput? output;
    private float volume = 1f;
    private int nextRequest;

    private readonly TimeProvider time;
    private long? firstDeviceLoss;
    private int deviceLossRetries;

    /// <summary>デバイスを失った直後に、出力を作り直して再生を続ける試みを繰り返してよい回数。</summary>
    private const int MaxDeviceLossRetries = 2;

    /// <summary>最初にデバイスを失ってからこの時間のうちに起きた喪失を、同じ障害の続きとして回数を数える。</summary>
    private static readonly TimeSpan DeviceLossWindow = TimeSpan.FromSeconds(10);

    /// <param name="context">
    /// 出力からの通知を処理するスレッドのコンテキスト。出力は自分のスレッドから通知することがあり、
    /// その場で出力や曲を破棄できないため、ここへ投げ直す。null の場合は通知を受けたスレッドでそのまま処理する（テスト用）。
    /// </param>
    public PlaybackService(
        IAudioDecoderOpener decoders,
        IAudioOutputFactory outputs,
        SynchronizationContext? context,
        TimeProvider? time = null,
        IReplayGainReader? replayGain = null)
    {
        this.replayGain = replayGain;
        this.decoders = decoders;
        this.outputs = outputs;
        this.context = context;
        this.time = time ?? TimeProvider.System;
    }

    public event EventHandler? StateChanged;

    /// <summary>出力が作られた、または手放された（<see cref="OutputDeviceId"/> が変わった）。</summary>
    public event EventHandler? OutputChanged;

    /// <summary>曲の終端まで再生して停止した（予約した次の曲へ続けられなかった場合を含む）。</summary>
    public event EventHandler? TrackEnded;

    /// <summary>出力を止めずに、予約してあった次の曲へ移った。<see cref="CurrentPath"/> は移った先を指す。</summary>
    public event EventHandler? TrackAdvanced;

    /// <summary>再生中に出力がエラーで止まった（デバイスの切断など）。</summary>
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;

    public PlayerState State { get; private set; }

    public string? CurrentPath => current?.Path;

    /// <summary>開いているファイルのデコード結果の形式。開いていなければ null。</summary>
    public WaveFormat? SourceFormat => current?.Format;

    public TimeSpan Duration => current?.Duration ?? TimeSpan.Zero;

    public TimeSpan Position => current?.Source.Position ?? TimeSpan.Zero;

    /// <summary>今の出力が使っているデバイスの ID。出力がなければ（停止中など）null。</summary>
    public string? OutputDeviceId => output?.DeviceId;

    /// <summary>今の出力が使っているデバイスの名前。出力がなければ null。</summary>
    public string? OutputDeviceName => output?.DeviceName;

    /// <summary>
    /// 再生中にデバイスを失い、出力を作り直せなかったので一時停止にしている。
    /// デバイスが使えるようになったら <see cref="RestartOutput"/> で再生を再開する。利用者が再生・停止などの操作をすると解除される。
    /// </summary>
    public bool IsWaitingForDevice { get; private set; }

    /// <summary>0.0（無音）から 1.0（最大）。範囲外は丸められる。</summary>
    public float Volume
    {
        get => volume;
        set
        {
            volume = float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);
            if (chain is not null)
            {
                chain.Volume = volume;
            }
        }
    }

    /// <summary>
    /// 今の出力がどの形式・どの経路で鳴らしているか。出力がなければ（停止中など）null。
    /// 変わったときは <see cref="OutputChanged"/> が発生する。
    /// </summary>
    public OutputInfo? OutputInfo => output?.Info;

    /// <summary>
    /// 今、デコードしたサンプル値を変えずにデバイスへ渡しているか（ビットパーフェクト）。
    /// 排他モードで値を変えずに渡せる構成になっていて、かつ音量が最大で ReplayGain が無効の間だけ true。
    /// </summary>
    public bool IsBitPerfect => output?.Info.BitPerfectCapable == true && volume >= 1f && !AppliesReplayGain;

    /// <summary>
    /// ReplayGain の使い方。再生中に変えると、出力を止めずにその場で切り替わる。
    /// 有効な間は、タグのない曲も含めて通常経路で処理する（ビットパーフェクトではなくなる）。
    /// タグを読む手段を渡されていない場合は、設定しても何も掛からない。
    /// </summary>
    public ReplayGainMode ReplayGainMode
    {
        get => replayGainMode;
        set
        {
            if (replayGainMode == value)
            {
                return;
            }

            replayGainMode = value;
            if (chain is null)
            {
                return;
            }

            CatchUp();
            foreach (TrackSource track in chain.PendingTracks())
            {
                ApplyReplayGain(track);
            }
            chain.AppliesGain = AppliesReplayGain;
        }
    }

    /// <summary>今の曲に掛けている ReplayGain のゲイン（dB）。無効、またはタグがなくて掛けていなければ null。</summary>
    public double? ReplayGainDb => current?.GainDb;

    private bool AppliesReplayGain => replayGainMode != ReplayGainMode.Off && replayGain is not null;

    /// <summary>
    /// ファイルを開く。開いていた曲と次の曲の予約は捨て、停止状態・先頭位置になる。
    /// 失敗した場合は例外を投げ、何も開いていない状態になる。
    /// </summary>
    public void Open(string path)
    {
        Close();

        TrackSource track = TrackSource.Open(decoders, path);
        ApplyReplayGain(track);
        chain = new GaplessChain(track) { Volume = volume, AppliesGain = AppliesReplayGain };
        chain.Advanced += OnChainAdvanced;
        current = track;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 今の曲が終端に達したときに続けて再生する曲を予約する。null で予約を取り消す。
    /// サンプルレートとチャンネル数が今の曲と同じ場合だけ、出力を止めずに続く（<see cref="TrackAdvanced"/>）。
    /// 形式が違う、または開けない場合は予約されず、今の曲の終端で <see cref="TrackEnded"/> になる。
    /// </summary>
    public void SetNext(string? path)
    {
        CatchUp();
        int request = ++nextRequest;
        chain?.ReplaceNext(null)?.Dispose();
        if (path is null || chain is null)
        {
            return;
        }

        // 曲の切り替え直後に呼ばれることが多い。次の曲を開く間に画面の更新を待たせないよう、後回しにする。
        if (context is null)
        {
            PrepareNext(path, request);
        }
        else
        {
            context.Post(_ => PrepareNext(path, request), null);
        }
    }

    /// <summary>再生を始める、または一時停止から再開する。出力を初期化できない場合は例外を投げ、停止状態になる。</summary>
    public void Play()
    {
        CatchUp();
        IsWaitingForDevice = false;
        ResetDeviceLossCount();
        if (current is null || State == PlayerState.Playing)
        {
            return;
        }

        try
        {
            StartOutput();
        }
        catch
        {
            ReleaseOutput();
            SetState(PlayerState.Stopped);
            throw;
        }

        SetState(PlayerState.Playing);
    }

    public void Pause()
    {
        IsWaitingForDevice = false;
        if (State != PlayerState.Playing)
        {
            return;
        }

        output!.Pause();
        SetState(PlayerState.Paused);
    }

    /// <summary>
    /// 再生状態と再生位置を保ったまま、出力だけを作り直す。出力先のデバイスを変えるとき、
    /// またはデバイスを失って待っているときに呼ぶ。作り直せなければ一時停止にして <see cref="PlaybackFailed"/> を通知する。
    /// 古い出力がまだ鳴らしていなかった分（最大で出力のバッファの長さ）は、鳴らさずに先へ進む。
    /// </summary>
    public void RestartOutput()
    {
        CatchUp();
        if (current is null)
        {
            return;
        }

        bool resume = IsWaitingForDevice || State == PlayerState.Playing;
        if (output is null && !IsWaitingForDevice)
        {
            return;
        }

        ReleaseOutput();
        if (!resume)
        {
            // 一時停止中。次に再生するときに新しい出力が作られる。
            return;
        }

        try
        {
            StartOutput();
        }
        catch (Exception ex)
        {
            WaitForDevice(ex, notify: !IsWaitingForDevice);
            return;
        }

        IsWaitingForDevice = false;
        ResetDeviceLossCount();
        SetState(PlayerState.Playing);
    }

    /// <summary>再生を止めて先頭に戻す。</summary>
    public void Stop()
    {
        CatchUp();
        IsWaitingForDevice = false;
        if (current is null)
        {
            return;
        }

        ReleaseOutput();

        // 出力が止まるまでの間に次の曲へ乗り換えていることがある。止まった時点の曲を先頭に戻す。
        CatchUp();
        current!.Source.Position = TimeSpan.Zero;
        // 既に停止中でも位置は先頭に戻るので、表示を更新できるように必ず通知する。
        SetState(PlayerState.Stopped, force: true);
    }

    /// <summary>再生位置を変える。範囲外は曲の先頭・終端に丸められる。</summary>
    public void Seek(TimeSpan position)
    {
        CatchUp();
        if (current is null)
        {
            return;
        }

        current.Source.Position = position;
    }

    /// <summary>開いている曲を閉じる。</summary>
    public void Close()
    {
        if (chain is null)
        {
            return;
        }

        nextRequest++;
        IsWaitingForDevice = false;
        ReleaseOutput();
        chain.Advanced -= OnChainAdvanced;
        chain.DisposeTracks();
        chain = null;
        current = null;
        SetState(PlayerState.Stopped, force: true);
    }

    public void Dispose() => Close();

    private void PrepareNext(string path, int request)
    {
        if (request != nextRequest || chain is null)
        {
            return;
        }

        TrackSource track;
        try
        {
            track = TrackSource.Open(decoders, path);
        }
        catch (Exception)
        {
            // 予約できなくても今の曲の再生には影響しない。終端で TrackEnded になり、
            // そこで改めて開こうとした側がエラーを扱う。
            return;
        }

        if (chain.CanChain(track))
        {
            ApplyReplayGain(track);
            chain.ReplaceNext(track)?.Dispose();
        }
        else
        {
            track.Dispose();
        }
    }

    private void ApplyReplayGain(TrackSource track) =>
        track.SetGain(AppliesReplayGain ? track.LoadReplayGain(replayGain!)?.ResolveDb(replayGainMode) : null);

    private void StartOutput()
    {
        bool created = false;
        if (output is null)
        {
            current!.Source.TakeSoughtAfterEnd();
            output = outputs.Create(chain!);
            output.Stopped += OnOutputStopped;
            created = true;
        }
        output.Play();

        // 再生を始められてから知らせる。途中で失敗した場合は、呼び出し側が出力を手放すときに知らせる。
        if (created)
        {
            OutputChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 出力を作れないので、位置を保ったまま一時停止にして、デバイスが使えるようになるのを待つ。
    /// </summary>
    private void WaitForDevice(Exception reason, bool notify)
    {
        ReleaseOutput();
        IsWaitingForDevice = true;
        SetState(PlayerState.Paused);
        if (notify)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(reason));
        }
    }

    /// <summary>
    /// デバイスを失った直後に、すぐ出力を作り直してよいか。Windows のデバイス一覧の更新が遅れていると、
    /// 作り直した出力もすぐに失われる。それを際限なく繰り返さないよう、短い時間のうちの回数を制限する。
    /// </summary>
    private bool CanRetryAfterDeviceLoss()
    {
        long now = time.GetTimestamp();
        if (firstDeviceLoss is not { } first || time.GetElapsedTime(first, now) > DeviceLossWindow)
        {
            // 新しい障害の始まり。ここから窓の長さのうちに起きた回数を数える。
            firstDeviceLoss = now;
            deviceLossRetries = 0;
        }

        deviceLossRetries++;
        return deviceLossRetries <= MaxDeviceLossRetries;
    }

    /// <summary>
    /// デバイスを失った回数の数え直し。デバイスの変化や利用者の操作で再生を始め直せたら、前の障害は終わったものとして扱う。
    /// </summary>
    private void ResetDeviceLossCount() => firstDeviceLoss = null;

    private void OnChainAdvanced(GaplessChain source)
    {
        if (context is null)
        {
            HandleAdvanced(source);
        }
        else
        {
            context.Post(_ => HandleAdvanced(source), null);
        }
    }

    /// <summary>
    /// 出力スレッドが次の曲へ乗り換えていたら、こちらの「今の曲」を追いつかせる（必要なら <see cref="TrackAdvanced"/> が発生する）。
    /// 乗り換えの通知は遅れて届くので、今の曲を基準に何かを決める側は、その前に呼ぶこと。
    /// このクラスの操作は内部で呼んでいる。
    /// </summary>
    public void CatchUp()
    {
        if (chain is not null)
        {
            HandleAdvanced(chain);
        }
    }

    private void HandleAdvanced(GaplessChain source)
    {
        // 通知が届くまでの間に別の曲を開いていれば、古い連結からの通知なので無視する。
        if (!ReferenceEquals(source, chain))
        {
            return;
        }

        TrackSource[] finished = source.TakeFinished();
        if (finished.Length == 0)
        {
            return;
        }

        current = source.Active;
        foreach (TrackSource track in finished)
        {
            track.Dispose();
        }
        TrackAdvanced?.Invoke(this, EventArgs.Empty);
    }

    private void OnOutputStopped(object? sender, AudioOutputStoppedEventArgs e)
    {
        if (context is null)
        {
            HandleOutputStopped(sender, e);
        }
        else
        {
            context.Post(_ => HandleOutputStopped(sender, e), null);
        }
    }

    private void HandleOutputStopped(object? sender, AudioOutputStoppedEventArgs e)
    {
        // 通知が届くまでの間に停止や別の曲への切り替えが済んでいれば、古い出力からの通知なので無視する。
        if (output is null || !ReferenceEquals(sender, output))
        {
            return;
        }

        CatchUp();
        if (e.DeviceLost)
        {
            HandleDeviceLost(e.Exception);
            return;
        }

        DecoderSource source = current!.Source;
        Exception? failure = e.Exception;
        bool ended = failure is null && source.EndReached;
        bool soughtAfterEnd = failure is null && source.TakeSoughtAfterEnd();

        // 動き続けている出力からの「再生元が終わった」という知らせは、届くまでの間に古くなっていることがある
        // （シークで戻された、次の曲へ移った）。その場合、出力は既に続きを鳴らしているので、何もしない。
        // 次に終端に達したときに、改めて知らせが来る。
        if (e.StillRunning && failure is null && !ended)
        {
            return;
        }
        ReleaseOutput();

        // 出力が終端を読んでから止まるまでの間にシークされた。曲は終わっていないので、シーク先から再生を続ける。
        if (!ended && soughtAfterEnd && State == PlayerState.Playing)
        {
            try
            {
                StartOutput();
                return;
            }
            catch (Exception ex)
            {
                ReleaseOutput();
                failure = ex;
            }
        }

        if (ended)
        {
            try
            {
                source.Position = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                // 巻き戻せなかった（再生中にファイルが読めなくなったなど）。この曲はもう読めないので閉じる。
                Close();
                failure = ex;
            }
        }
        SetState(PlayerState.Stopped);

        if (failure is not null)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(failure));
        }
        else if (ended)
        {
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 出力先のデバイスが使えなくなった（抜かれた、無効にされた、形式が変えられた）。曲は終わっていないので、
    /// 使えるデバイスで出力を作り直して同じ位置から続ける。作り直せなければ、使えるようになるまで待つ。
    /// </summary>
    private void HandleDeviceLost(Exception? reason)
    {
        bool wasPlaying = State == PlayerState.Playing;
        ReleaseOutput();
        if (!wasPlaying)
        {
            // 一時停止中。次に再生するときに新しい出力が作られる。
            return;
        }

        reason ??= new InvalidOperationException(UiLanguage.Text("出力デバイスが使えなくなりました。", "The output device is no longer available."));
        if (CanRetryAfterDeviceLoss())
        {
            try
            {
                StartOutput();
                return;
            }
            catch (Exception ex)
            {
                reason = ex;
            }
        }
        WaitForDevice(reason, notify: true);
    }

    private void ReleaseOutput()
    {
        if (output is null)
        {
            return;
        }

        output.Stopped -= OnOutputStopped;
        output.Dispose();
        output = null;

        // 次の曲へ続けられるかの条件は、出力ごとに違う。出力がない間は条件を付けない。
        if (chain is not null)
        {
            chain.CanContinueWith = null;
        }
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(PlayerState state, bool force = false)
    {
        if (State == state && !force)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PlaybackFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
