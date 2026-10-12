using NAudio.Wave;
using SoundPlayerChiyomaru.Core.Decoding;

namespace SoundPlayerChiyomaru.Core.Playback;

/// <summary>
/// デコーダを出力へつなぐ。出力スレッドの読み取りと、UI スレッドからのシーク・位置取得を排他する。
/// </summary>
internal sealed class DecoderSource(IAudioDecoder decoder) : IWaveProvider
{
    private readonly Lock gate = new();
    private volatile bool endReached;
    private bool soughtAfterEnd;

    public WaveFormat WaveFormat => decoder.WaveFormat;

    /// <summary>最後の読み取りで終端に達したか。シークすると解除される。</summary>
    public bool EndReached => endReached;

    public TimeSpan Position
    {
        get
        {
            lock (gate)
            {
                return decoder.Position;
            }
        }
        set
        {
            lock (gate)
            {
                if (endReached)
                {
                    soughtAfterEnd = true;
                }
                decoder.Position = value;
                endReached = false;
            }
        }
    }

    /// <summary>
    /// 終端まで読まれた後にシークされたかを返し、記録を消す。
    /// 出力は終端を読んだ後もバッファの残りを鳴らしてから止まるので、その間のシークを停止後に検知するために使う。
    /// </summary>
    public bool TakeSoughtAfterEnd()
    {
        lock (gate)
        {
            bool sought = soughtAfterEnd;
            soughtAfterEnd = false;
            return sought;
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (gate)
        {
            int read = decoder.Read(buffer);
            if (read == 0 && buffer.Length > 0)
            {
                endReached = true;
            }
            return read;
        }
    }
}
