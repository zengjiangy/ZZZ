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
    private BrowserTabViewModel? _splitTab;
    private BrowserTabViewModel? _findTab;
    private bool _isFullscreen;
    private bool _splitVertical;
    private bool _splitOrientationManuallySet;
    private WindowState _stateBeforeFullscreen;
    private WindowStyle _styleBeforeFullscreen;
    private ResizeMode _resizeBeforeFullscreen;
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel oldVm) { oldVm.PropertyChanged -= ViewModel_PropertyChanged; oldVm.Tabs.CollectionChanged -= Tabs_CollectionChanged; oldVm.Services.Browser.ShortcutRequested -= Browser_ShortcutRequested; }
            if (e.NewValue is MainViewModel newVm) { newVm.PropertyChanged += ViewModel_PropertyChanged; newVm.Tabs.CollectionChanged += Tabs_CollectionChanged; newVm.Services.Browser.ShortcutRequested += Browser_ShortcutRequested; ShowSelectedTab(newVm.SelectedTab); }
        };
        SizeChanged += Window_SizeChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab)) ShowSelectedTab(ViewModel.SelectedTab);
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null) return;
        foreach (BrowserTabViewModel tab in e.OldItems)
        {
            _tabViews.Remove(tab);
            if (ReferenceEquals(_findTab, tab)) CloseFind();
            if (ReferenceEquals(_splitTab, tab)) CloseSplit();
        }
    }

    private void ShowSelectedTab(BrowserTabViewModel? tab)
    {
        if (tab is null) { TabContentHost.Content = null; return; }
        if (ReferenceEquals(tab, _splitTab)) CloseSplit();
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
        else if (ctrl && shift && e.Key == Key.W && _splitTab is not null) { CloseSplit(); e.Handled = true; }
        else if (ctrl && e.Key == Key.W && ViewModel.SelectedTab is not null) { ViewModel.CloseTabCommand.Execute(ViewModel.SelectedTab); e.Handled = true; }
        else if ((ctrl && e.Key == Key.L) || (alt && e.Key == Key.D)) { AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.R) { ViewModel.SelectedTab?.ReloadCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.P) { ViewModel.SelectedTab?.Print(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { ShowFind(); e.Handled = true; }
        else if (alt && e.Key == Key.Left) { ViewModel.SelectedTab?.BackCommand.Execute(null); e.Handled = true; }
        else if (alt && e.Key == Key.Right) { ViewModel.SelectedTab?.ForwardCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F12) { ViewModel.SelectedTab?.OpenDevToolsCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
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
        menu.Items.Add(Item(LocalizationService.Text("SaveAsPdf"), async (_, _) => await (ViewModel.SelectedTab?.SaveAsPdfAsync() ?? Task.CompletedTask)));
        menu.Items.Add(Item(LocalizationService.Text("SaveAsMht"), async (_, _) => await (ViewModel.SelectedTab?.SaveAsMhtAsync() ?? Task.CompletedTask)));
        menu.Items.Add(Item(LocalizationService.Text("Print"), (_, _) => ViewModel.SelectedTab?.Print(), "Ctrl+P"));
        menu.Items.Add(Item(LocalizationService.Text("FindInPage"), (_, _) => ShowFind(), "Ctrl+F"));
        menu.Items.Add(BuildSplitMenu());
        menu.Items.Add(Item(LocalizationService.Text("Fullscreen"), (_, _) => ToggleFullscreen(), "F11"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(LocalizationService.Text("DeveloperTools"), (_, _) => ViewModel.SelectedTab?.OpenDevToolsCommand.Execute(null), "F12"));
        menu.Items.Add(Item(LocalizationService.Text("ClearBrowsingData"), async (_, _) => await ClearBrowsingDataAsync()));
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
        await ViewModel.Services.Privacy.ClearAsync(dialog.Selection);
        ViewModel.SelectedTab!.Status = LocalizationService.Text("ClearComplete");
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

    private MenuItem BuildSplitMenu()
    {
        var split = new MenuItem { Header = LocalizationService.Text("SplitScreen") };
        foreach (var tab in ViewModel.Tabs.Where(x => !ReferenceEquals(x, ViewModel.SelectedTab)))
        {
            var item = new MenuItem { Header = tab.Title, ToolTip = tab.Url, IsCheckable = true, IsChecked = ReferenceEquals(tab, _splitTab) };
            item.Click += (_, _) => OpenSplit(tab);
            split.Items.Add(item);
        }
        if (_splitTab is not null)
        {
            split.Items.Add(new Separator());
            split.Items.Add(Item(LocalizationService.Text("CloseSplit"), (_, _) => CloseSplit(), "Ctrl+Shift+W"));
        }
        if (split.Items.Count == 0)
        {
            split.Items.Add(Item(LocalizationService.Text("OpenNewSplit"), (_, _) =>
            {
                var primary = ViewModel.SelectedTab;
                ViewModel.CreateTab(BrowserHome.GetHomeUrl(ViewModel.Services.Settings.Current));
                var created = ViewModel.SelectedTab;
                ViewModel.SelectedTab = primary;
                if (created is not null) OpenSplit(created);
            }));
        }
        return split;
    }

    private void OpenSplit(BrowserTabViewModel tab)
    {
        _splitTab = tab;
        ViewModel.SelectedTab?.ResetZoom();
        tab.ResetZoom();
        if (!_tabViews.TryGetValue(tab, out var view)) { view = new BrowserTabView { DataContext = tab }; _tabViews[tab] = view; }
        SecondaryTabContentHost.Content = view;
        SplitBar.Visibility = Visibility.Visible;
        _splitOrientationManuallySet = false;
        _splitVertical = BrowserArea.ActualWidth < 1400;
        UpdateSplitLayout();
    }

    private void CloseSplit()
    {
        if (ReferenceEquals(_findTab, _splitTab)) CloseFind();
        _splitTab = null;
        SecondaryTabContentHost.Content = null;
        SplitBar.Visibility = Visibility.Collapsed;
        ResetSplitLayout();
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            _styleBeforeFullscreen = WindowStyle;
            _resizeBeforeFullscreen = ResizeMode;
            TabBar.Visibility = Toolbar.Visibility = StatusBar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal; WindowStyle = _styleBeforeFullscreen; ResizeMode = _resizeBeforeFullscreen; WindowState = _stateBeforeFullscreen;
            TabBar.Visibility = ViewModel.Services.Settings.Current.Ui.ShowTabBar ? Visibility.Visible : Visibility.Collapsed;
            Toolbar.Visibility = ViewModel.Services.Settings.Current.Ui.ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Visible;
        }
    }

    private void ShowFind(BrowserTabViewModel? target = null) { _findTab = target ?? ViewModel.SelectedTab; FindBar.Visibility = Visibility.Visible; FindBox.Focus(); FindBox.SelectAll(); }
    private void CloseFind() { FindBar.Visibility = Visibility.Collapsed; _findTab = null; }
    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFind();
    private async void FindNext_Click(object sender, RoutedEventArgs e) => await ((_findTab ?? ViewModel.SelectedTab)?.FindAsync(FindBox.Text, false) ?? Task.CompletedTask);
    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await ((_findTab ?? ViewModel.SelectedTab)?.FindAsync(FindBox.Text, true) ?? Task.CompletedTask);
    private async void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { await ((_findTab ?? ViewModel.SelectedTab)?.FindAsync(FindBox.Text, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ?? Task.CompletedTask); e.Handled = true; }
    }

    private void Browser_ShortcutRequested(object? sender, BrowserShortcutEventArgs e)
    {
        if (e.Shortcut == BrowserShortcut.Find)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => ShowFind(e.Tab)));
        }
        else if (e.Shortcut == BrowserShortcut.CloseSplit && _splitTab is not null)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(CloseSplit));
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_splitTab is null || _splitOrientationManuallySet) return;
        var useVertical = BrowserArea.ActualWidth < 1400;
        if (useVertical != _splitVertical) { _splitVertical = useVertical; UpdateSplitLayout(); }
    }

    private void ToggleSplitOrientation_Click(object sender, RoutedEventArgs e)
    {
        _splitVertical = !_splitVertical;
        _splitOrientationManuallySet = true;
        UpdateSplitLayout();
    }

    private void UpdateSplitLayout()
    {
        if (_splitTab is null) return;
        if (_splitVertical)
        {
            PrimaryColumn.Width = new GridLength(1, GridUnitType.Star); SplitterColumn.Width = SecondaryColumn.Width = new GridLength(0);
            PrimaryRow.Height = SecondaryRow.Height = new GridLength(1, GridUnitType.Star); HorizontalSplitterRow.Height = new GridLength(5);
            Grid.SetRow(TabContentHost, 0); Grid.SetColumn(TabContentHost, 0); Grid.SetRowSpan(TabContentHost, 1); Grid.SetColumnSpan(TabContentHost, 3);
            Grid.SetRow(SecondaryTabContentHost, 2); Grid.SetColumn(SecondaryTabContentHost, 0); Grid.SetRowSpan(SecondaryTabContentHost, 1); Grid.SetColumnSpan(SecondaryTabContentHost, 3);
            SplitDivider.Visibility = Visibility.Collapsed; HorizontalSplitDivider.Visibility = Visibility.Visible;
            SplitOrientationButton.Content = "↔";
        }
        else
        {
            PrimaryColumn.Width = SecondaryColumn.Width = new GridLength(1, GridUnitType.Star); SplitterColumn.Width = new GridLength(5);
            PrimaryRow.Height = new GridLength(1, GridUnitType.Star); HorizontalSplitterRow.Height = SecondaryRow.Height = new GridLength(0);
            Grid.SetRow(TabContentHost, 0); Grid.SetColumn(TabContentHost, 0); Grid.SetRowSpan(TabContentHost, 3); Grid.SetColumnSpan(TabContentHost, 1);
            Grid.SetRow(SecondaryTabContentHost, 0); Grid.SetColumn(SecondaryTabContentHost, 2); Grid.SetRowSpan(SecondaryTabContentHost, 3); Grid.SetColumnSpan(SecondaryTabContentHost, 1);
            SplitDivider.Visibility = Visibility.Visible; HorizontalSplitDivider.Visibility = Visibility.Collapsed;
            SplitOrientationButton.Content = "↕";
        }
    }

    private void ResetSplitLayout()
    {
        PrimaryColumn.Width = new GridLength(1, GridUnitType.Star); SplitterColumn.Width = SecondaryColumn.Width = new GridLength(0);
        PrimaryRow.Height = new GridLength(1, GridUnitType.Star); HorizontalSplitterRow.Height = SecondaryRow.Height = new GridLength(0);
        Grid.SetRow(TabContentHost, 0); Grid.SetColumn(TabContentHost, 0); Grid.SetRowSpan(TabContentHost, 1); Grid.SetColumnSpan(TabContentHost, 1);
        Grid.SetRow(SecondaryTabContentHost, 0); Grid.SetColumn(SecondaryTabContentHost, 2); Grid.SetRowSpan(SecondaryTabContentHost, 1); Grid.SetColumnSpan(SecondaryTabContentHost, 1);
        SplitDivider.Visibility = HorizontalSplitDivider.Visibility = Visibility.Collapsed;
    }

    private void CloseSplit_Click(object sender, RoutedEventArgs e) => CloseSplit();
    private void PrimaryZoomOut_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedTab?.ZoomBy(-0.1);
    private void PrimaryZoomReset_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedTab?.ResetZoom();
    private void PrimaryZoomIn_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedTab?.ZoomBy(0.1);
    private void SecondaryZoomOut_Click(object sender, RoutedEventArgs e) => _splitTab?.ZoomBy(-0.1);
    private void SecondaryZoomReset_Click(object sender, RoutedEventArgs e) => _splitTab?.ResetZoom();
    private void SecondaryZoomIn_Click(object sender, RoutedEventArgs e) => _splitTab?.ZoomBy(0.1);
}
