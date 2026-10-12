using SoundPlayerChiyomaru.Core.Settings;
using Xunit;

namespace SoundPlayerChiyomaru.Core.Tests;

public class WindowPlacementTests
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040, 1.0);
    private static readonly ScreenArea Left = new(-2560, 0, 2560, 1400, 1.5);

    [Fact]
    public void 画面の中にあればそのまま()
    {
        var saved = new WindowBounds(300, 200, 600, 480, Maximized: true);

        Assert.Equal(saved, WindowPlacement.Fit(saved, [Primary, Left]));
    }

    [Fact]
    public void 端に付けたときの見えない枠の分や_下や横へのはみ出しは直さない()
    {
        var snapped = new WindowBounds(-7, 0, 960, 1040, false);
        var hangingRight = new WindowBounds(1700, 900, 600, 480, false);

        Assert.Equal(snapped, WindowPlacement.Fit(snapped, [Primary]));
        Assert.Equal(hangingRight, WindowPlacement.Fit(hangingRight, [Primary]));
    }

    [Fact]
    public void 別のモニターにあった位置は_そのモニターがあれば保つ()
    {
        var saved = new WindowBounds(-2000, 300, 600, 480, false);

        Assert.Equal(saved, WindowPlacement.Fit(saved, [Primary, Left]));
    }

    [Fact]
    public void モニターがなくなっていたら主のモニターに収める()
    {
        var saved = new WindowBounds(-2000, 300, 600, 480, false);

        Assert.Equal(new WindowBounds(0, 300, 600, 480, false), WindowPlacement.Fit(saved, [Primary]));
    }

    [Theory]
    [InlineData(1900, 200, 1320, 200)]
    [InlineData(-560, 200, 0, 200)]
    [InlineData(300, 1020, 300, 560)]
    [InlineData(300, -100, 300, 0)]
    public void つかめる部分が残っていなければ画面の中へ動かす(int x, int y, int expectedX, int expectedY)
    {
        var saved = new WindowBounds(x, y, 600, 480, false);

        Assert.Equal(new WindowBounds(expectedX, expectedY, 600, 480, false), WindowPlacement.Fit(saved, [Primary]));
    }

    [Fact]
    public void 画面より大きければ画面の大きさまで縮める_拡大率を考慮する()
    {
        var saved = new WindowBounds(-2560, 0, 3000, 2000, false);

        WindowBounds? fitted = WindowPlacement.Fit(saved, [Primary, Left]);

        Assert.NotNull(fitted);
        Assert.Equal(2560 / 1.5, fitted.Value.Width, 3);
        Assert.Equal(1400 / 1.5, fitted.Value.Height, 3);
        Assert.Equal((-2560, 0), (fitted.Value.X, fitted.Value.Y));
    }

    [Fact]
    public void 画面より大きいウィンドウは_隣のモニターにはみ出していても元のモニターに収める()
    {
        var right = new ScreenArea(1920, 0, 3840, 2100, 1.0);
        var saved = new WindowBounds(100, 100, 9000, 9000, false);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040, false), WindowPlacement.Fit(saved, [Primary, right]));
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(600, -1)]
    [InlineData(double.NaN, 480)]
    [InlineData(600, double.PositiveInfinity)]
    public void 大きさが壊れていれば使わない(double width, double height)
    {
        Assert.Null(WindowPlacement.Fit(new WindowBounds(0, 0, width, height, false), [Primary]));
    }

    [Fact]
    public void モニターがない_または使えない値なら使わない()
    {
        var saved = new WindowBounds(0, 0, 600, 480, false);

        Assert.Null(WindowPlacement.Fit(saved, []));
        Assert.Null(WindowPlacement.Fit(saved, [new ScreenArea(0, 0, 0, 1040, 1.0), new ScreenArea(0, 0, 1920, 1040, 0)]));
    }

    [Fact]
    public void 極端な位置や大きさでも例外にならない()
    {
        var saved = new WindowBounds(int.MaxValue, int.MinValue, 1e300, 1e300, false);

        Assert.Equal(new WindowBounds(0, 0, 1920, 1040, false), WindowPlacement.Fit(saved, [Primary]));
    }
}
