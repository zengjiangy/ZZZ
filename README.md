# ZZZ Browser

ZZZ is a compact, open-source Windows browser built with .NET 8, WPF, MVVM, and Microsoft WebView2. It stores configuration and library data as readable JSON under `%LOCALAPPDATA%\ZZZ` and uses the installed WebView2 runtime instead of bundling Chromium.

## Included

- Multiple tabs with duplicate, close-others, close-to-right, and background resource sleep
- Combined address/search bar with Bing, Google, Baidu, DuckDuckGo, and custom templates
- Typed settings for appearance, interface visibility, privacy, user-agent, downloads, and advanced features
- Request-level ad blocking, media URL sniffing, userscript injection, and developer tools
- Unified application and website light/dark themes, with Smart and Force web rendering strengths
- Runtime-switchable English, Simplified Chinese, Traditional Chinese, and Japanese interface resources
- DNT, best-effort third-party-cookie blocking, WebRTC blocking, and configurable site permissions
- Bookmark HTML import/export, history, settings backup/restore, rule import/export, and browsing-data cleanup
- Reactive bookmark indicator with click-to-add/remove behavior
- Isolated private tabs backed by a unique WebView2 InPrivate profile per tab
- Built-in download list plus configurable external downloader and media player
- SVG-authored multi-resolution application icon and automatic last-session restoration
- Built-in visible-page region capture with clipboard copy and PNG saving

## Private tabs

Create one from the main menu or press `Ctrl+Shift+N`. Each private tab uses its own off-the-record WebView2 profile, does not share cookies, cache, or local storage with normal or other private tabs, and is excluded from ZZZ history and session restoration. Closing the tab destroys its temporary web data. Bookmarks and files explicitly saved by the user remain persistent.

## Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+T | New tab |
| Ctrl+Shift+N | New private tab |
| Ctrl+W | Close tab |
| Ctrl+L | Focus address bar |
| Ctrl+R | Reload |
| Ctrl+Shift+T | Reopen most recent history entry |
| Alt+Left / Alt+Right | Back / forward |
| F12 | Developer tools (when enabled) |
| Ctrl+Shift+S | Capture a region of the visible page |

## Build

```powershell
dotnet build ZZZ.sln -c Release
dotnet publish ZZZ\ZZZ.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

WebView2 Evergreen Runtime is required and is normally already present on supported Windows systems.

## License

[MIT](LICENSE)
