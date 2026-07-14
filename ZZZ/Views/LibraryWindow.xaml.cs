using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Text.Json;
using ZZZ.Models;
using ZZZ.ViewModels;
using ZZZ.Services;

namespace ZZZ.Views;

public partial class LibraryWindow : Window
{
    private readonly MainViewModel _main;
    private readonly ObservableCollection<UserScript> _scripts;
    public LibraryWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Owner = Application.Current.MainWindow;
        BookmarksGrid.ItemsSource = main.Services.Bookmarks.Items;
        HistoryGrid.ItemsSource = main.Services.History.Items;
        _scripts = new ObservableCollection<UserScript>(main.Services.UserScripts.Items.Select(Clone));
        ScriptsGrid.ItemsSource = _scripts;
        if (_scripts.Count > 0) ScriptsGrid.SelectedIndex = 0;
    }
    private void OpenBookmark_Click(object sender, RoutedEventArgs e) { if (BookmarksGrid.SelectedItem is Bookmark b) { _main.CreateTab(b.Url); Close(); } }
    private async void RemoveBookmark_Click(object sender, RoutedEventArgs e) { if (BookmarksGrid.SelectedItem is Bookmark b) { await _main.Services.Bookmarks.RemoveAsync(b); BookmarksGrid.Items.Refresh(); } }
    private async void SaveBookmarks_Click(object sender, RoutedEventArgs e) { BookmarksGrid.CommitEdit(); await _main.Services.Bookmarks.SaveAsync(); }
    private async void ImportBookmarks_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Bookmarks HTML|*.html;*.htm" }; if (d.ShowDialog() == true) { await _main.Services.Bookmarks.ImportHtmlAsync(d.FileName); BookmarksGrid.Items.Refresh(); } }
    private async void ExportBookmarks_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Filter = "HTML|*.html", FileName = "zzz-bookmarks.html" }; if (d.ShowDialog() == true) await _main.Services.Bookmarks.ExportHtmlAsync(d.FileName); }
    private void OpenHistory_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is HistoryEntry h) { _main.CreateTab(h.Url); Close(); } }
    private void HistoryGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenHistory_Click(sender, e);
    private async void RemoveHistory_Click(object sender, RoutedEventArgs e) { if (HistoryGrid.SelectedItem is HistoryEntry h) { await _main.Services.History.RemoveAsync(h); HistoryGrid.Items.Refresh(); } }
    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ClearDataWindow(new ZZZ.Configuration.ClearDataSelection { History = true, Cache = false, Cookies = false }, true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await _main.Services.History.ClearAsync(); HistoryGrid.Items.Refresh();
    }
    private void NewScript_Click(object sender, RoutedEventArgs e) { var s = new UserScript { Name = "New script", Match = "*", Code = "// Runs after navigation\n" }; _scripts.Add(s); ScriptsGrid.SelectedItem = s; }
    private async void ImportScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Userscripts|*.user.js;*.js|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = await _main.Services.UserScripts.ImportAsync(dialog.FileName);
            var existing = _scripts.FirstOrDefault(x => x.Namespace == imported.Namespace && x.Name == imported.Name);
            if (existing is not null) _scripts.Remove(existing);
            var copy = Clone(imported);
            _scripts.Add(copy);
            ScriptsGrid.SelectedItem = copy;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, LocalizationService.Text("Userscripts"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void RemoveScript_Click(object sender, RoutedEventArgs e) { if (ScriptsGrid.SelectedItem is UserScript s && ConfirmSensitive("RemoveScript")) _scripts.Remove(s); }
    private async void SaveScripts_Click(object sender, RoutedEventArgs e) { ScriptsGrid.CommitEdit(); await _main.Services.UserScripts.SaveAsync(_scripts); await _main.ReapplySettingsAsync(); }
    private void ScriptsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private bool ConfirmSensitive(string operationKey)
    {
        var operation = LocalizationService.Text(operationKey);
        if (MessageBox.Show(this, string.Format(LocalizationService.Text("SensitiveConfirmFirst"), operation), operation, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        return MessageBox.Show(this, string.Format(LocalizationService.Text("SensitiveConfirmFinal"), operation), operation, MessageBoxButton.YesNo, MessageBoxImage.Stop) == MessageBoxResult.Yes;
    }
    private static UserScript Clone(UserScript source) => JsonSerializer.Deserialize<UserScript>(JsonSerializer.Serialize(source)) ?? new UserScript();
}
