using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ZZZ.Configuration;
using ZZZ.Models;
using ZZZ.ViewModels;

namespace ZZZ.Services;

public interface IDownloadService
{
    ObservableCollection<DownloadItem> Items { get; }
    void Handle(CoreWebView2DownloadStartingEventArgs args);
}

public sealed class DownloadService(ISettingsService settings) : IDownloadService
{
    public ObservableCollection<DownloadItem> Items { get; } = [];

    public void Handle(CoreWebView2DownloadStartingEventArgs args)
    {
        var config = settings.Current.Downloads;
        if (config.Mode == DownloadMode.External && !string.IsNullOrWhiteSpace(config.ExternalCommand))
        {
            args.Cancel = true;
            try
            {
                var safeUrl = args.DownloadOperation.Uri.Replace("\"", "%22");
                Process.Start(new ProcessStartInfo(config.ExternalCommand, config.ExternalArguments.Replace("{url}", $"\"{safeUrl}\"")) { UseShellExecute = true });
            }
            catch { }
            return;
        }

        Directory.CreateDirectory(config.Folder);
        var suggested = Path.GetFileName(args.ResultFilePath);
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "download";
        args.ResultFilePath = UniquePath(Path.Combine(config.Folder, suggested));
        var item = new DownloadItem { FileName = Path.GetFileName(args.ResultFilePath), SourceUrl = args.DownloadOperation.Uri, ResultPath = args.ResultFilePath };
        Items.Insert(0, item);
        args.DownloadOperation.BytesReceivedChanged += (_, _) => Update(item, args.DownloadOperation);
        args.DownloadOperation.StateChanged += (_, _) => Update(item, args.DownloadOperation);
    }

