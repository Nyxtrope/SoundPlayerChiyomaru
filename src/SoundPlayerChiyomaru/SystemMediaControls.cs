using System.ComponentModel;
using Avalonia.Threading;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;
using Windows.Media;

namespace SoundPlayerChiyomaru;

/// <summary>
/// Windows のシステムメディアコントロール（SMTC）との連携。キーボードのメディアキーや Windows のメディア操作パネルから
/// 再生を操作でき、そこに曲名が表示される。
/// </summary>
sealed class SystemMediaControls : IDisposable
{
    private readonly SystemMediaTransportControls controls;
    private readonly MainWindowViewModel viewModel;
    private (string? Title, string? Artist, string? Album) shown;
    private bool disposed;

    private SystemMediaControls(SystemMediaTransportControls controls, MainWindowViewModel viewModel)
    {
        this.controls = controls;
        this.viewModel = viewModel;

        controls.IsEnabled = true;
        controls.DisplayUpdater.Type = MediaPlaybackType.Music;
        Refresh();

        // ここまで成功してから購読する。途中で失敗した場合に、破棄されない購読を残さないため。
        controls.ButtonPressed += OnButtonPressed;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>連携を始める。この環境で使えなければ null（再生には影響しないので、使えないまま動かす）。</summary>
    public static SystemMediaControls? TryCreate(IntPtr windowHandle, MainWindowViewModel viewModel)
    {
        try
        {
            return new SystemMediaControls(SystemMediaTransportControlsInterop.GetForWindow(windowHandle), viewModel);
        }
        catch (Exception)
        {
            // WinRT の呼び出しは、失敗の種類ごとに異なる例外（COMException など）を投げる。
            // どの失敗でも「連携なしで動かす」以外にできることがないので、まとめて扱う。
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        viewModel.PropertyChanged -= OnViewModelChanged;
        try
        {
            controls.ButtonPressed -= OnButtonPressed;
            controls.IsEnabled = false;
        }
        catch (Exception)
        {
            // 連携が既に壊れている場合の後始末。できることがないので無視する。
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // このイベントは UI スレッド以外から届く。
        SystemMediaTransportControlsButton button = args.Button;
        Dispatcher.UIThread.Post(() =>
        {
            // ウィンドウを閉じた後に届いた操作で、終了処理中に再生を始め直さないようにする。
            if (disposed)
            {
                return;
            }

            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    viewModel.SetPlaying(true);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    viewModel.SetPlaying(false);
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    viewModel.Stop();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    viewModel.Next();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    viewModel.Previous();
                    break;
            }
        });
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainWindowViewModel.State)
            or nameof(MainWindowViewModel.CurrentItem)
            or nameof(MainWindowViewModel.HasTrack)
            or nameof(MainWindowViewModel.HasItems)))
        {
            return;
        }

        try
        {
            Refresh();
        }
        catch (Exception)
        {
            // この通知は再生の状態遷移の途中で届く。ここから例外を出すと、その遷移を途中で打ち切ってしまう。
            // 連携が壊れたものとして止め、再生は続ける。
            Dispose();
        }
    }

    private void Refresh()
    {
        controls.IsPlayEnabled = viewModel.CanPlayPause;
        controls.IsPauseEnabled = viewModel.CanPlayPause;
        controls.IsStopEnabled = viewModel.HasTrack;
        controls.IsNextEnabled = viewModel.HasItems;
        controls.IsPreviousEnabled = viewModel.HasItems;

        // 曲を開いていなくても、プレイリストに曲があれば「再生」で始められる（前回のプレイリストを復元した直後など）。
        // その状態を Closed にすると Windows が操作の対象から外してしまうので、Stopped にする。
        controls.PlaybackStatus = viewModel.State switch
        {
            PlayerState.Playing => MediaPlaybackStatus.Playing,
            PlayerState.Paused => MediaPlaybackStatus.Paused,
            _ => viewModel.CanPlayPause ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Closed,
        };

        // 再生対象の曲が選ばれていれば、まだ開いていなくても表示する。
        PlaylistItem? item = viewModel.CanPlayPause ? viewModel.CurrentItem : null;
        (string?, string?, string?) info = (item?.DisplayTitle, item?.Artist, item?.Album);
        if (info != shown)
        {
            shown = info;
            MusicDisplayProperties music = controls.DisplayUpdater.MusicProperties;
            music.Title = info.Item1 ?? "";
            music.Artist = info.Item2 ?? "";
            music.AlbumTitle = info.Item3 ?? "";
            controls.DisplayUpdater.Update();
        }
    }
}
