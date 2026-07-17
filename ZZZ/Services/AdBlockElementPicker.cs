using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ZZZ.Models;

namespace ZZZ.Services;

public static class AdBlockElementPicker
{
    public static async Task<AdBlockElementPickerSession> AttachAsync(
        CoreWebView2 core,
        string menuLabel,
        Func<AdBlockElementRule, Task> saveRuleAsync,
        Func<string, string>? cosmeticCssProvider = null)
    {
        if (core is null) throw new ArgumentNullException(nameof(core));
        if (saveRuleAsync is null) throw new ArgumentNullException(nameof(saveRuleAsync));
        var session = new AdBlockElementPickerSession(core, string.IsNullOrWhiteSpace(menuLabel) ? "Block this element" : menuLabel, saveRuleAsync, cosmeticCssProvider);
        await session.AttachAsync().ConfigureAwait(true);
        return session;
    }

    public static string BuildApplyCssScript(string css)
    {
        var encoded = JsonSerializer.Serialize(css ?? string.Empty);
        return "(() => { try { const id='zzz-adblock-cosmetic'; let s=document.getElementById(id); " +
            "if(!s){s=document.createElement('style');s.id=id;(document.head||document.documentElement).appendChild(s);}" +
            "s.textContent=" + encoded + "; } catch {} })();";
    }

    internal const string Bootstrap = @"(() => {
if (window.__zzzAdBlockElementPicker) return;
let target = null;
const escapeCss = value => {
  try { if (window.CSS && typeof CSS.escape === 'function') return CSS.escape(String(value)); } catch {}
  return String(value).replace(/[^a-zA-Z0-9_-]/g, ch => '\\' + ch.codePointAt(0).toString(16) + ' ');
};
const unique = selector => { try { return document.querySelectorAll(selector).length === 1; } catch { return false; } };
const selectorFor = element => {
  if (!element || element.nodeType !== 1 || element === document.documentElement || element === document.body) return '';
  const parts = [];
  let current = element;
  for (let depth = 0; current && current.nodeType === 1 && depth < 8; depth++, current = current.parentElement) {
    const tag = current.localName ? current.localName.toLowerCase() : '*';
    if (current.id) {
      const byId = '#' + escapeCss(current.id);
      if (unique(byId)) { parts.unshift(byId); break; }
    }
    let part = tag;
    const classes = Array.from(current.classList || []).filter(x => x && x.length <= 64 && !/[0-9a-f]{10,}/i.test(x)).slice(0, 3);
    if (classes.length) part += classes.map(x => '.' + escapeCss(x)).join('');
    const parent = current.parentElement;
    if (parent) {
      const same = Array.from(parent.children).filter(x => x.localName === current.localName);
      if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(current) + 1) + ')';
    }
    parts.unshift(part);
    const candidate = parts.join(' > ');
    if (unique(candidate)) break;
    if (current === document.body) break;
  }
  const selector = parts.join(' > ');
  return selector.length <= 2048 ? selector : '';
};
document.addEventListener('contextmenu', event => {
  target = event.target && event.target.nodeType === 1 ? event.target : null;
  const selector = selectorFor(target);
  if (!selector) return;
  try { window.chrome.webview.postMessage({ kind:'zzz-adblock-picker', token:'__ZZZ_PICKER_TOKEN__', pageUrl:location.href, selector:selector }); } catch {}
}, true);
const api = Object.freeze({
  capture: () => {
    const selector = selectorFor(target);
    if (!selector) return null;
    return { pageUrl: location.href, selector: selector };
  },
  hide: () => { try { if(target && target.style) target.style.setProperty('display','none','important'); } catch {} }
});
try { Object.defineProperty(window, '__zzzAdBlockElementPicker', { value: api, configurable: false, writable: false }); }
catch { window.__zzzAdBlockElementPicker = api; }
})();";
}

