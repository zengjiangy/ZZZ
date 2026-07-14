using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZZZ.Configuration;
using ZZZ.ViewModels;
using ZZZ.Views;
using System.Collections.Specialized;
using System.ComponentModel;
using ZZZ.Services;

namespace ZZZ;

public partial class MainWindow : Window
{
    private readonly Dictionary<BrowserTabViewModel, BrowserTabView> _tabViews = [];
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel oldVm) { oldVm.PropertyChanged -= ViewModel_PropertyChanged; oldVm.Tabs.CollectionChanged -= Tabs_CollectionChanged; }
            if (e.NewValue is MainViewModel newVm) { newVm.PropertyChanged += ViewModel_PropertyChanged; newVm.Tabs.CollectionChanged += Tabs_CollectionChanged; ShowSelectedTab(newVm.SelectedTab); }
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab)) ShowSelectedTab(ViewModel.SelectedTab);
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null) return;
        foreach (BrowserTabViewModel tab in e.OldItems) _tabViews.Remove(tab);
    }

    private void ShowSelectedTab(BrowserTabViewModel? tab)
    {
        if (tab is null) { TabContentHost.Content = null; return; }
        if (!_tabViews.TryGetValue(tab, out var view)) { view = new BrowserTabView { DataContext = tab }; _tabViews[tab] = view; }
        TabContentHost.Content = view;
    }

    private void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabsList.SelectedItem is not BrowserTabViewModel tab || DataContext is not MainViewModel vm) return;
        vm.SelectedTab = tab;
        tab.Activate();
        ShowSelectedTab(tab);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.SelectedTab?.NavigateAddress();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var home = BrowserHome.GetHomeUrl(ViewModel.Services.Settings.Current);
        if (ctrl && shift && e.Key == Key.N) { ViewModel.CreateTab(home, true); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.T) { ViewModel.CreateTab(ViewModel.Services.History.Items.FirstOrDefault()?.Url ?? home); e.Handled = true; }
        else if (ctrl && e.Key == Key.T) { ViewModel.CreateTab(home); e.Handled = true; }
        else if (ctrl && e.Key == Key.W && ViewModel.SelectedTab is not null) { ViewModel.CloseTabCommand.Execute(ViewModel.SelectedTab); e.Handled = true; }
        else if ((ctrl && e.Key == Key.L) || (alt && e.Key == Key.D)) { AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.R) { ViewModel.SelectedTab?.ReloadCommand.Execute(null); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { _ = CaptureRegionAsync(); e.Handled = true; }
        else if (alt && e.Key == Key.Left) { ViewModel.SelectedTab?.BackCommand.Execute(null); e.Handled = true; }
        else if (alt && e.Key == Key.Right) { ViewModel.SelectedTab?.ForwardCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F12) { ViewModel.SelectedTab?.OpenDevToolsCommand.Execute(null); e.Handled = true; }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var home = BrowserHome.GetHomeUrl(ViewModel.Services.Settings.Current);
        menu.Items.Add(Item(LocalizationService.Text("NewTab"), (_, _) => ViewModel.CreateTab(home), "Ctrl+T"));
        menu.Items.Add(Item(LocalizationService.Text("NewPrivateTab"), (_, _) => ViewModel.CreateTab(home, true), "Ctrl+Shift+N"));
        menu.Items.Add(Item(LocalizationService.Text("Library"), (_, _) => new LibraryWindow(ViewModel).ShowDialog()));
        menu.Items.Add(Item(LocalizationService.Text("Downloads"), (_, _) => new DownloadsWindow(ViewModel.Services.Downloads).Show()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(LocalizationService.Text("DeveloperTools"), (_, _) => ViewModel.SelectedTab?.OpenDevToolsCommand.Execute(null), "F12"));
        menu.Items.Add(Item(LocalizationService.Text("ClearBrowsingData"), async (_, _) => await ClearBrowsingDataAsync()));
        menu.Items.Add(Item(LocalizationService.Text("RegionCapture"), async (_, _) => await CaptureRegionAsync(), "Ctrl+Shift+S"));
        var theme = new MenuItem { Header = LocalizationService.Text("Theme") };
        theme.Items.Add(ThemeItem(AppearanceMode.System, "FollowSystem"));
        theme.Items.Add(ThemeItem(AppearanceMode.Light, "Light"));
        theme.Items.Add(ThemeItem(AppearanceMode.Dark, "Dark"));
        menu.Items.Add(theme);
        menu.Items.Add(Item(LocalizationService.Text("Settings"), (_, _) => new SettingsWindow(ViewModel).ShowDialog()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(LocalizationService.Text("Exit"), (_, _) => Close()));
        menu.PlacementTarget = MenuButton;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string title, RoutedEventHandler handler, string gesture = "")
    {
        var item = new MenuItem { Header = title, InputGestureText = gesture };
        item.Click += handler;
        return item;
    }

    private MenuItem ThemeItem(AppearanceMode mode, string labelKey)
    {
        var item = new MenuItem
        {
            Header = LocalizationService.Text(labelKey), IsCheckable = true,
            IsChecked = ViewModel.Services.Settings.Current.Appearance == mode
        };
        item.Click += async (_, _) =>
        {
            ViewModel.Services.Settings.Current.Appearance = mode;
            await ViewModel.Services.Settings.SaveAsync();
            await ViewModel.ReapplySettingsAsync();
        };
        return item;
    }

    private async Task ClearBrowsingDataAsync()
    {
        var dialog = new ClearDataWindow(ViewModel.Services.Settings.Current.Privacy.ClearOnExitItems) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show(this, LocalizationService.Text("ClearDataFinalConfirm"), LocalizationService.Text("ClearDataTitle"), MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
        await ViewModel.Services.Privacy.ClearAsync(dialog.Selection);
        MessageBox.Show(this, LocalizationService.Text("ClearComplete"), LocalizationService.Text("ClearDataTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task CaptureRegionAsync()
    {
        if (ViewModel.SelectedTab is null) return;
        var bytes = await ViewModel.SelectedTab.CaptureVisiblePageAsync();
        if (bytes is null || bytes.Length == 0)
        {
            MessageBox.Show(this, LocalizationService.Text("CaptureFailed"), LocalizationService.Text("RegionCapture"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new RegionCaptureWindow(bytes) { Owner = this };
        if (dialog.ShowDialog() == true)
            ViewModel.SelectedTab.Status = LocalizationService.Text("CaptureSaved");
    }

    private void MediaButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MediaButton };
        foreach (var media in ViewModel.SelectedTab?.MediaResources ?? [])
        {
            var item = new MenuItem { Header = media.ToString(), ToolTip = media.Url };
            item.Click += (_, _) => Clipboard.SetText(media.Url);
            if (!string.IsNullOrWhiteSpace(ViewModel.Services.Settings.Current.Advanced.ExternalPlayerPath))
            {
                var open = new MenuItem { Header = LocalizationService.Text("OpenExternal") };
                open.Click += (_, _) => Process.Start(new ProcessStartInfo(ViewModel.Services.Settings.Current.Advanced.ExternalPlayerPath, $"\"{media.Url}\"") { UseShellExecute = true });
                item.Items.Add(open);
            }
            var copy = new MenuItem { Header = LocalizationService.Text("CopyUrl") };
            copy.Click += (_, _) => Clipboard.SetText(media.Url);
            item.Items.Add(copy);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => await ViewModel.Services.Settings.SaveAsync();
}
