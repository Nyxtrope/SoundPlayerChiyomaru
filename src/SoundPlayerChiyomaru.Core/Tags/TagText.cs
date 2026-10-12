using System.Text;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>タグの文字列の復号。</summary>
internal sealed class TagText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Encoding? shiftJis;

    /// <param name="tryShiftJis">
    /// 文字コードの指定がない（または Latin-1 と書かれている）文字列を、シフト JIS としても試すか。
    /// </param>
    public TagText(bool tryShiftJis)
    {
        if (tryShiftJis)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            shiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
    }

    /// <summary>
    /// システムの文字コードが日本語（シフト JIS）のときだけ、シフト JIS を試す。
    /// 中国語・韓国語の文字コードは Latin-1 の欧文と見分けにくく、欧文を壊すおそれが大きいので試さない。
    /// </summary>
    public static TagText ForSystem()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // コードページ 0 は「システムの既定」。
        return new TagText(Encoding.GetEncoding(0).CodePage == 932);
    }

    /// <summary>
    /// 文字コードの指定がない、または Latin-1 と書かれている文字列。古い日本語のタグは、Latin-1 と書きながら
    /// 実際にはシフト JIS で書かれていることが多い。UTF-8 として正しければ UTF-8、シフト JIS らしければシフト JIS、
    /// どちらでもなければ Latin-1 として読む。
    /// </summary>
    /// <param name="mayBeCut">固定長の欄に収めるために、文字の途中で切られていることがあるか。</param>
    public string Legacy(ReadOnlySpan<byte> bytes, bool mayBeCut = false)
    {
        if (!bytes.ContainsAnyExceptInRange((byte)0, (byte)0x7F))
        {
            return Encoding.Latin1.GetString(bytes);
        }

        // 途中で切られた文字は、UTF-8 なら最大 3 バイト、シフト JIS なら 1 バイトが末尾に残る。それを落として読み直す。
        for (int cut = 0; cut <= (mayBeCut ? 3 : 0) && cut < bytes.Length; cut++)
        {
            ReadOnlySpan<byte> candidate = bytes[..^cut];
            if (cut > 0 && !candidate.ContainsAnyExceptInRange((byte)0, (byte)0x7F))
            {
                // 残りが ASCII だけになるのは、アクセント付きの文字で終わる Latin-1 の文字列。切られた文字ではない。
                break;
            }

            try
            {
                return StrictUtf8.GetString(candidate);
            }
            catch (DecoderFallbackException)
            {
            }

            if (shiftJis is not null && cut <= 1 && LooksLikeShiftJis(candidate))
            {
                try
                {
                    return shiftJis.GetString(candidate);
                }
                catch (DecoderFallbackException)
                {
                }
            }
        }
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// シフト JIS で書かれた日本語らしいか。Latin-1 のアクセント付きの文字の並びは、シフト JIS としても復号できてしまうことが多い
    /// （「é」＋英字が漢字 1 文字になる、大文字のアクセント付きの文字が半角カナになる）ので、復号できるだけでは決めない。
    /// 次のどちらかがあるときだけ、日本語とみなす。
    /// <list type="bullet">
    /// <item>1 バイト目が 0x81〜0x9F の 2 バイト文字（ひらがな・カタカナ・記号・第 1 水準の漢字）。Latin-1 ではこの範囲は制御文字で、文章には現れない。</item>
    /// <item>半角カナが 3 文字以上続く並び。</item>
    /// </list>
    /// ただし Windows の欧文の文字コードは 0x80〜0x9F に引用符やダッシュを持つので、それらの直後が ASCII の場合は証拠に数えない。
    /// </summary>
    private static bool LooksLikeShiftJis(ReadOnlySpan<byte> bytes)
    {
        bool evidence = false;
        int kanaRun = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b is >= 0xA1 and <= 0xDF)
            {
                evidence |= ++kanaRun >= 3;
                continue;
            }

            kanaRun = 0;
            if (b < 0x80)
            {
                continue;
            }
            if (b is 0x80 or 0xA0 or > 0xFC || i + 1 >= bytes.Length)
            {
                return false;
            }

            byte trail = bytes[++i];
            if (trail is < 0x40 or 0x7F or > 0xFC)
            {
                return false;
            }
            if (b <= 0x9F)
            {
                bool westernPunctuation = b is 0x85 or (>= 0x91 and <= 0x94) or 0x96 or 0x97;
                evidence |= !(westernPunctuation && trail < 0x80);
            }
        }
        return evidence;
    }

    /// <summary>不正な並びがあっても読める範囲で読む UTF-8。</summary>
    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    public static string Utf16LittleEndian(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(Even(bytes));

    public static string Utf16BigEndian(ReadOnlySpan<byte> bytes) => Encoding.BigEndianUnicode.GetString(Even(bytes));

    /// <summary>先頭の BOM で向きを決める UTF-16。BOM がなければリトルエンディアンとして読む。</summary>
    public static string Utf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Utf16BigEndian(bytes[2..]);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Utf16LittleEndian(bytes[2..]);
        }
        return Utf16LittleEndian(bytes);
    }

    /// <summary>NUL で区切られた複数の値を「; 」でつなぐ。値ごとの BOM と空の値は取り除く。</summary>
    public static string? JoinValues(string text)
    {
        string joined = text.Contains('\0')
            ? string.Join("; ", text.Split('\0').Select(value => value.Trim('﻿')).Where(value => value.Length > 0))
            : text.Trim('﻿');
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>最初の NUL より前の部分。</summary>
    public static ReadOnlySpan<byte> UntilNul(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return end < 0 ? bytes : bytes[..end];
    }

    private static ReadOnlySpan<byte> Even(ReadOnlySpan<byte> bytes) => bytes[..(bytes.Length & ~1)];
}
