using SoundPlayerChiyomaru.Core.Localization;

namespace SoundPlayerChiyomaru;

/// <summary>
/// 画面の文のうち、XAML から参照するもの（{x:Static}）と、XAML とコードの両方で使うもの。
/// コードだけで使う文は、使う場所に <see cref="UiLanguage.Text"/> で日本語と英語を並べて書く。
/// 言語は起動時に決まり、以後は変わらない。値はプロパティで返す（静的な初期化子に入れると、言語が決まる前に作られることがあるため）。
/// メニューの見出しの「_」の次の文字は、Alt と組み合わせて選ぶ文字キー。同じ階層の中で重ならないようにすること。
/// </summary>
public static class Strings
{
    private static string T(string japanese, string english) => UiLanguage.Text(japanese, english);

    private static string WithoutAccessKey(string header) => AccessKeys.Remove(header);

    // メニューバー
    public static string FileMenu => T("ファイル(_F)", "_File");
    public static string Open => T("開く(_O)...", "_Open...");
    public static string AddFiles => T("ファイルを追加(_A)...", "Add _Files...");
    public static string AddFolder => T("フォルダを追加(_D)...", "Add Fol_der...");
    public static string SavePlaylist => T("プレイリストを上書き保存(_S)", "_Save Playlist");
    public static string SavePlaylistAs => T("プレイリストに名前を付けて保存(_N)...", "Save Playlist _As...");
    public static string Settings => T("設定(_T)...", "Se_ttings...");
    public static string Exit => T("終了(_X)", "E_xit");

    public static string EditMenu => T("編集(_E)", "_Edit");
    public static string Undo => T("元に戻す(_U)", "_Undo");
    public static string Redo => T("やり直し(_R)", "_Redo");
    public static string SelectAll => T("すべて選択(_A)", "Select _All");
    public static string Move => T("移動(_M)", "_Move");
    public static string MoveUp => T("上へ(_U)", "_Up");
    public static string MoveDown => T("下へ(_D)", "_Down");
    public static string MoveAfterCurrent => T("再生中の曲の次へ(_N)", "After the _Current Track");
    public static string MoveToTop => T("先頭へ(_T)", "To the _Top");
    public static string MoveToBottom => T("最後尾へ(_B)", "To the _Bottom");
    public static string Sort => T("並べ替え(_S)", "_Sort");
    public static string SortByOrder => T("曲順（#）(_O)", "Original _Order (#)");
    public static string SortByTrackNumber => T("トラック番号(_N)", "Track _Number");
    public static string SortByTitle => T("タイトル(_T)", "_Title");
    public static string SortByArtist => T("アーティスト(_A)", "_Artist");
    public static string SortByAlbum => T("アルバムとトラック番号(_L)", "A_lbum and Track Number");
    public static string SortByAlbumArtist => T("アルバムアーティスト(_B)", "Al_bum Artist");
    public static string SortByDuration => T("時間(_D)", "T_ime");
    public static string SortByFileName => T("ファイル名(_F)", "_File Name");
    public static string SortByPath => T("フルパス(_P)", "Full _Path");
    public static string Shuffle => T("プレイリストをシャッフル(_H)", "S_huffle Playlist");
    public static string Renumber => T("現在の曲順でリナンバリング(_N)", "Re_number in Current Order");
    public static string Remove => T("プレイリストから削除(_D)", "Remo_ve from Playlist");
    public static string Clear => T("プレイリストからすべて削除(_C)", "_Clear Playlist");

    public static string PlayMenu => T("再生(_P)", "_Play");
    public static string PlayPause => T("再生 / 一時停止(_P)", "_Play / Pause");
    public static string Stop => T("停止(_S)", "_Stop");
    public static string Previous => T("前の曲(_B)", "Pre_vious Track");
    public static string Next => T("次の曲(_N)", "_Next Track");
    public static string PlayModeMenu => T("再生モード(_O)", "Play M_ode");
    public static string ModeNormal => T("通常(_N)", "_Normal");
    public static string ModeRepeatOne => T("1曲リピート(_1)", "Repeat _One");
    public static string ModeRepeatAll => T("全曲リピート(_A)", "Repeat _All");
    public static string ModeRandom => T("ランダム(_R)", "_Random");
    public static string ModeOneShot => T("1ショット(_O)", "One _Shot");
    public static string SeekForward => T("5 秒進む(_F)", "_Forward 5 Seconds");
    public static string SeekBack => T("5 秒戻る(_R)", "_Back 5 Seconds");
    public static string VolumeUp => T("音量を上げる(_U)", "Volume _Up");
    public static string VolumeDown => T("音量を下げる(_D)", "Volume _Down");
    public static string Mute => T("ミュート(_M)", "_Mute");

    public static string ViewMenu => T("表示(_V)", "_View");
    public static string MiniMode => T("ミニモード(_M)", "_Mini Mode");
    public static string AlwaysOnTop => T("常に手前に表示(_T)", "Always on _Top");
    public static string ThemeMenu => T("配色(_C)", "_Color Theme");
    public static string ThemeChiyomaruSystem => T("Chiyomaru: System（既定）", "Chiyomaru: System (default)");

