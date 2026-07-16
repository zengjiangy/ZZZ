using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZZZ.Services;
using ZZZ.Configuration;

namespace ZZZ.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _sleepTimer;
    private bool _savingSession;
    public ObservableCollection<BrowserTabViewModel> Tabs => _services.Tabs.Items;
    [ObservableProperty] private BrowserTabViewModel? selectedTab;
    [ObservableProperty] private bool isCurrentPageBookmarked;
    public AppServices Services => _services;

    public MainViewModel(AppServices services, IEnumerable<string>? launchUrls = null)
    {
        _services = services;
        _services.Bookmarks.Changed += (_, _) => UpdateBookmarkState();
        _services.Browser.NewTabRequested += (url, isPrivate) => CreateTab(url, isPrivate);
        var suppliedUrls = launchUrls?.Where(x => Uri.TryCreate(x, UriKind.Absolute, out _)).ToArray() ?? [];
        var startupUrls = suppliedUrls.Length > 0 ? suppliedUrls : (_services.Settings.Current.Browser.RestoreLastSession ? _services.Session.Urls : []);
        foreach (var url in startupUrls) CreateTab(url);
        if (Tabs.Count == 0) CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current));
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sleepTimer.Tick += async (_, _) =>
        {
            SleepIdleTabs();
            await SaveSessionSnapshotAsync();
        };
        _sleepTimer.Start();
    }

    partial void OnSelectedTabChanged(BrowserTabViewModel? oldValue, BrowserTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= SelectedTab_PropertyChanged;
        if (newValue is not null)
        {
            newValue.PropertyChanged += SelectedTab_PropertyChanged;
            newValue.Activate();
        }
        _services.Browser.SetActive(newValue);
        UpdateBookmarkState();
    }

    private void SelectedTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTabViewModel.Url)) UpdateBookmarkState();
    }

    private void UpdateBookmarkState() => IsCurrentPageBookmarked = SelectedTab is not null && _services.Bookmarks.Contains(SelectedTab.Url);

    [RelayCommand]
    private void CreateTab() => CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current));

    [RelayCommand]
    private void CreatePrivateTab() => CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current), true);

    public void CreateTab(string url, bool isPrivate = false)
    {
        SelectedTab = _services.Tabs.Create(url, isPrivate);
    }

    [RelayCommand]
    private void SelectTab(BrowserTabViewModel tab) => SelectedTab = tab;

    [RelayCommand]
    private void CloseTab(BrowserTabViewModel tab)
    {
        var index = _services.Tabs.Close(tab);
        if (Tabs.Count == 0) { CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current)); return; }
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void DuplicateTab(BrowserTabViewModel tab) => CreateTab(tab.Url, tab.IsPrivate);

    [RelayCommand]
    private void CloseOthers(BrowserTabViewModel tab)
    {
        _services.Tabs.CloseOthers(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTabsToRight(BrowserTabViewModel tab)
    {
        _services.Tabs.CloseToRight(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (SelectedTab is null || SelectedTab.IsStartPage) return;
        await _services.Bookmarks.ToggleAsync(SelectedTab.Title, SelectedTab.Url);
        UpdateBookmarkState();
    }

    public async Task ReapplySettingsAsync()
    {
        LocalizationService.Apply(_services.Settings.Current.Ui.Language);
        ThemeService.Apply(_services.Settings.Current.Appearance);
        await _services.Browser.ApplyCurrentSettingsAsync(reloadPages: true);
        foreach (var tab in Tabs.Where(x => x.IsStartPage)) tab.RefreshStartPage();
        OnPropertyChanged(nameof(Services));
        OnPropertyChanged(string.Empty);
    }

    private void SleepIdleTabs()
    {
        var minutes = _services.Settings.Current.Browser.SleepBackgroundTabsAfterMinutes;
        if (minutes <= 0) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
        foreach (var tab in Tabs.Where(x => x != SelectedTab && !x.IsPrivate && !x.IsSleeping && x.LastActiveUtc < cutoff)) _services.Browser.Sleep(tab);
    }

    private async Task SaveSessionSnapshotAsync()
    {
        if (_savingSession) return;
        _savingSession = true;
        try { await _services.Session.SaveAsync(Tabs.Where(x => !x.IsPrivate).Select(x => x.Url)); }
        finally { _savingSession = false; }
    }
}
