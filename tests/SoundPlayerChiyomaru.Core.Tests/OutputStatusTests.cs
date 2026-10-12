using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Tags;

namespace SoundPlayerChiyomaru.Core.Tests;

public class OutputStatusTests
{
    private static readonly WaveFormat Device = TestFormats.Pcm24In32(96000);

    private static OutputInfo Exclusive(bool bitPerfectCapable = true, int? resampledFrom = null, string? reason = null) =>
        new(true, Device, bitPerfectCapable, resampledFrom, reason, null);

    private static OutputInfo Shared(string? notice = null) => new(false, null, false, null, null, notice);

    [Fact]
    public void 出力していなければ印は出さない()
    {
        Assert.Equal((BitPerfectBadge.None, null), OutputStatus.Badge(true, null, false, true, false));
    }

    [Fact]
    public void ビットパーフェクトで再生していれば_印と出力の形式を出す()
    {
        (BitPerfectBadge badge, string? description) = OutputStatus.Badge(true, Exclusive(), isBitPerfect: true, true, false);

        Assert.Equal(BitPerfectBadge.BitPerfect, badge);
        Assert.Contains("96000 Hz / 24-in-32", description);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void 利用者が音量やReplayGainで通常経路にしている間は_印を出さない(bool volumeIsMax, bool replayGain)
    {
        Assert.Equal((BitPerfectBadge.None, null), OutputStatus.Badge(true, Exclusive(), false, volumeIsMax, replayGain));
    }

    [Fact]
    public void 共有モードを選んでいる間は_印を出さない()
    {
        Assert.Equal((BitPerfectBadge.None, null), OutputStatus.Badge(false, Shared(), false, true, false));
    }

    [Fact]
    public void 排他モードを選んでいるのに共有モードで鳴らしていれば_警告の印と理由を出す()
    {
        const string Notice = "排他モードを使えません（他のアプリがデバイスを使用中です）。共有モードで再生しています。";

        Assert.Equal((BitPerfectBadge.Unavailable, Notice), OutputStatus.Badge(true, Shared(Notice), false, true, false));

        // 音量を下げていても、希望した出力になっていないことは知らせる。
        Assert.Equal((BitPerfectBadge.Unavailable, Notice), OutputStatus.Badge(true, Shared(Notice), false, false, true));
        Assert.Equal(BitPerfectBadge.Unavailable, OutputStatus.Badge(true, Shared(), false, true, false).Badge);
    }

    [Fact]
    public void 排他モードでリサンプリングしていれば_警告の印と理由を出す()
    {
        (BitPerfectBadge badge, string? description) =
            OutputStatus.Badge(true, Exclusive(bitPerfectCapable: false, resampledFrom: 88200), false, true, false);

        Assert.Equal(BitPerfectBadge.Unavailable, badge);
        Assert.Contains("88200 → 96000 Hz", description);
    }

    [Fact]
    public void 排他モードで形式を変換していれば_警告の印と理由を出す()
    {
        (BitPerfectBadge badge, string? description) =
            OutputStatus.Badge(true, Exclusive(bitPerfectCapable: false, reason: "float を整数に変換"), false, true, false);

        Assert.Equal(BitPerfectBadge.Unavailable, badge);
        Assert.Contains("float を整数に変換", description);
    }

    [Fact]
    public void 出力の説明は_モードと経路_出力の形式_デバイスに分ける()
    {
        Assert.Equal(
            ["WASAPI 排他  ·  ビットパーフェクト", "出力 96000 Hz / 24-in-32", "DAC"],
            OutputStatus.Describe(Exclusive(), "DAC", isBitPerfect: true, replayGain: false));
        Assert.Equal(
            ["WASAPI 排他  ·  通常経路（音量調整）", "出力 96000 Hz / 24-in-32", "DAC"],
            OutputStatus.Describe(Exclusive(), "DAC", isBitPerfect: false, replayGain: false));
        Assert.Equal(
            ["WASAPI 排他  ·  通常経路（ReplayGain）", "出力 96000 Hz / 24-in-32"],
            OutputStatus.Describe(Exclusive(), null, isBitPerfect: false, replayGain: true));
        Assert.Equal(
            ["WASAPI 排他  ·  通常経路（リサンプリング 88200 → 96000 Hz）", "出力 96000 Hz / 24-in-32", "DAC"],
            OutputStatus.Describe(Exclusive(false, 88200), "DAC", isBitPerfect: false, replayGain: false));
        Assert.Equal(
            ["WASAPI 共有（通常経路）", "スピーカー"],
            OutputStatus.Describe(Shared(), "スピーカー", isBitPerfect: false, replayGain: false));
    }
}

public class ArtworkFinderTests : TempDirectoryTest
{
    private sealed class FakeArtworkReader : IArtworkReader
    {
        public Dictionary<string, byte[]> Pictures { get; } = [];

        public byte[]? ReadArtwork(string path) => Pictures.GetValueOrDefault(path);
    }

    private readonly FakeArtworkReader reader = new();

    [Fact]
    public void タグの画像があれば_それを使う()
    {
        string track = Touch("album", "a.mp3");
        File.WriteAllBytes(At("album", "folder.jpg"), [9, 9]);
        reader.Pictures[track] = [1, 2, 3];

        Assert.Equal(new byte[] { 1, 2, 3 }, new ArtworkFinder(reader).Find(track));
    }

    [Fact]
    public void タグの画像がなければ_同じフォルダのfolder_jpgを使う()
    {
        string track = Touch("album", "a.mp3");
        File.WriteAllBytes(At("album", "Folder.JPG"), [9, 8, 7]);

        Assert.Equal(new byte[] { 9, 8, 7 }, new ArtworkFinder(reader).Find(track));
    }

    [Fact]
    public void どちらもなければnull()
    {
        string track = Touch("album", "a.mp3");
        File.WriteAllBytes(At("folder.jpg"), [1]);
        File.WriteAllBytes(At("album", "cover.jpg"), [1]);
        Touch("album", "empty", "folder.jpg");

        var finder = new ArtworkFinder(reader);

        Assert.Null(finder.Find(track));
        Assert.Null(finder.Find(At("album", "empty", "b.mp3")));
        Assert.Null(finder.Find(At("missing", "c.mp3")));
        Assert.Null(finder.Find("a.mp3"));
        Assert.Null(finder.Find(""));
    }
}
