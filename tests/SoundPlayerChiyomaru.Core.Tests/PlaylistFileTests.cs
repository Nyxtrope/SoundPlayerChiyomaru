using System.Text;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Tags;
using SoundPlayerChiyomaru.Core.Settings;
using SoundPlayerChiyomaru.Core.Playback;

namespace SoundPlayerChiyomaru.Core.Tests;

public abstract class TempDirectoryTest : IDisposable
{
    protected string Root { get; } = Directory.CreateTempSubdirectory("chiyomaru-test-").FullName;

    public void Dispose() => Directory.Delete(Root, recursive: true);

    protected string At(params string[] parts) => Path.Combine([Root, .. parts]);

    protected string Touch(params string[] parts)
    {
        string path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    protected static PlaylistItem[] Items(params string[] paths) =>
        paths.Select(path => new PlaylistItem(path)).ToArray();
}

public class PlaylistFileTests : TempDirectoryTest
{
    [Theory]
    [InlineData("a.m3u8", true)]
    [InlineData("a.M3U", true)]
    [InlineData("a.pls", true)]
    [InlineData("a.mp3", false)]
    [InlineData("m3u8", false)]
    public void 拡張子でプレイリストファイルかどうかを判定する(string path, bool expected)
    {
        Assert.Equal(expected, PlaylistFile.IsPlaylistFile(path));
    }

