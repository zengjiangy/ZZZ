using System.Net.Http;
using System.Net;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using ZZZ.Configuration;
using ZZZ.Models;

namespace ZZZ.Services;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class UserScriptBridge
{
    private static readonly object FileGate = new();
    private static Dictionary<string, Dictionary<string, string>>? SharedValues;
    private readonly bool _persistent;
    private readonly string _secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Dictionary<string, string>> _privateValues = [];
    private UserScriptNetworkBroker? _network;

    public UserScriptBridge(bool persistent) => _persistent = persistent;
    internal string SecretForBootstrap => _secret;
    internal void AttachNetwork(UserScriptNetworkBroker network) => _network = network;

    public void NetworkMessage(string token, string messageJson)
    {
        Validate(token);
        _network?.HandleHostMessage(messageJson);
    }

    public string PollNetworkEvents(string token, string requestId, string authorization)
    {
        Validate(token);
        return _network?.Poll(requestId, authorization) ?? "[]";
    }

    public string GetValue(string token, string authorization, string scriptId, string key, string fallbackJson)
    {
        Validate(token);
        AuthorizeScript(authorization, scriptId);
        lock (FileGate)
        {
            var values = Values();
            return values.TryGetValue(scriptId, out var script) && script.TryGetValue(key, out var value) ? value : fallbackJson;
        }
    }

    public void SetValue(string token, string authorization, string scriptId, string key, string valueJson)
    {
        Validate(token);
        AuthorizeScript(authorization, scriptId);
        lock (FileGate)
        {
            var values = Values();
            if (!values.TryGetValue(scriptId, out var script)) values[scriptId] = script = [];
            script[key] = valueJson;
            Save(values);
        }
    }

    public void DeleteValue(string token, string authorization, string scriptId, string key)
    {
        Validate(token);
        AuthorizeScript(authorization, scriptId);
        lock (FileGate)
        {
            var values = Values();
            if (values.TryGetValue(scriptId, out var script)) script.Remove(key);
            Save(values);
        }
    }

    public string ListValues(string token, string authorization, string scriptId)
    {
        Validate(token);
        AuthorizeScript(authorization, scriptId);
        lock (FileGate)
        {
            var values = Values();
            return JsonSerializer.Serialize(values.TryGetValue(scriptId, out var script) ? script.Keys.ToArray() : Array.Empty<string>());
        }
    }

    private void Validate(string token)
    {
        if (!string.Equals(token, _secret, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Invalid userscript bridge token.");
    }

    private void AuthorizeScript(string authorization, string scriptId)
    {
        if (_network is null || !_network.IsAuthorized(authorization, scriptId))
            throw new UnauthorizedAccessException("Userscript storage authorization was rejected.");
    }

    private Dictionary<string, Dictionary<string, string>> Values()
    {
        if (!_persistent) return _privateValues;
        if (SharedValues is not null) return SharedValues;
        try
        {
            SharedValues = File.Exists(AppPaths.UserScriptValues)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(AppPaths.UserScriptValues)) ?? []
                : [];
        }
        catch { SharedValues = []; }
        return SharedValues;
    }

    private void Save(Dictionary<string, Dictionary<string, string>> values)
    {
        if (!_persistent) return;
        Directory.CreateDirectory(AppPaths.Root);
        var temp = AppPaths.UserScriptValues + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(values));
        if (File.Exists(AppPaths.UserScriptValues)) File.Delete(AppPaths.UserScriptValues);
        File.Move(temp, AppPaths.UserScriptValues);
    }

}

public sealed class UserScriptNetworkBroker : IDisposable
{
    private const int MaxConcurrentRequests = 8;
    private const long MaxInMemoryResponseBytes = 64L * 1024 * 1024;
    private const int MaxUploadBytes = 32 * 1024 * 1024;

