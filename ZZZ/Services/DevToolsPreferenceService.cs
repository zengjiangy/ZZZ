using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZZZ.Services;

internal static class DevToolsPreferenceService
{
    public static void SuppressObsoleteWebHintBanner()
    {
        var path = Path.Combine(AppPaths.WebViewData, "EBWebView", "Default", "Preferences");
        if (!File.Exists(path)) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null) return;
            var devTools = GetOrCreateObject(root, "devtools");
            var preferences = GetOrCreateObject(devTools, "preferences");
            var changed = SetString(preferences, "edge-webhint-deprecation-migration-v1-done", "true");
            changed |= SetString(preferences, "webhint-auto-disable-banner-pending", "false");
            if (!changed) return;

            var temp = path + ".zzz.tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
            try { File.Replace(temp, path, null); }
            catch
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
        catch
        {
            // DevTools preferences are owned by the WebView2 runtime.  A format
            // change or a locked profile must never prevent the browser starting.
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static bool SetString(JsonObject parent, string name, string value)
    {
        try { if (parent[name]?.GetValue<string>() == value) return false; }
        catch { }
        parent[name] = value;
        return true;
    }
}
