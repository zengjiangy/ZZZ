using System.Collections.ObjectModel;
using ZZZ.ViewModels;

namespace ZZZ.Services;

public interface ITabService
{
    ObservableCollection<BrowserTabViewModel> Items { get; }
    BrowserTabViewModel Create(string url, bool isPrivate = false, string? workspaceId = null);
    void Move(BrowserTabViewModel tab, int destinationIndex);
    void MoveWithinWorkspace(BrowserTabViewModel tab, int destinationIndex);
    void MoveToWorkspace(BrowserTabViewModel tab, string workspaceId);
    int Close(BrowserTabViewModel tab);
    void CloseOthers(BrowserTabViewModel tab);
    void CloseToRight(BrowserTabViewModel tab);
}

public sealed class TabService(AppServices services) : ITabService
{
    public ObservableCollection<BrowserTabViewModel> Items { get; } = [];
    public BrowserTabViewModel Create(string url, bool isPrivate = false, string? workspaceId = null)
    {
        var targetWorkspace = string.IsNullOrWhiteSpace(workspaceId) ? services.Workspaces.ActiveWorkspaceId : workspaceId!;
        var tab = new BrowserTabViewModel(services, url, isPrivate, targetWorkspace);
        var lastWorkspaceIndex = Items.Select((item, index) => (item, index)).Where(x => string.Equals(x.item.WorkspaceId, targetWorkspace, StringComparison.OrdinalIgnoreCase)).Select(x => x.index).DefaultIfEmpty(-1).Last();
        if (lastWorkspaceIndex < 0 || lastWorkspaceIndex == Items.Count - 1) Items.Add(tab);
        else Items.Insert(lastWorkspaceIndex + 1, tab);
        return tab;
    }
    public void MoveWithinWorkspace(BrowserTabViewModel tab, int destinationIndex)
    {
        var siblings = Items.Where(x => string.Equals(x.WorkspaceId, tab.WorkspaceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var sourceWorkspaceIndex = Array.IndexOf(siblings, tab);
        if (sourceWorkspaceIndex < 0 || siblings.Length < 2) return;
        destinationIndex = Math.Max(0, Math.Min(destinationIndex, siblings.Length - 1));
        if (sourceWorkspaceIndex == destinationIndex) return;
        Move(tab, Items.IndexOf(siblings[destinationIndex]));
    }
    public void MoveToWorkspace(BrowserTabViewModel tab, string workspaceId)
    {
        if (!Items.Contains(tab) || string.IsNullOrWhiteSpace(workspaceId) || string.Equals(tab.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)) return;
        var sourceIndex = Items.IndexOf(tab);
        var lastTargetIndex = Items.Select((item, index) => (item, index))
            .Where(x => !ReferenceEquals(x.item, tab) && string.Equals(x.item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.index).DefaultIfEmpty(-1).Last();
        tab.WorkspaceId = workspaceId;
        if (lastTargetIndex < 0) Move(tab, Items.Count - 1);
        else Move(tab, Math.Min(Items.Count - 1, sourceIndex < lastTargetIndex ? lastTargetIndex : lastTargetIndex + 1));
    }
    public void Move(BrowserTabViewModel tab, int destinationIndex)
    {
        var sourceIndex = Items.IndexOf(tab);
        if (sourceIndex < 0 || Items.Count < 2) return;
        destinationIndex = Math.Max(0, Math.Min(destinationIndex, Items.Count - 1));
        if (sourceIndex != destinationIndex) Items.Move(sourceIndex, destinationIndex);
    }
    public int Close(BrowserTabViewModel tab)
    {
        var index = Items.IndexOf(tab);
        if (index < 0) return -1;
        services.Browser.Close(tab);
        Items.RemoveAt(index);
        return index;
    }
    public void CloseOthers(BrowserTabViewModel tab)
    {
        foreach (var other in Items.Where(x => x != tab && string.Equals(x.WorkspaceId, tab.WorkspaceId, StringComparison.OrdinalIgnoreCase)).ToArray()) Close(other);
    }
    public void CloseToRight(BrowserTabViewModel tab)
    {
        var siblings = Items.Where(x => string.Equals(x.WorkspaceId, tab.WorkspaceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var index = Array.IndexOf(siblings, tab);
        foreach (var other in siblings.Skip(index + 1).ToArray()) Close(other);
    }
}

public interface IMouseGestureService
{
    bool IsEnabled { get; }
    void Attach(System.Windows.IInputElement surface);
}

// Reserved extension point: the default implementation is deliberately inert.
public sealed class NullMouseGestureService : IMouseGestureService
{
    public bool IsEnabled => false;
    public void Attach(System.Windows.IInputElement surface) { }
}
