# ZZZ Browser

ZZZ is a compact, open-source Windows browser built with .NET Framework 4.8, WPF, MVVM, and Microsoft WebView2. It stores configuration and library data as readable JSON under `%LOCALAPPDATA%\ZZZ` and uses the installed WebView2 runtime instead of bundling Chromium.

Current release: **1.1**.

## Download

Download the current standalone executable from [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest/download/ZZZ.exe). No .NET 8 Desktop Runtime is required. Windows must provide .NET Framework 4.8 and the Microsoft Edge WebView2 Evergreen Runtime.

## Included

- Multiple tabs with duplicate, close-others, close-to-right, and background resource sleep
- Combined address/search bar with Bing, Google, Baidu, DuckDuckGo, and custom templates
- Typed settings for appearance, interface visibility, privacy, user-agent, downloads, and advanced features
- Request/response media sniffing by URL, MIME type, HLS/DASH manifest, and content metadata
- Tampermonkey-style userscripts with metadata import, run timing, match/exclude rules, `@require`, resources, persistent values, and common `GM_*` APIs
- One-click page translation through Google (Chrome-compatible request identity) or Microsoft, with a configurable target language
- Unified application and website light/dark themes, with Smart and Force web rendering strengths
- Runtime-switchable English, Simplified Chinese, Traditional Chinese, and Japanese interface resources
- DNT, best-effort third-party-cookie blocking, WebRTC blocking, and configurable site permissions
- Bookmark HTML import/export, history, settings backup/restore, rule import/export, and browsing-data cleanup
- Reactive bookmark indicator with click-to-add/remove behavior
- Strict private tabs backed by unique InPrivate profiles in a separate per-session temporary WebView2 environment
- Built-in download list plus configurable external downloader and media player
- SVG-authored multi-resolution application icon and automatic last-session restoration
- Built-in visible-page region capture with clipboard copy and PNG saving
- A single shared browser instance accepts URLs from later launches; inactive tabs request WebView2's low-memory mode

## Private tabs

Create one from the main menu or press `Ctrl+Shift+N`. Each private tab uses its own off-the-record WebView2 profile, does not share cookies, cache, or local storage with normal or other private tabs, and is excluded from ZZZ history and session restoration. Closing the tab destroys its temporary web data. Bookmarks and files explicitly saved by the user remain persistent.

Strict private tabs also use a separate per-session temporary WebView2 data directory. Site permissions, autofill, password saving, and persistent HTTP cache are disabled; userscript values remain in memory only. The directory is removed when ZZZ exits, with stale-session cleanup on the next launch as a fallback.

## Userscripts

Open **Library → Userscripts** to create a script or import a `.user.js` file. ZZZ 1.1 reads standard metadata and supports `document-start`, `document-end`, `document-idle`, `@match`, `@include`, `@exclude`, `@require`, `@resource`, and common legacy/modern `GM_*` APIs. Imported `@require` dependencies are cached with the script so document-start execution does not wait for the network.

## Page translation

Use the **译** button beside the address bar. The provider and BCP-47 target language code are configurable under **Settings → Advanced**. Google translation uses a desktop Chrome-compatible user agent only for the translation page; normal browsing keeps the selected user-agent setting.

## Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+T | New tab |
| Ctrl+Shift+N | New private tab |
| Ctrl+W | Close tab |
| Ctrl+L | Focus address bar |
| Alt+D | Focus address bar |
| Ctrl+R | Reload |
| Ctrl+Shift+T | Reopen most recent history entry |
| Alt+Left / Alt+Right | Back / forward |
| F12 | Developer tools (when enabled) |
| Ctrl+Shift+S | Capture a region of the visible page |

## Build

```powershell
dotnet build ZZZ.sln -c Release
```

The standalone output is `ZZZ\bin\Release\net48\ZZZ.exe`. Managed dependencies and the x86/x64 WebView2 native loaders are embedded into this file. The `Microsoft.NETFramework.ReferenceAssemblies.net48` package is build-only and is not shipped to users.

## License

[MIT](LICENSE)
