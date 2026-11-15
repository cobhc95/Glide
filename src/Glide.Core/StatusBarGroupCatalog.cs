using Glide.Core.Commands;

namespace Glide.Core;

/// <summary>
/// what the status bar shows, in order. Two kinds of entries:
/// <list type="bullet">
/// <item>the nine built-in button groups (zoom, fit, ...); a group never splits, the adaptive layout moves whole groups;</item>
/// <item>extra single buttons for any real Glide action from <see cref="AppActionRegistry"/> that has an icon
/// (stored as <c>a:&lt;action id&gt;</c>, like the title-bar customizer). None are shown by default.</item>
/// </list>
/// Stored as an ordered, comma-separated list of the shown ids; an empty value means the default layout
/// (every group, default order, no extra buttons) and "none" means nothing is shown.
/// </summary>
public static class StatusBarGroupCatalog
{
    public const string ActionPrefix = "a:";

    public sealed record Group(string Id, string Label);

    /// <summary>One available extra button: a registry action with an icon.</summary>
    public sealed record ActionButton(string Id, string ActionId, string Label, string Icon, string Category);

    public static IReadOnlyList<Group> All { get; } =
    [
        new("zoom", "Zoom in / out"),
        new("fit", "Fit modes"),
        new("info", "Image information"),
        new("options", "Settings"),
        new("save", "Save / Save as"),
        new("overlay", "Window-in-Window overlays"),
        new("gif", "GIF playback"),
        new("slideshow", "Slideshow"),
        new("nav", "First / previous / next / last")
    ];

    public static IReadOnlyList<string> DefaultOrder { get; } = All.Select(g => g.Id).ToArray();

    /// <summary>Actions the built-in groups already provide, so they are not offered twice.</summary>
    private static readonly HashSet<string> CoveredByGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "view.zoomIn", "view.zoomOut", "view.fit", "view.fitWindow", "view.fitWidth", "view.fitHeight", "view.info", "app.settings",
        "file.save", "file.saveAs", "overlay.add", "overlay.saveLayout", "overlay.loadLayout", "overlay.clear",
        "gif.playPause", "gif.previousFrame", "gif.nextFrame", "slideshow.startPause", "slideshow.stop",
        "nav.first", "nav.previous", "nav.next", "nav.last", "status.toggle", "status.close"
    };

    /// <summary>Every extra button a user can add: non-destructive registry commands with an icon, grouped by registry category.</summary>
    public static IReadOnlyList<ActionButton> AvailableActions { get; } = AppActionRegistry.All
        .Where(a => a.Kind == AppActionKind.Command && a.Icon is not null && !a.Destructive && a.AliasOf is null && !CoveredByGroups.Contains(a.Id))
        .Select(a => new ActionButton(ActionPrefix + a.Id, a.Id, a.Label.TrimEnd('…', '.'), a.Icon!, a.Category))
        .ToArray();

    public static IReadOnlyList<string> ActionCategories { get; } = AvailableActions.Select(a => a.Category).Distinct(StringComparer.Ordinal).ToArray();

    public static ActionButton? FindAction(string? id) =>
        id is null ? null : AvailableActions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsGroup(string id) => DefaultOrder.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The shown ids in order: unknown and duplicate ids dropped; empty / null = every group in default order.</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return DefaultOrder;
        var result = new List<string>();
        foreach (var raw in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = DefaultOrder.FirstOrDefault(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase)) ?? FindAction(raw)?.Id;
            if (id is not null && !result.Contains(id, StringComparer.OrdinalIgnoreCase)) result.Add(id);
        }
        return result;
    }

    /// <summary>Stored form; the default layout is stored as an empty string.</summary>
    public static string Format(IEnumerable<string> shown)
    {
        var materialized = shown.ToList();
        if (materialized.Count == 0) return "none";
        var list = Parse(string.Join(",", materialized)).ToList();
        if (list.Count == 0) return "none";
        return list.SequenceEqual(DefaultOrder, StringComparer.Ordinal) ? string.Empty : string.Join(",", list);
    }

    public static string LabelOf(string id) =>
        All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase))?.Label ?? FindAction(id)?.Label ?? id;

    /// <summary>Icon for a customizer row: an extra button's own icon, or a representative icon for a group.</summary>
    public static string IconOf(string id) => id.ToLowerInvariant() switch
    {
        "zoom" => "ZoomIn", "fit" => "Fit", "info" => "Info", "options" => "Settings", "save" => "SaveFile",
        "overlay" => "OverlayAdd", "gif" => "Play", "slideshow" => "Slideshow", "nav" => "Next",
        _ => FindAction(id)?.Icon ?? "More"
    };
}
