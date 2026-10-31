namespace ZZZ.Configuration;

/// <summary>
/// Stable identifiers shared by the main menu, customizable shortcuts and settings.
/// Persist only these IDs and gestures; localized labels stay in resource dictionaries.
/// </summary>
public static class BrowserActionIds
{
    public const string NewTab = "new-tab";
    public const string NewPrivateTab = "new-private-tab";
    public const string CloseTab = "close-tab";
    public const string ReopenClosedTab = "reopen-closed-tab";
    public const string FocusAddress = "focus-address";
    public const string RestoreInterface = "restore-interface";
    public const string Reload = "reload";
    public const string Back = "back";
    public const string Forward = "forward";
    public const string Bookmark = "bookmark";
    public const string Library = "library";
    public const string Downloads = "downloads";
    public const string TaskManager = "task-manager";
    public const string SavePdf = "save-pdf";
    public const string SaveMht = "save-mht";
    public const string Print = "print";
    public const string ReaderMode = "reader-mode";
    public const string Find = "find";
    public const string NextTab = "next-tab";
    public const string PreviousTab = "previous-tab";
    public const string SelectTabNumber = "select-tab-number";
    public const string CloseSplit = "close-split";
    public const string SplitScreen = "split-screen";
    public const string Fullscreen = "fullscreen";
    public const string DeveloperTools = "developer-tools";
    public const string ClearData = "clear-data";
    public const string Theme = "theme";
    public const string Grayscale = "grayscale";
    public const string Settings = "settings";
    public const string Exit = "exit";
}

public sealed class BrowserActionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public string DefaultGesture { get; set; } = string.Empty;
    public bool ShowInMenu { get; set; }
    public bool SeparatorBefore { get; set; }
    public bool AlwaysVisible { get; set; }
}

public sealed class MenuActionSetting
{
    public string ActionId { get; set; } = string.Empty;
    public bool Visible { get; set; } = true;
}

public sealed class ShortcutSetting
{
    public string ActionId { get; set; } = string.Empty;
    /// <summary>One or more canonical gestures separated by semicolons.</summary>
    public string Gesture { get; set; } = string.Empty;
}

