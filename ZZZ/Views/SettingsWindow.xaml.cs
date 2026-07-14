using System.Windows;
using Microsoft.Win32;
using ZZZ.Configuration;
using ZZZ.ViewModels;
using ZZZ.Services;
using System.Text.Json;

namespace ZZZ.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;
    private AppSettings _working;
    private string _pendingBackgroundSource = string.Empty;
    public SettingsWindow(MainViewModel main, bool showAdvanced = false)
    {
        InitializeComponent();
        _main = main;
        Owner = Application.Current.MainWindow;
        _working = Clone(main.Services.Settings.Current);
        DataContext = _working;
        LanguageBox.ItemsSource = LocalizationService.Languages;
        AppearanceBox.ItemsSource = new[] { Choice(AppearanceMode.System, "FollowSystem"), Choice(AppearanceMode.Light, "Light"), Choice(AppearanceMode.Dark, "Dark") };
        StartupPageModeBox.ItemsSource = new[] { Choice(StartupPageMode.StartPage, "BuiltInStartPage"), Choice(StartupPageMode.SearchEngineWebsite, "SearchEngineWebsite") };
        WebDarkModeBox.ItemsSource = new[] { Choice(WebContentDarkMode.Smart, "DarkSmart"), Choice(WebContentDarkMode.Force, "DarkForce") };
        LocationBox.ItemsSource = new[] { Choice(LocationPolicy.Ask, "AskCustomLocation"), Choice(LocationPolicy.Custom, "AlwaysCustomLocation"), Choice(LocationPolicy.Deny, "Deny") };
        MediaPermissionBox.ItemsSource = new[] { Choice(PermissionPolicy.Ask, "Ask"), Choice(PermissionPolicy.Deny, "Deny") };
        BookmarkStyleBox.ItemsSource = new[] { Choice(BookmarkTileStyle.Compact, "BookmarkCompact"), Choice(BookmarkTileStyle.Rounded, "BookmarkRounded"), Choice(BookmarkTileStyle.Card, "BookmarkCard") };
        UserAgentBox.ItemsSource = new[] { Choice(UserAgentPreset.DefaultDesktop, "DefaultDesktop"), Choice(UserAgentPreset.AndroidMobile, "AndroidMobile"), Choice(UserAgentPreset.IPad, "IPad"), Choice(UserAgentPreset.Custom, "Custom") };
        TranslationProviderBox.ItemsSource = new[] { Choice(TranslationProvider.Google, "GoogleTranslate"), Choice(TranslationProvider.Microsoft, "MicrosoftTranslate") };
        DownloadModeBox.ItemsSource = new[] { Choice(DownloadMode.BuiltIn, "BuiltIn"), Choice(DownloadMode.External, "External") };
        StorageModeBox.ItemsSource = new[] { Choice(DataStorageMode.LocalAppData, "LocalData"), Choice(DataStorageMode.Portable, "PortableMode"), Choice(DataStorageMode.Custom, "CustomData") };
        SearchEngineBox.ItemsSource = _working.SearchEngines;
        CurrentDataPathText.Text = $"{LocalizationService.Text("CurrentDataPath")}: {AppPaths.Root}";
        if (showAdvanced) SettingsTabs.SelectedItem = AdvancedTab;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SearchGrid.CommitEdit();
            if (_working.Storage.Mode == DataStorageMode.Custom && string.IsNullOrWhiteSpace(_working.Storage.CustomPath))
                throw new InvalidOperationException(LocalizationService.Text("CustomPathRequired"));
            if (!string.IsNullOrWhiteSpace(_pendingBackgroundSource) && File.Exists(_pendingBackgroundSource))
            {
                var relative = "start-background" + Path.GetExtension(_pendingBackgroundSource).ToLowerInvariant();
                var destination = Path.Combine(AppPaths.Root, relative);
                if (!string.Equals(Path.GetFullPath(_pendingBackgroundSource), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    File.Copy(_pendingBackgroundSource, destination, true);
                _working.StartPage.BackgroundImage = relative;
            }

            var storageChanged = _working.Storage.Mode != AppPaths.StorageMode ||
                !string.Equals((_working.Storage.CustomPath ?? string.Empty).Trim(), AppPaths.CustomStoragePath.Trim(), StringComparison.OrdinalIgnoreCase);
            _main.Services.Settings.Replace(_working);
            await _main.Services.Settings.SaveAsync();
            if (storageChanged) AppPaths.ScheduleStorageChange(_working.Storage);
            await _main.ReapplySettingsAsync();
            if (storageChanged)
                MessageBox.Show(this, LocalizationService.Text("StorageRestartRequired"), LocalizationService.Text("PortableData"), MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, LocalizationService.Text("SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private async void ExportSettings_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = "zzz-settings.json" }; if (d.ShowDialog() == true) await _main.Services.Settings.ExportAsync(d.FileName); }
    private async void ImportSettings_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() == true && ConfirmSensitive("ImportSettings")) { await _main.Services.Settings.ImportAsync(d.FileName); _working = Clone(_main.Services.Settings.Current); DataContext = _working; SearchEngineBox.ItemsSource = _working.SearchEngines; } }
    private async void ExportRules_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = "zzz-blocking-rules.txt" }; if (d.ShowDialog() == true) await _main.Services.AdBlock.ExportAsync(d.FileName); }
    private async void ImportRules_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Text|*.txt|All files|*.*" }; if (d.ShowDialog() == true && ConfirmSensitive("ImportRules")) await _main.Services.AdBlock.ImportAsync(d.FileName); }
    private void BrowseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        _pendingBackgroundSource = dialog.FileName;
        BackgroundImageBox.Text = dialog.FileName;
    }
    private void ResetBackground_Click(object sender, RoutedEventArgs e)
    {
        _pendingBackgroundSource = string.Empty;
        _working.StartPage.BackgroundImage = string.Empty;
        BackgroundImageBox.Text = string.Empty;
    }
    private void BrowseDataPath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = LocalizationService.Text("CustomDataPath"), ShowNewFolderButton = true };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _working.Storage.CustomPath = dialog.SelectedPath;
        DataContext = null;
        DataContext = _working;
    }
    private bool ConfirmSensitive(string operationKey)
    {
        var operation = LocalizationService.Text(operationKey);
        if (MessageBox.Show(this, string.Format(LocalizationService.Text("SensitiveConfirmFirst"), operation), operation, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        return MessageBox.Show(this, string.Format(LocalizationService.Text("SensitiveConfirmFinal"), operation), operation, MessageBoxButton.YesNo, MessageBoxImage.Stop) == MessageBoxResult.Yes;
    }
    private static SettingChoice<T> Choice<T>(T value, string resourceKey) where T : struct, Enum => new(value, LocalizationService.Text(resourceKey));
    private static AppSettings Clone(AppSettings source) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source)) ?? new AppSettings();
}