    [Fact]
    public void 絶対パスで書いて読み戻せる_日本語のパスも保たれる()
    {
        string[] paths = [@"C:\音楽\曲 [live] #1.mp3", @"D:\other\b.flac", @"\\server\share\c.wav"];
        string file = At("list.m3u8");

        PlaylistFile.Write(file, Items(paths), relativePaths: false);

        Assert.Equal(paths, PlaylistFile.Read(file));
        byte[] bytes = File.ReadAllBytes(file);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.StartsWith("#EXTM3U\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void 相対パスで書くと_同じドライブの曲だけ相対になり_読むと絶対パスに戻る()
    {
        string inside = At("music", "sub", "a.mp3");
        string above = At("b.mp3");
        string otherDrive = (Root[0] is 'Q' or 'q' ? @"R:\" : @"Q:\") + "c.mp3";
        string file = At("music", "list.m3u8");
        Directory.CreateDirectory(At("music"));

        PlaylistFile.Write(file, Items(inside, above, otherDrive), relativePaths: true);

        string[] lines = File.ReadAllLines(file);
        Assert.Equal(["#EXTM3U", @"sub\a.mp3", @"..\b.mp3", otherDrive], lines);
        Assert.Equal([inside, above, otherDrive], PlaylistFile.Read(file));
    }

    [Fact]
    public void 長さが分かっている曲には_EXTINFを付ける()
    {
        PlaylistItem[] items = Items(@"C:\a.mp3", @"C:\b.mp3", @"C:\c.mp3");
        items[0].ApplyInfo(new TrackInfo("曲名", "歌手", null, 0, TimeSpan.FromSeconds(123.6)), exists: true);
        items[1].ApplyInfo(new TrackInfo(null, null, null, 0, TimeSpan.FromSeconds(5)), exists: true);
        string file = At("list.m3u8");

        PlaylistFile.Write(file, items, relativePaths: false);

        Assert.Equal(
            ["#EXTM3U", "#EXTINF:124,歌手 - 曲名", @"C:\a.mp3", "#EXTINF:5,b", @"C:\b.mp3", @"C:\c.mp3"],
            File.ReadAllLines(file));
        Assert.Equal([@"C:\a.mp3", @"C:\b.mp3", @"C:\c.mp3"], PlaylistFile.Read(file));
    }

    [Fact]
    public void 書き込み後に一時ファイルを残さず_既存のファイルを置き換える()
    {
        string file = At("list.m3u8");
        File.WriteAllText(file, "old");

        PlaylistFile.Write(file, Items(@"C:\a.mp3"), relativePaths: false);

        Assert.Equal(["list.m3u8"], Directory.GetFiles(Root).Select(Path.GetFileName));
        Assert.Equal([@"C:\a.mp3"], PlaylistFile.Read(file));
    }

    [Fact]
    public void 相対パスで書いても_シャープや空白で始まるファイル名を読み戻せる()
    {
        string[] paths = [At("#1 hit.mp3"), At(" lead space.mp3"), At("sub", "#2.mp3"), At("normal.mp3")];
        string file = At("list.m3u8");

        PlaylistFile.Write(file, Items(paths), relativePaths: true);

        Assert.Equal(
            ["#EXTM3U", @".\#1 hit.mp3", @".\ lead space.mp3", @"sub\#2.mp3", "normal.mp3"],
            File.ReadAllLines(file));
        Assert.Equal(paths, PlaylistFile.Read(file));
    }

    [Fact]
    public void 置き換えに失敗したら例外を投げ_元のファイルは残り_一時ファイルは残さない()
    {
        string file = At("list.m3u8");
        File.WriteAllText(file, "old");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => PlaylistFile.Write(file, Items(@"C:\a.mp3"), relativePaths: false));

            Assert.Equal("old", File.ReadAllText(file));
            Assert.Equal(["list.m3u8"], Directory.GetFiles(Root).Select(Path.GetFileName));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void M3Uはコメントと空行とURLを読み飛ばし_相対パスとfileのURIを解決する()
    {
        string file = At("list.m3u");
        File.WriteAllText(file, string.Join("\r\n",
            "#EXTM3U",
            "",
            "#EXTINF:10,title",
            @"sub\a.mp3",
            "  ",
            "http://example.com/stream.mp3",
            @"C:\abs\b.mp3",
            "file:///C:/uri/c%20d.mp3",
            @"..\up.mp3"));

        Assert.Equal(
            [At("sub", "a.mp3"), @"C:\abs\b.mp3", @"C:\uri\c d.mp3", Path.GetFullPath(At("..", "up.mp3"))],
            PlaylistFile.Read(file));
    }

    [Fact]
    public void UTF8として読めないM3Uは_システムの文字コードで読む()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // コードページ 0 はシステムの既定。日本語環境（932）以外では、西欧（1252）で表せる名前を使う。
        // どちらでもない環境では、この読み分けの確認にはならない。
        Encoding ansi = Encoding.GetEncoding(0);
        string name = ansi.CodePage == 932 ? "日本語の曲.mp3" : "caf\u00e9.mp3";
        string file = At("list.m3u");
        File.WriteAllBytes(file, ansi.GetBytes(@"C:\music\" + name + "\r\n"));

        Assert.Equal([@"C:\music\" + name], PlaylistFile.Read(file));
    }

    [Fact]
    public void BOM付きのUTF8とUTF16も読める()
    {
        string utf8 = At("utf8.m3u");
        string utf16 = At("utf16.m3u");
        File.WriteAllText(utf8, @"C:\音楽\a.mp3", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(utf16, @"C:\音楽\b.mp3", Encoding.Unicode);

        Assert.Equal([@"C:\音楽\a.mp3"], PlaylistFile.Read(utf8));
        Assert.Equal([@"C:\音楽\b.mp3"], PlaylistFile.Read(utf16));
    }

    [Fact]
    public void PLSはFileの行だけを読む()
    {
        string file = At("list.pls");
        File.WriteAllText(file, string.Join("\n",
            "[playlist]",
            @"File1=C:\a.mp3",
            "Title1=File2=not a path",
            "Length1=10",
            @"file2 = sub\b.mp3",
            "FileX=ignored.mp3",
            "File3=",
            "NumberOfEntries=2",
            "Version=2"));

        Assert.Equal([@"C:\a.mp3", At("sub", "b.mp3")], PlaylistFile.Read(file));
    }

    [Fact]
    public void 空のファイルは0件()
    {
        string file = Touch("empty.m3u8");

        Assert.Empty(PlaylistFile.Read(file));
    }
}

public class PathExpanderTests : TempDirectoryTest
{
    private readonly PathExpander expander = new([".mp3", ".flac"]);

    [Fact]
    public void フォルダは下の階層まで_曲の拡張子だけを_数字を数値として並べて拾う()
    {
        string f10 = Touch("album", "10.mp3");
        string f2 = Touch("album", "2.MP3");
        string deep = Touch("album", "disc2", "1.flac");
        Touch("album", "cover.jpg");
        Touch("album", "notes.txt");

        Assert.Equal([f2, f10, deep], expander.Expand([At("album")]));
    }

    [Fact]
    public void 複数のファイルからは曲の拡張子だけを拾い_渡された順を保つ()
    {
        string b = Touch("b.mp3");
        string a = Touch("a.flac");
        string jpg = Touch("cover.jpg");

        Assert.Equal([b, a], expander.Expand([b, jpg, a]));
    }

    [Fact]
    public void ファイルを1個だけ渡された場合は_拡張子を問わず通す()
    {
        string unknown = Touch("song.xyz");

        Assert.Equal([unknown], expander.Expand([unknown]));
    }

    [Fact]
    public void プレイリストファイルは中身に展開し_ファイルやフォルダと混ぜられる()
    {
        string a = Touch("a.mp3");
        string inFolder = Touch("folder", "c.mp3");
        string list = At("list.m3u8");
        File.WriteAllText(list, "x\\b.mp3\n");

        Assert.Equal([a, At("x", "b.mp3"), inFolder], expander.Expand([a, list, At("folder")]));
    }

    [Fact]
    public void 存在しないフォルダやプレイリストは0件として扱う()
    {
        Assert.Empty(expander.Expand([At("missing.m3u8"), At("missing-folder", "x.txt")]));
    }

    [Fact]
    public void 相対パスは絶対パスにする()
    {
        string result = Assert.Single(expander.Expand(["relative.mp3"]));

        Assert.Equal(Path.GetFullPath("relative.mp3"), result);
    }
}

public class SettingsStoreTests : TempDirectoryTest
{
    [Fact]
    public void 設定ファイルがなければ既定値()
    {
        AppSettings settings = new SettingsStore(Root).Load();

        Assert.Equal(1.0, settings.Volume);
        Assert.Equal(PlayMode.Normal, settings.PlayMode);
        Assert.False(settings.SaveRelativePaths);
        Assert.Null(settings.PlaylistFilePath);
        Assert.Equal(-1, settings.CurrentIndex);
        Assert.Null(settings.Window);
        Assert.Equal(200, settings.BufferMilliseconds);
        Assert.Null(settings.MaxOutputBitDepth);
        Assert.False(settings.ExcludeDuplicates);
        Assert.Equal(AppLanguage.System, settings.Language);
    }

    [Fact]
    public void 保存した設定を読み戻せる_再生モードは名前で保存する()
    {
        var store = new SettingsStore(Root);

        store.Save(new AppSettings
        {
            Volume = 0.25,
            PlayMode = PlayMode.OneShot,
            SaveRelativePaths = true,
            PlaylistFilePath = @"C:\音楽\list.m3u8",
            CurrentIndex = 7,
            Window = new WindowBounds(-120, 40, 812.5, 600, Maximized: true),
            BufferMilliseconds = 100,
            MaxOutputBitDepth = 16,
            ExcludeDuplicates = true,
            Language = AppLanguage.English,
        });
        AppSettings loaded = new SettingsStore(Root).Load();

        Assert.Equal(new WindowBounds(-120, 40, 812.5, 600, Maximized: true), loaded.Window);
        Assert.Equal(100, loaded.BufferMilliseconds);
        Assert.Equal(16, loaded.MaxOutputBitDepth);
        Assert.True(loaded.ExcludeDuplicates);
        Assert.Equal(0.25, loaded.Volume);
        Assert.Equal(PlayMode.OneShot, loaded.PlayMode);
        Assert.True(loaded.SaveRelativePaths);
        Assert.Equal(@"C:\音楽\list.m3u8", loaded.PlaylistFilePath);
        Assert.Equal(7, loaded.CurrentIndex);
        Assert.Equal(AppLanguage.English, loaded.Language);
        Assert.Contains("\"OneShot\"", File.ReadAllText(At("settings.json")));
        Assert.Contains("\"English\"", File.ReadAllText(At("settings.json")));
        Assert.Equal(["settings.json"], Directory.GetFiles(Root).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"PlayMode\":\"NoSuchMode\"}")]
    public void 壊れた設定ファイルは既定値として読む(string contents)
    {
        File.WriteAllText(At("settings.json"), contents);

        AppSettings settings = new SettingsStore(Root).Load();

        Assert.Equal(1.0, settings.Volume);
        Assert.Equal(PlayMode.Normal, settings.PlayMode);
    }

    [Fact]
    public void 作業中プレイリストを保存して読み戻せる()
    {
        var store = new SettingsStore(Root);
        Assert.Empty(store.LoadWorkingPlaylist());

        store.SaveWorkingPlaylist(Items(@"C:\a.mp3", @"D:\音楽\b.flac"));

        Assert.Equal([@"C:\a.mp3", @"D:\音楽\b.flac"], new SettingsStore(Root).LoadWorkingPlaylist().Select(entry => entry.Path));
    }
}

[Collection(TestAudioFiles.Collection)]
public class TrackInfoTests(TestAudioFiles files) : TempDirectoryTest
{
    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    [InlineData("tone.m4a")]
    [InlineData("pcm16.wav")]
    public void タグと長さを読める(string name)
    {
        string path = At(name);
        File.Copy(files.PathOf(name), path);
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.Title = "題名";
            file.Tag.Performers = ["歌手"];
            file.Tag.Album = "アルバム";
            file.Tag.Track = 7;
            file.Save();
        }

        TrackInfo? info = new TagReader().Read(path);

        Assert.NotNull(info);
        Assert.Equal("題名", info.Title);
        Assert.Equal("歌手", info.Artist);
        Assert.Equal("アルバム", info.Album);
        Assert.Equal(7u, info.TrackNumber);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.95, 2.15);
    }

    [Fact]
    public void タグのないファイルは長さだけが入る()
    {
        TrackInfo? info = new TagReader().Read(files.PathOf("tone.flac"));

        Assert.NotNull(info);
        Assert.Null(info.Title);
        Assert.Null(info.Artist);
        Assert.Equal(0u, info.TrackNumber);
        Assert.NotNull(info.Duration);
    }

    [Theory]
    [InlineData("garbage.mp3")]
    [InlineData("missing.mp3")]
    public void 壊れたファイルや存在しないファイルはnull(string name)
    {
        Assert.Null(new TagReader().Read(files.PathOf(name)));
    }

    [Fact]
    public void 対応していない拡張子はnull()
    {
        Assert.Null(new TagReader().Read(Touch("a.xyz")));
    }

    [Fact]
    public void 読み込みは項目に情報とリンク切れを反映し_同じ項目を二度は読まない()
    {
        var reader = new CountingReader();
        var loader = new TrackInfoLoader(reader, context: null);
        var existing = new PlaylistItem(Touch("a.mp3"));
        var missing = new PlaylistItem(At("missing.mp3"));
        var changedProperties = new List<string?>();
        existing.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        loader.Enqueue([existing, missing]);
        loader.Enqueue([existing, missing]);

        Assert.Equal(1, reader.Calls);
        Assert.Equal("T", existing.Title);
        Assert.Equal("T", existing.DisplayTitle);
        Assert.Equal("1:05", existing.DurationText);
        Assert.False(existing.IsMissing);
        Assert.True(missing.IsMissing);
        Assert.Equal("missing", missing.DisplayTitle);
        Assert.Equal("", missing.DurationText);
        Assert.Contains(nameof(PlaylistItem.DisplayTitle), changedProperties);
        Assert.Contains(nameof(PlaylistItem.DurationText), changedProperties);
    }

    [Fact]
    public async Task コンテキストがあれば裏で読み_結果はコンテキストに投げる()
    {
        var context = new QueueContext();
        var loader = new TrackInfoLoader(new CountingReader(), context);
        PlaylistItem[] items = Enumerable.Range(0, 20).Select(i => new PlaylistItem(Touch($"{i}.mp3"))).ToArray();

        loader.Enqueue(items);
        for (int i = 0; i < 1000 && context.Pending < items.Length; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(items.Length, context.Pending);
        Assert.All(items, item => Assert.Null(item.Title));
        context.RunAll();
        Assert.All(items, item => Assert.Equal("T", item.Title));
    }

    [Fact]
    public void 合計の再生時間は長さが分かっている曲だけを数え_反映のたびに通知する()
    {
        var loader = new TrackInfoLoader(new CountingReader(), context: null);
        int applied = 0;
        loader.InfoApplied += (_, _) => applied++;
        var playlist = new Playlist();
        playlist.Add([Touch("a.mp3"), Touch("b.mp3"), At("missing.mp3")]);
        Assert.Equal(TimeSpan.Zero, playlist.GetTotalDuration());

        loader.Enqueue(playlist.Items);

        Assert.Equal(3, applied);
        Assert.Equal(TimeSpan.FromSeconds(130), playlist.GetTotalDuration());
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65.9, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3723, "1:02:03")]
    [InlineData(90000, "25:00:00")]
    [InlineData(-5, "0:00")]
    public void 長さの表示は一時間以上で時間の桁を出し_二十四時間を超えても繰り上げない(double seconds, string expected)
    {
        Assert.Equal(expected, PlaylistItem.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    private sealed class CountingReader : ITrackInfoReader
    {
        public int Calls;

        public TrackInfo? Read(string path)
        {
            Interlocked.Increment(ref Calls);
            return new TrackInfo("T", "A", "B", 1, TimeSpan.FromSeconds(65));
        }
    }
}
