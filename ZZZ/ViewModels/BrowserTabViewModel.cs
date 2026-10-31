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
using System.Windows.Media;

namespace ZZZ.ViewModels;

public partial class BrowserTabViewModel : ObservableObject
{
    private const int ReaderModeMaxAttempts = 8;
    private static readonly TimeSpan ReaderModeRetryDelay = TimeSpan.FromMilliseconds(450);
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _readerModeCancellation;
    private TaskCompletionSource<bool>? _readerModeRetrySignal;
    private int _readerModeGeneration;
    private int _readerModeAttempts;
    private bool _readerModePending;
    private bool _readerModeRetryRequested;
    private WebView2? _view;
    private string _googleTranslationOriginalUrl = string.Empty;
    [ObservableProperty] private string id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string title = "New tab";
    [ObservableProperty] private string url;
    [ObservableProperty] private string address;
    [ObservableProperty] private string siteIdentity = "Web";
    [ObservableProperty] private ImageSource? favicon;
    public string FaviconFallback => FaviconCacheService.FallbackLetter(Title, Url);
    [ObservableProperty] private string workspaceId = "default";
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isSleeping;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private DateTime lastActiveUtc = DateTime.UtcNow;
    [ObservableProperty] private int startPageRevision;
    [ObservableProperty] private bool isReaderMode;
    public ObservableCollection<MediaResource> MediaResources { get; } = [];
    public double ZoomFactor { get; private set; } = 1;
    public bool HasMedia => MediaResources.Count > 0;
    public bool IsStartPage => BrowserHome.IsStartPage(Url);
    public bool IsPrivate { get; }
    public bool IsClosed { get; private set; }
    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public BrowserTabViewModel(AppServices services, string url, bool isPrivate = false, string? workspaceId = null)
    {
        _services = services;
        IsPrivate = isPrivate;
        this.workspaceId = string.IsNullOrWhiteSpace(workspaceId) ? "default" : workspaceId!;
        title = LocalizationService.Text(isPrivate ? "PrivateTab" : "NewTab");
        this.url = url;
        address = url;
        siteIdentity = BrowserHome.IsStartPage(url) ? LocalizationService.Text("StartPage") : IdentifySite(url);
        favicon = BrowserHome.IsStartPage(url) || isPrivate ? null : _services.Favicons.GetCached(url);
    }

    public void Attach(WebView2 view)
    {
        _view = view;
        _view.ZoomFactor = ZoomFactor;
        IsSleeping = false;
        ToggleReaderModeCommand.NotifyCanExecuteChanged();
    }

    public void Detach()
    {
        CancelReaderModeRequest();
        _view = null;
        IsReaderMode = false;
        ToggleReaderModeCommand.NotifyCanExecuteChanged();
    }

    internal void MarkClosed()
    {
        if (IsClosed) return;
        IsClosed = true;
        CancelReaderModeRequest();
        _lifetimeCancellation.Cancel();
    }
    public void Activate() => LastActiveUtc = DateTime.UtcNow;

    public void BeginNavigation(string target)
    {
        CancelReaderModeRequest();
        if (IsReaderMode && _view?.CoreWebView2 is { } core)
            _ = IgnoreScriptErrorAsync(core, ReaderDisableScript);
        IsReaderMode = false;
        IsLoading = true;
        Address = target;
        Url = target;
        Favicon = BrowserHome.IsStartPage(target) || IsPrivate ? null : _services.Favicons.GetCached(target);
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
        // The built-in start page is rendered by WPF rather than WebView2.  Setting
        // Url swaps the view immediately, so never navigate the WebView that the
        // swap just disposed.
        if (BrowserHome.IsStartPage(target))
        {
            IsLoading = false;
            Status = string.Empty;
            return;
        }
        if (_view?.CoreWebView2 is not null) _view.CoreWebView2.Navigate(target);
    }

