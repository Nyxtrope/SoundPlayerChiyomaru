using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;
using SoundPlayerChiyomaru.Core.Tags;

namespace SoundPlayerChiyomaru.Core.Tests;

sealed class FakeReplayGainReader : IReplayGainReader
{
    public Dictionary<string, ReplayGainTags> Tags { get; } = [];

    public List<string> Reads { get; } = [];

    public ReplayGainTags? ReadReplayGain(string path)
    {
        Reads.Add(path);
        return Tags.GetValueOrDefault(path);
    }
}

public class ReplayGainTagsTests
{
    [Fact]
    public void オフなら何も掛けない()
    {
        Assert.Null(new ReplayGainTags(-6, 0.5, -7, 0.5).ResolveDb(ReplayGainMode.Off));
    }

    [Fact]
    public void モードに応じてトラックかアルバムの値を使う()
    {
        var tags = new ReplayGainTags(-6, 0.9, -8, 0.95);

        Assert.Equal(-6, tags.ResolveDb(ReplayGainMode.Track));
        Assert.Equal(-8, tags.ResolveDb(ReplayGainMode.Album));
    }

    [Fact]
    public void 選んだ側の値がなければもう一方を使い_ピークも同じ側のものを使う()
    {
        // ピーク 0.5 は +6.02 dB まで上げられる。ピーク 1.0 は上げられない。
        var trackOnly = new ReplayGainTags(3, 0.5, null, 1.0);
        var albumOnly = new ReplayGainTags(null, 1.0, 3, 0.5);

        Assert.Equal(3, trackOnly.ResolveDb(ReplayGainMode.Album));
        Assert.Equal(3, albumOnly.ResolveDb(ReplayGainMode.Track));
    }

    [Fact]
    public void どちらの値もなければ何も掛けない()
    {
        Assert.Null(new ReplayGainTags(null, 0.5, null, 0.5).ResolveDb(ReplayGainMode.Track));
        Assert.Null(new ReplayGainTags(double.NaN, null, double.PositiveInfinity, null).ResolveDb(ReplayGainMode.Album));
    }

    [Theory]
    [InlineData(-500)]
    [InlineData(-60.1)]
    [InlineData(60.1)]
    public void 壊れた値のゲインは使わない(double gain)
    {
        Assert.Null(new ReplayGainTags(gain, 0.000001, null, null).ResolveDb(ReplayGainMode.Track));
        Assert.Equal(-60, new ReplayGainTags(-60, null, null, null).ResolveDb(ReplayGainMode.Track));
    }

    [Fact]
    public void ピークが最大振幅を超えないところまでゲインを抑える()
    {
        double? db = new ReplayGainTags(9, 0.5, null, null).ResolveDb(ReplayGainMode.Track);

        Assert.Equal(6.0206, db!.Value, 3);
        Assert.Equal(1.0, 0.5 * ReplayGainTags.ToScale(db.Value), 4);
    }

