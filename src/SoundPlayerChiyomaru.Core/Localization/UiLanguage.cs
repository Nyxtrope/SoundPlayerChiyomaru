using System.Globalization;
using SoundPlayerChiyomaru.Core.Settings;

namespace SoundPlayerChiyomaru.Core.Localization;

/// <summary>画面に出す言葉の言語。</summary>
public enum DisplayLanguage
{
    Japanese,
    English,
}

/// <summary>
/// 画面に出す言葉の言語を決め、その言語の文を選ぶ。言語は起動時に一度だけ決める（変更は再起動で反映する。SPEC 6 章）。
/// 文は呼び出し元に日本語と英語を並べて書く。リソースファイルを読まずに済み、訳の抜けはコンパイル時に分かる。
/// </summary>
public static class UiLanguage
{
    /// <summary>
    /// 今の言語。<see cref="Apply"/> を呼ぶまでは日本語（このライブラリを単体で使う場合とテストのため）。
    /// </summary>
    public static DisplayLanguage Current { get; private set; } = DisplayLanguage.Japanese;

    /// <summary>設定に従って言語を決める。画面の文を作る前（起動時、設定を読んだ直後）に呼ぶこと。</summary>
    public static void Apply(AppLanguage setting) => Current = Resolve(setting, CultureInfo.CurrentUICulture);

    /// <summary>
    /// 設定と Windows の表示言語から、使う言語を決める。Windows に従う場合、日本語以外の言語はすべて英語にする。
    /// </summary>
    public static DisplayLanguage Resolve(AppLanguage setting, CultureInfo uiCulture) => setting switch
    {
        AppLanguage.Japanese => DisplayLanguage.Japanese,
        AppLanguage.English => DisplayLanguage.English,
        _ => uiCulture.TwoLetterISOLanguageName == "ja" ? DisplayLanguage.Japanese : DisplayLanguage.English,
    };

    /// <summary>今の言語の文を選ぶ。</summary>
    public static string Text(string japanese, string english) =>
        Current == DisplayLanguage.English ? english : japanese;

    /// <summary>テストで、言語を一時的に切り替える。</summary>
    internal static void SetForTest(DisplayLanguage language) => Current = language;
}
