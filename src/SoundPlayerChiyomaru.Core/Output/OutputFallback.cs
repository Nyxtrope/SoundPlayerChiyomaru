using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using SoundPlayerChiyomaru.Core.Localization;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>出力の作り方の候補。</summary>
/// <param name="FailureLabel">この候補で作れなかったときに表示する文（「排他モードを使えません」など）。</param>
/// <param name="FallbackLabel">前の候補が失敗し、この候補で作れたときに表示する文（「共有モードで再生しています」など）。</param>
/// <param name="Create">出力を作る。引数は、出力の <see cref="OutputInfo.Notice"/> に入れる説明（問題がなければ null）。</param>
public sealed record OutputAttempt(string FailureLabel, string FallbackLabel, Func<string?, IAudioOutput> Create);

/// <summary>
/// 出力を、候補の順に作れるまで試す。排他モードで初期化できなければ共有モード、選んだデバイスが使えなければ既定のデバイス、のように使う。
/// </summary>
public static class OutputFallback
{
    /// <summary>最初に作れた出力を返す。前の候補が失敗していた場合は、その理由が <see cref="OutputInfo.Notice"/> に入る。</summary>
    /// <exception cref="InvalidOperationException">どの候補でも作れなかった。</exception>
    public static IAudioOutput Create(IReadOnlyList<OutputAttempt> attempts)
    {
        var failures = new List<string>();
        Exception? last = null;

        foreach (OutputAttempt attempt in attempts)
        {
            string? notice = failures.Count == 0
                ? null
                : $"{string.Concat(failures)}{attempt.FallbackLabel}{UiLanguage.Text("。", ".")}";
            try
            {
                return attempt.Create(notice);
            }
            catch (Exception ex)
            {
                // デバイスや形式に起因する失敗は種類が多い。どの失敗でも、次の候補を試す以外にできることがない。
                string reason = DescribeFailure(ex);
                failures.Add(UiLanguage.Text($"{attempt.FailureLabel}（{reason}）。", $"{attempt.FailureLabel} ({reason}). "));
                last = ex;
            }
        }

        throw new InvalidOperationException(
            failures.Count == 0
                ? UiLanguage.Text("使える出力デバイスがありません。", "No output device is available.")
                : string.Concat(failures).TrimEnd(),
            last);
    }

    /// <summary>
    /// 出力を初期化できなかった原因を、利用者に分かる言葉にする（SPEC 3.2「排他モードのエラー処理とフォールバック」）。
    /// </summary>
    public static string DescribeFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is COMException com)
            {
                switch (com.HResult)
                {
                    case AudioClientErrorCode.UnsupportedFormat:
                        return UiLanguage.Text("デバイスがこの形式を受け付けません", "the device does not accept this format");
                    case AudioClientErrorCode.DeviceInUse:
                        return UiLanguage.Text("他のアプリがデバイスを使用中です", "another app is using the device");
                    case AudioClientErrorCode.ExclusiveModeNotAllowed:
                        return UiLanguage.Text(
                            "デバイスの設定で排他モードが許可されていません",
                            "exclusive mode is not allowed in the device settings");
                    case AudioClientErrorCode.DeviceInvalidated:
                    case AudioClientErrorCode.ResourcesInvalidated:
                        return UiLanguage.Text("デバイスが使えなくなりました", "the device is no longer available");
                }
            }
        }
        return exception.Message;
    }
}
