using System.Buffers.Binary;
using System.Text;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Tags;

namespace SoundPlayerChiyomaru.Core.Tests;

/// <summary>
/// 自前のタグ読み取りを、TagLib# で書いたファイルで確かめる。同じファイルを TagLib# でも読み、結果を突き合わせる。
/// TagLib# の書き込みの設定（ID3v2 の版と文字コード）は静的な変数なので、TagLib# で書くテストは同じコレクションに入れて直列に走らせる。
/// </summary>
[Collection(TestAudioFiles.Collection)]
public class TagReaderOracleTests(TestAudioFiles files) : TempDirectoryTest
{
    private static readonly byte[] FrontImage = FakeImage(seed: 1, length: 70_000);
    private static readonly byte[] BackImage = FakeImage(seed: 2, length: 3_000);

    private readonly TagReader reader = new(tryShiftJis: false);

    /// <summary>JPEG の先頭の印だけを持つ、でたらめな中身。0xFF を多く含むので、非同期化の確認にもなる。</summary>
    private static byte[] FakeImage(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        for (int i = 0; i < length; i += 7)
        {
            bytes[i] = 0xFF;
        }
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        return bytes;
    }

    private string Copy(string name)
    {
        string path = At(name);
        File.Copy(files.PathOf(name), path, overwrite: true);
        return path;
    }

    private void AssertMatchesTagLib(string path)
    {
        using TagLib.File expected = TagLib.File.Create(path);
        TagLib.Tag tag = expected.Tag;
        TrackInfo? info = reader.Read(path);

        Assert.NotNull(info);
        Assert.Equal(NullIfEmpty(tag.Title), info.Title);
        Assert.Equal(NullIfEmpty(tag.JoinedPerformers), info.Artist);
        Assert.Equal(NullIfEmpty(tag.Album), info.Album);
        Assert.Equal(NullIfEmpty(tag.JoinedAlbumArtists), info.AlbumArtist);
        Assert.Equal(tag.Track, info.TrackNumber);
        Assert.NotNull(info.Duration);
        Assert.Equal(expected.Properties.Duration.TotalSeconds, info.Duration.Value.TotalSeconds, 0.06);

        ReplayGainTags? gain = reader.ReadReplayGain(path);
        if (double.IsNaN(tag.ReplayGainTrackGain) && double.IsNaN(tag.ReplayGainAlbumGain))
        {
            Assert.Null(gain);
        }
        else
        {
            Assert.NotNull(gain);
            AssertSame(tag.ReplayGainTrackGain, gain.TrackGainDb);
            AssertSame(tag.ReplayGainTrackPeak, gain.TrackPeak);
            AssertSame(tag.ReplayGainAlbumGain, gain.AlbumGainDb);
            AssertSame(tag.ReplayGainAlbumPeak, gain.AlbumPeak);
        }

        TagLib.IPicture? picture = tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures.FirstOrDefault();
        Assert.Equal(picture?.Data.Data, reader.ReadArtwork(path));
    }

    private static void AssertSame(double expected, double? actual)
    {
        if (double.IsNaN(expected))
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Equal(expected, actual!.Value, 6);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static TagLib.Picture Picture(byte[] image, TagLib.PictureType type) =>
        new(new TagLib.ByteVector(image)) { Type = type, MimeType = "image/jpeg", Description = "説明 description" };

    private static void WriteEverything(TagLib.Tag tag, bool typedPictures)
    {
        tag.Title = "題名 Title ♪";
        tag.Performers = ["歌手 Artist"];
        tag.Album = "アルバム Album";
        tag.AlbumArtists = ["アルバムの歌手 Album Artist"];
        tag.Track = 7;
        tag.TrackCount = 12;
        tag.ReplayGainTrackGain = -7.25;
        tag.ReplayGainTrackPeak = 0.812345;
        tag.ReplayGainAlbumGain = 3.5;
        tag.ReplayGainAlbumPeak = 0.987654;
        tag.Pictures = typedPictures
            ? [Picture(BackImage, TagLib.PictureType.BackCover), Picture(FrontImage, TagLib.PictureType.FrontCover)]
            : [Picture(FrontImage, TagLib.PictureType.FrontCover)];
    }

    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    [InlineData("tone.m4a")]
    [InlineData("tone.wma")]
    [InlineData("pcm16.wav")]
    [InlineData("pcm24-extensible.wav")]
    [InlineData("float32-extensible.wav")]
    public void タグのないファイルは_長さだけが入り_TagLibと一致する(string name)
    {
        string path = Copy(name);

        AssertMatchesTagLib(path);
        TrackInfo info = reader.Read(path)!;
        Assert.Null(info.Title);
        Assert.Null(info.Artist);
        Assert.Null(info.Album);
        Assert.Equal(0u, info.TrackNumber);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.95, 2.15);
        Assert.Null(reader.ReadArtwork(path));
    }

