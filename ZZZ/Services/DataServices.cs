using System.Net;
using System.Net.Http;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;
using ZZZ.Configuration;
using ZZZ.Models;

namespace ZZZ.Services;

public static class AppPaths
{
    private const string LocationFileName = "zzz-data-location.json";
    public static string ExecutableDirectory { get; } = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static string Root { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZ");
    public static DataStorageMode StorageMode { get; private set; } = DataStorageMode.LocalAppData;
    public static string CustomStoragePath { get; private set; } = string.Empty;
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Bookmarks => Path.Combine(Root, "bookmarks.dat");
    public static string History => Path.Combine(Root, "history.dat");
    public static string LegacyBookmarks => Path.Combine(Root, "bookmarks.json");
    public static string LegacyHistory => Path.Combine(Root, "history.json");
    public static string Scripts => Path.Combine(Root, "userscripts.json");
    public static string UserScriptValues => Path.Combine(Root, "userscript-values.json");
    public static string BlockingRules => Path.Combine(Root, "blocking-rules.txt");
    public static string Session => Path.Combine(Root, "session.json");
    public static string Workspaces => Path.Combine(Root, "workspaces.dat");
    public static string Favicons => Path.Combine(Root, "Favicons");
    public static string WebViewData => Path.Combine(Root, "WebView2");
    public static string PrivateWebViewRoot => Path.Combine(Root, "Private");
    public static string LegacyPrivateWebViewRoot => Path.Combine(Path.GetTempPath(), "ZZZ", "Private");

    private static string LocationFile => Path.Combine(ExecutableDirectory, LocationFileName);

    public static void Initialize()
    {
        var config = LoadLocation();
        StorageMode = config.Mode;
        CustomStoragePath = config.CustomPath ?? string.Empty;
        var target = ResolveRoot(StorageMode, CustomStoragePath);

        if (!string.IsNullOrWhiteSpace(config.PendingMigrationFrom) &&
            !SamePath(config.PendingMigrationFrom, target) && Directory.Exists(config.PendingMigrationFrom))
        {
            try
            {
                CopyDirectory(config.PendingMigrationFrom, target);
                config.PendingMigrationFrom = string.Empty;
                SaveLocation(config);
            }
            catch
            {
                // Continue with the selected directory. Existing data remains at
                // the old location so a failed migration is never destructive.
            }
        }

        Root = target;
        Directory.CreateDirectory(Root);
    }

    public static void ScheduleStorageChange(StorageSettings storage)
    {
        var target = ResolveRoot(storage.Mode, storage.CustomPath);
        var currentWithSeparator = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetWithSeparator = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!SamePath(Root, target) && targetWithSeparator.StartsWith(currentWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.Text("NestedDataPathInvalid"));
        Directory.CreateDirectory(target);
        var probe = Path.Combine(target, $".zzz-write-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);

        SaveLocation(new DataLocationConfig
        {
            Mode = storage.Mode,
            CustomPath = storage.CustomPath?.Trim() ?? string.Empty,
            PendingMigrationFrom = SamePath(Root, target) ? string.Empty : Root
        });
    }

    public static string ResolveDataFile(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty
        : Path.IsPathRooted(path) ? path : Path.Combine(Root, path);

    private static DataLocationConfig LoadLocation()
    {
        try
        {
            if (!File.Exists(LocationFile)) return new DataLocationConfig();
            return JsonSerializer.Deserialize<DataLocationConfig>(File.ReadAllText(LocationFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DataLocationConfig();
        }
        catch { return new DataLocationConfig(); }
    }

    private static void SaveLocation(DataLocationConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LocationFile, json);
    }

    private static string ResolveRoot(DataStorageMode mode, string? customPath)
    {
        var path = mode switch
        {
            DataStorageMode.Portable => Path.Combine(ExecutableDirectory, "Data"),
            DataStorageMode.Custom when !string.IsNullOrWhiteSpace(customPath) => Environment.ExpandEnvironmentVariables(customPath!.Trim()),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZ")
        };
        return Path.GetFullPath(path);
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private sealed class DataLocationConfig
    {
        public DataStorageMode Mode { get; set; } = DataStorageMode.LocalAppData;
        public string CustomPath { get; set; } = string.Empty;
        public string PendingMigrationFrom { get; set; } = string.Empty;
    }
}

internal static class JsonFiles
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task<T> LoadAsync<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options) ?? fallback();
        }
        catch { return fallback(); }
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null, true); return; }
                catch (PlatformNotSupportedException) { }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

internal static class ProtectedJsonFiles
{
    private static readonly byte[] Header = [0x5A, 0x5A, 0x5A, 0x21, 0x01];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public static async Task<T> LoadAndMigrateAsync<T>(string path, string legacyPath, string purpose, Func<T> fallback)
    {
        if (File.Exists(path))
        {
            var value = await LoadAsync(path, purpose, fallback);
            if (File.Exists(legacyPath)) ScrubAndDelete(legacyPath);
            return value;
        }
        if (!File.Exists(legacyPath)) return fallback();

        try
        {
            T? value;
            using (var stream = File.OpenRead(legacyPath))
                value = await JsonSerializer.DeserializeAsync<T>(stream, Options);
            if (value is null) return fallback();
            await SaveAsync(path, purpose, value);
            ScrubAndDelete(legacyPath);
            return value;
        }
        catch { return fallback(); }
    }

