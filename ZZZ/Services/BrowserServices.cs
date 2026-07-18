using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ZZZ.Configuration;
using ZZZ.Models;
using ZZZ.ViewModels;
using ZZZ.Views;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

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
        var item = new DownloadItem
        {
            FileName = Path.GetFileName(args.ResultFilePath),
            SourceUrl = args.DownloadOperation.Uri,
            ResultPath = args.ResultFilePath,
            MimeType = args.DownloadOperation.MimeType,
            StartedAt = DateTime.Now,
            Status = LocalizationService.Text("DownloadStarting")
        };
        Items.Insert(0, item);
        args.DownloadOperation.BytesReceivedChanged += (_, _) => Update(item, args.DownloadOperation);
        args.DownloadOperation.StateChanged += (_, _) => Update(item, args.DownloadOperation);
        Update(item, args.DownloadOperation);
    }

    private static void Update(DownloadItem item, CoreWebView2DownloadOperation op)
    {
        item.BytesReceived = op.BytesReceived;
        item.TotalBytes = op.TotalBytesToReceive is > 0 and var total && total <= long.MaxValue ? (long)total : null;
        item.MimeType = op.MimeType;
        item.Progress = item.TotalBytes is > 0 and var totalBytes ? Math.Min(100, item.BytesReceived * 100d / totalBytes) : 0;
        if (op.State == CoreWebView2DownloadState.Completed)
        {
            item.CompletedAt ??= DateTime.Now;
            if (item.TotalBytes is null && File.Exists(item.ResultPath)) item.TotalBytes = new FileInfo(item.ResultPath).Length;
            item.Progress = 100;
        }
        item.InterruptReason = op.State == CoreWebView2DownloadState.Interrupted && op.InterruptReason != CoreWebView2DownloadInterruptReason.None
            ? op.InterruptReason.ToString()
            : string.Empty;
        item.Status = op.State switch
        {
            CoreWebView2DownloadState.Completed => LocalizationService.Text("DownloadCompleted"),
            CoreWebView2DownloadState.Interrupted => string.IsNullOrWhiteSpace(item.InterruptReason)
                ? LocalizationService.Text("DownloadInterrupted")
                : $"{LocalizationService.Text("DownloadInterrupted")} · {item.InterruptReason}",
            _ => item.TotalBytes is > 0 ? $"{item.Progress:0}%" : LocalizationService.Text("Downloading")
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
    event EventHandler<BrowserShortcutEventArgs>? ShortcutRequested;
    Task InitializeAsync(WebView2 view, BrowserTabViewModel tab, CancellationToken cancellationToken = default);
    Task ApplyCurrentSettingsAsync(bool reloadPages);
    void SetActive(BrowserTabViewModel? activeTab);
    void Sleep(BrowserTabViewModel tab);
    void Close(BrowserTabViewModel tab);
}

public enum BrowserShortcut { Find, CloseSplit, TaskManager }

public sealed class BrowserShortcutEventArgs(BrowserTabViewModel tab, BrowserShortcut shortcut) : EventArgs
{
    public BrowserTabViewModel Tab { get; } = tab;
    public BrowserShortcut Shortcut { get; } = shortcut;
    public bool Handled { get; set; }
}

public sealed class BrowserLifecycleService : IBrowserLifecycleService
{
    private const string ChromeDesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private readonly ISettingsService _settings;
    private readonly AdBlockManager _adBlock;
    private readonly IUserScriptService _scripts;
    private readonly IHistoryService _history;
    private readonly IDownloadService _downloads;
    private readonly IPrivacyService _privacy;
    private readonly ITranslationService _translation;
    private readonly Dictionary<BrowserTabViewModel, WeakReference<WebView2>> _views = [];
    private readonly Dictionary<CoreWebView2, string> _darkScriptIds = [];
    private readonly Dictionary<CoreWebView2, string> _grayscaleScriptIds = [];
    private readonly Dictionary<CoreWebView2, string> _webRtcScriptIds = [];
    private readonly Dictionary<CoreWebView2, string> _userScriptIds = [];
    private readonly Dictionary<CoreWebView2, UserScriptBridge> _scriptBridges = [];
    private readonly Dictionary<CoreWebView2, UserScriptNetworkBroker> _scriptBrokers = [];
    private readonly Dictionary<CoreWebView2, AdBlockElementPickerSession> _adBlockPickers = [];
    private readonly Dictionary<BrowserTabViewModel, CoreWebView2Environment> _tabEnvironments = [];
    private readonly object _environmentGate = new();
    private readonly object _initializationGate = new();
    private readonly Dictionary<BrowserTabViewModel, WebView2> _initializingViews = [];
    private readonly string _privateDataPath = Path.Combine(AppPaths.PrivateWebViewRoot, Guid.NewGuid().ToString("N"));
    private Task<CoreWebView2Environment>? _environmentTask;
    private Task<CoreWebView2Environment>? _privateEnvironmentTask;
    private bool _runtimeUpdateNotified;
    private volatile bool _isShuttingDown;
    private int _pendingInitializations;
    private TaskCompletionSource<bool>? _initializationsDrained;
    public event Action<string, bool>? NewTabRequested;
    public event EventHandler<BrowserShortcutEventArgs>? ShortcutRequested;


    public BrowserLifecycleService(ISettingsService settings, AdBlockManager adBlock, IUserScriptService scripts, IHistoryService history, IDownloadService downloads, IPrivacyService privacy, ITranslationService translation)
    {
        _settings = settings; _adBlock = adBlock; _scripts = scripts; _history = history; _downloads = downloads; _privacy = privacy; _translation = translation;
        _adBlock.RulesChanged += AdBlock_RulesChanged;
    }

    private void AdBlock_RulesChanged(object? sender, EventArgs e)
    {
        if (_isShuttingDown) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_isShuttingDown) return;
            foreach (var weak in _views.Values.ToArray())
            {
                if (!weak.TryGetTarget(out var view) || view.CoreWebView2 is not { } core) continue;
                await ApplyCosmeticRulesAsync(core, core.Source, _settings.Current.Advanced.EnableAdBlock);
                if (_adBlockPickers.TryGetValue(core, out var picker)) await picker.ApplyFrameCosmeticRulesAsync();
            }
        }));
    }

    public async Task InitializeAsync(WebView2 view, BrowserTabViewModel tab, CancellationToken cancellationToken = default)
    {
        EnterInitialization(view, tab, cancellationToken);
        try
        {
            ThrowIfInitializationStopped(tab, cancellationToken);
            if (view.CoreWebView2 is not null) { tab.Attach(view); return; }
            NativeDependencyService.PrepareWebView2Loader();
            var environment = await GetEnvironmentAsync(tab.IsPrivate && _settings.Current.Privacy.StrictPrivateTabs);
            ThrowIfInitializationStopped(tab, cancellationToken);
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
            ThrowIfInitializationStopped(tab, cancellationToken);
            var core = view.CoreWebView2!;
            _views[tab] = new WeakReference<WebView2>(view);
            _tabEnvironments[tab] = environment;
            tab.Attach(view);
            view.PreviewKeyDown += (_, e) => OnWebViewPreviewKeyDown(tab, e);
            await ConfigureAsync(core, tab, cancellationToken);
            ThrowIfInitializationStopped(tab, cancellationToken);
            if (!string.IsNullOrWhiteSpace(tab.Url)) view.Source = new Uri(tab.Url);
        }
        catch
        {
            if (_views.ContainsKey(tab)) Sleep(tab);
            else
            {
                tab.Detach();
                try { view.Dispose(); } catch { }
            }
            throw;
        }
        finally { ExitInitialization(view, tab); }
    }

    private void EnterInitialization(WebView2 view, BrowserTabViewModel tab, CancellationToken cancellationToken)
    {
        lock (_initializationGate)
        {
            ThrowIfInitializationStopped(tab, cancellationToken);
            if (_pendingInitializations++ == 0)
                _initializationsDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initializingViews[tab] = view;
        }
    }

    private void ExitInitialization(WebView2 view, BrowserTabViewModel tab)
    {
        lock (_initializationGate)
        {
            if (_initializingViews.TryGetValue(tab, out var current) && ReferenceEquals(current, view))
                _initializingViews.Remove(tab);
            if (--_pendingInitializations == 0) _initializationsDrained?.TrySetResult(true);
        }
    }

    private void ThrowIfInitializationStopped(BrowserTabViewModel tab, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isShuttingDown || tab.IsClosed) throw new OperationCanceledException("Browser initialization was cancelled.");
    }

    public Task WaitForPendingInitializationsAsync()
    {
        lock (_initializationGate)
            return _pendingInitializations == 0 ? Task.CompletedTask : _initializationsDrained!.Task;
    }

    private void OnWebViewPreviewKeyDown(BrowserTabViewModel tab, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        BrowserShortcut? shortcut = ctrl && e.Key == Key.F ? BrowserShortcut.Find
            : ctrl && shift && e.Key == Key.W ? BrowserShortcut.CloseSplit
            : shift && e.Key == Key.Escape ? BrowserShortcut.TaskManager
            : null;
        if (shortcut is null) return;
        var request = new BrowserShortcutEventArgs(tab, shortcut.Value);
        ShortcutRequested?.Invoke(this, request);
        if (request.Handled) e.Handled = true;
    }

    private async Task ConfigureAsync(CoreWebView2 core, BrowserTabViewModel tab, CancellationToken cancellationToken)
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
        core.ProcessFailed += (_, e) => OnProcessFailed(core, tab, e);
        core.WebMessageReceived += async (_, e) => await OnWebMessageReceivedAsync(core, tab, e);
        core.DownloadStarting += (_, e) => _downloads.Handle(e);
        core.WebResourceResponseReceived += async (_, e) => await OnWebResourceResponseReceivedAsync(core, tab, e);
        core.Profile.PreferredTrackingPreventionLevel = cfg.Privacy.BlockThirdPartyCookies
            ? CoreWebView2TrackingPreventionLevel.Strict
            : CoreWebView2TrackingPreventionLevel.Balanced;
        if (!tab.IsPrivate) _privacy.AttachProfile(core.Profile);
        var bridge = new UserScriptBridge(!tab.IsPrivate);
        _scriptBridges[core] = bridge;
        var networkBroker = new UserScriptNetworkBroker(core, _settings);
        bridge.AttachNetwork(networkBroker);
        _scriptBrokers[core] = networkBroker;
        try { core.AddHostObjectToScript("zzzUserscript", bridge); } catch { }
        core.FrameCreated += (_, e) =>
        {
            try { e.Frame.AddHostObjectToScript("zzzUserscript", bridge, new[] { "*" }); } catch { }
        };
        await RegisterDarkModeAsync(core);
        ThrowIfInitializationStopped(tab, cancellationToken);
        await RegisterGrayscaleModeAsync(core);
        ThrowIfInitializationStopped(tab, cancellationToken);
        await RegisterWebRtcProtectionAsync(core);
        ThrowIfInitializationStopped(tab, cancellationToken);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(LocationBootstrap);
        ThrowIfInitializationStopped(tab, cancellationToken);
        await RegisterUserScriptsAsync(core);
        ThrowIfInitializationStopped(tab, cancellationToken);
        try
        {
            var picker = await AdBlockElementPicker.AttachAsync(
                core,
                LocalizationService.Text("BlockThisAd"),
                async rule =>
                {
                    try
                    {
                        await _adBlock.AddElementRuleAsync(rule);
                        await ApplyCosmeticRulesAsync(core, tab.Url, _settings.Current.Advanced.EnableAdBlock);
                        tab.Status = LocalizationService.Text("AdElementBlocked");
                    }
                    catch
                    {
                        tab.Status = LocalizationService.Text("AdElementBlockFailed");
                        throw;
                    }
                },
                url => _settings.Current.Advanced.EnableAdBlock ? _adBlock.GetCosmeticCss(url) : string.Empty);
            _adBlockPickers[core] = picker;
            ThrowIfInitializationStopped(tab, cancellationToken);
        }
        catch { tab.Status = LocalizationService.Text("AdBlockPickerUnavailable"); }
    }

    private void OnWebResourceRequested(BrowserTabViewModel tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var cfg = _settings.Current;
        var url = e.Request.Uri;
        if (cfg.Advanced.EnableAdBlock &&
            !MediaPlaybackPolicy.MustAllow(e.ResourceContext, url, tab.Url) &&
            _adBlock.ShouldBlock(new AdBlockRequestContext
        {
            Url = url,
            DocumentUrl = tab.Url,
            ResourceType = ToAdBlockResourceType(e.ResourceContext, url, tab.Url),
            Method = e.Request.Method
        }))
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
        // Strict mode intentionally strips every cross-site Cookie header. This can
        // require users to temporarily disable the option for federated sign-in or
        // payment widgets, but it makes the setting match its privacy promise.
        if (cfg.Privacy.BlockThirdPartyCookies && SiteClassifier.IsThirdParty(tab.Url, url))
        {
            try { e.Request.Headers.RemoveHeader("Cookie"); } catch { }
        }
        if (cfg.Advanced.EnableResourceSniffer && TryMedia(url, null, null, out var kind)) tab.AddMedia(url, kind);
    }

    private static AdBlockResourceType ToAdBlockResourceType(CoreWebView2WebResourceContext context, string requestUrl, string documentUrl)
    {
        var isSubdocument = context == CoreWebView2WebResourceContext.Document &&
            !string.Equals(requestUrl.TrimEnd('/'), documentUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        return AdBlockWebView2Mapper.FromWebView2(context, isSubdocument);
    }

    private async Task OnWebResourceResponseReceivedAsync(CoreWebView2 core, BrowserTabViewModel tab, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        var url = e.Request.Uri;
        if (_settings.Current.Privacy.BlockThirdPartyCookies && SiteClassifier.IsThirdParty(tab.Url, url))
        {
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(url);
                foreach (var cookie in cookies) core.CookieManager.DeleteCookie(cookie);
            }
            catch { }
        }
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
        if (_isShuttingDown) return;
        tab.Status = string.Empty;
        if (!tab.IsPrivate)
            try { await _history.AddAsync(tab.Title, tab.Url); }
            catch { /* History IO must never tear down the UI dispatcher. */ }
        var cfg = _settings.Current;
        if (cfg.Advanced.EnableAdBlock) await ApplyCosmeticRulesAsync(core, tab.Url);
        var darkScript = WebDarkModeService.ScriptFor(ThemeService.EffectiveWebDarkMode(cfg));
        if (darkScript.Length > 0) await SafeScript(core, darkScript);
        if (cfg.Ui.GrayscaleMode) await SafeScript(core, GrayscaleScript(true));
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
        // Real device geolocation is never allowed. Ask/Custom are implemented by
        // the document-start mock below, so deleting or bypassing it still cannot
        // reach WebView2's native location provider.
        if (e.PermissionKind == CoreWebView2PermissionKind.Geolocation)
            e.State = CoreWebView2PermissionState.Deny;
        if ((e.PermissionKind == CoreWebView2PermissionKind.Camera || e.PermissionKind == CoreWebView2PermissionKind.Microphone) && p.CameraMicrophonePermission == PermissionPolicy.Deny) e.State = CoreWebView2PermissionState.Deny;
    }

    private void OnProcessFailed(CoreWebView2 core, BrowserTabViewModel tab, CoreWebView2ProcessFailedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                        tab.Status = LocalizationService.Text("RendererRecovered");
                        core.Reload();
                        break;
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        Sleep(tab);
                        tab.Status = LocalizationService.Text("BrowserRecovered");
                        tab.RefreshStartPage();
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        tab.Status = LocalizationService.Text("RendererUnresponsive");
                        break;
                }
            }
            catch { tab.Status = LocalizationService.Text("BrowserRecoveryFailed"); }
        }));
    }

    private async Task OnWebMessageReceivedAsync(CoreWebView2 core, BrowserTabViewModel tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "zzz-geolocation") return;
            var id = root.GetProperty("id").GetInt32();
            var privacy = _settings.Current.Privacy;
            var allowed = privacy.LocationPermission != LocationPolicy.Deny;
            var latitude = privacy.CustomLatitude;
            var longitude = privacy.CustomLongitude;
            var accuracy = privacy.CustomLocationAccuracy;
            if (privacy.LocationPermission == LocationPolicy.Ask)
            {
                var dialog = new LocationRequestWindow(tab.Title, tab.Url, latitude, longitude, accuracy)
                {
                    Owner = Application.Current.MainWindow
                };
                allowed = dialog.ShowDialog() == true && dialog.AllowLocation;
                latitude = dialog.Latitude;
                longitude = dialog.Longitude;
                accuracy = dialog.Accuracy;
            }
            var reply = JsonSerializer.Serialize(new { kind = "zzz-geolocation-reply", id, allowed, latitude, longitude, accuracy = Math.Max(1, accuracy), timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            core.PostWebMessageAsJson(reply);
            await Task.CompletedTask;
        }
        catch { }
    }

    private const string LocationBootstrap = @"(() => {