    [Theory]
    [InlineData("tone.mp3", true)]
    [InlineData("tone.flac", true)]
    [InlineData("tone.m4a", false)]
    [InlineData("tone.wma", true)]
    [InlineData("pcm16.wav", true)]
    public void すべての項目を書いたファイルを読め_TagLibと一致する(string name, bool typedPictures)
    {
        string path = Copy(name);
        using (TagLib.File file = TagLib.File.Create(path))
        {
            WriteEverything(file.Tag, typedPictures);
            file.Save();
        }

        AssertMatchesTagLib(path);
        TrackInfo info = reader.Read(path)!;
        Assert.Equal("題名 Title ♪", info.Title);
        Assert.Equal("歌手 Artist", info.Artist);
        Assert.Equal("アルバム Album", info.Album);

        // WAV のタグ（ID3v2 のチャンク）にも入る。
        Assert.Equal("アルバムの歌手 Album Artist", info.AlbumArtist);
        Assert.Equal(7u, info.TrackNumber);
        Assert.Equal(FrontImage, reader.ReadArtwork(path));
        Assert.Equal(new ReplayGainTags(-7.25, 0.812345, 3.5, 0.987654), reader.ReadReplayGain(path));
    }

    [Theory]
    [InlineData(3, TagLib.StringType.Latin1)]
    [InlineData(3, TagLib.StringType.UTF16)]
    [InlineData(4, TagLib.StringType.Latin1)]
    [InlineData(4, TagLib.StringType.UTF16)]
    [InlineData(4, TagLib.StringType.UTF16BE)]
    [InlineData(4, TagLib.StringType.UTF8)]
    public void ID3v2の版と文字コードの組み合わせを読める(byte version, TagLib.StringType encoding)
    {
        string path = Copy("tone.mp3");
        byte savedVersion = TagLib.Id3v2.Tag.DefaultVersion;
        bool savedForceVersion = TagLib.Id3v2.Tag.ForceDefaultVersion;
        TagLib.StringType savedEncoding = TagLib.Id3v2.Tag.DefaultEncoding;
        bool savedForceEncoding = TagLib.Id3v2.Tag.ForceDefaultEncoding;
        try
        {
            TagLib.Id3v2.Tag.DefaultVersion = version;
            TagLib.Id3v2.Tag.ForceDefaultVersion = true;
            TagLib.Id3v2.Tag.DefaultEncoding = encoding;
            TagLib.Id3v2.Tag.ForceDefaultEncoding = true;
            using var file = new TagLib.Mpeg.AudioFile(path);
            file.RemoveTags(TagLib.TagTypes.Id3v1 | TagLib.TagTypes.Ape);
            TagLib.Tag tag = file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            WriteEverything(tag, typedPictures: true);
            if (encoding == TagLib.StringType.Latin1)
            {
                // Latin-1 に収まる文字だけにする。
                tag.Title = "Título";
                tag.Performers = ["Björk"];
                tag.Album = "Älbum";
            }
            file.Save();
        }
        finally
        {
            TagLib.Id3v2.Tag.DefaultVersion = savedVersion;
            TagLib.Id3v2.Tag.ForceDefaultVersion = savedForceVersion;
            TagLib.Id3v2.Tag.DefaultEncoding = savedEncoding;
            TagLib.Id3v2.Tag.ForceDefaultEncoding = savedForceEncoding;
        }

        Assert.Equal(version, File.ReadAllBytes(path)[3]);
        AssertMatchesTagLib(path);
        Assert.Equal(encoding == TagLib.StringType.Latin1 ? "Björk" : "歌手 Artist", reader.Read(path)!.Artist);
        Assert.Equal(FrontImage, reader.ReadArtwork(path));
    }

    [Theory]
    [InlineData(TagLib.TagTypes.Ape)]
    [InlineData(TagLib.TagTypes.Id3v1)]
    [InlineData(TagLib.TagTypes.Ape | TagLib.TagTypes.Id3v1)]
    public void MP3の末尾のタグだけでも読める(TagLib.TagTypes types)
    {
        string path = Copy("tone.mp3");
        using (var file = new TagLib.Mpeg.AudioFile(path))
        {
            file.RemoveTags(TagLib.TagTypes.AllTags);
            foreach (TagLib.TagTypes type in new[] { TagLib.TagTypes.Ape, TagLib.TagTypes.Id3v1 })
            {
                if (types.HasFlag(type))
                {
                    TagLib.Tag tag = file.GetTag(type, create: true);
                    tag.Title = type == TagLib.TagTypes.Ape ? "APE の題名" : "ID3v1 title";
                    tag.Performers = ["Artist"];
                    tag.Album = "Album";
                    tag.Track = 9;
                    if (type == TagLib.TagTypes.Ape)
                    {
                        tag.ReplayGainTrackGain = -4.5;
                        tag.ReplayGainTrackPeak = 0.5;
                        tag.Pictures = [Picture(FrontImage, TagLib.PictureType.FrontCover)];
                    }
                }
            }
            file.Save();
        }

        AssertMatchesTagLib(path);
        TrackInfo info = reader.Read(path)!;
        Assert.Equal(types.HasFlag(TagLib.TagTypes.Ape) ? "APE の題名" : "ID3v1 title", info.Title);
        Assert.Equal(9u, info.TrackNumber);
        if (types.HasFlag(TagLib.TagTypes.Ape))
        {
            Assert.Equal(new ReplayGainTags(-4.5, 0.5, null, null), reader.ReadReplayGain(path));
            Assert.Equal(FrontImage, reader.ReadArtwork(path));
        }
    }

