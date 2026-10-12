using SoundPlayerChiyomaru.Core.Decoding;

namespace SoundPlayerChiyomaru.Core.Tests;

public class DecoderRegistryTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("chiyomaru-registry-").FullName;
    private readonly List<string> calls = [];

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void 拡張子を担当するデコーダで開く()
    {
        var registry = new DecoderRegistry([Factory("A", [".aaa"]), Factory("B", [".bbb"])]);

        using IAudioDecoder decoder = registry.Open(CreateFile("x.BBB"));

        Assert.Equal(["B"], calls);
    }

    [Fact]
    public void 担当のデコーダが失敗したら代替を登録順に試す()
    {
        var registry = new DecoderRegistry(
        [
            Factory("A", [".aaa"], fails: true),
            Factory("Other", [".zzz"]),
            Factory("Fallback1", [], isFallback: true, fails: true),
            Factory("Fallback2", [], isFallback: true),
        ]);

        using IAudioDecoder decoder = registry.Open(CreateFile("x.aaa"));

        Assert.Equal(["A", "Fallback1", "Fallback2"], calls);
    }

    [Fact]
    public void 担当も兼ねる代替デコーダは二度試さない()
    {
        var registry = new DecoderRegistry([Factory("Both", [".aaa"], isFallback: true, fails: true)]);

        Assert.Throws<AudioDecodeException>(() => registry.Open(CreateFile("x.aaa")));

        Assert.Equal(["Both"], calls);
    }

    [Fact]
    public void 未知の拡張子は代替デコーダで開く()
    {
        var registry = new DecoderRegistry([Factory("A", [".aaa"]), Factory("Fallback", [], isFallback: true)]);

        using IAudioDecoder decoder = registry.Open(CreateFile("noextension"));

        Assert.Equal(["Fallback"], calls);
    }

    [Fact]
    public void すべて失敗したら各デコーダの理由を含む例外を投げる()
    {
        var registry = new DecoderRegistry(
        [
            Factory("A", [".aaa"], fails: true),
            Factory("Fallback", [], isFallback: true, fails: true),
        ]);

        var ex = Assert.Throws<AudioDecodeException>(() => registry.Open(CreateFile("x.aaa")));

        Assert.Contains("A: A failed", ex.Message);
        Assert.Contains("Fallback: Fallback failed", ex.Message);
        Assert.Equal(2, Assert.IsType<AggregateException>(ex.InnerException).InnerExceptions.Count);
    }

    [Fact]
    public void 試せるデコーダがなければ非対応として例外を投げる()
    {
        var registry = new DecoderRegistry([Factory("A", [".aaa"])]);

        var ex = Assert.Throws<AudioDecodeException>(() => registry.Open(CreateFile("x.bbb")));

        Assert.Contains("対応していない", ex.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public void 対応拡張子は全デコーダの担当を重複なくまとめたもの()
    {
        var registry = new DecoderRegistry([Factory("A", [".aaa", ".bbb"]), Factory("B", [".bbb", ".ccc"])]);

        Assert.Equal([".aaa", ".bbb", ".ccc"], registry.SupportedExtensions);
    }

    [Fact]
    public void 存在しないファイルではデコーダを試さない()
    {
        var registry = new DecoderRegistry([Factory("A", [".aaa"])]);

        Assert.Throws<FileNotFoundException>(() => registry.Open(Path.Combine(directory, "missing.aaa")));

        Assert.Empty(calls);
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private StubFactory Factory(string name, string[] extensions, bool isFallback = false, bool fails = false) =>
        new(name, extensions, isFallback, fails, calls);

    private sealed class StubFactory(string name, string[] extensions, bool isFallback, bool fails, List<string> calls)
        : IAudioDecoderFactory
    {
        public string Name => name;

        public bool IsFallback => isFallback;

        public IReadOnlyList<string> Extensions => extensions;

        public IAudioDecoder Open(string path)
        {
            calls.Add(name);
            return fails
                ? throw new InvalidOperationException($"{name} failed")
                : new FakeDecoder(TimeSpan.FromSeconds(1));
        }
    }
}
