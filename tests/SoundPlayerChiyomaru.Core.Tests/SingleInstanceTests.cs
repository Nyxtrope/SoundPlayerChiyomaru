using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Win32;
using SoundPlayerChiyomaru.Core.Instance;
using SoundPlayerChiyomaru.Core.Shell;

namespace SoundPlayerChiyomaru.Core.Tests;

public class SingleInstanceTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private readonly string name = "chiyomaru-test-" + Guid.NewGuid().ToString("N");
    private readonly BlockingCollection<IReadOnlyList<string>> received = [];

    private IReadOnlyList<string> Take() =>
        received.TryTake(out IReadOnlyList<string>? message, Long) ? message : throw new TimeoutException("届きませんでした。");

    [Fact]
    public void 名乗りを上げられるのは1つだけで_破棄すると次が取れる()
    {
        SingleInstance? first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));

        first.Dispose();

        using SingleInstance? again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void 送ったパスが_届いた順にそのまま受け取れる()
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);
        string[] first = [@"C:\音楽\曲 [live] #1.mp3", @"\\server\share\b.flac"];

        Assert.True(SingleInstance.TrySend(name, first, Long));
        Assert.True(SingleInstance.TrySend(name, [], Long));
        Assert.True(SingleInstance.TrySend(name, ["x"], Long));

        Assert.Equal(first, Take());
        Assert.Empty(Take());
        Assert.Equal(["x"], Take());
    }

    [Fact]
    public void 受信している相手がいなければ_待ち時間の後に失敗を返す()
    {
        Assert.False(SingleInstance.TrySend(name, ["a"], Short));
    }

    [Fact]
    public void 相手が受信を始める前に送っても_始まるまで待って届く()
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        // ミューテックスは取得したスレッドで解放する必要があるので、テスト自体は同期のままにして、送信側を別スレッドにする。
        bool sent = false;
        var sending = new Thread(() => sent = SingleInstance.TrySend(name, ["early"], Long));
        sending.Start();
        Thread.Sleep(300);
        Assert.True(sending.IsAlive);

        instance.StartListening(received.Add);

        Assert.Equal(["early"], Take());
        Assert.True(sending.Join(Long));
        Assert.True(sent);
    }

    [Fact]
    public void 同時に送られた要求は_取りこぼさずにすべて届く()
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);

        bool[] results = Enumerable.Range(0, 12)
            .AsParallel()
            .WithDegreeOfParallelism(12)
            .Select(i => SingleInstance.TrySend(name, [$"file{i}"], Long))
            .ToArray();

        Assert.All(results, Assert.True);
        string[] all = Enumerable.Range(0, 12).Select(_ => Assert.Single(Take())).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"file{i}").Order(), all);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 10, 0, 0, 0, 65 })]
    public void 壊れた要求は捨てて_その後の要求は受け取れる(byte[] garbage)
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);

        using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            client.Connect(Long);
            client.Write(garbage);
        }

        Assert.True(SingleInstance.TrySend(name, ["after"], Long));
        Assert.Equal(["after"], Take());
        Assert.Empty(received);
    }

    [Fact]
    public void 接続したまま何も送らない相手がいても_受信は止まらない()
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);
        using var silent = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        silent.Connect(Long);

        Assert.True(SingleInstance.TrySend(name, ["after"], Long));

        Assert.Equal(["after"], Take());
    }

    [Fact]
    public void 破棄すると受信が止まり_送信は失敗する()
    {
        SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);
        Assert.True(SingleInstance.TrySend(name, ["a"], Long));
        Take();

        instance.Dispose();

        Assert.False(SingleInstance.TrySend(name, ["b"], Short));
    }

    [Fact]
    public void 空の文字列や_対になっていないサロゲートを含むパスも_変わらずに届く()
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);
        string loneSurrogate = "C:\\music\\bad\uD83Dname.mp3";

        Assert.True(SingleInstance.TrySend(name, [""], Long));
        Assert.True(SingleInstance.TrySend(name, ["", "a", ""], Long));
        Assert.True(SingleInstance.TrySend(name, [loneSurrogate], Long));

        Assert.Equal([""], Take());
        Assert.Equal(["", "a", ""], Take());
        Assert.Equal(loneSurrogate, Assert.Single(Take()));
    }

    [Fact]
    public void 受信を止めると_保持したまま送信だけが失敗するようになる()
    {
        using SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);

        instance.StopListening();
        instance.StopListening();

        Assert.False(SingleInstance.TrySend(name, ["a"], Short));
        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void 二度破棄しても_別のスレッドから破棄しても例外にならない()
    {
        SingleInstance instance = SingleInstance.TryAcquire(name)!;
        instance.StartListening(received.Add);
        Exception? failure = null;

        var other = new Thread(() => failure = Record.Exception(instance.Dispose));
        other.Start();
        Assert.True(other.Join(Long));
        instance.Dispose();

        Assert.Null(failure);
        Assert.False(SingleInstance.TrySend(name, ["a"], Short));
    }

    [Theory]
    [InlineData("yukino", 1, "SoundPlayerChiyomaru-yukino-1")]
    [InlineData("a.b", 2, "SoundPlayerChiyomaru-a_002Eb-2")]
    [InlineData("a_b", 2, "SoundPlayerChiyomaru-a_005Fb-2")]
    [InlineData(@"dom\user name", 3, "SoundPlayerChiyomaru-dom_005Cuser_0020name-3")]
    [InlineData("太郎", 4, "SoundPlayerChiyomaru-_592A_90CE-4")]
    public void 名前は英数字以外を文字コードに置き換え_別の利用者名が同じ名前にならない(string user, int session, string expected)
    {
        Assert.Equal(expected, SingleInstance.BuildName(user, session));
    }
}

