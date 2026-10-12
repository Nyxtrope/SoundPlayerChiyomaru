using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using SoundPlayerChiyomaru.Core.Tags;

namespace SoundPlayerChiyomaru;

/// <summary>
/// 曲のアートワークを、画面を止めずに裏で読み込む。表示する大きさに縮小して読み込むので、
/// 保持するのは小さな画像 1 枚だけになる。UI スレッドから使うこと。
/// </summary>
public sealed class ArtworkLoader(ArtworkFinder finder)
{
    /// <summary>
    /// 読み込む画像の画素数の上限。PNG などは元の大きさで展開してから縮小するので、展開に画素数 × 4 バイトのメモリが要る。
    /// ファイルは小さくても縦横が極端に大きい画像で、メモリを使い切らないようにする。
    /// </summary>
    private const long MaxPixels = 40_000_000;

    private int request;
    private string? requestedPath;
    private bool running;
    private (int Id, string Path, int PixelSize)? pending;

    /// <summary>
    /// 読み込みが終わった（UI スレッドで発生する）。画像がなければ null。
    /// 受け取った画像は、受け取った側が破棄する。
    /// </summary>
    public event Action<Bitmap?>? Loaded;

    /// <summary>曲のアートワークを読み込む。前の読み込みが終わっていなければ、その結果は捨てる。</summary>
    /// <param name="path">曲のファイル。null なら「画像なし」を通知する。</param>
    /// <param name="pixelSize">表示する大きさ（画面の実際のピクセル数）。</param>
    public void Load(string? path, int pixelSize)
    {
        if (path == requestedPath)
        {
            return;
        }
        requestedPath = path;

        int id = ++request;
        pending = null;
        if (path is null)
        {
            Loaded?.Invoke(null);
            return;
        }

        // 読み込みは 1 つずつ行う。曲を続けて送られたときに、大きな画像の展開がいくつも同時に走らないようにするため。
        // 待たせるのは最新の要求だけで、その間に来た古い要求は読まずに捨てる。
        pending = (id, path, pixelSize);
        if (!running)
        {
            StartNext();
        }
    }

    /// <summary>読み込み中の結果を受け取らないようにする。</summary>
    public void Cancel()
    {
        request++;
        requestedPath = null;
        pending = null;
    }

    private void StartNext()
    {
        if (pending is not var (id, path, pixelSize))
        {
            running = false;
            return;
        }

        pending = null;
        running = true;
        Task.Run(() =>
        {
            Bitmap? image = Decode(finder.Find(path), pixelSize);
            Dispatcher.UIThread.Post(() =>
            {
                if (id == request)
                {
                    Loaded?.Invoke(image);
                }
                else
                {
                    image?.Dispose();
                }
                StartNext();
            });
        });
    }

    private static Bitmap? Decode(byte[]? bytes, int pixelSize)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            // 見出しだけを読んで大きさを確かめる（画素は展開しない）。
            SKImageInfo info = SKBitmap.DecodeBounds(bytes);
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(stream, Math.Max(1, pixelSize), BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            // 画像として読めないデータ。形式ごとにさまざまな例外になる。表示できなくても再生には関係ないので、なしとして扱う。
            return null;
        }
        finally
        {
            // 縮小のときに描画ライブラリ（Skia）が作る中間の画像（元の大きさの 1/2、1/4…）が、共有のキャッシュに残る。
            // 3000 ピクセル四方の PNG で約 12MB になり、次に使われることもないので、読み込みのたびに捨てる。
            SKGraphics.PurgeResourceCache();
        }
    }
}
