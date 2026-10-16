using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Decoding;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Playback;

namespace SoundPlayerChiyomaru.Core.Tests;

static class TestFormats
{
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>32bit の入れ物に有効 24bit。</summary>
    public static WaveFormat Pcm24In32(int rate, int channels = 2) =>
        new WaveFormatExtensible(rate, 32, channels, Pcm, 24, channels == 1 ? 0x4 : 0x3);
}

/// <summary>全ビットを使う乱数のサンプルを返すデコーダ。下位ビットまで保たれているかを確かめるために使う。</summary>
sealed class RandomPcmDecoder : IAudioDecoder
{
    private readonly byte[] data;
    private int position;

    public RandomPcmDecoder(int bits, int sampleRate, int frames, int seed, int channels = 2)
    {
        WaveFormat = new WaveFormat(sampleRate, bits, channels);
        int bytesPerSample = bits / 8;
        Samples = new int[frames * channels];
        data = new byte[Samples.Length * bytesPerSample];
        var random = new Random(seed);
        for (int i = 0; i < Samples.Length; i++)
        {
            // 最小値と最大値も確実に含める。
            int value = i switch
            {
                0 => -(1 << (bits - 1)),
                1 => (int)((1L << (bits - 1)) - 1),
                _ => (int)random.NextInt64(-(1L << (bits - 1)), 1L << (bits - 1)),
            };
            Samples[i] = value;
            for (int b = 0; b < bytesPerSample; b++)
            {
                data[i * bytesPerSample + b] = (byte)(value >> (8 * b));
            }
        }
    }

    /// <summary>デコード結果のサンプル値（符号つき）。</summary>
    public int[] Samples { get; }

    public WaveFormat WaveFormat { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)data.Length / WaveFormat.AverageBytesPerSecond);

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds((double)position / WaveFormat.AverageBytesPerSecond);
        set
        {
            long frame = (long)Math.Round(Math.Max(0, value.TotalSeconds) * WaveFormat.SampleRate);
            position = (int)Math.Min(frame * WaveFormat.BlockAlign, data.Length);
        }
    }

    public int Read(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, data.Length - position);
        data.AsSpan(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public void Dispose()
    {
    }
}

public class PcmConversionTests
{
    [Fact]
    public void 二十四ビットを32ビットの入れ物に詰めると_上位24ビットに値が入り下位8ビットは0になる()
    {
        byte[] source = [0x56, 0x34, 0x12, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x80];
        byte[] destination = new byte[12];

        int written = PcmConversion.Widen(source, 3, destination, 4);

        Assert.Equal(12, written);
        Assert.Equal([0x00, 0x56, 0x34, 0x12, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x80], destination);
        Assert.Equal(0x123456 << 8, BinaryPrimitives.ReadInt32LittleEndian(destination));
        Assert.Equal(-1 << 8, BinaryPrimitives.ReadInt32LittleEndian(destination.AsSpan(4)));
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(destination.AsSpan(8)));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    public void 詰め替えた値を元のビット数に戻すと_元の値と一致する(int sourceBytes, int destinationBytes)
    {
        var decoder = new RandomPcmDecoder(sourceBytes * 8, 48000, 2000, seed: 3);
        byte[] source = new byte[decoder.Samples.Length * sourceBytes];
        decoder.Read(source);
        byte[] destination = new byte[decoder.Samples.Length * destinationBytes];

        PcmConversion.Widen(source, sourceBytes, destination, destinationBytes);

        int shift = (destinationBytes - sourceBytes) * 8;
        for (int i = 0; i < decoder.Samples.Length; i++)
        {
            int widened = ReadSample(destination.AsSpan(i * destinationBytes, destinationBytes));
            Assert.Equal(decoder.Samples[i], widened >> shift);
            Assert.Equal(0, widened & ((1 << shift) - 1));
        }
    }

