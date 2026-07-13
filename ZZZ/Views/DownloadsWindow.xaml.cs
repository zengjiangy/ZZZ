using System.Diagnostics;
using System.Windows;
using ZZZ.Models;
using ZZZ.Services;

namespace ZZZ.Views;
public partial class DownloadsWindow : Window
{
    public DownloadsWindow(IDownloadService downloads) { InitializeComponent(); Owner = Application.Current.MainWindow; Grid.ItemsSource = downloads.Items; }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = (Grid.SelectedItem as DownloadItem)?.ResultPath;
        var folder = string.IsNullOrWhiteSpace(path) ? App.Services.Settings.Current.Downloads.Folder : Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
