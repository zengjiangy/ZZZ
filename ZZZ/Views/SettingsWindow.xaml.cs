using System.Windows;
using Microsoft.Win32;
using ZZZ.Configuration;
using ZZZ.ViewModels;
using ZZZ.Services;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZZZ.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;
    private AppSettings _working = new();
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
        TranslationTargetBox.ItemsSource = LocalizationService.TranslationTargets;
        LoadTranslationTarget(_working.Browser.TranslationTargetLanguage);
        DownloadModeBox.ItemsSource = new[] { Choice(DownloadMode.BuiltIn, "BuiltIn"), Choice(DownloadMode.External, "External") };
        StorageModeBox.ItemsSource = new[] { Choice(DataStorageMode.LocalAppData, "LocalData"), Choice(DataStorageMode.Portable, "PortableMode"), Choice(DataStorageMode.Custom, "CustomData") };
        SearchEngineBox.ItemsSource = _working.SearchEngines;
        CurrentDataPathText.Text = $"{LocalizationService.Text("CurrentDataPath")}: {AppPaths.Root}";
        RefreshBackgroundColorPreview();
        if (showAdvanced) SettingsTabs.SelectedItem = AdvancedTab;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SearchGrid.CommitEdit();
            if (_working.Storage.Mode == DataStorageMode.Custom && string.IsNullOrWhiteSpace(_working.Storage.CustomPath))
                throw new InvalidOperationException(LocalizationService.Text("CustomPathRequired"));
            _working.Browser.TranslationTargetLanguage = ReadTranslationTarget();
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
    private async void ImportSettings_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() == true && ConfirmSensitive("ImportSettings")) { await _main.Services.Settings.ImportAsync(d.FileName); _working = Clone(_main.Services.Settings.Current); DataContext = _working; SearchEngineBox.ItemsSource = _working.SearchEngines; LoadTranslationTarget(_working.Browser.TranslationTargetLanguage); } }
    private async void ExportRules_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = "zzz-blocking-rules.txt" }; if (d.ShowDialog() == true) await _main.Services.AdBlock.ExportAsync(d.FileName); }
    private async void ImportRules_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Text|*.txt|All files|*.*" }; if (d.ShowDialog() == true && ConfirmSensitive("ImportRules")) await _main.Services.AdBlock.ImportAsync(d.FileName); }
    private void ManageAdBlock_Click(object sender, RoutedEventArgs e) => new AdBlockSettingsWindow(_main) { Owner = this }.ShowDialog();
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
        _working.StartPage.BackgroundColor = "#101826";
        _working.StartPage.SyncApplicationAccent = false;
        BackgroundImageBox.Text = string.Empty;
        BackgroundColorBox.Text = _working.StartPage.BackgroundColor;
        RefreshBackgroundColorPreview();
    }
    private void ChooseBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(_working.StartPage.BackgroundColor);
            dialog.Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch { }
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SetBackgroundColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }
    private void PresetBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color }) SetBackgroundColor(color);
    }
    private void BackgroundColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var value = BackgroundColorBox?.Text ?? _working.StartPage.BackgroundColor;
        if (BackgroundColorBox?.IsKeyboardFocused == true)
        {
            try
            {
                _ = (Color)ColorConverter.ConvertFromString(value);
                _working.StartPage.BackgroundColor = value;
                _working.StartPage.SyncApplicationAccent = true;
            }
            catch { }
        }
        RefreshBackgroundColorPreview();
    }
    private void SetBackgroundColor(string color)
    {
        _working.StartPage.BackgroundColor = color;
        _working.StartPage.SyncApplicationAccent = true;
        if (BackgroundColorBox is not null) BackgroundColorBox.Text = color;
        RefreshBackgroundColorPreview();
    }
    private void RefreshBackgroundColorPreview()
    {
        if (BackgroundColorPreview is null) return;
        var value = BackgroundColorBox?.Text ?? _working.StartPage.BackgroundColor;
        try { BackgroundColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch { BackgroundColorPreview.Background = Brushes.Transparent; }
    }
    private void TranslationTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomTranslationTargetBox is null) return;
        CustomTranslationTargetBox.Visibility = TranslationTargetBox.SelectedValue as string == "__custom__"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
    private void LoadTranslationTarget(string code)
    {
        var normalized = (code ?? string.Empty).Trim();
        var preset = LocalizationService.TranslationTargets.FirstOrDefault(x =>
            x.Code != "__custom__" && string.Equals(x.Code, normalized, StringComparison.OrdinalIgnoreCase));
        TranslationTargetBox.SelectedValue = preset?.Code ?? "__custom__";
        CustomTranslationTargetBox.Text = preset is null ? normalized : string.Empty;
    }
    private string ReadTranslationTarget()
    {
        var selected = TranslationTargetBox.SelectedValue as string;
        if (selected != "__custom__" && !string.IsNullOrWhiteSpace(selected)) return selected!;
        var custom = CustomTranslationTargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(custom)) throw new InvalidOperationException(LocalizationService.Text("CustomLanguageRequired"));
        return custom;
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
