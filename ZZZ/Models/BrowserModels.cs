using CommunityToolkit.Mvvm.ComponentModel;

namespace ZZZ.Models;

public sealed class Bookmark
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool ShowOnStartPage { get; set; } = true;
}

public sealed class HistoryEntry
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
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
    [ObservableProperty] private double progress;
    [ObservableProperty] private string status = "Starting";
}
