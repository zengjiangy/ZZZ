namespace ZZZ.Configuration;

public enum AppearanceMode { System, Light, Dark }
public enum UserAgentPreset { DefaultDesktop, AndroidMobile, IPad, Custom }
public enum PermissionPolicy { Ask, Deny }
public enum DownloadMode { BuiltIn, External }
public enum WebContentDarkMode { Off, Smart, Force }
public enum TranslationProvider { Google, Microsoft }
public enum StartupPageMode { StartPage, SearchEngineWebsite }
public enum DataStorageMode { LocalAppData, Portable, Custom }

public sealed class AppSettings
{
    // Retained for migration from 1.1. Home navigation is controlled by
    // StartupPageMode in 1.2.
    public string HomePage { get; set; } = "zzz://start/";
    public StartupPageMode StartupPageMode { get; set; } = StartupPageMode.StartPage;
    public string ActiveSearchEngineId { get; set; } = "bing";
    public List<SearchEngine> SearchEngines { get; set; } = SearchEngine.Defaults();
    public AppearanceMode Appearance { get; set; } = AppearanceMode.System;
    public UiSettings Ui { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();
    public DownloadSettings Downloads { get; set; } = new();
    public StartPageSettings StartPage { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
}

public sealed class UiSettings
{
    public string Language { get; set; } = "auto";
    public bool ShowTabBar { get; set; } = true;
    public bool ShowToolbar { get; set; } = true;
    public bool ShowAddressBar { get; set; } = true;
}

public sealed class PrivacySettings
{
    public bool SendDoNotTrack { get; set; } = true;
    public bool DisableWebRtc { get; set; }
    public bool BlockThirdPartyCookies { get; set; } = true;
    public bool SendGlobalPrivacyControl { get; set; } = true;
    public bool StrictPrivateTabs { get; set; } = true;
    public PermissionPolicy LocationPermission { get; set; } = PermissionPolicy.Ask;
    public PermissionPolicy CameraMicrophonePermission { get; set; } = PermissionPolicy.Ask;
    public bool ClearOnExit { get; set; }
    public ClearDataSelection ClearOnExitItems { get; set; } = new();
}

public sealed class ClearDataSelection
{
    public bool History { get; set; } = true;
    public bool Cache { get; set; } = true;
    public bool Cookies { get; set; } = true;
    public bool Passwords { get; set; }
    public bool FormData { get; set; }
}

public sealed class BrowserSettings
{
    public UserAgentPreset UserAgent { get; set; } = UserAgentPreset.DefaultDesktop;
    public string CustomUserAgent { get; set; } = string.Empty;
    public int SleepBackgroundTabsAfterMinutes { get; set; } = 15;
    public bool RestoreLastSession { get; set; } = true;
    public TranslationProvider TranslationProvider { get; set; } = TranslationProvider.Microsoft;
    public string TranslationTargetLanguage { get; set; } = "zh-CN";
    public bool AutoTranslateForeignPages { get; set; }
}

public sealed class StartPageSettings
{
    public string BackgroundColor { get; set; } = "#101826";
    public string BackgroundImage { get; set; } = string.Empty;
    public double BackgroundOpacity { get; set; } = 1.0;
    public bool ShowBookmarks { get; set; } = true;
}

public sealed class StorageSettings
{
    public DataStorageMode Mode { get; set; } = DataStorageMode.LocalAppData;
    public string CustomPath { get; set; } = string.Empty;
}

public sealed class AdvancedSettings
{
    public bool EnableAdBlock { get; set; } = true;
    public bool EnableResourceSniffer { get; set; } = true;
    public bool EnableUserScripts { get; set; } = true;
    public bool EnableDeveloperTools { get; set; } = true;
    public WebContentDarkMode WebDarkMode { get; set; } = WebContentDarkMode.Smart;
    // Kept for one release so existing settings from ZZZ 1.x migrate cleanly.
    public bool ForceDarkWebContent { get; set; }
    public string ExternalPlayerPath { get; set; } = string.Empty;
}

public sealed class DownloadSettings
{
    public DownloadMode Mode { get; set; } = DownloadMode.BuiltIn;
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string ExternalCommand { get; set; } = string.Empty;
    public string ExternalArguments { get; set; } = "{url}";
}

public sealed class SearchEngine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom";
    public string UrlTemplate { get; set; } = "https://www.google.com/search?q={query}";

    public static List<SearchEngine> Defaults() =>
    [
        new() { Id = "bing", Name = "Bing", UrlTemplate = "https://www.bing.com/search?q={query}" },
        new() { Id = "google", Name = "Google", UrlTemplate = "https://www.google.com/search?q={query}" },
        new() { Id = "baidu", Name = "Baidu", UrlTemplate = "https://www.baidu.com/s?wd={query}" },
        new() { Id = "duckduckgo", Name = "DuckDuckGo", UrlTemplate = "https://duckduckgo.com/?q={query}" }
    ];
}

public static class BrowserHome
{
    public const string StartPageUrl = "zzz://start/";

    public static string GetHomeUrl(AppSettings settings)
    {
        if (settings.StartupPageMode == StartupPageMode.StartPage) return StartPageUrl;
        var engine = settings.SearchEngines.FirstOrDefault(x => x.Id == settings.ActiveSearchEngineId) ?? settings.SearchEngines.FirstOrDefault();
        if (engine is not null && Uri.TryCreate(engine.UrlTemplate.Replace("{query}", string.Empty), UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Authority) + "/";
        return "https://www.bing.com/";
    }

    public static bool IsStartPage(string? url) => string.Equals(url?.TrimEnd('/'), StartPageUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
