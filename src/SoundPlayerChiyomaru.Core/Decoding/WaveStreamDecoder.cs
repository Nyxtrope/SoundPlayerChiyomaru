using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Decoding;

/// <summary>NAudio の <see cref="WaveStream"/> を <see cref="IAudioDecoder"/> として扱う。</summary>
public sealed class WaveStreamDecoder : IAudioDecoder
{
    private readonly WaveStream stream;

    /// <exception cref="NotSupportedException">整数 PCM でも IEEE float でもない（ADPCM など）。</exception>
    public WaveStreamDecoder(WaveStream stream)
    {
        // 24bit や float の WAV は WAVE_FORMAT_EXTENSIBLE で書かれていることが多い。
        // SubFormat が PCM / IEEE float なら中身は同じなので、通常形式として扱う（SPEC 3.2「WAV の互換性」）。
        WaveFormat format = stream.WaveFormat.AsStandardWaveFormat();
        if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
        {
            throw new NotSupportedException($"PCM ではない形式です: {format.Encoding}");
        }

        this.stream = stream;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan Duration => stream.TotalTime;

    public TimeSpan Position
    {
        get => stream.CurrentTime;
        set
        {
            // WaveStream.CurrentTime の設定はブロック境界に揃わないことがあるため、サンプル単位で計算する。
            long frames = (long)(Math.Max(0, value.TotalSeconds) * WaveFormat.SampleRate);
            long totalFrames = stream.Length / WaveFormat.BlockAlign;
            stream.Position = Math.Min(frames, totalFrames) * WaveFormat.BlockAlign;
        }
    }

    public int Read(Span<byte> buffer) => stream.Read(buffer);

    public void Dispose() => stream.Dispose();
}
