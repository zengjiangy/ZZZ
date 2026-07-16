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

    public string PollNetworkEvents(string token, string requestId)
    {
        Validate(token);
        return _network?.Poll(requestId) ?? "[]";
    }

    public string GetValue(string token, string scriptId, string key, string fallbackJson)
    {
        Validate(token);
        lock (FileGate)
        {
            var values = Values();
            return values.TryGetValue(scriptId, out var script) && script.TryGetValue(key, out var value) ? value : fallbackJson;
        }
    }

    public void SetValue(string token, string scriptId, string key, string valueJson)
    {
        Validate(token);
        lock (FileGate)
        {
            var values = Values();
            if (!values.TryGetValue(scriptId, out var script)) values[scriptId] = script = [];
            script[key] = valueJson;
            Save(values);
        }
    }

    public void DeleteValue(string token, string scriptId, string key)
    {
        Validate(token);
        lock (FileGate)
        {
            var values = Values();
            if (values.TryGetValue(scriptId, out var script)) script.Remove(key);
            Save(values);
        }
    }

    public string ListValues(string token, string scriptId)
    {
        Validate(token);
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
    private sealed class RequestState
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        public bool Aborted { get; set; }
        public bool Completed { get; set; }
    }

    private readonly CoreWebView2 _core;
    private readonly ISettingsService _settings;
    private readonly ConcurrentDictionary<string, RequestState> _requests = new(StringComparer.Ordinal);

    public UserScriptNetworkBroker(CoreWebView2 core, ISettingsService settings)
    {
        _core = core;
        _settings = settings;
    }

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
                if (_requests.TryGetValue(id, out var existing)) { existing.Aborted = true; existing.Cancellation.Cancel(); }
                return;
            }
            if (!root.TryGetProperty("request", out var requestElement)) return;
            var request = requestElement.Clone();
            var state = new RequestState();
            if (!_requests.TryAdd(id, state)) return;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () => await StartAsync(id, kind, request, state)));
        }
        catch { }
    }

    public string Poll(string id)
    {
        if (!_requests.TryGetValue(id, out var state)) return "[]";
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
        var cookieHeader = string.Empty;
        if (GetBool(request, "useCookies", true) && IsHttpUrl(url))
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
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream(total is > 0 and <= int.MaxValue ? (int)total : 0);
            var buffer = new byte[64 * 1024];
            long loaded = 0;
            int read;
            var lastProgress = DateTime.UtcNow;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, state.Cancellation.Token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer, 0, read, state.Cancellation.Token).ConfigureAwait(false);
                loaded += read;
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

public static class UserScriptRuntime
{
    public static string BuildBootstrap(IEnumerable<UserScript> scripts, string bridgeToken)
    {
        var output = new StringBuilder("(()=>{'use strict';window.__zzzUserscripts=window.__zzzUserscripts||Object.create(null);\n");
        output.Append("const __zzzBridgeToken=").Append(JsonSerializer.Serialize(bridgeToken)).AppendLine(";");
        output.AppendLine(NetworkBootstrap);
        foreach (var script in scripts.Where(x => x.Enabled)) AppendScript(output, script);
        output.AppendLine("})();");
        return output.ToString();
    }

