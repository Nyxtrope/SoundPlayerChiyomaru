using System.Reflection;
using System.Runtime.InteropServices;

namespace SoundPlayerChiyomaru;

/// <summary>
/// Windows の DLL を、実行ファイルのフォルダではなく System32 から読む（SPEC 6 章「その他」）。
/// ポータブルなので、展開先に同じ名前の DLL（dwmapi.dll など）が置かれていても、それを読み込まないようにする。
/// 起動時に直接読み込む DLL は、リンカーの /DEPENDENTLOADFLAG で絞っている（プロジェクトファイル）。
/// </summary>
static class SystemDllLoading
{
    private const uint LoadLibrarySearchSystem32 = 0x800;

    /// <summary>ほかの処理より先に呼ぶこと。</summary>
    public static void Apply()
    {
        // 名前だけで読み込む DLL（Windows の部品が中で読むものなど）。失敗しても（古い Windows など）起動は続ける。
        SetDefaultDllDirectories(LoadLibrarySearchSystem32);

        // DllImport の DLL は、Native AOT が名前だけで探す前に「実行ファイルのフォルダ＋名前」のフルパスで読みにいく。
        // フルパスでの読み込みには上の設定が効かないので、System32 にある名前なら System32 のフルパスで先に読む。
        // System32 にない名前（同梱の libSkiaSharp.dll など）は、これまでどおり実行ファイルのフォルダから読まれる。
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                NativeLibrary.SetDllImportResolver(assembly, ResolveFromSystem32);
            }
            catch (InvalidOperationException)
            {
                // ライブラリが自分で設定している。そのライブラリの決め方に任せる。
            }
        }
    }

    private static IntPtr ResolveFromSystem32(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        if (Path.GetFileName(fileName) != fileName)
        {
            return IntPtr.Zero;
        }

        string systemPath = Path.Combine(Environment.SystemDirectory, fileName);
        return File.Exists(systemPath) && NativeLibrary.TryLoad(systemPath, out IntPtr handle) ? handle : IntPtr.Zero;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
