using System.Globalization;

namespace SoundPlayerChiyomaru.Core.Tags;

/// <summary>
/// ファイルから読み取ったタグの内容。同じ項目が複数のタグに書かれている場合は、先に読んだものを採る
/// （優先するタグから順に読ませる）。
/// </summary>
internal sealed class TagData
{
    /// <summary>表紙の画像を表す種別の番号（ID3v2・FLAC・ASF で共通）。</summary>
    public const int FrontCover = 3;

    private bool pictureIsFrontCover;

    public string? Title { get; private set; }

    public string? Artist { get; private set; }

    public string? Album { get; private set; }

    public string? AlbumArtist { get; private set; }

    public uint TrackNumber { get; private set; }

    public TimeSpan? Duration { get; set; }

    public double? TrackGainDb { get; private set; }

    public double? TrackPeak { get; private set; }

    public double? AlbumGainDb { get; private set; }

    public double? AlbumPeak { get; private set; }

    /// <summary>画像ファイル（JPEG や PNG）の中身そのもの。</summary>
    public byte[]? Picture { get; private set; }

    public void OfferTitle(string? value) => Title ??= Clean(value);

    public void OfferArtist(string? value) => Artist ??= Clean(value);

    public void OfferAlbum(string? value) => Album ??= Clean(value);

    public void OfferAlbumArtist(string? value) => AlbumArtist ??= Clean(value);

    /// <summary>「3」や「3/12」（曲数つき）の形のトラック番号。</summary>
    public void OfferTrackNumber(string? value)
    {
        if (value is null)
        {
            return;
        }

        ReadOnlySpan<char> digits = value.AsSpan().Trim();
        int end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end]))
        {
            end++;
        }
        if (uint.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out uint number))
        {
            OfferTrackNumber(number);
        }
    }

    public void OfferTrackNumber(uint value)
    {
        if (TrackNumber == 0)
        {
            TrackNumber = value;
        }
    }

    /// <summary>名前が ReplayGain の項目（大文字と小文字は区別しない）なら値を取り込む。</summary>
    /// <returns>ReplayGain の項目だったか。</returns>
    public bool OfferReplayGain(ReadOnlySpan<char> name, string? value)
    {
        if (name.Equals("replaygain_track_gain", StringComparison.OrdinalIgnoreCase))
        {
            TrackGainDb ??= ParseNumber(value);
        }
        else if (name.Equals("replaygain_track_peak", StringComparison.OrdinalIgnoreCase))
        {
            TrackPeak ??= ParseNumber(value);
        }
        else if (name.Equals("replaygain_album_gain", StringComparison.OrdinalIgnoreCase))
        {
            AlbumGainDb ??= ParseNumber(value);
        }
        else if (name.Equals("replaygain_album_peak", StringComparison.OrdinalIgnoreCase))
        {
            AlbumPeak ??= ParseNumber(value);
        }
        else
        {
            return false;
        }
        return true;
    }

    /// <summary>画像を取り込む。表紙があればそれを、なければ最初に見つかった画像を採る。</summary>
    public void OfferPicture(int type, byte[] image)
    {
        if (image.Length == 0)
        {
            return;
        }

        bool frontCover = type == FrontCover;
        if (Picture is null || (frontCover && !pictureIsFrontCover))
        {
            Picture = image;
            pictureIsFrontCover = frontCover;
        }
    }

    /// <summary>まだ入っていない項目を、優先度の低い別のタグの内容で埋める。</summary>
    public void FillFrom(TagData other)
    {
        Title ??= other.Title;
        Artist ??= other.Artist;
        Album ??= other.Album;
        AlbumArtist ??= other.AlbumArtist;
        OfferTrackNumber(other.TrackNumber);
        Duration ??= other.Duration;
        TrackGainDb ??= other.TrackGainDb;
        TrackPeak ??= other.TrackPeak;
        AlbumGainDb ??= other.AlbumGainDb;
        AlbumPeak ??= other.AlbumPeak;
        if (Picture is null && other.Picture is not null)
        {
            Picture = other.Picture;
            pictureIsFrontCover = other.pictureIsFrontCover;
        }
    }

    private static string? Clean(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>「-6.50 dB」や「0.988」の形の数値。読めなければ null。</summary>
    private static double? ParseNumber(string? value)
    {
        if (value is null)
        {
            return null;
        }

        ReadOnlySpan<char> text = value.AsSpan().Trim();
        if (text.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].TrimEnd();
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number
            : null;
    }
}
