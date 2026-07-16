using System.Text;
using System.Text.RegularExpressions;
using ZZZ.Models;

namespace ZZZ.Services;

/// <summary>
/// A deliberately side-effect-free ABP-compatible rule snapshot. The parser
/// supports the network and cosmetic syntax that WebView2 can enforce without
/// emulating extension APIs. Unsupported transforming/scriptlet options are
/// ignored instead of being applied too broadly.
/// </summary>
public sealed class AdBlockRuleEngine
{
    private const int MaximumRules = 500_000;
    private const int MaximumCosmeticSelectorsPerPage = 20_000;
    private const int MaximumCosmeticCssLength = 2 * 1024 * 1024;
    private readonly NetworkRuleIndex _blocking = new();
    private readonly NetworkRuleIndex _exceptions = new();
    private readonly List<CosmeticRule> _cosmetic = [];
    private readonly List<CosmeticRule> _cosmeticExceptions = [];

    public static AdBlockRuleEngine Empty { get; } = new(Array.Empty<string>());
    public AdBlockRuleStatistics Statistics { get; } = new();

    public AdBlockRuleEngine(IEnumerable<string> rules)
    {
        if (rules is null) throw new ArgumentNullException(nameof(rules));
        var lines = rules.Take(MaximumRules).Select(NormalizeLine).Where(x => x.Length > 0).ToArray();
        var disabled = CollectBadFilters(lines);
        var id = 0;

        foreach (var original in lines)
        {
            if (original.Length > 8192)
            {
                Statistics.IgnoredRules++;
                continue;
            }
            var line = NormalizeHostsEntry(original);
            if (IsMetadata(line)) continue;
            if (TryGetBadFilterCanonical(line, out _)) continue;
            if (disabled.Contains(Canonicalize(line))) continue;

            if (TryParseCosmetic(line, out var cosmetic, out var cosmeticException))
            {
                if (cosmetic is null)
                {
                    Statistics.IgnoredRules++;
                    continue;
                }
                (cosmeticException ? _cosmeticExceptions : _cosmetic).Add(cosmetic!);
                if (cosmeticException) Statistics.CosmeticExceptionRules++;
                else Statistics.CosmeticRules++;
                continue;
            }

            var parsed = NetworkRule.TryParse(++id, line);
            if (parsed is null)
            {
                Statistics.IgnoredRules++;
                continue;
            }

            if (parsed.IsException)
            {
                _exceptions.Add(parsed);
                Statistics.NetworkExceptionRules++;
            }
            else
            {
                _blocking.Add(parsed);
                Statistics.NetworkBlockingRules++;
            }
        }
    }

    public bool ShouldBlock(AdBlockRequestContext request)
    {
        if (request is null || !IsHttpUrl(request.Url)) return false;
        var exceptionMatches = _exceptions.Matching(request);
        var blockingMatches = _blocking.Matching(request);

        // uBO/ABP important blocking rules bypass allow-list rules. The
        // modifier is not valid on exception rules, which the parser rejects.
        var importantBlock = blockingMatches.Any(x => x.Important);
        if (importantBlock) return true;
        if (exceptionMatches.Count > 0) return false;
        return blockingMatches.Count > 0;
    }

