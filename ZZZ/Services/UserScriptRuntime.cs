using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ZZZ.Models;

namespace ZZZ.Services;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class UserScriptBridge
{
    private static readonly object FileGate = new();
    private static Dictionary<string, Dictionary<string, string>>? SharedValues;
    private static readonly HttpClient Client = CreateClient();
    private readonly bool _persistent;
    private readonly string _secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Dictionary<string, string>> _privateValues = [];

    public UserScriptBridge(bool persistent) => _persistent = persistent;
    internal string SecretForBootstrap => _secret;

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

    public string HttpRequest(string token, string method, string url, string headersJson, string body)
    {
        try
        {
            Validate(token);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Only HTTP and HTTPS URLs are allowed.");
            using var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method), uri);
            if (!string.IsNullOrEmpty(body)) request.Content = new StringContent(body);
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson) ?? [];
            foreach (var pair in headers)
                if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) request.Content?.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            using var response = Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("GM_xmlhttpRequest response is larger than 16 MB.");
            var responseText = Encoding.UTF8.GetString(bytes);
            var responseHeaders = string.Join("\r\n", response.Headers.Concat(response.Content.Headers).SelectMany(x => x.Value.Select(v => $"{x.Key}: {v}")));
            return JsonSerializer.Serialize(new
            {
                status = (int)response.StatusCode,
                statusText = response.ReasonPhrase ?? string.Empty,
                responseText,
                response = responseText,
                finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url,
                responseHeaders,
                readyState = 4
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message, status = 0, readyState = 4 });
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36 ZZZ/1.1");
        return client;
    }
}

public static class UserScriptRuntime
{
    public static string BuildBootstrap(IEnumerable<UserScript> scripts, string bridgeToken)
    {
        var output = new StringBuilder("(()=>{'use strict';window.__zzzUserscripts=window.__zzzUserscripts||Object.create(null);\n");
        output.Append("const __zzzBridgeToken=").Append(JsonSerializer.Serialize(bridgeToken)).AppendLine(";");
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
            version = "1.1",
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
            .Append("const GM_download=(d,n)=>{const o=typeof d==='object'?d:{url:d,name:n};const a=document.createElement('a');a.href=o.url;a.download=o.name||'';a.click()};")
            .Append("const GM_registerMenuCommand=(n,f)=>{(window.__zzzUserScriptCommands=window.__zzzUserScriptCommands||[]).push({name:n,run:f,script:__id});return n};")
            .Append("const GM_xmlhttpRequest=d=>{let dead=false;const p=chrome.webview.hostObjects.async.zzzUserscript.HttpRequest(__zzzBridgeToken,d.method||'GET',d.url,JSON.stringify(d.headers||{}),d.data||'').then(JSON.parse).then(r=>{if(dead)return r;if(r.error){d.onerror&&d.onerror(r)}else{d.onload&&d.onload(r)}d.onloadend&&d.onloadend(r);return r});return{abort:()=>{dead=true},then:p.then.bind(p),catch:p.catch.bind(p)}};")
            .Append("const __resources=").Append(resources).Append(";const GM_getResourceURL=n=>__resources[n]||null;const GM_getResourceText=n=>fetch(__resources[n]).then(r=>r.text());")
            .Append("const GM_info=").Append(info).Append(";const unsafeWindow=window;")
            .Append("const GM={info:GM_info,getValue:(k,d)=>Promise.resolve(GM_getValue(k,d)),setValue:(k,v)=>Promise.resolve(GM_setValue(k,v)),deleteValue:k=>Promise.resolve(GM_deleteValue(k)),listValues:()=>Promise.resolve(GM_listValues()),addStyle:css=>Promise.resolve(GM_addStyle(css)),openInTab:GM_openInTab,setClipboard:GM_setClipboard,notification:GM_notification,download:GM_download,xmlHttpRequest:GM_xmlhttpRequest,getResourceUrl:n=>Promise.resolve(GM_getResourceURL(n)),getResourceText:GM_getResourceText,registerMenuCommand:GM_registerMenuCommand};")
            .Append("const __run=()=>{try{\n").Append(script.RequiredCode).AppendLine().Append(script.Code).Append("\n}catch(e){console.error('[ZZZ userscript] ',GM_info.script.name,e)}};");
        if (runAt == "start") output.Append("__run();");
        else if (runAt == "end") output.Append("if(document.readyState==='loading')addEventListener('DOMContentLoaded',__run,{once:true});else __run();");
        else output.Append("if(document.readyState==='complete')setTimeout(__run,0);else addEventListener('load',()=>setTimeout(__run,0),{once:true});");
        output.Append("})();\n//# sourceURL=").Append(sourceName).AppendLine();
    }
}