    [Fact]
    public void ID3v2があれば_末尾のタグより優先する()
    {
        string path = Copy("tone.mp3");
        using (var file = new TagLib.Mpeg.AudioFile(path))
        {
            file.GetTag(TagLib.TagTypes.Id3v2, create: true).Title = "ID3v2";
            file.GetTag(TagLib.TagTypes.Ape, create: true).Title = "APE";
            file.GetTag(TagLib.TagTypes.Ape, create: true).Album = "APE album";
            file.GetTag(TagLib.TagTypes.Id3v1, create: true).Title = "ID3v1";
            file.GetTag(TagLib.TagTypes.Id3v1, create: true).Performers = ["ID3v1 artist"];
            file.Save();
        }

        TrackInfo info = reader.Read(path)!;

        Assert.Equal("ID3v2", info.Title);
        Assert.Equal("APE album", info.Album);
        Assert.Equal("ID3v1 artist", info.Artist);
    }

    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    public void 複数のアーティストは区切ってつなぐ(string name)
    {
        string path = Copy(name);
        using (TagLib.File file = TagLib.File.Create(path))
        {
            if (file.GetTag(TagLib.TagTypes.Id3v2, create: true) is TagLib.Id3v2.Tag id3)
            {
                // 複数の値を区切って持てるのは 2.4 から。
                id3.Version = 4;
            }
            file.Tag.Performers = ["一人目", "二人目", "三人目"];
            file.Tag.AlbumArtists = ["甲", "乙"];
            file.Save();
        }

        AssertMatchesTagLib(path);
        Assert.Equal("一人目; 二人目; 三人目", reader.Read(path)!.Artist);
        Assert.Equal("甲; 乙", reader.Read(path)!.AlbumArtist);
    }

    [Fact]
    public void ID3v2_3のアーティストの斜線は_区切りとみなさずそのまま読む()
    {
        // TagLib# は 2.3 のアーティストを「/」で分けるので「AC; DC」になる。名前に斜線を含むアーティストを壊さないよう、そのまま読む。
        string path = Copy("tone.mp3");
        using (var file = new TagLib.Mpeg.AudioFile(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            id3.Version = 3;
            id3.Performers = ["AC/DC"];
            file.Save();
        }

        Assert.Equal("AC/DC", reader.Read(path)!.Artist);
    }

    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    [InlineData("tone.m4a")]
    [InlineData("tone.wma")]
    [InlineData("pcm16.wav")]
    public void 壊したファイルを読ませても_例外を出さず_すぐに返る(string name)
    {
        string source = Copy(name);
        using (TagLib.File file = TagLib.File.Create(source))
        {
            WriteEverything(file.Tag, typedPictures: name != "tone.m4a");
            file.Save();
        }
        byte[] original = File.ReadAllBytes(source);
        string path = At("broken" + Path.GetExtension(name));
        var random = new Random(name.GetHashCode(StringComparison.Ordinal));
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int variants = 0;

        void Check(byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            reader.Read(path);
            reader.ReadReplayGain(path);
            reader.ReadArtwork(path);
            variants++;
        }

        // 途中で切れたファイル。先頭付近は 1 バイト刻みで確かめる。
        for (int length = 0; length < original.Length; length += length < 600 ? 1 : 1 + random.Next(original.Length / 150))
        {
            Check(original[..length]);
        }

        // 一部を書き換えたファイル。大きさや種類の欄が集まる、先頭と末尾の近くを重点的に壊す。
        for (int i = 0; i < 600; i++)
        {
            byte[] bytes = (byte[])original.Clone();
            int changes = 1 + random.Next(4);
            for (int j = 0; j < changes; j++)
            {
                int region = random.Next(3);
                int position = region switch
                {
                    0 => random.Next(Math.Min(400, bytes.Length)),
                    1 => bytes.Length - 1 - random.Next(Math.Min(400, bytes.Length)),
                    _ => random.Next(bytes.Length),
                };
                bytes[position] = (byte)random.Next(256);
            }
            Check(bytes);
        }

        // 壊れた大きさの値を信じて巨大な領域を確保していないこと（1 回の読み取りあたり、ファイルの大きさの数倍まで）。
        long perRead = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (variants * 3);
        Assert.InRange(perRead, 0, original.Length * 4L);
    }
}

/// <summary>TagLib# では作れない形のタグを、バイト列を組み立てて確かめる。</summary>
public class TagReaderHandBuiltTests : TempDirectoryTest
{
    private static readonly Encoding ShiftJis = CreateShiftJis();

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    /// <summary>MPEG1 Layer 3、128kbps、44.1kHz、ステレオのフレーム（中身は 0）。1 フレームは 417 バイト、1152 サンプル。</summary>
    private static byte[] Mp3Frames(int count, Action<byte[]>? editFirst = null)
    {
        const int FrameLength = 417;
        byte[] audio = new byte[count * FrameLength];
        for (int i = 0; i < count; i++)
        {
            new byte[] { 0xFF, 0xFB, 0x90, 0x00 }.CopyTo(audio, i * FrameLength);
        }
        editFirst?.Invoke(audio);
        return audio;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] Syncsafe(int value) =>
        [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] LittleEndian(uint value) => BitConverter.GetBytes(value);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Id3Tag(int version, int flags, params byte[][] frames)
    {
        byte[] body = Concat(frames);
        return Concat(Ascii("ID3"), [(byte)version, 0, (byte)flags], Syncsafe(body.Length), body);
    }

