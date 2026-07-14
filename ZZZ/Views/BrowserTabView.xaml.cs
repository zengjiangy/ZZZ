using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using ZZZ.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace ZZZ.Views;

public partial class BrowserTabView : UserControl
{
    private WebView2? _browser;
    private bool _subscribed;
    public BrowserTabView() => InitializeComponent();
    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowserTabViewModel tab) return;
        if (!_subscribed) { tab.PropertyChanged += Tab_PropertyChanged; _subscribed = true; }
        await ShowCurrentAsync(tab);
    }

    private async void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BrowserTabViewModel tab && (e.PropertyName == nameof(BrowserTabViewModel.Url) || e.PropertyName == nameof(BrowserTabViewModel.StartPageRevision)))
            await ShowCurrentAsync(tab);
    }

    private async Task ShowCurrentAsync(BrowserTabViewModel tab)
    {
        if (tab.IsStartPage)
        {
            if (_browser is not null)
            {
                App.Services.Browser.Sleep(tab);
                _browser = null;
            }
            Host.Children.Clear();
            Host.Children.Add(new StartPageView(tab));
            return;
        }

        if (_browser is null || tab.IsSleeping)
        {
            Host.Children.Clear();
            _browser = new WebView2 { DefaultBackgroundColor = Services.ThemeService.WebBackgroundColor() };
            Host.Children.Add(_browser);
        }
        try { await App.Services.Browser.InitializeAsync(_browser, tab); }
        catch (Exception ex)
        {
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
}