public static class BrowserActionCatalog
{
    private static readonly BrowserActionDefinition[] Items =
    [
        new() { Id = BrowserActionIds.NewTab, LabelKey = "NewTab", DefaultGesture = "Ctrl+T", ShowInMenu = true },
        new() { Id = BrowserActionIds.NewPrivateTab, LabelKey = "NewPrivateTab", DefaultGesture = "Ctrl+Shift+N", ShowInMenu = true },
        new() { Id = BrowserActionIds.CloseTab, LabelKey = "CloseTab", DefaultGesture = "Ctrl+W" },
        new() { Id = BrowserActionIds.ReopenClosedTab, LabelKey = "ReopenClosedTab", DefaultGesture = "Ctrl+Shift+T" },
        new() { Id = BrowserActionIds.FocusAddress, LabelKey = "FocusAddressBar", DefaultGesture = "Ctrl+L;Alt+D" },
        new() { Id = BrowserActionIds.RestoreInterface, LabelKey = "RestoreInterface", DefaultGesture = "Ctrl+Shift+F10", ShowInMenu = true, SeparatorBefore = true, AlwaysVisible = true },
        new() { Id = BrowserActionIds.Reload, LabelKey = "Reload", DefaultGesture = "Ctrl+R;F5" },
        new() { Id = BrowserActionIds.Back, LabelKey = "Back", DefaultGesture = "Alt+Left" },
        new() { Id = BrowserActionIds.Forward, LabelKey = "Forward", DefaultGesture = "Alt+Right" },
        new() { Id = BrowserActionIds.Bookmark, LabelKey = "BookmarkPage", DefaultGesture = "Ctrl+D" },
        new() { Id = BrowserActionIds.Library, LabelKey = "Library", DefaultGesture = "Ctrl+H", ShowInMenu = true },
        new() { Id = BrowserActionIds.Downloads, LabelKey = "Downloads", DefaultGesture = "Ctrl+J", ShowInMenu = true },
        new() { Id = BrowserActionIds.TaskManager, LabelKey = "BrowserTaskManager", DefaultGesture = "Shift+Escape", ShowInMenu = true },
        new() { Id = BrowserActionIds.SavePdf, LabelKey = "SaveAsPdf", ShowInMenu = true, SeparatorBefore = true },
        new() { Id = BrowserActionIds.SaveMht, LabelKey = "SaveAsMht", ShowInMenu = true },
        new() { Id = BrowserActionIds.Print, LabelKey = "Print", DefaultGesture = "Ctrl+P", ShowInMenu = true },
        new() { Id = BrowserActionIds.ReaderMode, LabelKey = "ReadingMode", DefaultGesture = "F9", ShowInMenu = true },
        new() { Id = BrowserActionIds.Find, LabelKey = "FindInPage", DefaultGesture = "Ctrl+F", ShowInMenu = true },
        new() { Id = BrowserActionIds.NextTab, LabelKey = "NextTab", DefaultGesture = "Ctrl+Tab;Ctrl+PageDown" },
        new() { Id = BrowserActionIds.PreviousTab, LabelKey = "PreviousTab", DefaultGesture = "Ctrl+Shift+Tab;Ctrl+PageUp" },
        new() { Id = BrowserActionIds.SelectTabNumber, LabelKey = "SelectTabByNumber", DefaultGesture = "Ctrl+1-9" },
        new() { Id = BrowserActionIds.CloseSplit, LabelKey = "CloseSplit", DefaultGesture = "Ctrl+Shift+W" },
        new() { Id = BrowserActionIds.SplitScreen, LabelKey = "SplitScreen", ShowInMenu = true },
        new() { Id = BrowserActionIds.Fullscreen, LabelKey = "Fullscreen", DefaultGesture = "F11", ShowInMenu = true },
        new() { Id = BrowserActionIds.DeveloperTools, LabelKey = "DeveloperTools", DefaultGesture = "F12", ShowInMenu = true, SeparatorBefore = true },
        new() { Id = BrowserActionIds.ClearData, LabelKey = "ClearBrowsingData", ShowInMenu = true },
        new() { Id = BrowserActionIds.Theme, LabelKey = "Theme", ShowInMenu = true },
        new() { Id = BrowserActionIds.Grayscale, LabelKey = "GrayscaleMode", ShowInMenu = true },
        new() { Id = BrowserActionIds.Settings, LabelKey = "Settings", ShowInMenu = true, AlwaysVisible = true },
        new() { Id = BrowserActionIds.Exit, LabelKey = "Exit", ShowInMenu = true, SeparatorBefore = true }
    ];

    public static IReadOnlyList<BrowserActionDefinition> Definitions => Items;

    public static BrowserActionDefinition? Find(string? id) =>
        Items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public static List<MenuActionSetting> DefaultMenuSettings() =>
        Items.Where(x => x.ShowInMenu).Select(x => new MenuActionSetting { ActionId = x.Id, Visible = true }).ToList();

    public static List<ShortcutSetting> DefaultShortcutSettings() =>
        Items.Where(x => x.DefaultGesture.Length > 0).Select(x => new ShortcutSetting { ActionId = x.Id, Gesture = x.DefaultGesture }).ToList();

    public static void Normalize(AppSettings settings)
    {
        settings.Menu ??= DefaultMenuSettings();
        settings.Shortcuts ??= DefaultShortcutSettings();
        settings.Menu = settings.Menu
            .Where(x => Find(x.ActionId)?.ShowInMenu == true)
            .GroupBy(x => x.ActionId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        foreach (var item in settings.Menu)
            if (Find(item.ActionId)?.AlwaysVisible == true) item.Visible = true;
        settings.Shortcuts = settings.Shortcuts
            .Where(x => Find(x.ActionId) is not null)
            .GroupBy(x => x.ActionId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }
}