const geo = navigator.geolocation;
if (!geo) return;
let seq = 0, watchSeq = 0; const pending = new Map(), watches = new Map();
const makePosition = x => Object.freeze({coords:Object.freeze({latitude:x.latitude,longitude:x.longitude,accuracy:x.accuracy,altitude:null,altitudeAccuracy:null,heading:null,speed:null}),timestamp:x.timestamp});
const request = (ok, fail, watch) => { const id=++seq; pending.set(id,{ok,fail,watch}); chrome.webview.postMessage({kind:'zzz-geolocation',id}); return id; };
chrome.webview.addEventListener('message', e => { const x=e.data; if(!x || x.kind!=='zzz-geolocation-reply')return; const p=pending.get(x.id); if(!p)return; pending.delete(x.id); if(x.allowed){try{p.ok(makePosition(x));}catch{}}else if(p.fail){try{p.fail(Object.freeze({code:1,message:'User denied Geolocation'}));}catch{}} });
const get = function(success,error){ if(typeof success!=='function') throw new TypeError('Callback must be a function'); request(success,error,false); };
const watch = function(success,error){ if(typeof success!=='function') throw new TypeError('Callback must be a function'); const wid=++watchSeq; const ask=()=>{if(!watches.has(wid))return; request(x=>{success(x);},error,true);}; watches.set(wid,ask); ask(); return wid; };
const clear = function(id){ watches.delete(Number(id)); };
 const methods={getCurrentPosition:{value:get,writable:false,configurable:false},watchPosition:{value:watch,writable:false,configurable:false},clearWatch:{value:clear,writable:false,configurable:false}};
 try{Object.defineProperties(geo,methods);}catch{}
 try{const proto=Object.getPrototypeOf(geo);if(proto)Object.defineProperties(proto,methods);}catch{}
})();";

    private const string WebRtcBlockScript = @"(() => {
