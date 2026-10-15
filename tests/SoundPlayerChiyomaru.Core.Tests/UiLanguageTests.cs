using System.Globalization;
using System.Runtime.InteropServices;
using SoundPlayerChiyomaru.Core.Localization;
using SoundPlayerChiyomaru.Core.Output;
using SoundPlayerChiyomaru.Core.Settings;

namespace SoundPlayerChiyomaru.Core.Tests;

public class UiLanguageTests
{
    [Theory]
    [InlineData("ja-JP", DisplayLanguage.Japanese)]
    [InlineData("ja", DisplayLanguage.Japanese)]
    [InlineData("en-US", DisplayLanguage.English)]
    [InlineData("fr-FR", DisplayLanguage.English)]
    [InlineData("zh-CN", DisplayLanguage.English)]
    [InlineData("", DisplayLanguage.English)]
    public void Windowsに従う設定では_日本語なら日本語_それ以外は英語(string culture, DisplayLanguage expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(AppLanguage.System, new CultureInfo(culture)));
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void 言語を選んだ設定では_Windowsの言語に関係なくその言語(string culture)
    {
        Assert.Equal(DisplayLanguage.Japanese, UiLanguage.Resolve(AppLanguage.Japanese, new CultureInfo(culture)));
        Assert.Equal(DisplayLanguage.English, UiLanguage.Resolve(AppLanguage.English, new CultureInfo(culture)));
    }

    [Fact]
    public void 設定ファイルにない値は_Windowsに従う設定として扱う()
    {
        Assert.Equal(DisplayLanguage.Japanese, UiLanguage.Resolve((AppLanguage)99, new CultureInfo("ja-JP")));
        Assert.Equal(DisplayLanguage.English, UiLanguage.Resolve((AppLanguage)99, new CultureInfo("en-US")));
    }

    [Theory]
    [InlineData("ファイル(_F)", "ファイル")]
    [InlineData("開く(_O)...", "開く...")]
    [InlineData("_File", "File")]
    [InlineData("Add Fol_der...", "Add Folder...")]
    [InlineData("E_xit", "Exit")]
    [InlineData("閉じる", "閉じる")]
    [InlineData("", "")]
    public void 見出しから文字キーの表記を取り除く(string header, string expected)
    {
        Assert.Equal(expected, AccessKeys.Remove(header));
    }

    [Theory]
    [InlineData("Line 1 (__)", "Line 1 (__)")]
    [InlineData("USB__DAC", "USB__DAC")]
    [InlineData("USB__DAC(_D)", "USB__DAC")]
    [InlineData("A__B _Cd", "A__B Cd")]
    public void 名前の中の下線を表す二重の下線は残す(string header, string expected)
    {
        Assert.Equal(expected, AccessKeys.Remove(header));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSwitchCollection
{
    /// <summary>言語は 1 つの静的な値なので、切り替えるテストは他のテストと同時に走らせない。</summary>
    public const string Name = "言語を切り替えるテスト";
}

[Collection(LanguageSwitchCollection.Name)]
public sealed class EnglishTextTests : IDisposable
{
    public EnglishTextTests() => UiLanguage.SetForTest(DisplayLanguage.English);

    public void Dispose() => UiLanguage.SetForTest(DisplayLanguage.Japanese);

    [Fact]
    public void 英語では英語の文を選ぶ()
    {
        Assert.Equal("Play", UiLanguage.Text("再生", "Play"));
    }

    [Fact]
    public void 代わりの出力の説明を_英語の句読点でつなぐ()
    {
        IAudioOutput output = OutputFallback.Create(
        [
            new OutputAttempt("Cannot use exclusive mode", "Playing in exclusive mode", _ => throw new COMException("raw", unchecked((int)0x8889000A))),
            new OutputAttempt("Cannot use this device", "Playing in shared mode", notice => new NamedOutput(notice)),
        ]);

        Assert.Equal("Cannot use exclusive mode (another app is using the device). Playing in shared mode.", output.Info.Notice);
    }

    [Fact]
    public void どの候補でも作れないときの例外の文は_末尾に空白を残さない()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OutputFallback.Create(
        [
            new OutputAttempt("Cannot use exclusive mode", "-", _ => throw new COMException("raw", unchecked((int)0x88890008))),
            new OutputAttempt("Cannot use this device", "-", _ => throw new InvalidOperationException("Broken.")),
        ]));

        Assert.Equal("Cannot use exclusive mode (the device does not accept this format). Cannot use this device (Broken.).", ex.Message);
        Assert.Equal("No output device is available.", Assert.Throws<InvalidOperationException>(() => OutputFallback.Create([])).Message);
    }

    [Fact]
    public void 出力の説明を英語にする()
    {
        var shared = new OutputInfo(false, null, false, null, null, null);
        var exclusive = new OutputInfo(true, TestFormats.Pcm24In32(96000), true, null, null, null);

        Assert.Equal(["WASAPI shared (normal path)", "Speakers"], OutputStatus.Describe(shared, "Speakers", false, false));
        Assert.Equal(
            ["WASAPI exclusive  ·  Bit-perfect", "Output 96000 Hz / 24-in-32"],
            OutputStatus.Describe(exclusive, null, isBitPerfect: true, replayGain: false));
        Assert.Equal(
            (BitPerfectBadge.Unavailable, "Exclusive mode is not in use. Playing in shared mode."),
            OutputStatus.Badge(true, shared, false, true, false));
    }

    private sealed class NamedOutput(string? notice) : IAudioOutput
    {
        public event EventHandler<AudioOutputStoppedEventArgs>? Stopped
        {
            add { }
            remove { }
        }

        public string DeviceId => "device";

        public string DeviceName => "device";

        public OutputInfo Info { get; } = new(false, null, false, null, null, notice);

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Dispose()
        {
        }
    }
}