    /// <summary>2.3 のフレーム。</summary>
    private static byte[] Frame3(string id, byte[] content, int flags = 0) =>
        Concat(Ascii(id), BigEndian((uint)content.Length), [(byte)(flags >> 8), (byte)flags], content);

    /// <summary>2.4 のフレーム。</summary>
    private static byte[] Frame4(string id, byte[] content, int flags = 0, bool plainSize = false) =>
        Concat(Ascii(id), plainSize ? BigEndian((uint)content.Length) : Syncsafe(content.Length), [(byte)(flags >> 8), (byte)flags], content);

    /// <summary>2.2 のフレーム（ID は 3 文字、大きさは 3 バイト）。</summary>
    private static byte[] Frame2(string id, byte[] content) =>
        Concat(Ascii(id), [(byte)(content.Length >> 16), (byte)(content.Length >> 8), (byte)content.Length], content);

    private static byte[] Text(byte encoding, byte[] text) => Concat([encoding], text);

    private static byte[] Unsynchronize(byte[] bytes)
    {
        var result = new List<byte>();
        for (int i = 0; i < bytes.Length; i++)
        {
            result.Add(bytes[i]);
            if (bytes[i] == 0xFF && (i + 1 == bytes.Length || bytes[i + 1] == 0 || bytes[i + 1] >= 0xE0))
            {
                result.Add(0);
            }
        }
        return [.. result];
    }

    private string Write(string name, params byte[][] parts)
    {
        string path = At(name);
        File.WriteAllBytes(path, Concat(parts));
        return path;
    }

    [Fact]
    public void ID3v2_2のタグを読める()
    {
        byte[] image = [0xFF, 0xD8, 1, 2, 3, 4];
        string path = Write(
            "a.mp3",
            Id3Tag(
                2,
                0,
                Frame2("TT2", Text(0, Ascii("Title"))),
                Frame2("TP1", Text(1, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("歌手")).ToArray())),
                Frame2("TAL", Text(0, Ascii("Album\0"))),
                Frame2("TP2", Text(0, Ascii("Various"))),
                Frame2("TRK", Text(0, Ascii("5/10"))),
                Frame2("TXX", Text(0, Ascii("replaygain_track_gain\0-1.5 dB"))),
                Frame2("PIC", Concat([0], Ascii("JPG"), [3], Ascii("cover\0"), image))),
            Mp3Frames(10));
        var reader = new TagReader(tryShiftJis: false);

        Assert.Equal(new TrackInfo("Title", "歌手", "Album", 5, reader.Read(path)!.Duration, "Various"), reader.Read(path));
        Assert.Equal(new ReplayGainTags(-1.5, null, null, null), reader.ReadReplayGain(path));
        Assert.Equal(image, reader.ReadArtwork(path));
    }

    [Fact]
    public void タグ全体が非同期化されたID3v2_3を読める()
    {
        byte[] image = [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0x00, 0xFF, 0xFF, 0x12];
        byte[] body = Concat(
            Frame3("TIT2", Text(1, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("題名")).ToArray())),
            Frame3("APIC", Concat([0], Ascii("image/jpeg\0"), [3], [0], image)),
            Frame3("TALB", Text(0, Ascii("Album"))));
        byte[] unsynchronized = Unsynchronize(body);
        Assert.NotEqual(body.Length, unsynchronized.Length);
        string path = Write("a.mp3", Ascii("ID3"), [3, 0, 0x80], Syncsafe(unsynchronized.Length), unsynchronized, Mp3Frames(10));
        var reader = new TagReader(tryShiftJis: false);

        TrackInfo info = reader.Read(path)!;