    private static void Update(DownloadItem item, CoreWebView2DownloadOperation op)
    {
        item.Progress = op.TotalBytesToReceive is > 0 and var total ? op.BytesReceived * 100d / total : 0;
        item.Status = op.State switch
        {
            CoreWebView2DownloadState.Completed => "Completed",
            CoreWebView2DownloadState.Interrupted => "Interrupted",
            _ => $"{item.Progress:0}%"
        };
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

public interface IBrowserLifecycleService : IDisposable
{
    event Action<string, bool>? NewTabRequested;
    Task InitializeAsync(WebView2 view, BrowserTabViewModel tab);
    Task ApplyCurrentSettingsAsync(bool reloadPages);
    void Sleep(BrowserTabViewModel tab);
    void Close(BrowserTabViewModel tab);
}

public sealed class BrowserLifecycleService : IBrowserLifecycleService
{
    private readonly ISettingsService _settings;
    private readonly IAdBlockService _adBlock;
    private readonly IUserScriptService _scripts;
    private readonly IHistoryService _history;
    private readonly IDownloadService _downloads;
    private readonly IPrivacyService _privacy;
    private readonly Dictionary<BrowserTabViewModel, WeakReference<WebView2>> _views = [];
    private readonly Dictionary<CoreWebView2, string> _darkScriptIds = [];
    private CoreWebView2Environment? _environment;
    public event Action<string, bool>? NewTabRequested;

    public BrowserLifecycleService(ISettingsService settings, IAdBlockService adBlock, IUserScriptService scripts, IHistoryService history, IDownloadService downloads, IPrivacyService privacy)
    {
        _settings = settings; _adBlock = adBlock; _scripts = scripts; _history = history; _downloads = downloads; _privacy = privacy;
    }

    public async Task InitializeAsync(WebView2 view, BrowserTabViewModel tab)
    {
        if (view.CoreWebView2 is not null) { tab.Attach(view); return; }
        _environment ??= await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData, new CoreWebView2EnvironmentOptions { Language = LocalizationService.CurrentLanguage });
        if (tab.IsPrivate)
        {
            var options = _environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = $"Private_{tab.Id}";
            options.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(_environment, options);
        }
        else
        {
            await view.EnsureCoreWebView2Async(_environment);
        }
        var core = view.CoreWebView2!;
        _views[tab] = new WeakReference<WebView2>(view);
        tab.Attach(view);
        await ConfigureAsync(core, tab);
        if (!string.IsNullOrWhiteSpace(tab.Url)) view.Source = new Uri(tab.Url);
    }

    private async Task ConfigureAsync(CoreWebView2 core, BrowserTabViewModel tab)
    {
        var cfg = _settings.Current;
        core.Settings.AreDevToolsEnabled = cfg.Advanced.EnableDeveloperTools;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        var userAgent = ResolveUserAgent(cfg.Browser);
        if (!string.IsNullOrWhiteSpace(userAgent)) core.Settings.UserAgent = userAgent;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => OnWebResourceRequested(tab, e);
        core.NavigationStarting += (_, e) => { tab.IsLoading = true; tab.Address = e.Uri; tab.Url = e.Uri; };
        core.SourceChanged += (_, _) => { tab.Url = core.Source; tab.Address = core.Source; };
        core.DocumentTitleChanged += (_, _) => tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? LocalizationService.Text("NewTab") : core.DocumentTitle;
        core.HistoryChanged += (_, _) => { tab.CanGoBack = core.CanGoBack; tab.CanGoForward = core.CanGoForward; };
        core.NavigationCompleted += async (_, e) => await OnNavigationCompletedAsync(core, tab, e);
        core.NewWindowRequested += (_, e) => { e.Handled = true; NewTabRequested?.Invoke(e.Uri, tab.IsPrivate); };
        core.PermissionRequested += (_, e) => OnPermissionRequested(e);
        core.DownloadStarting += (_, e) => _downloads.Handle(e);
        if (!tab.IsPrivate) _privacy.AttachProfile(core.Profile);
        await RegisterDarkModeAsync(core);
    }

    private void OnWebResourceRequested(BrowserTabViewModel tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var cfg = _settings.Current;
        var url = e.Request.Uri;
        if (cfg.Advanced.EnableAdBlock && _adBlock.ShouldBlock(url))
        {
            e.Response = _environment!.CreateWebResourceResponse(null, 403, "Blocked by ZZZ", "Content-Type: text/plain");
            return;
        }
        if (cfg.Privacy.SendDoNotTrack) e.Request.Headers.SetHeader("DNT", "1");
        e.Request.Headers.SetHeader("Accept-Language", LocalizationService.CurrentLanguage);
        if (cfg.Privacy.BlockThirdPartyCookies && IsThirdParty(tab.Url, url))
        {
            try { e.Request.Headers.RemoveHeader("Cookie"); } catch { }
        }
        if (cfg.Advanced.EnableResourceSniffer && TryMedia(url, out var kind)) tab.AddMedia(url, kind);
    }

    private async Task OnNavigationCompletedAsync(CoreWebView2 core, BrowserTabViewModel tab, CoreWebView2NavigationCompletedEventArgs e)
    {
        tab.IsLoading = false;
        tab.CanGoBack = core.CanGoBack;
        tab.CanGoForward = core.CanGoForward;
        if (!e.IsSuccess) { tab.Status = $"Navigation error: {e.WebErrorStatus}"; return; }
        tab.Status = string.Empty;
        if (!tab.IsPrivate) await _history.AddAsync(tab.Title, tab.Url);
        var cfg = _settings.Current;
        if (cfg.Privacy.DisableWebRtc)
            await SafeScript(core, "Object.defineProperty(window,'RTCPeerConnection',{value:undefined});Object.defineProperty(window,'webkitRTCPeerConnection',{value:undefined});");
        var darkScript = WebDarkModeService.ScriptFor(ThemeService.EffectiveWebDarkMode(cfg));
        if (darkScript.Length > 0) await SafeScript(core, darkScript);
        if (cfg.Advanced.EnableUserScripts)
            foreach (var script in _scripts.Matching(tab.Url)) await SafeScript(core, script.Code);
        if (cfg.Privacy.BlockThirdPartyCookies) await RemoveThirdPartyCookiesAsync(core, tab.Url);
    }

    private async Task RemoveThirdPartyCookiesAsync(CoreWebView2 core, string topUrl)
    {
        if (!Uri.TryCreate(topUrl, UriKind.Absolute, out var top)) return;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(null);
            foreach (var c in cookies)
            {
                var domain = c.Domain.TrimStart('.');
                if (!top.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) && !top.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase))
                    core.CookieManager.DeleteCookie(c);
            }
        }
        catch { }
    }

    private void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e)
    {
        var p = _settings.Current.Privacy;
        if (e.PermissionKind == CoreWebView2PermissionKind.Geolocation && p.LocationPermission == PermissionPolicy.Deny) e.State = CoreWebView2PermissionState.Deny;
        if ((e.PermissionKind == CoreWebView2PermissionKind.Camera || e.PermissionKind == CoreWebView2PermissionKind.Microphone) && p.CameraMicrophonePermission == PermissionPolicy.Deny) e.State = CoreWebView2PermissionState.Deny;
    }

    private static string ResolveUserAgent(BrowserSettings settings) => settings.UserAgent switch
    {
        UserAgentPreset.AndroidMobile => "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36",
        UserAgentPreset.IPad => "Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1",
        UserAgentPreset.Custom when !string.IsNullOrWhiteSpace(settings.CustomUserAgent) => settings.CustomUserAgent,
        _ => string.Empty
    };

    private static bool IsThirdParty(string topUrl, string requestUrl)
    {
        if (!Uri.TryCreate(topUrl, UriKind.Absolute, out var top) || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) return false;
        return !request.Host.Equals(top.Host, StringComparison.OrdinalIgnoreCase) && !request.Host.EndsWith('.' + top.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMedia(string url, out string kind)
    {
        var clean = url.Split('?', '#')[0];
        foreach (var ext in new[] { "m3u8", "mp4", "mp3", "webm", "m4a", "flac", "aac" })
            if (clean.EndsWith('.' + ext, StringComparison.OrdinalIgnoreCase)) { kind = ext; return true; }
        kind = string.Empty; return false;
    }

    private static async Task SafeScript(CoreWebView2 core, string script) { try { await core.ExecuteScriptAsync(script); } catch { } }

    private async Task RegisterDarkModeAsync(CoreWebView2 core)
    {
        if (_darkScriptIds.Remove(core, out var oldId))
            try { core.RemoveScriptToExecuteOnDocumentCreated(oldId); } catch { }
        var mode = ThemeService.EffectiveWebDarkMode(_settings.Current);
        await WebDarkModeService.ApplyNativeModeAsync(core, mode);
        var script = WebDarkModeService.ScriptFor(mode);
        if (script.Length > 0) _darkScriptIds[core] = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    public async Task ApplyCurrentSettingsAsync(bool reloadPages)
    {
        foreach (var weak in _views.Values.ToArray())
        {
            if (!weak.TryGetTarget(out var view) || view.CoreWebView2 is not { } core) continue;
            core.Settings.AreDevToolsEnabled = _settings.Current.Advanced.EnableDeveloperTools;
            view.DefaultBackgroundColor = ThemeService.WebBackgroundColor();
            var userAgent = ResolveUserAgent(_settings.Current.Browser);
            core.Settings.UserAgent = userAgent;
            await RegisterDarkModeAsync(core);
            if (reloadPages) core.Reload();
        }
    }

    public void Sleep(BrowserTabViewModel tab)
    {
        if (!_views.TryGetValue(tab, out var weak) || !weak.TryGetTarget(out var view)) return;
        tab.Detach();
        if (view.CoreWebView2 is { } core) _darkScriptIds.Remove(core);
        view.Dispose();
        _views.Remove(tab);
        tab.IsSleeping = true;
    }

    public void Close(BrowserTabViewModel tab) => Sleep(tab);
    public void Dispose()
    {
        foreach (var tab in _views.Keys.ToArray()) Close(tab);
        _views.Clear();
    }
}

public sealed class AppServices : IDisposable
{
    public SettingsService Settings { get; } = new();
    public BookmarkService Bookmarks { get; } = new();
    public HistoryService History { get; } = new();
    public UserScriptService UserScripts { get; } = new();
    public AdBlockService AdBlock { get; } = new();
    public SessionService Session { get; } = new();
    public DownloadService Downloads { get; }
    public PrivacyService Privacy { get; }
    public BrowserLifecycleService Browser { get; }
    public TabService Tabs { get; }
    public IMouseGestureService MouseGestures { get; } = new NullMouseGestureService();

    public AppServices()
    {
        Downloads = new DownloadService(Settings);
        Privacy = new PrivacyService(Settings, History);
        Browser = new BrowserLifecycleService(Settings, AdBlock, UserScripts, History, Downloads, Privacy);
        Tabs = new TabService(this);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        await Task.WhenAll(Settings.LoadAsync(), Bookmarks.LoadAsync(), History.LoadAsync(), UserScripts.LoadAsync(), AdBlock.LoadAsync(), Session.LoadAsync());
    }
    public void Dispose() => Browser.Dispose();
}
