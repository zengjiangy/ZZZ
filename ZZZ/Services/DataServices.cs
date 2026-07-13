using System.Net;
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
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZ");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Bookmarks => Path.Combine(Root, "bookmarks.json");
    public static string History => Path.Combine(Root, "history.json");
    public static string Scripts => Path.Combine(Root, "userscripts.json");
    public static string BlockingRules => Path.Combine(Root, "blocking-rules.txt");
    public static string Session => Path.Combine(Root, "session.json");
    public static string WebViewData => Path.Combine(Root, "WebView2");
}

internal static class JsonFiles
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task<T> LoadAsync<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options) ?? fallback();
        }
        catch { return fallback(); }
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, value, Options);
        File.Move(temp, path, true);
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
        foreach (var item in _items)
            html.Append("<DT><A HREF=\"").Append(WebUtility.HtmlEncode(item.Url)).Append("\">").Append(WebUtility.HtmlEncode(item.Title)).Append("</A>\n");
        html.Append("</DL><p>\n");
        await File.WriteAllTextAsync(path, html.ToString(), Encoding.UTF8);
    }
    public async Task ImportHtmlAsync(string path)
    {
        var html = await File.ReadAllTextAsync(path);
        var changed = false;
        foreach (Match match in Regex.Matches(html, "<A\\s+[^>]*HREF=[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var title = WebUtility.HtmlDecode(Regex.Replace(match.Groups["title"].Value, "<.*?>", string.Empty));
            if (!Contains(url)) { _items.Add(new Bookmark { Title = title, Url = url }); changed = true; }
        }
        await JsonFiles.SaveAsync(AppPaths.Bookmarks, _items);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

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
    IEnumerable<UserScript> Matching(string url);
}

public sealed class UserScriptService : IUserScriptService
{
    private List<UserScript> _items = [];
    public IReadOnlyList<UserScript> Items => _items;
    public async Task LoadAsync() => _items = await JsonFiles.LoadAsync(AppPaths.Scripts, () => new List<UserScript>());
    public async Task SaveAsync(IEnumerable<UserScript> items) { _items = items.ToList(); await JsonFiles.SaveAsync(AppPaths.Scripts, _items); }
    public IEnumerable<UserScript> Matching(string url) => _items.Where(x => x.Enabled && (x.Match == "*" || Wildcard(x.Match, url)));
    private static bool Wildcard(string pattern, string value) => Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
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
        if (!File.Exists(AppPaths.BlockingRules)) await File.WriteAllLinesAsync(AppPaths.BlockingRules, Defaults);
        _rules = (await File.ReadAllLinesAsync(AppPaths.BlockingRules)).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('!') && !x.StartsWith('#')).ToArray();
    }
    public bool ShouldBlock(string url)
    {
        foreach (var rule in _rules)
        {
            if (rule.Contains('*') && Regex.IsMatch(url, Regex.Escape(rule).Replace("\\*", ".*"), RegexOptions.IgnoreCase)) return true;
            if (!rule.Contains('*') && url.Contains(rule, StringComparison.OrdinalIgnoreCase)) return true;
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
