using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// Canonical list of user-configurable title/tab-bar utility buttons. Stable IDs are persisted in
/// settings so users can freely reorder/remove buttons without coupling persistence to control names.
/// Keep this list compact and explicit: every item must map to a real runtime action and vector icon.
/// </summary>
public sealed record TitleBarButtonDefinition(string Id, string Label, string IconKind, GlideCommand Command);

public static class TitleBarButtonCatalog
{
    public static readonly string[] DefaultButtonIds =
    [
        "folder.previous", "folder.next", "file.open", "file.locate", "window.alwaysOnTop", "settings"
    ];

    public static IReadOnlyList<TitleBarButtonDefinition> All { get; } = new TitleBarButtonDefinition[]
    {
        new("window.alwaysOnTop", "Always on top", "AlwaysOnTop", GlideCommand.ToggleAlwaysOnTop),
        new("file.open", "Open file", "Open", GlideCommand.OpenFile),
        new("file.print", "Print", "Print", GlideCommand.Print),
        new("file.locate", "Show in File Explorer (select current image)", "FolderLocate", GlideCommand.OpenContainingFolder),
        new("folder.previous", "Previous folder", "FolderPrevious", GlideCommand.PreviousFolder),
        new("folder.next", "Next folder", "FolderNext", GlideCommand.NextFolder),
        new("home", "Home", "Home", GlideCommand.Home),
        new("tab.new", "New tab", "Plus", GlideCommand.NewTab),
        new("image.previous", "Previous image", "Previous", GlideCommand.PreviousImage),
        new("image.next", "Next image", "Next", GlideCommand.NextImage),
        new("image.first", "First image", "First", GlideCommand.FirstImage),
        new("image.last", "Last image", "Last", GlideCommand.LastImage),
        new("view.fit", "Fit image", "Fit", GlideCommand.FitImage),
        new("view.actual", "100% / actual size", "Actual", GlideCommand.ActualSize),
        new("view.zoomIn", "Zoom in", "ZoomIn", GlideCommand.ZoomIn),
        new("view.zoomOut", "Zoom out", "ZoomOut", GlideCommand.ZoomOut),
        new("view.fullscreen", "Fullscreen", "Fullscreen", GlideCommand.ToggleFullscreen),
        new("image.info", "Image information", "Info", GlideCommand.ToggleImageInfo),
        new("overlay.add", "Add overlay", "OverlayAdd", GlideCommand.AddOverlay),
        new("slideshow.toggle", "Slideshow play / pause", "Play", GlideCommand.StartPauseSlideshow),
        new("window.transparency", "Window transparency", "WindowOpacity", GlideCommand.ToggleTransparency),
        new("settings", "Settings", "Settings", GlideCommand.Settings),
    };

    /// <summary>Display order of the groups used to organise the button list in the customiser.</summary>
    public static readonly string[] Categories =
    [
        "Window", "Files and folders", "Images and tabs", "View", "Overlay and slideshow", "App"
    ];

    public static string CategoryOf(string id) => id switch
    {
        "window.alwaysOnTop" or "window.transparency" or "view.fullscreen" => "Window",
        "file.open" or "file.print" or "file.locate" or "folder.previous" or "folder.next" => "Files and folders",
        "home" or "tab.new" or "image.previous" or "image.next" or "image.first" or "image.last" => "Images and tabs",
        "view.fit" or "view.actual" or "view.zoomIn" or "view.zoomOut" or "image.info" => "View",
        "overlay.add" or "slideshow.toggle" => "Overlay and slideshow",
        _ => "App"
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
