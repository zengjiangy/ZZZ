using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ZZZ.Services;

internal static class PrivateDataGuard
{
    private const string CleanupSwitch = "--zzz-private-cleanup-worker";
    private const string MarkerName = ".zzz-private-profile";
    private static readonly object Gate = new();
    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string WorkerRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZ", "CleanupWorkers");

    public static void ProtectAndWatch(string path)
    {
        lock (Gate)
        {
            if (!Watched.Add(path)) return;
        }

        Directory.CreateDirectory(path);
        ApplyCurrentUserAcl(path);
        try { EncryptFile(path); } catch { }

        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(Path.Combine(path, MarkerName), token, new UTF8Encoding(false)); }
        catch { return; }
        StartCleanupWatchdog(path, token);
    }

    public static async Task<bool> TryRunCleanupWorkerAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], CleanupSwitch, StringComparison.Ordinal)) return false;
        if (args.Length != 6 || !int.TryParse(args[1], out var parentId) ||
            !long.TryParse(args[2], out var parentStartTicks)) return true;

        string target;
        string workerDirectory;
        try
        {
            target = Decode(args[3]);
            workerDirectory = Decode(args[5]);
        }
        catch { return true; }
        var token = args[4];
        if (!IsPrivateProfilePath(target) || token.Length != 64 || token.Any(x => !Uri.IsHexDigit(x))) return true;

        await Task.Run(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks == parentStartTicks) parent.WaitForExit();
            }
            catch { }

            for (var attempt = 0; attempt < 240; attempt++)
            {
                if (!Directory.Exists(target)) break;
                if (!MarkerMatches(target, token)) break;
                try { Directory.Delete(target, true); break; }
                catch { Thread.Sleep(500); }
            }
        });

        ScheduleWorkerSelfCleanup(workerDirectory);
        return true;
    }

    public static void CleanupStaleWorkerCopies()
    {
        try
        {
            if (!Directory.Exists(WorkerRoot)) return;
            foreach (var directory in Directory.GetDirectories(WorkerRoot))
                try { Directory.Delete(directory, true); } catch { }
        }
        catch { }
    }

    public static bool TryDeleteProfile(string path)
    {
        if (!IsPrivateProfilePath(path)) return false;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return !Directory.Exists(path);
        }
        catch { return false; }
    }

    public static bool IsPrivateProfilePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _) &&
                   string.Equals(Path.GetFileName(Path.GetDirectoryName(fullPath)), "Private", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void StartCleanupWatchdog(string target, string token)
    {
        string? workerDirectory = null;
        try
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
            workerDirectory = Path.Combine(WorkerRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workerDirectory);
            var workerExecutable = Path.Combine(workerDirectory, "ZZZ.PrivateCleanup.exe");
            File.Copy(executable, workerExecutable, true);

            using var current = Process.GetCurrentProcess();
            var arguments = string.Join(" ", CleanupSwitch, current.Id.ToString(),
                current.StartTime.ToUniversalTime().Ticks.ToString(), Encode(target), token, Encode(workerDirectory));
            Process.Start(new ProcessStartInfo(workerExecutable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            if (workerDirectory is not null)
                try { Directory.Delete(workerDirectory, true); } catch { }
        }
    }

    private static void ScheduleWorkerSelfCleanup(string workerDirectory)
    {
        try
        {
            var fullWorkerRoot = Path.GetFullPath(WorkerRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullWorkerDirectory = Path.GetFullPath(workerDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!fullWorkerDirectory.StartsWith(fullWorkerRoot, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(fullWorkerDirectory), "N", out _)) return;
            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            Process.Start(new ProcessStartInfo(comspec,
                "/d /q /c ping.exe 127.0.0.1 -n 3 -w 500 >nul & rmdir /s /q \"" + fullWorkerDirectory + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static bool MarkerMatches(string path, string token)
    {
        try { return string.Equals(File.ReadAllText(Path.Combine(path, MarkerName)), token, StringComparison.Ordinal); }
        catch { return false; }
    }

    private static void ApplyCurrentUserAcl(string path)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        catch { }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool EncryptFile(string path);
}
