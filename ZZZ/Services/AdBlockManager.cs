using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using ZZZ.Models;

namespace ZZZ.Services;

public sealed class AdBlockManager : IDisposable
{
    private const int MaximumSubscriptionBytes = 20 * 1024 * 1024;
    private const int MaximumCustomRulesBytes = 8 * 1024 * 1024;
    private const int MaximumCustomSubscriptions = 32;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly BuiltInDefinition[] BuiltIns =
    [
        new("easylist", "EasyList", "https://easylist-downloads.adblockplus.org/easylist.txt"),
        new("easylist-china", "EasyList China", "https://easylist-downloads.adblockplus.org/easylistchina.txt"),
        new("cjx-annoyance", "CJX's Annoyance List", "https://raw.githubusercontent.com/cjx82630/cjxlist/master/cjx-annoyance.txt"),
        new("easyprivacy", "EasyPrivacy", "https://easylist-downloads.adblockplus.org/easyprivacy.txt"),
        new("adblock-warning-removal", "Adblock Warning Removal List", "https://easylist-downloads.adblockplus.org/antiadblockfilters.txt")
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private AdBlockConfiguration _configuration = new();
    private string _customRules = string.Empty;
    private AdBlockRuleEngine _engine = AdBlockRuleEngine.Empty;
    private Timer? _updateTimer;
    private bool _loaded;
    private bool _disposed;
    private bool _customRulesUnavailable;

    public AdBlockManager(HttpClient? client = null)
    {
        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
            return;
        }

        _client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // Redirects are handled below so a HTTPS subscription can never
            // silently downgrade to HTTP.
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(45) };
        var platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "Windows NT 10.0",
            Architecture.Arm64 => "Windows NT 10.0; ARM64",
            _ => "Windows NT 10.0; Win64; x64"
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 ({platform}) ZZZ/2.0.6");
        _ownsClient = true;
    }

    public event EventHandler? ConfigurationChanged;
    public event EventHandler? RulesChanged;

    public string CustomRules => _customRules;
    public AdBlockRuleStatistics RuleStatistics => _engine.Statistics;
    public string LastLoadError { get; private set; } = string.Empty;

    public async Task LoadAsync()
    {
        ThrowIfDisposed();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            LastLoadError = string.Empty;
            _customRulesUnavailable = false;
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(ListsDirectory);
            _configuration = await LoadConfigurationAsync().ConfigureAwait(false);
            ReconcileConfiguration(_configuration);
            _customRules = await LoadInitialCustomRulesAsync().ConfigureAwait(false);
            RefreshCachedRuleCounts(_configuration);
            await SaveConfigurationFileAsync(_configuration).ConfigureAwait(false);
            RebuildEngine();
            _loaded = true;
            _updateTimer ??= new Timer(OnUpdateTimer, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30));
        }
        finally { _gate.Release(); }

        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public AdBlockConfiguration GetConfigurationSnapshot() => Clone(_configuration);

    public Task SaveConfigurationAsync(AdBlockConfiguration configuration, string customRules) =>
        SaveConfigurationCoreAsync(configuration, customRules, allowUnavailableReplacement: false);

    private async Task SaveConfigurationCoreAsync(AdBlockConfiguration configuration, string customRules, bool allowUnavailableReplacement)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        ThrowIfDisposed();
        ValidateCustomRules(customRules);
        if (_customRulesUnavailable && !allowUnavailableReplacement && string.IsNullOrEmpty(customRules) && HasNonemptyUnreadCustomRulesFile())
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(LastLoadError)
                ? "The existing custom rule file could not be read and will not be overwritten."
                : LastLoadError);
        var next = Clone(configuration);
        ReconcileConfiguration(next);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var retainedIds = new HashSet<string>(next.Subscriptions.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            var orphanedCaches = _configuration.Subscriptions.Where(x => !x.IsBuiltIn && !retainedIds.Contains(x.Id)).Select(CachePath).ToArray();
            await WriteTextAtomicAsync(CustomRulesPath, NormalizeNewlines(customRules), CancellationToken.None).ConfigureAwait(false);
            await SaveConfigurationFileAsync(next).ConfigureAwait(false);
            _configuration = next;
            _customRules = NormalizeNewlines(customRules);
            _customRulesUnavailable = false;
            LastLoadError = string.Empty;
            RebuildEngine();
            foreach (var cache in orphanedCaches) TryDelete(cache);
        }
        finally { _gate.Release(); }

        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AdBlockSubscription> AddCustomSubscriptionAsync(string name, string url)
    {
        var uri = ValidateSubscriptionUrl(url);
        var snapshot = GetConfigurationSnapshot();
        if (snapshot.Subscriptions.Count(x => !x.IsBuiltIn) >= MaximumCustomSubscriptions)
            throw new InvalidOperationException($"No more than {MaximumCustomSubscriptions} custom filter subscriptions are allowed.");
        if (snapshot.Subscriptions.Any(x => UriEquals(x.Url, uri.AbsoluteUri)))
            throw new InvalidOperationException("This filter subscription has already been added.");

        var subscription = new AdBlockSubscription
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(),
            Url = uri.AbsoluteUri,
            Enabled = true,
            IsBuiltIn = false
        };
        subscription.CacheFileName = CacheFileName(subscription.Id);
        snapshot.Subscriptions.Add(subscription);
        await SaveConfigurationAsync(snapshot, _customRules).ConfigureAwait(false);
        return Clone(subscription);
    }

    public async Task RemoveSubscriptionAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var snapshot = GetConfigurationSnapshot();
        var item = snapshot.Subscriptions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if (item.IsBuiltIn) throw new InvalidOperationException("Built-in subscriptions can be disabled but not removed.");
        snapshot.Subscriptions.Remove(item);
        await SaveConfigurationAsync(snapshot, _customRules).ConfigureAwait(false);
        TryDelete(CachePath(item));
    }

    public Task<IReadOnlyList<AdBlockUpdateResult>> UpdateNowAsync(CancellationToken cancellationToken = default) =>
        UpdateSelectedAsync(null, automatic: false, cancellationToken);

    public Task<IReadOnlyList<AdBlockUpdateResult>> UpdateNowAsync(IEnumerable<string> subscriptionIds, CancellationToken cancellationToken = default) =>
        UpdateSelectedAsync(subscriptionIds, automatic: false, cancellationToken);

    public async Task<IReadOnlyList<AdBlockUpdateResult>> UpdateDueAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AdBlockUpdateResult> results;
        var rulesChanged = false;
        try
        {
            EnsureLoaded();
            if (_configuration.UpdateInterval == AdBlockUpdateInterval.Manual || !AutomaticUpdateIsDue(_configuration))
                return Array.Empty<AdBlockUpdateResult>();
            _configuration.LastAutomaticUpdateCheckUtc = DateTime.UtcNow;
            var update = await UpdateCoreAsync(null, cancellationToken).ConfigureAwait(false);
            results = update.Results;
            rulesChanged = update.RulesChanged;
            await SaveConfigurationFileAsync(_configuration).ConfigureAwait(false);
            if (rulesChanged) RebuildEngine();
        }
        finally { _gate.Release(); }

        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        if (rulesChanged) RulesChanged?.Invoke(this, EventArgs.Empty);
        return results;
    }

    public bool ShouldBlock(AdBlockRequestContext request) => _engine.ShouldBlock(request);

    public string GetCosmeticCss(string documentUrl) => _engine.GetCosmeticCss(documentUrl);

    public async Task AddElementRuleAsync(AdBlockElementRule elementRule)
    {
        if (elementRule is null) throw new ArgumentNullException(nameof(elementRule));
        if (!Uri.TryCreate(elementRule.PageUrl, UriKind.Absolute, out var page) ||
            (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The page URL is not an HTTP(S) URL.", nameof(elementRule));
        var host = page.IdnHost.Trim().Trim('.').ToLowerInvariant();
        var selector = elementRule.Selector.Trim();
        ValidateElementSelector(selector);
        var filter = host + "##" + selector;

        ThrowIfDisposed();
        var changed = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            if (_customRulesUnavailable)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(LastLoadError)
                    ? "The existing custom rule file could not be read."
                    : LastLoadError);
            var lines = SplitLines(_customRules).ToList();
            if (!lines.Any(x => x.Equals(filter, StringComparison.Ordinal)))
            {
                lines.Add(filter);
                var next = string.Join("\r\n", lines.Where(x => x.Length > 0)) + "\r\n";
                ValidateCustomRules(next);
                await WriteTextAtomicAsync(CustomRulesPath, next, CancellationToken.None).ConfigureAwait(false);
                _customRules = next;
                RebuildEngine();
                changed = true;
            }
        }
        finally { _gate.Release(); }

        elementRule.Host = host;
        elementRule.FilterText = filter;
        if (changed) RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ImportCustomRulesAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The rule file was not found.", path);
        if (file.Length > MaximumCustomRulesBytes) throw new InvalidDataException("The custom rule file is larger than 8 MB.");
        var text = await ReadTextLimitedAsync(path, MaximumCustomRulesBytes, CancellationToken.None).ConfigureAwait(false);
        await SaveConfigurationCoreAsync(GetConfigurationSnapshot(), text, allowUnavailableReplacement: true).ConfigureAwait(false);
    }

    public async Task ExportCustomRulesAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        await WriteTextAtomicAsync(path, _customRules, CancellationToken.None).ConfigureAwait(false);
    }

    // Compatibility aliases for the existing settings backup buttons.
    public Task ImportAsync(string path) => ImportCustomRulesAsync(path);
    public Task ExportAsync(string path) => ExportCustomRulesAsync(path);

    private async Task<IReadOnlyList<AdBlockUpdateResult>> UpdateSelectedAsync(IEnumerable<string>? subscriptionIds, bool automatic, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AdBlockUpdateResult> results;
        var rulesChanged = false;
        try
        {
            EnsureLoaded();
            // A full user-requested refresh also satisfies the current
            // automatic interval; avoid repeating the same requests minutes later.
            if (automatic || subscriptionIds is null) _configuration.LastAutomaticUpdateCheckUtc = DateTime.UtcNow;
            var update = await UpdateCoreAsync(subscriptionIds, cancellationToken).ConfigureAwait(false);
            results = update.Results;
            rulesChanged = update.RulesChanged;
            await SaveConfigurationFileAsync(_configuration).ConfigureAwait(false);
            if (rulesChanged) RebuildEngine();
        }
        finally { _gate.Release(); }

        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        if (rulesChanged) RulesChanged?.Invoke(this, EventArgs.Empty);
        return results;
    }

    private async Task<UpdateBatch> UpdateCoreAsync(IEnumerable<string>? subscriptionIds, CancellationToken cancellationToken)
    {
        HashSet<string>? selected = subscriptionIds is null ? null : new HashSet<string>(subscriptionIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<AdBlockUpdateResult>();
        var rulesChanged = false;
        foreach (var subscription in _configuration.Subscriptions)
        {
            if (!subscription.Enabled || (selected is not null && !selected.Contains(subscription.Id))) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var result = await UpdateOneAsync(subscription, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Outcome == AdBlockUpdateOutcome.Updated) rulesChanged = true;
        }
        return new UpdateBatch(results, rulesChanged);
    }

    private async Task<AdBlockUpdateResult> UpdateOneAsync(AdBlockSubscription subscription, CancellationToken cancellationToken)
    {
        var result = new AdBlockUpdateResult { SubscriptionId = subscription.Id, SubscriptionName = subscription.Name };
        subscription.LastAttemptUtc = DateTime.UtcNow;
        try
        {
            var uri = ValidateSubscriptionUrl(subscription.Url);
            var cachePath = CachePath(subscription);
            var hadCache = HasUsableCache(cachePath);
            using var response = await SendWithCacheRecoveryAsync(uri, subscription, hadCache, cachePath, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!HasUsableCache(cachePath)) throw new InvalidDataException("The server returned Not Modified, but no cached filter list exists.");
                subscription.LastError = string.Empty;
                result.Outcome = AdBlockUpdateOutcome.NotModified;
                result.RuleCount = subscription.RuleCount;
                return result;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared > MaximumSubscriptionBytes)
                throw new InvalidDataException("The filter subscription is larger than 20 MB.");
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The subscription URL returned an HTML page instead of filter rules.");

            var content = await ReadResponseTextLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            if (content.IndexOf('\0') >= 0) throw new InvalidDataException("The filter subscription is not a text file.");
            if (!LooksLikeFilterDocument(content)) throw new InvalidDataException("The downloaded file does not look like an ad-block filter subscription.");
            var parsed = new AdBlockRuleEngine(SplitLines(content));
            if (parsed.Statistics.TotalActiveRules == 0) throw new InvalidDataException("The downloaded subscription contains no supported active filter rules.");
            var count = AdBlockRuleEngine.CountFilterRules(content);
            await WriteTextAtomicAsync(cachePath, NormalizeNewlines(content), cancellationToken).ConfigureAwait(false);

            subscription.RuleCount = count;
            subscription.LastUpdatedUtc = DateTime.UtcNow;
            subscription.LastError = string.Empty;
            subscription.ETag = response.Headers.ETag?.ToString() ?? string.Empty;
            subscription.LastModified = response.Content.Headers.LastModified;
            result.Outcome = AdBlockUpdateOutcome.Updated;
            result.RuleCount = count;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            subscription.LastError = "The update timed out.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            subscription.LastError = ex.Message;
        }
        result.Outcome = AdBlockUpdateOutcome.Failed;
        result.RuleCount = subscription.RuleCount;
        result.Error = subscription.LastError;
        return result;
    }

    private async Task<HttpResponseMessage> SendWithCacheRecoveryAsync(Uri uri, AdBlockSubscription subscription, bool conditional, string cachePath, CancellationToken cancellationToken)
    {
        var response = await SendSubscriptionRequestAsync(uri, subscription, conditional, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotModified || HasUsableCache(cachePath)) return response;
        response.Dispose();
        return await SendSubscriptionRequestAsync(uri, subscription, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendSubscriptionRequestAsync(Uri initialUri, AdBlockSubscription subscription, bool conditional, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("text/plain, text/*;q=0.9, */*;q=0.1");
            if (conditional && !string.IsNullOrWhiteSpace(subscription.ETag)) request.Headers.TryAddWithoutValidation("If-None-Match", subscription.ETag);
            if (conditional && subscription.LastModified.HasValue) request.Headers.IfModifiedSince = subscription.LastModified;
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("The subscription redirect has no destination.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Filter subscription redirects must remain on HTTPS.");
                if (redirects == 5) throw new InvalidDataException("The filter subscription redirected too many times.");
                current = next;
                continue;
            }

            var finalUri = response.RequestMessage?.RequestUri ?? current;
            if (finalUri.Scheme != Uri.UriSchemeHttps)
            {
                response.Dispose();
                throw new InvalidDataException("The final filter subscription URL must use HTTPS.");
            }
            return response;
        }
        throw new InvalidDataException("The filter subscription redirected too many times.");
    }

    private static bool HasUsableCache(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaximumSubscriptionBytes; }
        catch { return false; }
    }

    private static bool LooksLikeFilterDocument(string content)
    {
        var trimmed = content.TrimStart();
        var sample = trimmed.Substring(0, Math.Min(trimmed.Length, 4096));
        if (sample.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || sample.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            sample.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        var hasHeader = false;
        var plausible = false;
        var examined = 0;
        foreach (var raw in SplitLines(content))
        {
            if (++examined > 20_000) break;
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("[Adblock", StringComparison.OrdinalIgnoreCase)) { hasHeader = true; continue; }
            if (line.Length == 0 || line.StartsWith("!", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("||", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("|", StringComparison.Ordinal) ||
                line.StartsWith("/", StringComparison.Ordinal) || line.Contains("##") || line.Contains("#@#") || line.IndexOf('*') >= 0 || line.IndexOf('^') >= 0 ||
                line.IndexOf('$') > 0 || line.StartsWith("0.0.0.0 ", StringComparison.Ordinal) || line.StartsWith("127.0.0.1 ", StringComparison.Ordinal) ||
                (line.IndexOfAny(new[] { ' ', '\t', '<', '>', '{', '}' }) < 0 && Uri.CheckHostName(line) != UriHostNameType.Unknown))
            {
                plausible = true;
                break;
            }
        }
        return hasHeader || plausible;
    }

    private async Task<string> ReadResponseTextLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumSubscriptionBytes) throw new InvalidDataException("The filter subscription is larger than 20 MB.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }

    private void RebuildEngine()
    {
        // User-authored and element-picker rules take priority if the global
        // safety cap is ever reached by a large set of remote subscriptions.
        _engine = new AdBlockRuleEngine(SplitLines(_customRules).Concat(EnumerateActiveRules()));
    }

    private IEnumerable<string> EnumerateActiveRules()
    {
        foreach (var subscription in _configuration.Subscriptions.Where(x => x.Enabled))
        {
            var path = CachePath(subscription);
            if (!File.Exists(path)) continue;
            try { if (new FileInfo(path).Length > MaximumSubscriptionBytes) continue; }
            catch { continue; }
            IEnumerable<string> lines;
            try { lines = File.ReadLines(path, Encoding.UTF8); }
            catch { continue; }
            using var enumerator = lines.GetEnumerator();
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    current = enumerator.Current;
                }
                catch { break; }
                yield return current;
            }
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    private static bool AutomaticUpdateIsDue(AdBlockConfiguration configuration)
    {
        if (!configuration.LastAutomaticUpdateCheckUtc.HasValue) return true;
        var interval = configuration.UpdateInterval == AdBlockUpdateInterval.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        return DateTime.UtcNow - configuration.LastAutomaticUpdateCheckUtc.Value.ToUniversalTime() >= interval;
    }

    private async void OnUpdateTimer(object? state)
    {
        if (_disposed || !_loaded) return;
        try { await UpdateDueAsync().ConfigureAwait(false); }
        catch { }
    }

    private async Task<AdBlockConfiguration> LoadConfigurationAsync()
    {
        try
        {
            if (!File.Exists(ConfigurationPath)) return new AdBlockConfiguration();
            if (new FileInfo(ConfigurationPath).Length > 2 * 1024 * 1024) return new AdBlockConfiguration();
            using var stream = File.OpenRead(ConfigurationPath);
            return await JsonSerializer.DeserializeAsync<AdBlockConfiguration>(stream, JsonOptions).ConfigureAwait(false) ?? new AdBlockConfiguration();
        }
        catch { return new AdBlockConfiguration(); }
    }

    private async Task<string> LoadInitialCustomRulesAsync()
    {
        if (File.Exists(CustomRulesPath))
        {
            try
            {
                _customRulesUnavailable = false;
                return NormalizeNewlines(await ReadTextLimitedAsync(CustomRulesPath, MaximumCustomRulesBytes, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Never replace an unreadable or oversized user file with an
                // empty one. The UI can surface this diagnostic and let the
                // user repair or export the original file.
                LastLoadError = ex.Message;
                _customRulesUnavailable = true;
                return string.Empty;
            }
        }

        // Preserve the 1.x single-file rules when this manager is first used.
        if (File.Exists(AppPaths.BlockingRules))
        {
            try
            {
                var legacy = NormalizeNewlines(await ReadTextLimitedAsync(AppPaths.BlockingRules, MaximumCustomRulesBytes, CancellationToken.None).ConfigureAwait(false));
                await WriteTextAtomicAsync(CustomRulesPath, legacy, CancellationToken.None).ConfigureAwait(false);
                _customRulesUnavailable = false;
                return legacy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                LastLoadError = ex.Message;
                _customRulesUnavailable = true;
                return string.Empty;
            }
        }

        try { await WriteTextAtomicAsync(CustomRulesPath, string.Empty, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadError = ex.Message;
            _customRulesUnavailable = true;
        }
        return string.Empty;
    }

    private static async Task<string> ReadTextLimitedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        if (stream.Length > maximumBytes) throw new InvalidDataException("The rule file is too large.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return text.TrimStart('\uFEFF');
    }

    private static async Task WriteTextAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                await writer.WriteAsync(text ?? string.Empty).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(true);
            }
            ReplaceAtomic(temporary, fullPath);
        }
        finally { TryDelete(temporary); }
    }

    private async Task SaveConfigurationFileAsync(AdBlockConfiguration configuration)
    {
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        await WriteTextAtomicAsync(ConfigurationPath, json, CancellationToken.None).ConfigureAwait(false);
    }

    private static void ReplaceAtomic(string temporary, string destination)
    {
        if (!File.Exists(destination)) { File.Move(temporary, destination); return; }
        var backup = destination + ".bak";
        TryDelete(backup);
        try
        {
            File.Replace(temporary, destination, backup, true);
            TryDelete(backup);
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(temporary, destination, true);
            File.Delete(temporary);
        }
    }

    private static void ReconcileConfiguration(AdBlockConfiguration configuration)
    {
        configuration.SchemaVersion = 1;
        configuration.Subscriptions ??= [];
        var existing = configuration.Subscriptions.Where(x => x is not null)
            .GroupBy(x => x.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToList();
        var result = new List<AdBlockSubscription>();
        foreach (var definition in BuiltIns)
        {
            var item = existing.FirstOrDefault(x => string.Equals(x.Id, definition.Id, StringComparison.OrdinalIgnoreCase)) ??
                new AdBlockSubscription { Id = definition.Id, Enabled = true };
            item.Id = definition.Id;
            item.Name = definition.Name;
            item.Url = definition.Url;
            item.IsBuiltIn = true;
            item.CacheFileName = CacheFileName(item.Id);
            result.Add(item);
        }

        var seenUrls = new HashSet<string>(result.Select(x => NormalizeUrl(x.Url)), StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing.Where(x => !BuiltIns.Any(b => string.Equals(b.Id, x.Id, StringComparison.OrdinalIgnoreCase))).Take(MaximumCustomSubscriptions))
        {
            if (!TryValidateSubscriptionUrl(item.Url, out var uri) || !seenUrls.Add(NormalizeUrl(uri!.AbsoluteUri))) continue;
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64 || item.Id.Any(ch => !char.IsLetterOrDigit(ch))) item.Id = Guid.NewGuid().ToString("N");
            item.Name = string.IsNullOrWhiteSpace(item.Name) ? uri.Host : item.Name.Trim();
            if (item.Name.Length > 128) item.Name = item.Name.Substring(0, 128);
            item.Url = uri.AbsoluteUri;
            item.IsBuiltIn = false;
            item.CacheFileName = CacheFileName(item.Id);
            result.Add(item);
        }
        configuration.Subscriptions = result;
    }

    private static void RefreshCachedRuleCounts(AdBlockConfiguration configuration)
    {
        foreach (var subscription in configuration.Subscriptions)
        {
            var path = CachePath(subscription);
            if (!HasUsableCache(path)) { subscription.RuleCount = 0; continue; }
            try
            {
                var count = 0;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    var value = line.Trim().TrimStart('\uFEFF');
                    if (value.Length > 0 && !value.StartsWith("!", StringComparison.Ordinal) && !value.StartsWith("[", StringComparison.Ordinal) &&
                        (!value.StartsWith("#", StringComparison.Ordinal) || value.StartsWith("##", StringComparison.Ordinal) || value.StartsWith("#@#", StringComparison.Ordinal))) count++;
                }
                subscription.RuleCount = count;
            }
            catch { subscription.RuleCount = 0; }
        }
    }

    private static Uri ValidateSubscriptionUrl(string url)
    {
        if (!TryValidateSubscriptionUrl(url, out var uri)) throw new ArgumentException("Filter subscription URLs must use HTTPS.", nameof(url));
        return uri!;
    }

    private static bool TryValidateSubscriptionUrl(string? url, out Uri? uri)
    {
        var value = (url ?? string.Empty).Trim();
        uri = null;
        return value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static bool HasNonemptyUnreadCustomRulesFile()
    {
        try
        {
            if (File.Exists(CustomRulesPath)) return new FileInfo(CustomRulesPath).Length > 0;
            return File.Exists(AppPaths.BlockingRules) && new FileInfo(AppPaths.BlockingRules).Length > 0;
        }
        catch { return true; }
    }

    private static bool UriEquals(string left, string right) => NormalizeUrl(left).Equals(NormalizeUrl(right), StringComparison.OrdinalIgnoreCase);
    private static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');
    private static string CacheFileName(string id) => "subscription-" + new string(id.Where(char.IsLetterOrDigit).Take(64).ToArray()).ToLowerInvariant() + ".txt";
    private static string CachePath(AdBlockSubscription subscription) => Path.Combine(ListsDirectory, CacheFileName(subscription.Id));
    private static string NormalizeNewlines(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private static void ValidateCustomRules(string? text)
    {
        if (Utf8WithoutBom.GetByteCount(text ?? string.Empty) > MaximumCustomRulesBytes)
            throw new InvalidDataException("Custom filter rules cannot be larger than 8 MB.");
    }

    private static void ValidateElementSelector(string selector)
    {
        if (selector.Length == 0 || selector.Length > 2048 || selector.IndexOfAny(new[] { '{', '}', '\0', '\r', '\n' }) >= 0 ||
            selector.Contains("/*") || selector.Contains("*/") || selector.StartsWith("+js(", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected element did not produce a safe CSS selector.", nameof(selector));
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void EnsureLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("LoadAsync must be called before using the ad-block manager.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdBlockManager));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updateTimer?.Dispose();
        _updateTimer = null;
        _gate.Dispose();
        if (_ownsClient) _client.Dispose();
    }

    private static string DataDirectory => Path.Combine(AppPaths.Root, "adblock");
    private static string ListsDirectory => Path.Combine(DataDirectory, "lists");
    private static string ConfigurationPath => Path.Combine(DataDirectory, "subscriptions.json");
    private static string CustomRulesPath => Path.Combine(DataDirectory, "custom-rules.txt");

    private sealed class BuiltInDefinition(string id, string name, string url)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Url { get; } = url;
    }

    private sealed class UpdateBatch(IReadOnlyList<AdBlockUpdateResult> results, bool rulesChanged)
    {
        public IReadOnlyList<AdBlockUpdateResult> Results { get; } = results;
        public bool RulesChanged { get; } = rulesChanged;
    }
}
