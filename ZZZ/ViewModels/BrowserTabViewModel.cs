using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using ZZZ.Models;
using ZZZ.Services;
using ZZZ.Configuration;

namespace ZZZ.ViewModels;

public partial class BrowserTabViewModel : ObservableObject
{
    private readonly AppServices _services;
    private WebView2? _view;
    [ObservableProperty] private string id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string title = "New tab";
    [ObservableProperty] private string url;
    [ObservableProperty] private string address;
    [ObservableProperty] private string siteIdentity = "Web";
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isSleeping;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private DateTime lastActiveUtc = DateTime.UtcNow;
    [ObservableProperty] private int startPageRevision;
    public ObservableCollection<MediaResource> MediaResources { get; } = [];
    public bool HasMedia => MediaResources.Count > 0;
    public bool IsStartPage => BrowserHome.IsStartPage(Url);
    public bool IsPrivate { get; }

    public BrowserTabViewModel(AppServices services, string url, bool isPrivate = false)
    {
        _services = services;
        IsPrivate = isPrivate;
        title = LocalizationService.Text(isPrivate ? "PrivateTab" : "NewTab");
        this.url = url;
        address = url;
        siteIdentity = BrowserHome.IsStartPage(url) ? LocalizationService.Text("StartPage") : IdentifySite(url);
    }

    public void Attach(WebView2 view)
    {
        _view = view;
        IsSleeping = false;
    }

    public void Detach() => _view = null;
    public void Activate() => LastActiveUtc = DateTime.UtcNow;

    public void BeginNavigation(string target)
    {
        IsLoading = true;
        Address = target;
        Url = target;
        Status = string.Empty;
        App.Current.Dispatcher.Invoke(() =>
        {
            MediaResources.Clear();
            OnPropertyChanged(nameof(HasMedia));
        });
    }

    public void NavigateAddress()
    {
        var input = Address.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;
        var target = ToUrl(input);
        Url = target;
        if (_view?.CoreWebView2 is not null) _view.CoreWebView2.Navigate(target);
    }

    private string ToUrl(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "file")) return uri.AbsoluteUri;
        if (!input.Contains(' ') && input.Contains('.') && Uri.TryCreate("https://" + input, UriKind.Absolute, out uri)) return uri.AbsoluteUri;
        var engine = _services.Settings.Current.SearchEngines.FirstOrDefault(x => x.Id == _services.Settings.Current.ActiveSearchEngineId) ?? _services.Settings.Current.SearchEngines.First();
        return engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(input));
    }

    public void AddMedia(string url, string kind, string mimeType = "", long? contentLength = null)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var existing = MediaResources.FirstOrDefault(x => x.Url == url);
            if (existing is not null)
            {
                var index = MediaResources.IndexOf(existing);
                MediaResources[index] = new MediaResource
                {
                    Url = url,
                    Kind = string.IsNullOrWhiteSpace(kind) ? existing.Kind : kind,
                    MimeType = string.IsNullOrWhiteSpace(mimeType) ? existing.MimeType : mimeType,
                    ContentLength = contentLength ?? existing.ContentLength
                };
                return;
            }
            if (MediaResources.Count >= 300) MediaResources.RemoveAt(0);
            MediaResources.Add(new MediaResource { Url = url, Kind = kind, MimeType = mimeType, ContentLength = contentLength });
            OnPropertyChanged(nameof(HasMedia));
        });
    }

    partial void OnUrlChanged(string value)
    {
        SiteIdentity = BrowserHome.IsStartPage(value) ? LocalizationService.Text("StartPage") : IdentifySite(value);
        OnPropertyChanged(nameof(IsStartPage));
    }

    public void NavigateText(string text)
    {
        Address = text;
        NavigateAddress();
    }

    public void RefreshStartPage() => StartPageRevision++;

    private static string IdentifySite(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "Web";
        if (uri.IsFile) return "Local";
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host.Substring(4);
        if (host == "google.com" || host.EndsWith(".google.com")) return "Google";
        if (host == "bing.com" || host.EndsWith(".bing.com")) return "Bing";
        if (host == "baidu.com" || host.EndsWith(".baidu.com")) return "Baidu";
        if (host == "duckduckgo.com" || host.EndsWith(".duckduckgo.com")) return "DuckDuckGo";
        if (host.Length <= 22) return host;
        return host.Substring(0, 19) + "…";
    }

    public async Task<byte[]?> CaptureVisiblePageAsync()
    {
        if (_view?.CoreWebView2 is not { } core) return null;
        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            return stream.ToArray();
        }
        catch
        {
            Status = LocalizationService.Text("CaptureFailed");
            return null;
        }
    }

    [RelayCommand] private void Back() { if (_view?.CanGoBack == true) _view.GoBack(); }
    [RelayCommand] private void Forward() { if (_view?.CanGoForward == true) _view.GoForward(); }
    [RelayCommand] private void Reload() { if (_view?.CoreWebView2 is not null) _view.Reload(); }
    [RelayCommand] private void Stop() { _view?.CoreWebView2?.Stop(); }
    [RelayCommand] private void Home()
    {
        Address = BrowserHome.GetHomeUrl(_services.Settings.Current);
        NavigateAddress();
    }
    [RelayCommand] private async Task TranslateAsync()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var source) || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps)) return;
        var browser = _services.Settings.Current.Browser;
        var targetLanguage = string.IsNullOrWhiteSpace(browser.TranslationTargetLanguage) ? "zh-CN" : browser.TranslationTargetLanguage.Trim();
        if (browser.TranslationProvider == TranslationProvider.Google)
        {
            Address = $"https://translate.google.com/translate?sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&u={Uri.EscapeDataString(source.AbsoluteUri)}";
            NavigateAddress();
            return;
        }
        if (_view?.CoreWebView2 is not { } core) return;
        try
        {
            Status = LocalizationService.Text("Translating");
            var count = await _services.Translation.TranslatePageAsync(core, targetLanguage);
            Status = count > 0 ? LocalizationService.Text("TranslationComplete") : LocalizationService.Text("NothingToTranslate");
        }
        catch { Status = LocalizationService.Text("TranslationFailed"); }
    }
    [RelayCommand] private void OpenDevTools() { if (_services.Settings.Current.Advanced.EnableDeveloperTools) _view?.CoreWebView2?.OpenDevToolsWindow(); }
}
