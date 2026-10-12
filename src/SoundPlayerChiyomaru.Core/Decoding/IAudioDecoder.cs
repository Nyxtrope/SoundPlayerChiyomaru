using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Decoding;

/// <summary>
/// 1 つの音声ファイルをデコードして PCM を返す。内蔵デコーダと、将来の DirectShow 経由のデコーダを同じ形で扱うための境界。
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// デコード結果の形式。整数 PCM または IEEE float で、<c>WAVE_FORMAT_EXTENSIBLE</c> ではない形に正規化されている。
    /// </summary>
    WaveFormat WaveFormat { get; }

    TimeSpan Duration { get; }

    /// <summary>設定時は 0 から <see cref="Duration"/> の範囲に丸められる。</summary>
    TimeSpan Position { get; set; }

    /// <summary>終端に達したら 0 を返す。</summary>
    int Read(Span<byte> buffer);
}
