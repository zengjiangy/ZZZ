# ZZZ Browser

[简体中文](README.md) | [English](README.en.md)

ZZZ is a lightweight, open-source browser for Windows, built with .NET Framework 4.8, WPF, and Microsoft WebView2. It uses the system WebView2 Runtime instead of bundling Chromium and can keep all browser data beside the executable for portable use.

Current version: **1.9.5**

## Download

Download the latest build from [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest):

| File | Platform |
|---|---|
| `ZZZ-v1.9.5-win-x64.exe` | Native Windows x64 build |
| `ZZZ-v1.9.5-win-arm64.exe` | Native Windows ARM64 build |

No installer is required. Windows 10 or 11, .NET Framework 4.8, and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) are required.

The WinGet community submission is under review in [microsoft/winget-pkgs#402023](https://github.com/microsoft/winget-pkgs/pull/402023).

## Highlights

- Interface language selection for Simplified Chinese, Traditional Chinese, English, Japanese, Korean, Portuguese, Spanish, Russian, French, and German
- Tabs, session restore, private tabs, and horizontal or vertical split view
- Combined address and search box with history matches and live suggestions
- Grouped bookmarks, bookmark HTML import/export, and a customizable native start page
- Per-pane zoom, in-page find, printing, full-page PDF, and MHT archive export
- Built-in translation, userscripts, configurable user agent, and web theme rendering
- Download manager with file size, progress, MIME type, timestamps, save path, and double-click open
- Optional external downloaders and media players, with a warning for cookie-authenticated resources
- DNT, GPC, Public-Suffix-List-aware third-party cookie blocking, document-start/frame-level WebRTC controls, and native-deny geolocation mocking
- Userscript `@grant`/`@connect` enforcement in both JavaScript and the C# broker, with isolated per-script authorization
- Userscript requests with WebView2 session cookies, progress/readystate events, abort/timeout support, a 64 MB in-memory response limit, and streaming background `GM_download`
- Built-in EasyList, EasyList China, CJX's Annoyance List, EasyPrivacy, and Adblock Warning Removal subscriptions, plus custom HTTPS lists and ABP rules
- Manual, daily, or weekly filter updates; ABP network and cosmetic filtering; and a page context-menu command for blocking an ad element
- WebView2 process recovery, runtime update notices, and periodic non-private session snapshots
- Local AppData, portable, or custom browser-data storage
- Complete UI resources for Simplified Chinese, Traditional Chinese, English, Japanese, Korean, Portuguese, Spanish, Russian, French, and German

## Private tabs

Use the main menu or press `Ctrl+Shift+N`. Private tabs use isolated WebView2 profiles and do not retain history, sessions, cache, cookies, or online search suggestions. Their temporary directory is protected by a current-user ACL, EFS when available, a crash/force-close cleanup watchdog, and next-launch cleanup. Files explicitly downloaded and bookmarks explicitly saved by the user remain persistent.

## Portable mode

Open **Settings → Backup → Data and cookie storage location**, select **Portable mode**, save, and restart. When moving the browser, copy `ZZZ.exe`, the `Data` directory, and `zzz-data-location.json` together.

## Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+T` | New tab |
| `Ctrl+Shift+N` | New private tab |
| `Ctrl+W` | Close current tab |
| `Ctrl+L` / `Alt+D` | Focus address bar |
| `Ctrl+R` | Reload |
| `Ctrl+P` | Print |
| `Ctrl+F` | Find in page |
| `Ctrl+Shift+W` | Close split view |
| `Ctrl+Shift+T` | Open the most recent history entry |
| `Alt+Left` / `Alt+Right` | Back / forward |
| `F11` | Full screen |
| `F12` | Developer tools, when enabled in Settings |

## Build

```powershell
dotnet build ZZZ.sln -c Release
```

The x64 single-file output is `ZZZ\bin\Release\net48\ZZZ.exe`. Managed dependencies and x86, x64, and ARM64 WebView2 native loaders are embedded in the executable.

Native ARM64 build:

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## Support and license

- Issues: [GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- License: [MIT License](LICENSE)
- Third-party components and data: [Third-party notices](THIRD-PARTY-NOTICES.md)
