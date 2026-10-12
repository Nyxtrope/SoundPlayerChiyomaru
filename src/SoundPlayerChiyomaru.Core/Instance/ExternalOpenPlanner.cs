namespace SoundPlayerChiyomaru.Core.Instance;

/// <summary>起動済みのインスタンスにファイルを渡されたときの動作（SPEC 5 章）。</summary>
public enum ExternalOpenAction
{
    /// <summary>プレイリストを置き換えて再生する。</summary>
    ReplaceAndPlay,

    /// <summary>プレイリストの末尾に追加する。再生中の曲は変えない。</summary>
    Append,

    /// <summary>プレイリストの末尾に追加し、追加した曲を再生する。</summary>
    AppendAndPlay,
}

/// <summary>渡されたファイルに対して実際に行う操作。</summary>
public enum ExternalOpenOperation
{
    /// <summary>プレイリストを置き換えて再生する。</summary>
    Replace,

    /// <summary>末尾に追加するだけ。</summary>
    Add,

    /// <summary>末尾に追加し、追加した先頭の曲を再生する。</summary>
    AddAndPlay,

    /// <summary>末尾に追加し、何も再生していなければ追加した先頭の曲を再生する。</summary>
    AddAndPlayIfIdle,
}

/// <summary>
/// 渡されたファイルをどう扱うかを決める。短い間隔で続けて届いた要求は、1 回の操作の続きとして扱う。
/// </summary>
/// <remarks>
/// エクスプローラーで複数のファイルを選んで開くと、ファイルごとに別々のプロセスが起動して 1 個ずつ届く。
/// 2 個目以降を新しい操作として扱うと、「置き換えて再生」では最後の 1 曲しか残らない。
/// </remarks>
public sealed class ExternalOpenPlanner
{
    private readonly TimeSpan window;
    private readonly TimeProvider time;
    private long? lastTimestamp;
    private bool burstPlays;

    /// <param name="window">前の要求の到着からこの時間以内に届いた要求を、続きとみなす。</param>
    public ExternalOpenPlanner(TimeSpan window, TimeProvider? time = null)
    {
        this.window = window;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>要求の到着時刻として記録するための、今の時刻。</summary>
    public long Now => time.GetTimestamp();

    /// <summary>
    /// 起動時に引数で渡されたファイルを開くことを記録する。操作は常に「置き換えて再生」（SPEC 4.2）で、
    /// この後すぐに届く要求の起点になる。
    /// </summary>
    /// <param name="timestamp">プロセスが起動した時刻（<see cref="Now"/> の値）。</param>
    public void RegisterStartup(long timestamp)
    {
        lastTimestamp = timestamp;
        burstPlays = true;
    }

    /// <summary>届いた要求に対する操作を決める。</summary>
    /// <param name="timestamp">
    /// 要求が届いた時刻（<see cref="Now"/> の値）。処理を始めた時刻ではなく届いた時刻を渡すこと。
    /// 前の要求の処理に時間がかかっても、続きの判定がずれないようにするため。
    /// </param>
    public ExternalOpenOperation Plan(long timestamp, ExternalOpenAction action)
    {
        bool continuation = lastTimestamp is { } last && time.GetElapsedTime(last, timestamp) <= window;

        // 続きが届き続ける限り、間隔は最後の要求から数え直す。
        lastTimestamp = timestamp;

        if (continuation)
        {
            // 再生する操作の続きなら、先頭の曲が開けずに止まっている場合に備えて、止まっていれば再生を始める。
            return burstPlays ? ExternalOpenOperation.AddAndPlayIfIdle : ExternalOpenOperation.Add;
        }

        burstPlays = action != ExternalOpenAction.Append;
        return action switch
        {
            ExternalOpenAction.Append => ExternalOpenOperation.Add,
            ExternalOpenAction.AppendAndPlay => ExternalOpenOperation.AddAndPlay,
            _ => ExternalOpenOperation.Replace,
        };
    }
}