        Assert.Equal("題名", info.Title);
        Assert.Equal("Album", info.Album);
        Assert.Equal(image, reader.ReadArtwork(path));
        Assert.NotNull(info.Duration);
    }

    [Fact]
    public void フレームごとに非同期化されデータ長が付いたID3v2_4を読める()
    {
        byte[] image = [0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0x00, 0x12];
        byte[] picture = Concat([3], Ascii("image/jpeg\0"), [3], [0], image);
        string path = Write(
            "a.mp3",
            Id3Tag(
                4,
                0,
                Frame4("APIC", Concat(Syncsafe(picture.Length), Unsynchronize(picture)), flags: 0x0003),
                Frame4("TIT2", Text(3, Encoding.UTF8.GetBytes("題名")))),
            Mp3Frames(10));
        var reader = new TagReader(tryShiftJis: false);

        Assert.Equal("題名", reader.Read(path)!.Title);
        Assert.Equal(image, reader.ReadArtwork(path));
    }

    [Fact]
    public void フレームの大きさを普通の32ビットで書いたID3v2_4を読める()
    {
        // 300 バイトのフレーム。7 ビットずつ詰めた値として読むと 172 になり、次のフレームに届かない。
        byte[] longText = Text(3, Encoding.UTF8.GetBytes(new string('あ', 99) + "x"));
        Assert.Equal(299, longText.Length);
        string path = Write(
            "a.mp3",
            Id3Tag(
                4,
                0,
                Frame4("TIT2", longText, plainSize: true),
                Frame4("TALB", Text(3, Encoding.UTF8.GetBytes("アルバム")))),
            Mp3Frames(10));

        TrackInfo info = new TagReader(tryShiftJis: false).Read(path)!;

        Assert.Equal(new string('あ', 99) + "x", info.Title);
        Assert.Equal("アルバム", info.Album);
    }

    [Fact]
    public void 拡張ヘッダーと詰め物のあるID3v2_3を読める()
    {
        byte[] extended = Concat(BigEndian(6), [0, 0], BigEndian(0));
        string path = Write(
            "a.mp3",
            Id3Tag(3, 0x40, extended, Frame3("TIT2", Text(0, Ascii("Title"))), new byte[512]),
            Mp3Frames(10));

        Assert.Equal("Title", new TagReader(tryShiftJis: false).Read(path)!.Title);
    }

    [Fact]
    public void 圧縮や暗号化されたフレームは読み飛ばし_後ろのフレームは読める()
    {
        string path = Write(
            "a.mp3",
            Id3Tag(
                3,
                0,
                Frame3("TIT2", Text(0, Ascii("compressed")), flags: 0x0080),
                Frame3("TALB", Text(0, Ascii("Album")))),
            Mp3Frames(10));

        TrackInfo info = new TagReader(tryShiftJis: false).Read(path)!;

        Assert.Null(info.Title);
        Assert.Equal("Album", info.Album);
    }

    [Fact]
    public void Latin1と書かれたシフトJISの文字列を_日本語環境では正しく読む()
    {
        byte[] title = ShiftJis.GetBytes("日本語の題名");
        byte[] id3v1 = new byte[128];
        Ascii("TAG").CopyTo(id3v1, 0);
        ShiftJis.GetBytes("歌手").CopyTo(id3v1, 33);
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, title))), Mp3Frames(10), id3v1);

        TrackInfo japanese = new TagReader(tryShiftJis: true).Read(path)!;
        TrackInfo western = new TagReader(tryShiftJis: false).Read(path)!;

        Assert.Equal("日本語の題名", japanese.Title);
        Assert.Equal("歌手", japanese.Artist);
        Assert.Equal(Encoding.Latin1.GetString(title), western.Title);
    }

    [Fact]
    public void Latin1と書かれたUTF8の文字列はUTF8として読む()
    {
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, Encoding.UTF8.GetBytes("日本語 Björk")))), Mp3Frames(10));

        Assert.Equal("日本語 Björk", new TagReader(tryShiftJis: true).Read(path)!.Title);
    }

    [Fact]
    public void 日本語環境でも_シフトJISとして読めないLatin1の文字列はLatin1として読む()
    {
        // 0xF6 0x72 はシフト JIS では外字の領域になる。
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, Encoding.Latin1.GetBytes("Björk")))), Mp3Frames(10));

        Assert.Equal("Björk", new TagReader(tryShiftJis: true).Read(path)!.Title);
    }

    [Fact]
    public void VBRの情報があれば_フレーム数から長さを求める()
    {
        string path = Write("a.mp3", Mp3Frames(5, first =>
        {
            Ascii("Xing").CopyTo(first, 36);
            BigEndian(1).CopyTo(first, 40);
            BigEndian(1000).CopyTo(first, 44);
        }));

        TimeSpan duration = new TagReader(tryShiftJis: false).Read(path)!.Duration!.Value;

        Assert.Equal(1000 * 1152 / 44100.0, duration.TotalSeconds, 3);
    }

    [Fact]
    public void VBRの情報がなければ_ビットレートから長さを求める()
    {
        // 先頭の詰め物と末尾の ID3v1 は、音声の長さに数えない。
        byte[] id3v1 = new byte[128];
        Ascii("TAG").CopyTo(id3v1, 0);
        string path = Write("a.mp3", new byte[300], Mp3Frames(100), id3v1);

        TimeSpan duration = new TagReader(tryShiftJis: false).Read(path)!.Duration!.Value;

        Assert.Equal(100 * 1152 / 44100.0, duration.TotalSeconds, 0.01);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x00, 1, 2, 3 })]
    [InlineData(new byte[] { 0x49, 0x44, 0x33 })]
    [InlineData(new byte[0])]
    public void 音声フレームもタグもないMP3はnull(byte[] content)
    {
        byte[] noise = new byte[5000];
        new Random(5).NextBytes(noise);
        for (int i = 0; i < noise.Length; i++)
        {
            if (noise[i] == 0xFF)
            {
                noise[i] = 0xFE;
            }
        }

        Assert.Null(new TagReader(tryShiftJis: false).Read(Write("a.mp3", noise, content, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0])));
    }

    [Fact]
    public void WAVのINFOチャンクを読め_データの大きさが書かれていなければ残り全部とみなす()
    {
        byte[] Chunk(string id, byte[] content) =>
            Concat(Ascii(id), LittleEndian((uint)content.Length), content, new byte[content.Length & 1]);

        byte[] format = Concat(
            [1, 0, 2, 0],
            LittleEndian(44100),
            LittleEndian(44100 * 4),
            [4, 0, 16, 0]);
        byte[] info = Concat(
            Ascii("INFO"),
            Chunk("INAM", ShiftJis.GetBytes("題名\0")),
            Chunk("IART", Encoding.UTF8.GetBytes("歌手")),
            Chunk("IPRD", Ascii("Album\0\0")),
            Chunk("ITRK", Ascii("12")));
        byte[] body = Concat(
            Ascii("WAVE"),
            Chunk("fmt ", format),
            Chunk("LIST", info),
            Ascii("data"),
            LittleEndian(0),
            new byte[44100 * 4 * 3]);
        string path = Write("a.wav", Ascii("RIFF"), LittleEndian((uint)body.Length), body);

        TrackInfo info1 = new TagReader(tryShiftJis: true).Read(path)!;

        Assert.Equal(new TrackInfo("題名", "歌手", "Album", 12, TimeSpan.FromSeconds(3)), info1);
    }

    [Fact]
    public void MP4の変則的な形を読める()
    {
        byte[] Atom(string type, params byte[][] content)
        {
            byte[] body = Concat(content);
            return Concat(BigEndian((uint)(8 + body.Length)), Encoding.Latin1.GetBytes(type), body);
        }

        byte[] Data(uint kind, byte[] value) => Atom("data", BigEndian(kind), BigEndian(0), value);

        byte[] movieHeader = Concat([1, 0, 0, 0], new byte[16], BigEndian(1000), new byte[4], BigEndian(2500), new byte[80]);
        byte[] items = Concat(
            Atom("©nam", Data(2, Encoding.BigEndianUnicode.GetBytes("題名"))),
            Atom("©ART", Data(1, Encoding.UTF8.GetBytes("一人目")), Data(1, Encoding.UTF8.GetBytes("二人目"))),
            Atom("aART", Data(1, Encoding.UTF8.GetBytes("アルバムの歌手"))),
            Atom("trkn", Data(0, [0, 0, 0, 4, 0, 9, 0, 0])),
            Atom("----", Atom("mean", BigEndian(0), Ascii("com.apple.iTunes")), Atom("name", BigEndian(0), Ascii("REPLAYGAIN_ALBUM_GAIN")), Data(1, Ascii("+2.00 dB"))),
            Atom("covr", Data(13, [0xFF, 0xD8, 9, 9])));

        // meta の先頭の版とフラグを書かない形。moov は音声データの後ろに置く。
        byte[] meta = Atom("meta", Atom("hdlr", new byte[25]), Atom("ilst", items));
        string path = Write(
            "a.m4a",
            Atom("ftyp", Ascii("M4A "), new byte[4]),
            Atom("mdat", new byte[3000]),
            Atom("moov", Atom("mvhd", movieHeader), Atom("udta", meta)));
        var reader = new TagReader(tryShiftJis: false);

        Assert.Equal(new TrackInfo("題名", "一人目; 二人目", null, 4, TimeSpan.FromSeconds(2.5), "アルバムの歌手"), reader.Read(path));
        Assert.Equal(new ReplayGainTags(null, null, 2, null), reader.ReadReplayGain(path));
        Assert.Equal(new byte[] { 0xFF, 0xD8, 9, 9 }, reader.ReadArtwork(path));
    }

    [Fact]
    public void 先頭にID3v2が付いたFLACは_FLACのタグを優先し_足りない項目をID3v2で埋める()
    {
        byte[] Block(int type, bool last, byte[] content) =>
            Concat([(byte)(type | (last ? 0x80 : 0)), (byte)(content.Length >> 16), (byte)(content.Length >> 8), (byte)content.Length], content);

        byte[] Comment(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return Concat(LittleEndian((uint)bytes.Length), bytes);
        }

        // 48kHz、ステレオ、16bit、96000 サンプル（2 秒）。
        byte[] streamInfo = new byte[34];
        streamInfo[10] = 0x0B;
        streamInfo[11] = 0xB8;
        streamInfo[12] = 0x02;
        streamInfo[13] = 0xF0;
        BigEndian(96000).CopyTo(streamInfo, 14);
        byte[] comments = Concat(Comment("vendor"), LittleEndian(4), Comment("title=FLAC の題名"), Comment("Album Artist=V.A."), Comment("TRACKNUMBER=3/9"), Comment("broken"));
        string path = Write(
            "a.flac",
            Id3Tag(3, 0, Frame3("TIT2", Text(0, Ascii("ID3"))), Frame3("TALB", Text(0, Ascii("ID3 album")))),
            Ascii("fLaC"),
            Block(0, false, streamInfo),
            Block(4, true, comments),
            new byte[100]);

        Assert.Equal(
            new TrackInfo("FLAC の題名", null, "ID3 album", 3, TimeSpan.FromSeconds(2), "V.A."),
            new TagReader(tryShiftJis: false).Read(path));
    }

    [Theory]
    [InlineData("Céline Dion")]
    [InlineData("Édith Piaf")]
    [InlineData("Mädchen")]
    [InlineData("Straße")]
    [InlineData("Pokémon")]
    [InlineData("Françoise Hardy")]
    [InlineData("Blue Öyster Cult")]
    [InlineData("Måneskin")]
    [InlineData("Motörhead")]
    [InlineData("Sigur Rós")]
    [InlineData("Die Ärzte")]
    [InlineData("Beyoncé")]
    [InlineData("créée")]
    [InlineData("© 2001 Señor Ünïcode °")]
    public void 日本語環境でも_Latin1の欧文はそのまま読む(string western)
    {
        byte[] id3v1 = new byte[128];
        Ascii("TAG").CopyTo(id3v1, 0);
        Encoding.Latin1.GetBytes(western).CopyTo(id3v1, 33);
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, Encoding.Latin1.GetBytes(western)))), Mp3Frames(10), id3v1);

        TrackInfo info = new TagReader(tryShiftJis: true).Read(path)!;

        Assert.Equal(western, info.Title);
        Assert.Equal(western, info.Artist);
    }

    [Fact]
    public void 大文字のアクセント付きの文字が3つ以上続く欧文は_半角カナと区別できず化ける()
    {
        // 判定の限界。半角カナだけの題名（「ｱｲｳｴｵ」）を読めることを優先している。
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, Encoding.Latin1.GetBytes("ÀÉÎ")))), Mp3Frames(10));

        Assert.Equal("ﾀﾉﾎ", new TagReader(tryShiftJis: true).Read(path)!.Title);
        Assert.Equal("ÀÉÎ", new TagReader(tryShiftJis: false).Read(path)!.Title);
    }

    [Fact]
    public void 日本語環境でも_Windowsの欧文の引用符をシフトJISの漢字と取り違えない()
    {
        // 0x92（’）と 0x93（“）は、直後の英字と合わせてシフト JIS の漢字としても成立する。
        byte[] bytes = Encoding.GetEncoding(1252).GetBytes("Don’t Stop “Me” Now");
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, bytes))), Mp3Frames(10));

        Assert.Equal(Encoding.Latin1.GetString(bytes), new TagReader(tryShiftJis: true).Read(path)!.Title);
    }

    [Theory]
    [InlineData("日本")]
    [InlineData("愛")]
    [InlineData("東京事変")]
    [InlineData("椎名林檎")]
    [InlineData("Love ～愛～ (Remix)")]
    [InlineData("B'z ベスト")]
    [InlineData("凛として時雨")]
    [InlineData("ｱｲｳｴｵ")]
    [InlineData("１２３")]
    [InlineData("鬱な朝")]
    public void 日本語環境では_シフトJISの日本語を読む(string japanese)
    {
        string path = Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, ShiftJis.GetBytes(japanese)))), Mp3Frames(10));

        Assert.Equal(japanese, new TagReader(tryShiftJis: true).Read(path)!.Title);
    }

    [Theory]
    [InlineData("あいうえおかきくけこさしすせそ", "あいうえおかきくけこさしすせそ")]
    [InlineData("Aあいうえおかきくけこさしすせそ", "Aあいうえおかきくけこさしすせ")]
    [InlineData("ABCあいうえおかきくけこさしすせそ", "ABCあいうえおかきくけこさしす")]
    public void ID3v1の欄で文字の途中で切れたシフトJISは_切れた文字を落として読む(string title, string expected)
    {
        byte[] id3v1 = new byte[128];
        Ascii("TAG").CopyTo(id3v1, 0);
        ShiftJis.GetBytes(title).AsSpan(0, Math.Min(30, ShiftJis.GetByteCount(title))).CopyTo(id3v1.AsSpan(3));
        Encoding.UTF8.GetBytes("あいうえおかきくけこさ").AsSpan(0, 30).CopyTo(id3v1.AsSpan(33));
        Encoding.Latin1.GetBytes("Señor Beyoncé").CopyTo(id3v1, 63);
        string path = Write("a.mp3", Mp3Frames(10), id3v1);

        TrackInfo info = new TagReader(tryShiftJis: true).Read(path)!;

        Assert.Equal(expected, info.Title);
        Assert.Equal("あいうえおかきくけこ", info.Artist);
        Assert.Equal("Señor Beyoncé", info.Album);
    }

    [Fact]
    public void 誤り検出の符号が付いたフレームでも_VBRの情報を見つける()
    {
        string path = Write("a.mp3", Mp3Frames(5, first =>
        {
            for (int i = 0; i < 5; i++)
            {
                first[(i * 417) + 1] = 0xFA;
            }
            Ascii("Info").CopyTo(first, 38);
            BigEndian(1).CopyTo(first, 42);
            BigEndian(2000).CopyTo(first, 46);
        }));

        TimeSpan duration = new TagReader(tryShiftJis: false).Read(path)!.Duration!.Value;

        Assert.Equal(2000 * 1152 / 44100.0, duration.TotalSeconds, 3);
    }

    [Fact]
    public void 画像を文字列で埋め込んだ巨大なコメントがあるFLACでも_他の項目を読める()
    {
        byte[] Block(int type, bool last, byte[] content) =>
            Concat([(byte)(type | (last ? 0x80 : 0)), (byte)(content.Length >> 16), (byte)(content.Length >> 8), (byte)content.Length], content);

        byte[] Comment(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return Concat(LittleEndian((uint)bytes.Length), bytes);
        }

        byte[] comments = Concat(
            Comment("vendor"),
            LittleEndian(4),
            Comment("ARTIST=歌手"),
            Comment("COVERART=" + new string('A', 5 << 20)),
            Comment("TITLE=題名"),
            Comment("REPLAYGAIN_TRACK_GAIN=-3.00 dB"));
        string path = Write("a.flac", Ascii("fLaC"), Block(0, false, new byte[34]), Block(4, true, comments));
        var reader = new TagReader(tryShiftJis: false);

        Assert.Equal(new TrackInfo("題名", "歌手", null, 0, null), reader.Read(path));
        Assert.Equal(new ReplayGainTags(-3, null, null, null), reader.ReadReplayGain(path));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("wav")]
    [InlineData("wav-unsized-data")]
    [InlineData("m4a")]
    [InlineData("wma")]
    [InlineData("mp3")]
    public void 中身のない領域が続く大きなファイルでも_すぐに返る(string kind)
    {
        // 0 で埋めた領域は、大きさ 0 のブロックやチャンクが延々と並んでいるように見える。
        byte[] filler = new byte[16 << 20];
        string path = kind switch
        {
            "flac" => Write("a.flac", Ascii("fLaC"), filler),
            "wav" => Write("a.wav", Ascii("RIFF"), LittleEndian(uint.MaxValue), Ascii("WAVE"), filler),
            "wav-unsized-data" => Write(
                "a.wav",
                Ascii("RIFF"),
                LittleEndian(0),
                Ascii("WAVEfmt "),
                LittleEndian(16),
                [1, 0, 2, 0],
                LittleEndian(44100),
                LittleEndian(44100 * 4),
                [4, 0, 16, 0],
                Ascii("data"),
                LittleEndian(0),
                filler),
            "m4a" => Write(
                "a.m4a",
                [0, 0, 0, 16],
                Ascii("ftypM4A "),
                new byte[4],
                Enumerable.Repeat(Concat(BigEndian(8), Ascii("free")), filler.Length / 8).SelectMany(atom => atom).ToArray()),
            "wma" => Write("a.wma", new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray(), BitConverter.GetBytes((long)filler.Length), new byte[6], filler),
            _ => Write("a.mp3", Id3Tag(3, 0, Frame3("TIT2", Text(0, Ascii("Title")))), filler),
        };
        var reader = new TagReader(tryShiftJis: false);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        TrackInfo? info = reader.Read(path);
        reader.ReadReplayGain(path);
        reader.ReadArtwork(path);
        stopwatch.Stop();

        // 修正前は、16MB のファイル 1 個に数秒かかっていた。
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 1.5);
        if (kind == "wav-unsized-data")
        {
            Assert.Equal(filler.Length / (44100.0 * 4), info!.Duration!.Value.TotalSeconds, 3);
        }
    }

    [Fact]
    public void 生のAACは_ID3v2のタグだけを読む()
    {
        string path = Write("a.aac", Id3Tag(4, 0, Frame4("TIT2", Text(3, Encoding.UTF8.GetBytes("題名")))), [0xFF, 0xF1, 0x50, 0x80, 0, 0x1F, 0xFC], new byte[500]);

        Assert.Equal(new TrackInfo("題名", null, null, 0, null), new TagReader(tryShiftJis: false).Read(path));
        Assert.Null(new TagReader(tryShiftJis: false).Read(Write("b.aac", [0xFF, 0xF1, 0x50, 0x80, 0, 0x1F, 0xFC], new byte[500])));
    }

    [Theory]
    [InlineData("a.flac")]
    [InlineData("a.m4a")]
    [InlineData("a.wav")]
    [InlineData("a.wma")]
    [InlineData("a.mp3")]
    public void 中身が別物のファイルはnull(string name)
    {
        byte[] noise = new byte[3000];
        new Random(11).NextBytes(noise);
        noise[0] = 0x12;

        Assert.Null(new TagReader(tryShiftJis: false).Read(Write(name, noise.Select(b => b == 0xFF ? (byte)0 : b).ToArray())));
        Assert.Null(new TagReader(tryShiftJis: false).ReadArtwork(Write(name, [])));
    }
}
