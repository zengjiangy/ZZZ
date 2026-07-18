using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZZZ.Services;
using ZZZ.Configuration;
using ZZZ.Models;

namespace ZZZ.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _sleepTimer;
    private readonly DispatcherTimer _sessionJournalTimer;
    private readonly DispatcherTimer? _sessionRestoreTimer;
    private IReadOnlyList<WorkspaceTabSnapshot> _pendingSessionTabs = [];
    private bool _sessionJournalPaused;
    public ObservableCollection<BrowserTabViewModel> Tabs => _services.Tabs.Items;
    public ObservableCollection<BrowserTabViewModel> WorkspaceTabs { get; } = [];
    public ObservableCollection<WorkspaceDefinition> Workspaces => _services.Workspaces.Items;
    [ObservableProperty] private BrowserTabViewModel? selectedTab;
    [ObservableProperty] private WorkspaceDefinition? activeWorkspace;
    [ObservableProperty] private bool isCurrentPageBookmarked;
    public bool ShowSessionRestorePrompt => _pendingSessionTabs.Count > 0 && SelectedTab?.IsPrivate != true;
    public bool ShowHorizontalTabBar => _services.Settings.Current.Ui.ShowTabBar && !_services.Settings.Current.Ui.UseVerticalTabs;
    public bool ShowVerticalTabBar => _services.Settings.Current.Ui.ShowTabBar && _services.Settings.Current.Ui.UseVerticalTabs;
    public bool AreVerticalTabsExpanded => !_services.Settings.Current.Ui.VerticalTabsCollapsed;
    public double VerticalTabBarWidth => _services.Settings.Current.Ui.VerticalTabsCollapsed ? 58 : 250;
    public bool CanDeleteWorkspace => Workspaces.Count > 1;
    public AppServices Services => _services;

    public MainViewModel(AppServices services, IEnumerable<string>? launchUrls = null)
    {
        _services = services;
        _services.Bookmarks.Changed += (_, _) => UpdateBookmarkState();
        _services.Browser.NewTabRequested += (url, isPrivate) => CreateTab(url, isPrivate);
        activeWorkspace = _services.Workspaces.Active;
        _sessionJournalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _sessionJournalTimer.Tick += async (_, _) =>
        {
            _sessionJournalTimer.Stop();
            try { await SaveSessionSnapshotAsync(); }
            catch { /* Keep the last complete snapshot and retry on the next change. */ }
        };
        Tabs.CollectionChanged += Tabs_CollectionChanged;
        var suppliedUrls = launchUrls?.Where(x => Uri.TryCreate(x, UriKind.Absolute, out _)).ToArray() ?? [];
        _pendingSessionTabs = _services.Settings.Current.Browser.RestoreLastSession ? _services.Workspaces.RestoreTabs() : [];
        if (_pendingSessionTabs.Count == 0 && _services.Settings.Current.Browser.RestoreLastSession)
            _pendingSessionTabs = _services.Session.Urls.Where(x => !BrowserHome.IsStartPage(x)).Take(50)
                .Select(x => new WorkspaceTabSnapshot { WorkspaceId = activeWorkspace.Id, Url = x }).ToArray();
        // Keep the previous durable snapshot intact while the 10-second restore
        // decision is pending. A crash during the prompt can therefore offer it
        // again instead of replacing it with an empty start page.
        _sessionJournalPaused = _pendingSessionTabs.Count > 0;
        foreach (var url in suppliedUrls) CreateTab(url);
        if (Tabs.Count == 0) CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current));
        if (_pendingSessionTabs.Count > 0)
        {
            _sessionRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _sessionRestoreTimer.Tick += (_, _) => DismissPreviousSession();
            _sessionRestoreTimer.Start();
            OnPropertyChanged(nameof(ShowSessionRestorePrompt));
        }
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sleepTimer.Tick += (_, _) => SleepIdleTabs();
        _sleepTimer.Start();
    }

    partial void OnActiveWorkspaceChanged(WorkspaceDefinition? oldValue, WorkspaceDefinition? newValue)
    {
        if (newValue is null || ReferenceEquals(oldValue, newValue)) return;
        _services.Workspaces.SetActive(newValue.Id);
        RefreshWorkspaceTabs();
        if (WorkspaceTabs.Count == 0) CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current), workspaceId: newValue.Id);
        else if (SelectedTab is null || !string.Equals(SelectedTab.WorkspaceId, newValue.Id, StringComparison.OrdinalIgnoreCase)) SelectedTab = WorkspaceTabs[0];
        QueueSessionSnapshot();
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

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (BrowserTabViewModel tab in e.OldItems) tab.PropertyChanged -= Tab_SessionPropertyChanged;
        if (e.NewItems is not null)
            foreach (BrowserTabViewModel tab in e.NewItems) tab.PropertyChanged += Tab_SessionPropertyChanged;
        RefreshWorkspaceTabs();
        var changedPublicTab = e.Action == NotifyCollectionChangedAction.Reset ||
            (e.OldItems?.Cast<BrowserTabViewModel>().Any(x => !x.IsPrivate) ?? false) ||
            (e.NewItems?.Cast<BrowserTabViewModel>().Any(x => !x.IsPrivate) ?? false);
        if (changedPublicTab) QueueSessionSnapshot();
    }

    private void Tab_SessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BrowserTabViewModel { IsPrivate: false } && e.PropertyName is nameof(BrowserTabViewModel.Url) or nameof(BrowserTabViewModel.WorkspaceId)) QueueSessionSnapshot();
    }

    private void QueueSessionSnapshot()
    {
        if (!_services.Settings.Current.Browser.RestoreLastSession) return;
        if (_sessionJournalPaused)
        {
            // Do not replace the previous session with the automatic start-page
            // placeholder. A real public-page change, however, is the current
            // run's newest state and must be journaled immediately.
            if (!Tabs.Any(x => !x.IsPrivate && !x.IsStartPage)) return;
            _sessionJournalPaused = false;
        }
        _sessionJournalTimer.Stop();
        _sessionJournalTimer.Start();
    }

    private void UpdateBookmarkState() => IsCurrentPageBookmarked = SelectedTab is not null && _services.Bookmarks.Contains(SelectedTab.Url);

    [RelayCommand]
    private void CreateTab() => CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current));

    [RelayCommand]
    private void CreatePrivateTab() => CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current), true);

    public void CreateTab(string url, bool isPrivate = false, string? workspaceId = null)
    {
        var target = _services.Workspaces.Find(workspaceId) ?? ActiveWorkspace ?? _services.Workspaces.Active;
        var tab = _services.Tabs.Create(url, isPrivate, target.Id);
        if (string.Equals(target.Id, ActiveWorkspace?.Id, StringComparison.OrdinalIgnoreCase)) SelectedTab = tab;
    }

    [RelayCommand]
    private void RestorePreviousSession()
    {
        var tabs = _pendingSessionTabs;
        if (tabs.Count == 0) return;
        var placeholder = Tabs.Count == 1 && Tabs[0].IsStartPage && !Tabs[0].IsPrivate ? Tabs[0] : null;
        ClearPendingSession();
        if (placeholder is not null) _services.Tabs.Close(placeholder);
        foreach (var tab in tabs) CreateTab(tab.Url, workspaceId: tab.WorkspaceId);
        ActiveWorkspace = _services.Workspaces.Find(_services.Workspaces.ActiveWorkspaceId) ?? Workspaces[0];
        RefreshWorkspaceTabs();
        SelectedTab = WorkspaceTabs.FirstOrDefault();
    }

    [RelayCommand]
    private void DismissPreviousSession() => ClearPendingSession();

    private void ClearPendingSession()
    {
        _sessionRestoreTimer?.Stop();
        _pendingSessionTabs = [];
        _sessionJournalPaused = false;
        OnPropertyChanged(nameof(ShowSessionRestorePrompt));
        QueueSessionSnapshot();
    }

    [RelayCommand]
    private void SelectTab(BrowserTabViewModel tab) => SelectedTab = tab;

    [RelayCommand]
    private void CloseTab(BrowserTabViewModel tab)
    {
        var index = WorkspaceTabs.IndexOf(tab);
        var wasSelected = ReferenceEquals(SelectedTab, tab);
        _services.Tabs.Close(tab);
        if (WorkspaceTabs.Count == 0) { CreateTab(BrowserHome.GetHomeUrl(_services.Settings.Current)); return; }
        if (wasSelected) SelectedTab = WorkspaceTabs[Math.Max(0, Math.Min(index, WorkspaceTabs.Count - 1))];
    }

    [RelayCommand]
    private void DuplicateTab(BrowserTabViewModel tab) => CreateTab(tab.Url, tab.IsPrivate, tab.WorkspaceId);

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
    private void SwitchWorkspace(WorkspaceDefinition workspace) => ActiveWorkspace = workspace;

    [RelayCommand]
    private async Task CreateWorkspaceAsync(string? name)
    {
        var workspace = _services.Workspaces.Create(name);
        ActiveWorkspace = workspace;
        OnPropertyChanged(nameof(CanDeleteWorkspace));
        await _services.Workspaces.SaveMetadataAsync();
    }

    [RelayCommand]
    private async Task SaveWorkspaceAsync()
    {
        if (ActiveWorkspace is null) return;
        var name = ActiveWorkspace.Name.Trim();
        ActiveWorkspace.Name = string.IsNullOrWhiteSpace(name) ? LocalizationService.Text("Workspace") : name.Length > 40 ? name.Substring(0, 40) : name;
        await _services.Workspaces.SaveMetadataAsync();
    }

    [RelayCommand]
    private async Task DeleteWorkspaceAsync()
    {
        if (ActiveWorkspace is null || Workspaces.Count <= 1) return;
        var removed = ActiveWorkspace;
        foreach (var tab in Tabs.Where(x => string.Equals(x.WorkspaceId, removed.Id, StringComparison.OrdinalIgnoreCase)).ToArray()) _services.Tabs.Close(tab);
        if (!_services.Workspaces.Remove(removed)) return;
        ActiveWorkspace = _services.Workspaces.Active;
        OnPropertyChanged(nameof(CanDeleteWorkspace));
        await _services.Workspaces.SaveMetadataAsync();
    }

    [RelayCommand]
    private void MoveSelectedTabToWorkspace(WorkspaceDefinition workspace)
    {
        if (SelectedTab is null || workspace is null) return;
        var tab = SelectedTab;
        _services.Tabs.MoveToWorkspace(tab, workspace.Id);
        ActiveWorkspace = workspace;
        RefreshWorkspaceTabs();
        SelectedTab = tab;
        QueueSessionSnapshot();
    }

    [RelayCommand]
    private async Task ToggleVerticalTabsCollapsedAsync()
    {
        _services.Settings.Current.Ui.VerticalTabsCollapsed = !_services.Settings.Current.Ui.VerticalTabsCollapsed;
        NotifyTabLayout();
        await _services.Settings.SaveAsync();
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
        ThemeService.Apply(_services.Settings.Current.Appearance, _services.Settings.Current.StartPage, _services.Settings.Current.Ui.GrayscaleMode);
        if (_services.Settings.Current.Browser.RestoreLastSession) QueueSessionSnapshot();
        else
        {
            _sessionJournalTimer.Stop();
            ClearPendingSession();
            await Task.WhenAll(_services.Session.ClearAsync(), _services.Workspaces.ClearTabsAsync());
        }
        await _services.Browser.ApplyCurrentSettingsAsync(reloadPages: true);
        foreach (var tab in Tabs.Where(x => x.IsStartPage)) tab.RefreshStartPage();
        NotifyTabLayout();
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
        if (!_services.Settings.Current.Browser.RestoreLastSession) return;
        await Task.WhenAll(
            _services.Session.SaveAsync(Tabs.Where(x => !x.IsPrivate).Select(x => (x.Url, false))),
            _services.Workspaces.SaveSnapshotAsync(Tabs.Select(x => (x.Url, x.IsPrivate, x.WorkspaceId)), ActiveWorkspace?.Id ?? _services.Workspaces.ActiveWorkspaceId));
    }

    public async Task FlushSessionJournalAsync()
    {
        _sessionJournalTimer.Stop();
        _sleepTimer.Stop();
        _sessionRestoreTimer?.Stop();
        Tabs.CollectionChanged -= Tabs_CollectionChanged;
        foreach (var tab in Tabs) tab.PropertyChanged -= Tab_SessionPropertyChanged;
        if (_services.Settings.Current.Browser.RestoreLastSession)
            await Task.WhenAll(
                _services.Session.SaveAsync(Tabs.Where(x => !x.IsPrivate).Select(x => (x.Url, false))),
                _services.Workspaces.SaveSnapshotAsync(Tabs.Select(x => (x.Url, x.IsPrivate, x.WorkspaceId)), ActiveWorkspace?.Id ?? _services.Workspaces.ActiveWorkspaceId));
        else
            await Task.WhenAll(_services.Session.ClearAsync(), _services.Workspaces.ClearTabsAsync());
    }

    private void RefreshWorkspaceTabs()
    {
        var activeId = ActiveWorkspace?.Id;
        var desired = SelectedTab is not null && string.Equals(SelectedTab.WorkspaceId, activeId, StringComparison.OrdinalIgnoreCase) ? SelectedTab : null;
        WorkspaceTabs.Clear();
        foreach (var tab in Tabs.Where(x => string.Equals(x.WorkspaceId, activeId, StringComparison.OrdinalIgnoreCase))) WorkspaceTabs.Add(tab);
        if (desired is not null && WorkspaceTabs.Contains(desired)) SelectedTab = desired;
    }

    private void NotifyTabLayout()
    {
        OnPropertyChanged(nameof(ShowHorizontalTabBar));
        OnPropertyChanged(nameof(ShowVerticalTabBar));
        OnPropertyChanged(nameof(AreVerticalTabsExpanded));
        OnPropertyChanged(nameof(VerticalTabBarWidth));
    }
}
