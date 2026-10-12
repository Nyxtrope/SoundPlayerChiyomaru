using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Tests;

[CollectionDefinition(TestAudioFiles.Collection)]
public sealed class TestAudioFilesCollection : ICollectionFixture<TestAudioFiles>;

/// <summary>
/// テスト用の音声ファイル（440Hz・振幅 0.25 の正弦波、2 秒、44.1kHz ステレオ）を生成する。
/// リポジトリの <c>artifacts/test-audio</c> に置くので、<c>tools/Measure-Startup.ps1</c> の再生確認にも使える。
/// </summary>
public sealed class TestAudioFiles
{
    /// <summary>
    /// このファイル群を使うテストクラスが属するコレクション。クラスごとに生成すると、
    /// 並列実行されたときに同じファイルへ同時に書き込んで失敗するので、1 回だけ生成して共有する。
    /// </summary>
    public const string Collection = "TestAudioFiles";

    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const double Seconds = 2.0;
    public const double Amplitude = 0.25;

    private const int Frames = (int)(SampleRate * Seconds);
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public TestAudioFiles()
    {
        Directory = FindDirectory();
        System.IO.Directory.CreateDirectory(Directory);

        byte[] pcm16 = Render(2, (span, v) => BitConverter.TryWriteBytes(span, (short)(v * short.MaxValue)));
        byte[] pcm24 = Render(3, (span, v) =>
        {
            int sample = (int)(v * 8388607);
            span[0] = (byte)sample;
            span[1] = (byte)(sample >> 8);
            span[2] = (byte)(sample >> 16);
        });
        byte[] float32 = Render(4, (span, v) => BitConverter.TryWriteBytes(span, (float)v));

        WriteWav("pcm16.wav", pcm16, bits: 16, subFormat: null);
        WriteWav("pcm24-extensible.wav", pcm24, bits: 24, subFormat: PcmSubFormat);
        WriteWav("float32-extensible.wav", float32, bits: 32, subFormat: FloatSubFormat);

        MediaFoundationApi.Startup();
        Encode("tone.mp3", pcm16, (source, path) => MediaFoundationEncoder.EncodeToMp3(source, path, 192000));
        Encode("tone.m4a", pcm16, (source, path) => MediaFoundationEncoder.EncodeToAac(source, path, 192000));
        Encode("tone.flac", pcm16, MediaFoundationEncoder.EncodeToFlac);
        Encode("tone.wma", pcm16, (source, path) => MediaFoundationEncoder.EncodeToWma(source, path, 192000));

        File.WriteAllBytes(PathOf("garbage.mp3"), new byte[4096]);
    }

    public string Directory { get; }

    public string PathOf(string name) => Path.Combine(Directory, name);

    private static string FindDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SoundPlayerChiyomaru.slnx")))
            {
                return Path.Combine(dir.FullName, "artifacts", "test-audio");
            }
        }
        return Path.Combine(Path.GetTempPath(), "SoundPlayerChiyomaru-test-audio");
    }

    private static byte[] Render(int bytesPerSample, Action<Span<byte>, double> write)
    {
        byte[] data = new byte[Frames * Channels * bytesPerSample];
        for (int frame = 0; frame < Frames; frame++)
        {
            double value = Math.Sin(2 * Math.PI * 440 * frame / SampleRate) * Amplitude;
            for (int channel = 0; channel < Channels; channel++)
            {
                write(data.AsSpan((frame * Channels + channel) * bytesPerSample, bytesPerSample), value);
            }
        }
        return data;
    }

    /// <summary>NAudio の書き込み処理に頼らず、ヘッダーを直接書く。subFormat を指定すると WAVE_FORMAT_EXTENSIBLE になる。</summary>
    private void WriteWav(string name, byte[] data, int bits, Guid? subFormat)
    {
        int blockAlign = Channels * bits / 8;
        int formatSize = subFormat is null ? 16 : 40;

        using var writer = new BinaryWriter(File.Create(PathOf(name)));
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + formatSize + 8 + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(formatSize);
        writer.Write((ushort)(subFormat is null ? 1 : 0xFFFE));
        writer.Write((ushort)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * blockAlign);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)bits);
        if (subFormat is Guid guid)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write(3);
            writer.Write(guid.ToByteArray());
        }
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
    }

    private void Encode(string name, byte[] pcm16, Action<IWaveProvider, string> encode)
    {
        using var source = new RawSourceWaveStream(new MemoryStream(pcm16), new WaveFormat(SampleRate, 16, Channels));
        encode(source, PathOf(name));
    }
}
