using SoundPlayerChiyomaru.Core.Localization;

namespace SoundPlayerChiyomaru.Core.Decoding;

/// <summary>
/// 拡張子からデコーダを選んで開く。担当のデコーダで開けなければ、代替として登録されたデコーダを順に試す。
/// </summary>
public sealed class DecoderRegistry : IAudioDecoderOpener
{
    private readonly IReadOnlyList<IAudioDecoderFactory> factories;

    /// <param name="factories">優先順に並べたデコーダ。</param>
    public DecoderRegistry(IReadOnlyList<IAudioDecoderFactory> factories)
    {
        this.factories = factories;
    }

    public static DecoderRegistry CreateDefault() => new(
    [
        new WavDecoderFactory(),
        new MediaFoundationDecoderFactory(),
    ]);

    /// <summary>いずれかのデコーダが担当する拡張子（ドット付き・小文字、重複なし）。</summary>
    public IReadOnlyList<string> SupportedExtensions =>
        factories.SelectMany(f => f.Extensions).Distinct().ToArray();

    public IAudioDecoder Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(UiLanguage.Text($"ファイルが見つかりません: {path}", $"File not found: {path}"), path);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        List<Exception>? failures = null;

        foreach (IAudioDecoderFactory factory in Candidates(extension))
        {
            try
            {
                return factory.Open(path);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(new InvalidOperationException($"{factory.Name}: {ex.Message}", ex));
            }
        }

        string name = Path.GetFileName(path);
        string reasons = failures is null ? "" : string.Join(" / ", failures.Select(f => f.Message));
        throw new AudioDecodeException(
            failures is null
                ? UiLanguage.Text($"対応していない形式です: {name}", $"Unsupported format: {name}")
                : UiLanguage.Text($"再生できない形式です: {name}（{reasons}）", $"Cannot play this format: {name} ({reasons})"),
            failures is null ? null : new AggregateException(failures));
    }

    private IEnumerable<IAudioDecoderFactory> Candidates(string extension)
    {
        foreach (IAudioDecoderFactory factory in factories)
        {
            if (factory.Extensions.Contains(extension))
            {
                yield return factory;
            }
        }

        foreach (IAudioDecoderFactory factory in factories)
        {
            if (factory.IsFallback && !factory.Extensions.Contains(extension))
            {
                yield return factory;
            }
        }
    }
}
