using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Decoding;

/// <summary>NAudio のリーダーを使う内蔵デコーダ。</summary>
public abstract class WaveStreamDecoderFactory : IAudioDecoderFactory
{
    protected WaveStreamDecoderFactory(string name, params string[] extensions)
    {
        Name = name;
        Extensions = extensions;
    }

    public string Name { get; }

    public IReadOnlyList<string> Extensions { get; }

    public virtual bool IsFallback => false;

    public IAudioDecoder Open(string path)
    {
        WaveStream stream = CreateStream(path);
        try
        {
            return new WaveStreamDecoder(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    protected abstract WaveStream CreateStream(string path);
}

public sealed class WavDecoderFactory() : WaveStreamDecoderFactory("WAV", ".wav", ".wave")
{
    protected override WaveStream CreateStream(string path) => new WaveFileReader(path);
}

/// <summary>
/// Media Foundation で読める形式全般。MP3・AAC/M4A・WMA・FLAC を担当し、他のデコーダが失敗したときの代替にもなる。
/// </summary>
/// <remarks>
/// MP3 に NAudio の <c>Mp3FileReaderBase</c> を使わないのは、同じリーダーで 2 回目のシークをすると
/// 位置が進まないままデータを返し続けて終端に達しなくなるため（3.1.0 と 3.1.1-preview.2 で確認）。
/// </remarks>
public sealed class MediaFoundationDecoderFactory()
    : WaveStreamDecoderFactory("Media Foundation", ".mp3", ".m4a", ".aac", ".mp4", ".m4b", ".wma", ".flac")
{
    public override bool IsFallback => true;

    protected override WaveStream CreateStream(string path) => new MediaFoundationReader(path);
}
