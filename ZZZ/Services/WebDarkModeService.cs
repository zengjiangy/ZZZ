using Microsoft.Web.WebView2.Core;
using ZZZ.Configuration;

namespace ZZZ.Services;

public static class WebDarkModeService
{
    public const string SmartScript = """
        (() => {
          const apply = () => {
            if (!document.documentElement) return;
            document.documentElement.style.setProperty('color-scheme', 'dark', 'important');
            document.documentElement.style.setProperty('background-color', '#111318', 'important');
          };
          apply();
          document.addEventListener('DOMContentLoaded', apply, { once: true });
        })();
        """;

    // Native Chromium auto-dark does most of the work. This fallback remaps bright
    // DOM colors, including dynamically inserted content and open shadow roots.
    public const string ForceScript = """
        (() => {
          if (window.__zzzStrongDarkActive) return;
          window.__zzzStrongDarkActive = true;
          const SKIP = new Set(['IMG','VIDEO','CANVAS','SVG','PICTURE','IFRAME','OBJECT','EMBED']);
          const seen = new WeakSet();
          const parse = value => {
            const m = value && value.match(/rgba?\((\d+)[, ]+(\d+)[, ]+(\d+)(?:[, /]+([\d.]+))?\)/i);
            return m ? [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : +m[4]] : null;
          };
          const luminance = c => (0.2126*c[0] + 0.7152*c[1] + 0.0722*c[2]) / 255;
          const darken = c => {
            const max = Math.max(c[0], c[1], c[2]) || 1;
            const scale = Math.min(1, 54 / max);
            return `rgb(${Math.round(c[0]*scale)},${Math.round(c[1]*scale)},${Math.round(c[2]*scale)})`;
          };
          const lighten = c => {
            const max = Math.max(c[0], c[1], c[2]);
            const scale = Math.min(5, 226 / Math.max(1, max));
            return `rgb(${Math.min(238,Math.round(c[0]*scale+24))},${Math.min(238,Math.round(c[1]*scale+24))},${Math.min(238,Math.round(c[2]*scale+24))})`;
          };
          const remap = el => {
            if (!(el instanceof Element) || SKIP.has(el.tagName) || seen.has(el)) return;
            seen.add(el);
            const s = getComputedStyle(el);
            const bg = parse(s.backgroundColor);
            const fg = parse(s.color);
            if (bg && bg[3] > .08 && luminance(bg) > .62)
              el.style.setProperty('background-color', darken(bg), 'important');
            if (fg && fg[3] > .2 && luminance(fg) < .43)
              el.style.setProperty('color', lighten(fg), 'important');
            for (const p of ['border-top-color','border-right-color','border-bottom-color','border-left-color']) {
              const c = parse(s.getPropertyValue(p));
              if (c && c[3] > .08 && luminance(c) > .58) el.style.setProperty(p, '#41444b', 'important');
            }
            if (el.shadowRoot) processRoot(el.shadowRoot);
          };
          const processRoot = root => {
            const all = root.querySelectorAll ? Array.from(root.querySelectorAll('*')) : [];
            let i = 0;
            const batch = deadline => {
              while (i < all.length && (!deadline || deadline.timeRemaining() > 2)) remap(all[i++]);
              if (i < all.length) (window.requestIdleCallback || (f => setTimeout(() => f(null), 1)))(batch);
            };
            batch(null);
          };
          const start = () => {
            if (!document.documentElement) return;
            document.documentElement.style.setProperty('color-scheme','dark','important');
            document.documentElement.style.setProperty('background','#111318','important');
            if (document.body) {
              document.body.style.setProperty('background-color','#111318','important');
              processRoot(document);
              new MutationObserver(records => records.forEach(r => r.addedNodes.forEach(n => {
                if (n instanceof Element) { remap(n); processRoot(n); }
              }))).observe(document.documentElement, { childList:true, subtree:true });
            }
            const style = document.createElement('style');
            style.id = 'zzz-strong-dark-style';
            style.textContent = `
              :root { color-scheme: dark !important; }
              ::selection { background:#65558f !important; color:#fff !important; }
              input,textarea,select,button { color-scheme:dark !important; border-color:#454850 !important; }
              a:link { color:#8ab4f8 !important; } a:visited { color:#c7a0ff !important; }
              img,video,picture,canvas,svg { filter:none !important; }
              ::-webkit-scrollbar { width:11px; height:11px; }
              ::-webkit-scrollbar-thumb { background:#4a4d55; border-radius:8px; border:2px solid #202228; }
              ::-webkit-scrollbar-track { background:#202228; }
            `;
            (document.head || document.documentElement).appendChild(style);
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, {once:true}); else start();
        })();
        """;

    public static string ScriptFor(WebContentDarkMode mode) => mode switch
    {
        WebContentDarkMode.Smart => SmartScript,
        WebContentDarkMode.Force => ForceScript,
        _ => string.Empty
    };

    public static async Task ApplyNativeModeAsync(CoreWebView2 core, WebContentDarkMode mode)
    {
        core.Profile.PreferredColorScheme = mode == WebContentDarkMode.Off
            ? CoreWebView2PreferredColorScheme.Auto
            : CoreWebView2PreferredColorScheme.Dark;
        try
        {
            var enabled = mode == WebContentDarkMode.Force ? "true" : "false";
            await core.CallDevToolsProtocolMethodAsync("Emulation.setAutoDarkModeOverride", $"{{\"enabled\":{enabled}}}");
        }
        catch { /* Older runtimes still receive the CSS/DOM fallback. */ }
    }
}
