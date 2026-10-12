using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Decoding;
using SoundPlayerChiyomaru.Core.Playback;

namespace SoundPlayerChiyomaru.Core.Tests;

/// <summary>生成した実ファイルを内蔵デコーダで読む。Media Foundation のコーデックが入った Windows が必要。</summary>
[Collection(TestAudioFiles.Collection)]
public class DecoderTests(TestAudioFiles files)
{
    private readonly DecoderRegistry registry = DecoderRegistry.CreateDefault();

    public static TheoryData<string, WaveFormatEncoding, int, bool> Files => new()
    {
        { "pcm16.wav", WaveFormatEncoding.Pcm, 16, true },
        { "pcm24-extensible.wav", WaveFormatEncoding.Pcm, 24, true },
        { "float32-extensible.wav", WaveFormatEncoding.IeeeFloat, 32, true },
        { "tone.mp3", WaveFormatEncoding.Pcm, 16, false },
        { "tone.m4a", WaveFormatEncoding.Pcm, 16, false },
        { "tone.flac", WaveFormatEncoding.Pcm, 16, true },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void 形式と長さを正しく読み取る(string name, WaveFormatEncoding encoding, int bits, bool lossless)
    {
        using IAudioDecoder decoder = registry.Open(files.PathOf(name));

        Assert.Equal(encoding, decoder.WaveFormat.Encoding);
        Assert.Equal(bits, decoder.WaveFormat.BitsPerSample);
        Assert.Equal(TestAudioFiles.SampleRate, decoder.WaveFormat.SampleRate);
        Assert.Equal(TestAudioFiles.Channels, decoder.WaveFormat.Channels);
        Assert.InRange(decoder.Duration.TotalSeconds, TestAudioFiles.Seconds - 0.01, TestAudioFiles.Seconds + (lossless ? 0.01 : 0.15));
        Assert.Equal(TimeSpan.Zero, decoder.Position);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void 終端まで読むと長さ分のデータが得られ0を返す(string name, WaveFormatEncoding encoding, int bits, bool lossless)
    {
        _ = (encoding, bits);
        using IAudioDecoder decoder = registry.Open(files.PathOf(name));
        byte[] buffer = new byte[decoder.WaveFormat.BlockAlign * 4096];

        long total = 0;
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            total += read;
        }

        double seconds = (double)total / decoder.WaveFormat.AverageBytesPerSecond;
        Assert.InRange(seconds, TestAudioFiles.Seconds - (lossless ? 0.01 : 0.1), TestAudioFiles.Seconds + (lossless ? 0.01 : 0.15));
        Assert.Equal(0, decoder.Read(buffer));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void シークした位置から読める(string name, WaveFormatEncoding encoding, int bits, bool lossless)
    {
        _ = (encoding, bits, lossless);
        using IAudioDecoder decoder = registry.Open(files.PathOf(name));
        byte[] buffer = new byte[decoder.WaveFormat.BlockAlign * 1024];

        decoder.Position = TimeSpan.FromSeconds(1);
        Assert.InRange(decoder.Position.TotalSeconds, 0.95, 1.05);
        Assert.True(decoder.Read(buffer) > 0);
        Assert.InRange(decoder.Position.TotalSeconds, 0.95, 1.2);

        decoder.Position = TimeSpan.FromSeconds(-5);
        Assert.Equal(TimeSpan.Zero, decoder.Position);

        decoder.Position = TimeSpan.FromSeconds(99);
        Assert.InRange(decoder.Position.TotalSeconds, decoder.Duration.TotalSeconds - 0.05, decoder.Duration.TotalSeconds);

        // 非可逆形式はフレーム単位でしか位置決めできず、終端へシークしても最後のフレームの残りが読めることがある。
        long remaining = 0;
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            remaining += read;
        }
        Assert.InRange((double)remaining / decoder.WaveFormat.AverageBytesPerSecond, 0, 0.1);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void 終端まで読んだ後に巻き戻すと同じ長さを読み直せる(string name, WaveFormatEncoding encoding, int bits, bool lossless)
    {
        _ = (encoding, bits, lossless);
        using IAudioDecoder decoder = registry.Open(files.PathOf(name));
        long limit = decoder.WaveFormat.AverageBytesPerSecond * 10L;

        long first = Drain(decoder, limit);
        decoder.Position = TimeSpan.Zero;
        long second = Drain(decoder, limit);
        decoder.Position = TimeSpan.FromSeconds(1);
        long fromMiddle = Drain(decoder, limit);

        Assert.Equal(first, second);
        Assert.InRange((double)fromMiddle / decoder.WaveFormat.AverageBytesPerSecond, (double)first / decoder.WaveFormat.AverageBytesPerSecond - 1.05, (double)first / decoder.WaveFormat.AverageBytesPerSecond - 0.95);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void 何度シークしても位置が進み終端に達する(string name, WaveFormatEncoding encoding, int bits, bool lossless)
    {
        _ = (encoding, bits, lossless);
        using IAudioDecoder decoder = registry.Open(files.PathOf(name));
        byte[] buffer = new byte[decoder.WaveFormat.BlockAlign * 1024];

        decoder.Position = TimeSpan.FromSeconds(1);
        decoder.Read(buffer);
        decoder.Position = TimeSpan.FromSeconds(0.5);
        decoder.Read(buffer);
        decoder.Position = TimeSpan.FromSeconds(1.5);
        TimeSpan before = decoder.Position;
        Assert.True(decoder.Read(buffer) > 0);
        Assert.True(decoder.Position > before);

        long remaining = Drain(decoder, decoder.WaveFormat.AverageBytesPerSecond * 10L);
        Assert.InRange((double)remaining / decoder.WaveFormat.AverageBytesPerSecond, 0.3, 0.7);
    }

    private static long Drain(IAudioDecoder decoder, long limit)
    {
        byte[] buffer = new byte[decoder.WaveFormat.BlockAlign * 4096];
        long total = 0;
        int read;
        while (total < limit && (read = decoder.Read(buffer)) > 0)
        {
            total += read;
        }
        return total;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void 再生サービス経由でfloatに変換され振幅が保たれる(string name, WaveFormatEncoding encoding, int bits, bool lossless)
    {
        _ = (encoding, bits);
        var outputs = new FakeOutputFactory();
        using var service = new PlaybackService(registry, outputs, context: null);
        service.Open(files.PathOf(name));
        service.Seek(TimeSpan.FromSeconds(0.5));
        service.Play();

        var samples = new float[TestAudioFiles.SampleRate / 2];
        int read = outputs.Last.Source!.ToSampleProvider().Read(samples);
        Assert.True(read > TestAudioFiles.SampleRate / 4);

        // 非可逆形式はフレームの途中から復号を始めるため、シーク直後にごく短い乱れが出る。その区間は振幅の確認から外す。
        int skip = lossless ? 0 : TestAudioFiles.SampleRate * TestAudioFiles.Channels / 10;
        float peak = samples.AsSpan(skip, read - skip).ToArray().Max(Math.Abs);

        Assert.InRange(peak, TestAudioFiles.Amplitude - (lossless ? 0.001 : 0.03), TestAudioFiles.Amplitude + (lossless ? 0.001 : 0.03));
    }

    [Fact]
    public void 存在しないファイルはFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => registry.Open(files.PathOf("missing.mp3")));
    }

    [Fact]
    public void 中身が音声でないファイルはAudioDecodeException()
    {
        var ex = Assert.Throws<AudioDecodeException>(() => registry.Open(files.PathOf("garbage.mp3")));

        Assert.Contains("garbage.mp3", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
