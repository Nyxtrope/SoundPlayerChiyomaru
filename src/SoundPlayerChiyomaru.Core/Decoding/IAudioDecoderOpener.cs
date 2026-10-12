namespace SoundPlayerChiyomaru.Core.Decoding;

public interface IAudioDecoderOpener
{
    /// <exception cref="FileNotFoundException">ファイルが存在しない。</exception>
    /// <exception cref="AudioDecodeException">どのデコーダでも開けない。</exception>
    IAudioDecoder Open(string path);
}