public class ExternalOpenPlannerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private readonly ManualTime time = new();
    private readonly ExternalOpenPlanner planner;

    public ExternalOpenPlannerTests()
    {
        planner = new ExternalOpenPlanner(Window, time);
    }

    private ExternalOpenOperation PlanAfter(int milliseconds, ExternalOpenAction action)
    {
        time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        return planner.Plan(planner.Now, action);
    }

    [Theory]
    [InlineData(ExternalOpenAction.ReplaceAndPlay, ExternalOpenOperation.Replace)]
    [InlineData(ExternalOpenAction.Append, ExternalOpenOperation.Add)]
    [InlineData(ExternalOpenAction.AppendAndPlay, ExternalOpenOperation.AddAndPlay)]
    public void 最初の要求は設定どおりの操作になる(ExternalOpenAction action, ExternalOpenOperation expected)
    {
        Assert.Equal(expected, PlanAfter(0, action));
    }

    [Theory]
    [InlineData(ExternalOpenAction.ReplaceAndPlay, ExternalOpenOperation.AddAndPlayIfIdle)]
    [InlineData(ExternalOpenAction.AppendAndPlay, ExternalOpenOperation.AddAndPlayIfIdle)]
    [InlineData(ExternalOpenAction.Append, ExternalOpenOperation.Add)]
    public void 間を置かずに届いた要求は追加になり_再生する操作の続きなら止まっているときだけ再生する(
        ExternalOpenAction action, ExternalOpenOperation expected)
    {
        PlanAfter(0, action);

        Assert.Equal(expected, PlanAfter(500, action));
        Assert.Equal(expected, PlanAfter(100, action));
    }

    [Fact]
    public void 間隔が窓を超えたら新しい操作になる()
    {
        PlanAfter(0, ExternalOpenAction.ReplaceAndPlay);

        Assert.Equal(ExternalOpenOperation.Replace, PlanAfter(501, ExternalOpenAction.ReplaceAndPlay));
    }

    [Fact]
    public void 続きが届き続ける限り_最後の要求から数え直す()
    {
        PlanAfter(0, ExternalOpenAction.ReplaceAndPlay);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(ExternalOpenOperation.AddAndPlayIfIdle, PlanAfter(400, ExternalOpenAction.ReplaceAndPlay));
        }
    }

    [Fact]
    public void 続きかどうかは処理した時刻ではなく届いた時刻で決まる()
    {
        long first = planner.Now;
        time.Advance(TimeSpan.FromMilliseconds(300));
        long second = planner.Now;

        // 1 件目の処理に時間がかかり、2 件目を処理するころには窓を過ぎている。
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(ExternalOpenOperation.Replace, planner.Plan(first, ExternalOpenAction.ReplaceAndPlay));
        Assert.Equal(ExternalOpenOperation.AddAndPlayIfIdle, planner.Plan(second, ExternalOpenAction.ReplaceAndPlay));
    }

    [Theory]
    [InlineData(ExternalOpenAction.ReplaceAndPlay)]
    [InlineData(ExternalOpenAction.Append)]
    public void 起動時のファイルの直後に届いた要求は_設定に関係なく再生する操作の続きになる(ExternalOpenAction action)
    {
        planner.RegisterStartup(planner.Now);

        Assert.Equal(ExternalOpenOperation.AddAndPlayIfIdle, PlanAfter(300, action));
    }

    [Fact]
    public void 起動から時間が経ってから届いた要求は新しい操作になる()
    {
        planner.RegisterStartup(planner.Now);

        Assert.Equal(ExternalOpenOperation.Add, PlanAfter(2000, ExternalOpenAction.Append));
        Assert.Equal(ExternalOpenOperation.Add, PlanAfter(100, ExternalOpenAction.Append));
    }
}

