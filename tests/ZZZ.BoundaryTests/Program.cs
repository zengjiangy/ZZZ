using System.Net;
using System.Net.Http;
using ZZZ.Models;
using ZZZ.Services;
using ZZZ.Configuration;

var failures = new List<string>();
var passed = 0;

Check(!SiteClassifier.IsThirdParty("https://www.example.com/page", "https://static.example.com/app.js"), "same registrable domain");
Check(SiteClassifier.IsThirdParty("https://alice.github.io/", "https://bob.github.io/"), "private PSL github.io tenants");
Check(SiteClassifier.IsThirdParty("https://alice.appspot.com/", "https://bob.appspot.com/"), "private PSL appspot.com tenants");
Check(SiteClassifier.IsThirdParty("https://alice.co.za/", "https://bob.co.za/"), "multi-label ICANN suffix");
Check(SiteClassifier.IsThirdParty("http://example.com/", "https://example.com/"), "schemeful site boundary");

var sessionSnapshot = SessionService.BuildSnapshot(new (string Url, bool IsPrivate)[]
{
    ("https://public.example/", false),
    ("https://private.example/", true),
    ("https://public.example/", false),
    (BrowserHome.StartPageUrl, false),
    ("not a URL", false)
});
Check(sessionSnapshot.SequenceEqual(new[] { "https://public.example/", "https://public.example/" }), "session snapshots exclude private tabs, invalid URLs, and the start page while preserving tab order and duplicates");
await CheckSessionJournalAsync();
await CheckProtectedBrowserDataAsync();

try
{
    using var tabServices = new AppServices();
    var closedTab = tabServices.Tabs.Create("https://closed.example/");
    tabServices.Tabs.Close(closedTab);
    Check(closedTab.IsClosed, "closing a tab permanently cancels its browser lifetime");
    Check(!tabServices.Tabs.Items.Contains(closedTab), "closed tabs are removed after cancellation is marked");
    var firstTab = tabServices.Tabs.Create("https://first.example/");
    var secondTab = tabServices.Tabs.Create("https://second.example/");
    var thirdTab = tabServices.Tabs.Create("https://third.example/");
    tabServices.Tabs.Move(firstTab, 2);
    Check(tabServices.Tabs.Items.SequenceEqual(new[] { secondTab, thirdTab, firstTab }), "tab service preserves drag-and-drop reorder destinations");
    var research = tabServices.Workspaces.Create("Research");
    var researchFirst = tabServices.Tabs.Create("https://research-one.example/", workspaceId: research.Id);
    var researchSecond = tabServices.Tabs.Create("https://research-two.example/", workspaceId: research.Id);
    tabServices.Tabs.MoveWithinWorkspace(researchFirst, 1);
    Check(tabServices.Tabs.Items.Where(x => x.WorkspaceId == research.Id).SequenceEqual(new[] { researchSecond, researchFirst }), "workspace tab reorder preserves the visible workspace order");
    tabServices.Tabs.MoveToWorkspace(firstTab, research.Id);
    Check(firstTab.WorkspaceId == research.Id && tabServices.Tabs.Items.Where(x => x.WorkspaceId == research.Id).Last() == firstTab, "tabs move between workspaces without being closed");
}
catch (Exception ex) { Check(false, $"closed-tab lifetime test threw {ex.GetType().FullName}: {ex.Message}"); }

