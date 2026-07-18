# ZZZ Browser

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [한국어](README.ko.md) | [繁體中文](README.zh-TW.md)

Offizielle Website: [zzz.campusphere.ltd](https://zzz.campusphere.ltd/)

ZZZ ist ein schlanker, quelloffener Browser für Windows, der auf .NET Framework 4.8, WPF und Microsoft WebView2 basiert. Er verwendet die auf dem System installierte WebView2 Runtime, statt Chromium mitzuliefern. Für den portablen Einsatz können die Browserdaten neben der Programmdatei gespeichert werden.

Aktuelle Version: **2.1.0**

## Download und Voraussetzungen

Die aktuelle Version steht unter [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest) bereit.

| Datei | Plattform |
|---|---|
| `ZZZ-v2.1.0-win-x64.exe` | Native Windows-x64-Version |
| `ZZZ-v2.1.0-win-x86.exe` | 32-Bit-Version für Windows 10 x86 und Windows 10 on Arm per x86-Emulation |
| `ZZZ-v2.1.0-win-arm64.exe` | Native Windows-ARM64-Version |

Eine Installation ist nicht erforderlich. Benötigt werden Windows 10 oder Windows 11, .NET Framework 4.8 und die [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

## Wichtigste Funktionen

- Mehrsprachige Oberfläche, Tabs, Wiederherstellung kürzlich geschlossener Seiten und horizontale oder vertikale geteilte Ansicht
- Kombinierte Adress- und Suchleiste mit Treffern aus dem Verlauf und Live-Suchvorschlägen
- Gruppierbare und bearbeitbare Lesezeichen sowie HTML-Import und -Export
- Schlanke native Startseite mit frei wählbarer Farbe, Bild- oder GIF-Hintergrund
- Seitensuche, Drucken, PDF-/MHT-Export, Vollbildmodus und unabhängiger Zoom je Bereich
- Lesemodus per F9, anwendungsweiter Graustufenmodus, bearbeitbare Lesezeichennamen und datenschutzfreundliche Info-Seite
- Deaktivierbares, atomares Sitzungsjournal nur für normale Tabs sowie einmalige Nutzungsbedingungen vor dem Start eines Webprozesses
- Webseitenübersetzung, Benutzerskripte, umschaltbarer User-Agent und helle oder dunkle Webdarstellung
- Werbeblocker mit EasyList-Abonnements, eigenen ABP-Regeln und Auswahl von Werbeelementen per Rechtsklick
- Downloadverwaltung mit Fortschritt, MIME-Typ und Speicherort sowie Unterstützung externer Werkzeuge
- Browserdaten wahlweise in AppData, neben der Programmdatei oder in einem eigenen Ordner

## Datenschutz

Jeder private Tab verwendet ein isoliertes WebView2-Profil und speichert weder Verlauf und Sitzungen noch Cache, Cookies oder Online-Suchvorschläge. Temporäre Daten werden beim Schließen des Tabs entfernt. Nach einem Absturz versuchen ein Überwachungsprozess und der nächste Programmstart die Bereinigung erneut. Ausdrücklich gespeicherte Downloads und Lesezeichen bleiben erhalten.

Für das normale Surfen stehen außerdem DNT, GPC, auf der Public Suffix List basierende Drittanbieter-Cookie-Sperren, WebRTC-Beschränkungen und eine Verwaltung der Websiteberechtigungen zur Verfügung.

## Portabler Modus

Unter **Einstellungen → Sicherung → Speicherort für Daten und Cookies** den portablen Modus wählen, speichern und ZZZ neu starten. Beim Verschieben müssen `ZZZ.exe`, der Ordner `Data` und `zzz-data-location.json` gemeinsam kopiert werden.

## Erstellen

```powershell
dotnet build ZZZ.sln -c Release
```

Die x64-Ausgabe befindet sich unter `ZZZ\bin\Release\net48\ZZZ.exe`. Die x86-32-Bit-Kompatibilitätsversion wird so erstellt:

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=x86 -p:OutputPath=outputs\win-x86\
```

WebView2 stellt keine ARM32-Runtime und keinen ARM32-Loader bereit. Unter Windows 10 on Arm dient daher die emulierte x86-Version als 32-Bit-Kompatibilitätsversion; die native Version ist ARM64.

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## Support und Lizenz

- Fehler und Vorschläge: [GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- Lizenz: [MIT License](LICENSE)
- Komponenten von Drittanbietern: [Third-party notices](THIRD-PARTY-NOTICES.md)
