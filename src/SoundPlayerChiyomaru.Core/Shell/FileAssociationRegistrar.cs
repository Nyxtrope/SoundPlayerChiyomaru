using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SoundPlayerChiyomaru.Core.Shell;

/// <summary>
/// ファイルの関連付けを現在の利用者のレジストリに登録・解除する。利用者が明示的に操作したときだけ使うこと（SPEC 7 章）。
/// </summary>
/// <remarks>
/// 登録すると、エクスプローラーの「プログラムから開く」と Windows の「既定のアプリ」設定にこのアプリが現れる。
/// Windows 10 以降は、アプリが自分を既定のアプリに設定することはできない（利用者が設定で選ぶ）。
/// 既存の関連付け（拡張子の既定値や他のアプリの登録）には手を付けない。
/// </remarks>
public sealed class FileAssociationRegistrar
{
    private const string AppName = "SoundPlayerChiyomaru";
    private const string ProgId = "SoundPlayerChiyomaru.AudioFile";
    private const string CapabilitiesPath = @"Software\" + AppName + @"\Capabilities";

    private readonly RegistryKey softwareRoot;
    private readonly string executablePath;
    private readonly IReadOnlyList<string> extensions;

    /// <param name="softwareRoot">
    /// <c>HKEY_CURRENT_USER\Software</c>。テストでは、実際の設定を変えないよう別のキーを渡す。
    /// </param>
    /// <param name="executablePath">関連付けから起動する実行ファイルのフルパス。</param>
    /// <param name="extensions">関連付ける拡張子（ドット付き）。</param>
    public FileAssociationRegistrar(RegistryKey softwareRoot, string executablePath, IReadOnlyList<string> extensions)
    {
        this.softwareRoot = softwareRoot;
        this.executablePath = executablePath;
        this.extensions = extensions;
    }

    private string ApplicationKeyPath => @"Classes\Applications\" + Path.GetFileName(executablePath);

    private string OpenCommand => $"\"{executablePath}\" \"%1\"";

    /// <summary>この実行ファイルを指す登録があるか。実行ファイルを移動した後は false になる（登録し直しが必要）。</summary>
    public bool IsRegistered
    {
        get
        {
            using RegistryKey? command = softwareRoot.OpenSubKey($@"Classes\{ProgId}\shell\open\command");
            return string.Equals(command?.GetValue(null) as string, OpenCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Register()
    {
        using (RegistryKey progId = softwareRoot.CreateSubKey($@"Classes\{ProgId}"))
        {
            progId.SetValue(null, $"{AppName} 音声ファイル");
            using RegistryKey icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{executablePath}\",0");
            using RegistryKey command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(null, OpenCommand);
        }

        using (RegistryKey application = softwareRoot.CreateSubKey(ApplicationKeyPath))
        {
            application.SetValue("FriendlyAppName", AppName);
            using RegistryKey command = application.CreateSubKey(@"shell\open\command");
            command.SetValue(null, OpenCommand);
            using RegistryKey supported = application.CreateSubKey("SupportedTypes");
            foreach (string extension in extensions)
            {
                supported.SetValue(extension, "");
            }
        }

        // 「既定のアプリ」設定に出すための登録。
        using (RegistryKey capabilities = softwareRoot.CreateSubKey(AppName + @"\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "軽量音楽プレーヤー");
            using RegistryKey associations = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in extensions)
            {
                associations.SetValue(extension, ProgId);
            }
        }
        using (RegistryKey registered = softwareRoot.CreateSubKey("RegisteredApplications"))
        {
            registered.SetValue(AppName, CapabilitiesPath);
        }

        foreach (string extension in extensions)
        {
            using RegistryKey openWith = softwareRoot.CreateSubKey($@"Classes\{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, "");
        }
    }

    /// <summary>
    /// このアプリの登録を消す。他のアプリの値や、値・サブキーが残っているキーには手を付けない
    /// （登録を消した結果として空になったキーは消す）。
    /// 登録は利用者ごとに 1 つなので、別の場所に置いたコピーから登録したものも消える。
    /// </summary>
    public void Unregister()
    {
        // 登録した後で実行ファイルの名前や対応する拡張子が変わっていても残骸を残さないよう、
        // 今の値ではなく、登録時に書いた内容から消す対象を決める。
        string[] registeredExtensions = RegisteredExtensions();
        string? registeredApplicationKey = RegisteredApplicationKeyPath();

        softwareRoot.DeleteSubKeyTree($@"Classes\{ProgId}", throwOnMissingSubKey: false);
        softwareRoot.DeleteSubKeyTree(ApplicationKeyPath, throwOnMissingSubKey: false);
        if (registeredApplicationKey is not null)
        {
            softwareRoot.DeleteSubKeyTree(registeredApplicationKey, throwOnMissingSubKey: false);
        }
        softwareRoot.DeleteSubKeyTree(AppName + @"\Capabilities", throwOnMissingSubKey: false);
        DeleteIfEmpty(softwareRoot, AppName);

        using (RegistryKey? registered = softwareRoot.OpenSubKey("RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        DeleteIfEmpty(softwareRoot, "RegisteredApplications");

        foreach (string extension in extensions.Union(registeredExtensions, StringComparer.OrdinalIgnoreCase))
        {
            using (RegistryKey? openWith = softwareRoot.OpenSubKey($@"Classes\{extension}\OpenWithProgids", writable: true))
            {
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
            DeleteIfEmpty(softwareRoot, $@"Classes\{extension}\OpenWithProgids");
            DeleteIfEmpty(softwareRoot, $@"Classes\{extension}");
        }
        DeleteIfEmpty(softwareRoot, @"Classes\Applications");
        DeleteIfEmpty(softwareRoot, "Classes");
    }

    /// <summary>登録時に書いた拡張子の一覧。</summary>
    private string[] RegisteredExtensions()
    {
        using RegistryKey? associations = softwareRoot.OpenSubKey(AppName + @"\Capabilities\FileAssociations");
        return associations?.GetValueNames().Where(name => name.StartsWith('.')).ToArray() ?? [];
    }

    /// <summary>登録時の実行ファイル名に対応する Applications のキー。登録がない、または読み取れなければ null。</summary>
    private string? RegisteredApplicationKeyPath()
    {
        using RegistryKey? command = softwareRoot.OpenSubKey($@"Classes\{ProgId}\shell\open\command");
        if (command?.GetValue(null) is not string value || !value.StartsWith('"'))
        {
            return null;
        }

        int end = value.IndexOf('"', 1);
        string fileName = end > 1 ? Path.GetFileName(value[1..end]) : "";
        return fileName.Length > 0 ? @"Classes\Applications\" + fileName : null;
    }

    /// <summary>関連付けが変わったことをエクスプローラーに知らせる。登録・解除の後に呼ぶ。</summary>
    public static void NotifyShell() => SHChangeNotify(AssociationChanged, 0, 0, 0);

    private const int AssociationChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>値もサブキーも持たないキーを消す。他のアプリが使っているキーを巻き込まないための確認。</summary>
    private static void DeleteIfEmpty(RegistryKey root, string path)
    {
        using (RegistryKey? key = root.OpenSubKey(path))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
            {
                return;
            }
        }
        root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