const block = name => { try { Object.defineProperty(window,name,{value:undefined,writable:false,configurable:false}); } catch {} };
block('RTCPeerConnection'); block('webkitRTCPeerConnection');
})();";

    private static string ResolveUserAgent(BrowserSettings settings) => settings.UserAgent switch
    {
        UserAgentPreset.AndroidMobile => "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36",
        UserAgentPreset.IPad => "Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1",
        UserAgentPreset.Custom when !string.IsNullOrWhiteSpace(settings.CustomUserAgent) => settings.CustomUserAgent,
        _ => string.Empty
    };

    private static bool IsGoogleTranslate(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".translate.goog", StringComparison.OrdinalIgnoreCase));

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

    private async Task ApplyCosmeticRulesAsync(CoreWebView2 core, string documentUrl, bool enabled = true)
    {
        var css = enabled ? _adBlock.GetCosmeticCss(documentUrl) : string.Empty;
        await SafeScript(core, AdBlockElementPicker.BuildApplyCssScript(css));
    }

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

    private async Task RegisterGrayscaleModeAsync(CoreWebView2 core)
    {
        if (_grayscaleScriptIds.TryGetValue(core, out var oldId))
        {
            _grayscaleScriptIds.Remove(core);
            try { core.RemoveScriptToExecuteOnDocumentCreated(oldId); } catch { }
        }
        if (_settings.Current.Ui.GrayscaleMode)
            _grayscaleScriptIds[core] = await core.AddScriptToExecuteOnDocumentCreatedAsync(GrayscaleScript(true));
        await SafeScript(core, GrayscaleScript(_settings.Current.Ui.GrayscaleMode));
    }

    private static string GrayscaleScript(bool enabled) => $@"(() => {{