    private string ToUrl(string input)
    {
        if (BrowserHome.IsStartPage(input)) return BrowserHome.StartPageUrl;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "file")) return uri.AbsoluteUri;
        if (!input.Contains(' ') && input.Contains('.') && Uri.TryCreate("https://" + input, UriKind.Absolute, out uri)) return uri.AbsoluteUri;
        var engine = _services.Settings.Current.SearchEngines.FirstOrDefault(x => x.Id == _services.Settings.Current.ActiveSearchEngineId) ?? _services.Settings.Current.SearchEngines.First();
        return engine.UrlTemplate.Replace("{query}", Uri.EscapeDataString(input));
    }

    public void AddMedia(string url, string kind, string mimeType = "", long? contentLength = null)
    {
        void Update()
        {
            var existing = MediaResources.FirstOrDefault(x => x.Url == url);
            if (existing is not null)
            {
                var nextKind = string.IsNullOrWhiteSpace(kind) ? existing.Kind : kind;
                var nextMimeType = string.IsNullOrWhiteSpace(mimeType) ? existing.MimeType : mimeType;
                var nextContentLength = contentLength ?? existing.ContentLength;
                if (existing.Kind == nextKind && existing.MimeType == nextMimeType && existing.ContentLength == nextContentLength) return;
                MediaResources[MediaResources.IndexOf(existing)] = new MediaResource
                {
                    Url = url,
                    Kind = nextKind,
                    MimeType = nextMimeType,
                    ContentLength = nextContentLength
                };
                return;
            }
            if (MediaResources.Count >= 300) MediaResources.RemoveAt(0);
            MediaResources.Add(new MediaResource { Url = url, Kind = kind, MimeType = mimeType, ContentLength = contentLength });
            OnPropertyChanged(nameof(HasMedia));
        }

        var dispatcher = App.Current.Dispatcher;
        if (dispatcher.CheckAccess()) Update();
        else dispatcher.BeginInvoke(new Action(Update));
    }

    partial void OnUrlChanged(string value)
    {
        CancelReaderModeRequest();
        if (IsReaderMode && _view?.CoreWebView2 is { } core)
            _ = IgnoreScriptErrorAsync(core, ReaderDisableScript);
        IsReaderMode = false;
        SiteIdentity = BrowserHome.IsStartPage(value) ? LocalizationService.Text("StartPage") : IdentifySite(value);
        if (BrowserHome.IsStartPage(value)) Favicon = null;
        OnPropertyChanged(nameof(FaviconFallback));
        OnPropertyChanged(nameof(IsStartPage));
        ToggleReaderModeCommand.NotifyCanExecuteChanged();
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(FaviconFallback));

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

    [RelayCommand(CanExecute = nameof(CanToggleReaderMode))]
    private async Task ToggleReaderModeAsync()
    {
        if (_view?.CoreWebView2 is not { } core) return;
        if (IsReaderMode || _readerModePending)
        {
            var generation = CancelReaderModeRequest();
            try
            {
                await core.ExecuteScriptAsync(ReaderDisableScript);
                if (generation != _readerModeGeneration || IsClosed || !ReferenceEquals(_view?.CoreWebView2, core)) return;
                IsReaderMode = false;
                Status = string.Empty;
            }
            catch
            {
                if (generation != _readerModeGeneration || IsClosed || !ReferenceEquals(_view?.CoreWebView2, core)) return;
                IsReaderMode = false;
                Status = LocalizationService.Text("ReaderModeUnavailable");
            }
            return;
        }

        var requestGeneration = BeginReaderModeRequest();
        await RunReaderModeRequestAsync(core, requestGeneration, _readerModeCancellation!.Token);
    }

    private int BeginReaderModeRequest()
    {
        var generation = CancelReaderModeRequest();
        _readerModeCancellation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        _readerModePending = true;
        _readerModeAttempts = 0;
        Status = string.Empty;
        return generation;
    }

    private int CancelReaderModeRequest()
    {
        _readerModeGeneration++;
        _readerModePending = false;
        _readerModeRetryRequested = false;
        _readerModeRetrySignal?.TrySetResult(true);
        _readerModeRetrySignal = null;
        _readerModeCancellation?.Cancel();
        _readerModeCancellation?.Dispose();
        _readerModeCancellation = null;
        return _readerModeGeneration;
    }

    private void FinishReaderModeRequest(int generation)
    {
        if (generation != _readerModeGeneration) return;
        _readerModePending = false;
        _readerModeRetryRequested = false;
        _readerModeRetrySignal = null;
        _readerModeCancellation?.Dispose();
        _readerModeCancellation = null;
    }

    private bool IsReaderModeRequestCurrent(CoreWebView2 core, int generation, CancellationToken cancellationToken) =>
        generation == _readerModeGeneration &&
        _readerModePending &&
        !cancellationToken.IsCancellationRequested &&
        !IsClosed &&
        ReferenceEquals(_view?.CoreWebView2, core);

    internal void RetryPendingReaderMode(CoreWebView2 core)
    {
        if (!_readerModePending || IsClosed || !ReferenceEquals(_view?.CoreWebView2, core)) return;
        _readerModeRetryRequested = true;
        _readerModeRetrySignal?.TrySetResult(true);
    }

    private async Task RunReaderModeRequestAsync(CoreWebView2 core, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (IsReaderModeRequestCurrent(core, generation, cancellationToken) && _readerModeAttempts < ReaderModeMaxAttempts)
            {
                _readerModeAttempts++;
                var result = await core.ExecuteScriptAsync(ReaderEnableScript);
                if (!IsReaderModeRequestCurrent(core, generation, cancellationToken)) return;
                if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                {
                    IsReaderMode = true;
                    Status = string.Empty;
                    FinishReaderModeRequest(generation);
                    return;
                }
                if (_readerModeAttempts >= ReaderModeMaxAttempts) break;
                if (_readerModeRetryRequested)
                {
                    _readerModeRetryRequested = false;
                    continue;
                }

                var retrySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerModeRetrySignal = retrySignal;
                await Task.WhenAny(Task.Delay(ReaderModeRetryDelay, cancellationToken), retrySignal.Task);
                if (ReferenceEquals(_readerModeRetrySignal, retrySignal)) _readerModeRetrySignal = null;
                _readerModeRetryRequested = false;
            }

            if (!IsReaderModeRequestCurrent(core, generation, cancellationToken)) return;
            IsReaderMode = false;
            Status = LocalizationService.Text("ReaderModeUnavailable");
            FinishReaderModeRequest(generation);
        }
        catch
        {
            if (!IsReaderModeRequestCurrent(core, generation, cancellationToken)) return;
            IsReaderMode = false;
            Status = LocalizationService.Text("ReaderModeUnavailable");
            FinishReaderModeRequest(generation);
        }
    }

    private bool CanToggleReaderMode() => Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static async Task IgnoreScriptErrorAsync(CoreWebView2 core, string script)
    {
        try { await core.ExecuteScriptAsync(script); }
        catch { }
    }

    private const string ReaderDisableScript = @"(() => {
