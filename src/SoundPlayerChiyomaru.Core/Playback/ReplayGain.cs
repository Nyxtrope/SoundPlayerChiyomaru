namespace SoundPlayerChiyomaru.Core.Playback;

public enum ReplayGainMode
{
    Off,

    /// <summary>曲ごとの値を使う。曲ごとの聞こえる大きさが揃う。</summary>
    Track,

    /// <summary>アルバムごとの値を使う。アルバムの中の曲どうしの音量差は保たれる。</summary>
    Album,
}

/// <summary>ファイルのタグに書かれた ReplayGain の値。書かれていない項目は null。</summary>
/// <param name="TrackGainDb">曲ごとのゲイン（dB）。</param>
/// <param name="TrackPeak">曲のピーク（最大振幅を 1.0 とした比）。</param>
public sealed record ReplayGainTags(double? TrackGainDb, double? TrackPeak, double? AlbumGainDb, double? AlbumPeak)
{
    /// <summary>これを超える大きさのゲイン（dB）は、壊れたタグとみなして使わない。実際の値は ±20 dB 程度に収まる。</summary>
    private const double MaxGainDb = 60;

    /// <summary>
    /// 実際に掛けるゲイン（dB）を決める。選んだ側の値がなければもう一方を使い、どちらもなければ null（何も掛けない）。
    /// 値が壊れている場合も null。
    /// クリップしないように、ピークが分かっていればピークが最大振幅を超えないところまで抑える。
    /// ピークが分からない曲は、音量を上げる方向には掛けない。
    /// </summary>
    public double? ResolveDb(ReplayGainMode mode)
    {
        if (mode == ReplayGainMode.Off)
        {
            return null;
        }

        (double? gain, double? peak) = mode == ReplayGainMode.Album
            ? (AlbumGainDb ?? TrackGainDb, AlbumGainDb is null ? TrackPeak : AlbumPeak)
            : (TrackGainDb ?? AlbumGainDb, TrackGainDb is null ? AlbumPeak : TrackPeak);
        if (gain is not { } db || !double.IsFinite(db) || Math.Abs(db) > MaxGainDb)
        {
            return null;
        }

        double limit = peak is { } p && double.IsFinite(p) && p > 0 ? -20 * Math.Log10(p) : 0;
        return Math.Min(db, limit);
    }

    /// <summary>dB を、サンプルに掛ける倍率にする。</summary>
    public static float ToScale(double db) => (float)Math.Pow(10, db / 20);
}

public interface IReplayGainReader
{
    /// <summary>ReplayGain のタグを読む。タグがない、対応していない形式、壊れたファイルでは null を返す（例外は投げない）。</summary>
    ReplayGainTags? ReadReplayGain(string path);
}