    public static Task<T> LoadAsync<T>(string path, string purpose, Func<T> fallback) =>
        Task.FromResult(Load(path, purpose, fallback));

    private static T Load<T>(string path, string purpose, Func<T> fallback)
    {
        byte[] plaintext = [];
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            if (protectedBytes.Length <= Header.Length || !Header.SequenceEqual(protectedBytes.Take(Header.Length))) return fallback();
            plaintext = ProtectedData.Unprotect(protectedBytes.Skip(Header.Length).ToArray(), Entropy(purpose), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plaintext, Options) ?? fallback();
        }
        catch { return fallback(); }
        finally { if (plaintext.Length > 0) Array.Clear(plaintext, 0, plaintext.Length); }
    }

    public static async Task SaveAsync<T>(string path, string purpose, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(plaintext, Entropy(purpose), DataProtectionScope.CurrentUser); }
        finally { Array.Clear(plaintext, 0, plaintext.Length); }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Header, 0, Header.Length);
                await stream.WriteAsync(protectedBytes, 0, protectedBytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null, true); return; }
                catch (PlatformNotSupportedException) { }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        finally
        {
            Array.Clear(protectedBytes, 0, protectedBytes.Length);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static byte[] Entropy(string purpose)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes("ZZZ 2.1 protected browser data:" + purpose));
    }

    private static void ScrubAndDelete(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
            var zeros = new byte[8192];
            for (long written = 0; written < length; written += zeros.Length)
                stream.Write(zeros, 0, (int)Math.Min(zeros.Length, length - written));
            stream.Flush(true);
        }
        catch { }
        try { File.Delete(path); } catch { }
    }
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync();
    Task SaveAsync();
    Task ExportAsync(string path);
    Task ImportAsync(string path);
    void Replace(AppSettings settings);
}

public sealed class SettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();
    public async Task LoadAsync()
    {
        Current = await JsonFiles.LoadAsync(AppPaths.Settings, () => new AppSettings());
        Current.Legal ??= new LegalSettings();
        Current.Storage.Mode = AppPaths.StorageMode;
        Current.Storage.CustomPath = AppPaths.CustomStoragePath;
        if (Current.Advanced.ForceDarkWebContent)
        {
            Current.Advanced.WebDarkMode = WebContentDarkMode.Force;
            Current.Advanced.ForceDarkWebContent = false;
        }
        else if (Current.Advanced.WebDarkMode == WebContentDarkMode.Off)
        {
            // Web content now follows the application theme. Keep only the
            // preferred dark rendering strength in settings.
            Current.Advanced.WebDarkMode = WebContentDarkMode.Smart;
        }
    }
    public Task SaveAsync() => JsonFiles.SaveAsync(AppPaths.Settings, Current);
    public Task ExportAsync(string path) => JsonFiles.SaveAsync(path, Current);
    public void Replace(AppSettings settings) => Current = settings;
    public async Task ImportAsync(string path)
    {
        Current = await JsonFiles.LoadAsync(path, () => Current);
        await SaveAsync();
    }
}

public sealed class WorkspaceService
{
    private static readonly string[] Palette = ["#6557C8", "#2F7ED8", "#1B9A75", "#D9822B", "#D64F70", "#7A5AF8", "#148A9C", "#A66B1F"];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public ObservableCollection<WorkspaceDefinition> Items { get; } = [];
    public string ActiveWorkspaceId { get; private set; } = string.Empty;

    public WorkspaceDefinition Active => Find(ActiveWorkspaceId) ?? Items.First();

    public void ApplyLocalizedDefaults()
    {
        var workspace = Find("default");
        if (workspace is not null && string.Equals(workspace.Name, "DefaultWorkspace", StringComparison.Ordinal))
            workspace.Name = LocalizationService.Text("DefaultWorkspace");
    }

