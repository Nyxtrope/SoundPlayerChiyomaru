namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>
/// 音声の出力先。WASAPI の共有モードと排他モード（将来の ASIO）を同じ形で差し替えるための境界。
/// 作られた時点で、再生元を読める状態になっている。
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>
    /// 出力が自分から止まったとき（再生元の終端、デバイスの切断などのエラー）に発生する。
    /// 出力スレッドから呼ばれることがあるため、ハンドラの中でこの出力を破棄してはならない。
    /// </summary>
    event EventHandler<AudioOutputStoppedEventArgs>? Stopped;

    /// <summary>出力先のデバイスを識別する ID。</summary>
    string DeviceId { get; }

    string DeviceName { get; }

    /// <summary>どの形式・どの経路で鳴らしているか。</summary>
    OutputInfo Info { get; }

    void Play();

    void Pause();
}

public sealed class AudioOutputStoppedEventArgs(Exception? exception, bool deviceLost = false, bool stillRunning = false) : EventArgs
{
    /// <summary>
    /// 出力そのものは動き続けていて、再生元が終端に達したことだけを知らせている。
    /// この場合、再生元がまた読める状態になっていれば（シークで戻された、次の曲へ移った）、出力はそのまま再生を続けている。
    /// </summary>
    public bool StillRunning { get; } = stillRunning;

    /// <summary>エラーで止まった場合の原因。再生元の終端で止まった場合は null。</summary>
    public Exception? Exception { get; } = exception;

    /// <summary>
    /// 出力先のデバイスが使えなくなって止まった（抜かれた、無効にされた、形式が変えられた）。
    /// 出力を作り直せば再生を続けられる可能性がある。
    /// </summary>
    public bool DeviceLost { get; } = deviceLost;
}

public interface IAudioOutputFactory
{
    /// <summary>再生元を読む出力を作る。希望どおりに作れなければ、代わりの出力（共有モード、既定のデバイス）を試す。</summary>
    /// <exception cref="Exception">どの出力も作れない（デバイスがない、など）。</exception>
    IAudioOutput Create(IPlaybackSource source);
}
