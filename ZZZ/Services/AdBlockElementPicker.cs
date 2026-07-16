using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ZZZ.Models;

namespace ZZZ.Services;

public static class AdBlockElementPicker
{
    public static async Task<AdBlockElementPickerSession> AttachAsync(
        CoreWebView2 core,
        string menuLabel,
        Func<AdBlockElementRule, Task> saveRuleAsync)
    {
        if (core is null) throw new ArgumentNullException(nameof(core));
        if (saveRuleAsync is null) throw new ArgumentNullException(nameof(saveRuleAsync));
        var session = new AdBlockElementPickerSession(core, string.IsNullOrWhiteSpace(menuLabel) ? "Block this element" : menuLabel, saveRuleAsync);
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
document.addEventListener('contextmenu', event => { target = event.target && event.target.nodeType === 1 ? event.target : null; }, true);
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
    private readonly CoreWebView2ContextMenuItem _menuItem;
    private string _scriptId = string.Empty;
    private string _lastPageUrl = string.Empty;
    private bool _attached;
    private bool _disposed;

    internal AdBlockElementPickerSession(CoreWebView2 core, string menuLabel, Func<AdBlockElementRule, Task> saveRuleAsync)
    {
        _core = core;
        _saveRuleAsync = saveRuleAsync;
        _menuItem = core.Environment.CreateContextMenuItem(menuLabel, null!, CoreWebView2ContextMenuItemKind.Command);
    }

    public event EventHandler<AdBlockElementRuleCreatedEventArgs>? RuleCreated;
    public string LastError { get; private set; } = string.Empty;

    internal async Task AttachAsync()
    {
        if (_attached) return;
        _scriptId = await _core.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockElementPicker.Bootstrap).ConfigureAwait(true);
        try { await _core.ExecuteScriptAsync(AdBlockElementPicker.Bootstrap).ConfigureAwait(true); } catch { }
        _core.ContextMenuRequested += OnContextMenuRequested;
        _menuItem.CustomItemSelected += OnCustomItemSelected;
        _attached = true;
    }

    public async Task<AdBlockElementRule?> CaptureAndSaveAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdBlockElementPickerSession));
        try
        {
            var json = await _core.ExecuteScriptAsync("window.__zzzAdBlockElementPicker ? window.__zzzAdBlockElementPicker.capture() : null").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
            var capture = JsonSerializer.Deserialize<PickerCapture>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (capture is null || string.IsNullOrWhiteSpace(capture.Selector)) return null;
            var pageUrl = string.IsNullOrWhiteSpace(capture.PageUrl) ? _lastPageUrl : capture.PageUrl;
            var rule = new AdBlockElementRule { PageUrl = pageUrl, Selector = capture.Selector };
            // Give immediate visual feedback while the combined subscription
            // snapshot is rebuilt and the new rule is persisted in the background.
            try { await _core.ExecuteScriptAsync("window.__zzzAdBlockElementPicker && window.__zzzAdBlockElementPicker.hide()").ConfigureAwait(true); } catch { }
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
        if (_disposed || !args.ContextMenuTarget.IsRequestedForMainFrame) return;
        _lastPageUrl = args.ContextMenuTarget.PageUri ?? string.Empty;
        if (!Uri.TryCreate(_lastPageUrl, UriKind.Absolute, out var page) ||
            (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps)) return;
        args.MenuItems.Add(_menuItem);
    }

    private async void OnCustomItemSelected(object? sender, object args) => await CaptureAndSaveAsync().ConfigureAwait(true);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached)
        {
            _core.ContextMenuRequested -= OnContextMenuRequested;
            _menuItem.CustomItemSelected -= OnCustomItemSelected;
        }
        if (!string.IsNullOrWhiteSpace(_scriptId))
        {
            try { _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId); } catch { }
        }
        _scriptId = string.Empty;
    }

    private sealed class PickerCapture
    {
        public string PageUrl { get; set; } = string.Empty;
        public string Selector { get; set; } = string.Empty;
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