/// <summary>実際の関連付けを変えないよう、テスト専用のキーを「Software」に見立てて実行し、終わったら消す。</summary>
public sealed class FileAssociationRegistrarTests : IDisposable
{
    private const string TestsKey = @"Software\SoundPlayerChiyomaru.Tests";
    private const string Exe = @"C:\Apps\Chiyomaru Player\SoundPlayerChiyomaru.exe";

    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly RegistryKey root;

    public FileAssociationRegistrarTests()
    {
        root = Registry.CurrentUser.CreateSubKey($@"{TestsKey}\{id}");
    }

    public void Dispose()
    {
        root.Dispose();
        using RegistryKey? tests = Registry.CurrentUser.OpenSubKey(TestsKey, writable: true);
        tests?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
        if (tests is { SubKeyCount: 0, ValueCount: 0 })
        {
            Registry.CurrentUser.DeleteSubKey(TestsKey, throwOnMissingSubKey: false);
        }
    }

    private FileAssociationRegistrar Create(string exe = Exe) => new(root, exe, [".mp3", ".flac"]);

    private string? Value(string path, string? name)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    [Fact]
    public void 登録すると_開くコマンドと各拡張子の候補と既定のアプリ用の情報が書かれる()
    {
        FileAssociationRegistrar registrar = Create();
        Assert.False(registrar.IsRegistered);

        registrar.Register();

        string command = $"\"{Exe}\" \"%1\"";
        Assert.True(registrar.IsRegistered);
        Assert.Equal(command, Value(@"Classes\SoundPlayerChiyomaru.AudioFile\shell\open\command", null));
        Assert.Equal($"\"{Exe}\",0", Value(@"Classes\SoundPlayerChiyomaru.AudioFile\DefaultIcon", null));
        Assert.Equal("", Value(@"Classes\.mp3\OpenWithProgids", "SoundPlayerChiyomaru.AudioFile"));
        Assert.Equal("", Value(@"Classes\.flac\OpenWithProgids", "SoundPlayerChiyomaru.AudioFile"));
        Assert.Equal(command, Value(@"Classes\Applications\SoundPlayerChiyomaru.exe\shell\open\command", null));
        Assert.Equal("", Value(@"Classes\Applications\SoundPlayerChiyomaru.exe\SupportedTypes", ".flac"));
        Assert.Equal(
            "SoundPlayerChiyomaru.AudioFile",
            Value(@"SoundPlayerChiyomaru\Capabilities\FileAssociations", ".mp3"));
        Assert.Equal(
            @"Software\SoundPlayerChiyomaru\Capabilities",
            Value("RegisteredApplications", "SoundPlayerChiyomaru"));
    }

