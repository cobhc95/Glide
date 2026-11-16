using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// Canonical list of user-configurable title/tab-bar utility buttons. Stable IDs are persisted in
/// settings so users can freely reorder/remove buttons without coupling persistence to control names.
/// Every button is a master-registry action (<see cref="AppActionRegistry"/>): its command comes from the
/// registry, and every registry command with an icon is offered automatically (5.0.x), so a new feature
/// that declares an icon can be placed on the title bar without touching this file.
/// </summary>
public sealed record TitleBarButtonDefinition(string Id, string Label, string IconKind, GlideCommand Command)
{
    /// <summary>The <see cref="AppActionRegistry"/> action this button runs.</summary>
    public string ActionId { get; init; } = Id;
}

public static class TitleBarButtonCatalog
{
    /// <summary>
    /// The fresh-install title bar. 5.0.0 adds browser-style Back / Forward (nav.back / nav.forward) in front; image
    /// Previous / Next stay available as optional buttons (the status bar and the arrow keys have them).
    /// </summary>
    public static readonly string[] DefaultButtonIds =
    [
        "nav.back", "nav.forward", "folder.previous", "folder.next", "file.open", "file.locate", "window.alwaysOnTop", "settings"
    ];

    /// <summary>The default before Back / Forward. A saved layout that is exactly this list moves to
    /// <see cref="DefaultButtonIds"/> once (settings schema 9); any other saved layout is the user's and is kept.</summary>
    public static readonly string[] PreviousDefaultButtonIds =
    [
        "folder.previous", "folder.next", "file.open", "file.locate", "window.alwaysOnTop", "settings"
    ];

    /// <summary>Title-bar buttons whose enabled state follows the active tab's history.</summary>
    public static readonly string[] HistoryButtonIds = ["nav.back", "nav.forward"];

    /// <summary>The 22 historical buttons: persisted button id -> registry action id, with their original label and icon.</summary>
    private static readonly (string Id, string ActionId, string Label, string Icon)[] Legacy =
    [
        ("window.alwaysOnTop", "window.alwaysTop", "Always on top", "AlwaysOnTop"),
        ("file.open", "file.open", "Open file", "Open"),
        ("file.print", "file.print", "Print", "Print"),
        ("file.locate", "file.showInExplorer", LocateLabel(ShowInExplorerTargets.GlideTab), "FolderLocate"),
        ("folder.previous", "folder.previous", "Previous folder", "FolderPrevious"),
        ("folder.next", "folder.next", "Next folder", "FolderNext"),
        ("home", "workspace.home", "Home", "Home"),
        ("tab.new", "tab.new", "New tab", "Plus"),
        ("image.previous", "nav.previous", "Previous image", "Previous"),
        ("image.next", "nav.next", "Next image", "Next"),
        ("image.first", "nav.first", "First image", "First"),
        ("image.last", "nav.last", "Last image", "Last"),
        ("view.fit", "view.fit", "Fit image", "Fit"),
        ("view.actual", "view.actual", "100% / actual size", "Actual"),
        ("view.zoomIn", "view.zoomIn", "Zoom in", "ZoomIn"),
        ("view.zoomOut", "view.zoomOut", "Zoom out", "ZoomOut"),
        ("view.fullscreen", "view.fullscreen", "Fullscreen", "Fullscreen"),
        ("image.info", "view.info", "Image information", "Info"),
        ("overlay.add", "overlay.add", "Add overlay", "OverlayAdd"),
        ("slideshow.toggle", "slideshow.startPause", "Slideshow play / pause", "Play"),
        ("window.transparency", "window.transparency", "Window transparency", "WindowOpacity"),
        ("settings", "app.settings", "Settings", "Settings"),
    ];

    public static IReadOnlyList<TitleBarButtonDefinition> All { get; } = Build();

    private static TitleBarButtonDefinition[] Build()
    {
        var result = new List<TitleBarButtonDefinition>();
        foreach (var (id, actionId, label, icon) in Legacy)
        {
            var action = AppActionRegistry.Find(actionId) ?? throw new InvalidOperationException($"Title-bar button {id} names unknown action {actionId}");
            result.Add(new TitleBarButtonDefinition(id, label, icon, action.Command) { ActionId = action.Id });
        }
        var covered = result.Select(r => r.ActionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var action in AppActionRegistry.All)
        {
            if (action.Kind != AppActionKind.Command || action.Icon is null || action.Destructive || covered.Contains(action.Id)) continue;
            if (action.AliasOf is not null && covered.Contains(action.AliasOf)) continue;
            var definition = new TitleBarButtonDefinition(action.Id, action.Label, action.Icon, action.Command) { ActionId = action.Id };
            // Back / Forward lead the "Images and tabs" group of the customiser (before Home and the image arrows).
            if (HistoryButtonIds.Contains(action.Id, StringComparer.OrdinalIgnoreCase))
            {
                var home = result.FindIndex(r => r.Id == "home");
                result.Insert(home < 0 ? result.Count : home, definition);
            }
            else result.Add(definition);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Label of the Show-in-Explorer button for the chosen target (the button's tooltip follows the
    /// <see cref="GlideSettingsState.ShowInExplorerTarget"/> setting; the catalogue lists the default).
    /// </summary>
    public static string LocateLabel(string? target) => ShowInExplorerTargets.IsAlternative(target)
        ? "Show in alternative file manager"
        : ShowInExplorerTargets.IsWindowsExplorer(target)
        ? "Show in File Explorer (select current image)"
        : "Show in Explorer tab (select current image)";

    /// <summary>Display order of the groups used to organise the button list in the customiser.</summary>
    public static readonly string[] Categories =
    [
        "Window", "Files and folders", "Images and tabs", "View", "Overlay and slideshow", "App"
    ];

    public static string CategoryOf(string id) => id switch
    {
        "window.alwaysOnTop" or "window.transparency" or "view.fullscreen" => "Window",
        "file.open" or "file.print" or "file.locate" or "folder.previous" or "folder.next" => "Files and folders",
        "nav.back" or "nav.forward" or "home" or "tab.new" or "image.previous" or "image.next" or "image.first" or "image.last" => "Images and tabs",
        "view.fit" or "view.actual" or "view.zoomIn" or "view.zoomOut" or "image.info" => "View",
        "overlay.add" or "slideshow.toggle" => "Overlay and slideshow",
        "settings" => "App",
        _ => AppActionRegistry.Find(id)?.Category switch
        {
            "Application" => "Window",
            "File" or "External" or "Explorer" => "Files and folders",
            "Navigation" or "Tabs" or "Workspace" => "Images and tabs",
            "Viewing" or "Selection" => "View",
            "Overlays" or "Slideshow" => "Overlay and slideshow",
            _ => "App"
        }
    };

    private static readonly Dictionary<string, TitleBarButtonDefinition> ById =
        All.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? ids)
    {
        var supplied = ids?.ToArray() ?? Array.Empty<string>();
        // 4.2.0 explorer-removal regression could persist an empty strip. Treat that state as
        // "restore defaults"; users can still remove/reorder individual buttons normally.
        if (supplied.Length == 0) supplied = DefaultButtonIds;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in supplied)
            if (ById.ContainsKey(id) && seen.Add(id)) result.Add(id);
        return result;
    }

    public static TitleBarButtonDefinition? Find(string id) => ById.TryGetValue(id, out var value) ? value : null;
}
