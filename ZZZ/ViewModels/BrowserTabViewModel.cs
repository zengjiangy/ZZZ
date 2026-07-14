using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using ZZZ.Models;
using ZZZ.Services;
using ZZZ.Configuration;
using Microsoft.Win32;
using System.Text.Json;

namespace ZZZ.ViewModels;

public partial class BrowserTabViewModel : ObservableObject
{
    private readonly AppServices _services;
    private WebView2? _view;
    private string _googleTranslationOriginalUrl = string.Empty;
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
            if (IsGoogleTranslationPage(Url) && !string.IsNullOrWhiteSpace(_googleTranslationOriginalUrl))
            {
                Address = _googleTranslationOriginalUrl;
                _googleTranslationOriginalUrl = string.Empty;
                NavigateAddress();
                Status = LocalizationService.Text("OriginalRestored");
                return;
            }
            _googleTranslationOriginalUrl = source.AbsoluteUri;
            Address = $"https://translate.google.com/translate?sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&u={Uri.EscapeDataString(source.AbsoluteUri)}";
            NavigateAddress();
            return;
        }
        if (_view?.CoreWebView2 is not { } core) return;
        try
        {
            if (await _services.Translation.IsPageTranslatedAsync(core))
            {
                var restored = await _services.Translation.RestorePageAsync(core);
                Status = restored > 0 ? LocalizationService.Text("OriginalRestored") : LocalizationService.Text("NothingToTranslate");
                return;
            }
            Status = LocalizationService.Text("Translating");
            var count = await _services.Translation.TranslatePageAsync(core, targetLanguage);
            Status = count > 0 ? LocalizationService.Text("TranslationComplete") : LocalizationService.Text("NothingToTranslate");
        }
        catch { Status = LocalizationService.Text("TranslationFailed"); }
    }
    private static bool IsGoogleTranslationPage(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("translate.google.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".translate.goog", StringComparison.OrdinalIgnoreCase));
    [RelayCommand] private void OpenDevTools() { if (_services.Settings.Current.Advanced.EnableDeveloperTools) _view?.CoreWebView2?.OpenDevToolsWindow(); }

    public void Print() => _view?.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.System);

    public async Task SaveAsPdfAsync()
    {
        if (_view?.CoreWebView2 is not { } core) return;
        var dialog = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = SafeFileName(Title) + ".pdf", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        Status = LocalizationService.Text("SavingPage");
        try
        {
            var success = await core.PrintToPdfAsync(dialog.FileName);
            Status = success ? string.Format(LocalizationService.Text("PageSaved"), dialog.FileName) : LocalizationService.Text("SavePageFailed");
        }
        catch { Status = LocalizationService.Text("SavePageFailed"); }
    }

    public async Task SaveAsMhtAsync()
    {
        if (_view?.CoreWebView2 is not { } core) return;
        var dialog = new SaveFileDialog { Filter = "Web archive (*.mht)|*.mht", FileName = SafeFileName(Title) + ".mht", AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        Status = LocalizationService.Text("SavingPage");
        try
        {
            var response = await core.CallDevToolsProtocolMethodAsync("Page.captureSnapshot", "{\"format\":\"mhtml\"}");
            using var json = JsonDocument.Parse(response);
            var data = json.RootElement.GetProperty("data").GetString() ?? string.Empty;
            File.WriteAllText(dialog.FileName, data, new System.Text.UTF8Encoding(false));
            Status = string.Format(LocalizationService.Text("PageSaved"), dialog.FileName);
        }
        catch { Status = LocalizationService.Text("SavePageFailed"); }
    }

    public async Task FindAsync(string query, bool backwards)
    {
        if (_view?.CoreWebView2 is not { } core || string.IsNullOrWhiteSpace(query)) return;
        var encoded = JsonSerializer.Serialize(query);
        await core.ExecuteScriptAsync($"window.find({encoded}, false, {(backwards ? "true" : "false")}, true, false, true, false)");
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((string.IsNullOrWhiteSpace(value) ? "page" : value).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length > 80 ? safe.Substring(0, 80) : safe;
    }
}
