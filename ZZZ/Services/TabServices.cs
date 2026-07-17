using System.Collections.ObjectModel;
using ZZZ.ViewModels;

namespace ZZZ.Services;

public interface ITabService
{
    ObservableCollection<BrowserTabViewModel> Items { get; }
    BrowserTabViewModel Create(string url, bool isPrivate = false);
    void Move(BrowserTabViewModel tab, int destinationIndex);
    int Close(BrowserTabViewModel tab);
    void CloseOthers(BrowserTabViewModel tab);
    void CloseToRight(BrowserTabViewModel tab);
}

public sealed class TabService(AppServices services) : ITabService
{
    public ObservableCollection<BrowserTabViewModel> Items { get; } = [];
    public BrowserTabViewModel Create(string url, bool isPrivate = false)
    {
        var tab = new BrowserTabViewModel(services, url, isPrivate);
        Items.Add(tab);
        return tab;
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
        foreach (var other in Items.Where(x => x != tab).ToArray()) Close(other);
    }
    public void CloseToRight(BrowserTabViewModel tab)
    {
        var index = Items.IndexOf(tab);
        foreach (var other in Items.Skip(index + 1).ToArray()) Close(other);
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
