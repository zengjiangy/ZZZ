using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using ZZZ.Configuration;

namespace ZZZ.Services;

/// <summary>
/// Builds the read-only information displayed by the About page. The snapshot
/// deliberately excludes user names, machine names, network addresses, serial
/// numbers and literal custom data paths.
/// </summary>
public sealed class AboutInfoService
{
    public const string ProjectUrl = "https://github.com/zengjiangy/ZZZ";
    public const string UpdatesUrl = "https://github.com/zengjiangy/ZZZ/releases";

    private readonly ISettingsService _settings;

    public AboutInfoService(ISettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public AboutInfoSnapshot GetSnapshot() => CreateSnapshot(_settings.Current);

    public static AboutInfoSnapshot CreateSnapshot(AppSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        var applicationVersion = ReadApplicationVersion();
        var webViewRuntimeVersion = ReadWebViewRuntimeVersion();
        var language = string.IsNullOrWhiteSpace(LocalizationService.CurrentLanguage)
            ? CultureInfo.CurrentUICulture.Name
            : LocalizationService.CurrentLanguage;
        var configuredLanguage = string.IsNullOrWhiteSpace(settings.Ui.Language)
            ? "auto"
            : settings.Ui.Language.Trim();
        var storageLocationSummary = DescribeStorageLocation(AppPaths.StorageMode);
        var userAgentMode = DescribeUserAgentMode(settings.Browser);
        var osVersion = Environment.OSVersion.VersionString;
        var osArchitecture = RuntimeInformation.OSArchitecture.ToString();
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
        var framework = RuntimeInformation.FrameworkDescription;

        var fingerprintSource = string.Join("\n", new[]
        {
            "schema=1",
            "app=" + applicationVersion,
            "webview=" + webViewRuntimeVersion,
            "os=" + osVersion,
            "os-arch=" + osArchitecture,
            "process-arch=" + processArchitecture,
            "framework=" + framework,
            "cpu-count=" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            "language=" + language,
            "ua-mode=" + settings.Browser.UserAgent,
            "dnt=" + Bool(settings.Privacy.SendDoNotTrack),
            "gpc=" + Bool(settings.Privacy.SendGlobalPrivacyControl),
            "third-party-cookies-blocked=" + Bool(settings.Privacy.BlockThirdPartyCookies),
            "webrtc-disabled=" + Bool(settings.Privacy.DisableWebRtc),
            "web-dark-mode=" + settings.Advanced.WebDarkMode
        });

        return new AboutInfoSnapshot(
            applicationVersion,
            webViewRuntimeVersion,
            CreateLocalFingerprint(fingerprintSource),
            osVersion,
            osArchitecture,
            processArchitecture,
            framework,
            Environment.ProcessorCount,
            language,
            configuredLanguage,
            AppPaths.StorageMode,
            storageLocationSummary,
            userAgentMode,
            ProjectUrl,
            UpdatesUrl);
    }

    private static string ReadApplicationVersion()
    {
        var assembly = typeof(AboutInfoService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational!;

        var version = assembly.GetName().Version;
        if (version is null) return LocalizationService.Text("UnknownValue");
        return version.Build >= 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", version.Major, version.Minor, version.Build)
            : version.ToString();
    }

    private static string ReadWebViewRuntimeVersion()
    {
        try
        {
            NativeDependencyService.PrepareWebView2Loader();
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            return string.IsNullOrWhiteSpace(version) ? LocalizationService.Text("Unavailable") : version;
        }
        catch
        {
            // The About page must remain usable when WebView2 is absent or its
            // runtime registration is damaged.
            return LocalizationService.Text("Unavailable");
        }
    }

    private static string DescribeStorageLocation(DataStorageMode mode) => mode switch
    {
        DataStorageMode.Portable => LocalizationService.Text("PortableMode"),
        DataStorageMode.Custom => LocalizationService.Text("CustomData"),
        _ => LocalizationService.Text("LocalData")
    };

    private static string DescribeUserAgentMode(BrowserSettings browser) => browser.UserAgent switch
    {
        UserAgentPreset.AndroidMobile => LocalizationService.Text("AndroidMobile"),
        UserAgentPreset.IPad => LocalizationService.Text("IPad"),
        UserAgentPreset.Custom => LocalizationService.Text("CustomUserAgentHidden"),
        _ => LocalizationService.Text("DefaultDesktop")
    };

    private static string Bool(bool value) => value ? "1" : "0";

    private static string CreateLocalFingerprint(string value)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        // A 128-bit truncation is compact enough for display while avoiding the
        // implication that this local diagnostic identifier is a global ID.
        return "LOCAL-" + BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty);
    }
}

/// <summary>
/// Immutable, privacy-filtered About-page data. LocalBrowserFingerprint is a
/// truncated SHA-256 diagnostic identifier and is never transmitted by this
/// service.
/// </summary>
public sealed class AboutInfoSnapshot
{
    internal AboutInfoSnapshot(
        string applicationVersion,
        string webViewRuntimeVersion,
        string localBrowserFingerprint,
        string operatingSystem,
        string osArchitecture,
        string processArchitecture,
        string framework,
        int processorCount,
        string language,
        string configuredLanguage,
        DataStorageMode storageMode,
        string storageLocationSummary,
        string userAgentMode,
        string projectUrl,
        string updatesUrl)
    {
        ApplicationVersion = applicationVersion;
        WebViewRuntimeVersion = webViewRuntimeVersion;
        LocalBrowserFingerprint = localBrowserFingerprint;
        OperatingSystem = operatingSystem;
        OsArchitecture = osArchitecture;
        ProcessArchitecture = processArchitecture;
        Framework = framework;
        ProcessorCount = processorCount;
        Language = language;
        ConfiguredLanguage = configuredLanguage;
        StorageMode = storageMode;
        StorageLocationSummary = storageLocationSummary;
        UserAgentMode = userAgentMode;
        ProjectUrl = projectUrl;
        UpdatesUrl = updatesUrl;
    }

    public string ApplicationVersion { get; }
    public string WebViewRuntimeVersion { get; }

    /// <summary>Local SHA-256/128 diagnostic identifier; it is not a network or hardware ID.</summary>
    public string LocalBrowserFingerprint { get; }

    public string OperatingSystem { get; }
    public string OsArchitecture { get; }
    public string ProcessArchitecture { get; }
    public string Framework { get; }
    public int ProcessorCount { get; }
    public string Language { get; }
    public string ConfiguredLanguage { get; }
    public DataStorageMode StorageMode { get; }
    public string StorageLocationSummary { get; }
    public string UserAgentMode { get; }
    public string ProjectUrl { get; }
    public string UpdatesUrl { get; }
}
