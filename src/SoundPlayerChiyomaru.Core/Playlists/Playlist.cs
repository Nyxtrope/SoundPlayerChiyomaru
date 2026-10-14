using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

namespace SoundPlayerChiyomaru.Core.Playlists;

public enum PlaylistSortKey
{
    FileName,
    Title,
    Artist,
    Album,
    Path,

    /// <summary>曲順の番号（<see cref="PlaylistItem.Order"/>）。シャッフルや並べ替えの前の順に戻すために使う。</summary>
    Order,
}

/// <summary>選んだ曲をまとめて動かす先。</summary>
public enum PlaylistMove
{
    /// <summary>選んだ曲のうち、いちばん上の曲の 1 つ上。</summary>
    Up,

    /// <summary>選んだ曲のうち、いちばん下の曲の 1 つ下。</summary>
    Down,

    /// <summary>再生中の曲のすぐ下。選んだ曲に再生中の曲が含まれていれば、それは動かさない。</summary>
    AfterCurrent,

    Top,

    Bottom,
}

/// <summary>
/// 作業中のプレイリスト。編集はすべて元に戻せる。1 つのスレッド（UI スレッド）から使うこと。
/// </summary>
public sealed class Playlist
{
    private const int MaxUndo = 50;

    /// <summary>元に戻せる置き換え（<see cref="Open"/>）の数。</summary>
    private const int MaxReplaceUndo = 3;

    private readonly ItemCollection items = new();
    private readonly List<Snapshot> undoStack = [];
    private readonly List<Snapshot> redoStack = [];

    /// <summary>
    /// 元に戻すための、ある時点の並びと曲順の番号。番号は項目が持っているので、並びとは別に控える。
    /// 置き換えの前後では、上書き保存の書き戻し先（<see cref="FilePath"/>）も控える（<paramref name="HasFilePath"/>）。
    /// それ以外の編集では控えない。控えると、名前を付けて保存した後に編集を戻したとき、書き戻し先まで戻ってしまう。
    /// </summary>
    private readonly record struct Snapshot(PlaylistItem[] Items, int[] Orders, bool HasFilePath, string? FilePath);

    /// <summary>内容が変わった（編集、元に戻す、読み込み）。</summary>
    public event EventHandler? Changed;

    /// <summary>画面にそのままバインドできる（変更通知つき）。</summary>
    public IReadOnlyList<PlaylistItem> Items => items;

    public int Count => items.Count;

    /// <summary>長さが分かっている曲の合計。タグをまだ読んでいない曲と、長さを読めない曲は数えない。</summary>
    public TimeSpan GetTotalDuration()
    {
        long ticks = 0;
        foreach (PlaylistItem item in items)
        {
            ticks += item.Duration?.Ticks ?? 0;
        }
        return new TimeSpan(ticks);
    }

    /// <summary>読み込み元、または最後に保存したプレイリストファイル。「上書き保存」の書き戻し先。</summary>
    public string? FilePath { get; set; }

    public bool CanUndo => undoStack.Count > 0;

    public bool CanRedo => redoStack.Count > 0;

    public int IndexOf(PlaylistItem item) => items.IndexOf(item);

    public IReadOnlyList<PlaylistItem> Add(IEnumerable<string> paths) => Insert(items.Count, paths);

    /// <param name="index">挿入位置。範囲外は先頭・末尾に丸める。</param>
    /// <returns>追加された項目。</returns>
    public IReadOnlyList<PlaylistItem> Insert(int index, IEnumerable<string> paths)
    {
        PlaylistItem[] added = paths.Select(path => new PlaylistItem(path)).ToArray();
        if (added.Length == 0)
        {
            return added;
        }

        index = Math.Clamp(index, 0, items.Count);
        PushUndo();

        // 曲順の番号は、どこに挿入しても今の最大の続きから振る。
        int order = MaxOrder(items);
        foreach (PlaylistItem item in added)
        {
            item.Order = ++order;
        }
        items.InsertRange(index, added);
        OnChanged();
        return added;
    }

    public void Remove(IReadOnlyCollection<PlaylistItem> targets)
    {
        var set = new HashSet<PlaylistItem>(targets);
        PlaylistItem[] remaining = items.Where(item => !set.Contains(item)).ToArray();
        Replace(remaining);
    }

    /// <summary>項目を、元の並び順を保ったまままとめて移動する。</summary>
    /// <param name="insertIndex">移動前の並びで数えた挿入位置（その位置にある項目の直前に入る）。</param>
    public void Move(IReadOnlyCollection<PlaylistItem> targets, int insertIndex) =>
        Replace(Arrange(new HashSet<PlaylistItem>(targets), insertIndex));

