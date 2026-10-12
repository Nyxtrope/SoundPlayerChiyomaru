using SoundPlayerChiyomaru.Core.Playback;
using SoundPlayerChiyomaru.Core.Playlists;

namespace SoundPlayerChiyomaru.Core.Tags;

public interface IArtworkReader
{
    /// <summary>
    /// タグに埋め込まれた画像（表紙があれば表紙）を、画像ファイルの中身のまま返す。
    /// 画像がない、対応していない形式、壊れたファイルでは null を返す（例外は投げない）。
    /// </summary>
    byte[]? ReadArtwork(string path);
}

/// <summary>
/// 曲のファイルからタグを読む。読むのは、表示と再生に使う項目（題名・アーティスト・アルバム・アルバムアーティスト・トラック番号・長さ・
/// ReplayGain・画像）だけで、書き込みはしない。状態を持たないので、複数のスレッドから同時に使える。
/// </summary>
public sealed class TagReader : ITrackInfoReader, IReplayGainReader, IArtworkReader
{
    private readonly TagText text;

    public TagReader()
    {
        text = TagText.ForSystem();
    }

    /// <param name="tryShiftJis">
    /// 文字コードの指定がない文字列を、シフト JIS としても試すか。引数なしのコンストラクタは、システムの文字コードから決める。
    /// </param>
    public TagReader(bool tryShiftJis)
    {
        text = new TagText(tryShiftJis);
    }

    public TrackInfo? Read(string path) => Parse(path, wantPicture: false) is { } data
        ? new TrackInfo(data.Title, data.Artist, data.Album, data.TrackNumber, data.Duration, data.AlbumArtist)
        : null;

    public ReplayGainTags? ReadReplayGain(string path) =>
        Parse(path, wantPicture: false) is { } data && (data.TrackGainDb is not null || data.AlbumGainDb is not null)
            ? new ReplayGainTags(data.TrackGainDb, data.TrackPeak, data.AlbumGainDb, data.AlbumPeak)
            : null;

    public byte[]? ReadArtwork(string path) => Parse(path, wantPicture: true)?.Picture;

    /// <returns>その形式のファイルとして読めなかった場合は null。</returns>
    private TagData? Parse(string path, bool wantPicture)
    {
        try
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".mp3" or ".flac" or ".m4a" or ".m4b" or ".mp4" or ".aac" or ".wav" or ".wave" or ".wma"))
            {
                return null;
            }

            // 再生中のファイルも読む。他のソフトが書き込み用に開いていても読めるようにしておく。
            using var stream = new TagFileStream(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var data = new TagData();
            bool recognized = extension switch
            {
                ".mp3" => MpegTags.Read(stream, data, text, wantPicture),
                ".flac" => FlacTags.Read(stream, data, text, wantPicture),
                ".wav" or ".wave" => RiffTags.Read(stream, data, text, wantPicture),
                ".wma" => AsfTags.Read(stream, data, wantPicture),

                // .aac は、MP4 に入っていることも、生のストリーム（ADTS）のこともある。後者のタグの置き方は MP3 と同じ。
                ".aac" => Mp4Tags.Read(stream, data, wantPicture) || ReadRawAac(stream, data, wantPicture),
                _ => Mp4Tags.Read(stream, data, wantPicture),
            };
            return recognized ? data : null;
        }
        catch (Exception)
        {
            // 壊れたファイルでは、読み取りの途中でさまざまな例外が起きうる。タグが読めなくても再生はできるので、情報なしとして扱う。
            return null;
        }
    }

    private bool ReadRawAac(Stream stream, TagData data, bool wantPicture)
    {
        stream.Position = 0;
        return MpegTags.ReadTagsOnly(stream, data, text, wantPicture);
    }
}
