using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace SoundPlayerChiyomaru.Core.Instance;

/// <summary>
/// 同じ利用者・同じセッションで 1 つだけ起動するための仕組み。最初に起動したプロセスがこれを保持し、
/// 後から起動したプロセスは、開きたいファイルをここへ送って終了する。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private const int AllowAnyProcess = -1;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly string name;
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stopping = new();
    private Task? listener;
    private bool disposed;

    private SingleInstance(string name, Mutex mutex)
    {
        this.name = name;
        this.mutex = mutex;
    }

    /// <summary>このアプリの既定の名前。利用者とセッションごとに別になる。</summary>
    public static string DefaultName => BuildName(Environment.UserName, Process.GetCurrentProcess().SessionId);

    /// <summary>
    /// ミューテックスとパイプの名前に使える文字だけで名前を作る。利用者名は使えない文字を含みうるので、
    /// 英数字以外は文字コードに置き換える（別の利用者名が同じ名前にならないようにするため）。
    /// </summary>
    internal static string BuildName(string userName, int sessionId)
    {
        var user = new StringBuilder();
        foreach (char c in userName)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                user.Append(c);
            }
            else
            {
                user.Append('_').Append(((int)c).ToString("X4"));
            }
        }
        return $"SoundPlayerChiyomaru-{user}-{sessionId}";
    }

    /// <summary>
    /// 最初のインスタンスとして名乗りを上げる。既に他のプロセスが保持していれば null。
    /// 返されたオブジェクトは、呼び出したスレッドで破棄すること。
    /// </summary>
    public static SingleInstance? TryAcquire(string name)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out createdNew);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // 管理者として起動したインスタンスが保持していると、通常の権限からは開けない。
            // 保持されていること自体は確かなので、取得できなかったものとして扱う。
            return null;
        }

        if (createdNew)
        {
            return new SingleInstance(name, mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// 起動済みのインスタンスへパスを送る。相手が受け取れば true。
    /// 相手が起動処理中でまだ受信を始めていない場合は、<paramref name="timeout"/> まで待つ。
    /// </summary>
    /// <param name="paths">送るパス。空でもよい（ウィンドウを前面に出す合図になる）。</param>
    public static bool TrySend(string name, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        byte[] payload = Encode(paths);
        if (payload.Length > MaxMessageBytes)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout);

            // 受け取った側がウィンドウを前面に出せるよう、その権利を譲る（前面にいるプロセスだけが譲れる）。
            AllowSetForegroundWindow(AllowAnyProcess);

            client.Write(BitConverter.GetBytes(payload.Length));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 他のプロセスからのパスの受信を始める。<paramref name="onReceived"/> は受信用のスレッドから、届いた順に 1 件ずつ呼ばれる。
    /// </summary>
    public void StartListening(Action<IReadOnlyList<string>> onReceived)
    {
        if (listener is not null)
        {
            throw new InvalidOperationException("受信は既に始まっています。");
        }
        listener = Task.Run(() => ListenAsync(onReceived, stopping.Token));
    }

    /// <summary>
    /// 受信をやめる。以後に送られた要求は受け取られず、送った側の <see cref="TrySend"/> は失敗する。
    /// 終了処理に入ったら最初に呼ぶこと。受け取ったのに処理できない要求を出さないため。
    /// </summary>
    public void StopListening()
    {
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        stopping.Cancel();
        try
        {
            listener?.Wait(StopTimeout);
        }
        catch (AggregateException)
        {
            // 受信の打ち切りによる例外。終了処理なので無視する。
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        StopListening();
        if (listener is null or { IsCompleted: true })
        {
            // 受信がまだ終わっていない場合は、使用中のものを破棄しないでおく（プロセスの終了で解放される）。
            stopping.Dispose();
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 取得したのとは別のスレッドから呼ばれた。解放はできないが、ハンドルは閉じておく。
        }
        mutex.Dispose();
    }

    /// <summary>パスを 1 個ずつ NUL で終端して並べる。空の文字列 1 個と「パスなし」を区別できる。</summary>
    private static byte[] Encode(IReadOnlyList<string> paths)
    {
        var text = new StringBuilder();
        foreach (string path in paths)
        {
            text.Append(path).Append('\0');
        }

        // ファイル名には対になっていないサロゲートを含められる。文字コードの変換を通すと別の文字に置き換わってしまうので、
        // 文字列の中身（UTF-16）をそのままバイト列として送る。
        return MemoryMarshal.AsBytes(text.ToString().AsSpan()).ToArray();
    }

    /// <summary>壊れている（文字の途中で切れている）場合は null。</summary>
    private static IReadOnlyList<string>? Decode(byte[] payload)
    {
        if (payload.Length % sizeof(char) != 0)
        {
            return null;
        }

        string text = new(MemoryMarshal.Cast<byte, char>(payload));
        if (text.Length == 0)
        {
            return [];
        }
        return text[^1] == '\0' ? text[..^1].Split('\0') : text.Split('\0');
    }

    private async Task ListenAsync(Action<IReadOnlyList<string>> onReceived, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    name,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation);

                if (await ReadMessageAsync(server, cancellation) is { } paths)
                {
                    onReceived(paths);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // 相手が途中で切断した。次の接続を待つ。
            }
        }
    }

    /// <summary>1 件読む。途中で切れた、大きすぎる、時間がかかりすぎる場合は null（その接続は捨てる）。</summary>
    private static async Task<IReadOnlyList<string>?> ReadMessageAsync(Stream stream, CancellationToken cancellation)
    {
        // 接続したまま何も送ってこない相手に、受信を止められないようにする。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ReadTimeout);

        try
        {
            byte[] header = new byte[sizeof(int)];
            await stream.ReadExactlyAsync(header, timeout.Token);
            int length = BitConverter.ToInt32(header);
            if (length is < 0 or > MaxMessageBytes)
            {
                return null;
            }

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, timeout.Token);
            return Decode(payload);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern int AllowSetForegroundWindow(int processId);
}