    private sealed class RequestState
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        public string Authorization { get; set; } = string.Empty;
        public bool Aborted { get; set; }
        public bool Completed { get; set; }
    }

    private readonly CoreWebView2 _core;
    private readonly ISettingsService _settings;
    private readonly ConcurrentDictionary<string, RequestState> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UserScript> _policies = new(StringComparer.Ordinal);

    public UserScriptNetworkBroker(CoreWebView2 core, ISettingsService settings)
    {
        _core = core;
        _settings = settings;
    }

    public IReadOnlyDictionary<string, string> ConfigurePolicies(IEnumerable<UserScript> scripts)
    {
        _policies.Clear();
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in scripts.Where(x => x.Enabled))
        {
            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            _policies[token] = script;
            tokens[script.Id] = token;
        }
        return tokens;
    }

    public bool IsAuthorized(string authorization, string scriptId) =>
        _policies.TryGetValue(authorization, out var script) && script.Id.Equals(scriptId, StringComparison.Ordinal);

    public void HandleHostMessage(string messageJson)
    {
        try
        {
            using var message = JsonDocument.Parse(messageJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement)) return;
            var kind = kindElement.GetString() ?? string.Empty;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            if (!kind.StartsWith("zzz-userscript-net-", StringComparison.Ordinal) || id.Length == 0) return;
            if (kind == "zzz-userscript-net-abort")
            {
                var authorization = root.TryGetProperty("authorization", out var authElement) ? authElement.GetString() ?? string.Empty : string.Empty;
                if (_requests.TryGetValue(id, out var existing) && FixedEquals(existing.Authorization, authorization)) { existing.Aborted = true; existing.Cancellation.Cancel(); }
                return;
            }
            if (!root.TryGetProperty("request", out var requestElement)) return;
            var request = requestElement.Clone();
            var state = new RequestState { Authorization = GetString(request, "authorization") };
            if (!_requests.TryAdd(id, state)) return;
            if (_requests.Count > MaxConcurrentRequests)
            {
                Post(state, id, "error", new { status = 0, readyState = 4, error = $"Userscript request limit exceeded ({MaxConcurrentRequests})." });
                state.Completed = true;
                return;
            }
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () => await StartAsync(id, kind, request, state)));
        }
        catch { }
    }

    public string Poll(string id, string authorization)
    {
        if (!_requests.TryGetValue(id, out var state) || !FixedEquals(state.Authorization, authorization)) return "[]";
        var events = new List<string>();
        while (state.Events.TryDequeue(out var item)) events.Add(item);
        if (state.Completed && state.Events.IsEmpty)
        {
            _requests.TryRemove(id, out _);
            state.Cancellation.Dispose();
        }
        return "[" + string.Join(",", events) + "]";
    }

    private async Task StartAsync(string id, string kind, JsonElement request, RequestState state)
    {
        var url = GetString(request, "url");
        var documentUrl = GetString(request, "documentUrl");
        var authorization = GetString(request, "authorization");
        if (!_policies.TryGetValue(authorization, out var script) || !UserScriptMatcher.IsMatch(script, documentUrl))
        {
            Post(state, id, "error", new { status = 0, readyState = 4, error = "Userscript authorization was rejected." });
            state.Completed = true;
            return;
        }
        var requiredGrant = kind == "zzz-userscript-net-download"
            ? new[] { "GM_download", "GM.download" }
            : new[] { "GM_xmlhttpRequest", "GM.xmlHttpRequest" };
        if (!UserScriptPermissionPolicy.HasGrant(script, requiredGrant) || !UserScriptPermissionPolicy.CanConnect(script, documentUrl, url))
        {
            Post(state, id, "error", new { status = 0, readyState = 4, error = "Userscript request is not allowed by @grant or @connect." });
            state.Completed = true;
            return;
        }
        var cookieHeader = string.Empty;
        if (GetBool(request, "useCookies", true) && IsHttpUrl(url) &&
            !(_settings.Current.Privacy.BlockThirdPartyCookies && SiteClassifier.IsThirdParty(documentUrl, url)))
        {
            try
            {
                var cookies = await _core.CookieManager.GetCookiesAsync(url);
                cookieHeader = string.Join("; ", cookies.Select(x => x.Name + "=" + x.Value));
            }
            catch { }
        }
        if (kind == "zzz-userscript-net-download") _ = RunDownloadAsync(id, request, cookieHeader, state);
        else _ = RunRequestAsync(id, request, cookieHeader, state);
    }

    private async Task RunRequestAsync(string id, JsonElement options, string cookies, RequestState state)
    {
        try
        {
            var timeout = GetInt(options, "timeout");
            if (timeout > 0) state.Cancellation.CancelAfter(timeout);
            using var client = CreateClient();
            using var request = CreateRequest(options, cookies, (sent, total) => Post(state, id, "progress", new { phase = "upload", loaded = sent, total, lengthComputable = total > 0, readyState = 1 }));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, state.Cancellation.Token).ConfigureAwait(false);
            var responseHeaders = string.Join("\r\n", response.Headers.Concat(response.Content.Headers).SelectMany(x => x.Value.Select(v => x.Key + ": " + v)));
            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? GetString(options, "url");
            var basic = new { status = (int)response.StatusCode, statusText = response.ReasonPhrase ?? string.Empty, finalUrl, responseHeaders };
            Post(state, id, "readystatechange", new { basic.status, basic.statusText, basic.finalUrl, basic.responseHeaders, readyState = 2 });
            var total = response.Content.Headers.ContentLength ?? -1;
            if (total > MaxInMemoryResponseBytes) throw new InvalidDataException($"Userscript response exceeds the {MaxInMemoryResponseBytes / 1024 / 1024} MB in-memory limit. Use GM_download for larger files.");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream(total is > 0 ? (int)total : 0);
            var buffer = new byte[64 * 1024];
            long loaded = 0;
            int read;
            var lastProgress = DateTime.UtcNow;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, state.Cancellation.Token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer, 0, read, state.Cancellation.Token).ConfigureAwait(false);
                loaded += read;
                if (loaded > MaxInMemoryResponseBytes) throw new InvalidDataException($"Userscript response exceeds the {MaxInMemoryResponseBytes / 1024 / 1024} MB in-memory limit. Use GM_download for larger files.");
                if ((DateTime.UtcNow - lastProgress).TotalMilliseconds >= 80 || (total > 0 && loaded == total))
                {
                    lastProgress = DateTime.UtcNow;
                    Post(state, id, "progress", new { phase = "download", loaded, total, lengthComputable = total >= 0, readyState = 3, basic.status, basic.statusText, basic.finalUrl, basic.responseHeaders });
                }
            }
            var bytes = output.ToArray();
            var responseType = GetString(options, "responseType").ToLowerInvariant();
            var mime = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            string responseText = string.Empty, responseBase64 = string.Empty;
            if (responseType is "arraybuffer" or "blob") responseBase64 = Convert.ToBase64String(bytes);
            else responseText = DecodeText(bytes, response.Content.Headers.ContentType?.CharSet);
            Post(state, id, "load", new { basic.status, basic.statusText, basic.finalUrl, basic.responseHeaders, readyState = 4, responseText, responseBase64, responseType, mime, loaded, total, lengthComputable = total >= 0 });
        }
        catch (OperationCanceledException)
        {
            Post(state, id, state.Aborted ? "abort" : "timeout", new { status = 0, readyState = 4 });
        }
        catch (Exception ex)
        {
            Post(state, id, "error", new { status = 0, readyState = 4, error = ex.Message });
        }
        finally { state.Completed = true; }
    }

    private async Task RunDownloadAsync(string id, JsonElement options, string cookies, RequestState state)
    {
        string? temporaryPath = null;
        try
        {
            var timeout = GetInt(options, "timeout");
            if (timeout > 0) state.Cancellation.CancelAfter(timeout);
            using var client = CreateClient();
            using var request = CreateRequest(options, cookies, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, state.Cancellation.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? GetString(options, "url");
            Directory.CreateDirectory(_settings.Current.Downloads.Folder);
            var name = SafeFileName(GetString(options, "name"));
            if (name.Length == 0) name = SafeFileName(Path.GetFileName(new Uri(finalUrl).LocalPath));
            if (name.Length == 0) name = "download";
            var destination = UniquePath(Path.Combine(_settings.Current.Downloads.Folder, name));
            temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".zzzpart";
            var total = response.Content.Headers.ContentLength ?? -1;
            long loaded = 0;
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                var buffer = new byte[64 * 1024];
                int read;
                var lastProgress = DateTime.UtcNow;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, state.Cancellation.Token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read, state.Cancellation.Token).ConfigureAwait(false);
                    loaded += read;
                    if ((DateTime.UtcNow - lastProgress).TotalMilliseconds >= 80 || (total > 0 && loaded == total))
                    {
                        lastProgress = DateTime.UtcNow;
                        Post(state, id, "progress", new { phase = "download", loaded, total, lengthComputable = total >= 0, readyState = 3 });
                    }
                }
            }
            File.Move(temporaryPath, destination);
            temporaryPath = null;
            Post(state, id, "load", new { status = (int)response.StatusCode, readyState = 4, finalUrl, path = destination, loaded, total, lengthComputable = total >= 0 });
        }
        catch (OperationCanceledException)
        {
            Post(state, id, state.Aborted ? "abort" : "timeout", new { status = 0, readyState = 4 });
        }
        catch (Exception ex)
        {
            Post(state, id, "error", new { status = 0, readyState = 4, error = ex.Message });
        }
        finally
        {
            if (temporaryPath is not null) try { File.Delete(temporaryPath); } catch { }
            state.Completed = true;
        }
    }

    private static HttpRequestMessage CreateRequest(JsonElement options, string cookies, Action<long, long>? uploadProgress)
    {
        var url = GetString(options, "url");
        if (!IsHttpUrl(url)) throw new InvalidOperationException("Only HTTP and HTTPS URLs are allowed.");
        var method = GetString(options, "method");
        var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method), url);
        var body = GetString(options, "body");
        var bodyBase64 = GetString(options, "bodyBase64");
        var bytes = bodyBase64.Length > 0 ? Convert.FromBase64String(bodyBase64) : Encoding.UTF8.GetBytes(body);
        if (bytes.Length > MaxUploadBytes) throw new InvalidDataException($"Userscript upload exceeds the {MaxUploadBytes / 1024 / 1024} MB limit.");
        if (bytes.Length > 0 || method.Equals("POST", StringComparison.OrdinalIgnoreCase) || method.Equals("PUT", StringComparison.OrdinalIgnoreCase) || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
            request.Content = uploadProgress is null ? new ByteArrayContent(bytes) : new ProgressByteArrayContent(bytes, uploadProgress);
        if (options.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                var value = header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString() ?? string.Empty : header.Value.ToString();
                if (!request.Headers.TryAddWithoutValidation(header.Name, value)) request.Content?.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }
        if (cookies.Length > 0 && !request.Headers.Contains("Cookie")) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        return request;
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static void Post(RequestState state, string id, string eventName, object details)
    {
        var detailsJson = JsonSerializer.Serialize(details);
        var json = "{\"kind\":\"zzz-userscript-net-event\",\"id\":" + JsonSerializer.Serialize(id) + ",\"event\":" + JsonSerializer.Serialize(eventName) + ",\"details\":" + detailsJson + "}";
        state.Events.Enqueue(json);
    }

    private static bool IsHttpUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    private static bool FixedEquals(string left, string right)
    {
        if (left.Length == 0 || left.Length != right.Length) return false;
        var difference = 0;
        for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }
    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static int GetInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static bool GetBool(JsonElement element, string name, bool fallback) => element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) ? value.GetBoolean() : fallback;
    private static string DecodeText(byte[] bytes, string? charset) { try { var name = string.IsNullOrWhiteSpace(charset) ? "utf-8" : charset!.Trim('"'); return Encoding.GetEncoding(name).GetString(bytes); } catch { return Encoding.UTF8.GetString(bytes); } }
    private static string SafeFileName(string value) { foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return Path.GetFileName(value).Trim(); }
    private static string UniquePath(string path) { if (!File.Exists(path)) return path; var folder = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path); for (var i = 2; ; i++) { var candidate = Path.Combine(folder, stem + " (" + i + ")" + ext); if (!File.Exists(candidate)) return candidate; } }

    public void Dispose()
    {
        foreach (var state in _requests.Values) { state.Aborted = true; try { state.Cancellation.Cancel(); } catch { } }
    }

    private sealed class ProgressByteArrayContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly Action<long, long> _progress;
        public ProgressByteArrayContent(byte[] bytes, Action<long, long> progress) { _bytes = bytes; _progress = progress; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            const int size = 64 * 1024;
            long sent = 0;
            for (var offset = 0; offset < _bytes.Length; offset += size)
            {
                var count = Math.Min(size, _bytes.Length - offset);
                await stream.WriteAsync(_bytes, offset, count).ConfigureAwait(false);
                sent += count;
                _progress(sent, _bytes.Length);
            }
        }
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
    }
}

