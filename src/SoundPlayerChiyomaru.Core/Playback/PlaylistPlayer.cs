using SoundPlayerChiyomaru.Core.Playlists;

namespace SoundPlayerChiyomaru.Core.Playback;

/// <summary>再生モード（SPEC 3.3）。</summary>
public enum PlayMode
{
    /// <summary>順に再生し、最後の曲で止まる。</summary>
    Normal,

    /// <summary>今の曲を繰り返す。</summary>
    RepeatOne,

    /// <summary>順に再生し、最後の曲の次は最初に戻る。</summary>
    RepeatAll,

    /// <summary>無作為に選んで再生し続ける。</summary>
    Random,

    /// <summary>今の曲が終わったら止まる（1Shot）。</summary>
    OneShot,
}

/// <summary>
/// プレイリストと再生サービスをつなぐ。どの曲を次に再生するかを再生モードから決め、曲の終わりで自動的に次へ進める。
/// 1 つのスレッド（UI スレッド）から使うこと。
/// </summary>
public sealed class PlaylistPlayer : IDisposable
{
    private const int MaxHistory = 200;
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);

    private readonly Playlist playlist;
    private readonly PlaybackService playback;
    private readonly Random random;

    /// <summary>再生した順。<see cref="historyIndex"/> が今の曲を指す。「前へ」で戻っても、その先は消さずに残す。</summary>
    private readonly List<PlaylistItem> history = [];
    private int historyIndex = -1;

    private PlayMode mode;
    private PlaylistItem? plannedNext;

    /// <summary>
    /// 「次にこの曲から再生」で指定された曲。今の曲が終わったら、再生モードに関係なくこの曲へ進み、そこからは再生モードに従う。
    /// 別の曲へ移ったら（この曲へ進んだ場合も、利用者が他の曲を選んだ場合も）取り消す。
    /// </summary>
    private PlaylistItem? queuedNext;

    /// <summary>直前の変更が入る前の並び。今の曲が削除されたときに、その後ろにあった曲を探すために持つ。</summary>
    private PlaylistItem[] lastOrder;

    /// <summary>今の曲がプレイリストから削除された場合に、その位置を引き継ぐ曲（削除前に後ろにあった曲）。</summary>
    private PlaylistItem? successor;

    private enum PlayResult
    {
        Started,
        OpenFailed,
        OutputFailed,
    }

    public PlaylistPlayer(Playlist playlist, PlaybackService playback, Random? random = null)
    {
        this.playlist = playlist;
        this.playback = playback;
        this.random = random ?? Random.Shared;
        lastOrder = [.. playlist.Items];

        playlist.Changed += OnPlaylistChanged;
        playback.TrackEnded += OnTrackEnded;
        playback.TrackAdvanced += OnTrackAdvanced;
    }

    /// <summary><see cref="Current"/> が変わった（同じ曲を繰り返して再生し直した場合を含む）。</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>曲を開けなかった、または再生を始められなかった。</summary>
    public event EventHandler<TrackFailedEventArgs>? TrackFailed;

    /// <summary>再生対象の曲。プレイリストから削除された後も、再生が続いている間は保持する。</summary>
    public PlaylistItem? Current { get; private set; }

    public PlayMode Mode
    {
        get => mode;
        set
        {
            if (mode != value)
            {
                mode = value;
                PlanNext();
            }
        }
    }

    /// <summary>再生を始めずに、再生対象の曲だけを選ぶ（前回終了時の曲の復元用）。</summary>
    public void Select(PlaylistItem? item)
    {
        playback.Close();
        SetCurrent(item, recordHistory: false);
    }

    /// <summary>指定した曲を再生する。開けなければ <see cref="TrackFailed"/> を通知し、他の曲へは進まない。</summary>
    public bool Play(PlaylistItem item) => TryPlay(item) == PlayResult.Started;

    /// <summary>
    /// 今の曲が終わったら、指定した曲へ進むようにする。何も開いていなければ、すぐにその曲を再生する。
    /// </summary>
    public void PlayAfterCurrent(PlaylistItem item)
    {
        playback.CatchUp();
        if (playback.CurrentPath is null)
        {
            TryPlay(item);
            return;
        }

        queuedNext = item;
        PlanNext();
    }

    /// <summary>再生と一時停止を切り替える。何も開いていなければ、選ばれている曲（なければ最初の曲）から再生する。</summary>
    public void PlayPause()
    {
        playback.CatchUp();
        if (playback.CurrentPath is null)
        {
            PlaylistItem? start = Current is not null && playlist.IndexOf(Current) >= 0
                ? Current
                : mode == PlayMode.Random ? PickRandom() : playlist.Items.FirstOrDefault();
            PlaySkippingUnopenable(start);
        }
        else if (playback.State == PlayerState.Playing)
        {
            playback.Pause();
        }
        else
        {
            StartPlayback(Current);
        }
    }

    public void Stop() => playback.Stop();

    /// <summary>次の曲を再生する。最後の曲では、全曲リピートのときだけ最初へ戻る。</summary>
    public void Next()
    {
        playback.CatchUp();
        PlaylistItem? next = queuedNext ?? (mode == PlayMode.Random
            ? plannedNext ?? PickRandom()
            : Following(Current, wrap: mode == PlayMode.RepeatAll));
        PlaySkippingUnopenable(next);
    }

    /// <summary>
    /// 曲の途中なら先頭に戻り、先頭付近なら前の曲を再生する。ランダムでは再生した履歴をさかのぼる。
    /// </summary>
    public void Previous()
    {
        playback.CatchUp();
        if (playback.CurrentPath is not null && playback.Position > RestartThreshold)
        {
            playback.Seek(TimeSpan.Zero);
            return;
        }

        if (mode == PlayMode.Random)
        {
            if (historyIndex > 0)
            {
                historyIndex--;
                TryPlay(history[historyIndex], recordHistory: false);
            }
            else
            {
                playback.Seek(TimeSpan.Zero);
            }
            return;
        }

        if (Preceding(Current) is { } previous)
        {
            TryPlay(previous);
        }
        else
        {
            playback.Seek(TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        playlist.Changed -= OnPlaylistChanged;
        playback.TrackEnded -= OnTrackEnded;
        playback.TrackAdvanced -= OnTrackAdvanced;
    }

    private void OnTrackEnded(object? sender, EventArgs e)
    {
        // 予約した次の曲へ出力を止めずに続けられなかった（形式が違う、予約が間に合わなかった）場合もここに来る。
        PlaySkippingUnopenable(plannedNext ?? NaturalNext());
    }

    private void OnTrackAdvanced(object? sender, EventArgs e)
    {
        // 予約していた曲が、通知が届く前にプレイリストから消えていることがある。その場合もパスから探して追従する。
        PlaylistItem? advanced = plannedNext?.Path == playback.CurrentPath
            ? plannedNext
            : playlist.Items.FirstOrDefault(item => item.Path == playback.CurrentPath);
        SetCurrent(advanced, recordHistory: true);
        PlanNext();
    }

    private void OnPlaylistChanged(object? sender, EventArgs e)
    {
        // 出力側が次の曲へ乗り換え済みなら、先に今の曲を追いつかせる。古い「今の曲」を基準に位置を決めないため。
        playback.CatchUp();

        if (Current is not null)
        {
            bool present = playlist.IndexOf(Current) >= 0;
            successor = present ? null : FindSuccessor();

            // 削除を元に戻して一覧に戻ってきた場合に、印も戻す。
            Current.IsCurrent = present;
        }
        lastOrder = [.. playlist.Items];
        if (queuedNext is not null && playlist.IndexOf(queuedNext) < 0)
        {
            queuedNext = null;
        }

        int removed = history.RemoveAll(item => playlist.IndexOf(item) < 0 && !ReferenceEquals(item, Current));
        if (removed > 0)
        {
            historyIndex = Current is null ? history.Count - 1 : history.LastIndexOf(Current);
        }

        PlanNext();
    }

    /// <summary>
    /// 今の曲がプレイリストにないとき、その位置を引き継ぐ曲を決める。
    /// 変更前の並びで今の曲（すでに消えていた場合は前回の引き継ぎ先）より後ろにあり、今も残っている最初の曲。
    /// </summary>
    private PlaylistItem? FindSuccessor()
    {
        if (successor is not null && playlist.IndexOf(successor) >= 0)
        {
            return successor;
        }

        int start = Array.IndexOf(lastOrder, successor ?? Current);
        if (start < 0)
        {
            return null;
        }

        var remaining = new HashSet<PlaylistItem>(playlist.Items);
        for (int i = start + 1; i < lastOrder.Length; i++)
        {
            if (remaining.Contains(lastOrder[i]))
            {
                return lastOrder[i];
            }
        }
        return null;
    }

    /// <summary>曲の終わりで自然に続く曲を決め、再生サービスに予約する。</summary>
    private void PlanNext()
    {
        // 乗り換えの通知がまだ届いていないと Current が古い。先に追いつかせてから決める
        // （追いついた場合は、その通知の中でこのメソッドがもう一度呼ばれて予約し直される）。
        playback.CatchUp();
        plannedNext = playback.CurrentPath is null ? null : NaturalNext();
        playback.SetNext(plannedNext?.Path);
    }

    private PlaylistItem? NaturalNext() => queuedNext ?? mode switch
    {
        PlayMode.OneShot => null,
        PlayMode.RepeatOne => Current is not null && playlist.IndexOf(Current) >= 0 ? Current : Following(Current, wrap: false),
        PlayMode.RepeatAll => Following(Current, wrap: true),
        PlayMode.Random => PickRandom(),
        _ => Following(Current, wrap: false),
    };

    /// <summary>並び順で次の曲。今の曲がプレイリストから消えていれば、その位置を引き継いだ曲。</summary>
    private PlaylistItem? Following(PlaylistItem? item, bool wrap)
    {
        if (playlist.Count == 0)
        {
            return null;
        }
        if (item is null)
        {
            return playlist.Items[0];
        }

        int index = playlist.IndexOf(item);
        if (index < 0)
        {
            return successor ?? (wrap ? playlist.Items[0] : null);
        }
        if (index + 1 < playlist.Count)
        {
            return playlist.Items[index + 1];
        }
        return wrap ? playlist.Items[0] : null;
    }

    private PlaylistItem? Preceding(PlaylistItem? item)
    {
        if (playlist.Count == 0 || item is null)
        {
            return null;
        }

        int index = playlist.IndexOf(item);
        if (index < 0)
        {
            // 今の曲がプレイリストから消えている。その位置を引き継いだ曲の 1 つ前（引き継ぎ先がなければ末尾）。
            index = successor is null ? playlist.Count : playlist.IndexOf(successor);
        }

        if (index > 0)
        {
            return playlist.Items[index - 1];
        }
        return mode == PlayMode.RepeatAll && playlist.Count > 1 ? playlist.Items[^1] : null;
    }

    /// <summary>今の曲以外から無作為に選ぶ。1 曲しかなければその曲。</summary>
    private PlaylistItem? PickRandom()
    {
        if (playlist.Count == 0)
        {
            return null;
        }

        int currentIndex = Current is null ? -1 : playlist.IndexOf(Current);
        if (currentIndex < 0 || playlist.Count == 1)
        {
            return playlist.Items[random.Next(playlist.Count)];
        }

        int pick = random.Next(playlist.Count - 1);
        return playlist.Items[pick >= currentIndex ? pick + 1 : pick];
    }

    /// <summary>
    /// 開けない曲を飛ばしながら再生を試みる。どれも開けなければ止まったままにする。
    /// 出力を開始できない場合（デバイスがないなど）は曲のせいではないので、次の曲は試さない。
    /// </summary>
    private void PlaySkippingUnopenable(PlaylistItem? item)
    {
        for (int attempts = 0; item is not null && attempts < playlist.Count; attempts++)
        {
            if (TryPlay(item) != PlayResult.OpenFailed)
            {
                return;
            }

            // TryPlay は失敗した曲を今の曲にしているので、そこから先へ進む。
            item = mode == PlayMode.Random ? PickRandom() : Following(item, wrap: mode == PlayMode.RepeatAll);
        }
    }

    private PlayResult TryPlay(PlaylistItem item, bool recordHistory = true)
    {
        SetCurrent(item, recordHistory);
        try
        {
            playback.Open(item.Path);
        }
        catch (Exception ex)
        {
            plannedNext = null;
            TrackFailed?.Invoke(this, new TrackFailedEventArgs(item, ex));
            return PlayResult.OpenFailed;
        }

        return StartPlayback(item) ? PlayResult.Started : PlayResult.OutputFailed;
    }

    /// <summary>開いてある曲の再生を始める。始められなければ <see cref="TrackFailed"/> を通知する。</summary>
    private bool StartPlayback(PlaylistItem? item)
    {
        try
        {
            playback.Play();
        }
        catch (Exception ex)
        {
            if (item is not null)
            {
                TrackFailed?.Invoke(this, new TrackFailedEventArgs(item, ex));
            }
            return false;
        }

        PlanNext();
        return true;
    }

    private void SetCurrent(PlaylistItem? item, bool recordHistory)
    {
        if (Current is not null)
        {
            Current.IsCurrent = false;
        }

        Current = item;
        successor = null;
        queuedNext = null;
        if (item is not null)
        {
            item.IsCurrent = playlist.IndexOf(item) >= 0;
            if (recordHistory && (historyIndex < 0 || !ReferenceEquals(history[historyIndex], item)))
            {
                // 「前へ」で戻った位置から別の曲へ進んだら、そこから先の履歴は捨てて積み直す。
                history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                history.Add(item);
                if (history.Count > MaxHistory)
                {
                    history.RemoveAt(0);
                }
                historyIndex = history.Count - 1;
            }
        }
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class TrackFailedEventArgs(PlaylistItem item, Exception exception) : EventArgs
{
    public PlaylistItem Item { get; } = item;

    public Exception Exception { get; } = exception;
}
