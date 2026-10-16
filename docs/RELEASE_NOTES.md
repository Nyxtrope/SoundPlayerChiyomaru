# リリースノート / Release notes

## 0.1.1

不具合の修正と、安全性の改善。

- 修正: 一部のデバイス（Roland Rubix 24 など）で、モノラルの曲を排他モードで再生すると音が出なかった。ステレオのデバイスには、左右に同じ値を入れたステレオで渡すようにした（値は変わらないので、ビットパーフェクト再生のまま）
- 修正: 自分の上のフォルダを指すジャンクションやシンボリックリンクがあるフォルダを追加すると、曲が 1 つも追加されない、または探索が終わらないことがあった。リンクのフォルダの中は探さないようにした（リンクのフォルダそのものを追加すれば、中を探す）
- 安全性: 実行ファイルと同じフォルダに Windows の DLL と同じ名前のファイルが置かれていても、読み込まないようにした（DLL は System32 から読む）
- 安全性: ネットワーク上のパスを含むプレイリストを開く前に、開いてよいかを確かめるようにした。そうした曲を開くと、Windows がそのサーバーへ接続し、利用者の資格情報を送ることがあるため。このコンピューターのドライブの曲と、プレイリストと同じサーバーの曲だけなら、確かめない

Bug fixes and security improvements.

- Fixed: On some devices (such as the Roland Rubix 24), mono tracks played in exclusive mode made no sound. Stereo devices now receive stereo, with the same value in both channels (the values are unchanged, so playback stays bit-perfect).
- Fixed: Adding a folder that contains a junction or symbolic link pointing to a folder above it could add no tracks at all, or the search never finished. The app no longer looks inside linked folders (adding the linked folder itself still searches inside it).
- Security: Files placed next to the app with the same names as Windows DLLs are no longer loaded (DLLs are loaded from System32).
- Security: Before opening a playlist that contains network paths, the app now asks whether to open it. Opening such tracks can make Windows connect to that server and send your credentials. Playlists that only contain tracks on this computer's drives, or on the same server as the playlist, open without asking.

## 0.1.0

最初のバージョン。

- 再生: MP3 / FLAC / WAV / AAC (M4A) / WMA。ギャップレス再生、シーク、音量
- 出力: WASAPI 共有モード / 排他モード（ビットパーフェクト再生）、出力デバイスの選択、デバイスの抜き差しへの追従
- ReplayGain（トラック / アルバム）
- 再生モード: 通常 / 1曲リピート / 全曲リピート / ランダム / 1ショット
- プレイリスト: ドラッグ & ドロップでの追加と並べ替え、選んだ曲の移動（上へ / 下へ / 再生中の曲の次へ / 先頭へ / 最後尾へ）、元に戻す・やり直し、並べ替え、シャッフル、M3U8 での保存、M3U8 / M3U / PLS の読み込み
- プレイリストの列: 表示する列の選択、幅と順番の変更、曲順の列
- タグとアルバムアートワークの表示
- ミニモード、常に手前に表示、配色の選択
- 画面の言語: 日本語 / 英語（既定は Windows の表示言語に従う。設定で選べる）
- 二重起動の防止、ファイルの関連付け、メディアキー

The first version.

- Playback: MP3 / FLAC / WAV / AAC (M4A) / WMA, with gapless playback, seeking, and volume
- Output: WASAPI shared mode / exclusive mode (bit-perfect playback), output device selection, following devices as they are connected and disconnected
- ReplayGain (track / album)
- Play modes: Normal / Repeat One / Repeat All / Random / One Shot
- Playlist: adding and reordering by drag and drop, moving selected tracks (up / down / after the current track / to the top / to the bottom), undo and redo, sorting, shuffling, saving as M3U8, and opening M3U8 / M3U / PLS
- Playlist columns: choosing which columns to show, changing their width and order, and the original order column
- Tags and album artwork
- Mini mode, always on top, and color themes
- Display language: Japanese / English (follows the Windows display language by default; can be changed in the settings)
- Single instance, file associations, and media keys
