using SoundPlayerChiyomaru.Core.Output;

namespace SoundPlayerChiyomaru.Core.Playback;

/// <summary>
/// 出力に使うべきデバイスが変わったら、再生サービスに出力を作り直させる。
/// 利用者がデバイスを選び直した、選んだデバイスが抜かれた・戻ってきた、Windows の既定のデバイスが変わった、のどれにも同じ仕組みで対応する。
/// 1 つのスレッド（UI スレッド）から使うこと。
/// </summary>
public sealed class OutputDeviceFollower : IDisposable
{
    private readonly PlaybackService playback;
    private readonly OutputDeviceSelection selection;
    private readonly IAudioDeviceService devices;
    private string? lastWanted;

    public OutputDeviceFollower(PlaybackService playback, OutputDeviceSelection selection, IAudioDeviceService devices)
    {
        this.playback = playback;
        this.selection = selection;
        this.devices = devices;
        lastWanted = selection.ResolveDeviceId();

        devices.Changed += OnChanged;
        selection.PreferredChanged += OnChanged;
    }

    public void Dispose()
    {
        devices.Changed -= OnChanged;
        selection.PreferredChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (selection.ResolveDeviceId() is not { } wanted)
        {
            lastWanted = null;
            return;
        }

        // デバイスの変化は 1 回の操作で何度も通知される。作り直すのは、使うべきデバイスが前回から変わり、
        // かつ今の出力がそのデバイスではないときだけ。
        // - 前回と比べるのは、使うべきデバイスで出力を作れず代わりのデバイスで鳴らしている間に、通知のたびに作り直さないため。
        // - 今の出力と比べるのは、通知より先に出力がデバイスの喪失を検知して、既に正しいデバイスで作り直している場合があるため。
        bool changed = wanted != lastWanted;
        lastWanted = wanted;

        if (playback.IsWaitingForDevice
            || (changed && playback.OutputDeviceId is { } current && current != wanted))
        {
            playback.RestartOutput();
        }
    }
}
