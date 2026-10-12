using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SoundPlayerChiyomaru;

/// <summary>
/// 中に置いた 1 行の文字を、幅に収まらないときだけ横に流して全体を読めるようにする。
/// 先頭でしばらく止まり、末尾まで流し、末尾でしばらく止まってから先頭に戻る。収まっている間は何もしない。
/// </summary>
public sealed class MarqueeText : Decorator
{
    /// <summary>流す速さ（1 秒あたりのピクセル数。画面の拡大率を掛ける前）。</summary>
    private const double Speed = 30;

    /// <summary>描き直す間隔。短いほど滑らかだが、流れている間の CPU 使用率が上がる。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    /// <summary>先頭と末尾で止まっている長さ（更新の回数）。</summary>
    private const int HoldTicks = 50;

    // 流すときは、中身の描画位置だけをずらす。配置をやり直すと、そのたびに周りの部品の配置の計算まで走る。
    private readonly TranslateTransform shift = new();
    private readonly DispatcherTimer timer;
    private double offset;
    private double overflow;
    private int hold = HoldTicks;
    private bool attached;

    public MarqueeText()
    {
        ClipToBounds = true;
        timer = new DispatcherTimer { Interval = Interval };
        timer.Tick += (_, _) => Advance();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
        {
            if (change.OldValue is TextBlock oldText)
            {
                oldText.PropertyChanged -= OnChildPropertyChanged;
            }
            if (change.NewValue is TextBlock newText)
            {
                newText.PropertyChanged += OnChildPropertyChanged;
            }
        }
    }

    /// <summary>
    /// 文字が変わったら、先頭から読ませ直す。幅の変化では判定しない（連番だけが違う題名のように、
    /// 別の文字でも同じ幅になることがある）。
    /// </summary>
    private void OnChildPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
        {
            hold = HoldTicks;
            MoveTo(0);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        // 中身は切り詰めずに、必要な幅をそのまま測る。
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        Size desired = child.DesiredSize;
        return new Size(Math.Min(desired.Width, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child)
        {
            return finalSize;
        }

        double width = child.DesiredSize.Width;
        overflow = Math.Max(0, width - finalSize.Width);
        if (offset > overflow)
        {
            MoveTo(overflow);
        }
        child.RenderTransform ??= shift;
        child.Arrange(new Rect(0, 0, Math.Max(width, finalSize.Width), finalSize.Height));

        // 収まっている間は時計を止めておく（何も描き直さない）。
        timer.IsEnabled = overflow > 0.5 && attached;
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        InvalidateArrange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        timer.Stop();
    }

    private void Advance()
    {
        if (!IsEffectivelyVisible)
        {
            // 見えなくなった。次に配置されたとき（見えるようになったとき）に、必要ならまた動かす。
            timer.Stop();
            return;
        }
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized })
        {
            return;
        }

        if (hold > 0)
        {
            hold--;
            if (hold == 0 && offset >= overflow)
            {
                // 末尾で止まり終えた。先頭に戻して、また止まるところから始める。
                hold = HoldTicks;
                MoveTo(0);
            }
            return;
        }

        MoveTo(Math.Min(overflow, offset + (Speed * Interval.TotalSeconds)));
        if (offset >= overflow)
        {
            hold = HoldTicks;
        }
    }

    private void MoveTo(double value)
    {
        offset = value;
        shift.X = -value;
    }
}
