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
    public SettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Owner = Application.Current.MainWindow;
        _working = Clone(main.Services.Settings.Current);
        DataContext = _working;
        LanguageBox.ItemsSource = LocalizationService.Languages;
        AppearanceBox.ItemsSource = new[] { Choice(AppearanceMode.System, "FollowSystem"), Choice(AppearanceMode.Light, "Light"), Choice(AppearanceMode.Dark, "Dark") };
        WebDarkModeBox.ItemsSource = new[] { Choice(WebContentDarkMode.Smart, "DarkSmart"), Choice(WebContentDarkMode.Force, "DarkForce") };
        LocationBox.ItemsSource = MediaPermissionBox.ItemsSource = new[] { Choice(PermissionPolicy.Ask, "Ask"), Choice(PermissionPolicy.Deny, "Deny") };
        UserAgentBox.ItemsSource = new[] { Choice(UserAgentPreset.DefaultDesktop, "DefaultDesktop"), Choice(UserAgentPreset.AndroidMobile, "AndroidMobile"), Choice(UserAgentPreset.IPad, "IPad"), Choice(UserAgentPreset.Custom, "Custom") };
        DownloadModeBox.ItemsSource = new[] { Choice(DownloadMode.BuiltIn, "BuiltIn"), Choice(DownloadMode.External, "External") };
        SearchEngineBox.ItemsSource = _working.SearchEngines;
    }
    private async void Save_Click(object sender, RoutedEventArgs e) { SearchGrid.CommitEdit(); _main.Services.Settings.Replace(_working); await _main.Services.Settings.SaveAsync(); await _main.ReapplySettingsAsync(); DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private async void ExportSettings_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "JSON|*.json", FileName = "zzz-settings.json" }; if (d.ShowDialog() == true) await _main.Services.Settings.ExportAsync(d.FileName); }
    private async void ImportSettings_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog() == true) { await _main.Services.Settings.ImportAsync(d.FileName); _working = Clone(_main.Services.Settings.Current); DataContext = _working; SearchEngineBox.ItemsSource = _working.SearchEngines; } }
    private async void ExportRules_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "Text|*.txt", FileName = "zzz-blocking-rules.txt" }; if (d.ShowDialog() == true) await _main.Services.AdBlock.ExportAsync(d.FileName); }
    private async void ImportRules_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Text|*.txt|All files|*.*" }; if (d.ShowDialog() == true) await _main.Services.AdBlock.ImportAsync(d.FileName); }
    private static SettingChoice<T> Choice<T>(T value, string resourceKey) where T : struct, Enum => new(value, LocalizationService.Text(resourceKey));
    private static AppSettings Clone(AppSettings source) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source)) ?? new AppSettings();
}
