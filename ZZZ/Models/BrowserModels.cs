using CommunityToolkit.Mvvm.ComponentModel;

namespace ZZZ.Models;

public sealed class Bookmark
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class HistoryEntry
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class UserScript
{
    public string Name { get; set; } = "New script";
    public bool Enabled { get; set; } = true;
    public string Match { get; set; } = "*";
    public string Code { get; set; } = string.Empty;
}

public sealed class MediaResource
{
    public string Url { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public override string ToString() => $"{Kind.ToUpperInvariant()}  {Url}";
}

public partial class DownloadItem : ObservableObject
{
    [ObservableProperty] private string fileName = string.Empty;
    [ObservableProperty] private string sourceUrl = string.Empty;
    [ObservableProperty] private string resultPath = string.Empty;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string status = "Starting";
}