    [Fact]
    public void 同じ大きさの入れ物へはそのまま写す()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7];
        byte[] destination = new byte[8];

        Assert.Equal(6, PcmConversion.Widen(source, 3, destination, 3));
        Assert.Equal([1, 2, 3, 4, 5, 6, 0, 0], destination);
    }

    [Fact]
    public void 小さい入れ物へは詰め替えられない()
    {
        Assert.Throws<ArgumentException>(() => PcmConversion.Widen(new byte[4], 4, new byte[3], 3));
    }

    [Theory]
    [InlineData(2, 16, 0.5f, 16384)]
    [InlineData(2, 16, -1f, -32768)]
    [InlineData(2, 16, 1f, 32767)]
    [InlineData(2, 16, 2f, 32767)]
    [InlineData(2, 16, -2f, -32768)]
    [InlineData(3, 24, 0.5f, 4194304)]
    [InlineData(3, 24, -1f, -8388608)]
    [InlineData(4, 24, 0.5f, 4194304 << 8)]
    [InlineData(4, 24, 1f, 8388607 << 8)]
    [InlineData(4, 32, 0.5f, 1073741824)]
    [InlineData(4, 32, 1f, int.MaxValue)]
    [InlineData(4, 32, -1f, int.MinValue)]
    public void floatを整数にするときは_有効ビット数で量子化して上位に詰め_範囲外は丸める(
        int containerBytes, int validBits, float value, int expected)
    {
        byte[] destination = new byte[containerBytes];

        Assert.Equal(containerBytes, PcmConversion.FloatToPcm([value], destination, containerBytes, validBits));
        Assert.Equal(expected, ReadSample(destination));
    }

    [Theory]
    [InlineData(16, 16, 16, true)]
    [InlineData(16, 32, 24, true)]
    [InlineData(24, 32, 24, true)]
    [InlineData(24, 24, 24, true)]
    [InlineData(24, 16, 16, false)]
    [InlineData(32, 32, 24, false)]
    [InlineData(32, 32, 32, true)]
    [InlineData(8, 16, 16, false)]
    public void 値を変えずに詰め替えられるのは_整数でビット数がデバイスの有効ビット数以下のとき(
        int sourceBits, int container, int valid, bool expected)
    {
        var source = new WaveFormat(48000, sourceBits, 2);
        var device = new WaveFormatExtensible(48000, container, 2, TestFormats.Pcm, valid, 3);

        Assert.Equal(expected, PcmConversion.CanWidenLosslessly(source, device));
    }

    [Fact]
    public void floatの曲や_サンプルレートやチャンネル数が違う場合は_詰め替えられない()
    {
        WaveFormat device = TestFormats.Pcm24In32(48000);

        Assert.False(PcmConversion.CanWidenLosslessly(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), device));
        Assert.False(PcmConversion.CanWidenLosslessly(new WaveFormat(44100, 16, 2), device));
        Assert.False(PcmConversion.CanWidenLosslessly(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 16, 1)));
        Assert.False(PcmConversion.CanWidenLosslessly(new WaveFormat(48000, 16, 1), new WaveFormat(48000, 16, 6)));
        Assert.False(PcmConversion.CanWidenLosslessly(new WaveFormat(48000, 16, 2), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
    }

    [Fact]
    public void モノラルの曲は_ステレオのデバイスへ左右に複製して詰め替えられる()
    {
        Assert.True(PcmConversion.CanWidenLosslessly(new WaveFormat(48000, 16, 1), TestFormats.Pcm24In32(48000)));
        Assert.Equal(2, PcmConversion.CopiesPerSample(new WaveFormat(48000, 16, 1), TestFormats.Pcm24In32(48000)));
        Assert.Equal(1, PcmConversion.CopiesPerSample(new WaveFormat(48000, 16, 2), TestFormats.Pcm24In32(48000)));

        byte[] destination = new byte[8];
        int written = PcmConversion.Widen([0x34, 0x12], 2, destination, 4, copies: 2);

        Assert.Equal(8, written);
        Assert.Equal(new byte[] { 0, 0, 0x34, 0x12, 0, 0, 0x34, 0x12 }, destination);
    }

    internal static int ReadSample(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
        3 => (bytes[0] | (bytes[1] << 8) | ((sbyte)bytes[2] << 16)),
        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes),
    };
}

public class ExclusiveFormatResolverTests
{
    /// <summary>Roland Rubix 24 の実測どおりに答えるデバイス: 16bit と 24-in-32 だけ、44.1 / 48 / 96 / 192kHz、モノラルとステレオ。</summary>
    private sealed class RubixLikeDevice : IExclusiveFormatProbe
    {
        public bool AcceptMono { get; init; } = true;

        public bool IsSupported(WaveFormat format)
        {
            if (format.SampleRate is not (44100 or 48000 or 96000 or 192000))
            {
                return false;
            }
            if (format.Channels != 2 && !(format.Channels == 1 && AcceptMono))
            {
                return false;
            }
            if (!PcmConversion.IsIntegerPcm(format))
            {
                return false;
            }
            return (format.BitsPerSample, PcmConversion.ValidBitsOf(format)) is (16, 16) or (32, 24)
                && (format.BitsPerSample == 16 || format is WaveFormatExtensible);
        }
    }

    private sealed class Device(Func<WaveFormat, bool> accepts) : IExclusiveFormatProbe
    {
        public bool IsSupported(WaveFormat format) => accepts(format);
    }

    private static readonly WaveFormat Mix = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static string Describe(ExclusiveFormatChoice? choice) => choice is null
        ? "none"
        : $"{ExclusiveFormatResolver.Describe(choice.DeviceFormat)} / {choice.DeviceFormat.Channels} ch"
            + (choice.SampleRateChanged ? " resample" : "")
            + (choice.ChannelsChanged ? " widen" : "");

