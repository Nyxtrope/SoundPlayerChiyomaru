using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SoundPlayerChiyomaru.Core.Output;

/// <summary>出力デバイス。</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// 使える出力デバイスの一覧と、その変化の通知。どのメンバーも例外を投げない
/// （デバイスの抜き差しの最中は Windows への問い合わせが失敗することがあり、その場合は「ない」ものとして答える）。
/// </summary>
public interface IAudioDeviceService
{
    /// <summary>
    /// デバイスの追加・削除・有効/無効の切り替え、または既定のデバイスの変更があった。
    /// 1 回の変化で何度も発生することがある。
    /// </summary>
    event EventHandler? Changed;

    /// <summary>今使える出力デバイス。</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>その ID のデバイスが今使えるか。</summary>
    bool IsAvailable(string deviceId);

    /// <summary>Windows の既定の出力デバイスの ID。出力デバイスが 1 つもなければ null。</summary>
    string? DefaultDeviceId { get; }
}

/// <summary>
/// Windows の出力デバイスを列挙し、変化を見張る。変化の通知は、このオブジェクトを作ったスレッド（UI スレッド）で発生する。
/// </summary>
public sealed class WasapiDeviceService : IAudioDeviceService, IDisposable
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDeviceNotificationClient notifications;

    private WasapiDeviceService(MMDeviceEnumerator enumerator, MMDeviceNotificationClient notifications)
    {
        this.enumerator = enumerator;
        this.notifications = notifications;
        notifications.DeviceAdded += OnChanged;
        notifications.DeviceRemoved += OnChanged;
        notifications.DeviceStateChanged += OnChanged;
        notifications.DefaultDeviceChanged += OnDefaultChanged;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// 見張りを始める。Windows の音声の仕組みが使えない環境では null
    /// （その場合もアプリは起動でき、再生しようとした時点でエラーになる）。
    /// </summary>
    public static WasapiDeviceService? TryCreate()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            return new WasapiDeviceService(enumerator, enumerator.CreateNotificationClient(useSynchronizationContext: true));
        }
        catch (COMException)
        {
            enumerator?.Dispose();
            return null;
        }
    }

    public string? DefaultDeviceId
    {
        get
        {
            try
            {
                if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? device))
                {
                    return null;
                }

                using (device)
                {
                    return device.ID;
                }
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    try
                    {
                        result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                    }
                    catch (COMException)
                    {
                        // 列挙している間に抜かれたデバイス。一覧から外す。
                    }
                }
            }
        }
        catch (COMException)
        {
            // 列挙そのものができない。取れたところまでを返す。
        }
        return result;
    }

    public bool IsAvailable(string deviceId)
    {
        try
        {
            using MMDevice device = enumerator.GetDevice(deviceId);
            return device.State == DeviceState.Active;
        }
        catch (COMException)
        {
            // その ID のデバイスがない。
            return false;
        }
    }

    public void Dispose()
    {
        notifications.Dispose();
        enumerator.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnDefaultChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        // 録音デバイスの既定が変わっても出力には関係ない。
        if (e.Flow == DataFlow.Render)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>出力デバイスが 1 つもない状態を表す。Windows の音声の仕組みが使えない環境で使う。</summary>
public sealed class NoAudioDeviceService : IAudioDeviceService
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public string? DefaultDeviceId => null;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

    public bool IsAvailable(string deviceId) => false;
}

/// <summary>
/// 利用者が選んだ出力デバイスと、実際に使うデバイスの対応。
/// 選んだデバイスが使えない間は既定のデバイスを使い、使えるようになったら選んだデバイスに戻る。
/// </summary>
public sealed class OutputDeviceSelection
{
    private readonly IAudioDeviceService devices;
    private string? preferredDeviceId;

    public OutputDeviceSelection(IAudioDeviceService devices)
    {
        this.devices = devices;
    }

    /// <summary><see cref="PreferredDeviceId"/> が変わった。</summary>
    public event EventHandler? PreferredChanged;

    /// <summary>利用者が選んだデバイスの ID。null は「Windows の既定のデバイスに従う」。</summary>
    public string? PreferredDeviceId
    {
        get => preferredDeviceId;
        set
        {
            if (preferredDeviceId != value)
            {
                preferredDeviceId = value;
                PreferredChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// WASAPI 排他モードで出力するか。排他モードが使えない場合（他のアプリが使用中など）は、共有モードで出力される。
    /// </summary>
    public bool PreferExclusive { get; set; }

    /// <summary>出力のバッファの長さ（ミリ秒）の既定。</summary>
    public const int DefaultBufferMilliseconds = 200;

    /// <summary>設定画面で選べるバッファの長さ（ミリ秒）。</summary>
    public static IReadOnlyList<int> BufferChoices { get; } = [50, 100, 200, 300, 500];

    /// <summary>設定画面で選べる、出力のビット深度の上限。</summary>
    public static IReadOnlyList<int> BitDepthLimits { get; } = [16, 24];

    /// <summary>
    /// 出力のバッファの長さ（ミリ秒）。短いほど操作への反応が速く、長いほど音が途切れにくい。
    /// デバイスが受け付けない長さだと、排他モードでは初期化に失敗して共有モードに切り替わる。
    /// </summary>
    public int BufferMilliseconds { get; set; } = DefaultBufferMilliseconds;

    /// <summary>
    /// 排他モードでデバイスへ渡すビット深度の上限（有効ビット数）。null は上限なし（曲に合わせて自動で選ぶ）。
    /// 曲のビット数より小さければ変換が入るので、ビットパーフェクトではなくなる。
    /// </summary>
    public int? MaxBitDepth { get; set; }

    /// <summary>設定ファイルから読んだバッファの長さを、使える値に直す。選べる値以外は既定にする。</summary>
    public static int NormalizeBuffer(int milliseconds) =>
        BufferChoices.Contains(milliseconds) ? milliseconds : DefaultBufferMilliseconds;

    /// <summary>設定ファイルから読んだビット深度の上限を、使える値に直す。選べる値以外は「上限なし」にする。</summary>
    public static int? NormalizeBitDepth(int? bits) => bits is { } value && BitDepthLimits.Contains(value) ? value : null;

    /// <summary>Windows の既定の出力デバイスの ID。出力デバイスが 1 つもなければ null。</summary>
    public string? DefaultDeviceId => devices.DefaultDeviceId;

    /// <summary>今、出力に使うべきデバイスの ID。使えるデバイスが 1 つもなければ null。</summary>
    public string? ResolveDeviceId() =>
        preferredDeviceId is not null && devices.IsAvailable(preferredDeviceId)
            ? preferredDeviceId
            : devices.DefaultDeviceId;
}
