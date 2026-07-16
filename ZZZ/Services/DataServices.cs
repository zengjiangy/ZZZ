using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
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
    public static string Bookmarks => Path.Combine(Root, "bookmarks.json");
    public static string History => Path.Combine(Root, "history.json");
    public static string Scripts => Path.Combine(Root, "userscripts.json");
    public static string UserScriptValues => Path.Combine(Root, "userscript-values.json");
    public static string BlockingRules => Path.Combine(Root, "blocking-rules.txt");
    public static string Session => Path.Combine(Root, "session.json");
    public static string WebViewData => Path.Combine(Root, "WebView2");
    public static string PrivateWebViewRoot => Path.Combine(Path.GetTempPath(), "ZZZ", "Private");

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
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, value, Options);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
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
    public async Task LoadAsync() => _items = await JsonFiles.LoadAsync(AppPaths.Bookmarks, () => new List<Bookmark>());
    public async Task AddAsync(string title, string url)
    {
        if (Contains(url)) return;
        _items.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
        await JsonFiles.SaveAsync(AppPaths.Bookmarks, _items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task RemoveAsync(Bookmark item)
    {
        if (!_items.Remove(item)) return;
        await JsonFiles.SaveAsync(AppPaths.Bookmarks, _items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task SaveAsync()
    {
        await JsonFiles.SaveAsync(AppPaths.Bookmarks, _items);
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
        await JsonFiles.SaveAsync(AppPaths.Bookmarks, _items);
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
        await JsonFiles.SaveAsync(AppPaths.Bookmarks, _items);
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
    Task LoadAsync();
    Task AddAsync(string title, string url);
    Task RemoveAsync(HistoryEntry item);
    Task ClearAsync();
}

public sealed class HistoryService : IHistoryService
{
    private List<HistoryEntry> _items = [];
    public IReadOnlyList<HistoryEntry> Items => _items;
    public async Task LoadAsync() => _items = await JsonFiles.LoadAsync(AppPaths.History, () => new List<HistoryEntry>());
    public async Task AddAsync(string title, string url)
    {
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
        _items.Insert(0, new HistoryEntry { Title = title, Url = url });
        if (_items.Count > 3000) _items.RemoveRange(3000, _items.Count - 3000);
        await JsonFiles.SaveAsync(AppPaths.History, _items);
    }
    public async Task RemoveAsync(HistoryEntry item)
    {
        if (_items.Remove(item)) await JsonFiles.SaveAsync(AppPaths.History, _items);
    }
    public async Task ClearAsync() { _items.Clear(); await JsonFiles.SaveAsync(AppPaths.History, _items); }
}

public sealed class SessionService
{
    public IReadOnlyList<string> Urls { get; private set; } = [];
    public async Task LoadAsync() => Urls = await JsonFiles.LoadAsync(AppPaths.Session, () => new List<string>());
    public Task SaveAsync(IEnumerable<string> urls) => JsonFiles.SaveAsync(AppPaths.Session, urls.Where(x => Uri.TryCreate(x, UriKind.Absolute, out _)).Distinct().Take(50).ToList());
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36 ZZZ/1.7.1");
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
    private string[] _rules = [];
    private static readonly string[] Defaults = ["doubleclick.net", "googlesyndication.com", "googleadservices.com", "adnxs.com", "tracking.*", "/ads/", "/advertising/"];
    public async Task LoadAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (!File.Exists(AppPaths.BlockingRules)) File.WriteAllLines(AppPaths.BlockingRules, Defaults);
        _rules = File.ReadAllLines(AppPaths.BlockingRules).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("!", StringComparison.Ordinal) && !x.StartsWith("#", StringComparison.Ordinal)).ToArray();
        await Task.CompletedTask;
    }
    public bool ShouldBlock(string url)
    {
        foreach (var rule in _rules)
        {
            if (rule.Contains('*') && Regex.IsMatch(url, Regex.Escape(rule).Replace("\\*", ".*"), RegexOptions.IgnoreCase)) return true;
            if (!rule.Contains('*') && url.IndexOf(rule, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }
    public async Task ImportAsync(string path) { File.Copy(path, AppPaths.BlockingRules, true); await LoadAsync(); }
    public Task ExportAsync(string path) { File.Copy(AppPaths.BlockingRules, path, true); return Task.CompletedTask; }
}

public static class ThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    public static bool IsDarkTheme { get; private set; }

    public static void Apply(AppearanceMode mode)
    {
        IsDarkTheme = mode == AppearanceMode.Dark || (mode == AppearanceMode.System && IsSystemDark());
        var dark = IsDarkTheme;
        Set("WindowBrush", dark ? "#FF202124" : "#FFF7F7F8");
        Set("ChromeBrush", dark ? "#FF25262A" : "#FFF0F1F5");
        Set("SurfaceBrush", dark ? "#FF292A2D" : "#FFFFFFFF");
        Set("AddressBrush", dark ? "#FF303136" : "#FFF9F9FB");
        Set("TextBrush", dark ? "#FFF1F3F4" : "#FF202124");
        Set("MutedBrush", dark ? "#FFB5B8BD" : "#FF62666D");
        Set("LineBrush", dark ? "#FF3B3D42" : "#FFE3E3E7");
        Set("AccentBrush", dark ? "#FF9D8CFF" : "#FF6557C8");
        Set("AccentSoftBrush", dark ? "#FF3D3760" : "#FFE9E6FF");
        Set("HoverBrush", dark ? "#18FFFFFF" : "#10000000");
        Set("PressedBrush", dark ? "#28FFFFFF" : "#1D000000");
        Set("WebBackdropBrush", dark ? "#FF111318" : "#FFFFFFFF");
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

    private static void Set(string key, string color) => Application.Current.Resources[key] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
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
