using System.Collections.Concurrent;

namespace SoundPlayerChiyomaru.Core.Playlists;

/// <summary>
/// プレイリストの項目のタグとファイルの有無を、画面を止めずに裏で読む。
/// </summary>
public sealed class TrackInfoLoader
{
    /// <summary>一度にこの曲数以上を読んだら、読み終えた後にヒープを詰める。</summary>
    private const int CompactThreshold = 200;

    private readonly ITrackInfoReader reader;
    private readonly SynchronizationContext? context;
    private readonly ConcurrentQueue<PlaylistItem> queue = new();
    private int workerRunning;

    /// <param name="context">
    /// 読み取り結果を項目に反映するスレッドのコンテキスト。null の場合は <see cref="Enqueue"/> の中で同期的に読む（テスト用）。
    /// </param>
    public TrackInfoLoader(ITrackInfoReader reader, SynchronizationContext? context)
    {
        this.reader = reader;
        this.context = context;
    }

    /// <summary>
    /// 読み取った結果を 1 曲に反映した。反映と同じスレッドで、1 曲ごとに呼ばれる
    /// （数千曲を読むと数千回になるので、受け取る側でまとめること）。
    /// </summary>
    public event EventHandler? InfoApplied;

    /// <summary>まだ読み取りを依頼していない項目だけを読み取り待ちに入れる。<paramref name="context"/> のスレッドから呼ぶこと。</summary>
    public void Enqueue(IEnumerable<PlaylistItem> items)
    {
        bool added = false;
        foreach (PlaylistItem item in items)
        {
            if (item.InfoRequested)
            {
                continue;
            }

            item.InfoRequested = true;
            if (context is null)
            {
                Load(item);
            }
            else
            {
                queue.Enqueue(item);
                added = true;
            }
        }

        if (added && Interlocked.CompareExchange(ref workerRunning, 1, 0) == 0)
        {
            Task.Run(Work);
        }
    }

    private void Work()
    {
        int loaded = 0;
        do
        {
            while (queue.TryDequeue(out PlaylistItem? item))
            {
                Load(item);
                loaded++;
            }
            Volatile.Write(ref workerRunning, 0);

            // 止める直前に追加された項目を取りこぼさないよう、もう一度確かめる。
        }
        while (!queue.IsEmpty && Interlocked.CompareExchange(ref workerRunning, 1, 0) == 0);

        // 読み取ったタグは項目に残るので、まとめて読むと古い世代へ移り、途中の一時オブジェクトとまじってヒープがすき間だらけになる。
        // 若い世代の GC だけでは OS に返らない（10,000 曲で約 8MB。SPEC 8.3 の実測）。
        // まとめて読み終えた時点で 1 回だけ、ヒープを詰めて返す。
        if (loaded >= CompactThreshold)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private void Load(PlaylistItem item)
    {
        bool exists = File.Exists(item.Path);
        TrackInfo? info = exists ? reader.Read(item.Path) : null;

        if (context is null)
        {
            Apply(item, info, exists);
        }
        else
        {
            context.Post(_ => Apply(item, info, exists), null);
        }
    }

    private void Apply(PlaylistItem item, TrackInfo? info, bool exists)
    {
        item.ApplyInfo(info, exists);
        InfoApplied?.Invoke(this, EventArgs.Empty);
    }
}
