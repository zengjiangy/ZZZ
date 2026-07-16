using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using ZZZ.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace ZZZ.Views;

public partial class BrowserTabView : UserControl
{
    private WebView2? _browser;
    private Task? _browserInitialization;
    private CancellationTokenSource? _browserInitializationCancellation;
    private Task _retiringBrowser = Task.CompletedTask;
    private bool _subscribed;
    private int _showRevision;
    public BrowserTabView() => InitializeComponent();
    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowserTabViewModel tab || tab.IsClosed) return;
        if (!_subscribed) { tab.PropertyChanged += Tab_PropertyChanged; _subscribed = true; }
        await ShowCurrentAsync(tab);
    }

    private async void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BrowserTabViewModel { IsClosed: false } tab && (e.PropertyName == nameof(BrowserTabViewModel.Url) || e.PropertyName == nameof(BrowserTabViewModel.StartPageRevision)))
            await ShowCurrentAsync(tab);
    }

    private async Task ShowCurrentAsync(BrowserTabViewModel tab)
    {
        var revision = ++_showRevision;
        if (tab.IsClosed) return;
        if (tab.IsStartPage)
        {
            var retiring = _browser;
            var initialization = _browserInitialization;
            var cancellation = _browserInitializationCancellation;
            _browser = null;
            _browserInitialization = null;
            _browserInitializationCancellation = null;
            try { cancellation?.Cancel(); } catch { }
            if (retiring is not null)
            {
                var previousRetirement = _retiringBrowser;
                _retiringBrowser = RetireBrowserAsync(previousRetirement, initialization, cancellation, retiring, tab);
            }
            else cancellation?.Dispose();
            Host.Children.Clear();
            Host.Children.Add(new StartPageView(tab));
            return;
        }

        await _retiringBrowser;
        if (revision != _showRevision || tab.IsStartPage || tab.IsClosed) return;
        if (_browser is null || tab.IsSleeping)
        {
            Host.Children.Clear();
            _browser = new WebView2 { DefaultBackgroundColor = Services.ThemeService.WebBackgroundColor() };
            _browserInitialization = null;
            _browserInitializationCancellation?.Dispose();
            _browserInitializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(tab.LifetimeToken);
            Host.Children.Add(_browser);
        }
        var browser = _browser;
        var initializationCancellation = _browserInitializationCancellation!;
        try
        {
            _browserInitialization ??= InitializeBrowserAsync(browser, tab, initializationCancellation.Token);
            await _browserInitialization;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_browser, browser))
            {
                _browser = null;
                _browserInitialization = null;
                _browserInitializationCancellation = null;
                initializationCancellation.Dispose();
            }
            try { browser.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            if (revision != _showRevision || tab.IsStartPage || tab.IsClosed || !ReferenceEquals(_browser, browser)) return;
            _browser = null;
            _browserInitialization = null;
            _browserInitializationCancellation = null;
            initializationCancellation.Dispose();
            try { browser.Dispose(); } catch { }
            Host.Children.Clear();
            Host.Children.Add(new TextBlock
            {
                Text = $"{Services.LocalizationService.Text("WebViewUnavailable")}\n{ex.Message}",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
                TextAlignment = TextAlignment.Center
            });
        }
    }

    private static async Task InitializeBrowserAsync(WebView2 browser, BrowserTabViewModel tab, CancellationToken cancellationToken)
    {
        try
        {
            await App.Services.EnsureBackgroundInitializedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            await App.Services.Browser.InitializeAsync(browser, tab, cancellationToken);
            if (App.Services.BackgroundInitializationDegraded && string.IsNullOrWhiteSpace(tab.Status))
                tab.Status = Services.LocalizationService.Text("BackgroundServicesDegraded");
        }
        catch (OperationCanceledException)
        {
            try { browser.Dispose(); } catch { }
            throw;
        }
    }

    private static async Task RetireBrowserAsync(Task previousRetirement, Task? initialization, CancellationTokenSource? cancellation, WebView2 browser, BrowserTabViewModel tab)
    {
        try { await previousRetirement; } catch { }
        if (initialization is not null)
            try { await initialization; } catch { }
        App.Services.Browser.Sleep(tab);
        try { browser.Dispose(); } catch { }
        cancellation?.Dispose();
    }
}