    /// <summary>
    /// 項目を、元の並び順を保ったまままとめて、<paramref name="move"/> の示す先へ移動する。
    /// 離れた項目を選んでいたときも、移動先でひとかたまりになる。
    /// </summary>
    /// <param name="current">再生中の曲（<see cref="PlaylistMove.AfterCurrent"/> で使う）。</param>
    /// <returns>並びが変わったか。</returns>
    public bool Move(IReadOnlyCollection<PlaylistItem> targets, PlaylistMove move, PlaylistItem? current)
    {
        if (Plan(targets, move, current) is not { } arranged)
        {
            return false;
        }
        Replace(arranged);
        return true;
    }

    /// <summary><see cref="Move(IReadOnlyCollection{PlaylistItem}, PlaylistMove, PlaylistItem?)"/> で並びが変わるか。</summary>
    public bool CanMove(IReadOnlyCollection<PlaylistItem> targets, PlaylistMove move, PlaylistItem? current) =>
        Plan(targets, move, current) is not null;

    /// <summary>移動した後の並び。並びが変わらないとき（動かす先がないときを含む）は null。</summary>
    private PlaylistItem[]? Plan(IReadOnlyCollection<PlaylistItem> targets, PlaylistMove move, PlaylistItem? current)
    {
        var set = new HashSet<PlaylistItem>(targets);
        if (move == PlaylistMove.AfterCurrent && current is not null)
        {
            set.Remove(current);
        }

        int first = -1;
        int last = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (set.Contains(items[i]))
            {
                first = first < 0 ? i : first;
                last = i;
            }
        }
        if (first < 0)
        {
            return null;
        }

        int insertIndex;
        switch (move)
        {
            case PlaylistMove.Up:
                insertIndex = first - 1;
                break;
            case PlaylistMove.Down:
                insertIndex = last + 2;
                break;
            case PlaylistMove.AfterCurrent:
                int currentIndex = current is null ? -1 : items.IndexOf(current);
                if (currentIndex < 0)
                {
                    return null;
                }
                insertIndex = currentIndex + 1;
                break;
            case PlaylistMove.Top:
                insertIndex = 0;
                break;
            default:
                insertIndex = items.Count;
                break;
        }

