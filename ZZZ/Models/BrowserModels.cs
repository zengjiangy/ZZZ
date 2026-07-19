using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media;
using ZZZ.Services;

namespace ZZZ.Models;

public sealed partial class Bookmark : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool ShowOnStartPage { get; set; } = true;
    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? favicon;
    [JsonIgnore]
    public string FaviconFallback => FaviconCacheService.FallbackLetter(Title, Url);
}

public sealed partial class HistoryEntry : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? favicon;
    [JsonIgnore]
    public string FaviconFallback => FaviconCacheService.FallbackLetter(Title, Url);
}

public sealed partial class WorkspaceDefinition : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string color = "#6557C8";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<string> SavedUrls { get; set; } = [];
}

public sealed class WorkspaceTabSnapshot
{
    public string WorkspaceId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class BrowserProcessSnapshot
{
    public int ProcessId { get; set; }
    public string Kind { get; set; } = string.Empty;
}

public sealed class UserScript
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New script";
    public string Namespace { get; set; } = "ZZZ";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Match { get; set; } = "*";
    public List<string> Matches { get; set; } = [];
    public List<string> Includes { get; set; } = [];
    public List<string> Excludes { get; set; } = [];
    public List<string> Requires { get; set; } = [];
    public List<string> Grants { get; set; } = [];
    public List<string> Connects { get; set; } = [];
    public Dictionary<string, string> Resources { get; set; } = [];
    public string RunAt { get; set; } = "document-idle";
    public bool NoFrames { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string RequiredCode { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public sealed class MediaResource
{
    public string Url { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long? ContentLength { get; set; }
    public override string ToString()
    {
        var size = ContentLength is > 0 ? $" · {FormatSize(ContentLength.Value)}" : string.Empty;
        return $"{Kind.ToUpperInvariant()}{size}  {Url}";
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}

public partial class DownloadItem : ObservableObject
{
    [ObservableProperty] private string fileName = string.Empty;
    [ObservableProperty] private string sourceUrl = string.Empty;
    [ObservableProperty] private string resultPath = string.Empty;
    [ObservableProperty] private string mimeType = string.Empty;
    [ObservableProperty] private long bytesReceived;
    [ObservableProperty] private long? totalBytes;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private string interruptReason = string.Empty;
    [ObservableProperty] private DateTime startedAt = DateTime.Now;
    [ObservableProperty] private DateTime? completedAt;

    public string SizeText => FormatBytes(TotalBytes ?? (BytesReceived > 0 ? BytesReceived : null));
    public string TransferredText => TotalBytes is > 0
        ? $"{FormatBytes(BytesReceived)} / {FormatBytes(TotalBytes)}"
        : FormatBytes(BytesReceived > 0 ? BytesReceived : null);
    public string StartedAtText => StartedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string CompletedAtText => CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    partial void OnBytesReceivedChanged(long value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(TransferredText));
    }

    partial void OnTotalBytesChanged(long? value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(TransferredText));
    }

    partial void OnStartedAtChanged(DateTime value) => OnPropertyChanged(nameof(StartedAtText));
    partial void OnCompletedAtChanged(DateTime? value) => OnPropertyChanged(nameof(CompletedAtText));

    private static string FormatBytes(long? value)
    {
        if (value is not > 0) return "—";
        var bytes = value.Value;
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024d:0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }
}