    public async Task LoadAsync()
    {
        var store = await ProtectedJsonFiles.LoadAsync(AppPaths.Workspaces, "workspaces", () => new WorkspaceStore());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in store.Items.Take(12))
        {
            workspace.Id = NormalizeId(workspace.Id, seen);
            workspace.Name = NormalizeName(workspace.Name, Items.Count + 1);
            workspace.Color = NormalizeColor(workspace.Color, Items.Count);
            workspace.SavedUrls = NormalizeUrls(workspace.SavedUrls).Take(50).ToList();
            Items.Add(workspace);
        }
        if (Items.Count == 0) Items.Add(CreateDefault());
        ActiveWorkspaceId = Find(store.ActiveWorkspaceId)?.Id ?? Items[0].Id;
    }

    public WorkspaceDefinition Create(string? requestedName)
    {
        if (Items.Count >= 12) return Active;
        var workspace = new WorkspaceDefinition
        {
            Name = NormalizeName(requestedName, Items.Count + 1),
            Color = Palette[Items.Count % Palette.Length]
        };
        Items.Add(workspace);
        ActiveWorkspaceId = workspace.Id;
        return workspace;
    }

    public bool Remove(WorkspaceDefinition workspace)
    {
        if (Items.Count <= 1 || !Items.Remove(workspace)) return false;
        if (string.Equals(ActiveWorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)) ActiveWorkspaceId = Items[0].Id;
        return true;
    }

    public WorkspaceDefinition? Find(string? id) => Items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public void SetActive(string id)
    {
        if (Find(id) is not null) ActiveWorkspaceId = id;
    }

    public IReadOnlyList<WorkspaceTabSnapshot> RestoreTabs()
    {
        return Items.SelectMany(workspace => NormalizeUrls(workspace.SavedUrls).Take(50).Select(url => new WorkspaceTabSnapshot
        {
            WorkspaceId = workspace.Id,
            Url = url
        })).Take(150).ToArray();
    }

    public Task SaveMetadataAsync() => PersistAsync();

    public async Task SaveSnapshotAsync(IEnumerable<(string Url, bool IsPrivate, string WorkspaceId)> tabs, string activeWorkspaceId)
    {
        var publicTabs = tabs.Where(x => !x.IsPrivate && IsRestorableUrl(x.Url)).ToArray();
        foreach (var workspace in Items)
            workspace.SavedUrls = publicTabs.Where(x => string.Equals(x.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)).Select(x => x.Url).Take(50).ToList();
        SetActive(activeWorkspaceId);
        await PersistAsync();
    }

    public async Task ClearTabsAsync()
    {
        foreach (var workspace in Items) workspace.SavedUrls.Clear();
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            await ProtectedJsonFiles.SaveAsync(AppPaths.Workspaces, "workspaces", new WorkspaceStore
            {
                ActiveWorkspaceId = ActiveWorkspaceId,
                Items = Items.ToList()
            });
        }
        finally { _saveGate.Release(); }
    }

    private static WorkspaceDefinition CreateDefault() => new()
    {
        Id = "default",
        Name = "DefaultWorkspace",
        Color = Palette[0]
    };

    private static string NormalizeId(string? id, HashSet<string> seen)
    {
        var candidate = id ?? string.Empty;
        var value = !string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 64 ? candidate.Trim() : Guid.NewGuid().ToString("N");
        while (!seen.Add(value)) value = Guid.NewGuid().ToString("N");
        return value;
    }

    private static string NormalizeName(string? name, int ordinal)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0) value = $"{LocalizationService.Text("Workspace")} {ordinal}";
        return value.Length > 40 ? value.Substring(0, 40) : value;
    }

    private static string NormalizeColor(string? color, int ordinal) =>
        !string.IsNullOrWhiteSpace(color) && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") ? color! : Palette[ordinal % Palette.Length];

    private static IEnumerable<string> NormalizeUrls(IEnumerable<string>? urls) =>
        (urls ?? []).Where(IsRestorableUrl);

    private static bool IsRestorableUrl(string url) => !BrowserHome.IsStartPage(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file";

    private sealed class WorkspaceStore
    {
        public string ActiveWorkspaceId { get; set; } = string.Empty;
        public List<WorkspaceDefinition> Items { get; set; } = [];
    }
}

public interface IBookmarkService
{
    event EventHandler? Changed;
    IReadOnlyList<Bookmark> Items { get; }
    Task LoadAsync();
    Task AddAsync(string title, string url);
    Task RemoveAsync(Bookmark item);
    Task SaveAsync();
    Task<bool> ToggleAsync(string title, string url);
    bool Contains(string url);
    Task ExportHtmlAsync(string path);
    Task ImportHtmlAsync(string path);
}

