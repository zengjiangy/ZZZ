using ZZZ.Models;
using ZZZ.Services;

var failures = new List<string>();
var passed = 0;

Check(!SiteClassifier.IsThirdParty("https://www.example.com/page", "https://static.example.com/app.js"), "same registrable domain");
Check(SiteClassifier.IsThirdParty("https://alice.github.io/", "https://bob.github.io/"), "private PSL github.io tenants");
Check(SiteClassifier.IsThirdParty("https://alice.appspot.com/", "https://bob.appspot.com/"), "private PSL appspot.com tenants");
Check(SiteClassifier.IsThirdParty("https://alice.co.za/", "https://bob.co.za/"), "multi-label ICANN suffix");
Check(SiteClassifier.IsThirdParty("http://example.com/", "https://example.com/"), "schemeful site boundary");

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
