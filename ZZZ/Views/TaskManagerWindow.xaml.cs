using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZZZ.ViewModels;
using ZZZ.Services;

namespace ZZZ.Views;

public partial class TaskManagerWindow : Window
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, CpuSample> _cpuSamples = [];
    private List<TaskEntry> _entries = [];

    public TaskManagerWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Owner = Application.Current.MainWindow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshEntries();
        Loaded += (_, _) => { RefreshEntries(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void RefreshEntries()
    {
        var now = DateTime.UtcNow;
        var entries = _main.Tabs.Select(tab => new TaskEntry
        {
            Title = LocalizationService.Text("TabTaskPrefix") + tab.Title,
            Subtitle = tab.IsSleeping ? LocalizationService.Text("SleepingTab") : tab.Url,
            Tab = tab,
            ProcessIdText = "—",
            CanTerminate = true
        }).ToList();

        foreach (var snapshot in _main.Services.Browser.GetProcessSnapshot())
        {
            var memory = 0L;
            var cpu = 0d;
            try
            {
                using var process = Process.GetProcessById(snapshot.ProcessId);
                process.Refresh();
                memory = process.WorkingSet64;
                var total = process.TotalProcessorTime;
                if (_cpuSamples.TryGetValue(snapshot.ProcessId, out var previous))
                {
                    var elapsed = (now - previous.Timestamp).TotalMilliseconds;
                    if (elapsed > 0) cpu = Math.Max(0, (total - previous.TotalProcessorTime).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100);
                }
                _cpuSamples[snapshot.ProcessId] = new CpuSample(total, now);
            }
            catch { }

            entries.Add(new TaskEntry
            {
                Title = ProcessTitle(snapshot.Kind),
                Subtitle = LocalizationService.Text("WebViewProcess"),
                MemoryText = FormatMemory(memory),
                CpuText = cpu.ToString("0.0") + "%",
                ProcessId = snapshot.ProcessId,
                ProcessIdText = snapshot.ProcessId.ToString(),
                CanTerminate = !snapshot.Kind.Equals("Browser", StringComparison.OrdinalIgnoreCase)
            });
        }

        _entries = entries;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filtered = query.Length == 0 ? _entries : _entries.Where(x =>
            x.Title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            x.Subtitle.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            x.ProcessIdText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        Grid.ItemsSource = filtered;
        SummaryText.Text = string.Format(LocalizationService.Text("TaskCount"), filtered.Count);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => EndTaskButton.IsEnabled = Grid.SelectedItem is TaskEntry { CanTerminate: true };

    private void EndTask_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not TaskEntry { CanTerminate: true } entry) return;
        if (entry.Tab is not null)
            _main.CloseTabCommand.Execute(entry.Tab);
        else if (entry.ProcessId is int processId)
            try { using var process = Process.GetProcessById(processId); process.Kill(); }
            catch { }
        RefreshEntries();
    }

    private static string ProcessTitle(string kind) => kind switch
    {
        "Browser" => LocalizationService.Text("BrowserProcess"),
        "Renderer" => LocalizationService.Text("RendererProcess"),
        "Gpu" => LocalizationService.Text("GpuProcess"),
        "Utility" => LocalizationService.Text("UtilityProcess"),
        _ => LocalizationService.Text("OtherProcess") + " (" + kind + ")"
    };

    private static string FormatMemory(long bytes) => bytes <= 0 ? "—" : bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024d / 1024d:0.00} GB"
        : $"{bytes / 1024d / 1024d:0.0} MB";

    private sealed class TaskEntry
    {
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string MemoryText { get; set; } = "—";
        public string CpuText { get; set; } = "—";
        public int? ProcessId { get; set; }
        public string ProcessIdText { get; set; } = "—";
        public bool CanTerminate { get; set; }
        public BrowserTabViewModel? Tab { get; set; }
    }

    private sealed class CpuSample(TimeSpan totalProcessorTime, DateTime timestamp)
    {
        public TimeSpan TotalProcessorTime { get; } = totalProcessorTime;
        public DateTime Timestamp { get; } = timestamp;
    }
}