const id='__zzzGrayscaleMode'; let style=document.getElementById(id);
if (!{(enabled ? "true" : "false")}) {{ if(style) style.remove(); return; }}
if(!style){{style=document.createElement('style');style.id=id;(document.head||document.documentElement).appendChild(style);}}
style.textContent='html{{filter:grayscale(1)!important}}';
}})();";

    private async Task RegisterWebRtcProtectionAsync(CoreWebView2 core)
    {
        if (_webRtcScriptIds.TryGetValue(core, out var oldId))
        {
            _webRtcScriptIds.Remove(core);
            try { core.RemoveScriptToExecuteOnDocumentCreated(oldId); } catch { }
        }
        if (_settings.Current.Privacy.DisableWebRtc)
            _webRtcScriptIds[core] = await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcBlockScript);
    }

    private async Task RegisterUserScriptsAsync(CoreWebView2 core)
    {
        if (_userScriptIds.TryGetValue(core, out var oldId))
        {
            _userScriptIds.Remove(core);
            try { core.RemoveScriptToExecuteOnDocumentCreated(oldId); } catch { }
        }
        if (!_scriptBrokers.TryGetValue(core, out var broker)) return;
        if (!_settings.Current.Advanced.EnableUserScripts)
        {
            broker.ConfigurePolicies(Array.Empty<UserScript>());
            return;
        }
        if (!_scripts.Items.Any(x => x.Enabled) || !_scriptBridges.TryGetValue(core, out var bridge))
        {
            broker.ConfigurePolicies(Array.Empty<UserScript>());
            return;
        }
        var authorizationTokens = broker.ConfigurePolicies(_scripts.Items);
        var bootstrap = UserScriptRuntime.BuildBootstrap(_scripts.Items, bridge.SecretForBootstrap, authorizationTokens);
        if (bootstrap.Length > 0) _userScriptIds[core] = await core.AddScriptToExecuteOnDocumentCreatedAsync(bootstrap);
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync(bool isPrivate)
    {
        lock (_environmentGate)
        {
            if (!isPrivate)
                return _environmentTask ??= CreateEnvironmentAsync(AppPaths.WebViewData, false);

            Directory.CreateDirectory(_privateDataPath);
            PrivateDataGuard.ProtectAndWatch(_privateDataPath);
            return _privateEnvironmentTask ??= CreateEnvironmentAsync(_privateDataPath, true);
        }
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync(string dataPath, bool isPrivate)
    {
        var options = new CoreWebView2EnvironmentOptions { Language = LocalizationService.CurrentLanguage };
        if (isPrivate) options.AdditionalBrowserArguments = "--disk-cache-size=1 --media-cache-size=1 --disable-features=AutofillServerCommunication,NetworkPrediction";
        var environment = await CoreWebView2Environment.CreateAsync(null, dataPath, options);
        environment.NewBrowserVersionAvailable += (_, _) => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_runtimeUpdateNotified) return;
            _runtimeUpdateNotified = true;
            foreach (var tab in _views.Keys) tab.Status = LocalizationService.Text("RuntimeUpdateAvailable");
        }));
        return environment;
    }

    internal void CleanupStalePrivateData()
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
            await RegisterGrayscaleModeAsync(core);
            await RegisterWebRtcProtectionAsync(core);
            await RegisterUserScriptsAsync(core);
            core.Profile.PreferredTrackingPreventionLevel = _settings.Current.Privacy.BlockThirdPartyCookies
                ? CoreWebView2TrackingPreventionLevel.Strict
                : CoreWebView2TrackingPreventionLevel.Balanced;
            if (reloadPages) core.Reload();
            else await ApplyCosmeticRulesAsync(core, core.Source, _settings.Current.Advanced.EnableAdBlock);
            if (!reloadPages && _adBlockPickers.TryGetValue(core, out var picker)) await picker.ApplyFrameCosmeticRulesAsync();
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

    public IReadOnlyList<BrowserProcessSnapshot> GetProcessSnapshot()
    {
        var processes = new Dictionary<int, BrowserProcessSnapshot>();
        foreach (var environment in _tabEnvironments.Values.Distinct())
        {
            try
            {
                foreach (var info in environment.GetProcessInfos())
                    processes[info.ProcessId] = new BrowserProcessSnapshot { ProcessId = info.ProcessId, Kind = info.Kind.ToString() };
            }
            catch { }
        }
        return processes.Values.OrderBy(x => x.Kind).ThenBy(x => x.ProcessId).ToArray();
    }

    public void BeginShutdown()
    {
        WebView2[] initializing;
        lock (_initializationGate)
        {
            _isShuttingDown = true;
            initializing = _initializingViews.Values.ToArray();
        }
        _history.StopRecording();
        foreach (var view in initializing)
            try { view.Dispose(); } catch { }
        foreach (var weak in _views.Values.ToArray())
            if (weak.TryGetTarget(out var view))
                try { view.CoreWebView2?.Stop(); } catch { }
    }

    public void Sleep(BrowserTabViewModel tab)
    {
        if (!_views.TryGetValue(tab, out var weak)) return;
        try
        {
            if (!weak.TryGetTarget(out var view)) return;
            tab.Detach();
            try
            {
                if (view.CoreWebView2 is { } core)
                {
                    _darkScriptIds.Remove(core);
                    _grayscaleScriptIds.Remove(core);
                    _webRtcScriptIds.Remove(core);
                    _userScriptIds.Remove(core);
                    _scriptBridges.Remove(core);
                    if (_adBlockPickers.TryGetValue(core, out var picker)) { _adBlockPickers.Remove(core); picker.Dispose(); }
                    if (_scriptBrokers.TryGetValue(core, out var broker)) { _scriptBrokers.Remove(core); broker.Dispose(); }
                    if (tab.IsPrivate) _ = core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                    try { core.RemoveHostObjectFromScript("zzzUserscript"); } catch { }
                }
            }
            catch { }
            try { view.Dispose(); } catch { }
            tab.IsSleeping = true;
        }
        finally
        {
            _views.Remove(tab);
            _tabEnvironments.Remove(tab);
        }
    }

    public void Close(BrowserTabViewModel tab)
    {
        tab.MarkClosed();
        WebView2? initializing;
        lock (_initializationGate) _initializingViews.TryGetValue(tab, out initializing);
        if (initializing is not null)
            try { initializing.Dispose(); } catch { }
        Sleep(tab);
    }
    public void Dispose()
    {
        _adBlock.RulesChanged -= AdBlock_RulesChanged;
        foreach (var tab in _views.Keys.ToArray()) Close(tab);
        _views.Clear();
        try { if (Directory.Exists(_privateDataPath)) Directory.Delete(_privateDataPath, true); } catch { }
    }
}