    private static void AppendScript(StringBuilder output, UserScript script)
    {
        var includes = script.Matches.Concat(script.Includes).ToArray();
        if (includes.Length == 0) includes = new[] { string.IsNullOrWhiteSpace(script.Match) ? "*" : script.Match };
        var sourceName = string.IsNullOrWhiteSpace(script.SourceUrl) ? $"zzz-userscript-{script.Id}.user.js" : script.SourceUrl.Replace("\r", "").Replace("\n", "");
        var info = JsonSerializer.Serialize(new
        {
            script = new { name = script.Name, @namespace = script.Namespace, version = script.Version, description = script.Description, runAt = script.RunAt },
            scriptMetaStr = string.Empty,
            version = "1.7.0",
            handler = "ZZZ Userscript"
        });
        var resources = JsonSerializer.Serialize(script.Resources);
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

        output.Append("(()=>{const __id=").Append(idJson).Append(";if(window.__zzzUserscripts[__id])return;")
            .Append("const __glob=(p,u)=>{if(!p||p==='*'||p==='<all_urls>')return true;const e=p.replace(/[.+?^${}()|[\\]\\\\]/g,'\\\\$&').replace(/\\*:\\/\\//g,'https?:\\/\\/').replace(/\\*\\\\\\./g,'(?:[^/]+\\\\.)?').replace(/\\*/g,'.*');try{return new RegExp('^'+e+'$','i').test(u)}catch{return false}};")
            .Append("const __inc=").Append(includeJson).Append(",__exc=").Append(excludeJson).Append(";if(!__inc.some(p=>__glob(p,location.href))||__exc.some(p=>__glob(p,location.href))||(")
            .Append(noFrames).Append("&&top!==self))return;window.__zzzUserscripts[__id]=true;")
            .Append("const __sync=()=>chrome.webview.hostObjects.sync.zzzUserscript;")
            .Append("const GM_getValue=(k,d)=>{try{return JSON.parse(__sync().GetValue(__zzzBridgeToken,__id,String(k),JSON.stringify(d)))}catch{return d}};")
            .Append("const GM_setValue=(k,v)=>{__sync().SetValue(__zzzBridgeToken,__id,String(k),JSON.stringify(v));};")
            .Append("const GM_deleteValue=k=>{__sync().DeleteValue(__zzzBridgeToken,__id,String(k));};")
            .Append("const GM_listValues=()=>{try{return JSON.parse(__sync().ListValues(__zzzBridgeToken,__id))}catch{return[]}};")
            .Append("const GM_addStyle=css=>{const s=document.createElement('style');s.textContent=css;(document.head||document.documentElement).appendChild(s);return s};")
            .Append("const GM_openInTab=(u,o)=>{const w=open(u,(o&&o.active===false)?'_blank':'_blank');return{close:()=>w&&w.close(),closed:!!(w&&w.closed)}};")
            .Append("const GM_setClipboard=t=>navigator.clipboard?navigator.clipboard.writeText(String(t)):Promise.resolve();")
            .Append("const GM_notification=(t,title)=>{const d=typeof t==='object'?t:{text:t,title:title};if(Notification.permission==='granted')new Notification(d.title||'ZZZ',{body:d.text||''});else Notification.requestPermission()};")
            .Append("const GM_download=(d,n)=>__zzzNet.download(typeof d==='object'?d:{url:d,name:n});")
            .Append("const GM_registerMenuCommand=(n,f)=>{(window.__zzzUserScriptCommands=window.__zzzUserScriptCommands||[]).push({name:n,run:f,script:__id});return n};")
            .Append("const GM_xmlhttpRequest=d=>__zzzNet.request(d);")
            .Append("const __resources=").Append(resources).Append(";const GM_getResourceURL=n=>__resources[n]||null;const GM_getResourceText=n=>fetch(__resources[n]).then(r=>r.text());")
            .Append("const GM_info=").Append(info).Append(";const unsafeWindow=window;")
            .Append("const GM={info:GM_info,getValue:(k,d)=>Promise.resolve(GM_getValue(k,d)),setValue:(k,v)=>Promise.resolve(GM_setValue(k,v)),deleteValue:k=>Promise.resolve(GM_deleteValue(k)),listValues:()=>Promise.resolve(GM_listValues()),addStyle:css=>Promise.resolve(GM_addStyle(css)),openInTab:GM_openInTab,setClipboard:GM_setClipboard,notification:GM_notification,download:GM_download,xmlHttpRequest:GM_xmlhttpRequest,getResourceUrl:n=>Promise.resolve(GM_getResourceURL(n)),getResourceText:GM_getResourceText,registerMenuCommand:GM_registerMenuCommand};")
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
 const poll=id=>{if(!requests.has(id))return;try{JSON.parse(host().PollNetworkEvents(__zzzBridgeToken,id)).forEach(handle)}catch{}if(requests.has(id))schedule(()=>poll(id),35)};
 const send=message=>host().NetworkMessage(__zzzBridgeToken,JSON.stringify(message));
 const start=(kind,options)=>{const d=options||{},id=uid();let resolve,reject;const promise=new Promise((ok,no)=>{resolve=ok;reject=no});promise.catch(()=>{});requests.set(id,{options:d,resolve,reject});try{d.onreadystatechange&&d.onreadystatechange({readyState:1,status:0})}catch{};(async()=>{const p=await payload(d.data);const request={method:d.method||'GET',url:String(d.url||''),headers:d.headers||{},body:p.body,bodyBase64:p.bodyBase64,responseType:d.responseType||'',timeout:Number(d.timeout)||0,useCookies:d.anonymous!==true&&d.withCredentials!==false,name:d.name||''};send({kind:kind==='download'?'zzz-userscript-net-download':'zzz-userscript-net-request',id,request});poll(id)})().catch(error=>{const s=requests.get(id);if(!s)return;requests.delete(id);const x={status:0,readyState:4,error:'bridge: '+String(error)};try{d.onerror&&d.onerror(x);d.onloadend&&d.onloadend(x)}catch{}reject(x)});return{abort:()=>send({kind:'zzz-userscript-net-abort',id}),then:promise.then.bind(promise),catch:promise.catch.bind(promise),finally:promise.finally?promise.finally.bind(promise):undefined}};
 return{request:d=>start('request',d),download:d=>start('download',d)};
})();";
}