    public static string OutputMenu => T("出力(_O)", "_Output");
    public static string DefaultDevice => T("既定のデバイス", "Default device");

    /// <summary>再生モードの表示名。並びは PlayMode の値の順。</summary>
    public static IReadOnlyList<string> ModeNames => [
        WithoutAccessKey(ModeNormal),
        WithoutAccessKey(ModeRepeatOne),
        WithoutAccessKey(ModeRepeatAll),
        WithoutAccessKey(ModeRandom),
        WithoutAccessKey(ModeOneShot),
    ];

    // 右クリックのメニュー（文字キーの表記を付けない）
    public static string Close => T("閉じる", "Close");
    public static string PlayFromHere => T("この曲から再生", "Play from This Track");
    public static string PlayNext => T("次にこの曲から再生", "Play This Track Next");
    public static string MovePlain => WithoutAccessKey(Move);
    public static string MoveUpPlain => WithoutAccessKey(MoveUp);
    public static string MoveDownPlain => WithoutAccessKey(MoveDown);
    public static string MoveAfterCurrentPlain => WithoutAccessKey(MoveAfterCurrent);
    public static string MoveToTopPlain => WithoutAccessKey(MoveToTop);
    public static string MoveToBottomPlain => WithoutAccessKey(MoveToBottom);
    public static string SortPlain => WithoutAccessKey(Sort);
    public static string SortByOrderPlain => WithoutAccessKey(SortByOrder);
    public static string SortByTrackNumberPlain => WithoutAccessKey(SortByTrackNumber);
    public static string SortByTitlePlain => WithoutAccessKey(SortByTitle);
    public static string SortByArtistPlain => WithoutAccessKey(SortByArtist);
    public static string SortByAlbumPlain => WithoutAccessKey(SortByAlbum);
    public static string SortByAlbumArtistPlain => WithoutAccessKey(SortByAlbumArtist);
    public static string SortByDurationPlain => WithoutAccessKey(SortByDuration);
    public static string SortByFileNamePlain => WithoutAccessKey(SortByFileName);
    public static string SortByPathPlain => WithoutAccessKey(SortByPath);
    public static string ShufflePlain => WithoutAccessKey(Shuffle);
    public static string RenumberPlain => WithoutAccessKey(Renumber);
    public static string RemovePlain => WithoutAccessKey(Remove);
    public static string ClearPlain => WithoutAccessKey(Clear);

    // メイン画面のツールチップ
    public static string AlwaysOnTopTip => T("常に手前に表示 (Ctrl+T)", "Always on top (Ctrl+T)");
    public static string MiniModeTip => T("ミニモード (Ctrl+M)", "Mini mode (Ctrl+M)");
    public static string PreviousTip => T("前の曲 (Ctrl+←)", "Previous track (Ctrl+←)");
    public static string NextTip => T("次の曲 (Ctrl+→)", "Next track (Ctrl+→)");
    public static string StopTip => T("停止 (Ctrl+Space)", "Stop (Ctrl+Space)");
    public static string MuteTip => T("ミュート（音量は Ctrl+↑ / Ctrl+↓）", "Mute (volume: Ctrl+↑ / Ctrl+↓)");

    // プレイリストの列。並びは PlaylistColumn の値の順。
    public static string ColumnTitle => T("タイトル", "Title");
    public static string ColumnArtist => T("アーティスト", "Artist");
    public static string ColumnAlbum => T("アルバム", "Album");
    public static string ColumnAlbumArtist => T("アルバムアーティスト", "Album Artist");
    public static string ColumnDuration => T("時間", "Time");
    public static string ColumnFileName => T("ファイル名", "File Name");
    public static string ColumnTrackNumber => T("トラック番号", "Track Number");
    public static string ColumnOrder => T("曲順（#）", "Order (#)");

    /// <summary>列の名前（見出しを隠したときのメニューなどに使う）。並びは PlaylistColumn の値の順。</summary>
    public static string[] ColumnNames =>
        [ColumnTrackNumber, ColumnTitle, ColumnArtist, ColumnAlbum, ColumnAlbumArtist, ColumnDuration, ColumnFileName, ColumnOrder];

    // 設定画面
    public static string SettingsTitle => T("設定 | S:P Chiyomaru", "Settings | S:P Chiyomaru");
    public static string PageOutput => T("出力", "Output");
    public static string PagePlaylist => T("プレイリスト", "Playlist");
    public static string PageStartup => T("起動と関連付け", "Opening Files");
    public static string PageDisplay => T("表示", "Display");
    public static string PageAbout => T("バージョン情報", "About");

