# ZZZ ブラウザー

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [한국어](README.ko.md) | [繁體中文](README.zh-TW.md)

公式サイト：[zzz.campusphere.ltd](https://zzz.campusphere.ltd/)

ZZZ は、.NET Framework 4.8、WPF、Microsoft WebView2 で構築された、Windows 向けの軽量なオープンソースブラウザーです。Chromium を同梱せず、システムにインストールされた WebView2 Runtime を利用します。ブラウザーデータを実行ファイルと同じ場所に保存するポータブル運用にも対応しています。

現在のバージョン：**2.2.1**

## ダウンロードと動作環境

最新版は [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest) からダウンロードできます。

| ファイル | 対応プラットフォーム |
|---|---|
| `ZZZ-v2.2.1-win-x64.exe` | Windows x64 ネイティブ版 |
| `ZZZ-v2.2.1-win-x86.exe` | Windows 10 x86 向け32ビット互換版（Windows 10 on Armではx86エミュレーション） |
| `ZZZ-v2.2.1-win-arm64.exe` | Windows ARM64 ネイティブ版 |

インストールは不要です。Windows 10 または Windows 11、.NET Framework 4.8、および [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) が必要です。

## 主な機能

- サイトアイコンとアニメーション並べ替えに対応した横型／折りたたみ式縦型タブ、および Edge 風の永続ワークスペース
- 履歴候補と検索候補に対応したアドレス／検索統合バー
- グループ分け、編集、HTML のインポート／エクスポートに対応したブックマーク
- 単色、画像、GIF の背景を選べる軽量なネイティブスタートページ
- ページ内検索、印刷、PDF／MHT 保存、全画面表示、ペインごとのズーム
- F9 リーダーモード、全画面グレースケール、編集可能なブックマーク名、プライバシーに配慮した「情報」ページ
- 通常タブだけを記録する原子的なセッション復元（無効化可能）と、Web プロセス開始前の初回利用規約確認
- Web ページ翻訳、ユーザースクリプト、User-Agent 切り替え、Web テーマ描画
- EasyList 系購読、自作 ABP ルール、右クリックによる広告要素の指定に対応した広告ブロック
- 進捗、MIME タイプ、保存先を表示するダウンロードマネージャーと外部ツール連携
- ローカル AppData、ポータブル、カスタム保存先から選べるブラウザーデータ配置

## プライバシー

プライベートタブはそれぞれ独立した WebView2 プロファイルを使用し、履歴、セッション、キャッシュ、Cookie、オンライン検索候補を保存しません。タブを閉じると一時データが削除され、異常終了時は監視プロセスと次回起動時の処理が削除を再試行します。ユーザーが明示的に保存したダウンロードファイルとブックマークは保持されます。

通常の閲覧では DNT、GPC、Public Suffix List に基づくサードパーティ Cookie のブロック、WebRTC 制限、サイト権限の管理を利用できます。

## ポータブルモード

**設定 → バックアップ → データと Cookie の保存場所**でポータブルモードを選択し、保存後に再起動します。移動する際は `ZZZ.exe`、`Data` フォルダー、`zzz-data-location.json` をまとめてコピーしてください。

## ビルド

```powershell
dotnet build ZZZ.sln -c Release
```

x64 の出力先は `ZZZ\bin\Release\net48\ZZZ.exe` です。x86 32ビット互換版は次のコマンドでビルドできます。

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=x86 -p:OutputPath=outputs\win-x86\
```

WebView2 は ARM32 Runtime / Loader を提供していません。Windows 10 on Arm で32ビット互換版が必要な場合は x86 エミュレーション版を使用してください。ネイティブ版は ARM64 としてビルドします。

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## サポートとライセンス

- バグ報告・提案：[GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- ライセンス：[MIT License](LICENSE)
- サードパーティ製コンポーネント：[Third-party notices](THIRD-PARTY-NOTICES.md)
