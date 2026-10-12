namespace SoundPlayerChiyomaru.Core.Settings;

/// <summary>
/// 保存しておくウィンドウの位置と大きさ。位置は画面のピクセル、大きさは拡大率を掛ける前の値（枠を含まない中身の大きさ）。
/// 最大化している間も、元に戻したときの位置と大きさを入れる。
/// </summary>
public readonly record struct WindowBounds(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>モニターの、タスクバーを除いた領域（ピクセル）と拡大率。</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling);

/// <summary>保存してあったウィンドウの位置を、今のモニターの構成で使えるものに直す。</summary>
public static class WindowPlacement
{
    /// <summary>横にこれだけ（ピクセル）見えていれば、つかんで動かせるとみなす。</summary>
    private const int MinVisibleWidth = 120;

    /// <summary>上端が、領域の下端からこれだけ（ピクセル）上にあれば、タイトルバーをつかめるとみなす。</summary>
    private const int MinVisibleHeight = 48;

    /// <summary>上端が領域の上にこれだけ（ピクセル）出ていてもよい。端に付けたウィンドウは、見えない枠の分だけ外に出ている。</summary>
    private const int TopSlack = 16;

    /// <summary>
    /// 保存してあった位置でつかんで動かせるならそのまま返し、そうでなければ（モニターを外した、解像度を下げたなど）
    /// 画面に収まる位置に直して返す。モニターより大きい場合は、モニターの大きさまで縮めて収める。値が壊れている、またはモニターがない場合は null。
    /// </summary>
    /// <param name="screens">先頭を主のモニターにすること。どのモニターにも重ならない場合の置き先になる。</param>
    public static WindowBounds? Fit(WindowBounds saved, IReadOnlyList<ScreenArea> screens)
    {
        if (!double.IsFinite(saved.Width) || !double.IsFinite(saved.Height) || saved.Width <= 0 || saved.Height <= 0)
        {
            return null;
        }

        // ウィンドウのうち、そのモニターに入っている割合がいちばん大きいモニターに置く。
        // 大きさは、そのモニターに置いたときのもの（収まらなければ縮めた後）で比べる。縮める前の大きさや面積そのもので比べると、
        // モニターより大きいウィンドウが、隣のモニターにはみ出した分で隣に取られる。
        ScreenArea? best = null;
        double bestOverlap = 0;
        foreach (ScreenArea screen in screens)
        {
            if (!IsUsable(screen))
            {
                continue;
            }
            best ??= screen;

            (long width, long height) = ToPixels(
                Math.Min(saved.Width, screen.Width / screen.Scaling),
                Math.Min(saved.Height, screen.Height / screen.Scaling),
                screen.Scaling);
            double overlap = (double)Overlap(saved.X, width, screen.X, screen.Width) / width
                * Overlap(saved.Y, height, screen.Y, screen.Height) / height;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = screen;
            }
        }
        if (best is not { } area)
        {
            return null;
        }

        double fittedWidth = Math.Min(saved.Width, area.Width / area.Scaling);
        double fittedHeight = Math.Min(saved.Height, area.Height / area.Scaling);
        (long pixelWidth, long pixelHeight) = ToPixels(fittedWidth, fittedHeight, area.Scaling);
        pixelWidth = Math.Min(pixelWidth, area.Width);
        pixelHeight = Math.Min(pixelHeight, area.Height);

        long bottom = (long)area.Y + area.Height;
        bool shrunk = fittedWidth < saved.Width || fittedHeight < saved.Height;
        bool reachable = bestOverlap > 0
            && !shrunk
            && Overlap(saved.X, pixelWidth, area.X, area.Width) >= Math.Min(MinVisibleWidth, pixelWidth)
            && saved.Y >= (long)area.Y - TopSlack
            && saved.Y <= bottom - MinVisibleHeight;
        if (reachable)
        {
            return saved with { Width = fittedWidth, Height = fittedHeight };
        }

        long x = Math.Clamp(saved.X, area.X, (long)area.X + area.Width - pixelWidth);
        long y = Math.Clamp(saved.Y, area.Y, bottom - pixelHeight);
        return saved with { X = (int)x, Y = (int)y, Width = fittedWidth, Height = fittedHeight };
    }

    private static bool IsUsable(ScreenArea screen) =>
        screen.Width > 0 && screen.Height > 0 && double.IsFinite(screen.Scaling) && screen.Scaling > 0;

    private static (long Width, long Height) ToPixels(double width, double height, double scaling) =>
        ((long)Math.Min(Math.Ceiling(width * scaling), int.MaxValue), (long)Math.Min(Math.Ceiling(height * scaling), int.MaxValue));

    /// <summary>1 つの軸で、2 つの区間が重なっている長さ。重ならなければ 0。</summary>
    private static long Overlap(long start, long length, long otherStart, long otherLength) =>
        Math.Max(0, Math.Min(start + length, otherStart + otherLength) - Math.Max(start, otherStart));
}
