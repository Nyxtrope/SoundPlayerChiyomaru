namespace SoundPlayerChiyomaru.Core.Decoding;

/// <summary>試したすべてのデコーダでファイルを開けなかった。個々の失敗は <see cref="Exception.InnerException"/> に入る。</summary>
public sealed class AudioDecodeException(string message, Exception? innerException) : Exception(message, innerException);