internal static class PrivateDataGuard
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase);

    public static void ProtectAndWatch(string path)
    {
        lock (Gate)
        {
            if (!Watched.Add(path)) return;
        }
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
        }
        catch { }
        try { EncryptFile(path); } catch { }
        StartCleanupWatchdog(path);
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool EncryptFile(string path);

    private static void StartCleanupWatchdog(string path)
    {
        try
        {
            var script = "$targetPid=" + Process.GetCurrentProcess().Id + ";$target=" + QuotePowerShell(path) +
                ";try{Wait-Process -Id $targetPid -ErrorAction SilentlyContinue}catch{};" +
                "for($i=0;$i -lt 240;$i++){try{if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop};break}catch{Start-Sleep -Milliseconds 500}}";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";
}

public sealed class AppServices : IDisposable
{
    private readonly object _backgroundInitializationGate = new();
    private Task? _backgroundInitialization;
    private readonly List<string> _backgroundInitializationErrors = [];
    public SettingsService Settings { get; } = new();
    public BookmarkService Bookmarks { get; } = new();
    public HistoryService History { get; } = new();
    public UserScriptService UserScripts { get; } = new();
    public AdBlockManager AdBlock { get; } = new();
    public SessionService Session { get; } = new();
    public DownloadService Downloads { get; }
    public PrivacyService Privacy { get; }
    public TranslationService Translation { get; } = new();
    public BrowserLifecycleService Browser { get; }
    public TabService Tabs { get; }
    public IMouseGestureService MouseGestures { get; } = new NullMouseGestureService();
    public bool BackgroundInitializationDegraded
    {
        get { lock (_backgroundInitializationErrors) return _backgroundInitializationErrors.Count > 0; }
    }

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
        // Only data required to paint the first window is on the cold-start path.
        await Settings.LoadAsync();
        var sessionTask = Settings.Current.Browser.RestoreLastSession ? Session.LoadAsync() : Session.ClearAsync();
        await Task.WhenAll(Bookmarks.LoadAsync(), sessionTask);
    }

    public Task EnsureBackgroundInitializedAsync()
    {
        lock (_backgroundInitializationGate)
            return _backgroundInitialization ??= Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        LoadOptionalAsync("history", History.LoadAsync),
                        LoadOptionalAsync("userscripts", UserScripts.LoadAsync),
                        LoadOptionalAsync("adblock", AdBlock.LoadAsync));
                }
                finally { Browser.CleanupStalePrivateData(); }
            });
    }

    private async Task LoadOptionalAsync(string name, Func<Task> load)
    {
        try { await load(); }
        catch
        {
            lock (_backgroundInitializationErrors) _backgroundInitializationErrors.Add(name);
        }
    }

    public async Task PrepareForShutdownAsync()
    {
        Task? initialization;
        lock (_backgroundInitializationGate) initialization = _backgroundInitialization;
        var background = initialization ?? Task.CompletedTask;
        try { await Task.WhenAll(background, Browser.WaitForPendingInitializationsAsync()); } catch { }
    }
    public void Dispose() { Browser.Dispose(); AdBlock.Dispose(); Translation.Dispose(); }
}
