using System.Reflection;
using System.Runtime.InteropServices;

namespace ZZZ.Services;

internal static class NativeDependencyService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string pathName);

    public static void PrepareWebView2Loader()
    {
        var architecture = Environment.Is64BitProcess ? "x64" : "x86";
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "current";
        var directory = Path.Combine(AppPaths.Root, "Native", version, architecture);
        var path = Path.Combine(directory, "WebView2Loader.dll");
        Directory.CreateDirectory(directory);

        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZZZ.Native.{architecture}.WebView2Loader.dll")
            ?? throw new InvalidOperationException("Embedded WebView2 loader was not found."))
        {
            if (!File.Exists(path) || new FileInfo(path).Length != source.Length)
            {
                var temporaryPath = path + ".tmp";
                using (var destination = File.Create(temporaryPath)) source.CopyTo(destination);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporaryPath, path);
            }
        }

        if (!SetDllDirectory(directory))
            throw new InvalidOperationException("The WebView2 native loader directory could not be registered.");
    }
}