var source = """
// ==UserScript==
// @name Boundary test
// @match https://app.example.com/*
// @grant GM_xmlhttpRequest
// @connect api.example.com
// ==/UserScript==
""";
var script = new UserScriptService().Parse(source);
Check(script.Grants.SequenceEqual(new[] { "GM_xmlhttpRequest" }), "@grant parsed");
Check(script.Connects.SequenceEqual(new[] { "api.example.com" }), "@connect parsed");
Check(UserScriptPermissionPolicy.HasGrant(script, "GM_xmlhttpRequest", "GM.xmlHttpRequest"), "declared network grant accepted");
Check(UserScriptPermissionPolicy.CanConnect(script, "https://app.example.com/page", "https://api.example.com/data"), "declared connect host accepted");
Check(!UserScriptPermissionPolicy.CanConnect(script, "https://app.example.com/page", "https://evil.example.net/data"), "undeclared connect host rejected");
var authorization = new Dictionary<string, string> { [script.Id] = "isolated-test-authorization" };
var bootstrap = UserScriptRuntime.BuildBootstrap(new[] { script }, "bridge-test-token", authorization);
Check(bootstrap.Contains("isolated-test-authorization") && bootstrap.Contains("documentUrl:location.href"), "per-script authorization embedded in network bridge");
Check(bootstrap.Contains("__has('GM_xmlhttpRequest','GM.xmlHttpRequest')"), "grant-gated API bootstrap");
Check(bootstrap.Contains("GetValue(__zzzBridgeToken,__authorization,__id"), "per-script storage authorization embedded");
script.Grants = new List<string> { "none", "GM_xmlhttpRequest" };
Check(!UserScriptPermissionPolicy.HasGrant(script, "GM_xmlhttpRequest"), "@grant none is restrictive");

var adblock = new AdBlockRuleSet(new[] { "||tracker.example^", "@@||tracker.example/allowed^", "example.org/banner*" });
Check(adblock.ShouldBlock("https://sub.tracker.example/pixel"), "ABP domain rule");
Check(!adblock.ShouldBlock("https://tracker.example/allowed/image"), "ABP exception rule");
Check(adblock.ShouldBlock("https://example.org/banner-300.png"), "wildcard rule");

var resourceRules = new AdBlockRuleEngine(new[]
{
    "||assets.example.com/ad.js$script",
    "||assets.example.com/no-images^$~image",
    "||api.example.com/submit^$method=POST"
});
Check(resourceRules.ShouldBlock(Request("https://assets.example.com/ad.js", "https://www.example.com/", AdBlockResourceType.Script)), "ABP script resource included");
Check(!resourceRules.ShouldBlock(Request("https://assets.example.com/ad.js", "https://www.example.com/", AdBlockResourceType.Image)), "ABP non-script resource excluded");
Check(resourceRules.ShouldBlock(Request("https://assets.example.com/no-images/file.js", "https://www.example.com/", AdBlockResourceType.Script)), "ABP negated resource permits other types");
Check(!resourceRules.ShouldBlock(Request("https://assets.example.com/no-images/banner.png", "https://www.example.com/", AdBlockResourceType.Image)), "ABP negated resource excludes image");
Check(resourceRules.ShouldBlock(Request("https://api.example.com/submit", "https://www.example.com/", AdBlockResourceType.XmlHttpRequest, "POST")), "ABP method option included");
Check(!resourceRules.ShouldBlock(Request("https://api.example.com/submit", "https://www.example.com/", AdBlockResourceType.XmlHttpRequest, "GET")), "ABP method option excludes other methods");

var partyAndDomainRules = new AdBlockRuleEngine(new[]
{
    "||cdn.example.com/third-party.js$third-party",
    "||cdn.example.com/first-party.js$first-party",
    "||ads.vendor.example^$domain=news.example|~members.news.example"
});
Check(partyAndDomainRules.ShouldBlock(Request("https://cdn.example.com/third-party.js", "https://news.example.net/", AdBlockResourceType.Script)), "ABP third-party option blocks cross-site request");
Check(!partyAndDomainRules.ShouldBlock(Request("https://cdn.example.com/third-party.js", "https://www.example.com/", AdBlockResourceType.Script)), "ABP third-party option permits same-site request");
Check(partyAndDomainRules.ShouldBlock(Request("https://cdn.example.com/first-party.js", "https://www.example.com/", AdBlockResourceType.Script)), "ABP first-party option blocks same-site request");
Check(!partyAndDomainRules.ShouldBlock(Request("https://cdn.example.com/first-party.js", "https://news.example.net/", AdBlockResourceType.Script)), "ABP first-party option permits cross-site request");
Check(partyAndDomainRules.ShouldBlock(Request("https://ads.vendor.example/banner", "https://sports.news.example/article", AdBlockResourceType.Image)), "ABP included document domain");
Check(!partyAndDomainRules.ShouldBlock(Request("https://ads.vendor.example/banner", "https://members.news.example/article", AdBlockResourceType.Image)), "ABP excluded document domain");
Check(!partyAndDomainRules.ShouldBlock(Request("https://ads.vendor.example/banner", "https://unrelated.example/page", AdBlockResourceType.Image)), "ABP unmatched document domain");