document.getElementById('__zzzReaderMode')?.remove();
document.getElementById('__zzzReaderModeStyle')?.remove();
document.documentElement.classList.remove('__zzzReaderActive');
document.body?.classList.remove('__zzzReaderActive');
return true;
})();";

    private const string ReaderEnableScript = """
        (() => {
        const id='__zzzReaderMode',styleId='__zzzReaderModeStyle';
        if(document.getElementById(id))return true;
        if(!document.body)return false;
        let overlay=null;
        try{
          const textOf=node=>(node.innerText||node.textContent||'').replace(/\s+/g,' ').trim();
          const score=node=>{
            const text=textOf(node);if(text.length<180)return -1;
            let linkText=0;for(const link of node.querySelectorAll('a'))linkText+=textOf(link).length;
            let paragraphText=0,paragraphs=0;
            for(const p of node.querySelectorAll('p')){const length=textOf(p).length;if(length>=30){paragraphText+=length;paragraphs++;}}
            const punctuation=(text.match(/[。！？.!?]/g)||[]).length;
            return paragraphText+paragraphs*90+punctuation*12+Math.min(text.length,6000)*.15-linkText*1.4;
          };
          const semantic=Array.from(document.querySelectorAll('article,main,[role="main"],[itemprop="articleBody"],#article-body,#mw-content-text,.mw-parser-output,.article,.article-body,.article-content,.story-body,.post,.post-content,.entry-content'));
          const generic=Array.from(document.body.querySelectorAll('section,div')).filter(node=>textOf(node).length>=300).slice(0,300);
          const candidates=Array.from(new Set([...semantic,...generic])).slice(0,400);
          let source=null,best=0;
          for(const candidate of candidates){const value=score(candidate);if(value>best){source=candidate;best=value;}}
          if(!source)return false;

          overlay=document.createElement('main');overlay.id=id;overlay.setAttribute('role','document');
          const page=document.createElement('div');page.className='zzz-reader-page';overlay.appendChild(page);
          const heading=document.createElement('h1');
          heading.textContent=textOf(source.querySelector('h1'))||textOf(document.querySelector('article h1,main h1,h1'))||document.title||location.hostname;
          page.appendChild(heading);
          const article=document.createElement('article');article.className='zzz-reader-content';page.appendChild(article);
          const allowed=new Set(['P','H1','H2','H3','H4','H5','H6','BLOCKQUOTE','PRE','CODE','UL','OL','LI','DL','DT','DD','FIGURE','FIGCAPTION','IMG','A','STRONG','B','EM','I','BR','HR','TABLE','THEAD','TBODY','TFOOT','TR','TH','TD','CAPTION','SPAN','SMALL','SUP','SUB','U','S','MARK','TIME','ABBR','CITE','Q','KBD','SAMP','VAR','WBR']);
          const blocked=new Set(['SCRIPT','STYLE','NOSCRIPT','NAV','ASIDE','FORM','BUTTON','INPUT','TEXTAREA','SELECT','OPTION','IFRAME','OBJECT','EMBED','SVG','CANVAS','VIDEO','AUDIO','TEMPLATE']);
          let nodes=0,characters=0;
          const copy=(node,parent,depth)=>{
            if(nodes>=6000||characters>=400000||depth>100)return;
            if(node.nodeType===Node.TEXT_NODE){
              const value=(node.nodeValue||'').slice(0,Math.max(0,400000-characters));
              if(value){parent.appendChild(document.createTextNode(value));characters+=value.length;}return;
            }
            if(node.nodeType!==Node.ELEMENT_NODE)return;
            const tag=node.tagName;
            if(blocked.has(tag)||node.hidden||node.getAttribute('aria-hidden')==='true')return;
            nodes++;
            const target=document.createElement(allowed.has(tag)?tag.toLowerCase():(tag==='DIV'||tag==='SECTION'||tag==='ARTICLE'?'div':'span'));
            if(tag==='A'){
              const href=node.href||'';
              if(/^https?:/i.test(href)){target.setAttribute('href',href);target.setAttribute('target','_blank');target.setAttribute('rel','noreferrer noopener');}
            }
            if(tag==='IMG'){
              const src=node.currentSrc||node.src||'';
              if(/^https?:/i.test(src)||/^data:image\/(?:png|jpe?g|gif|webp);/i.test(src)){
                target.setAttribute('src',src);target.setAttribute('loading','lazy');target.setAttribute('decoding','async');
              }
              const alt=node.getAttribute('alt');if(alt)target.setAttribute('alt',alt);
            }
            if(tag==='TIME'&&node.getAttribute('datetime'))target.setAttribute('datetime',node.getAttribute('datetime'));
            parent.appendChild(target);
            for(const child of node.childNodes){copy(child,target,depth+1);if(nodes>=6000||characters>=400000)break;}
          };
          copy(source,article,0);
          if(characters<200){overlay.remove();return false;}
          const duplicate=article.querySelector('h1');if(duplicate&&textOf(duplicate)===textOf(heading))duplicate.remove();
          const style=document.createElement('style');style.id=styleId;
          style.textContent=`
            html.__zzzReaderActive{background:#f6f3ec!important;overflow:hidden!important;color-scheme:light!important}
            html.__zzzReaderActive>body{display:none!important}
            #__zzzReaderMode{position:fixed!important;inset:0!important;z-index:2147483647!important;display:block!important;width:100%!important;height:100%!important;margin:0!important;padding:0!important;overflow-y:auto!important;overflow-x:hidden!important;background:#f6f3ec!important;color:#29261f!important;font-family:Georgia,'Noto Serif SC','Songti SC',serif!important;font-size:19px!important;line-height:1.8!important;color-scheme:light!important}
            #__zzzReaderMode *{box-sizing:border-box!important;max-width:100%!important}
            #__zzzReaderMode .zzz-reader-page{width:100%!important;max-width:820px!important;min-height:100%!important;margin:0 auto!important;padding:52px 38px 90px!important;background:#f6f3ec!important}
            #__zzzReaderMode h1,#__zzzReaderMode h2,#__zzzReaderMode h3,#__zzzReaderMode h4{font-family:system-ui,-apple-system,'Segoe UI',sans-serif!important;line-height:1.35!important;color:#171512!important}
            #__zzzReaderMode h1{font-size:36px!important;margin:0 0 34px!important}#__zzzReaderMode h2{font-size:27px!important;margin:1.6em 0 .7em!important}#__zzzReaderMode h3{font-size:22px!important;margin:1.4em 0 .6em!important}
            #__zzzReaderMode p{margin:0 0 1.2em!important}#__zzzReaderMode span{display:inline!important}#__zzzReaderMode div{height:auto!important}
            #__zzzReaderMode a{color:#285f9e!important;text-decoration:underline!important}#__zzzReaderMode img{display:block!important;height:auto!important;margin:1.2em auto!important;border:0!important}
            #__zzzReaderMode pre{white-space:pre-wrap!important;overflow:auto!important;overflow-wrap:anywhere!important;padding:16px!important;background:#e9e5dc!important}#__zzzReaderMode code{font-family:Consolas,monospace!important;font-size:.88em!important}
            #__zzzReaderMode blockquote{margin:1.2em 0!important;padding:.2em 0 .2em 1.2em!important;border-left:4px solid #b7ad9c!important}#__zzzReaderMode li{margin:.35em 0!important}
            #__zzzReaderMode table{display:table!important;width:100%!important;border-collapse:collapse!important;margin:1.2em 0!important;font-size:.9em!important}#__zzzReaderMode th,#__zzzReaderMode td{padding:8px!important;border:1px solid #c9c1b4!important;text-align:left!important}
            @media(max-width:600px){#__zzzReaderMode .zzz-reader-page{padding:30px 20px 64px!important}#__zzzReaderMode h1{font-size:29px!important}#__zzzReaderMode{font-size:17px!important}}
            @media(prefers-color-scheme:dark){html.__zzzReaderActive{background:#1d2024!important;color-scheme:dark!important}#__zzzReaderMode{background:#1d2024!important;color:#e8e3d9!important;color-scheme:dark!important}#__zzzReaderMode .zzz-reader-page{background:#1d2024!important}#__zzzReaderMode h1,#__zzzReaderMode h2,#__zzzReaderMode h3,#__zzzReaderMode h4{color:#f5f2ec!important}#__zzzReaderMode a{color:#9fc5ef!important}#__zzzReaderMode pre{background:#2b3036!important}#__zzzReaderMode th,#__zzzReaderMode td{border-color:#51565e!important}}
          `;
          (document.head||document.documentElement).appendChild(style);
          document.documentElement.classList.add('__zzzReaderActive');
          document.documentElement.appendChild(overlay);overlay.scrollTop=0;return true;
        }catch(error){
          overlay?.remove();document.getElementById(styleId)?.remove();document.documentElement.classList.remove('__zzzReaderActive');return false;
        }
        })();
        """;

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

    public void ZoomBy(double delta)
    {
        ZoomFactor = Math.Max(0.5, Math.Min(2, Math.Round((ZoomFactor + delta) * 10) / 10));
        if (_view is not null) _view.ZoomFactor = ZoomFactor;
    }

    public void ResetZoom()
    {
        ZoomFactor = 1;
        if (_view is not null) _view.ZoomFactor = 1;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((string.IsNullOrWhiteSpace(value) ? "page" : value).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length > 80 ? safe.Substring(0, 80) : safe;
    }
}
