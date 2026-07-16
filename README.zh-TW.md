# ZZZ 瀏覽器

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [한국어](README.ko.md) | [繁體中文](README.zh-TW.md)

ZZZ 是一款精簡、開放原始碼的 Windows 瀏覽器，以 .NET Framework 4.8、WPF 與 Microsoft WebView2 建構。它使用系統中的 WebView2 Runtime，不另外綑綁 Chromium，也能將瀏覽器資料存放於執行檔旁，方便隨身使用。

目前版本：**2.0.0**

## 下載與系統需求

請從 [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest) 下載最新版本。

| 檔案 | 適用平台 |
|---|---|
| `ZZZ-v2.0.0-win-x64.exe` | Windows x64 原生版本 |
| `ZZZ-v2.0.0-win-x86.exe` | Windows 10 x86 32 位元相容版；亦可在 Windows 10 on Arm 透過 x86 模擬執行 |
| `ZZZ-v2.0.0-win-arm64.exe` | Windows ARM64 原生版本 |

下載後即可執行，無須安裝。系統需要 Windows 10 或 Windows 11、.NET Framework 4.8，以及 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

## 主要功能

- 多語言介面、多分頁、還原最近關閉的頁面，以及水平或垂直分割檢視
- 整合網址與搜尋的位址列，支援歷史記錄比對與即時搜尋建議
- 可分組及編輯的書籤，並支援 HTML 匯入與匯出
- 輕量原生啟動頁，可自訂純色、圖片或動態 GIF 背景
- 頁面內尋找、列印、儲存為 PDF 或 MHT、全螢幕及各窗格獨立縮放
- F9 閱讀模式、全應用程式黑白模式、可編輯書籤名稱與重視隱私的「關於」頁面
- 僅記錄一般分頁且可停用的原子工作階段還原，以及網頁程序啟動前的一次性使用條款確認
- 網頁翻譯、使用者腳本、User-Agent 切換與網頁明暗顯示
- 內建 EasyList 訂閱、自訂 ABP 規則，以及透過右鍵選取廣告元素
- 顯示進度、MIME 類型與儲存位置的下載管理員，並可搭配外部工具
- 瀏覽器資料可存於 AppData、執行檔旁或自訂資料夾

## 隱私

每個隱私分頁都使用獨立的 WebView2 設定檔，不保留歷史記錄、工作階段、快取、Cookie 或線上搜尋建議。關閉分頁後會清除暫存資料；若程式異常結束，監護程序及下次啟動時的清理機制會繼續重試。使用者明確儲存的下載檔案與書籤仍會保留。

一般瀏覽亦提供 DNT、GPC、依據 Public Suffix List 的第三方 Cookie 阻擋、WebRTC 限制與網站權限管理。

## 可攜模式

在「**設定 → 備份 → 資料與 Cookie 儲存位置**」選擇可攜模式，儲存後重新啟動。移動瀏覽器時，請一併複製 `ZZZ.exe`、`Data` 資料夾與 `zzz-data-location.json`。

## 建置

```powershell
dotnet build ZZZ.sln -c Release
```

x64 輸出位於 `ZZZ\bin\Release\net48\ZZZ.exe`。x86 32 位元相容版可使用下列命令建置：

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=x86 -p:OutputPath=outputs\win-x86\
```

WebView2 不提供 ARM32 Runtime 或 Loader；Windows 10 on Arm 若需要 32 位元相容程式，請使用 x86 模擬版本，原生版本則建置為 ARM64。

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## 支援與授權

- 問題與建議：[GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- 開放原始碼授權：[MIT License](LICENSE)
- 第三方元件與資料：[Third-party notices](THIRD-PARTY-NOTICES.md)
