using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using SoundPlayerChiyomaru.Core.Settings;

namespace SoundPlayerChiyomaru;

/// <summary>
/// 画面の配色を切り替える。このアプリ独自の配色（Chiyomaru。アイコンの濃紺・緑・黄を基調にしたもの）と、
/// Windows の色に従う配色を選べる。どちらも、明るい・暗い・Windows の設定に従う、の 3 通りがある。
/// </summary>
static class ThemeManager
{
    // 独自の配色。緑が主の色（操作できるもの、選択、再生ボタン）、黄が副の色（今の曲、注意の印）。
    private static readonly Color DarkAccent = Color.Parse("#4FDD68");
    private static readonly Color DarkOnAccent = Color.Parse("#0B0920");
    private static readonly Color DarkSecondary = Color.Parse("#FAE500");
    private static readonly Color LightAccent = Color.Parse("#1A8F3F");
    private static readonly Color LightOnAccent = Colors.White;
    private static readonly Color LightSecondary = Color.Parse("#F2C200");
    private static readonly Color OnSecondary = Color.Parse("#1A1500");

    /// <summary>明るい背景の上で、黄は文字の色としては読めない。今の曲の文字には、同じ色相の暗い色を使う。</summary>
    private static readonly Color LightCurrentText = Color.Parse("#7A5C00");

    // Windows の配色のときに使う、印の色。主の色は Windows のアクセントカラー（取れなかったときの代わりがこの緑）。
    // Windows の配色には副の色に当たるものがないので、注意の印には黄を使う。
    private static readonly Color PlainBadge = Color.Parse("#2E7D32");
    private static readonly Color PlainWarning = Color.Parse("#E0B000");

    /// <summary>独自の配色のときだけ上書きする、テーマ側の色の名前（押せるものの上の文字の色）。</summary>
    private static readonly string[] OnAccentKeys =
    [
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
        "ToggleButtonForegroundChecked",
        "ToggleButtonForegroundCheckedPointerOver",
        "ToggleButtonForegroundCheckedPressed",
    ];

    /// <summary>
    /// 独自の配色のときだけ上書きする、一覧の選択行の背景の色の名前と濃さ。主の色をそのまま塗ると、
    /// 今の曲の文字（副の色）が読みにくくなるので、薄く重ねる。
    /// </summary>
    private static readonly (string Key, double Opacity)[] SelectionKeys =
    [
        ("SystemControlHighlightListAccentLowBrush", 0.26),
        ("SystemControlHighlightListAccentMediumBrush", 0.34),
        ("SystemControlHighlightListAccentHighBrush", 0.42),
    ];

    private static AppTheme current;
    private static bool subscribed;
    private static bool applied;

    public static void Apply(Application app, AppTheme theme)
    {
        current = theme;
        if (!subscribed)
        {
            // 「Windows の設定に従う」を選んでいる間に Windows の明るい・暗いが切り替わったら、印などの色も合わせ直す。
            subscribed = true;
            app.ActualThemeVariantChanged += (_, _) => ApplyColors(app);
        }

        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            if (applied)
            {
                // 実行中の切り替えでは、テーマごと作り直して入れ替える。使用中のテーマから配色を外しても、
                // 適用済みの色が画面に残った（独自の暗い配色から Windows の暗い配色へ切り替えたときなど）。
                var fresh = new FluentTheme();
                SetPalettes(fresh, theme);
                app.Styles[app.Styles.IndexOf(fluent)] = fresh;
            }
            else
            {
                SetPalettes(fluent, theme);
            }
        }
        applied = true;

        app.RequestedThemeVariant = theme switch
        {
            AppTheme.ChiyomaruLight or AppTheme.WindowsLight => ThemeVariant.Light,
            AppTheme.ChiyomaruDark or AppTheme.WindowsDark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        ApplyColors(app);
    }

    private static void SetPalettes(FluentTheme fluent, AppTheme theme)
    {
        if (IsChiyomaru(theme))
        {
            fluent.Palettes[ThemeVariant.Light] = LightPalette();
            fluent.Palettes[ThemeVariant.Dark] = DarkPalette();
        }
    }

    private static bool IsChiyomaru(AppTheme theme) =>
        theme is AppTheme.ChiyomaruSystem or AppTheme.ChiyomaruLight or AppTheme.ChiyomaruDark;