    public IReadOnlyList<string> GetCosmeticSelectors(string documentUrl)
    {
        if (!TryHost(documentUrl, out var host)) return Array.Empty<string>();
        var excepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _cosmeticExceptions)
            if (rule.AppliesTo(host)) excepted.Add(rule.Selector);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _cosmetic)
        {
            if (result.Count >= MaximumCosmeticSelectorsPerPage) break;
            if (!rule.AppliesTo(host) || excepted.Contains(rule.Selector) || !seen.Add(rule.Selector)) continue;
            result.Add(rule.Selector);
        }
        return result;
    }

    public string GetCosmeticCss(string documentUrl)
    {
        var css = new StringBuilder();
        foreach (var selector in GetCosmeticSelectors(documentUrl))
        {
            if (css.Length + selector.Length + 34 > MaximumCosmeticCssLength) break;
            css.Append(selector).Append("{display:none!important;}\n");
        }
        return css.ToString();
    }

    public static int CountFilterRules(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = NormalizeLine(line);
            if (!IsMetadata(line)) count++;
        }
        return count;
    }

    private static HashSet<string> CollectBadFilters(IEnumerable<string> lines)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
            if (TryGetBadFilterCanonical(line, out var canonical)) result.Add(canonical);
        return result;
    }

    private static bool TryGetBadFilterCanonical(string line, out string canonical)
    {
        canonical = string.Empty;
        var optionsAt = FindOptionsSeparator(line);
        if (optionsAt < 0) return false;
        var options = line.Substring(optionsAt + 1).Split(',').Select(x => x.Trim()).ToList();
        var removed = options.RemoveAll(x => x.Equals("badfilter", StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        canonical = Canonicalize(line.Substring(0, optionsAt) + (options.Count == 0 ? string.Empty : "$" + string.Join(",", options)));
        return true;
    }

    private static string Canonicalize(string value)
    {
        value = value.Trim();
        var optionsAt = FindOptionsSeparator(value);
        if (optionsAt < 0) return value.ToLowerInvariant();
        var pattern = value.Substring(0, optionsAt);
        var options = value.Substring(optionsAt + 1).Split(',').Select(x => x.Trim()).ToArray();
        var matchCase = options.Any(x => x.Equals("match-case", StringComparison.OrdinalIgnoreCase));
        if (!matchCase) pattern = pattern.ToLowerInvariant();
        return pattern + "$" + string.Join(",", options.Select(x => x.ToLowerInvariant()));
    }

    private static string NormalizeLine(string? value) => (value ?? string.Empty).Trim().TrimStart('\uFEFF');

    private static bool IsMetadata(string line) => line.Length == 0 || line.StartsWith("!", StringComparison.Ordinal) ||
        line.StartsWith("[", StringComparison.Ordinal) ||
        (line.StartsWith("#", StringComparison.Ordinal) && !line.StartsWith("##", StringComparison.Ordinal) && !line.StartsWith("#@#", StringComparison.Ordinal));

    private static string NormalizeHostsEntry(string line)
    {
        var match = Regex.Match(line, @"^(?:0\.0\.0\.0|127\.0\.0\.1|::1)\s+([^\s#]+)", RegexOptions.CultureInvariant);
        if (!match.Success) return line;
        var host = match.Groups[1].Value.Trim().TrimEnd('.');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "!" : "||" + host + "^";
    }

    private static bool TryParseCosmetic(string line, out CosmeticRule? rule, out bool isException)
    {
        rule = null;
        isException = false;
        var delimiter = "#@#";
        var at = line.IndexOf(delimiter, StringComparison.Ordinal);
        if (at >= 0) isException = true;
        else
        {
            delimiter = "##";
            at = line.IndexOf(delimiter, StringComparison.Ordinal);
        }
        if (at < 0) return false;

        var selector = line.Substring(at + delimiter.Length).Trim();
        // Scriptlets, procedural selectors and CSS-injection rules require a
        // full extension runtime. Remote lists are untrusted, so only inert CSS
        // selectors are admitted to the generated style element.
        if (!IsSafeSelector(selector)) return true;
        var domainText = line.Substring(0, at);
        var domains = domainText.Length == 0 ? Array.Empty<string>() : domainText.Split(',');
        var include = new List<string>();
        var exclude = new List<string>();
        foreach (var raw in domains)
        {
            var domain = raw.Trim();
            if (domain.Length == 0) return true;
            var target = include;
            if (domain[0] == '~') { target = exclude; domain = domain.Substring(1); }
            if (!TryNormalizeDomainConstraint(domain, out domain)) return true;
            target.Add(domain);
        }
        rule = new CosmeticRule(selector, include, exclude);
        return true;
    }

    private static bool IsSafeSelector(string selector)
    {
        if (selector.Length == 0 || selector.Length > 2048) return false;
        if (selector.IndexOfAny(new[] { '{', '}', '\0', '\r', '\n' }) >= 0) return false;
        if (selector.Contains("/*") || selector.Contains("*/") || selector.StartsWith("+js(", StringComparison.OrdinalIgnoreCase) ||
            selector.IndexOf(":-abp-", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    private static bool TryHost(string url, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) return false;
        host = NormalizeDomain(uri.IdnHost);
        return host.Length > 0;
    }

    private static string NormalizeDomain(string domain) => domain.Trim().Trim('.').ToLowerInvariant();

    private static bool TryNormalizeDomainConstraint(string value, out string domain)
    {
        domain = string.Empty;
        value = value.Trim();
        if (value.Length == 0 || value.IndexOf('*') >= 0 || value.StartsWith(".", StringComparison.Ordinal) || value.EndsWith(".", StringComparison.Ordinal)) return false;
        try
        {
            domain = new System.Globalization.IdnMapping().GetAscii(value).ToLowerInvariant();
            if (domain.Length == 0 || Uri.CheckHostName(domain) == UriHostNameType.Unknown) { domain = string.Empty; return false; }
            return true;
        }
        catch (ArgumentException) { domain = string.Empty; return false; }
    }

    private static bool DomainMatches(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static int FindOptionsSeparator(string line)
    {
        var at = line.LastIndexOf('$');
        if (at <= 0 || at == line.Length - 1) return -1;
        if (line[0] == '/')
        {
            var closingSlash = line.LastIndexOf('/', at - 1);
            if (closingSlash < 1 || at < closingSlash) return -1;
        }
        var suffix = line.Substring(at + 1);
        return suffix.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '~' or '=' or '|' or ',' or '.' or '*') ? at : -1;
    }

    private sealed class CosmeticRule
    {
        public CosmeticRule(string selector, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
        {
            Selector = selector; Include = include; Exclude = exclude;
        }
        public string Selector { get; }
        private IReadOnlyList<string> Include { get; }
        private IReadOnlyList<string> Exclude { get; }
        public bool AppliesTo(string host) => !Exclude.Any(x => DomainMatches(host, x)) &&
            (Include.Count == 0 || Include.Any(x => DomainMatches(host, x)));
    }

    private sealed class NetworkRule
    {
        private static readonly Dictionary<string, AdBlockResourceType> ResourceTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["document"] = AdBlockResourceType.Document,
            ["doc"] = AdBlockResourceType.Document,
            ["subdocument"] = AdBlockResourceType.Subdocument,
            ["frame"] = AdBlockResourceType.Subdocument,
            ["script"] = AdBlockResourceType.Script,
            ["stylesheet"] = AdBlockResourceType.Stylesheet,
            ["css"] = AdBlockResourceType.Stylesheet,
            ["image"] = AdBlockResourceType.Image,
            ["font"] = AdBlockResourceType.Font,
            ["media"] = AdBlockResourceType.Media,
            ["object"] = AdBlockResourceType.Object,
            ["object-subrequest"] = AdBlockResourceType.Object,
            ["xmlhttprequest"] = AdBlockResourceType.XmlHttpRequest,
            ["xhr"] = AdBlockResourceType.XmlHttpRequest,
            ["websocket"] = AdBlockResourceType.WebSocket,
            ["ping"] = AdBlockResourceType.Ping,
            ["other"] = AdBlockResourceType.Other
        };

        private NetworkRule(int id, bool isException, Regex regex, string? indexKey)
        {
            Id = id; IsException = isException; Regex = regex; IndexKey = indexKey;
        }

        public int Id { get; }
        public bool IsException { get; }
        public bool Important { get; private set; }
        public string? IndexKey { get; }
        private Regex Regex { get; }
        private bool? ThirdParty { get; set; }
        private HashSet<AdBlockResourceType> IncludedTypes { get; } = [];
        private HashSet<AdBlockResourceType> ExcludedTypes { get; } = [];
        private List<string> IncludedDomains { get; } = [];
        private List<string> ExcludedDomains { get; } = [];
        private HashSet<string> IncludedMethods { get; } = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> ExcludedMethods { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static NetworkRule? TryParse(int id, string value)
        {
            var line = value;
            var exception = line.StartsWith("@@", StringComparison.Ordinal);
            if (exception) line = line.Substring(2);
            var optionsAt = FindOptionsSeparator(line);
            var optionText = optionsAt >= 0 ? line.Substring(optionsAt + 1) : string.Empty;
            var pattern = optionsAt >= 0 ? line.Substring(0, optionsAt) : line;
            if (pattern.Length == 0 || pattern.Contains("##") || pattern.Contains("#@#") || pattern.Contains("#?#") ||
                pattern.Contains("#$#") || pattern.Contains("#%#")) return null;

            var matchCase = false;
            var pending = new List<Action<NetworkRule>>();
            foreach (var rawOption in optionText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var raw = rawOption.Trim();
                var negated = raw.StartsWith("~", StringComparison.Ordinal);
                var option = negated ? raw.Substring(1) : raw;
                var equalsAt = option.IndexOf('=');
                var name = (equalsAt >= 0 ? option.Substring(0, equalsAt) : option).ToLowerInvariant();
                var argument = equalsAt >= 0 ? option.Substring(equalsAt + 1) : string.Empty;

                if (ResourceTypes.TryGetValue(name, out var resourceType))
                {
                    pending.Add(rule => (negated ? rule.ExcludedTypes : rule.IncludedTypes).Add(resourceType));
                    continue;
                }
                switch (name)
                {
                    case "third-party":
                    case "3p": pending.Add(rule => rule.ThirdParty = !negated); break;
                    case "first-party":
                    case "1p": pending.Add(rule => rule.ThirdParty = negated); break;
                    case "match-case": matchCase = !negated; break;
                    case "important":
                        if (negated || exception) return null;
                        pending.Add(rule => rule.Important = true);
                        break;
                    case "domain":
                    case "from":
                        if (negated || argument.Length == 0) return null;
                        if (!TryParseDomains(argument, out var includedDomains, out var excludedDomains)) return null;
                        pending.Add(rule =>
                        {
                            foreach (var domain in includedDomains) rule.IncludedDomains.Add(domain);
                            foreach (var domain in excludedDomains) rule.ExcludedDomains.Add(domain);
                        });
                        break;
                    case "method":
                        if (argument.Length == 0) return null;
                        pending.Add(rule => AddMethods(argument, negated, rule.IncludedMethods, rule.ExcludedMethods));
                        break;
                    case "all":
                        break;
                    // These modifiers need response rewriting, CSP injection,
                    // popup lifecycle control, or an extension scriptlet engine.
                    // Skipping the entire rule is safer than overblocking.
                    case "badfilter":
                    case "redirect":
                    case "redirect-rule":
                    case "rewrite":
                    case "csp":
                    case "removeparam":
                    case "header":
                    case "replace":
                    case "urltransform":
                    case "uritransform":
                    case "permissions":
                    case "cookie":
                    case "popup":
                    case "popunder":
                    case "elemhide":
                    case "generichide":
                    case "specifichide":
                    case "genericblock":
                    case "sitekey":
                    case "denyallow":
                        return null;
                    default:
                        return null;
                }
            }

            var regex = CompilePattern(pattern, matchCase, out var rawRegex);
            if (regex is null) return null;
            var key = rawRegex ? null : ExtractIndexKey(pattern);
            var result = new NetworkRule(id, exception, regex, key);
            foreach (var apply in pending) apply(result);
            return result;
        }

        public bool Matches(AdBlockRequestContext request)
        {
            if (IncludedTypes.Count > 0 && !IncludedTypes.Contains(request.ResourceType)) return false;
            if (ExcludedTypes.Contains(request.ResourceType)) return false;
            var method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim();
            if (IncludedMethods.Count > 0 && !IncludedMethods.Contains(method)) return false;
            if (ExcludedMethods.Contains(method)) return false;

            if (IncludedDomains.Count > 0 || ExcludedDomains.Count > 0)
            {
                if (!TryHost(request.DocumentUrl, out var documentHost)) return false;
                if (ExcludedDomains.Any(x => DomainMatches(documentHost, x))) return false;
                if (IncludedDomains.Count > 0 && !IncludedDomains.Any(x => DomainMatches(documentHost, x))) return false;
            }
            if (ThirdParty.HasValue)
            {
                if (!IsHttpUrl(request.DocumentUrl)) return false;
                if (SiteClassifier.IsThirdParty(request.DocumentUrl, request.Url) != ThirdParty.Value) return false;
            }
            try { return Regex.IsMatch(request.Url); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static bool TryParseDomains(string value, out IReadOnlyList<string> include, out IReadOnlyList<string> exclude)
        {
            var included = new List<string>();
            var excluded = new List<string>();
            foreach (var raw in value.Split('|'))
            {
                var domain = raw.Trim();
                var target = included;
                if (domain.StartsWith("~", StringComparison.Ordinal)) { target = excluded; domain = domain.Substring(1); }
                if (!TryNormalizeDomainConstraint(domain, out domain)) { include = Array.Empty<string>(); exclude = Array.Empty<string>(); return false; }
                target.Add(domain);
            }
            include = included;
            exclude = excluded;
            return included.Count + excluded.Count > 0;
        }

        private static void AddMethods(string value, bool negatedOption, ICollection<string> include, ICollection<string> exclude)
        {
            foreach (var raw in value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var method = raw.Trim();
                var excluded = negatedOption;
                if (method.StartsWith("~", StringComparison.Ordinal)) { excluded = true; method = method.Substring(1); }
                if (method.Length > 0 && method.All(char.IsLetter)) (excluded ? exclude : include).Add(method);
            }
        }

        private static Regex? CompilePattern(string value, bool matchCase, out bool rawRegex)
        {
            rawRegex = value.Length > 2 && value[0] == '/' && value[value.Length - 1] == '/';
            try
            {
                var options = RegexOptions.CultureInvariant;
                if (!matchCase) options |= RegexOptions.IgnoreCase;
                if (rawRegex) return new Regex(value.Substring(1, value.Length - 2), options, TimeSpan.FromMilliseconds(75));

                var pattern = value;
                var domainAnchor = pattern.StartsWith("||", StringComparison.Ordinal);
                var startAnchor = !domainAnchor && pattern.StartsWith("|", StringComparison.Ordinal);
                var endAnchor = pattern.EndsWith("|", StringComparison.Ordinal);
                if (domainAnchor) pattern = pattern.Substring(2);
                else if (startAnchor) pattern = pattern.Substring(1);
                if (endAnchor && pattern.Length > 0) pattern = pattern.Substring(0, pattern.Length - 1);
                if (pattern.Length == 0) return null;

                pattern = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\^", "(?:[^A-Za-z0-9_.%-]|$)");
                if (domainAnchor) pattern = @"^(?:[^:/?#]+:)?//(?:[^/?#]*\.)?" + pattern;
                else if (startAnchor) pattern = "^" + pattern;
                if (endAnchor) pattern += "$";
                return new Regex(pattern, options, TimeSpan.FromMilliseconds(75));
            }
            catch (ArgumentException) { return null; }
        }

        private static string? ExtractIndexKey(string value)
        {
            var pattern = value;
            if (pattern.StartsWith("||", StringComparison.Ordinal)) pattern = pattern.Substring(2);
            else if (pattern.StartsWith("|", StringComparison.Ordinal)) pattern = pattern.Substring(1);
            if (pattern.EndsWith("|", StringComparison.Ordinal)) pattern = pattern.Substring(0, pattern.Length - 1);
            var literal = pattern.Split(new[] { '*', '^' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.Length >= 3).OrderByDescending(x => x.Length).FirstOrDefault();
            if (literal is null) return null;
            return literal.Substring(0, Math.Min(8, literal.Length)).ToLowerInvariant();
        }
    }

    private sealed class NetworkRuleIndex
    {
        private readonly Dictionary<int, Dictionary<string, List<NetworkRule>>> _byLength = [];
        private readonly List<NetworkRule> _unindexed = [];

        public void Add(NetworkRule rule)
        {
            if (string.IsNullOrEmpty(rule.IndexKey)) { _unindexed.Add(rule); return; }
            var length = rule.IndexKey!.Length;
            if (!_byLength.TryGetValue(length, out var keys)) _byLength[length] = keys = new Dictionary<string, List<NetworkRule>>(StringComparer.Ordinal);
            if (!keys.TryGetValue(rule.IndexKey, out var bucket)) keys[rule.IndexKey] = bucket = [];
            bucket.Add(rule);
        }

        public List<NetworkRule> Matching(AdBlockRequestContext request)
        {
            var candidates = new List<NetworkRule>(_unindexed);
            var seen = new HashSet<int>(_unindexed.Select(x => x.Id));
            var url = request.Url.ToLowerInvariant();
            foreach (var pair in _byLength)
            {
                var length = pair.Key;
                if (url.Length < length) continue;
                for (var index = 0; index <= url.Length - length; index++)
                {
                    if (!pair.Value.TryGetValue(url.Substring(index, length), out var bucket)) continue;
                    foreach (var rule in bucket) if (seen.Add(rule.Id)) candidates.Add(rule);
                }
            }
            return candidates.Where(x => x.Matches(request)).ToList();
        }
    }
}