var ordinaryExceptionRules = new AdBlockRuleEngine(new[]
{
    "||allow.example^",
    "@@||allow.example/path^"
});
Check(!ordinaryExceptionRules.ShouldBlock(Request("https://allow.example/path/image.png", "https://site.example/", AdBlockResourceType.Image)), "ABP ordinary exception overrides block");
Check(ordinaryExceptionRules.ShouldBlock(Request("https://allow.example/other/image.png", "https://site.example/", AdBlockResourceType.Image)), "ABP block applies outside exception");

var importantRules = new AdBlockRuleEngine(new[]
{
    "||ads.priority.example^$important",
    "@@||ads.priority.example/ordinary^",
    "@@||ads.priority.example/critical^$important"
});
Check(importantRules.ShouldBlock(Request("https://ads.priority.example/ordinary/banner.png", "https://site.example/", AdBlockResourceType.Image)), "ABP important block bypasses ordinary exception");
Check(importantRules.ShouldBlock(Request("https://ads.priority.example/critical/banner.png", "https://site.example/", AdBlockResourceType.Image)), "ABP important is block-only and bypasses every exception");

var badFilterRules = new AdBlockRuleEngine(new[]
{
    "||disabled.example^$script",
    "||disabled.example^$script,badfilter",
    "||disabled.example^$image"
});
Check(!badFilterRules.ShouldBlock(Request("https://disabled.example/app.js", "https://site.example/", AdBlockResourceType.Script)), "ABP badfilter disables matching rule");
Check(badFilterRules.ShouldBlock(Request("https://disabled.example/banner.png", "https://site.example/", AdBlockResourceType.Image)), "ABP badfilter does not disable different option set");
Check(badFilterRules.Statistics.NetworkBlockingRules == 1, "ABP badfilter excluded from active statistics");

var matchCaseBadFilterRules = new AdBlockRuleEngine(new[]
{
    "||CaseSensitive.example^$match-case",
    "||casesensitive.example^$match-case,badfilter"
});
Check(matchCaseBadFilterRules.ShouldBlock(Request("https://CaseSensitive.example/ad.js", "https://site.example/", AdBlockResourceType.Script)), "ABP badfilter preserves match-case pattern identity");
Check(!matchCaseBadFilterRules.ShouldBlock(Request("https://casesensitive.example/ad.js", "https://site.example/", AdBlockResourceType.Script)), "ABP match-case filter remains case-sensitive");

var cosmeticRules = new AdBlockRuleEngine(new[]
{
    "##.global-ad",
    "news.example##.promo",
    "news.example#@#.global-ad",
    "news.example,~shop.news.example##.section-ad"
});
var newsSelectors = cosmeticRules.GetCosmeticSelectors("https://www.news.example/article");
Check(newsSelectors.Contains(".promo") && newsSelectors.Contains(".section-ad") && !newsSelectors.Contains(".global-ad"), "ABP cosmetic domains and exception");
var shopSelectors = cosmeticRules.GetCosmeticSelectors("https://shop.news.example/");
Check(shopSelectors.Contains(".promo") && !shopSelectors.Contains(".section-ad") && !shopSelectors.Contains(".global-ad"), "ABP cosmetic excluded subdomain");
Check(cosmeticRules.GetCosmeticSelectors("https://elsewhere.example/").SequenceEqual(new[] { ".global-ad" }), "ABP generic cosmetic selector");
Check(cosmeticRules.GetCosmeticCss("https://elsewhere.example/").Contains(".global-ad{display:none!important;}"), "cosmetic CSS emitted safely");

var unsafeCosmeticRules = new AdBlockRuleEngine(new[]
{
    "news.example##body{display:block}",
    "news.example##+js(abort-current-inline-script)",
    "news.example##.bad/*comment*/",
    "news.example##.safe"
});
Check(unsafeCosmeticRules.GetCosmeticSelectors("https://news.example/").SequenceEqual(new[] { ".safe" }), "unsafe cosmetic selectors rejected");
Check(unsafeCosmeticRules.Statistics.IgnoredRules == 3, "unsafe cosmetic selectors counted as ignored");

