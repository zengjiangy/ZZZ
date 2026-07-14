# Changelog

## 1.1 - 2026-07-14

- Expanded userscript support with metadata parsing, correct run timing, imports, match/exclude rules, resources, persistent values, and common `GM_*` APIs.
- Added one-click Google or Microsoft page translation with a configurable target language.
- Improved media sniffing with response MIME/content metadata, HLS/DASH, and common audio/video formats.
- Strengthened private tabs with a separate temporary WebView2 environment, memory-only private userscript values, disabled persistent cache/autofill/password saving, denied site permissions, GPC, and cleanup.
- Reduced memory overhead through one shared application instance, a shared normal WebView2 environment, low-memory background tabs, bounded sniff results, and tab sleeping.
- Added command-line URL handling and `Alt+D` address-bar focus alongside `Ctrl+L`.
