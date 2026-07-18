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
    private CancellationTokenSource? _addressSuggestionCancellation;
    private Point _tabDragStart;
    private BrowserTabViewModel? _draggedTab;
    private bool _isFullscreen;
    private bool _splitVertical;
    private bool _splitOrientationManuallySet;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
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

    private void TabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(TabsList);
        _draggedTab = FindTabContainer(e.OriginalSource as DependencyObject)?.DataContext as BrowserTabViewModel;
    }

    private void TabsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTab is null) return;
        var current = e.GetPosition(TabsList);
        if (Math.Abs(current.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var tab = _draggedTab;
        _draggedTab = null;
        DragDrop.DoDragDrop(TabsList, new DataObject(typeof(BrowserTabViewModel), tab), DragDropEffects.Move);
    }

    private void TabsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BrowserTabViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TabsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BrowserTabViewModel)) is not BrowserTabViewModel tab) return;
        var insertionIndex = ViewModel.Tabs.Count;
        if (FindTabContainer(e.OriginalSource as DependencyObject) is { } targetContainer && targetContainer.DataContext is BrowserTabViewModel target)
        {
            insertionIndex = ViewModel.Tabs.IndexOf(target);
            if (e.GetPosition(targetContainer).X > targetContainer.ActualWidth / 2) insertionIndex++;
        }
        var sourceIndex = ViewModel.Tabs.IndexOf(tab);
        if (sourceIndex < 0) return;
        if (sourceIndex < insertionIndex) insertionIndex--;
        ViewModel.Services.Tabs.Move(tab, insertionIndex);
        ViewModel.SelectedTab = tab;
        e.Handled = true;
    }

    private ListBoxItem? FindTabContainer(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, TabsList))
        {
            if (source is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == TabsList) return item;
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (AddressSuggestionsPopup.IsOpen && e.Key is Key.Down or Key.Up)
        {
            var count = AddressSuggestionsList.Items.Count;
            if (count == 0) return;
            AddressSuggestionsList.SelectedIndex = e.Key == Key.Down
                ? Math.Min(count - 1, AddressSuggestionsList.SelectedIndex + 1)
                : AddressSuggestionsList.SelectedIndex < 0 ? count - 1 : Math.Max(0, AddressSuggestionsList.SelectedIndex - 1);
            AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.SelectedItem);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && AddressSuggestionsPopup.IsOpen)
        {
            CloseAddressSuggestions();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        if (AddressSuggestionsList.SelectedItem is AddressSuggestion selected)
        {
            ExecuteAddressSuggestion(selected);
            e.Handled = true;
            return;
        }
        CloseAddressSuggestions();
        ViewModel.SelectedTab?.NavigateAddress();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e) => QueueAddressSuggestions();
    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => QueueAddressSuggestions();

    private async void QueueAddressSuggestions()
    {
        _addressSuggestionCancellation?.Cancel();
        _addressSuggestionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _addressSuggestionCancellation = cancellation;
        if (!AddressBox.IsKeyboardFocused || DataContext is not MainViewModel vm || vm.SelectedTab?.IsPrivate == true)
        {
            CloseAddressSuggestions(cancelRequest: false);
            return;
        }

        var query = AddressBox.Text.Trim();
        var history = BuildHistorySuggestions(vm, query);
        ShowAddressSuggestions(history);
        if (query.Length < 2 || LooksLikeAddress(query)) return;

        try { await Task.Delay(180, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        var settings = vm.Services.Settings.Current;
        var engine = settings.SearchEngines.FirstOrDefault(x => x.Id == settings.ActiveSearchEngineId) ?? settings.SearchEngines.FirstOrDefault();
        if (engine is null) return;
        var online = await SearchSuggestionService.GetAsync(engine, query, cancellation.Token);
        if (cancellation.IsCancellationRequested || !AddressBox.IsKeyboardFocused || !string.Equals(query, AddressBox.Text.Trim(), StringComparison.Ordinal)) return;
        var combined = history.Concat(online.Select(x => new AddressSuggestion
        {
            Value = x,
            Title = x,
            Subtitle = engine.Name,
            Kind = LocalizationService.Text("SearchSuggestion")
        })).GroupBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase).Select(x => x.First()).Take(8).ToArray();
        ShowAddressSuggestions(combined);
    }

    private static IReadOnlyList<AddressSuggestion> BuildHistorySuggestions(MainViewModel vm, string query)
    {
        return vm.Services.History.Items
            .Where(x => query.Length == 0 || x.Title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || x.Url.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Take(5)
            .Select(x => new AddressSuggestion
            {
                Value = x.Url,
                Title = string.IsNullOrWhiteSpace(x.Title) ? x.Url : x.Title,
                Subtitle = x.Url,
                Kind = LocalizationService.Text("History")
            }).ToArray();
    }

    private void ShowAddressSuggestions(IReadOnlyList<AddressSuggestion> suggestions)
    {
        var selectedValue = (AddressSuggestionsList.SelectedItem as AddressSuggestion)?.Value;
        AddressSuggestionsList.ItemsSource = suggestions;
        AddressSuggestionsList.SelectedItem = selectedValue is null
            ? null
            : suggestions.FirstOrDefault(x => string.Equals(x.Value, selectedValue, StringComparison.CurrentCultureIgnoreCase));
        AddressSuggestionsPopup.IsOpen = suggestions.Count > 0;
    }

    private void AddressSuggestionsList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (AddressSuggestionsList.SelectedItem is AddressSuggestion selected) ExecuteAddressSuggestion(selected);
    }

    private void ExecuteAddressSuggestion(AddressSuggestion suggestion)
    {
        CloseAddressSuggestions();
        ViewModel.SelectedTab?.NavigateText(suggestion.Value);
        Keyboard.ClearFocus();
    }

    private void CloseAddressSuggestions(bool cancelRequest = true)
    {
        AddressSuggestionsPopup.IsOpen = false;
        AddressSuggestionsList.ItemsSource = null;
        if (!cancelRequest) return;
        _addressSuggestionCancellation?.Cancel();
        _addressSuggestionCancellation?.Dispose();
        _addressSuggestionCancellation = null;
    }

    private static bool LooksLikeAddress(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _)) return true;
        return !value.Any(char.IsWhiteSpace) && (value.Contains('.') || value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));
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
        else if (e.Key == Key.F12) { OpenDeveloperTools(); e.Handled = true; }
        else if (e.Key == Key.F9) { ViewModel.SelectedTab?.ToggleReaderModeCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (shift && e.Key == Key.Escape) { new TaskManagerWindow(ViewModel).Show(); e.Handled = true; }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var home = BrowserHome.GetHomeUrl(ViewModel.Services.Settings.Current);
        menu.Items.Add(Item(LocalizationService.Text("NewTab"), (_, _) => ViewModel.CreateTab(home), "Ctrl+T"));
        menu.Items.Add(Item(LocalizationService.Text("NewPrivateTab"), (_, _) => ViewModel.CreateTab(home, true), "Ctrl+Shift+N"));
        menu.Items.Add(Item(LocalizationService.Text("Library"), async (_, _) =>
        {
            await ViewModel.Services.EnsureBackgroundInitializedAsync();
            new LibraryWindow(ViewModel).ShowDialog();
        }));
        menu.Items.Add(Item(LocalizationService.Text("Downloads"), (_, _) => new DownloadsWindow(ViewModel.Services.Downloads).Show()));
        menu.Items.Add(Item(LocalizationService.Text("BrowserTaskManager"), (_, _) => new TaskManagerWindow(ViewModel).Show(), "Shift+Esc"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(LocalizationService.Text("SaveAsPdf"), async (_, _) => await (ViewModel.SelectedTab?.SaveAsPdfAsync() ?? Task.CompletedTask)));
        menu.Items.Add(Item(LocalizationService.Text("SaveAsMht"), async (_, _) => await (ViewModel.SelectedTab?.SaveAsMhtAsync() ?? Task.CompletedTask)));
        menu.Items.Add(Item(LocalizationService.Text("Print"), (_, _) => ViewModel.SelectedTab?.Print(), "Ctrl+P"));
        menu.Items.Add(Item(LocalizationService.Text(ViewModel.SelectedTab?.IsReaderMode == true ? "ExitReadingMode" : "ReadingMode"), (_, _) => ViewModel.SelectedTab?.ToggleReaderModeCommand.Execute(null), "F9"));
        menu.Items.Add(Item(LocalizationService.Text("FindInPage"), (_, _) => ShowFind(), "Ctrl+F"));
        menu.Items.Add(BuildSplitMenu());
        menu.Items.Add(Item(LocalizationService.Text("Fullscreen"), (_, _) => ToggleFullscreen(), "F11"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(LocalizationService.Text("DeveloperTools"), (_, _) => OpenDeveloperTools(), "F12"));
        menu.Items.Add(Item(LocalizationService.Text("ClearBrowsingData"), async (_, _) => await ClearBrowsingDataAsync()));
        var theme = new MenuItem { Header = LocalizationService.Text("Theme") };
        theme.Items.Add(ThemeItem(AppearanceMode.System, "FollowSystem"));
        theme.Items.Add(ThemeItem(AppearanceMode.Light, "Light"));
        theme.Items.Add(ThemeItem(AppearanceMode.Dark, "Dark"));
        menu.Items.Add(theme);
        var grayscale = new MenuItem
        {
            Header = LocalizationService.Text("GrayscaleMode"),
            IsCheckable = true,
            IsChecked = ViewModel.Services.Settings.Current.Ui.GrayscaleMode
        };
        grayscale.Click += async (_, _) =>
        {
            ViewModel.Services.Settings.Current.Ui.GrayscaleMode = !ViewModel.Services.Settings.Current.Ui.GrayscaleMode;
            await ViewModel.Services.Settings.SaveAsync();
            await ViewModel.ReapplySettingsAsync();
        };
        menu.Items.Add(grayscale);
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

    private void OpenDeveloperTools()
    {
        if (!ViewModel.Services.Settings.Current.Advanced.EnableDeveloperTools)
        {
            new SettingsWindow(ViewModel, showAdvanced: true).ShowDialog();
            return;
        }
        ViewModel.SelectedTab?.OpenDevToolsCommand.Execute(null);
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
        var tab = ViewModel.SelectedTab;
        var playerPath = ViewModel.Services.Settings.Current.Advanced.ExternalPlayerPath;
        if (tab is not null && !string.IsNullOrWhiteSpace(playerPath) &&
            Uri.TryCreate(tab.Url, UriKind.Absolute, out var pageUri) &&
            (pageUri.Scheme == Uri.UriSchemeHttp || pageUri.Scheme == Uri.UriSchemeHttps))
        {
            var openPage = new MenuItem { Header = LocalizationService.Text("OpenPageExternal") };
            openPage.Click += (_, _) => OpenExternalPlayer(playerPath, tab.Url);
            menu.Items.Add(openPage);
            if (tab.MediaResources.Count > 0) menu.Items.Add(new Separator());
        }

        foreach (var media in tab?.MediaResources ?? [])
        {
            var item = new MenuItem { Header = media.ToString(), ToolTip = media.Url };
            item.Click += (_, _) => Clipboard.SetText(media.Url);
            if (!string.IsNullOrWhiteSpace(playerPath))
            {
                var open = new MenuItem { Header = LocalizationService.Text("OpenExternal") };
                open.Click += (_, _) => OpenExternalPlayer(playerPath, media.Url);
                item.Items.Add(open);
            }
            var copy = new MenuItem { Header = LocalizationService.Text("CopyUrl") };
            copy.Click += (_, _) => Clipboard.SetText(media.Url);
            item.Items.Add(copy);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = LocalizationService.Text("NoMediaDetected"), IsEnabled = false });
        menu.IsOpen = true;
    }

    private void OpenExternalPlayer(string playerPath, string url)
    {
        if (ExternalPlayerLauncher.TryOpen(playerPath, url, out var error)) return;
        MessageBox.Show(this,
            string.Format(LocalizationService.Text("ExternalPlayerFailed"), error),
            LocalizationService.Text("ExternalPlayer"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        IsEnabled = false;
        CloseAddressSuggestions();
        ViewModel.Services.Browser.BeginShutdown();
        await IgnoreShutdownError(ViewModel.FlushSessionJournalAsync);
        await IgnoreShutdownError(ViewModel.Services.Settings.SaveAsync);
        await IgnoreShutdownError(ViewModel.Services.PrepareForShutdownAsync);
        // Clear last, after navigation has stopped and background history loading
        // has settled, so nothing can recreate data after the privacy operation.
        await IgnoreShutdownError(ViewModel.Services.Privacy.ClearOnExitAsync);
        _shutdownComplete = true;
        Close();
    }

    private static async Task IgnoreShutdownError(Func<Task> operation)
    {
        try { await operation(); }
        catch { }
    }

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
        else if (e.Shortcut == BrowserShortcut.TaskManager)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => new TaskManagerWindow(ViewModel).Show()));
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

    private sealed class AddressSuggestion
    {
        public string Value { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }
}
