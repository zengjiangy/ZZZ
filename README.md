# ZZZ 浏览器

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [한국어](README.ko.md) | [繁體中文](README.zh-TW.md)

ZZZ 是一款精简、开源的 Windows 浏览器，基于 .NET Framework 4.8、WPF 和 Microsoft WebView2 构建。它使用系统中的 WebView2 Runtime，不额外捆绑 Chromium，并支持把浏览器数据存放在程序目录中，方便随身携带。

当前版本：**2.0.5**

## 下载与运行

请从 [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest) 下载：

| 文件 | 适用平台 |
|---|---|
| `ZZZ-v2.0.5-win-x64.exe` | Windows x64 原生版本 |
| `ZZZ-v2.0.5-win-x86.exe` | Windows 10 x86 32 位兼容版；也可在 Windows 10 on Arm 上以 x86 仿真运行 |
| `ZZZ-v2.0.5-win-arm64.exe` | Windows ARM64 原生版本 |

下载后直接运行即可，无需安装。系统需要：

- Windows 10 或 Windows 11
- .NET Framework 4.8
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

WinGet 社区仓库收录申请正在 [microsoft/winget-pkgs#402023](https://github.com/microsoft/winget-pkgs/pull/402023) 审核中。

## 主要功能

### 浏览与界面

- 可在简体中文、繁体中文、英语、日语、韩语、葡萄牙语、西班牙语、俄语、法语和德语之间切换界面语言
- 多标签页、复制标签页、恢复最近关闭页面，以及关闭其他或右侧标签页
- 左右或上下分屏浏览，支持各窗格独立缩放和 `Ctrl+Shift+W` 快速退出分屏
- 地址栏统一处理网址与搜索，支持历史记录匹配和实时搜索联想
- 原生轻量启动页，可使用纯色、图片或动态 GIF 背景，并按分组展示书签
- F11 沉浸式全屏、页内查找、打印，以及将网页保存为 PDF 或 MHT
- F9 阅读模式、全应用黑白模式，以及应用和网页明暗主题；启动页会根据背景亮度自动调整文字和面板对比度
- 可选择是否记录并提示恢复上次普通标签会话；记录独立于历史数据且从不包含隐私标签
- 首次运行先显示简短使用条款；同意状态只保存在本地，拒绝不会启动网页

### 书签与数据

- 书签分组、编辑和筛选，支持单独控制书签是否显示在主页
- 导入或导出浏览器书签 HTML，备份和恢复设置、规则与浏览数据
- 历史记录支持双击打开、删除单条记录和全部清空
- 数据可存放在本机 AppData、程序目录或自定义路径，并支持迁移

### 隐私与权限

- 独立隐私标签页，不保留历史、会话、缓存、Cookie 或在线搜索联想
- DNT、GPC、基于完整 Public Suffix List 的严格第三方 Cookie 阻止，以及 document-start/子框架级 WebRTC 限制
- 可配置摄像头、麦克风、通知、剪贴板和位置等站点权限
- 原生位置权限始终拒绝；可在隔离的模拟层中询问或返回自定义坐标

### 网页增强

- 内置微软网页翻译和 Google 兼容代理翻译，可自动翻译语言不一致的页面
- Tampermonkey 风格用户脚本，支持匹配规则、运行时机、`@require`、`@grant`、`@connect`、资源和常用 `GM_*` API
- `GM_xmlhttpRequest` 支持当前 WebView2 会话 Cookie、上传/下载进度、细粒度 readyState、超时、中止及最高 64 MB 内存响应；`GM_download` 使用后台文件流处理更大文件
- 广告拦截内置 EasyList、EasyList China、CJX's Annoyance List、EasyPrivacy 和 Adblock Warning Removal List，可添加自定义订阅和 ABP 规则，并支持手动、每天或每周更新
- 支持 ABP 网络与元素隐藏规则；可在网页中右键“标记为广告”，立即隐藏元素并保存站点规则
- 可切换 User-Agent，并提供智能或强制网页明暗渲染
- F12 开发者工具可在设置中启用或关闭

### 下载与媒体

- 内置下载管理器显示文件名、大小、进度、MIME 类型、开始及完成时间和保存位置
- 下载完成后可双击打开文件
- 可配置外部下载器和媒体播放器
- 对需要 Cookie 或登录状态鉴权的资源，提示优先使用内置下载
- 可按 URL、MIME 类型和内容信息识别媒体及 HLS/DASH 清单

## 隐私标签页

通过主菜单或 `Ctrl+Shift+N` 新建隐私标签页。每个隐私标签页使用独立的 WebView2 隐私配置，不与普通标签页或其他隐私标签页共享 Cookie、缓存和本地存储，也不会写入 ZZZ 历史记录或会话恢复数据。

隐私数据目录使用仅当前用户可访问的 ACL，并在系统支持时使用 EFS 加密。关闭标签页后会清理数据；独立监护进程会在主程序崩溃或被强制结束后继续重试清理，下次启动仍会执行兜底清理。用户主动保存的下载文件和书签仍会保留。

## 网页翻译

点击地址栏旁的“译”即可翻译当前网页。微软方案会在页面内分批翻译文本，不跳转到代理页面；再次点击可恢复原文。翻译目标语言和自动翻译选项位于“设置 → 高级”。

## 用户脚本

打开“库 → 用户脚本”即可新建脚本或导入 `.user.js` 文件。支持 `document-start`、`document-end`、`document-idle`、`@match`、`@include`、`@exclude`、`@require`、`@resource` 和常用 `GM_*` API。特权 API 必须通过 `@grant` 明确声明；`GM_xmlhttpRequest` 与 `GM_download` 的跨域目标还必须通过 `@connect` 声明。未声明的能力会同时在 JavaScript 和 C# 后端拒绝。

## 便携模式

在“设置 → 备份 → 数据与 Cookie 存储位置”中选择“便携模式”，保存后重启。ZZZ 会把设置、书签、历史、脚本、Cookie 和缓存存放到 EXE 同目录的 `Data` 文件夹。

移动程序时，请一并复制：

- `ZZZ.exe`
- `Data` 文件夹
- `zzz-data-location.json`

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+T` | 新建标签页 |
| `Ctrl+Shift+N` | 新建隐私标签页 |
| `Ctrl+W` | 关闭当前标签页 |
| `Ctrl+L` / `Alt+D` | 聚焦地址栏 |
| `Ctrl+R` | 刷新页面 |
| `Ctrl+P` | 打印页面 |
| `Ctrl+F` | 在页面中查找 |
| `Ctrl+Shift+W` | 退出当前分屏 |
| `Ctrl+Shift+T` | 打开最近一条历史记录 |
| `Alt+Left` / `Alt+Right` | 后退 / 前进 |
| `F9` | 切换阅读模式 |
| `F11` | 全屏 |
| `F12` | 开发者工具（需在设置中启用） |

## 构建

```powershell
dotnet build ZZZ.sln -c Release
```

x64 版本输出位于 `ZZZ\bin\Release\net48\ZZZ.exe`。托管依赖以及 x86、x64、ARM64 WebView2 原生加载器会嵌入到对应架构的单文件 EXE 中。

x86 32 位兼容构建：

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=x86 -p:OutputPath=outputs\win-x86\
```

WebView2 不提供 ARM32 Runtime 或 Loader；Windows 10 on Arm 需要 32 位兼容程序时请使用上述 x86 构建（系统 x86 仿真），原生性能请选择 ARM64 构建。

ARM64 构建命令：

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## 反馈与许可

- 问题反馈：[GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- 开源许可：[MIT License](LICENSE)
- 第三方组件与数据：[Third-party notices](THIRD-PARTY-NOTICES.md)
