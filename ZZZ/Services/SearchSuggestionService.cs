using System.Net.Http;
using System.Text.Json;
using ZZZ.Configuration;

namespace ZZZ.Services;

public static class SearchSuggestionService
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<IReadOnlyList<string>> GetAsync(SearchEngine engine, string query, CancellationToken cancellationToken)
    {
        var endpoint = EndpointFor(engine.Id, query);
        if (endpoint is null) return [];
        try
        {
            using var response = await Client.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() < 2 ||
                document.RootElement[1].ValueKind != JsonValueKind.Array) return [];
            return document.RootElement[1].EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()?.Trim() ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(6)
                .ToArray();
        }
        catch (OperationCanceledException) { return []; }
        catch { return []; }
    }

    private static string? EndpointFor(string engineId, string query)
    {
        var encoded = Uri.EscapeDataString(query);
        return engineId.ToLowerInvariant() switch
        {
            "bing" => $"https://api.bing.com/osjson.aspx?query={encoded}",
            "google" => $"https://suggestqueries.google.com/complete/search?client=firefox&q={encoded}",
            "baidu" => $"https://suggestion.baidu.com/su?wd={encoded}&action=opensearch",
            "duckduckgo" => $"https://duckduckgo.com/ac/?q={encoded}&type=list",
            _ => null
        };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZZZ-Browser/1.5");
        return client;
    }
}