    [Theory]
    [InlineData(16, 44100, 2, "44100 Hz / 16 bit / 2 ch")]
    [InlineData(24, 44100, 2, "44100 Hz / 24-in-32 / 2 ch")]
    [InlineData(24, 96000, 2, "96000 Hz / 24-in-32 / 2 ch")]
    [InlineData(24, 192000, 2, "192000 Hz / 24-in-32 / 2 ch")]
    [InlineData(32, 48000, 2, "48000 Hz / 24-in-32 / 2 ch")]
    [InlineData(16, 48000, 1, "48000 Hz / 16 bit / 2 ch widen")]
    [InlineData(16, 88200, 2, "96000 Hz / 16 bit / 2 ch resample")]
    [InlineData(24, 88200, 2, "96000 Hz / 24-in-32 / 2 ch resample")]
    [InlineData(24, 176400, 2, "192000 Hz / 24-in-32 / 2 ch resample")]
    [InlineData(16, 22050, 2, "44100 Hz / 16 bit / 2 ch resample")]
    [InlineData(16, 11025, 2, "44100 Hz / 16 bit / 2 ch resample")]
    [InlineData(24, 352800, 2, "192000 Hz / 24-in-32 / 2 ch resample")]
    [InlineData(16, 32000, 2, "44100 Hz / 16 bit / 2 ch resample")]
    [InlineData(16, 48000, 6, "none")]
    public void Rubix24相当のデバイスでの解決(int bits, int rate, int channels, string expected)
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            new WaveFormat(rate, bits, channels), new RubixLikeDevice(), Mix);

        Assert.Equal(expected, Describe(choice));
    }

    [Theory]
    [InlineData(16, 16, "44100 Hz / 16 bit / 2 ch", true)]
    [InlineData(24, 16, "44100 Hz / 16 bit / 2 ch", false)]
    [InlineData(32, 16, "44100 Hz / 16 bit / 2 ch", false)]
    [InlineData(16, 24, "44100 Hz / 16 bit / 2 ch", true)]
    [InlineData(24, 24, "44100 Hz / 24-in-32 / 2 ch", true)]
    [InlineData(32, 24, "44100 Hz / 24-in-32 / 2 ch", false)]
    public void ビット深度の上限を指定すると_それを超える形式は選ばない(int bits, int limit, string expected, bool lossless)
    {
        var source = new WaveFormat(44100, bits, 2);

        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(source, new RubixLikeDevice(), Mix, limit);

        Assert.Equal(expected, Describe(choice));
        Assert.Equal(lossless, PcmConversion.CanWidenLosslessly(source, choice!.DeviceFormat));
    }

    [Fact]
    public void 上限を指定すると_floatを受け付けるデバイスでもfloatの曲を整数で渡す()
    {
        WaveFormat source = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        Assert.Equal("44100 Hz / 16 bit / 2 ch", Describe(ExclusiveFormatResolver.Resolve(source, new Device(_ => true), Mix, 16)));
        Assert.Equal("44100 Hz / 24-in-32 / 2 ch", Describe(ExclusiveFormatResolver.Resolve(source, new Device(_ => true), Mix, 24)));
        Assert.Equal("44100 Hz / 32 bit float / 2 ch", Describe(ExclusiveFormatResolver.Resolve(source, new Device(_ => true), Mix, null)));
    }

    [Fact]
    public void 上限以下の形式をデバイスが受け付けなければ_排他モードでは再生できない()
    {
        // 32bit の入れ物だけを受け付けるデバイスに、16bit までの上限を付けた。
        var device = new Device(format => format.BitsPerSample == 32);

        Assert.Null(ExclusiveFormatResolver.Resolve(new WaveFormat(44100, 16, 2), device, Mix, 16));
        Assert.NotNull(ExclusiveFormatResolver.Resolve(new WaveFormat(44100, 16, 2), device, Mix, 24));
    }

    [Fact]
    public void 二十四ビットの曲は_16ビットに格下げせず24in32を選ぶ()
    {
        ExclusiveFormatChoice choice = ExclusiveFormatResolver.Resolve(new WaveFormat(48000, 24, 2), new RubixLikeDevice(), Mix)!;

        var format = Assert.IsType<WaveFormatExtensible>(choice.DeviceFormat);
        Assert.Equal(32, format.BitsPerSample);
        Assert.Equal(24, format.ValidBitsPerSample);
        Assert.Equal(TestFormats.Pcm, format.SubFormat);
        Assert.True(PcmConversion.CanWidenLosslessly(new WaveFormat(48000, 24, 2), choice.DeviceFormat));
    }

    [Fact]
    public void floatの曲は_整数の形式のうち最もビット数の多いものを選ぶ()
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new RubixLikeDevice(), Mix);

        Assert.Equal("44100 Hz / 24-in-32 / 2 ch", Describe(choice));
    }

    [Fact]
    public void floatを受け付けるデバイスなら_floatの曲はfloatのまま渡す()
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new Device(_ => true), Mix);

        Assert.Equal("44100 Hz / 32 bit float / 2 ch", Describe(choice));
    }

    [Fact]
    public void 通常の形式を拒否されたら_配置つきの形式で確かめ直す()
    {
        var device = new Device(format => format is WaveFormatExtensible && format.BitsPerSample == 16);

        ExclusiveFormatChoice choice = ExclusiveFormatResolver.Resolve(new WaveFormat(44100, 16, 2), device, Mix)!;

        Assert.IsType<WaveFormatExtensible>(choice.DeviceFormat);
        Assert.False(choice.SampleRateChanged);
    }

    [Fact]
    public void 同じビット数を受け付けるなら_より大きい入れ物よりそちらを選ぶ()
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            new WaveFormat(48000, 24, 2), new Device(_ => true), Mix);

        Assert.Equal("48000 Hz / 24 bit / 2 ch", Describe(choice));
        Assert.IsNotType<WaveFormatExtensible>(choice!.DeviceFormat);
    }

    [Fact]
    public void 同じサンプルレートでビット数が減る形式を_別のサンプルレートより優先する()
    {
        var device = new Device(format => format.BitsPerSample == 16 && format.SampleRate is 44100 or 48000);

        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(new WaveFormat(44100, 24, 2), device, Mix);

        Assert.Equal("44100 Hz / 16 bit / 2 ch", Describe(choice));
    }

    [Fact]
    public void モノラルを受け付けないデバイスでは_ステレオに広げる()
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            new WaveFormat(44100, 16, 1), new RubixLikeDevice { AcceptMono = false }, Mix);

        Assert.Equal("44100 Hz / 16 bit / 2 ch widen", Describe(choice));
    }

    [Fact]
    public void モノラルを受け付けると答えるステレオのデバイスでも_ステレオに広げる()
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            new WaveFormat(44100, 16, 1), new RubixLikeDevice { AcceptMono = true }, Mix);

        Assert.Equal("44100 Hz / 16 bit / 2 ch widen", Describe(choice));
    }

    [Fact]
    public void モノラルのデバイスには_モノラルの曲をモノラルのまま渡す()
    {
        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            new WaveFormat(44100, 16, 1), new Device(_ => true), WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));

        Assert.Equal("44100 Hz / 16 bit / 1 ch", Describe(choice));
    }

    [Fact]
    public void モノラルのままリサンプリングするより_同じサンプルレートでステレオに広げるほうを選ぶ()
    {
        var device = new Device(format => format.BitsPerSample == 16
            && ((format.Channels == 1 && format.SampleRate == 48000) || (format.Channels == 2 && format.SampleRate == 44100)));

        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(new WaveFormat(44100, 16, 1), device, Mix);

        Assert.Equal("44100 Hz / 16 bit / 2 ch widen", Describe(choice));
    }

    [Fact]
    public void 標準のサンプルレートをどれも受け付けなければ_ミックス形式のサンプルレートを使う()
    {
        var device = new Device(format => format.SampleRate == 50000 && format.BitsPerSample == 16);

        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(
            new WaveFormat(44100, 16, 2), device, WaveFormat.CreateIeeeFloatWaveFormat(50000, 2));

        Assert.Equal("50000 Hz / 16 bit / 2 ch resample", Describe(choice));
        Assert.Null(ExclusiveFormatResolver.Resolve(new WaveFormat(44100, 16, 2), device, mixFormat: null));
    }

    [Fact]
    public void 配置つきの形式で渡された曲も_中身の形式で解決する()
    {
        var source = new WaveFormatExtensible(96000, 24, 2, TestFormats.Pcm, 24, 3);

        ExclusiveFormatChoice? choice = ExclusiveFormatResolver.Resolve(source, new RubixLikeDevice(), Mix);

        Assert.Equal("96000 Hz / 24-in-32 / 2 ch", Describe(choice));
    }
}