        PlaylistItem[] arranged = Arrange(set, insertIndex);
        return items.SequenceEqual(arranged) ? null : arranged;
    }

    private PlaylistItem[] Arrange(HashSet<PlaylistItem> set, int insertIndex)
    {
        insertIndex = Math.Clamp(insertIndex, 0, items.Count);

        var moved = new List<PlaylistItem>();
        var before = new List<PlaylistItem>();
        var after = new List<PlaylistItem>();
        for (int i = 0; i < items.Count; i++)
        {
            PlaylistItem item = items[i];
            if (set.Contains(item))
            {
                moved.Add(item);
            }
            else if (i < insertIndex)
            {
                before.Add(item);
            }
            else
            {
                after.Add(item);
            }
        }

        return [.. before, .. moved, .. after];
    }

    /// <summary>安定ソート。タグが未読み込みの項目はファイル名で比べる。</summary>
    public void Sort(PlaylistSortKey key)
    {
        // 「2.mp3」が「10.mp3」より前に来るよう、数字は数値として比べる。
        var comparer = StringComparer.Create(
            CultureInfo.CurrentCulture,
            CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

        IOrderedEnumerable<PlaylistItem> sorted = key switch
        {
            PlaylistSortKey.Title => items.OrderBy(item => item.DisplayTitle, comparer),
            PlaylistSortKey.Artist => items
                .OrderBy(item => item.Artist ?? "", comparer)
                .ThenBy(item => item.Album ?? "", comparer)
                .ThenBy(item => item.TrackNumber),
            PlaylistSortKey.Album => items
                .OrderBy(item => item.Album ?? "", comparer)
                .ThenBy(item => item.TrackNumber),
            PlaylistSortKey.Path => items.OrderBy(item => item.Path, comparer),
            PlaylistSortKey.Order => items.OrderBy(item => item.Order),
            _ => items.OrderBy(item => item.FileName, comparer),
        };

        Replace(sorted.ToArray());
    }

    public void Clear() => Replace([]);

    /// <summary>
    /// 並びを無作為に入れ替える（再生モードのランダムとは別で、プレイリストの並びそのものを変える）。
    /// 曲順の番号は変えないので、後で番号順に並べ替えれば元に戻せる。
    /// </summary>
    /// <param name="first">先頭に置く曲（再生中の曲）。プレイリストになければ無視する。</param>
    public void Shuffle(Random random, PlaylistItem? first)
    {
        PlaylistItem[] shuffled = items.ToArray();
        random.Shuffle(shuffled);
        int index = first is null ? -1 : Array.IndexOf(shuffled, first);
        if (index > 0)
        {
            (shuffled[0], shuffled[index]) = (shuffled[index], shuffled[0]);
        }
        Replace(shuffled);
    }

    /// <summary>今の並びの順に、曲順の番号を 1 から振り直す。</summary>
    public void Renumber()
    {
        bool sequential = true;
        for (int i = 0; i < items.Count && sequential; i++)
        {
            sequential = items[i].Order == i + 1;
        }
        if (sequential)
        {
            return;
        }

        PushUndo();
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Order = i + 1;
        }
        OnChanged();
    }

    /// <summary>
    /// 別のプレイリストとして読み込み直す。編集履歴は引き継がない。
    /// </summary>
    public void Load(IEnumerable<string> paths, string? filePath) =>
        Load(paths.Select(path => new PlaylistEntry(path)), filePath);

    /// <summary>
    /// 別のプレイリストとして読み込み直す。曲順の番号は、ファイルに書かれていればそれを使い、
    /// 書かれていない曲には、書かれていた最大の番号の続きを並びの順に振る（どの曲にも書かれていなければ 1 から）。
    /// </summary>
    public void Load(IEnumerable<PlaylistEntry> entries, string? filePath)
    {
        undoStack.Clear();
        redoStack.Clear();
        FilePath = filePath;
        items.ReplaceAll(CreateItems(entries));
        OnChanged();
    }

    /// <summary>
    /// 別のプレイリストに置き換える。<see cref="Load(IEnumerable{PlaylistEntry}, string?)"/> と違い、1 回の編集として元に戻せる
    /// （戻すと、<see cref="FilePath"/> も置き換える前のものに戻る）。曲順の番号の振り方は Load と同じ。
    /// </summary>
    public void Open(IEnumerable<PlaylistEntry> entries, string? filePath)
    {
        PlaylistItem[] newItems = CreateItems(entries);
        PushUndo(withFilePath: true);
        FilePath = filePath;
        items.ReplaceAll(newItems);
        OnChanged();
    }

    private static PlaylistItem[] CreateItems(IEnumerable<PlaylistEntry> entries)
    {
        PlaylistEntry[] loaded = entries.ToArray();
        var newItems = new PlaylistItem[loaded.Length];
        int order = 0;
        for (int i = 0; i < loaded.Length; i++)
        {
            newItems[i] = new PlaylistItem(loaded[i].Path) { Order = loaded[i].Order is > 0 and int known ? known : 0 };
            order = Math.Max(order, newItems[i].Order);
        }
        foreach (PlaylistItem item in newItems)
        {
            if (item.Order == 0)
            {
                // 番号が上限まで使われている壊れたファイルでも、あふれさせない。
                item.Order = order < int.MaxValue ? ++order : order;
            }
        }
        return newItems;
    }

    public void Undo() => Restore(undoStack, redoStack);

    public void Redo() => Restore(redoStack, undoStack);

    private void Restore(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        Snapshot snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(Capture(snapshot.HasFilePath));
        for (int i = 0; i < snapshot.Items.Length; i++)
        {
            snapshot.Items[i].Order = snapshot.Orders[i];
        }
        if (snapshot.HasFilePath)
        {
            FilePath = snapshot.FilePath;
        }
        items.ReplaceAll(snapshot.Items);
        OnChanged();
    }

    private Snapshot Capture(bool withFilePath)
    {
        PlaylistItem[] current = items.ToArray();
        return new Snapshot(current, Array.ConvertAll(current, item => item.Order), withFilePath, withFilePath ? FilePath : null);
    }

    private static int MaxOrder(IEnumerable<PlaylistItem> source)
    {
        int max = 0;
        foreach (PlaylistItem item in source)
        {
            max = Math.Max(max, item.Order);
        }

        // 上限まで使われていても、続きの番号をあふれさせない。
        return Math.Min(max, int.MaxValue - 1_000_000);
    }

    private void Replace(PlaylistItem[] newItems)
    {
        if (items.SequenceEqual(newItems))
        {
            return;
        }

        PushUndo();
        items.ReplaceAll(newItems);
        OnChanged();
    }

    private void PushUndo(bool withFilePath = false)
    {
        undoStack.Add(Capture(withFilePath));
        if (undoStack.Count > MaxUndo)
        {
            undoStack.RemoveAt(0);
        }

        // 置き換えの履歴は、置き換える前の曲を丸ごと持ち続ける。大きなプレイリストを何度も開いてもメモリを使いすぎないよう、
        // 古い置き換えは、それより前の履歴ごと捨てる（途中だけを捨てると、戻したときに並びが飛ぶ）。
        while (undoStack.Count(snapshot => snapshot.HasFilePath) > MaxReplaceUndo)
        {
            undoStack.RemoveRange(0, undoStack.FindIndex(snapshot => snapshot.HasFilePath) + 1);
        }
        redoStack.Clear();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>まとめての挿入・入れ替えを 1 回の通知で済ませるコレクション。</summary>
    private sealed class ItemCollection : ObservableCollection<PlaylistItem>
    {
        public void InsertRange(int index, PlaylistItem[] added)
        {
            for (int i = 0; i < added.Length; i++)
            {
                Items.Insert(index + i, added[i]);
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, index));
        }

        public void ReplaceAll(PlaylistItem[] newItems)
        {
            Items.Clear();
            foreach (PlaylistItem item in newItems)
            {
                Items.Add(item);
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