// Unsupported domain/entity syntax must never be dropped in a way that turns a
// scoped remote rule into a global rule.
var unsupportedDomainRules = new AdBlockRuleEngine(new[]
{
    "||global-risk.example^$domain=tenant.*",
    "tenant.*##.global-risk"
});
Check(!unsupportedDomainRules.ShouldBlock(Request("https://global-risk.example/ad.js", "https://unrelated.example/", AdBlockResourceType.Script)), "unsupported network domain constraint does not broaden globally");
Check(!unsupportedDomainRules.GetCosmeticSelectors("https://unrelated.example/").Contains(".global-risk"), "unsupported cosmetic domain constraint does not broaden globally");

Check(AdBlockRuleEngine.CountFilterRules("\uFEFF[Adblock Plus 2.0]\r\n! title\r\n# comment\r\n\r\n||one.example^\r\nexample.org##.ad\r\n") == 2, "filter rule count ignores metadata and comments");
Check(!resourceRules.ShouldBlock(Request("ftp://assets.example.com/ad.js", "https://www.example.com/", AdBlockResourceType.Script)), "non-HTTP requests are not blocked");
Check(AdBlockWebView2Mapper.FromWebView2(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Fetch) == AdBlockResourceType.XmlHttpRequest, "WebView2 fetch resource mapping");
Check(AdBlockWebView2Mapper.FromWebView2(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Websocket) == AdBlockResourceType.WebSocket, "WebView2 websocket resource mapping");
Check(AdBlockWebView2Mapper.FromWebView2(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Document, true) == AdBlockResourceType.Subdocument, "WebView2 subdocument resource mapping");
Check(MediaPlaybackPolicy.MustAllow(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Media, "https://cdn.example/video", "https://site.example/"), "media responses bypass filter-list false positives");
Check(MediaPlaybackPolicy.MustAllow(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Fetch, "https://rr4---sn.example.googlevideo.com/videoplayback?range=0-1", "https://www.youtube.com/watch?v=test"), "YouTube MSE playback relationship bypasses filter-list false positives");
Check(!MediaPlaybackPolicy.MustAllow(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Fetch, "https://rr4---sn.example.googlevideo.com/videoplayback?range=0-1", "https://unrelated.example/"), "googlevideo bypass is scoped to YouTube documents");
Check(ExternalPlayerLauncher.Quote("https://example.com/watch?v=1&list=2") == "\"https://example.com/watch?v=1&list=2\"", "external player URL is passed as one argument");
var encodedCssScript = AdBlockElementPicker.BuildApplyCssScript("</style><script>window.bad=true</script>");
Check(!encodedCssScript.Contains("</style>") && !encodedCssScript.Contains("<script>"), "cosmetic CSS is JSON-encoded before script injection");
var pickerBootstrap = (string?)typeof(AdBlockElementPicker).GetField("Bootstrap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetRawConstantValue() ?? string.Empty;
Check(pickerBootstrap.Contains("chrome.webview.postMessage") && pickerBootstrap.Contains("zzz-adblock-picker"), "element picker reports targets from top-level documents and child frames");
await CheckAdBlockManagerValidationAsync();
await CheckAdBlockManagerUpdatesAsync();

if (failures.Count > 0)
{
    Console.Error.WriteLine("Boundary tests failed:");
    foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
    return 1;
}

Console.WriteLine("All ZZZ boundary tests passed (" + passed + ").");
return 0;

void Check(bool condition, string name)
{
    if (condition) passed++;
    else failures.Add(name);
}

AdBlockRequestContext Request(string url, string documentUrl, AdBlockResourceType resourceType, string method = "GET") => new()
{
    Url = url,
    DocumentUrl = documentUrl,
    ResourceType = resourceType,
    Method = method
};

async Task CheckAdBlockManagerValidationAsync()
{
    var rootField = typeof(AppPaths).GetField("<Root>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    if (rootField is null)
    {
        Check(false, "ad-block update tests can isolate the data directory");
        return;
    }

    var originalRoot = (string)rootField.GetValue(null)!;
    var isolatedRoot = Path.Combine(Path.GetTempPath(), "ZZZ.BoundaryTests", Guid.NewGuid().ToString("N"));
    AdBlockManager? manager = null;
    try
    {
        rootField.SetValue(null, isolatedRoot);
        var adblockDirectory = Path.Combine(isolatedRoot, "adblock");
        Directory.CreateDirectory(adblockDirectory);
        var customRulesPath = Path.Combine(adblockDirectory, "custom-rules.txt");
        using (var oversized = new FileStream(customRulesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            oversized.SetLength(8L * 1024 * 1024 + 1);

        var constructor = typeof(AdBlockManager).GetConstructors().Single();
        manager = (AdBlockManager)constructor.Invoke(new object?[] { null });
        await manager.LoadAsync();
        Check(!string.IsNullOrWhiteSpace(manager.LastLoadError) && new FileInfo(customRulesPath).Length == 8L * 1024 * 1024 + 1,
            "oversized custom rule file is preserved on load failure");

        var httpRejected = false;
        try { await manager.AddCustomSubscriptionAsync("Unsafe", "http://filters.example/list.txt"); }
        catch (ArgumentException) { httpRejected = true; }
        Check(httpRejected, "ad-block subscriptions require HTTPS");

        var overwriteRejected = false;
        try { await manager.SaveConfigurationAsync(manager.GetConfigurationSnapshot(), manager.CustomRules); }
        catch (InvalidOperationException) { overwriteRejected = true; }
        Check(overwriteRejected && new FileInfo(customRulesPath).Length == 8L * 1024 * 1024 + 1,
            "unreadable custom rule file cannot be silently overwritten");
    }
    finally
    {
        manager?.Dispose();
        rootField.SetValue(null, originalRoot);
        try { if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true); } catch { }
    }
}

async Task CheckSessionJournalAsync()
{
    var rootField = typeof(AppPaths).GetField("<Root>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    if (rootField is null)
    {
        Check(false, "session journal tests can isolate the data directory");
        return;
    }
    var originalRoot = (string)rootField.GetValue(null)!;
    var isolatedRoot = Path.Combine(Path.GetTempPath(), "ZZZ.SessionTests", Guid.NewGuid().ToString("N"));
    try
    {
        rootField.SetValue(null, isolatedRoot);
        Directory.CreateDirectory(isolatedRoot);
        var service = new SessionService();
        var writes = new[] { "https://one.example/", "https://two.example/", "https://final.example/" }
            .Select(url => service.SaveAsync(new[] { (url, false) })).ToArray();
        await Task.WhenAll(writes);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AppPaths.Session));
        Check(persisted?.SequenceEqual(new[] { "https://final.example/" }) == true, "latest queued session snapshot wins concurrent writes");

        File.WriteAllText(AppPaths.Session, "[\"https://old.example/\"]");
        File.WriteAllText(AppPaths.Session + ".tmp", "[\"https://recovered.example/\"]");
        File.SetLastWriteTimeUtc(AppPaths.Session, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(AppPaths.Session + ".tmp", DateTime.UtcNow);
        var recovered = new SessionService();
        await recovered.LoadAsync();
        Check(recovered.Urls.SequenceEqual(new[] { "https://recovered.example/" }), "complete newer pre-2.0 session temporary file is recovered");

        await recovered.ClearAsync();
        Check(!File.Exists(AppPaths.Session) && !Directory.EnumerateFiles(isolatedRoot, "session.json*.tmp").Any(), "disabling session recording clears journal and temporary files");
    }
    finally
    {
        rootField.SetValue(null, originalRoot);
        try { if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true); } catch { }
    }
}

async Task CheckProtectedBrowserDataAsync()
{
    var rootField = typeof(AppPaths).GetField("<Root>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    if (rootField is null)
    {
        Check(false, "protected browser data tests can isolate the data directory");
        return;
    }

    var originalRoot = (string)rootField.GetValue(null)!;
    var isolatedRoot = Path.Combine(Path.GetTempPath(), "ZZZ.ProtectedDataTests", Guid.NewGuid().ToString("N"));
    try
    {
        rootField.SetValue(null, isolatedRoot);
        Directory.CreateDirectory(isolatedRoot);

        var legacyBookmarks = new List<Bookmark>
        {
            new() { Title = "Private bookmark", Url = "https://bookmark-secret.example/" }
        };
        File.WriteAllText(AppPaths.LegacyBookmarks, System.Text.Json.JsonSerializer.Serialize(legacyBookmarks));
        var bookmarks = new BookmarkService();
        await bookmarks.LoadAsync();
        Check(bookmarks.Items.Single().Url == "https://bookmark-secret.example/", "legacy plaintext bookmarks migrate without data loss");
        Check(File.Exists(AppPaths.Bookmarks) && !File.Exists(AppPaths.LegacyBookmarks), "bookmark migration replaces the plaintext JSON file");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(AppPaths.Bookmarks)).Contains("bookmark-secret.example"), "bookmark URLs are not readable in the protected file");
        var reloadedBookmarks = new BookmarkService();
        await reloadedBookmarks.LoadAsync();
        Check(reloadedBookmarks.Items.Single().Title == "Private bookmark", "DPAPI-protected bookmarks reload for the current Windows user");

        var legacyHistory = new List<HistoryEntry>
        {
            new() { Title = "Private history", Url = "https://history-secret.example/" }
        };
        File.WriteAllText(AppPaths.LegacyHistory, System.Text.Json.JsonSerializer.Serialize(legacyHistory));
        var history = new HistoryService();
        await history.LoadAsync();
        Check(history.Items.Single().Url == "https://history-secret.example/", "legacy plaintext history migrates without data loss");
        Check(File.Exists(AppPaths.History) && !File.Exists(AppPaths.LegacyHistory), "history migration replaces the plaintext JSON file");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(AppPaths.History)).Contains("history-secret.example"), "history URLs are not readable in the protected file");
        var reloadedHistory = new HistoryService();
        await reloadedHistory.LoadAsync();
        Check(reloadedHistory.Items.Single().Title == "Private history", "DPAPI-protected history reloads for the current Windows user");

        var workspaces = new WorkspaceService();
        await workspaces.LoadAsync();
        var defaultWorkspace = workspaces.Active;
        var research = workspaces.Create("Research");
        await workspaces.SaveSnapshotAsync(new (string Url, bool IsPrivate, string WorkspaceId)[]
        {
            ("https://default-workspace-secret.example/", false, defaultWorkspace.Id),
            ("https://research-workspace-secret.example/", false, research.Id),
            ("https://private-workspace-secret.example/", true, research.Id),
            (BrowserHome.StartPageUrl, false, research.Id)
        }, research.Id);
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(AppPaths.Workspaces)).Contains("workspace-secret.example"), "workspace tab URLs are DPAPI-protected at rest");
        var reloadedWorkspaces = new WorkspaceService();
        await reloadedWorkspaces.LoadAsync();
        var restoredTabs = reloadedWorkspaces.RestoreTabs();
        Check(reloadedWorkspaces.Items.Count == 2 && reloadedWorkspaces.Active.Name == "Research", "workspace names and active workspace persist");
        Check(restoredTabs.Count == 2 && restoredTabs.Any(x => x.WorkspaceId == defaultWorkspace.Id) && restoredTabs.Any(x => x.WorkspaceId == research.Id), "workspace restore keeps public tabs grouped and excludes private or start-page tabs");

        var faviconBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var faviconCache = new FaviconCacheService();
        using (var faviconStream = new MemoryStream(faviconBytes, false))
            await faviconCache.CaptureAsync("https://favicon-secret.example/path", faviconStream, persist: true);
        var faviconFile = Directory.GetFiles(AppPaths.Favicons, "*.dat").Single();
        Check(!File.ReadAllBytes(faviconFile).Take(8).SequenceEqual(faviconBytes.Take(8)), "favicon cache files are DPAPI-protected instead of storing raw PNG data");
        Check(new FaviconCacheService().GetCached("https://favicon-secret.example/other") is not null, "protected favicon cache reloads by site origin for bookmarks and history");

        Check(AppPaths.PrivateWebViewRoot.StartsWith(isolatedRoot, StringComparison.OrdinalIgnoreCase), "private WebView data follows the selected data root");
    }
    finally
    {
        rootField.SetValue(null, originalRoot);
        try { if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true); } catch { }
    }
}