    [Fact]
    public void 解除すると_登録で書いたものは何も残らない()
    {
        FileAssociationRegistrar registrar = Create();
        registrar.Register();

        registrar.Unregister();

        Assert.False(registrar.IsRegistered);
        Assert.Equal(0, root.SubKeyCount);
        Assert.Equal(0, root.ValueCount);
    }

    [Fact]
    public void 登録と解除は_前からあった関連付けや他のアプリの登録を変えない()
    {
        using (RegistryKey mp3 = root.CreateSubKey(@"Classes\.mp3"))
        {
            mp3.SetValue(null, "OtherPlayer.mp3");
            using RegistryKey openWith = mp3.CreateSubKey("OpenWithProgids");
            openWith.SetValue("OtherPlayer.mp3", "");
        }
        using (RegistryKey registered = root.CreateSubKey("RegisteredApplications"))
        {
            registered.SetValue("OtherPlayer", @"Software\OtherPlayer\Capabilities");
        }
        using (RegistryKey other = root.CreateSubKey(@"Classes\Applications\other.exe"))
        {
            other.SetValue("FriendlyAppName", "Other");
        }
        FileAssociationRegistrar registrar = Create();

        registrar.Register();
        Assert.Equal("OtherPlayer.mp3", Value(@"Classes\.mp3", null));
        Assert.Equal("", Value(@"Classes\.mp3\OpenWithProgids", "OtherPlayer.mp3"));

        registrar.Unregister();

        Assert.Equal("OtherPlayer.mp3", Value(@"Classes\.mp3", null));
        Assert.Equal("", Value(@"Classes\.mp3\OpenWithProgids", "OtherPlayer.mp3"));
        Assert.Null(Value(@"Classes\.mp3\OpenWithProgids", "SoundPlayerChiyomaru.AudioFile"));
        Assert.Equal(@"Software\OtherPlayer\Capabilities", Value("RegisteredApplications", "OtherPlayer"));
        Assert.Null(Value("RegisteredApplications", "SoundPlayerChiyomaru"));
        Assert.Equal("Other", Value(@"Classes\Applications\other.exe", "FriendlyAppName"));
        Assert.Null(root.OpenSubKey(@"Classes\.flac"));
        Assert.Null(root.OpenSubKey(@"Classes\SoundPlayerChiyomaru.AudioFile"));
        Assert.Null(root.OpenSubKey("SoundPlayerChiyomaru"));
    }

    [Fact]
    public void 実行ファイルを移動した後は_登録済みとはみなさず_登録し直すと新しい場所を指す()
    {
        Create().Register();
        FileAssociationRegistrar moved = Create(@"D:\New\SoundPlayerChiyomaru.exe");

        Assert.False(moved.IsRegistered);
        moved.Register();

        Assert.True(moved.IsRegistered);
        Assert.False(Create().IsRegistered);
        Assert.Equal(
            "\"D:\\New\\SoundPlayerChiyomaru.exe\" \"%1\"",
            Value(@"Classes\Applications\SoundPlayerChiyomaru.exe\shell\open\command", null));
    }

    [Fact]
    public void 登録した後で実行ファイルの名前や拡張子の一覧が変わっても_解除で残骸を残さない()
    {
        new FileAssociationRegistrar(root, @"C:\Apps\OldName.exe", [".mp3", ".flac", ".wma"]).Register();
        var renamed = new FileAssociationRegistrar(root, @"C:\Apps\NewName.exe", [".mp3"]);

        renamed.Unregister();

        Assert.Equal(0, root.SubKeyCount);
        Assert.Equal(0, root.ValueCount);
    }

    [Fact]
    public void 登録していない状態で解除しても何も起きない()
    {
        Create().Unregister();

        Assert.Equal(0, root.SubKeyCount);
    }

    [Fact]
    public void 二度登録しても_一度の解除ですべて消える()
    {
        FileAssociationRegistrar registrar = Create();
        registrar.Register();
        registrar.Register();

        registrar.Unregister();

        Assert.Equal(0, root.SubKeyCount);
    }
}
