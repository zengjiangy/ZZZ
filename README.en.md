# ZZZ Browser

[简体中文](README.md) | [English](README.en.md)

ZZZ is a lightweight, open-source browser for Windows, built with .NET Framework 4.8, WPF, and Microsoft WebView2. It uses the system WebView2 Runtime instead of bundling Chromium and can keep all browser data beside the executable for portable use.

Current version: **1.5.3**

## Download

Download the latest build from [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest):

| File | Platform |
|---|---|
| `ZZZ.exe` | Standard Windows build, recommended for x64 devices |
| `ZZZ-v1.5.3-win-arm64.exe` | Native Windows ARM64 build |

No installer is required. Windows 10 or 11, .NET Framework 4.8, and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) are required.

The WinGet community submission is under review in [microsoft/winget-pkgs#402023](https://github.com/microsoft/winget-pkgs/pull/402023).

## Highlights

- Tabs, session restore, private tabs, and horizontal or vertical split view
- Combined address and search box with history matches and live suggestions
- Grouped bookmarks, bookmark HTML import/export, and a customizable native start page
- Per-pane zoom, in-page find, printing, full-page PDF, and MHT archive export
- Built-in translation, userscripts, configurable user agent, and web theme rendering
- Download manager with file size, progress, MIME type, timestamps, save path, and double-click open
- Optional external downloaders and media players, with a warning for cookie-authenticated resources
- DNT, GPC, third-party tracking-cookie restrictions, WebRTC controls, and configurable site permissions
- Local AppData, portable, or custom browser-data storage
- English, Simplified Chinese, Traditional Chinese, and Japanese UI resources

## Private tabs

Use the main menu or press `Ctrl+Shift+N`. Private tabs use isolated WebView2 profiles and do not retain history, sessions, cache, cookies, or online search suggestions. Files explicitly downloaded and bookmarks explicitly saved by the user remain persistent.

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

The standard single-file output is `ZZZ\bin\Release\net48\ZZZ.exe`. Managed dependencies and x86, x64, and ARM64 WebView2 native loaders are embedded in the executable.

Native ARM64 build:

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## Support and license

- Issues: [GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- License: [MIT License](LICENSE)
