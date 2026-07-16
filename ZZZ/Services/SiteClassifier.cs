using System.Globalization;
using System.Net;
using System.Reflection;

namespace ZZZ.Services;

/// <summary>
/// Resolves schemeful sites with the complete publicsuffix.org list, including
/// private suffixes such as github.io. If the embedded list cannot be loaded,
/// the conservative fallback treats each full host as a separate site.
/// </summary>
public static class SiteClassifier
{
    private static readonly Lazy<PublicSuffixRules> Rules = new(PublicSuffixRules.Load);

    public static bool IsThirdParty(string topUrl, string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) || !IsWeb(request)) return false;
        if (!Uri.TryCreate(topUrl, UriKind.Absolute, out var top) || !IsWeb(top)) return true;
        return !SiteKey(top).Equals(SiteKey(request), StringComparison.OrdinalIgnoreCase);
    }

    public static string RegistrableDomain(string host) => Rules.Value.GetRegistrableDomain(NormalizeHost(host));

    private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    private static string SiteKey(Uri uri) => uri.Scheme.ToLowerInvariant() + "://" + RegistrableDomain(uri.Host);

    private static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out _)) return host;
        try { return new IdnMapping().GetAscii(host); }
        catch { return host; }
    }

    private sealed class PublicSuffixRules
    {
        private readonly HashSet<string> _exact;
        private readonly HashSet<string> _wildcards;
        private readonly HashSet<string> _exceptions;

        private PublicSuffixRules(HashSet<string> exact, HashSet<string> wildcards, HashSet<string> exceptions)
        {
            _exact = exact;
            _wildcards = wildcards;
            _exceptions = exceptions;
        }

        public static PublicSuffixRules Load()
        {
            var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wildcards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZZZ.Resources.public_suffix_list.dat")
                    ?? throw new InvalidOperationException("Embedded Public Suffix List is unavailable.");
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                    try { line = new IdnMapping().GetAscii(line); } catch { }
                    if (line[0] == '!') exceptions.Add(line.Substring(1));
                    else if (line.StartsWith("*.", StringComparison.Ordinal)) wildcards.Add(line.Substring(2));
                    else exact.Add(line);
                }
            }
            catch
            {
                // Empty rules intentionally produce the privacy-safe full-host fallback.
            }
            return new PublicSuffixRules(exact, wildcards, exceptions);
        }

        public string GetRegistrableDomain(string host)
        {
            if (host.Length == 0 || IPAddress.TryParse(host, out _)) return host;
            var labels = host.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length < 2) return host;
            if (_exact.Count == 0) return host;

            var publicSuffixLength = 1; // Prevailing PSL rule: "*".
            for (var index = 0; index < labels.Length; index++)
            {
                var candidate = string.Join(".", labels.Skip(index));
                if (_exceptions.Contains(candidate))
                {
                    publicSuffixLength = Math.Max(1, labels.Length - index - 1);
                    break;
                }
                if (_exact.Contains(candidate)) publicSuffixLength = Math.Max(publicSuffixLength, labels.Length - index);
                if (index < labels.Length - 1)
                {
                    var wildcardSuffix = string.Join(".", labels.Skip(index + 1));
                    if (_wildcards.Contains(wildcardSuffix)) publicSuffixLength = Math.Max(publicSuffixLength, labels.Length - index);
                }
            }

            if (labels.Length <= publicSuffixLength) return host;
            return string.Join(".", labels.Skip(labels.Length - publicSuffixLength - 1));
        }
    }
}
