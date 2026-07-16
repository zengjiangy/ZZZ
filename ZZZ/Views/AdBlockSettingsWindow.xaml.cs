using System.Windows;
using System.Windows.Controls;
using ZZZ.Configuration;
using ZZZ.Models;
using ZZZ.Services;
using ZZZ.ViewModels;

namespace ZZZ.Views;

public partial class AdBlockSettingsWindow : Window
{
    private readonly MainViewModel _main;
    private AdBlockConfiguration _working;
    private bool _busy;

    public AdBlockSettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Owner = Application.Current.MainWindow;
        _working = _main.Services.AdBlock.GetConfigurationSnapshot();
        DataContext = _working;
        UpdateIntervalBox.ItemsSource = new[]
        {
            Choice(AdBlockUpdateInterval.Manual, "UpdateManual"),
            Choice(AdBlockUpdateInterval.Daily, "UpdateDaily"),
            Choice(AdBlockUpdateInterval.Weekly, "UpdateWeekly")
        };
        CustomRulesBox.Text = _main.Services.AdBlock.CustomRules;
        UpdateStatusText.Text = _main.Services.AdBlock.LastLoadError;
        RefreshView();
    }

    private void AddSubscription_Click(object sender, RoutedEventArgs e)
    {
        var name = SubscriptionNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowWarning("SubscriptionNameRequired");
            SubscriptionNameBox.Focus();
            return;
        }
        var value = SubscriptionUrlBox.Text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || value.Length > 2048)
        {
            ShowWarning("SubscriptionUrlInvalid");
            SubscriptionUrlBox.Focus();
            return;
        }
        if (_working.Subscriptions.Any(x => string.Equals(x.Url.TrimEnd('/'), uri.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        {
            ShowWarning("SubscriptionUrlInvalid");
            SubscriptionUrlBox.Focus();
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        _working.Subscriptions.Add(new AdBlockSubscription
        {
            Id = id,
            Name = name.Length > 128 ? name.Substring(0, 128) : name,
            Url = uri.AbsoluteUri,
            Enabled = true,
            IsBuiltIn = false,
            CacheFileName = "custom-" + id + ".txt"
        });
        SubscriptionNameBox.Clear();
        SubscriptionUrlBox.Clear();
        RefreshSubscriptions();
    }

    private void RemoveSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: SubscriptionRow row } || !row.CanRemove) return;
        _working.Subscriptions.RemoveAll(x => string.Equals(x.Id, row.Subscription.Id, StringComparison.Ordinal));
        RefreshSubscriptions();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            SetBusy(true);
            await _main.Services.AdBlock.SaveConfigurationAsync(_working, CustomRulesBox.Text);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, LocalizationService.Text("AdBlockSaveFailed") + Environment.NewLine + ex.Message,
                LocalizationService.Text("AdBlockSettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            SetBusy(true);
            UpdateStatusText.Text = LocalizationService.Text("UpdatingRules");
            await _main.Services.AdBlock.SaveConfigurationAsync(_working, CustomRulesBox.Text);
            var result = await _main.Services.AdBlock.UpdateNowAsync();
            var updated = result.Count(x => x.Outcome == AdBlockUpdateOutcome.Updated);
            var unchanged = result.Count(x => x.Outcome is AdBlockUpdateOutcome.NotModified or AdBlockUpdateOutcome.Skipped);
            var failed = result.Count(x => x.Outcome == AdBlockUpdateOutcome.Failed);
            UpdateStatusText.Text = string.Format(LocalizationService.Text("UpdateResultSummary"), updated, unchanged, failed);
            _working = _main.Services.AdBlock.GetConfigurationSnapshot();
            DataContext = _working;
            CustomRulesBox.Text = _main.Services.AdBlock.CustomRules;
            RefreshView();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = LocalizationService.Text("AdBlockSaveFailed") + " " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshView()
    {
        RefreshSubscriptions();
        var stats = _main.Services.AdBlock.RuleStatistics;
        RuleSummaryText.Text = string.Format(LocalizationService.Text("ActiveRuleSummary"),
            stats.NetworkBlockingRules + stats.NetworkExceptionRules,
            stats.CosmeticRules + stats.CosmeticExceptionRules,
            stats.IgnoredRules);
    }

    private void RefreshSubscriptions()
    {
        SubscriptionList.ItemsSource = _working.Subscriptions.Select(x => new SubscriptionRow(x)).ToArray();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        UpdateNowButton.IsEnabled = !busy;
        SubscriptionList.IsEnabled = !busy;
    }

    private void ShowWarning(string key) => MessageBox.Show(this, LocalizationService.Text(key),
        LocalizationService.Text("AdBlockSettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private static SettingChoice<AdBlockUpdateInterval> Choice(AdBlockUpdateInterval value, string key) =>
        new(value, LocalizationService.Text(key));

    private sealed class SubscriptionRow
    {
        public SubscriptionRow(AdBlockSubscription subscription)
        {
            Subscription = subscription;
            CanRemove = !subscription.IsBuiltIn;
            var date = subscription.LastUpdatedUtc?.ToLocalTime().ToString("g") ?? string.Empty;
            StatusText = !string.IsNullOrWhiteSpace(subscription.LastError)
                ? string.Format(LocalizationService.Text("SubscriptionStatusError"), subscription.RuleCount, subscription.LastError)
                : subscription.LastUpdatedUtc.HasValue
                    ? string.Format(LocalizationService.Text("SubscriptionStatusUpdated"), subscription.RuleCount, date)
                    : string.Format(LocalizationService.Text("SubscriptionStatusNever"), subscription.RuleCount);
        }

        public AdBlockSubscription Subscription { get; }
        public bool CanRemove { get; }
        public string StatusText { get; }
    }
}