public static class UserScriptPermissionPolicy
{
    public static bool HasGrant(UserScript script, params string[] names)
    {
        if (script.Grants.Any(x => x.Equals("none", StringComparison.OrdinalIgnoreCase))) return false;
        return script.Grants.Any(grant => grant == "*" || names.Any(name => grant.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool CanConnect(UserScript script, string documentUrl, string targetUrl)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)) return false;

        if (Uri.TryCreate(documentUrl, UriKind.Absolute, out var document) && SameOrigin(document, target)) return true;
        foreach (var raw in script.Connects)
        {
            var rule = raw.Trim();
            if (rule.Length == 0) continue;
            if (rule == "*") return true;
            if (rule.Equals("self", StringComparison.OrdinalIgnoreCase) && document is not null && SameOrigin(document, target)) return true;
            if (rule.Contains("://") && UserScriptMatcher.MatchPattern(rule.Contains('*') ? rule : rule.TrimEnd('/') + "/*", target.AbsoluteUri)) return true;

            var hostRule = rule.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
            if (hostRule.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = hostRule.Substring(2);
                if (target.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || target.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (target.Host.Equals(hostRule, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}

public static class UserScriptRuntime
{
    public static string BuildBootstrap(IEnumerable<UserScript> scripts, string bridgeToken, IReadOnlyDictionary<string, string> authorizationTokens)
    {
        var output = new StringBuilder("(()=>{'use strict';window.__zzzUserscripts=window.__zzzUserscripts||Object.create(null);\n");
        output.Append("const __zzzBridgeToken=").Append(JsonSerializer.Serialize(bridgeToken)).AppendLine(";");
        output.AppendLine(NetworkBootstrap);
        foreach (var script in scripts.Where(x => x.Enabled))
            if (authorizationTokens.TryGetValue(script.Id, out var authorization)) AppendScript(output, script, authorization);
        output.AppendLine("})();");
        return output.ToString();
    }

    private static void AppendScript(StringBuilder output, UserScript script, string authorization)
    {
        var includes = script.Matches.Concat(script.Includes).ToArray();
        if (includes.Length == 0) includes = new[] { string.IsNullOrWhiteSpace(script.Match) ? "*" : script.Match };
        var sourceName = string.IsNullOrWhiteSpace(script.SourceUrl) ? $"zzz-userscript-{script.Id}.user.js" : script.SourceUrl.Replace("\r", "").Replace("\n", "");
        var info = JsonSerializer.Serialize(new
        {
            script = new { name = script.Name, @namespace = script.Namespace, version = script.Version, description = script.Description, runAt = script.RunAt },
            scriptMetaStr = string.Empty,
            version = "1.9.0",
            handler = "ZZZ Userscript"
        });
        var resources = JsonSerializer.Serialize(script.Resources);
        var grants = JsonSerializer.Serialize(script.Grants);
        var authorizationJson = JsonSerializer.Serialize(authorization);
        var includeJson = JsonSerializer.Serialize(includes);
        var excludeJson = JsonSerializer.Serialize(script.Excludes);
        var idJson = JsonSerializer.Serialize(script.Id);
        var noFrames = script.NoFrames ? "true" : "false";
        var runAt = script.RunAt?.ToLowerInvariant() switch
        {
            "document-start" => "start",
            "document-end" => "end",
            _ => "idle"
        };

        output.Append("(()=>{const __id=").Append(idJson).Append(",__authorization=").Append(authorizationJson).Append(";if(window.__zzzUserscripts[__id])return;")
            .Append("const __glob=(p,u)=>{if(!p||p==='*'||p==='<all_urls>')return true;const e=p.replace(/[.+?^${}()|[\\]\\\\]/g,'\\\\$&').replace(/\\*:\\/\\//g,'https?:\\/\\/').replace(/\\*\\\\\\./g,'(?:[^/]+\\\\.)?').replace(/\\*/g,'.*');try{return new RegExp('^'+e+'$','i').test(u)}catch{return false}};")
            .Append("const __inc=").Append(includeJson).Append(",__exc=").Append(excludeJson).Append(";if(!__inc.some(p=>__glob(p,location.href))||__exc.some(p=>__glob(p,location.href))||(")
            .Append(noFrames).Append("&&top!==self))return;window.__zzzUserscripts[__id]=true;const __grants=").Append(grants).Append(";const __has=(...n)=>!__grants.some(g=>String(g).toLowerCase()==='none')&&(__grants.includes('*')||n.some(x=>__grants.some(g=>String(g).toLowerCase()===String(x).toLowerCase())));")
            .Append("const __sync=()=>chrome.webview.hostObjects.sync.zzzUserscript;")
            .Append("const GM_getValue=__has('GM_getValue','GM.getValue')?((k,d)=>{try{return JSON.parse(__sync().GetValue(__zzzBridgeToken,__authorization,__id,String(k),JSON.stringify(d)))}catch{return d}}):undefined;")
            .Append("const GM_setValue=__has('GM_setValue','GM.setValue')?((k,v)=>{__sync().SetValue(__zzzBridgeToken,__authorization,__id,String(k),JSON.stringify(v));}):undefined;")
            .Append("const GM_deleteValue=__has('GM_deleteValue','GM.deleteValue')?(k=>{__sync().DeleteValue(__zzzBridgeToken,__authorization,__id,String(k));}):undefined;")
            .Append("const GM_listValues=__has('GM_listValues','GM.listValues')?(()=>{try{return JSON.parse(__sync().ListValues(__zzzBridgeToken,__authorization,__id))}catch{return[]}}):undefined;")
            .Append("const GM_addStyle=__has('GM_addStyle','GM.addStyle')?(css=>{const s=document.createElement('style');s.textContent=css;(document.head||document.documentElement).appendChild(s);return s}):undefined;")
            .Append("const GM_openInTab=__has('GM_openInTab','GM.openInTab')?((u,o)=>{const w=open(u,'_blank');return{close:()=>w&&w.close(),closed:!!(w&&w.closed)}}):undefined;")
            .Append("const GM_setClipboard=__has('GM_setClipboard','GM.setClipboard')?(t=>navigator.clipboard?navigator.clipboard.writeText(String(t)):Promise.resolve()):undefined;")
            .Append("const GM_notification=__has('GM_notification','GM.notification')?((t,title)=>{const d=typeof t==='object'?t:{text:t,title:title};if(Notification.permission==='granted')new Notification(d.title||'ZZZ',{body:d.text||''});else Notification.requestPermission()}):undefined;")
            .Append("const GM_download=__has('GM_download','GM.download')?((d,n)=>__zzzNet.download(typeof d==='object'?d:{url:d,name:n},__authorization)):undefined;")
            .Append("const GM_registerMenuCommand=__has('GM_registerMenuCommand','GM.registerMenuCommand')?((n,f)=>{(window.__zzzUserScriptCommands=window.__zzzUserScriptCommands||[]).push({name:n,run:f,script:__id});return n}):undefined;")
            .Append("const GM_xmlhttpRequest=__has('GM_xmlhttpRequest','GM.xmlHttpRequest')?(d=>__zzzNet.request(d,__authorization)):undefined;")
            .Append("const __resources=").Append(resources).Append(";const GM_getResourceURL=__has('GM_getResourceURL','GM.getResourceUrl')?(n=>__resources[n]||null):undefined;const GM_getResourceText=__has('GM_getResourceText','GM.getResourceText')?(n=>fetch(__resources[n]).then(r=>r.text())):undefined;")
            .Append("const GM_info=").Append(info).Append(";const unsafeWindow=__has('unsafeWindow')?window:undefined;")
            .Append("const GM={info:GM_info};if(GM_getValue)GM.getValue=(k,d)=>Promise.resolve(GM_getValue(k,d));if(GM_setValue)GM.setValue=(k,v)=>Promise.resolve(GM_setValue(k,v));if(GM_deleteValue)GM.deleteValue=k=>Promise.resolve(GM_deleteValue(k));if(GM_listValues)GM.listValues=()=>Promise.resolve(GM_listValues());if(GM_addStyle)GM.addStyle=css=>Promise.resolve(GM_addStyle(css));if(GM_openInTab)GM.openInTab=GM_openInTab;if(GM_setClipboard)GM.setClipboard=GM_setClipboard;if(GM_notification)GM.notification=GM_notification;if(GM_download)GM.download=GM_download;if(GM_xmlhttpRequest)GM.xmlHttpRequest=GM_xmlhttpRequest;if(GM_getResourceURL)GM.getResourceUrl=n=>Promise.resolve(GM_getResourceURL(n));if(GM_getResourceText)GM.getResourceText=GM_getResourceText;if(GM_registerMenuCommand)GM.registerMenuCommand=GM_registerMenuCommand;")
            .Append("const __run=()=>{try{\n").Append(script.RequiredCode).AppendLine().Append(script.Code).Append("\n}catch(e){console.error('[ZZZ userscript] ',GM_info.script.name,e)}};");
        if (runAt == "start") output.Append("__run();");
        else if (runAt == "end") output.Append("if(document.readyState==='loading')addEventListener('DOMContentLoaded',__run,{once:true});else __run();");
        else output.Append("if(document.readyState==='complete')setTimeout(__run,0);else addEventListener('load',()=>setTimeout(__run,0),{once:true});");
        output.Append("})();\n//# sourceURL=").Append(sourceName).AppendLine();
    }

    private const string NetworkBootstrap = @"
const __zzzNet=(()=>{
 const requests=new Map(),host=()=>chrome.webview.hostObjects.sync.zzzUserscript,schedule=setTimeout.bind(window);let sequence=0;
 const uid=()=>Date.now().toString(36)+'-'+(++sequence).toString(36)+'-'+Math.random().toString(36).slice(2);
 const toBase64=bytes=>{let s='';const a=new Uint8Array(bytes);for(let i=0;i<a.length;i+=0x8000)s+=String.fromCharCode.apply(null,a.subarray(i,i+0x8000));return btoa(s)};
 const payload=async data=>{if(data==null)return{body:'',bodyBase64:''};if(typeof data==='string')return{body:data,bodyBase64:''};if(data instanceof Blob)return{body:'',bodyBase64:toBase64(await data.arrayBuffer())};if(data instanceof ArrayBuffer)return{body:'',bodyBase64:toBase64(data)};if(ArrayBuffer.isView(data))return{body:'',bodyBase64:toBase64(data.buffer.slice(data.byteOffset,data.byteOffset+data.byteLength))};if(data instanceof URLSearchParams)return{body:data.toString(),bodyBase64:''};return{body:String(data),bodyBase64:''}};
 const handle=m=>{if(!m||m.kind!=='zzz-userscript-net-event')return;const s=requests.get(m.id);if(!s)return;const d=s.options,x=Object.assign({},m.details||{});if(x.responseBase64){const raw=atob(x.responseBase64),a=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)a[i]=raw.charCodeAt(i);x.response=x.responseType==='blob'?new Blob([a],{type:x.mime||''}):a.buffer}else if(x.responseType==='json'){try{x.response=JSON.parse(x.responseText)}catch{x.response=null}}else x.response=x.responseText;if(m.event==='readystatechange'||m.event==='progress'||m.event==='load')try{d.onreadystatechange&&d.onreadystatechange(x)}catch{}if(m.event==='progress'){try{if(x.phase==='upload'&&d.upload&&d.upload.onprogress)d.upload.onprogress(x);else d.onprogress&&d.onprogress(x)}catch{}return}if(!['load','error','abort','timeout'].includes(m.event))return;requests.delete(m.id);try{const cb=d['on'+m.event];cb&&cb(x)}catch{}try{d.onloadend&&d.onloadend(x)}catch{}if(m.event==='load')s.resolve(x);else s.reject(x)};
 const poll=(id,authorization)=>{if(!requests.has(id))return;try{JSON.parse(host().PollNetworkEvents(__zzzBridgeToken,id,authorization)).forEach(handle)}catch{}if(requests.has(id))schedule(()=>poll(id,authorization),35)};
 const send=message=>host().NetworkMessage(__zzzBridgeToken,JSON.stringify(message));
 const start=(kind,options,authorization)=>{const d=options||{},id=uid(),auth=String(authorization||'');let resolve,reject;const promise=new Promise((ok,no)=>{resolve=ok;reject=no});promise.catch(()=>{});requests.set(id,{options:d,resolve,reject});try{d.onreadystatechange&&d.onreadystatechange({readyState:1,status:0})}catch{};(async()=>{const p=await payload(d.data);const request={method:d.method||'GET',url:String(d.url||''),headers:d.headers||{},body:p.body,bodyBase64:p.bodyBase64,responseType:d.responseType||'',timeout:Number(d.timeout)||0,useCookies:d.anonymous!==true&&d.withCredentials!==false,name:d.name||'',authorization:auth,documentUrl:location.href};send({kind:kind==='download'?'zzz-userscript-net-download':'zzz-userscript-net-request',id,request});poll(id,auth)})().catch(error=>{const s=requests.get(id);if(!s)return;requests.delete(id);const x={status:0,readyState:4,error:'bridge: '+String(error)};try{d.onerror&&d.onerror(x);d.onloadend&&d.onloadend(x)}catch{}reject(x)});return{abort:()=>send({kind:'zzz-userscript-net-abort',id,authorization:auth}),then:promise.then.bind(promise),catch:promise.catch.bind(promise),finally:promise.finally?promise.finally.bind(promise):undefined}};
 return{request:(d,a)=>start('request',d,a),download:(d,a)=>start('download',d,a)};
})();";
}