public sealed class AdBlockElementPickerSession : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly Func<AdBlockElementRule, Task> _saveRuleAsync;
    private readonly Func<string, string>? _cosmeticCssProvider;
    private readonly CoreWebView2ContextMenuItem _menuItem;
    private readonly string _menuLabel;
    private readonly string _messageToken = Guid.NewGuid().ToString("N");
    private readonly string _bootstrap;
    private readonly Dictionary<uint, FrameRegistration> _frames = [];
    private string _scriptId = string.Empty;
    private string _lastPageUrl = string.Empty;
    private string _lastFrameUrl = string.Empty;
    private PickerCapture? _lastCapture;
    private CoreWebView2Frame? _lastCaptureFrame;
    private DateTime _lastCaptureUtc;
    private DateTime _lastNativeMenuUtc;
    private DateTime _lastContextMenuUtc;
    private DispatcherTimer? _fallbackTimer;
    private ContextMenu? _fallbackMenu;
    private bool _attached;
    private bool _disposed;

    internal AdBlockElementPickerSession(CoreWebView2 core, string menuLabel, Func<AdBlockElementRule, Task> saveRuleAsync, Func<string, string>? cosmeticCssProvider)
    {
        _core = core;
        _saveRuleAsync = saveRuleAsync;
        _cosmeticCssProvider = cosmeticCssProvider;
        _menuLabel = menuLabel;
        _bootstrap = AdBlockElementPicker.Bootstrap.Replace("__ZZZ_PICKER_TOKEN__", _messageToken);
        _menuItem = core.Environment.CreateContextMenuItem(menuLabel, null!, CoreWebView2ContextMenuItemKind.Command);
    }

    public event EventHandler<AdBlockElementRuleCreatedEventArgs>? RuleCreated;
    public string LastError { get; private set; } = string.Empty;

    internal async Task AttachAsync()
    {
        if (_attached) return;
        _scriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(_bootstrap).ConfigureAwait(true);
        try { await _core.ExecuteScriptAsync(_bootstrap).ConfigureAwait(true); } catch { }
        _core.ContextMenuRequested += OnContextMenuRequested;
        _core.WebMessageReceived += OnCoreWebMessageReceived;
        _core.FrameCreated += OnCoreFrameCreated;
        _menuItem.CustomItemSelected += OnCustomItemSelected;
        _attached = true;
    }

    public async Task<AdBlockElementRule?> CaptureAndSaveAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdBlockElementPickerSession));
        try
        {
            var captureIsCurrent = DateTime.UtcNow - _lastCaptureUtc < TimeSpan.FromMinutes(2) &&
                (_lastContextMenuUtc == default || _lastCaptureUtc >= _lastContextMenuUtc - TimeSpan.FromMilliseconds(500));
            var capture = captureIsCurrent ? _lastCapture : null;
            var captureFrame = capture is null ? null : _lastCaptureFrame;
            if (capture is null)
            {
                var registration = _frames.Values.LastOrDefault(x => string.Equals(x.PageUrl, _lastFrameUrl, StringComparison.OrdinalIgnoreCase));
                var json = registration is not null && registration.Frame.IsDestroyed() == 0
                    ? await registration.Frame.ExecuteScriptAsync("window.__zzzAdBlockElementPicker ? window.__zzzAdBlockElementPicker.capture() : null").ConfigureAwait(true)
                    : await _core.ExecuteScriptAsync("window.__zzzAdBlockElementPicker ? window.__zzzAdBlockElementPicker.capture() : null").ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
                capture = JsonSerializer.Deserialize<PickerCapture>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (registration is not null)
                {
                    captureFrame = registration.Frame;
                    if (capture is not null && string.IsNullOrWhiteSpace(capture.PageUrl)) capture.PageUrl = registration.PageUrl;
                }
            }
            if (capture is null || string.IsNullOrWhiteSpace(capture.Selector)) return null;
            var pageUrl = string.IsNullOrWhiteSpace(capture.PageUrl) ? _lastPageUrl : capture.PageUrl;
            if (!IsHttpPage(pageUrl)) pageUrl = _lastPageUrl;
            if (!IsHttpPage(pageUrl)) return null;
            var rule = new AdBlockElementRule { PageUrl = pageUrl, Selector = capture.Selector };
            // Give immediate visual feedback while the combined subscription
            // snapshot is rebuilt and the new rule is persisted in the background.
            try
            {
                var frame = captureFrame;
                if (frame is not null && frame.IsDestroyed() == 0)
                    await frame.ExecuteScriptAsync("window.__zzzAdBlockElementPicker && window.__zzzAdBlockElementPicker.hide()").ConfigureAwait(true);
                else
                    await _core.ExecuteScriptAsync("window.__zzzAdBlockElementPicker && window.__zzzAdBlockElementPicker.hide()").ConfigureAwait(true);
            }
            catch { }
            await _saveRuleAsync(rule).ConfigureAwait(true);
            LastError = string.Empty;
            RuleCreated?.Invoke(this, new AdBlockElementRuleCreatedEventArgs(rule));
            return rule;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        if (_disposed) return;
        _lastNativeMenuUtc = DateTime.UtcNow;
        _lastContextMenuUtc = _lastNativeMenuUtc;
        StopFallbackTimer();
        _lastPageUrl = args.ContextMenuTarget.PageUri ?? string.Empty;
        _lastFrameUrl = args.ContextMenuTarget.FrameUri ?? string.Empty;
        if (!IsHttpPage(_lastPageUrl)) return;
        args.MenuItems.Add(_menuItem);
    }

    private async void OnCustomItemSelected(object? sender, object args) => await CaptureAndSaveAsync().ConfigureAwait(true);

    private void OnCoreWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args) => OnPickerMessage(args, null);
    private void OnCoreFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args) => AttachFrame(args.Frame);

    private void AttachFrame(CoreWebView2Frame frame)
    {
        if (_disposed || _frames.ContainsKey(frame.FrameId)) return;
        var registration = new FrameRegistration(frame);
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> message = (_, e) => OnPickerMessage(e, frame);
        EventHandler<CoreWebView2NavigationStartingEventArgs> navigation = (_, e) => registration.PageUrl = e.Uri ?? string.Empty;
        EventHandler<CoreWebView2DOMContentLoadedEventArgs> loaded = async (_, _) => await ApplyFrameCosmeticRulesAsync(registration).ConfigureAwait(true);
        EventHandler<CoreWebView2FrameCreatedEventArgs> child = (_, e) => AttachFrame(e.Frame);
        EventHandler<object> destroyed = (_, _) => OnFrameDestroyed(registration);
        registration.Detach = () =>
        {
            // A destroyed CoreWebView2Frame no longer accepts event-subscription
            // changes. In particular, removing FrameCreated from inside the
            // Destroyed callback makes WebView2 150 fail-fast in
            // UpdateHasFrameCreatedEventHandlers.
            if (registration.IsDestroyed) return;
            try { frame.WebMessageReceived -= message; } catch { }
            try { frame.NavigationStarting -= navigation; } catch { }
            try { frame.DOMContentLoaded -= loaded; } catch { }
            try { frame.FrameCreated -= child; } catch { }
            try { frame.Destroyed -= destroyed; } catch { }
        };
        _frames[frame.FrameId] = registration;
        frame.WebMessageReceived += message;
        frame.NavigationStarting += navigation;
        frame.DOMContentLoaded += loaded;
        frame.FrameCreated += child;
        frame.Destroyed += destroyed;
    }

    private void OnFrameDestroyed(FrameRegistration registration)
    {
        // Only release our managed references here. Unsubscribing another frame
        // event while WebView2 is dispatching Destroyed is re-entrant and can
        // terminate the host process with 0x80000003.
        registration.IsDestroyed = true;
        _frames.Remove(registration.Id);
        if (ReferenceEquals(_lastCaptureFrame, registration.Frame)) _lastCaptureFrame = null;
    }

    private void OnPickerMessage(CoreWebView2WebMessageReceivedEventArgs args, CoreWebView2Frame? frame)
    {
        if (_disposed) return;
        try
        {
            var capture = JsonSerializer.Deserialize<PickerCapture>(args.WebMessageAsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (capture?.Kind != "zzz-adblock-picker" || capture.Token != _messageToken || string.IsNullOrWhiteSpace(capture.Selector) || capture.Selector.Length > 2048) return;
            capture.PageUrl = args.Source ?? capture.PageUrl;
            _lastCapture = capture;
            _lastCaptureFrame = frame;
            _lastCaptureUtc = DateTime.UtcNow;
            _lastPageUrl = IsHttpPage(_core.Source) ? _core.Source : _lastPageUrl;
            if (DateTime.UtcNow - _lastNativeMenuUtc > TimeSpan.FromMilliseconds(500)) StartFallbackTimer();
        }
        catch { }
    }

    private void StartFallbackTimer()
    {
        StopFallbackTimer();
        _fallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fallbackTimer.Tick += FallbackTimer_Tick;
        _fallbackTimer.Start();
    }

    private void FallbackTimer_Tick(object? sender, EventArgs e)
    {
        StopFallbackTimer();
        if (_disposed || _lastCapture is null || !IsHttpPage(_core.Source)) return;
        _lastPageUrl = _core.Source;
        _lastContextMenuUtc = _lastCaptureUtc;
        if (_fallbackMenu is not null) _fallbackMenu.IsOpen = false;
        var item = new MenuItem { Header = _menuLabel };
        item.Click += async (_, _) => await CaptureAndSaveAsync().ConfigureAwait(true);
        _fallbackMenu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _fallbackMenu.Items.Add(item);
        _fallbackMenu.Closed += (_, _) => _fallbackMenu = null;
        _fallbackMenu.IsOpen = true;
    }

    private void StopFallbackTimer()
    {
        if (_fallbackTimer is null) return;
        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= FallbackTimer_Tick;
        _fallbackTimer = null;
    }

    public async Task ApplyFrameCosmeticRulesAsync()
    {
        foreach (var registration in _frames.Values.ToArray())
            await ApplyFrameCosmeticRulesAsync(registration).ConfigureAwait(true);
    }

    private async Task ApplyFrameCosmeticRulesAsync(FrameRegistration registration)
    {
        var provider = _cosmeticCssProvider;
        if (_disposed || provider is null || registration.Frame.IsDestroyed() != 0) return;
        var scopeUrl = IsHttpPage(registration.PageUrl) ? registration.PageUrl : _core.Source;
        if (!IsHttpPage(scopeUrl)) return;
        try
        {
            var css = provider(scopeUrl);
            await registration.Frame.ExecuteScriptAsync(AdBlockElementPicker.BuildApplyCssScript(css)).ConfigureAwait(true);
        }
        catch { }
    }

    private static bool IsHttpPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var page) &&
        (page.Scheme == Uri.UriSchemeHttp || page.Scheme == Uri.UriSchemeHttps);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopFallbackTimer();
        if (_fallbackMenu is not null) { _fallbackMenu.IsOpen = false; _fallbackMenu = null; }
        if (_attached)
        {
            _core.ContextMenuRequested -= OnContextMenuRequested;
            _core.WebMessageReceived -= OnCoreWebMessageReceived;
            _core.FrameCreated -= OnCoreFrameCreated;
            _menuItem.CustomItemSelected -= OnCustomItemSelected;
        }
        foreach (var registration in _frames.Values.ToArray()) registration.Detach();
        _frames.Clear();
        if (!string.IsNullOrWhiteSpace(_scriptId))
        {
            try { _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId); } catch { }
        }
        _scriptId = string.Empty;
    }

    private sealed class PickerCapture
    {
        public string Kind { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string PageUrl { get; set; } = string.Empty;
        public string Selector { get; set; } = string.Empty;
    }

    private sealed class FrameRegistration(CoreWebView2Frame frame)
    {
        public uint Id { get; } = frame.FrameId;
        public CoreWebView2Frame Frame { get; } = frame;
        public string PageUrl { get; set; } = string.Empty;
        public bool IsDestroyed { get; set; }
        public Action Detach { get; set; } = () => { };
    }
}

public sealed class AdBlockElementRuleCreatedEventArgs(AdBlockElementRule rule) : EventArgs
{
    public AdBlockElementRule Rule { get; } = rule;
}

public static class AdBlockWebView2Mapper
{
    public static AdBlockResourceType FromWebView2(CoreWebView2WebResourceContext context, bool isSubdocument = false)
    {
        if (isSubdocument) return AdBlockResourceType.Subdocument;
        return context switch
        {
            CoreWebView2WebResourceContext.Document => AdBlockResourceType.Document,
            CoreWebView2WebResourceContext.Script => AdBlockResourceType.Script,
            CoreWebView2WebResourceContext.Stylesheet => AdBlockResourceType.Stylesheet,
            CoreWebView2WebResourceContext.Image => AdBlockResourceType.Image,
            CoreWebView2WebResourceContext.Font => AdBlockResourceType.Font,
            CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.TextTrack => AdBlockResourceType.Media,
            CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch or CoreWebView2WebResourceContext.EventSource => AdBlockResourceType.XmlHttpRequest,
            CoreWebView2WebResourceContext.Websocket => AdBlockResourceType.WebSocket,
            CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => AdBlockResourceType.Ping,
            _ => AdBlockResourceType.Other
        };
    }
}
