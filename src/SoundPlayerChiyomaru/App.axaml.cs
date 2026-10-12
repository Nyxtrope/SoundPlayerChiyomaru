using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Win32;
using SoundPlayerChiyomaru.Core.Decoding;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Settings;
using SoundPlayerChiyomaru.Core.Shell;
using SoundPlayerChiyomaru.Core.Tags;

namespace SoundPlayerChiyomaru;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupOptions options = Program.Options;
            SynchronizationContext context = SynchronizationContext.Current
                ?? throw new InvalidOperationException("UI スレッドの SynchronizationContext がありません。");

            var decoders = DecoderRegistry.CreateDefault();
            WasapiDeviceService? wasapiDevices = WasapiDeviceService.TryCreate();
            IAudioDeviceService deviceService = wasapiDevices is null ? new NoAudioDeviceService() : wasapiDevices;
            var deviceSelection = new OutputDeviceSelection(deviceService);
            var tagReader = new TagReader();
            var playback = new PlaybackService(decoders, new WasapiOutputFactory(deviceSelection), context, replayGain: tagReader);
            var deviceFollower = new OutputDeviceFollower(playback, deviceSelection, deviceService);
            var playlist = new Playlist();
            var player = new PlaylistPlayer(playlist, playback);

            // ポータブル運用のため、設定と作業中プレイリストは実行ファイルの隣に置く（SPEC 7 章）。
            var store = new SettingsStore(AppContext.BaseDirectory);
            RegistryKey software = Registry.CurrentUser.CreateSubKey("Software");
            var viewModel = new MainWindowViewModel(
                playlist,
                playback,
                player,
                new PathExpander(decoders.SupportedExtensions),
                new TrackInfoLoader(tagReader, context),
                store,
                store.Load(),
                decoders.SupportedExtensions,
                new FileAssociationRegistrar(
                    software,
                    Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SoundPlayerChiyomaru.exe"),
                    [.. decoders.SupportedExtensions, .. PlaylistFile.Extensions]),
                deviceService,
                deviceSelection,
                new ArtworkLoader(new ArtworkFinder(tagReader)));

            ThemeManager.Apply(this, viewModel.Theme);

            // ファイルを渡して起動された場合は前回のプレイリストを復元しない（SPEC 4.2）。
            viewModel.RestoreSession(restorePlaylist: options.FilePaths.Count == 0);

            desktop.MainWindow = new MainWindow(viewModel, options, Program.Instance);
            desktop.Exit += (_, _) =>
            {
                viewModel.SaveSession();
                viewModel.Dispose();
                deviceFollower.Dispose();
                player.Dispose();
                playback.Dispose();
                wasapiDevices?.Dispose();
                software.Dispose();

                // 設定を保存し終えてから印を手放す。次に起動するプロセスが、保存前の設定を読まないようにするため。
                Program.Instance?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
