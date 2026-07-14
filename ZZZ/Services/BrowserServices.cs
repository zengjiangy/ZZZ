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
    void SetActive(BrowserTabViewModel? activeTab);
    void Sleep(BrowserTabViewModel tab);
    void Close(BrowserTabViewModel tab);
}

public sealed class BrowserLifecycleService : IBrowserLifecycleService
{
    private const string ChromeDesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private readonly ISettingsService _settings;
    private readonly IAdBlockService _adBlock;
    private readonly IUserScriptService _scripts;
    private readonly IHistoryService _history;
    private readonly IDownloadService _downloads;
    private readonly IPrivacyService _privacy;
    private readonly ITranslationService _translation;
    private readonly Dictionary<BrowserTabViewModel, WeakReference<WebView2>> _views = [];
    private readonly Dictionary<CoreWebView2, string> _darkScriptIds = [];
    private readonly Dictionary<CoreWebView2, string> _userScriptIds = [];
    private readonly Dictionary<CoreWebView2, UserScriptBridge> _scriptBridges = [];
    private readonly Dictionary<BrowserTabViewModel, CoreWebView2Environment> _tabEnvironments = [];
    private readonly object _environmentGate = new();
    private readonly string _privateDataPath = Path.Combine(AppPaths.PrivateWebViewRoot, Guid.NewGuid().ToString("N"));
    private Task<CoreWebView2Environment>? _environmentTask;
    private Task<CoreWebView2Environment>? _privateEnvironmentTask;
    public event Action<string, bool>? NewTabRequested;

    public BrowserLifecycleService(ISettingsService settings, IAdBlockService adBlock, IUserScriptService scripts, IHistoryService history, IDownloadService downloads, IPrivacyService privacy, ITranslationService translation)
    {
        _settings = settings; _adBlock = adBlock; _scripts = scripts; _history = history; _downloads = downloads; _privacy = privacy; _translation = translation;
        CleanupStalePrivateData();
    }

