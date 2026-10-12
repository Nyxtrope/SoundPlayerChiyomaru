namespace SoundPlayerChiyomaru.Core.Decoding;

public interface IAudioDecoderFactory
{
    string Name { get; }

    /// <summary>担当する拡張子。<c>.mp3</c> のようにドット付き・小文字。</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// 担当する拡張子以外のファイルにも、他のデコーダが開けなかったときの代替として試してよいか。
    /// </summary>
    bool IsFallback { get; }

    /// <exception cref="Exception">このデコーダでは開けない場合。呼び出し側は次の候補を試す。</exception>
    IAudioDecoder Open(string path);
}
