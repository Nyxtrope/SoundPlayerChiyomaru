using NAudio.Wave;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>
/// 出力が読む再生元。デコードした音声を、整数 PCM のまま（サンプル値を変えずに）読む口と、float に変換して読む口の両方を持つ。
/// 読み取りは出力スレッドから呼ばれる。
/// </summary>
public interface IPlaybackSource
{
    /// <summary>今読んでいる曲のデコード結果の形式。次の曲へ続けて移ると変わることがある（サンプルレートとチャンネル数は変わらない）。</summary>
    WaveFormat Format { get; }

    /// <summary>音量。0.0（無音）から 1.0（最大）。読み取った音声にはまだ掛かっていない。</summary>
    float Volume { get; }

    /// <summary>
    /// <see cref="ReadFloat"/> が返す音声に、曲ごとのゲイン（ReplayGain）を掛けているか。
    /// <see cref="ReadPcm"/> には掛からないので、true の間、出力は <see cref="ReadFloat"/> で読むこと。
    /// </summary>
    bool AppliesGain { get; }

    /// <summary>
    /// 出力を止めずに次の曲へ続けてよいかを、次の曲の形式から判定する関数。出力が、自分の形式に収まる曲だけを受け入れるために設定する。
    /// null の場合は、サンプルレートとチャンネル数が同じなら続ける。
    /// </summary>
    Func<WaveFormat, bool>? CanContinueWith { get; set; }

    /// <summary>float で読む。サンプルレートとチャンネル数は <see cref="Format"/> と同じ。終端に達したら 0 を返す。</summary>
    int ReadFloat(Span<float> buffer);

    /// <summary>
    /// デコード結果のバイト列をそのまま読む。1 回の呼び出しで返すのは 1 曲分だけで、その形式を <paramref name="format"/> で返す。
    /// 要求より短く返すことがある。終端に達したら 0 を返す。
    /// </summary>
    /// <param name="buffer">読み取り先。<paramref name="maxSamples"/> 個のサンプルが、どのビット数の曲でも入る大きさが必要。</param>
    /// <param name="maxSamples">
    /// 読むサンプル数の上限（チャンネル数の倍数）。バイト数ではなくサンプル数で指定するのは、
    /// 読んでいる途中で次の曲へ移るとビット数が変わることがあり、呼び出し側は事前にバイト数を決められないため。
    /// </param>
    int ReadPcm(Span<byte> buffer, int maxSamples, out WaveFormat format);
}
