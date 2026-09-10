using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace UnzipTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        NativeBootstrap.ExtractSevenZip();
        base.OnStartup(e);
    }
}

/// <summary>Extracts the embedded 7z.dll so 7z.dll COM bindings work even from a
/// single-file self-contained publish (where no 7z.dll sits next to the exe).</summary>
internal static class NativeBootstrap
{
    private static bool _done;

    public static void ExtractSevenZip()
    {
        if (_done) return;
        _done = true;

        try
        {
            // Dev / classic build: 7z.dll already sits next to the exe.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "7z.dll")))
                return;

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteadyZip");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "7z.dll");

            if (!File.Exists(target))
            {
                using var src = typeof(App).Assembly.GetManifestResourceStream("UnzipTool.App.sevenzip_native");
                if (src is null)
                    return;
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }

            SetDllDirectory(dir);
        }
        catch
        {
            // Best-effort: if this fails, 7z.dll-dependent features report "unavailable".
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