async Task CheckAdBlockManagerUpdatesAsync()
{
    var rootField = typeof(AppPaths).GetField("<Root>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    if (rootField is null)
    {
        Check(false, "ad-block update tests can isolate the data directory");
        return;
    }

    var originalRoot = (string)rootField.GetValue(null)!;
    var isolatedRoot = Path.Combine(Path.GetTempPath(), "ZZZ.BoundaryTests", Guid.NewGuid().ToString("N"));
    try
    {
        rootField.SetValue(null, isolatedRoot);
        var handler = new AdBlockUpdateTestHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var manager = new AdBlockManager(client);
        await manager.LoadAsync();

        var valid = await manager.AddCustomSubscriptionAsync("Valid", "https://filters.example/valid.txt");
        var validResult = await manager.UpdateNowAsync(new[] { valid.Id });
        Check(validResult.Count == 1 && validResult[0].Outcome == AdBlockUpdateOutcome.Updated, "valid filter subscription update accepted");
        Check(manager.ShouldBlock(Request("https://updates-valid.example/ad.js", "https://site.example/", AdBlockResourceType.Script)), "updated subscription rules become active");

        var html = await manager.AddCustomSubscriptionAsync("HTML", "https://filters.example/error.html");
        var htmlResult = await manager.UpdateNowAsync(new[] { html.Id });
        Check(htmlResult.Count == 1 && htmlResult[0].Outcome == AdBlockUpdateOutcome.Failed, "HTML subscription response rejected");
        Check(!manager.ShouldBlock(Request("https://html-error.example/ad.js", "https://site.example/", AdBlockResourceType.Script)), "rejected HTML response does not replace active rules");

        var recovery = await manager.AddCustomSubscriptionAsync("Recovery", "https://filters.example/recovery.txt");
        var firstRecovery = await manager.UpdateNowAsync(new[] { recovery.Id });
        Check(firstRecovery.Single().Outcome == AdBlockUpdateOutcome.Updated, "subscription cache initially populated");
        var recoveryCache = Path.Combine(isolatedRoot, "adblock", "lists", "subscription-" + recovery.Id.ToLowerInvariant() + ".txt");
        File.Delete(recoveryCache);
        var missingCacheRecovery = await manager.UpdateNowAsync(new[] { recovery.Id });
        Check(missingCacheRecovery.Single().Outcome == AdBlockUpdateOutcome.Updated && File.Exists(recoveryCache), "missing conditional cache is recovered unconditionally");
        var notModified = await manager.UpdateNowAsync(new[] { recovery.Id });
        Check(notModified.Single().Outcome == AdBlockUpdateOutcome.NotModified, "usable subscription cache honors 304 response");

        await manager.UpdateNowAsync();
        Check(manager.GetConfigurationSnapshot().LastAutomaticUpdateCheckUtc.HasValue, "full manual update advances the automatic update interval");
    }
    finally
    {
        rootField.SetValue(null, originalRoot);
        try { if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true); } catch { }
    }
}

sealed class AdBlockUpdateTestHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("error.html", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Response(request, HttpStatusCode.OK,
                "<!doctype html><html><body>||html-error.example^</body></html>", "text/html"));

        if (request.Headers.IfNoneMatch.Any())
            return Task.FromResult(Response(request, HttpStatusCode.NotModified, string.Empty, "text/plain"));

        var rule = path.EndsWith("recovery.txt", StringComparison.OrdinalIgnoreCase)
            ? "||updates-recovered.example^"
            : "||updates-valid.example^";
        var response = Response(request, HttpStatusCode.OK, "[Adblock Plus 2.0]\n" + rule + "\n", "text/plain");
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"boundary-v1\"");
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, string content, string mediaType) => new(status)
    {
        RequestMessage = request,
        Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType)
    };
}