public sealed class BookmarkService : IBookmarkService
{
    private List<Bookmark> _items = [];
    public event EventHandler? Changed;
    public IReadOnlyList<Bookmark> Items => _items;
    public async Task LoadAsync() => _items = await ProtectedJsonFiles.LoadAndMigrateAsync(AppPaths.Bookmarks, AppPaths.LegacyBookmarks, "bookmarks", () => new List<Bookmark>());
    public async Task AddAsync(string title, string url)
    {
        if (Contains(url)) return;
        _items.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
        await ProtectedJsonFiles.SaveAsync(AppPaths.Bookmarks, "bookmarks", _items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task RemoveAsync(Bookmark item)
    {
        if (!_items.Remove(item)) return;
        await ProtectedJsonFiles.SaveAsync(AppPaths.Bookmarks, "bookmarks", _items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task SaveAsync()
    {
        await ProtectedJsonFiles.SaveAsync(AppPaths.Bookmarks, "bookmarks", _items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public bool Contains(string url) => _items.Any(x => SameUrl(x.Url, url));
    public async Task<bool> ToggleAsync(string title, string url)
    {
        var existing = _items.FirstOrDefault(x => SameUrl(x.Url, url));
        if (existing is null)
            _items.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
        else
            _items.Remove(existing);
        await ProtectedJsonFiles.SaveAsync(AppPaths.Bookmarks, "bookmarks", _items);
        Changed?.Invoke(this, EventArgs.Empty);
        return existing is null;
    }
    public async Task ExportHtmlAsync(string path)
    {
        var html = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>ZZZ Bookmarks</TITLE>\n<H1>ZZZ Bookmarks</H1>\n<DL><p>\n");
        foreach (var item in _items.Where(x => string.IsNullOrWhiteSpace(x.Group))) AppendBookmarkHtml(html, item, "    ");
        foreach (var group in _items.Where(x => !string.IsNullOrWhiteSpace(x.Group)).GroupBy(x => x.Group.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            html.Append("    <DT><H3>").Append(WebUtility.HtmlEncode(group.Key)).Append("</H3>\n    <DL><p>\n");
            foreach (var item in group) AppendBookmarkHtml(html, item, "        ");
            html.Append("    </DL><p>\n");
        }
        html.Append("</DL><p>\n");
        File.WriteAllText(path, html.ToString(), Encoding.UTF8);
        await Task.CompletedTask;
    }
    public async Task ImportHtmlAsync(string path)
    {
        var html = File.ReadAllText(path);
        var changed = false;
        var folders = new List<string>();
        string? pendingFolder = null;
        const string tokenPattern = "<H3\\b[^>]*>(?<folder>.*?)</H3>|<A\\s+[^>]*HREF=[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</A>|(?<open><DL\\b[^>]*>)|(?<close></DL\\s*>)";
        foreach (Match match in Regex.Matches(html, tokenPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (match.Groups["folder"].Success)
            {
                pendingFolder = CleanHtmlText(match.Groups["folder"].Value);
                continue;
            }
            if (match.Groups["open"].Success)
            {
                folders.Add(pendingFolder ?? string.Empty);
                pendingFolder = null;
                continue;
            }
            if (match.Groups["close"].Success)
            {
                if (folders.Count > 0) folders.RemoveAt(folders.Count - 1);
                continue;
            }
            if (!match.Groups["url"].Success) continue;
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var title = CleanHtmlText(match.Groups["title"].Value);
            var group = string.Join(" / ", folders.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!Contains(url)) { _items.Add(new Bookmark { Title = title, Url = url, Group = group }); changed = true; }
        }
        await ProtectedJsonFiles.SaveAsync(AppPaths.Bookmarks, "bookmarks", _items);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void AppendBookmarkHtml(StringBuilder html, Bookmark item, string indent) =>
        html.Append(indent).Append("<DT><A HREF=\"").Append(WebUtility.HtmlEncode(item.Url)).Append("\">")
            .Append(WebUtility.HtmlEncode(item.Title)).Append("</A>\n");

    private static string CleanHtmlText(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value, "<.*?>", string.Empty)).Trim();

    private static bool SameUrl(string left, string right)
    {
        if (Uri.TryCreate(left, UriKind.Absolute, out var leftUri) && Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
            return Uri.Compare(leftUri, rightUri, UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
        return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}

public interface IHistoryService
{
    IReadOnlyList<HistoryEntry> Items { get; }
    void StopRecording();
    Task LoadAsync();
    Task AddAsync(string title, string url);
    Task RemoveAsync(HistoryEntry item);
    Task ClearAsync();
}

public sealed class HistoryService : IHistoryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<HistoryEntry> _items = [];
    private volatile bool _recordingEnabled = true;
    public IReadOnlyList<HistoryEntry> Items => _items;
    public void StopRecording() => _recordingEnabled = false;
    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try { _items = await ProtectedJsonFiles.LoadAndMigrateAsync(AppPaths.History, AppPaths.LegacyHistory, "history", () => new List<HistoryEntry>()); }
        finally { _gate.Release(); }
    }
    public async Task AddAsync(string title, string url)
    {
        if (!_recordingEnabled || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
        await _gate.WaitAsync();
        try
        {
            if (!_recordingEnabled) return;
            _items.Insert(0, new HistoryEntry { Title = title, Url = url });
            if (_items.Count > 3000) _items.RemoveRange(3000, _items.Count - 3000);
            await ProtectedJsonFiles.SaveAsync(AppPaths.History, "history", _items);
        }
        finally { _gate.Release(); }
    }
    public async Task RemoveAsync(HistoryEntry item)
    {
        await _gate.WaitAsync();
        try { if (_items.Remove(item)) await ProtectedJsonFiles.SaveAsync(AppPaths.History, "history", _items); }
        finally { _gate.Release(); }
    }
    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try { _items.Clear(); await ProtectedJsonFiles.SaveAsync(AppPaths.History, "history", _items); }
        finally { _gate.Release(); }
    }
}

public sealed class SessionService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _requestedRevision;
    public IReadOnlyList<string> Urls { get; private set; } = [];
    public async Task LoadAsync()
    {
        var saved = await JsonFiles.LoadAsync(AppPaths.Session, () => new List<string>());
        Urls = BuildSnapshot(saved.Select(x => (x, false)));
        // Recover the newest complete temporary snapshot after a forced process
        // termination. Partial JSON is ignored and the previous complete file
        // remains valid because commits use File.Replace.
        foreach (var candidate in TemporaryFiles().Where(x => !File.Exists(AppPaths.Session) || File.GetLastWriteTimeUtc(x) > File.GetLastWriteTimeUtc(AppPaths.Session)).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var stream = File.OpenRead(candidate);
                var recovered = await JsonSerializer.DeserializeAsync<List<string>>(stream);
                if (recovered is not null)
                {
                    Urls = BuildSnapshot(recovered.Select(x => (x, false)));
                    await SaveAsync(Urls.Select(x => (x, false)));
                    break;
                }
            }
            catch { }
        }
        DeleteTemporaryFiles();
    }
    public async Task SaveAsync(IEnumerable<(string Url, bool IsPrivate)> tabs)
    {
        var snapshot = BuildSnapshot(tabs);
        var revision = Interlocked.Increment(ref _requestedRevision);
        await _saveGate.WaitAsync();
        try
        {
            // A newer snapshot is already waiting. Skipping this write prevents a
            // slow older save from becoming the next process' restore state.
            if (revision != Volatile.Read(ref _requestedRevision)) return;
            await JsonFiles.SaveAsync(AppPaths.Session, snapshot);
            Urls = snapshot;
            TryDelete(AppPaths.Session + ".tmp");
        }
        finally { _saveGate.Release(); }
    }

    public async Task ClearAsync()
    {
        Interlocked.Increment(ref _requestedRevision);
        await _saveGate.WaitAsync();
        try
        {
            // First replace any existing content with an empty durable snapshot.
            // Even if antivirus software temporarily prevents deletion, no URLs
            // remain on disk after the setting is switched off.
            try { await JsonFiles.SaveAsync(AppPaths.Session, new List<string>()); } catch { }
            ScrubAndDelete(AppPaths.Session);
            DeleteTemporaryFiles();
            Urls = [];
        }
        finally { _saveGate.Release(); }
    }

    public static List<string> BuildSnapshot(IEnumerable<(string Url, bool IsPrivate)> tabs) => tabs
        .Where(x => !x.IsPrivate && !BrowserHome.IsStartPage(x.Url) && Uri.TryCreate(x.Url, UriKind.Absolute, out _))
        .Select(x => x.Url)
        .Take(50)
        .ToList();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void DeleteTemporaryFiles()
    {
        foreach (var path in TemporaryFiles()) ScrubAndDelete(path);
    }

    private static IEnumerable<string> TemporaryFiles()
    {
        var legacy = AppPaths.Session + ".tmp";
        if (File.Exists(legacy)) yield return legacy;
        var directory = Path.GetDirectoryName(AppPaths.Session)!;
        if (!Directory.Exists(directory)) yield break;
        string[] files;
        try { files = Directory.GetFiles(directory, Path.GetFileName(AppPaths.Session) + ".*.tmp"); }
        catch { yield break; }
        foreach (var path in files) yield return path;
    }

    private static void ScrubAndDelete(string path)
    {
        try { if (File.Exists(path)) File.WriteAllText(path, "[]", new UTF8Encoding(false)); } catch { }
        TryDelete(path);
    }
}

public interface IUserScriptService
{
    IReadOnlyList<UserScript> Items { get; }
    Task LoadAsync();
    Task SaveAsync(IEnumerable<UserScript> items);
    Task<UserScript> ImportAsync(string pathOrUrl);
    UserScript Parse(string source, string sourceUrl = "");
    IEnumerable<UserScript> Matching(string url);
}

public sealed class UserScriptService : IUserScriptService
{
    private List<UserScript> _items = [];
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex MetadataLine = new(@"^\s*//\s*@(?<key>[\w-]+)\s*(?<value>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public IReadOnlyList<UserScript> Items => _items;
    public async Task LoadAsync()
    {
        _items = await JsonFiles.LoadAsync(AppPaths.Scripts, () => new List<UserScript>());
        foreach (var script in _items) Normalize(script);
    }

    public async Task SaveAsync(IEnumerable<UserScript> items)
    {
        _items = [];
        foreach (var item in items)
        {
            Normalize(item);
            var script = item;
            if (item.Code.IndexOf("==UserScript==", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var parsed = Parse(item.Code, item.SourceUrl);
                parsed.Id = item.Id;
                parsed.Enabled = item.Enabled;
                if (parsed.Requires.SequenceEqual(item.Requires, StringComparer.OrdinalIgnoreCase)) parsed.RequiredCode = item.RequiredCode;
                script = parsed;
            }
            Normalize(script);
            if (script.Requires.Count > 0 && string.IsNullOrWhiteSpace(script.RequiredCode)) await DownloadRequiresAsync(script);
            _items.Add(script);
        }
        await JsonFiles.SaveAsync(AppPaths.Scripts, _items);
    }

    public async Task<UserScript> ImportAsync(string pathOrUrl)
    {
        string source;
        string origin;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            source = await Client.GetStringAsync(uri);
            origin = uri.AbsoluteUri;
        }
        else
        {
            source = File.ReadAllText(pathOrUrl, Encoding.UTF8);
            origin = new Uri(Path.GetFullPath(pathOrUrl)).AbsoluteUri;
        }

        if (source.Length > 4 * 1024 * 1024) throw new InvalidDataException("Userscript is larger than 4 MB.");
        var script = Parse(source, origin);
        if (script.Requires.Count > 0) await DownloadRequiresAsync(script);

        var existing = _items.FirstOrDefault(x => !string.IsNullOrWhiteSpace(script.Namespace) && x.Namespace == script.Namespace && x.Name == script.Name);
        if (existing is not null) _items[_items.IndexOf(existing)] = script; else _items.Add(script);
        await JsonFiles.SaveAsync(AppPaths.Scripts, _items);
        return script;
    }

    public UserScript Parse(string source, string sourceUrl = "")
    {
        var script = new UserScript { Code = source, SourceUrl = sourceUrl };
        var start = source.IndexOf("==UserScript==", StringComparison.OrdinalIgnoreCase);
        var end = source.IndexOf("==/UserScript==", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end <= start) return script;

        using var reader = new StringReader(source.Substring(start, end - start));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var match = MetadataLine.Match(line);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value.ToLowerInvariant();
            var value = match.Groups["value"].Value.Trim();
            switch (key)
            {
                case "name": if (value.Length > 0) script.Name = value; break;
                case "namespace": script.Namespace = value; break;
                case "version": script.Version = value; break;
                case "description": script.Description = value; break;
                case "match": if (value.Length > 0) script.Matches.Add(value); break;
                case "include": if (value.Length > 0) script.Includes.Add(value); break;
                case "exclude": if (value.Length > 0) script.Excludes.Add(value); break;
                case "require": if (value.Length > 0) script.Requires.Add(value); break;
                case "grant": if (value.Length > 0) script.Grants.Add(value); break;
                case "connect": if (value.Length > 0) script.Connects.Add(value); break;
                case "run-at": if (value.Length > 0) script.RunAt = value; break;
                case "noframes": script.NoFrames = true; break;
                case "resource":
                    var split = value.IndexOfAny(new[] { ' ', '\t' });
                    if (split > 0) script.Resources[value.Substring(0, split)] = value.Substring(split).Trim();
                    break;
            }
        }
        script.Match = script.Matches.FirstOrDefault() ?? script.Includes.FirstOrDefault() ?? "*";
        Normalize(script);
        return script;
    }

    public IEnumerable<UserScript> Matching(string url) => _items.Where(x => x.Enabled && UserScriptMatcher.IsMatch(x, url));

    private static void Normalize(UserScript script)
    {
        if (string.IsNullOrWhiteSpace(script.Id)) script.Id = Guid.NewGuid().ToString("N");
        script.Matches ??= [];
        script.Includes ??= [];
        script.Excludes ??= [];
        script.Requires ??= [];
        script.Grants ??= [];
        script.Connects ??= [];
        script.Resources ??= [];
        if (string.IsNullOrWhiteSpace(script.Match)) script.Match = "*";
        if (string.IsNullOrWhiteSpace(script.RunAt)) script.RunAt = "document-idle";
    }

    private static async Task DownloadRequiresAsync(UserScript script)
    {
        var required = new StringBuilder();
        foreach (var require in script.Requires.Take(16))
        {
            if (!Uri.TryCreate(require, UriKind.Absolute, out var requireUri) || (requireUri.Scheme != Uri.UriSchemeHttp && requireUri.Scheme != Uri.UriSchemeHttps)) continue;
            var code = await Client.GetStringAsync(requireUri);
            if (required.Length + code.Length > 4 * 1024 * 1024) throw new InvalidDataException("Combined @require content is larger than 4 MB.");
            required.AppendLine(code).AppendLine($"//# sourceURL={requireUri.AbsoluteUri}");
        }
        script.RequiredCode = required.ToString();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "Windows NT 10.0",
            Architecture.Arm64 => "Windows NT 10.0; ARM64",
            _ => "Windows NT 10.0; Win64; x64"
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 ({platform}) AppleWebKit/537.36 Chrome/124.0 Safari/537.36 ZZZ/2.2.1");
        return client;
    }
}

public static class UserScriptMatcher
{
    public static bool IsMatch(UserScript script, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile)) return false;
        var includes = script.Matches.Concat(script.Includes).ToArray();
        if (includes.Length == 0) includes = new[] { script.Match };
        return includes.Any(x => MatchPattern(x, url)) && !script.Excludes.Any(x => MatchPattern(x, url));
    }

    public static bool MatchPattern(string pattern, string url)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*" || pattern.Equals("<all_urls>", StringComparison.OrdinalIgnoreCase)) return true;
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*://", @"https?://")
            .Replace(@"\*\.", @"(?:[^/]+\.)?")
            .Replace(@"\*", ".*") + "$";
        try { return Regex.IsMatch(url, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        catch { return false; }
    }
}

public interface IAdBlockService
{
    bool ShouldBlock(string url);
    Task LoadAsync();
    Task ImportAsync(string path);
    Task ExportAsync(string path);
}

public sealed class AdBlockService : IAdBlockService
{
    private AdBlockRuleSet _rules = new(Array.Empty<string>());
    private static readonly string[] Defaults = ["||doubleclick.net^", "||googlesyndication.com^", "||googleadservices.com^", "||adnxs.com^", "tracking.*", "/ads/", "/advertising/"];
    public async Task LoadAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (!File.Exists(AppPaths.BlockingRules)) File.WriteAllLines(AppPaths.BlockingRules, Defaults);
        _rules = new AdBlockRuleSet(File.ReadAllLines(AppPaths.BlockingRules));
        await Task.CompletedTask;
    }
    public bool ShouldBlock(string url) => _rules.ShouldBlock(url);
    public async Task ImportAsync(string path) { File.Copy(path, AppPaths.BlockingRules, true); await LoadAsync(); }
    public Task ExportAsync(string path) { File.Copy(AppPaths.BlockingRules, path, true); return Task.CompletedTask; }
}

public sealed class AdBlockRuleSet
{
    private readonly IReadOnlyList<Regex> _blocking;
    private readonly IReadOnlyList<Regex> _exceptions;

    public AdBlockRuleSet(IEnumerable<string> rules)
    {
        var blocking = new List<Regex>();
        var exceptions = new List<Regex>();
        foreach (var raw in rules.Take(200_000))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("!", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal) ||
                line.StartsWith("[", StringComparison.Ordinal) || line.Contains("##") || line.Contains("#@#")) continue;
            var isException = line.StartsWith("@@", StringComparison.Ordinal);
            if (isException) line = line.Substring(2);
            var optionsAt = line.LastIndexOf('$');
            if (optionsAt > 0) line = line.Substring(0, optionsAt);
            var regex = Compile(line);
            if (regex is null) continue;
            (isException ? exceptions : blocking).Add(regex);
        }
        _blocking = blocking;
        _exceptions = exceptions;
    }

    public bool ShouldBlock(string url)
    {
        if (_exceptions.Any(x => x.IsMatch(url))) return false;
        return _blocking.Any(x => x.IsMatch(url));
    }

    private static Regex? Compile(string rule)
    {
        if (rule.Length == 0) return null;
        try
        {
            if (rule.Length > 2 && rule[0] == '/' && rule[rule.Length - 1] == '/')
                return new Regex(rule.Substring(1, rule.Length - 2), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

            var domainAnchor = rule.StartsWith("||", StringComparison.Ordinal);
            var startAnchor = !domainAnchor && rule.StartsWith("|", StringComparison.Ordinal);
            var endAnchor = rule.EndsWith("|", StringComparison.Ordinal);
            if (domainAnchor) rule = rule.Substring(2);
            else if (startAnchor) rule = rule.Substring(1);
            if (endAnchor && rule.Length > 0) rule = rule.Substring(0, rule.Length - 1);

            var pattern = Regex.Escape(rule)
                .Replace("\\*", ".*")
                .Replace("\\^", "(?:[^A-Za-z0-9_.%-]|$)");
            if (domainAnchor) pattern = @"^(?:[^:/?#]+:)?//(?:[^/?#]*\.)?" + pattern;
            else if (startAnchor) pattern = "^" + pattern;
            if (endAnchor) pattern += "$";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        }
        catch { return null; }
    }
}

public static class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    public static bool IsDarkTheme { get; private set; }
    public static bool IsGrayscaleMode { get; private set; }

    public static void Apply(AppearanceMode mode, StartPageSettings? startPage = null, bool grayscale = false)
    {
        IsDarkTheme = mode == AppearanceMode.Dark || (mode == AppearanceMode.System && IsSystemDark());
        IsGrayscaleMode = grayscale;
        var dark = IsDarkTheme;
        Set("WindowBrush", dark ? "#FF17151D" : "#FFF6F5FA");
        Set("ChromeBrush", dark ? "#FF1E1B27" : "#FFF0EFF6");
        Set("SurfaceBrush", dark ? "#FF27232F" : "#FFFFFFFF");
        Set("SurfaceAltBrush", dark ? "#FF211E29" : "#FFFAF9FC");
        Set("ToolbarBrush", dark ? "#F526222F" : "#F7FFFFFF");
        Set("AddressBrush", dark ? "#FF302B39" : "#FFF4F3F8");
        Set("TextBrush", dark ? "#FFF4F1FA" : "#FF1D1A24");
        Set("MutedBrush", dark ? "#FFB9B2C4" : "#FF6E6978");
        Set("LineBrush", dark ? "#FF403A49" : "#FFE1DFE8");
        var defaultAccent = ParseColor(dark ? "#FFA99BFF" : "#FF6B5ADA", System.Windows.Media.Color.FromRgb(107, 90, 218));
        var accent = startPage?.SyncApplicationAccent == true
            ? ParseColor(startPage.BackgroundColor, defaultAccent)
            : defaultAccent;
        accent.A = 255;
        var softTarget = dark ? System.Windows.Media.Color.FromRgb(39, 35, 47) : System.Windows.Media.Colors.White;
        accent = GrayscaleIfNeeded(accent);
        defaultAccent = GrayscaleIfNeeded(defaultAccent);
        Set("AccentBrush", accent);
        var accentSoft = Blend(accent, GrayscaleIfNeeded(softTarget), dark ? 0.68 : 0.82);
        Set("AccentSoftBrush", accentSoft);
        var blackContrast = ContrastRatio(System.Windows.Media.Colors.Black, accent);
        var whiteContrast = ContrastRatio(System.Windows.Media.Colors.White, accent);
        Set("AccentForegroundBrush", blackContrast >= whiteContrast ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White);
        Set("AccentTextBrush", AccessibleAccentText(accent, accentSoft, dark));
        Set("AppIconBrush", defaultAccent);
        Set("HoverBrush", dark ? "#16FFFFFF" : "#0D241C35");
        Set("PressedBrush", dark ? "#26FFFFFF" : "#18241C35");
        Set("WebBackdropBrush", dark ? "#FF111016" : "#FFFFFFFF");
        foreach (Window window in Application.Current.Windows) ApplyWindowChrome(window);
    }

    public static WebContentDarkMode EffectiveWebDarkMode(AppSettings settings) =>
        IsDarkTheme
            ? settings.Advanced.WebDarkMode == WebContentDarkMode.Force ? WebContentDarkMode.Force : WebContentDarkMode.Smart
            : WebContentDarkMode.Off;

    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var enabled = IsDarkTheme ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
        catch { }
    }

    private static void Set(string key, string color) => Set(key, ParseColor(color, System.Windows.Media.Colors.Transparent));
    private static void Set(string key, System.Windows.Media.Color color) => Application.Current.Resources[key] = new System.Windows.Media.SolidColorBrush(GrayscaleIfNeeded(color));
    private static System.Windows.Media.Color ParseColor(string? value, System.Windows.Media.Color fallback)
    {
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }
    private static System.Windows.Media.Color Blend(System.Windows.Media.Color color, System.Windows.Media.Color target, double targetWeight)
    {
        static byte Channel(byte source, byte destination, double weight) => (byte)Math.Round(source * (1 - weight) + destination * weight);
        return System.Windows.Media.Color.FromRgb(Channel(color.R, target.R, targetWeight), Channel(color.G, target.G, targetWeight), Channel(color.B, target.B, targetWeight));
    }
    private static double RelativeLuminance(System.Windows.Media.Color color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
    private static System.Windows.Media.Color AccessibleAccentText(System.Windows.Media.Color accent, System.Windows.Media.Color background, bool dark)
    {
        if (ContrastRatio(accent, background) >= 4.5) return accent;
        var target = dark ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black;
        for (var step = 1; step <= 20; step++)
        {
            var candidate = Blend(accent, target, step / 20d);
            if (ContrastRatio(candidate, background) >= 4.5) return candidate;
        }
        return target;
    }
    private static double ContrastRatio(System.Windows.Media.Color left, System.Windows.Media.Color right)
    {
        var a = RelativeLuminance(left);
        var b = RelativeLuminance(right);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static System.Windows.Media.Color GrayscaleIfNeeded(System.Windows.Media.Color color)
    {
        if (!IsGrayscaleMode) return color;
        var gray = (byte)Math.Round(0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B);
        return System.Windows.Media.Color.FromArgb(color.A, gray, gray, gray);
    }
    public static System.Drawing.Color WebBackgroundColor()
    {
        var color = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["WebBackdropBrush"]).Color;
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }
}

public interface IPrivacyService
{
    void AttachProfile(CoreWebView2Profile profile);
    Task ClearAsync(ClearDataSelection selection);
    Task ClearOnExitAsync();
}

public sealed class PrivacyService(ISettingsService settings, IHistoryService history) : IPrivacyService
{
    private readonly HashSet<CoreWebView2Profile> _profiles = [];
    public void AttachProfile(CoreWebView2Profile profile) => _profiles.Add(profile);
    public async Task ClearAsync(ClearDataSelection s)
    {
        if (s.History) await history.ClearAsync();
        var kinds = (CoreWebView2BrowsingDataKinds)0;
        if (s.Cache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
        if (s.Cookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
        if (s.Passwords) kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;
        if (s.FormData) kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill;
        if (kinds == 0) return;
        foreach (var profile in _profiles.ToArray())
            try { await profile.ClearBrowsingDataAsync(kinds); } catch { }
    }
    public Task ClearOnExitAsync() => settings.Current.Privacy.ClearOnExit ? ClearAsync(settings.Current.Privacy.ClearOnExitItems) : Task.CompletedTask;
}
