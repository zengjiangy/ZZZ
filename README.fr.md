# Navigateur ZZZ

[简体中文](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [Français](README.fr.md) | [Deutsch](README.de.md) | [한국어](README.ko.md) | [繁體中文](README.zh-TW.md)

ZZZ est un navigateur Windows léger et open source, développé avec .NET Framework 4.8, WPF et Microsoft WebView2. Il utilise le runtime WebView2 installé sur le système au lieu d’intégrer Chromium. Il peut également conserver ses données à côté de l’exécutable pour une utilisation portable.

Version actuelle : **2.0.0**

## Téléchargement et configuration requise

Téléchargez la dernière version depuis [GitHub Releases](https://github.com/zengjiangy/ZZZ/releases/latest).

| Fichier | Plateforme |
|---|---|
| `ZZZ-v2.0.0-win-x64.exe` | Version native Windows x64 |
| `ZZZ-v2.0.0-win-x86.exe` | Version 32 bits pour Windows 10 x86 et Windows 10 on Arm via émulation x86 |
| `ZZZ-v2.0.0-win-arm64.exe` | Version native Windows ARM64 |

Aucune installation n’est nécessaire. ZZZ requiert Windows 10 ou Windows 11, .NET Framework 4.8 et le [runtime Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).

## Fonctionnalités principales

- Interface multilingue, navigation par onglets, restauration des pages récemment fermées et affichage partagé horizontal ou vertical
- Barre unifiée d’adresse et de recherche avec correspondances dans l’historique et suggestions en direct
- Favoris groupés et modifiables, avec importation et exportation au format HTML
- Page de démarrage native et légère, personnalisable avec une couleur, une image ou un GIF
- Recherche dans la page, impression, exportation PDF ou MHT, plein écran et zoom indépendant par volet
- Mode lecture F9, mode niveaux de gris global, noms de favoris modifiables et page À propos respectueuse de la vie privée
- Journal de session atomique limité aux onglets ordinaires, désactivable, et acceptation des conditions au premier lancement avant tout processus Web
- Traduction des pages, scripts utilisateur, changement de User-Agent et rendu clair ou sombre des sites
- Blocage publicitaire avec abonnements EasyList, règles ABP personnalisées et sélection d’un élément publicitaire par clic droit
- Gestionnaire de téléchargements avec progression, type MIME et emplacement, ainsi que prise en charge d’outils externes
- Stockage des données dans AppData, à côté de l’exécutable ou dans un dossier personnalisé

## Confidentialité

Chaque onglet privé utilise un profil WebView2 isolé et ne conserve ni historique, ni session, ni cache, ni cookies, ni suggestions de recherche en ligne. Les données temporaires sont supprimées à la fermeture de l’onglet ; après un arrêt anormal, un processus de surveillance puis le prochain démarrage réessaient le nettoyage. Les fichiers téléchargés et les favoris enregistrés explicitement par l’utilisateur sont conservés.

La navigation normale propose également DNT, GPC, le blocage des cookies tiers fondé sur la Public Suffix List, des restrictions WebRTC et la gestion des autorisations des sites.

## Mode portable

Dans **Paramètres → Sauvegarde → Emplacement des données et des cookies**, sélectionnez le mode portable, enregistrez puis redémarrez. Pour déplacer le navigateur, copiez ensemble `ZZZ.exe`, le dossier `Data` et `zzz-data-location.json`.

## Compilation

```powershell
dotnet build ZZZ.sln -c Release
```

La version x64 est générée dans `ZZZ\bin\Release\net48\ZZZ.exe`. Pour compiler la version x86 32 bits :

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=x86 -p:OutputPath=outputs\win-x86\
```

WebView2 ne fournit ni runtime ni Loader ARM32. Sur Windows 10 on Arm, utilisez la version x86 émulée pour la compatibilité 32 bits ; la version native est ARM64.

```powershell
dotnet build ZZZ\ZZZ.csproj -c Release -p:PlatformTarget=ARM64 -p:OutputPath=outputs\win-arm64\
```

## Assistance et licence

- Problèmes et suggestions : [GitHub Issues](https://github.com/zengjiangy/ZZZ/issues)
- Licence : [MIT License](LICENSE)
- Composants tiers : [Third-party notices](THIRD-PARTY-NOTICES.md)
