using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace ZZZ.Services;

/// <summary>
/// Keeps filter-list mistakes from replacing real playback responses. Media
/// requests are not ads merely because they are served from a third-party CDN.
/// </summary>
public static class MediaPlaybackPolicy
{
    public static bool MustAllow(CoreWebView2WebResourceContext context, string requestUrl, string documentUrl)
    {
        if (context is CoreWebView2WebResourceContext.Media or CoreWebView2WebResourceContext.TextTrack)
            return true;

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) ||
            !Uri.TryCreate(documentUrl, UriKind.Absolute, out var document))
            return false;

        // YouTube delivers Media Source Extension segments from googlevideo.com.
        // WebView2 can classify these POST requests as Fetch/Other instead of
        // Media, so protect this first-party playback relationship explicitly.
        return IsYouTube(document.Host) && IsGoogleVideo(request.Host) &&
            request.AbsolutePath.Equals("/videoplayback", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYouTube(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoogleVideo(string host) =>
        host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
}

public static class ExternalPlayerLauncher
{
    public static bool TryOpen(string playerPath, string url, out string error)
    {
        error = string.Empty;
        var executable = Environment.ExpandEnvironmentVariables((playerPath ?? string.Empty).Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = "No external player is configured.";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            error = "The selected address cannot be opened by an external player.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = Quote(target.AbsoluteUri),
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
}