    [Fact]
    public void ピークが最大振幅を超えている曲は_下げる量を増やす()
    {
        double? db = new ReplayGainTags(-1, 1.25, null, null).ResolveDb(ReplayGainMode.Track);

        Assert.Equal(1.0, 1.25 * ReplayGainTags.ToScale(db!.Value), 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ピークが分からない曲は_上げる方向には掛けず_下げる方向には掛ける(double? peak)
    {
        Assert.Equal(0, new ReplayGainTags(4, peak, null, null).ResolveDb(ReplayGainMode.Track));
        Assert.Equal(-4, new ReplayGainTags(-4, peak, null, null).ResolveDb(ReplayGainMode.Track));
    }

    [Fact]
    public void dBを倍率にする()
    {
        Assert.Equal(1f, ReplayGainTags.ToScale(0));
        Assert.Equal(0.5012f, ReplayGainTags.ToScale(-6), 4);
        Assert.Equal(0.1f, ReplayGainTags.ToScale(-20), 5);
    }
}

public class ReplayGainPlaybackTests
{
    private const float ValueA = 8192 / 32768f;
    private const float ValueB = 16384 / 32768f;
    private static readonly float Minus6 = ReplayGainTags.ToScale(-6);
    private static readonly float Minus12 = ReplayGainTags.ToScale(-12);

    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly FakeReplayGainReader reader = new();
    private readonly PlaybackService service;

    public ReplayGainPlaybackTests()
    {
        opener.Create = path => path switch
        {
            "a" => new FakeDecoder(TimeSpan.FromSeconds(2)) { SampleValue = 8192 },
            "b" => new FakeDecoder(TimeSpan.FromSeconds(3)) { SampleValue = 16384 },
            "untagged" => new FakeDecoder(TimeSpan.FromSeconds(2)) { SampleValue = 8192 },
            _ => throw new FileNotFoundException(path),
        };
        reader.Tags["a"] = new ReplayGainTags(-6, 0.5, -3, 0.9);
        reader.Tags["b"] = new ReplayGainTags(-12, 0.9, -3, 0.9);
        service = new PlaybackService(opener, outputs, context: null, replayGain: reader);
    }

    private float[] Read(double seconds) => outputs.Last.Read(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void オフの間はタグを読まず_何も掛けない()
    {
        service.Open("a");
        service.SetNext("b");
        service.Play();

        Assert.All(Read(1), s => Assert.Equal(ValueA, s, 6));
        Assert.Empty(reader.Reads);
        Assert.Null(service.ReplayGainDb);
    }

    [Fact]
    public void トラックのゲインを掛ける()
    {
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Open("a");
        service.Play();

        Assert.All(Read(1), s => Assert.Equal(ValueA * Minus6, s, 6));
        Assert.Equal(-6, service.ReplayGainDb);
    }

    [Fact]
    public void アルバムのゲインを掛ける()
    {
        service.ReplayGainMode = ReplayGainMode.Album;
        service.Open("a");
        service.Play();

        Assert.All(Read(1), s => Assert.Equal(ValueA * ReplayGainTags.ToScale(-3), s, 6));
        Assert.Equal(-3, service.ReplayGainDb);
    }

    [Fact]
    public void 続けて再生する曲は_境界ちょうどでその曲のゲインに切り替わる()
    {
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Open("a");
        service.SetNext("b");
        service.Play();

        float[] samples = Read(3);

        int boundary = 2 * 44100 * 2;
        Assert.Equal(3 * 44100 * 2, samples.Length);
        Assert.All(samples.Take(boundary), s => Assert.Equal(ValueA * Minus6, s, 6));
        Assert.All(samples.Skip(boundary), s => Assert.Equal(ValueB * Minus12, s, 6));
        Assert.Equal("b", service.CurrentPath);
        Assert.Equal(-12, service.ReplayGainDb);
    }

    [Fact]
    public void タグのない曲には何も掛けない()
    {
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Open("untagged");
        service.Play();

        Assert.All(Read(1), s => Assert.Equal(ValueA, s, 6));
        Assert.Null(service.ReplayGainDb);
    }

    [Fact]
    public void 音量はゲインに重ねて掛かる()
    {
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Volume = 0.5f;
        service.Open("a");
        service.Play();

        Assert.All(Read(1), s => Assert.Equal(ValueA * Minus6 * 0.5f, s, 6));
    }

    [Fact]
    public void 再生中に切り替えると_出力を作り直さずに_今の曲と予約済みの次の曲に反映される()
    {
        service.Open("a");
        service.SetNext("b");
        service.Play();
        Assert.All(Read(0.5), s => Assert.Equal(ValueA, s, 6));

        service.ReplayGainMode = ReplayGainMode.Track;
        float[] samples = Read(2.5);

        int boundary = (int)(1.5 * 44100) * 2;
        Assert.All(samples.Take(boundary), s => Assert.Equal(ValueA * Minus6, s, 6));
        Assert.All(samples.Skip(boundary), s => Assert.Equal(ValueB * Minus12, s, 6));
        Assert.Single(outputs.Created);

        service.ReplayGainMode = ReplayGainMode.Off;
        Assert.All(Read(0.5), s => Assert.Equal(ValueB, s, 6));
        Assert.Null(service.ReplayGainDb);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void モードを変えてもタグを読み直さない()
    {
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Open("a");
        service.ReplayGainMode = ReplayGainMode.Album;
        service.ReplayGainMode = ReplayGainMode.Off;
        service.ReplayGainMode = ReplayGainMode.Track;

        Assert.Equal(["a"], reader.Reads);
        Assert.Equal(-6, service.ReplayGainDb);
    }

    [Fact]
    public void 曲を開いていない間に切り替えても_次に開いた曲に反映される()
    {
        service.Open("a");
        service.Close();
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Open("b");

        Assert.Equal(-12, service.ReplayGainDb);
    }

    [Fact]
    public void タグを読む手段がなければ_有効にしても何も掛けず_ビットパーフェクトのまま()
    {
        var plain = new PlaybackService(opener, outputs, context: null);
        outputs.ExclusiveFormat = new WaveFormat(44100, 16, 2);
        plain.ReplayGainMode = ReplayGainMode.Track;
        plain.Open("a");
        plain.Play();

        Assert.True(plain.IsBitPerfect);
        Assert.Null(plain.ReplayGainDb);
    }

    [Fact]
    public void 排他モードでは_有効な間は通常経路になり_ビットパーフェクトではなくなる()
    {
        outputs.ExclusiveFormat = new WaveFormat(44100, 16, 2);
        service.Open("a");
        service.Play();
        FakeOutput output = outputs.Last;
        Assert.True(service.IsBitPerfect);
        Assert.All(ReadShorts(output, 1000), s => Assert.Equal(8192, s));

        service.ReplayGainMode = ReplayGainMode.Track;

        Assert.False(service.IsBitPerfect);
        Assert.All(ReadShorts(output, 1000), s => Assert.InRange(s, 4104, 4107));

        service.ReplayGainMode = ReplayGainMode.Off;

        Assert.True(service.IsBitPerfect);
        Assert.All(ReadShorts(output, 1000), s => Assert.Equal(8192, s));
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 排他モードで有効な間は_タグのない曲もビットパーフェクトとは表示しない()
    {
        outputs.ExclusiveFormat = new WaveFormat(44100, 16, 2);
        service.ReplayGainMode = ReplayGainMode.Track;
        service.Open("untagged");
        service.Play();

        Assert.False(service.IsBitPerfect);
        Assert.All(ReadShorts(outputs.Last, 1000), s => Assert.Equal(8192, s));
    }

    private static short[] ReadShorts(FakeOutput output, int frames)
    {
        byte[] bytes = output.ReadBytes(frames);
        short[] samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }
}

[Collection(TestAudioFiles.Collection)]
public class ReplayGainTagReadingTests(TestAudioFiles files) : TempDirectoryTest
{
    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    [InlineData("tone.m4a")]
    [InlineData("pcm16.wav")]
    public void ファイルのタグからReplayGainを読める(string name)
    {
        string path = At(name);
        File.Copy(files.PathOf(name), path);
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.ReplayGainTrackGain = -7.25;
            file.Tag.ReplayGainTrackPeak = 0.812345;
            file.Tag.ReplayGainAlbumGain = -5.5;
            file.Tag.ReplayGainAlbumPeak = 0.987654;
            file.Save();
        }

        ReplayGainTags? tags = new TagReader().ReadReplayGain(path);

        Assert.NotNull(tags);
        Assert.Equal(-7.25, tags.TrackGainDb!.Value, 2);
        Assert.Equal(0.812345, tags.TrackPeak!.Value, 5);
        Assert.Equal(-5.5, tags.AlbumGainDb!.Value, 2);
        Assert.Equal(0.987654, tags.AlbumPeak!.Value, 5);
    }

    [Fact]
    public void トラックの値だけが書かれていれば_アルバムの値はnull()
    {
        string path = At("tone.flac");
        File.Copy(files.PathOf("tone.flac"), path);
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.ReplayGainTrackGain = 2.5;
            file.Save();
        }

        ReplayGainTags? tags = new TagReader().ReadReplayGain(path);

        Assert.Equal(new ReplayGainTags(2.5, null, null, null), tags);
    }

    [Fact]
    public void MP3の小文字で書かれたタグも読める()
    {
        string path = At("tone.mp3");
        File.Copy(files.PathOf("tone.mp3"), path);
        using (var file = new TagLib.Mpeg.AudioFile(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            AddUserText(id3, "replaygain_track_gain", "-3.10 dB");
            AddUserText(id3, "replaygain_track_peak", "0.700000");
            file.Save();
        }

        ReplayGainTags? tags = new TagReader().ReadReplayGain(path);

        Assert.Equal(new ReplayGainTags(-3.1, 0.7, null, null), tags);
    }

    [Theory]
    [InlineData("tone.flac")]
    [InlineData("garbage.mp3")]
    [InlineData("missing.mp3")]
    public void タグがない_壊れている_存在しないファイルはnull(string name)
    {
        Assert.Null(new TagReader().ReadReplayGain(files.PathOf(name)));
    }

    [Fact]
    public void 対応していない拡張子はnull()
    {
        Assert.Null(new TagReader().ReadReplayGain(Touch("a.xyz")));
    }

    private static void AddUserText(TagLib.Id3v2.Tag tag, string description, string value)
    {
        var frame = new TagLib.Id3v2.UserTextInformationFrame(description) { Text = [value] };
        tag.AddFrame(frame);
    }
}
