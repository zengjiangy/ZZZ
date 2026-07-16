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
    private readonly DispatcherTimer? _sessionRestoreTimer;
    private IReadOnlyList<string> _pendingSessionUrls = [];
    private bool _savingSession;
    public ObservableCollection<BrowserTabViewModel> Tabs => _services.Tabs.Items;
    [ObservableProperty] private BrowserTabViewModel? selectedTab;
    [ObservableProperty] private bool isCurrentPageBookmarked;
    public bool ShowSessionRestorePrompt => _pendingSessionUrls.Count > 0 && SelectedTab?.IsPrivate != true;
    public AppServices Services => _services;

    public MainViewModel(AppServices services, IEnumerable<string>? launchUrls = null)
    {
        _services = services;
        _services.Bookmarks.Changed += (_, _) => UpdateBookmarkState();
        _services.Browser.NewTabRequested += (url, isPrivate) => CreateTab(url, isPrivate);
        var suppliedUrls = launchUrls?.Where(x => Uri.TryCreate(x, UriKind.Absolute, out _)).ToArray() ?? [];
        _pendingSessionUrls = _services.Settings.Current.Browser.RestoreLastSession
            ? _services.Session.Urls.Where(x => !BrowserHome.IsStartPage(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray()
            : [];
        foreach (var url in suppliedUrls) CreateTab(url);
        if (Tabs.Count == 0) CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current));
        if (_pendingSessionUrls.Count > 0)
        {
            _sessionRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _sessionRestoreTimer.Tick += (_, _) => DismissPreviousSession();
            _sessionRestoreTimer.Start();
            OnPropertyChanged(nameof(ShowSessionRestorePrompt));
        }
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
        OnPropertyChanged(nameof(ShowSessionRestorePrompt));
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
    private void RestorePreviousSession()
    {
        var urls = _pendingSessionUrls;
        if (urls.Count == 0) return;
        var placeholder = Tabs.Count == 1 && Tabs[0].IsStartPage && !Tabs[0].IsPrivate ? Tabs[0] : null;
        ClearPendingSession();
        if (placeholder is not null) _services.Tabs.Close(placeholder);
        foreach (var url in urls) CreateTab(url);
    }

    [RelayCommand]
    private void DismissPreviousSession() => ClearPendingSession();

    private void ClearPendingSession()
    {
        _sessionRestoreTimer?.Stop();
        _pendingSessionUrls = [];
        OnPropertyChanged(nameof(ShowSessionRestorePrompt));
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
        ThemeService.Apply(_services.Settings.Current.Appearance, _services.Settings.Current.StartPage);
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
        try { await _services.Session.SaveAsync(Tabs.Select(x => (x.Url, x.IsPrivate))); }
        finally { _savingSession = false; }
    }
}
