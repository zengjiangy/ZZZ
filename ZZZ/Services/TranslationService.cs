using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Web.WebView2.Core;

namespace ZZZ.Services;

public interface ITranslationService
{
    Task<int> TranslatePageAsync(CoreWebView2 core, string targetLanguage);
    Task<bool> ShouldAutoTranslateAsync(CoreWebView2 core, string targetLanguage);
}

/// <summary>
/// Translates the current DOM in place using the same token service and request
/// identity used by Microsoft Edge. This avoids region-blocked translation
/// proxy pages and keeps the user on the original site.
/// </summary>
public sealed class TranslationService : ITranslationService, IDisposable
{
    private const string EdgeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0";
    private const string CollectScript = """
        (() => {
          const result = [], refs = [];
          if (!document.body) return result;
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode(node) {
              const parent = node.parentElement;
              const text = node.nodeValue || '';
              if (!parent || text.trim().length < 2) return NodeFilter.FILTER_REJECT;
              if (parent.closest('script,style,noscript,textarea,input,select,option,code,pre,[contenteditable="true"],[data-zzz-no-translate]')) return NodeFilter.FILTER_REJECT;
              return NodeFilter.FILTER_ACCEPT;
            }
          });
          let node, total = 0;
          while ((node = walker.nextNode()) && result.length < 1200 && total < 60000) {
            const text = node.nodeValue || '';
            if (total + text.length > 60000) break;
            const id = result.length;
            refs[id] = node;
            result.push({ id, text });
            total += text.length;
          }
          window.__zzzTranslationNodes = refs;
          return result;
        })()
        """;

    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _token = string.Empty;
    private DateTime _tokenExpiresUtc;

    public TranslationService()
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(EdgeUserAgent);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    }

    public async Task<bool> ShouldAutoTranslateAsync(CoreWebView2 core, string targetLanguage)
    {
        try
        {
            var raw = await core.ExecuteScriptAsync("document.documentElement.lang || document.querySelector('meta[http-equiv=\"content-language\" i]')?.content || ''");
            var source = JsonSerializer.Deserialize<string>(raw)?.Split(',', ';')[0].Trim();
            if (string.IsNullOrWhiteSpace(source)) return false;
            return !SameLanguage(source!, targetLanguage);
        }
        catch { return false; }
    }

    public async Task<int> TranslatePageAsync(CoreWebView2 core, string targetLanguage)
    {
        await _gate.WaitAsync();
        try
        {
            var raw = await core.ExecuteScriptAsync(CollectScript);
            var segments = JsonSerializer.Deserialize<List<PageSegment>>(raw, JsonOptions) ?? [];
            if (segments.Count == 0) return 0;

            var translated = new List<PageSegment>(segments.Count);
            foreach (var batch in Batch(segments, 40, 12000))
            {
                var values = await TranslateBatchAsync(batch.Select(x => x.Text).ToArray(), NormalizeLanguage(targetLanguage), true);
                for (var i = 0; i < batch.Count && i < values.Count; i++)
                    translated.Add(new PageSegment { Id = batch[i].Id, Text = string.IsNullOrWhiteSpace(values[i]) ? batch[i].Text : values[i] });
            }

            foreach (var batch in Batch(translated, 100, int.MaxValue))
            {
                var json = JsonSerializer.Serialize(batch, JsonOptions);
                await core.ExecuteScriptAsync($"(() => {{ const updates={json}, refs=window.__zzzTranslationNodes||[]; for (const item of updates) {{ const node=refs[item.id]; if (node && node.isConnected) node.nodeValue=item.text; }} }})()");
            }
            return translated.Count;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string target, bool allowTokenRefresh)
    {
        var token = await GetTokenAsync();
        var requestBody = JsonSerializer.Serialize(texts.Select(x => new Dictionary<string, string> { ["Text"] = x }));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(target)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowTokenRefresh)
        {
            _tokenExpiresUtc = DateTime.MinValue;
            return await TranslateBatchAsync(texts, target, false);
        }
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<List<TranslationResponse>>(json, JsonOptions) ?? [];
        return items.Select(x => x.Translations.FirstOrDefault()?.Text ?? string.Empty).ToArray();
    }

    private async Task<string> GetTokenAsync()
    {
        if (_tokenExpiresUtc > DateTime.UtcNow.AddMinutes(1) && _token.Length > 0) return _token;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://edge.microsoft.com/translate/auth");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        _token = (await response.Content.ReadAsStringAsync()).Trim();
        _tokenExpiresUtc = DateTime.UtcNow.AddMinutes(8);
        return _token;
    }

    private static List<List<PageSegment>> Batch(IReadOnlyList<PageSegment> items, int maxItems, int maxCharacters)
    {
        var batches = new List<List<PageSegment>>();
        var current = new List<PageSegment>();
        var characters = 0;
        foreach (var item in items)
        {
            if (current.Count > 0 && (current.Count >= maxItems || characters + item.Text.Length > maxCharacters))
            {
                batches.Add(current);
                current = [];
                characters = 0;
            }
            current.Add(item);
            characters += item.Text.Length;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    private static bool SameLanguage(string left, string right)
    {
        static string Base(string value) => value.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return Base(left) == Base(right);
    }

    private static string NormalizeLanguage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "zh-cn" or "zh-sg" or "zh-hans" => "zh-Hans",
        "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" => "zh-Hant",
        _ => value.Trim()
    };

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed class PageSegment { public int Id { get; set; } public string Text { get; set; } = string.Empty; }
    private sealed class TranslationResponse { public List<TranslatedValue> Translations { get; set; } = []; }
    private sealed class TranslatedValue { public string Text { get; set; } = string.Empty; }
}