public class BitPerfectPlaybackTests
{
    private readonly FakeOpener opener = new();
    private readonly FakeOutputFactory outputs = new();
    private readonly PlaybackService service;
    private readonly Dictionary<string, RandomPcmDecoder> decoders = [];
    private int advanced;
    private int ended;

    public BitPerfectPlaybackTests()
    {
        opener.Create = path => decoders[path];
        service = new PlaybackService(opener, outputs, context: null);
        service.TrackAdvanced += (_, _) => advanced++;
        service.TrackEnded += (_, _) => ended++;
    }

    private RandomPcmDecoder Add(string path, int bits, int rate = 48000, int frames = 4800, int channels = 2)
    {
        var decoder = new RandomPcmDecoder(bits, rate, frames, seed: path.GetHashCode(), channels);
        decoders[path] = decoder;
        return decoder;
    }

    /// <summary>出力に渡ったバイト列を、デバイスの入れ物ごとの整数として読む。</summary>
    private int[] ReadOutput(int frames)
    {
        FakeOutput output = outputs.Last;
        int sampleBytes = output.Source!.WaveFormat.BitsPerSample / 8;
        byte[] bytes = output.ReadBytes(frames);
        int[] samples = new int[bytes.Length / sampleBytes];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = PcmConversionTests.ReadSample(bytes.AsSpan(i * sampleBytes, sampleBytes));
        }
        return samples;
    }

    [Theory]
    [InlineData(16, 16, 16)]
    [InlineData(16, 32, 24)]
    [InlineData(24, 32, 24)]
    [InlineData(24, 24, 24)]
    [InlineData(32, 32, 32)]
    public void 排他モードで音量が最大なら_出力に渡る値は元のサンプルと完全に一致する(int sourceBits, int container, int valid)
    {
        RandomPcmDecoder decoder = Add("a", sourceBits);
        outputs.ExclusiveFormat = new WaveFormatExtensible(48000, container, 2, TestFormats.Pcm, valid, 3);
        service.Open("a");
        service.Play();

        int[] output = ReadOutput(decoder.Samples.Length / 2);

        Assert.True(service.IsBitPerfect);
        Assert.Equal(decoder.Samples.Length, output.Length);
        int shift = container - sourceBits;
        Assert.Equal(decoder.Samples, output.Select(value => value >> shift));
        Assert.All(output, value => Assert.Equal(0, value & ((1 << shift) - 1)));
    }

    [Fact]
    public void 中途半端な大きさのバッファで少しずつ読んでも_値は一致する()
    {
        RandomPcmDecoder decoder = Add("a", 24);
        outputs.ExclusiveFormat = TestFormats.Pcm24In32(48000);
        service.Open("a");
        service.Play();
        FakeOutput output = outputs.Last;

        var collected = new List<int>();
        byte[] buffer = new byte[1021];
        while (output.EndedCount == 0)
        {
            int read = output.Source!.Read(buffer);
            Assert.Equal(1016, read);
            for (int i = 0; i < read; i += 4)
            {
                collected.Add(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i)) >> 8);
            }
        }

        // 最後のバッファは、残りが無音で埋められて満杯で返る。
        Assert.Equal(decoder.Samples, collected[..decoder.Samples.Length]);
        Assert.All(collected[decoder.Samples.Length..], value => Assert.Equal(0, value));
        Assert.InRange(collected.Count - decoder.Samples.Length, 0, 1016 / 4);
        Assert.Equal(1, ended);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public void 再生元が終端に達しても0や短いバッファは返さず_無音で満杯にして返し続ける(float volume)
    {
        RandomPcmDecoder decoder = Add("a", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        outputs.ExclusiveDrain = TimeSpan.FromSeconds(10);
        service.Volume = volume;
        service.Open("a");
        service.Play();
        IWaveProvider source = outputs.Last.Source!;
        byte[] buffer = new byte[1500 * 4];

        Assert.Equal(buffer.Length, source.Read(buffer));
        Assert.All(buffer[4000..], b => Assert.Equal(0, b));
        if (volume >= 1f)
        {
            Assert.Equal(decoder.Samples, MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, 4000)).ToArray().Select(v => (int)v));
        }

        buffer.AsSpan().Fill(0xFF);
        Assert.Equal(buffer.Length, source.Read(buffer));
        Assert.All(buffer, b => Assert.Equal(0, b));
        Assert.Equal(0, ended);
        Assert.Equal(PlayerState.Playing, service.State);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public void 一時停止の間は再生元を読まずに無音を満杯で返し_再開すると続きからそのまま読む(float volume)
    {
        RandomPcmDecoder decoder = Add("a", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        outputs.ExclusiveDrain = TimeSpan.FromMilliseconds(100);
        service.Volume = volume;
        service.Open("a");
        service.Play();
        FakeOutput output = outputs.Last;
        byte[] first = output.ReadBytes(300);

        service.Pause();
        byte[] silence = new byte[20000 * 4];
        silence.AsSpan().Fill(0xFF);
        Assert.Equal(silence.Length, output.Source!.Read(silence));
        Assert.All(silence, b => Assert.Equal(0, b));

        // 無音は終端の後の無音には数えない（100ms = 4800 フレームを超えて流しても、曲は終わらない）。
        Assert.Equal(0, ended);
        Assert.Equal(PlayerState.Paused, service.State);

        service.Play();
        byte[] rest = output.ReadBytes(700);

        Assert.Single(outputs.Created);
        if (volume >= 1f)
        {
            Assert.Equal(
                decoder.Samples,
                MemoryMarshal.Cast<byte, short>((byte[])[.. first, .. rest]).ToArray().Select(v => (int)v));
        }
        else
        {
            Assert.Contains(rest, b => b != 0);
        }
        Assert.Equal(0, ended);
    }

    [Fact]
    public void 終端の後は_決めた長さの無音を流し終えてから_曲の終わりを1回だけ知らせる()
    {
        Add("a", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        outputs.ExclusiveDrain = TimeSpan.FromMilliseconds(100);
        service.Open("a");
        service.Play();
        FakeOutput output = outputs.Last;

        // 1000 フレームの音声 + 無音 1000 フレーム。無音はまだ 4800 フレーム（100ms）に届かない。
        output.ReadBytes(2000);
        output.ReadBytes(3000);
        Assert.Equal(0, ended);
        Assert.Equal(PlayerState.Playing, service.State);

        output.ReadBytes(1000);

        Assert.Equal(1, ended);
        Assert.Equal(1, output.EndedCount);
        Assert.Equal(PlayerState.Stopped, service.State);
        Assert.True(output.Disposed);
    }

    [Fact]
    public void 無音を流している間にシークで戻されたら_そのまま読み継ぎ_曲の終わりは知らせない()
    {
        RandomPcmDecoder decoder = Add("a", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        outputs.ExclusiveDrain = TimeSpan.FromMilliseconds(100);
        service.Open("a");
        service.Play();
        FakeOutput output = outputs.Last;
        output.ReadBytes(3000);

        service.Seek(TimeSpan.Zero);
        int[] resumed = ReadOutput(500);
        output.ReadBytes(500 + 4000);

        Assert.Equal(decoder.Samples[..1000], resumed);
        Assert.Equal(0, ended);
        Assert.Single(outputs.Created);

        // 戻した後の終端からは、無音の長さを数え直す。
        output.ReadBytes(1000);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void 無音を流している間に次の曲が予約されたら_その曲へ続けて移る()
    {
        Add("a", 16, frames: 1000);
        RandomPcmDecoder second = Add("b", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        outputs.ExclusiveDrain = TimeSpan.FromMilliseconds(100);
        service.Open("a");
        service.Play();
        outputs.Last.ReadBytes(1500);

        service.SetNext("b");
        int[] continued = ReadOutput(1000);

        Assert.Equal(second.Samples, continued);
        Assert.Equal(1, advanced);
        Assert.Equal(0, ended);
        Assert.Equal("b", service.CurrentPath);
    }

    [Fact]
    public void 音量を下げると通常経路になり_最大に戻すとその位置からまた元の値どおりになる()
    {
        RandomPcmDecoder decoder = Add("a", 24);
        outputs.ExclusiveFormat = TestFormats.Pcm24In32(48000);
        service.Open("a");
        service.Play();

        int[] first = ReadOutput(1000);
        service.Volume = 0.5f;
        Assert.False(service.IsBitPerfect);
        int[] lowered = ReadOutput(1000);
        service.Volume = 1f;
        Assert.True(service.IsBitPerfect);
        int[] restored = ReadOutput(1000);

        Assert.Equal(decoder.Samples[..2000], first.Select(v => v >> 8));
        Assert.Equal(decoder.Samples[4000..6000], restored.Select(v => v >> 8));

        // 通常経路では半分の大きさになる（丸めの分だけずれる）。
        for (int i = 0; i < lowered.Length; i++)
        {
            Assert.InRange((lowered[i] >> 8) - decoder.Samples[2000 + i] / 2.0, -1.0, 1.0);
        }
        Assert.NotEqual(decoder.Samples[2000..4000], lowered.Select(v => v >> 8));
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void シークした位置から_元の値どおりに出力される()
    {
        RandomPcmDecoder decoder = Add("a", 16);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        service.Open("a");
        service.Seek(TimeSpan.FromSeconds(0.05));
        service.Play();

        int[] output = ReadOutput(500);

        Assert.Equal(decoder.Samples[4800..5800], output);
    }

    [Fact]
    public void ビット数の違う曲へ続けて移っても_境界をまたいで元の値どおりに出力される()
    {
        RandomPcmDecoder first = Add("a", 16, frames: 1000);
        RandomPcmDecoder second = Add("b", 24, frames: 1000);
        outputs.ExclusiveFormat = TestFormats.Pcm24In32(48000);
        service.Open("a");
        service.SetNext("b");
        service.Play();

        int[] output = ReadOutput(2000);

        Assert.Equal(1, advanced);
        Assert.Equal(4000, output.Length);
        Assert.Equal(first.Samples, output[..2000].Select(v => v >> 16));
        Assert.Equal(second.Samples, output[2000..].Select(v => v >> 8));
        Assert.True(service.IsBitPerfect);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void デバイスの形式に値を変えずに収まらない曲へは続けず_今の曲の終わりとして止まる()
    {
        Add("a", 16, frames: 1000);
        Add("b", 24, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        service.Open("a");
        service.SetNext("b");
        service.Play();

        int[] output = ReadOutput(2000);

        // 続けて移らないので、今の曲の 1000 フレームの後ろは無音で埋まり、曲の終わりとして通知される。
        Assert.All(output[2000..], value => Assert.Equal(0, value));
        Assert.Equal(0, advanced);
        Assert.Equal(1, ended);
        Assert.Equal("a", service.CurrentPath);
    }

    [Fact]
    public void 共有モードの出力なら_ビット数の違う曲へもそのまま続けて移る()
    {
        Add("a", 16, frames: 1000);
        Add("b", 24, frames: 1000);
        service.Open("a");
        service.SetNext("b");
        service.Play();

        outputs.Last.Read(TimeSpan.FromSeconds(1));

        Assert.Equal(1, advanced);
        Assert.False(service.IsBitPerfect);
        Assert.False(service.OutputInfo!.Exclusive);
    }

    [Fact]
    public void 出力を作り直すと_次の曲へ続けられるかの条件も新しい出力のものになる()
    {
        Add("a", 16, frames: 1000);
        Add("b", 24, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        service.Open("a");
        service.SetNext("b");
        service.Play();

        // 排他モードの 16bit では続けられないが、共有モードに作り直せば続けられる。
        outputs.ExclusiveFormat = null;
        service.RestartOutput();
        outputs.Last.Read(TimeSpan.FromSeconds(1));

        Assert.Equal(1, advanced);
        Assert.Equal("b", service.CurrentPath);
    }

    [Fact]
    public void ビット数が減る形式のデバイスでは_ビットパーフェクトにならず通常経路で量子化される()
    {
        RandomPcmDecoder decoder = Add("a", 24);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        service.Open("a");
        service.Play();

        int[] output = ReadOutput(1000);

        Assert.False(service.OutputInfo!.BitPerfectCapable);
        Assert.False(service.IsBitPerfect);
        for (int i = 0; i < output.Length; i++)
        {
            Assert.InRange(output[i] - decoder.Samples[i] / 256.0, -1.0, 1.0);
        }
    }

    [Fact]
    public void リサンプリングが要る場合は_ビットパーフェクトにならず_デバイスのサンプルレートの長さで出力される()
    {
        Add("a", 16, rate: 88200, frames: 88200);
        outputs.ExclusiveFormat = new WaveFormat(96000, 16, 2);
        service.Open("a");
        service.Play();
        FakeOutput output = outputs.Last;
        OutputInfo info = service.OutputInfo!;

        int frames = 0;
        while (output.EndedCount == 0)
        {
            frames += output.ReadBytes(4096).Length / 4;
        }

        Assert.False(info.BitPerfectCapable);
        Assert.Equal(88200, info.ResampledFrom);
        // 最後のバッファは無音で満杯にされるので、その分（最大 4096 フレーム）だけ長くなることがある。
        Assert.InRange(frames, 96000 - 200, 96000 + 4096);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public void モノラルの曲をステレオのデバイスへ出すときは_左右に同じ音を出す(float volume)
    {
        RandomPcmDecoder decoder = Add("a", 16, channels: 1, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        service.Volume = volume;
        service.Open("a");
        service.Play();

        int[] output = ReadOutput(1000);

        Assert.True(service.OutputInfo!.BitPerfectCapable);
        Assert.Null(service.OutputInfo.NormalPathReason);
        Assert.Equal(2000, output.Length);
        for (int i = 0; i < output.Length; i += 2)
        {
            Assert.Equal(output[i], output[i + 1]);
        }
        if (volume >= 1f)
        {
            Assert.True(service.IsBitPerfect);
            Assert.Equal(decoder.Samples, output.Where((_, i) => i % 2 == 0));
        }
    }

    [Fact]
    public void モノラルの曲をより大きい入れ物のステレオへ出しても_左右とも値は変わらない()
    {
        RandomPcmDecoder decoder = Add("a", 24, channels: 1, frames: 1000);
        outputs.ExclusiveFormat = TestFormats.Pcm24In32(48000);
        service.Open("a");
        service.Play();

        int[] output = ReadOutput(1000);

        Assert.True(service.IsBitPerfect);
        Assert.Equal(decoder.Samples, output.Where((_, i) => i % 2 == 0).Select(value => value >> 8));
        Assert.Equal(decoder.Samples, output.Where((_, i) => i % 2 == 1).Select(value => value >> 8));
    }

    [Fact]
    public void floatの曲は_整数のデバイスではビットパーフェクトにならない()
    {
        opener.Create = _ => new FloatDecoder();
        outputs.ExclusiveFormat = TestFormats.Pcm24In32(48000);
        service.Open("a");
        service.Play();

        int[] output = ReadOutput(100);

        Assert.False(service.OutputInfo!.BitPerfectCapable);
        Assert.All(output, value => Assert.Equal(4194304 << 8, value));
    }

    [Fact]
    public void 曲の終わりの知らせが届く前に次の曲へ移っていたら_再生を続ける()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context);
        int queuedEnded = 0;
        int queuedAdvanced = 0;
        queued.TrackEnded += (_, _) => queuedEnded++;
        queued.TrackAdvanced += (_, _) => queuedAdvanced++;
        Add("a", 16, frames: 1000);
        RandomPcmDecoder second = Add("b", 16, frames: 48000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        queued.Open("a");
        queued.Play();
        FakeOutput output = outputs.Last;

        // a をちょうど読み切ったところで次の曲の予約が頼まれ（まだ開かれていない）、その後で出力が終端に達して
        // 終わりの知らせを投げた。予約の処理が先に済み、出力が b へ乗り換えてから、終わりの知らせが処理される。
        output.ReadBytes(1000);
        queued.SetNext("b");
        output.ReadBytes(500);
        Assert.Equal(1, output.EndedCount);
        Assert.Equal(2, context.Pending);
        context.RunNext();

        int[] continued = ReadOutput(1000);
        context.RunAll();

        Assert.Equal(second.Samples[..2000], continued);
        Assert.Equal("b", queued.CurrentPath);
        Assert.Equal(PlayerState.Playing, queued.State);
        Assert.Equal(1, queuedAdvanced);
        Assert.Equal(0, queuedEnded);
        Assert.False(output.Disposed);
        Assert.Single(outputs.Created);
    }

    [Fact]
    public void 曲の終わりの知らせが届く前に乗り換えた曲が_その後で終われば_その曲の終わりとして止まる()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context);
        int queuedEnded = 0;
        queued.TrackEnded += (_, _) => queuedEnded++;
        Add("a", 16, frames: 1000);
        Add("b", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        queued.Open("a");
        queued.SetNext("b");
        context.RunAll();
        queued.Play();
        FakeOutput output = outputs.Last;

        output.ReadBytes(1000);
        output.ReadBytes(1500);
        context.RunAll();

        Assert.Equal(1, queuedEnded);
        Assert.Equal("b", queued.CurrentPath);
        Assert.Equal(PlayerState.Stopped, queued.State);
        Assert.True(output.Disposed);
    }

    [Fact]
    public void 曲の終わりの知らせが届く前にシークで戻していたら_出力を作り直さずに再生を続ける()
    {
        var context = new QueueContext();
        using var queued = new PlaybackService(opener, outputs, context);
        int queuedEnded = 0;
        queued.TrackEnded += (_, _) => queuedEnded++;
        RandomPcmDecoder decoder = Add("a", 16, frames: 1000);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        queued.Open("a");
        queued.Play();
        FakeOutput output = outputs.Last;
        output.ReadBytes(1500);

        queued.Seek(TimeSpan.Zero);
        context.RunAll();

        Assert.Equal(PlayerState.Playing, queued.State);
        Assert.Equal(0, queuedEnded);
        Assert.Single(outputs.Created);
        Assert.False(output.Disposed);
        Assert.Equal(decoder.Samples[..1000], ReadOutput(500));
    }

    [Theory]
    [InlineData(16, 1, 16, 16, 2, null)]
    [InlineData(24, 1, 16, 16, 2, "モノラルをステレオに変換")]
    [InlineData(24, 2, 16, 16, 2, "24 bit を 16 bit に変換")]
    [InlineData(32, 2, 32, 24, 2, "32 bit を 24 bit に変換")]
    [InlineData(24, 2, 32, 24, 2, null)]
    public void ビットパーフェクトにできない理由を持つ(
        int sourceBits, int sourceChannels, int container, int valid, int deviceChannels, string? expected)
    {
        Add("a", sourceBits, channels: sourceChannels);
        outputs.ExclusiveFormat = new WaveFormatExtensible(48000, container, deviceChannels, TestFormats.Pcm, valid, 3);
        service.Open("a");
        service.Play();

        Assert.Equal(expected, service.OutputInfo!.NormalPathReason);
        Assert.Equal(expected is null, service.OutputInfo.BitPerfectCapable);
    }

    [Fact]
    public void floatの曲には_整数に変換するという理由が付く()
    {
        opener.Create = _ => new FloatDecoder();
        outputs.ExclusiveFormat = TestFormats.Pcm24In32(48000);
        service.Open("a");
        service.Play();

        Assert.Equal("float を整数に変換", service.OutputInfo!.NormalPathReason);
    }

    [Fact]
    public void 出力がなければ_ビットパーフェクトではない()
    {
        Add("a", 16);
        outputs.ExclusiveFormat = new WaveFormat(48000, 16, 2);
        service.Open("a");

        Assert.False(service.IsBitPerfect);
        Assert.Null(service.OutputInfo);

        service.Play();
        Assert.True(service.IsBitPerfect);
        service.Stop();
        Assert.False(service.IsBitPerfect);
    }

    /// <summary>すべてのサンプルが 0.5 の float を返す。</summary>
    private sealed class FloatDecoder : IAudioDecoder
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public TimeSpan Duration => TimeSpan.FromSeconds(1);

        public TimeSpan Position { get; set; }

        public int Read(Span<byte> buffer)
        {
            Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
            samples.Fill(0.5f);
            return samples.Length * sizeof(float);
        }

        public void Dispose()
        {
        }
    }
}

public class OutputFallbackTests
{
    private readonly List<string> tried = [];

    private OutputAttempt Attempt(string name, Exception? failure = null) => new(
        $"{name}を使えません",
        $"{name}で再生しています",
        notice =>
        {
            tried.Add(name);
            return failure is null ? new NoticeOutput(name, notice) : throw failure;
        });

    private static Exception Com(int hresult) => new COMException("raw", hresult);

    [Fact]
    public void 最初の候補で作れれば_説明は付かず_後の候補は試さない()
    {
        IAudioOutput output = OutputFallback.Create([Attempt("排他モード"), Attempt("共有モード")]);

        Assert.Equal("排他モード", output.DeviceName);
        Assert.Null(output.Info.Notice);
        Assert.Equal(["排他モード"], tried);
    }

    [Fact]
    public void 前の候補が失敗したら次を試し_失敗の理由と代わりの出力を説明に入れる()
    {
        IAudioOutput output = OutputFallback.Create(
        [
            Attempt("排他モード", Com(unchecked((int)0x8889000A))),
            Attempt("共有モード"),
            Attempt("既定のデバイス"),
        ]);

        Assert.Equal("共有モード", output.DeviceName);
        Assert.Equal("排他モードを使えません（他のアプリがデバイスを使用中です）。共有モードで再生しています。", output.Info.Notice);
        Assert.Equal(["排他モード", "共有モード"], tried);
    }

    [Fact]
    public void 複数の候補が失敗した場合は_それぞれの理由を並べる()
    {
        IAudioOutput output = OutputFallback.Create(
        [
            Attempt("排他モード", Com(unchecked((int)0x8889000E))),
            Attempt("共有モード", new InvalidOperationException("壊れています")),
            Attempt("既定のデバイス"),
        ]);

        Assert.Equal(
            "排他モードを使えません（デバイスの設定で排他モードが許可されていません）。共有モードを使えません（壊れています）。既定のデバイスで再生しています。",
            output.Info.Notice);
    }

    [Fact]
    public void どの候補でも作れなければ_すべての理由を含む例外を投げる()
    {
        var last = new InvalidOperationException("最後の理由");

        var ex = Assert.Throws<InvalidOperationException>(() => OutputFallback.Create(
        [
            Attempt("排他モード", Com(unchecked((int)0x88890008))),
            Attempt("共有モード", last),
        ]));

        Assert.Equal("排他モードを使えません（デバイスがこの形式を受け付けません）。共有モードを使えません（最後の理由）。", ex.Message);
        Assert.Same(last, ex.InnerException);
    }

    [Fact]
    public void 候補が1つもなければ_デバイスがないという例外を投げる()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OutputFallback.Create([]));

        Assert.Contains("出力デバイスがありません", ex.Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x88890008), "デバイスがこの形式を受け付けません")]
    [InlineData(unchecked((int)0x8889000A), "他のアプリがデバイスを使用中です")]
    [InlineData(unchecked((int)0x8889000E), "デバイスの設定で排他モードが許可されていません")]
    [InlineData(unchecked((int)0x88890004), "デバイスが使えなくなりました")]
    [InlineData(unchecked((int)0x80070057), "raw")]
    public void 失敗の原因をエラーコードから言葉にする(int hresult, string expected)
    {
        Assert.Equal(expected, OutputFallback.DescribeFailure(Com(hresult)));
    }

    [Fact]
    public void 別の例外に包まれていても_中のエラーコードから原因を言葉にする()
    {
        var wrapped = new InvalidOperationException("outer", Com(unchecked((int)0x8889000A)));

        Assert.Equal("他のアプリがデバイスを使用中です", OutputFallback.DescribeFailure(wrapped));
        Assert.Equal("plain", OutputFallback.DescribeFailure(new IOException("plain")));
    }

    private sealed class NoticeOutput(string name, string? notice) : IAudioOutput
    {
        public event EventHandler<AudioOutputStoppedEventArgs>? Stopped
        {
            add { }
            remove { }
        }

        public string DeviceId => name;

        public string DeviceName => name;

        public OutputInfo Info { get; } = new(false, null, false, null, null, notice);

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Dispose()
        {
        }
    }
}