    public static string OutputDevice => T("出力デバイス", "Output device");
    public static string OutputMode => T("出力モード", "Output mode");
    public static string SharedMode => T("共有モード（既定）", "Shared mode (default)");
    public static string ExclusiveMode => T("排他モード（ビットパーフェクト再生用）", "Exclusive mode (for bit-perfect playback)");
    public static string ExclusiveModeNote => T(
        "排他モードの間は、他のアプリの音（通知音を含む）がこのデバイスから鳴らなくなります。",
        "While exclusive mode is on, other apps (including notification sounds) cannot play through this device.");
    public static string ReplayGainOff => T("オフ（既定）", "Off (default)");
    public static string ReplayGainTrack => T("トラック（曲ごとに揃える）", "Track (even out each track)");
    public static string ReplayGainAlbum => T("アルバム（アルバムの中の音量差を保つ）", "Album (keep differences within an album)");
    public static string ReplayGainNote => T(
        "有効な間は音量を調整するので、ビットパーフェクトではなくなります。",
        "While on, the volume is adjusted, so playback is no longer bit-perfect.");
    public static string BufferLength => T("バッファの長さ", "Buffer length");
    public static string BufferNote => T(
        "短いほど操作への反応が速く、長いほど音が途切れにくくなります。デバイスが受け付けない長さを選ぶと、排他モードから共有モードに切り替わります。",
        "Shorter responds faster to controls; longer is less likely to drop out. If the device does not accept the length, exclusive mode falls back to shared mode.");
    public static string BitDepth => T("出力のビット深度（排他モード）", "Output bit depth (exclusive mode)");
    public static string BitDepthNote => T(
        "通常は「自動」のままにします。上限が曲のビット数より小さいと、ビットパーフェクトではなくなります。",
        "Usually leave this on Auto. If the limit is lower than the track's bit depth, playback is no longer bit-perfect.");
    public static string CurrentOutput => T("今の出力", "Current output");

    public static string AddingTracks => T("曲の追加", "Adding tracks");
    public static string ExcludeDuplicates => T(
        "すでにプレイリストにある曲は追加しない（既定: チェックなし）",
        "Do not add tracks already in the playlist (default: off)");
    public static string ExcludeDuplicatesNote => T(
        "ファイルやフォルダを追加するときに、同じファイルは除かれます。同じファイルを 2 回再生したいときは、オフにします。「開く」で選んだ場合は除きません。",
        "When adding files or folders, files already in the playlist are skipped. Turn this off to play the same file twice. Files chosen with Open are never skipped.");
    public static string VisibleColumns => T("表示する列", "Visible columns");
    public static string ResetColumns => T("既定の表示に戻す", "Reset to Default");
    public static string ColumnsNote => T(
        "幅と順番は、一覧の見出しをドラッグするか右クリックして変えられます。",
        "To change widths and order, drag or right-click the column headers.");
    public static string SavingPlaylists => T("プレイリストの保存", "Saving playlists");
    public static string RelativePaths => T("曲を相対パスで書き込む（既定: チェックなし）", "Write tracks as relative paths (default: off)");
    public static string RelativePathsNote => T(
        "プレイリストファイルと同じドライブにある曲を、プレイリストファイルの場所からの相対パスで書き込みます。",
        "Tracks on the same drive as the playlist file are written relative to the playlist file's location.");

    public static string ExternalOpen => T("他から起動したときの動作", "When opened from another app");
    public static string ExternalReplace => T("プレイリストを置き換えて再生（既定）", "Replace the playlist and play (default)");
    public static string ExternalAppend => T("末尾に追加", "Add to the end");
    public static string ExternalAppendAndPlay => T("追加して再生", "Add and play");
    public static string ExternalOpenNote => T(
        "このアプリを起動している間に、エクスプローラーなどからファイルを開いたときの動作です。プレイリスト以外にファイルをドロップしたときも、この動作になります。「ファイル」メニューの「開く」の動作は変わりません（常に置き換えて再生）。",
        "What happens when you open a file from File Explorer or another app while this app is running. Dropping files outside the playlist works the same way. Open in the File menu always replaces the playlist and plays.");
    public static string FileAssociations => T("ファイルの関連付け", "File associations");
    public static string Register => T("登録する", "Register");
    public static string Unregister => T("解除する", "Unregister");
    public static string AssociationsNote => T(
        "曲とプレイリストのファイルを開くアプリの候補として、このアプリを Windows に登録します。",
        "Registers this app with Windows as a choice for opening audio and playlist files.");

    public static string Language => T("言語 / Language", "Language / 言語");
    public static string LanguageSystem => T("Windows の設定に従う（既定）", "Follow Windows (default)");
    public static string LanguageRestartNote => T("再起動後に反映されます。", "The change takes effect after a restart.");
    public static string RestartNow => T("今すぐ再起動", "Restart Now");
    public static string Theme => T("配色", "Color theme");
    public static string ThemeNote => T(
        "System は、Light か Dark かを Windows の設定に合わせます。",
        "System follows the Windows setting for light or dark.");
    public static string Window => T("ウィンドウ", "Window");
    public static string AlwaysOnTopSetting => T("常に手前に表示（既定: チェックなし）", "Always on top (default: off)");

    public static string Author => T("作者: Nyxtrope / yukino", "Author: Nyxtrope / yukino");
    public static string LicenseLine => T("ライセンス: MIT License", "License: MIT License");
    public static string ReleaseNotes => T("リリースノート", "Release notes");
    public static string AppLicense => T("このアプリのライセンス", "License of this app");
    public static string ThirdPartyLicenses => T("依存ライブラリのライセンス", "Licenses of libraries used");
}