    public async Task InitializeAsync(WebView2 view, BrowserTabViewModel tab)
    {
        if (view.CoreWebView2 is not null) { tab.Attach(view); return; }
        var environment = await GetEnvironmentAsync(tab.IsPrivate && _settings.Current.Privacy.StrictPrivateTabs);
        if (tab.IsPrivate)
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = $"Private_{tab.Id}";
            options.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(environment, options);
        }
        else
        {
            await view.EnsureCoreWebView2Async(environment);
        }
        var core = view.CoreWebView2!;
        _views[tab] = new WeakReference<WebView2>(view);
        _tabEnvironments[tab] = environment;
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
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        if (tab.IsPrivate && cfg.Privacy.StrictPrivateTabs)
        {
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Profile.IsGeneralAutofillEnabled = false;
            core.Profile.IsPasswordAutosaveEnabled = false;
        }
        var userAgent = ResolveUserAgent(cfg.Browser);
        if (!string.IsNullOrWhiteSpace(userAgent)) core.Settings.UserAgent = userAgent;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => OnWebResourceRequested(tab, e);
        core.NavigationStarting += (_, e) =>
        {
            core.Settings.UserAgent = IsGoogleTranslate(e.Uri) ? ChromeDesktopUserAgent : ResolveUserAgent(_settings.Current.Browser);
            tab.BeginNavigation(e.Uri);
        };
        core.SourceChanged += (_, _) => { tab.Url = core.Source; tab.Address = core.Source; };
        core.DocumentTitleChanged += (_, _) => tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? LocalizationService.Text("NewTab") : core.DocumentTitle;
        core.HistoryChanged += (_, _) => { tab.CanGoBack = core.CanGoBack; tab.CanGoForward = core.CanGoForward; };
        core.NavigationCompleted += async (_, e) => await OnNavigationCompletedAsync(core, tab, e);
        core.NewWindowRequested += (_, e) => { e.Handled = true; NewTabRequested?.Invoke(e.Uri, tab.IsPrivate); };
        core.PermissionRequested += (_, e) => OnPermissionRequested(tab, e);
        core.DownloadStarting += (_, e) => _downloads.Handle(e);
        core.WebResourceResponseReceived += (_, e) => OnWebResourceResponseReceived(tab, e);
        if (!tab.IsPrivate) _privacy.AttachProfile(core.Profile);
        var bridge = new UserScriptBridge(!tab.IsPrivate);
        _scriptBridges[core] = bridge;
        try { core.AddHostObjectToScript("zzzUserscript", bridge); } catch { }
        core.FrameCreated += (_, e) =>
        {
            try { e.Frame.AddHostObjectToScript("zzzUserscript", bridge, new[] { "*" }); } catch { }
        };
        await RegisterDarkModeAsync(core);
        await RegisterUserScriptsAsync(core);
    }

    private void OnWebResourceRequested(BrowserTabViewModel tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var cfg = _settings.Current;
        var url = e.Request.Uri;
        if (cfg.Advanced.EnableAdBlock && _adBlock.ShouldBlock(url))
        {
            if (_tabEnvironments.TryGetValue(tab, out var environment))
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked by ZZZ", "Content-Type: text/plain");
            return;
        }
        if (cfg.Privacy.SendDoNotTrack) e.Request.Headers.SetHeader("DNT", "1");
        if (cfg.Privacy.SendGlobalPrivacyControl) e.Request.Headers.SetHeader("Sec-GPC", "1");
        e.Request.Headers.SetHeader("Accept-Language", LocalizationService.CurrentLanguage);
        if (tab.IsPrivate && cfg.Privacy.StrictPrivateTabs)
        {
            e.Request.Headers.SetHeader("Cache-Control", "no-store, max-age=0");
            e.Request.Headers.SetHeader("Pragma", "no-cache");
        }
        // Do not delete or blanket-strip cross-site cookies: that breaks OAuth,
        // federated sign-in and payment flows. In balanced mode only known
        // tracking requests lose their Cookie header; normal embedded services
        // retain WebView2's standard SameSite protections.
        if (cfg.Privacy.BlockThirdPartyCookies && IsThirdParty(tab.Url, url) && _adBlock.ShouldBlock(url))
        {
            try { e.Request.Headers.RemoveHeader("Cookie"); } catch { }
        }
        if (cfg.Advanced.EnableResourceSniffer && TryMedia(url, null, null, out var kind)) tab.AddMedia(url, kind);
    }

    private void OnWebResourceResponseReceived(BrowserTabViewModel tab, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!_settings.Current.Advanced.EnableResourceSniffer) return;
        try
        {
            var mime = Header(e.Response.Headers, "Content-Type");
            var disposition = Header(e.Response.Headers, "Content-Disposition");
            if (!TryMedia(e.Request.Uri, mime, disposition, out var kind)) return;
            long? length = long.TryParse(Header(e.Response.Headers, "Content-Length"), out var parsed) ? parsed : null;
            tab.AddMedia(e.Request.Uri, kind, mime, length);
        }
        catch { }
    }

    private static string Header(CoreWebView2HttpResponseHeaders headers, string name)
    {
        try { return headers.Contains(name) ? headers.GetHeader(name) : string.Empty; }
        catch { return string.Empty; }
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
        if (cfg.Browser.AutoTranslateForeignPages && cfg.Browser.TranslationProvider == TranslationProvider.Microsoft)
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(cfg.Browser.TranslationTargetLanguage) ? "zh-CN" : cfg.Browser.TranslationTargetLanguage;
                if (await _translation.ShouldAutoTranslateAsync(core, target))
                {
                    tab.Status = LocalizationService.Text("Translating");
                    var count = await _translation.TranslatePageAsync(core, target);
                    tab.Status = count > 0 ? LocalizationService.Text("TranslationComplete") : string.Empty;
                }
            }
            catch { tab.Status = LocalizationService.Text("TranslationFailed"); }
        }
    }

    private void OnPermissionRequested(BrowserTabViewModel tab, CoreWebView2PermissionRequestedEventArgs e)
    {
        var p = _settings.Current.Privacy;
        if (tab.IsPrivate && p.StrictPrivateTabs) { e.State = CoreWebView2PermissionState.Deny; return; }
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

    private static bool IsGoogleTranslate(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".translate.goog", StringComparison.OrdinalIgnoreCase));

    private static bool IsThirdParty(string topUrl, string requestUrl)
    {
        if (!Uri.TryCreate(topUrl, UriKind.Absolute, out var top) || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) return false;
        return !request.Host.Equals(top.Host, StringComparison.OrdinalIgnoreCase) && !request.Host.EndsWith('.' + top.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMedia(string url, string? contentType, string? contentDisposition, out string kind)
    {
        var clean = url.Split('?', '#')[0];
        try { clean = Uri.UnescapeDataString(clean); } catch { }
        if (!string.IsNullOrWhiteSpace(contentDisposition)) clean += " " + contentDisposition;
        foreach (var ext in new[] { "m3u8", "mpd", "mp4", "m4v", "mov", "mkv", "webm", "mp3", "m4a", "flac", "aac", "ogg", "oga", "opus", "wav", "flv", "avi", "ts", "m2ts", "m4s" })
            if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"\." + ext + @"(?:$|[\s\""';])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { kind = ext; return true; }
        var mime = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (mime.StartsWith("video/") || mime.StartsWith("audio/")) { kind = mime.Substring(mime.IndexOf('/') + 1); return true; }
        if (mime.Contains("mpegurl") || mime.Contains("m3u8")) { kind = "m3u8"; return true; }
        if (mime.Contains("dash+xml")) { kind = "mpd"; return true; }
        if (mime.Contains("mp2t")) { kind = "ts"; return true; }
        kind = string.Empty; return false;
    }

    private static async Task SafeScript(CoreWebView2 core, string script) { try { await core.ExecuteScriptAsync(script); } catch { } }

    private async Task RegisterDarkModeAsync(CoreWebView2 core)
    {
        if (_darkScriptIds.TryGetValue(core, out var oldId))
        {
            _darkScriptIds.Remove(core);
            try { core.RemoveScriptToExecuteOnDocumentCreated(oldId); } catch { }
        }
        var mode = ThemeService.EffectiveWebDarkMode(_settings.Current);
        await WebDarkModeService.ApplyNativeModeAsync(core, mode);
        var script = WebDarkModeService.ScriptFor(mode);
        if (script.Length > 0) _darkScriptIds[core] = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private async Task RegisterUserScriptsAsync(CoreWebView2 core)
    {
        if (_userScriptIds.TryGetValue(core, out var oldId))
        {
            _userScriptIds.Remove(core);
            try { core.RemoveScriptToExecuteOnDocumentCreated(oldId); } catch { }
        }
        if (!_settings.Current.Advanced.EnableUserScripts) return;
        if (!_scripts.Items.Any(x => x.Enabled) || !_scriptBridges.TryGetValue(core, out var bridge)) return;
        var bootstrap = UserScriptRuntime.BuildBootstrap(_scripts.Items, bridge.SecretForBootstrap);
        if (bootstrap.Length > 0) _userScriptIds[core] = await core.AddScriptToExecuteOnDocumentCreatedAsync(bootstrap);
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync(bool isPrivate)
    {
        lock (_environmentGate)
        {
            if (!isPrivate)
                return _environmentTask ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData,
                    new CoreWebView2EnvironmentOptions { Language = LocalizationService.CurrentLanguage });

            Directory.CreateDirectory(_privateDataPath);
            return _privateEnvironmentTask ??= CoreWebView2Environment.CreateAsync(null, _privateDataPath,
                new CoreWebView2EnvironmentOptions
                {
                    Language = LocalizationService.CurrentLanguage,
                    AdditionalBrowserArguments = "--disk-cache-size=1 --media-cache-size=1 --disable-features=AutofillServerCommunication,NetworkPrediction"
                });
        }
    }

    private void CleanupStalePrivateData()
    {
        try
        {
            if (!Directory.Exists(AppPaths.PrivateWebViewRoot)) return;
            foreach (var path in Directory.GetDirectories(AppPaths.PrivateWebViewRoot))
            {
                if (string.Equals(path, _privateDataPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(path, true); } catch { }
            }
        }
        catch { }
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
            await RegisterUserScriptsAsync(core);
            if (reloadPages) core.Reload();
        }
    }

    public void SetActive(BrowserTabViewModel? activeTab)
    {
        foreach (var pair in _views)
        {
            if (!pair.Value.TryGetTarget(out var view) || view.CoreWebView2 is not { } core) continue;
            try { core.MemoryUsageTargetLevel = ReferenceEquals(pair.Key, activeTab) ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
        }
    }

    public void Sleep(BrowserTabViewModel tab)
    {
        if (!_views.TryGetValue(tab, out var weak) || !weak.TryGetTarget(out var view)) return;
        tab.Detach();
        if (view.CoreWebView2 is { } core)
        {
            _darkScriptIds.Remove(core);
            _userScriptIds.Remove(core);
            _scriptBridges.Remove(core);
            if (tab.IsPrivate) _ = core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            try { core.RemoveHostObjectFromScript("zzzUserscript"); } catch { }
        }
        view.Dispose();
        _views.Remove(tab);
        _tabEnvironments.Remove(tab);
        tab.IsSleeping = true;
    }

    public void Close(BrowserTabViewModel tab) => Sleep(tab);
    public void Dispose()
    {
        foreach (var tab in _views.Keys.ToArray()) Close(tab);
        _views.Clear();
        try { if (Directory.Exists(_privateDataPath)) Directory.Delete(_privateDataPath, true); } catch { }
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
    public TranslationService Translation { get; } = new();
    public BrowserLifecycleService Browser { get; }
    public TabService Tabs { get; }
    public IMouseGestureService MouseGestures { get; } = new NullMouseGestureService();

    public AppServices()
    {
        Downloads = new DownloadService(Settings);
        Privacy = new PrivacyService(Settings, History);
        Browser = new BrowserLifecycleService(Settings, AdBlock, UserScripts, History, Downloads, Privacy, Translation);
        Tabs = new TabService(this);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        await Task.WhenAll(Settings.LoadAsync(), Bookmarks.LoadAsync(), History.LoadAsync(), UserScripts.LoadAsync(), AdBlock.LoadAsync(), Session.LoadAsync());
    }
    public void Dispose() { Browser.Dispose(); Translation.Dispose(); }
}
