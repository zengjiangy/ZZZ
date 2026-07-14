using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ZZZ.Models;
using ZZZ.Services;

namespace ZZZ.Views;
public partial class DownloadsWindow : Window
{
    public DownloadsWindow(IDownloadService downloads) { InitializeComponent(); Owner = Application.Current.MainWindow; Grid.ItemsSource = downloads.Items; }
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
