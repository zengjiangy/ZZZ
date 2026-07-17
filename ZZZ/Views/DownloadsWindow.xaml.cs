using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ZZZ.Models;
using ZZZ.Services;

namespace ZZZ.Views;
public partial class DownloadsWindow : Window
{
    private readonly IDownloadService _downloads;
    public DownloadsWindow(IDownloadService downloads) { InitializeComponent(); _downloads = downloads; Owner = Application.Current.MainWindow; RefreshView(); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshView();
    private void RefreshView()
    {
        var query = SearchBox.Text.Trim();
        Grid.ItemsSource = query.Length == 0 ? _downloads.Items : _downloads.Items.Where(x => Matches(query, x.FileName, x.SourceUrl, x.ResultPath, x.MimeType, x.Status)).ToArray();
    }
    private static bool Matches(string query, params string?[] values) => values.Any(x => x is { Length: > 0 } && x.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Grid, e.OriginalSource as DependencyObject) is DataGridRow { Item: DownloadItem item }) OpenFile(item);
    }
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is DownloadItem item) OpenFile(item);
    }
    private void OpenFile(DownloadItem item)
    {
        var path = item.ResultPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = (Grid.SelectedItem as DownloadItem)?.ResultPath;
        var folder = string.IsNullOrWhiteSpace(path) ? App.Services.Settings.Current.Downloads.Folder : Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