    /// <summary>テーマには無い、このアプリの部品の色（印、今の曲）と、テーマの色のうち上書きするものを設定する。</summary>
    private static void ApplyColors(Application app)
    {
        bool dark = app.ActualThemeVariant == ThemeVariant.Dark;
        if (!IsChiyomaru(current))
        {
            // ビットパーフェクトの印は、どの配色でも主の色にそろえる。Windows の配色では、ボタンなどと同じアクセントカラー。
            Color windowsAccent = app.TryGetResource("SystemAccentColor", app.ActualThemeVariant, out object? value) && value is Color color
                ? color
                : PlainBadge;
            app.Resources["ChiyomaruBadgeBrush"] = new SolidColorBrush(windowsAccent);
            app.Resources["ChiyomaruOnBadgeBrush"] = new SolidColorBrush(ReadableOn(windowsAccent));
            app.Resources["ChiyomaruSecondaryBrush"] = new SolidColorBrush(PlainWarning);
            app.Resources["ChiyomaruOnSecondaryBrush"] = new SolidColorBrush(Colors.Black);

            // 今の曲は、色を変えずに太字だけにする。名前ごと外すと、切り替える前の色が画面に残るので、普通の文字の色を入れておく。
            app.Resources["ChiyomaruCurrentTextBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            foreach (string key in OnAccentKeys)
            {
                app.Resources.Remove(key);
            }
            foreach ((string key, _) in SelectionKeys)
            {
                app.Resources.Remove(key);
            }
            return;
        }

        Color accent = dark ? DarkAccent : LightAccent;
        var onAccent = new SolidColorBrush(dark ? DarkOnAccent : LightOnAccent);
        app.Resources["ChiyomaruBadgeBrush"] = new SolidColorBrush(accent);
        app.Resources["ChiyomaruOnBadgeBrush"] = onAccent;
        app.Resources["ChiyomaruSecondaryBrush"] = new SolidColorBrush(dark ? DarkSecondary : LightSecondary);
        app.Resources["ChiyomaruOnSecondaryBrush"] = new SolidColorBrush(OnSecondary);
        app.Resources["ChiyomaruCurrentTextBrush"] = new SolidColorBrush(dark ? DarkSecondary : LightCurrentText);
        foreach (string key in OnAccentKeys)
        {
            app.Resources[key] = onAccent;
        }
        foreach ((string key, double opacity) in SelectionKeys)
        {
            app.Resources[key] = new SolidColorBrush(accent, opacity);
        }
    }

    /// <summary>その色の上に載せて読める文字の色（明るい色の上なら黒、暗い色の上なら白）。</summary>
    private static Color ReadableOn(Color background)
    {
        double luminance = ((0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B)) / 255;
        return luminance > 0.6 ? Colors.Black : Colors.White;
    }

    /// <summary>暗い配色。濃紺の地に、緑と黄。</summary>
    private static ColorPaletteResources DarkPalette() => new()
    {
        Accent = DarkAccent,
        RegionColor = Color.Parse("#100D2E"),
        AltHigh = Color.Parse("#100D2E"),
        AltLow = Color.Parse("#100D2E"),
        AltMedium = Color.Parse("#100D2E"),
        AltMediumHigh = Color.Parse("#100D2E"),
        AltMediumLow = Color.Parse("#100D2E"),
        BaseHigh = Color.Parse("#F2F3F8"),
        BaseLow = Color.Parse("#2B2859"),
        BaseMedium = Color.Parse("#A9A7CB"),
        BaseMediumHigh = Color.Parse("#CBCAE2"),
        BaseMediumLow = Color.Parse("#5A5786"),
        ChromeAltLow = Color.Parse("#CBCAE2"),
        ChromeBlackHigh = Colors.Black,
        ChromeBlackLow = Color.Parse("#CBCAE2"),
        ChromeBlackMedium = Colors.Black,
        ChromeBlackMediumLow = Colors.Black,
        ChromeDisabledHigh = Color.Parse("#2B2859"),
        ChromeDisabledLow = Color.Parse("#A9A7CB"),
        ChromeGray = Color.Parse("#8482AA"),
        ChromeHigh = Color.Parse("#8482AA"),
        ChromeLow = Color.Parse("#171442"),
        ChromeMedium = Color.Parse("#1D1A4B"),
        ChromeMediumLow = Color.Parse("#1A1741"),
        ChromeWhite = Colors.White,
        ListLow = Color.Parse("#1D1A4B"),
        ListMedium = Color.Parse("#2B2859"),
    };

    /// <summary>明るい配色。明るい灰色の地に、緑と黄。緑は、白い文字が読めるところまで暗くしている。</summary>
    private static ColorPaletteResources LightPalette() => new()
    {
        Accent = LightAccent,
        RegionColor = Color.Parse("#ECEEF2"),
        AltHigh = Color.Parse("#ECEEF2"),
        AltLow = Color.Parse("#ECEEF2"),
        AltMedium = Color.Parse("#ECEEF2"),
        AltMediumHigh = Color.Parse("#ECEEF2"),
        AltMediumLow = Color.Parse("#ECEEF2"),
        BaseHigh = Color.Parse("#14122B"),
        BaseLow = Color.Parse("#C9CDD6"),
        BaseMedium = Color.Parse("#6A6D7C"),
        BaseMediumHigh = Color.Parse("#454757"),
        BaseMediumLow = Color.Parse("#8F93A1"),
        ChromeAltLow = Color.Parse("#454757"),
        ChromeBlackHigh = Colors.Black,
        ChromeBlackLow = Color.Parse("#C9CDD6"),
        ChromeBlackMedium = Color.Parse("#454757"),
        ChromeBlackMediumLow = Color.Parse("#6A6D7C"),
        ChromeDisabledHigh = Color.Parse("#C9CDD6"),
        ChromeDisabledLow = Color.Parse("#8F93A1"),
        ChromeGray = Color.Parse("#7C8090"),
        ChromeHigh = Color.Parse("#C9CDD6"),
        ChromeLow = Color.Parse("#E3E6EB"),
        ChromeMedium = Color.Parse("#DDE0E6"),
        ChromeMediumLow = Color.Parse("#F7F8FA"),
        ChromeWhite = Colors.White,
        ListLow = Color.Parse("#DDE0E6"),
        ListMedium = Color.Parse("#C9CDD6"),
    };
}
