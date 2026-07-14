# ZZZ 浏览器

ZZZ 是一款精简、开源的 Windows 浏览器，基于 .NET Framework 4.8、WPF、MVVM 和 Microsoft WebView2。它使用系统安装的 WebView2 Runtime，不额外捆绑 Chromium，并支持将全部浏览数据放在 EXE 同目录以便 U 盘携带。

当前版本：**1.5.3**。

## Download

从 [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest) 下载普通 Windows 单文件版或本次提供的 Windows ARM64 单文件版。无需 .NET 8 Desktop Runtime；系统需提供 .NET Framework 4.8 和 Microsoft Edge WebView2 Evergreen Runtime。

## 主要功能

- 原生低开销启动页，仅包含搜索/地址输入框和可选书签，不启动 WebView2；支持纯色、图片与动态 GIF 背景
- 可选择以内置启动页或当前搜索引擎网站作为主页
- 多标签页、复制标签、关闭其他/右侧标签及后台标签休眠
- 支持自适应左右/上下双页面分屏、各窗格独立缩放、快捷关闭、F11 沉浸式全屏，以及 `Ctrl+F` 页内查找
- 可打印当前网页，并将完整页面保存为 PDF 或 MHT 网页归档
- 地址/搜索合一，输入时即时匹配本地历史，并为 Bing、Google、百度、DuckDuckGo 提供防抖在线搜索联想；支持自定义搜索模板
- Typed settings for appearance, interface visibility, privacy, user-agent, downloads, and advanced features
- Request/response media sniffing by URL, MIME type, HLS/DASH manifest, and content metadata
- Tampermonkey-style userscripts with metadata import, run timing, match/exclude rules, `@require`, resources, persistent values, and common `GM_*` APIs
- 微软 Edge 风格页内翻译、Google Chrome 兼容代理翻译，以及语言不一致时自动翻译
- Unified application and website light/dark themes, with Smart and Force web rendering strengths
- Runtime-switchable English, Simplified Chinese, Traditional Chinese, and Japanese interface resources
- DNT、GPC、平衡式第三方跟踪 Cookie 限制、WebRTC 限制及可配置站点权限；位置请求可逐次或始终返回自定义坐标
- Bookmark HTML import/export (including folder groups), history, settings backup/restore, rule import/export, and modern in-app browsing-data cleanup
- 书签支持分组编辑和筛选，主页按组展示；每条书签可单独选择是否出现在主页，并可统一调整主页书签的样式和宽度
- 历史记录支持双击直接打开、删除单条记录或清空全部记录
- Reactive bookmark indicator with click-to-add/remove behavior
- Strict private tabs backed by unique InPrivate profiles in a separate per-session temporary WebView2 environment
- 内置下载管理器显示文件大小、传输进度、MIME、开始/完成时间、状态及保存位置，支持双击打开；也可配置外部下载器和媒体播放器，并提示 Cookie 鉴权资源优先使用内置下载
- 本机 AppData、EXE 同目录便携模式或自定义数据/Cookie 路径，并支持重启迁移
- SVG 多分辨率图标与上次会话恢复
- A single shared browser instance accepts URLs from later launches; inactive tabs request WebView2's low-memory mode

## Private tabs

Create one from the main menu or press `Ctrl+Shift+N`. Each private tab uses its own off-the-record WebView2 profile, does not share cookies, cache, or local storage with normal or other private tabs, and is excluded from ZZZ history and session restoration. Closing the tab destroys its temporary web data. Bookmarks and files explicitly saved by the user remain persistent.

Strict private tabs also use a separate per-session temporary WebView2 data directory. Site permissions, autofill, password saving, and persistent HTTP cache are disabled; userscript values remain in memory only. The directory is removed when ZZZ exits, with stale-session cleanup on the next launch as a fallback.

## Userscripts

Open **Library → Userscripts** to create a script or import a `.user.js` file. ZZZ 1.1 reads standard metadata and supports `document-start`, `document-end`, `document-idle`, `@match`, `@include`, `@exclude`, `@require`, `@resource`, and common legacy/modern `GM_*` APIs. Imported `@require` dependencies are cached with the script so document-start execution does not wait for the network.

## 网页翻译

点击地址栏旁的 **译**。默认的微软方案以 Edge 请求身份获取短期令牌，在当前页面内分批翻译文本，不跳转到地区受限的代理页；翻译完成后再次点击 **译** 即可恢复原文，再次点击可重新翻译。Google 方案同样支持再次点击返回原网站。目标语言和自动翻译开关位于 **设置 → 高级**。

## 便携模式

在 **设置 → 备份 → 数据与 Cookie 存储位置** 中选择“便携模式”，保存后重启。ZZZ 会在 WebView2 启动前把设置、书签、历史、脚本、Cookie 和缓存迁移到 EXE 同目录的 `Data` 文件夹。移动时请同时复制 `ZZZ.exe`、`Data` 和 `zzz-data-location.json`。

## Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+T | New tab |
| Ctrl+Shift+N | New private tab |
| Ctrl+W | Close tab |
| Ctrl+L | Focus address bar |
| Alt+D | Focus address bar |
| Ctrl+R | Reload |
| Ctrl+P | Print |
| Ctrl+F | Find in page |
| Ctrl+Shift+W | Close split view (when active) |
| Ctrl+Shift+T | Reopen most recent history entry |
| Alt+Left / Alt+Right | Back / forward |
| F12 | Developer tools (when enabled) |
| F11 | Full screen |

## Build

```powershell
dotnet build ZZZ.sln -c Release
```

The standalone output is `ZZZ\bin\Release\net48\ZZZ.exe`. Managed dependencies and the x86/x64/ARM64 WebView2 native loaders are embedded into this file. The `Microsoft.NETFramework.ReferenceAssemblies.net48` package is build-only and is not shipped to users.

本次 ARM64 构建命令：

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## License

[MIT](LICENSE)
