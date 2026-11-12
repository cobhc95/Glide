namespace Glide.Core.Commands;

/// <summary>
/// Framework-free contract for Glide's configurable pointer gesture slots. UI adapters identify
/// the physical context; this catalogue only owns stable slot/action IDs, labels and defaults.
/// </summary>
public sealed record GestureSlotDefinition(string Id, string Label, string Context, string DefaultActionId);
public sealed record GestureActionDefinition(string Id, string Label, GlideCommand Command = GlideCommand.None);

public static class GestureCatalog
{
    public const string Default = "default";
    public const string Nothing = "nothing";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string ZoomIn = "zoomIn";
    public const string ZoomOut = "zoomOut";
    public const string Fit = "fit";
    public const string Actual = "actual";
    public const string ToggleFitActual = "toggleFitActual";
    public const string Fullscreen = "fullscreen";
    public const string ContextMenu = "contextMenu";
    public const string ClearSelection = "clearSelection";
    public const string ToggleInfo = "toggleInfo";
    public const string Settings = "settings";
    public const string Pan = "pan";
    public const string CreateSelection = "createSelection";
    public const string MoveWindow = "moveWindow";
    public const string ResetOverlayZoom = "resetOverlayZoom";
    public const string BringOverlayFront = "bringOverlayFront";
    // Direction-aware wheel actions (meaningful for wheel slots only). Paired directions are
    // merged into one item whose effect follows the wheel direction: up = zoom in / previous,
    // down = zoom out / next. The "(reversed)" variants invert that order.
    public const string ZoomWheel = "zoomWheel";
    public const string BrowseWheel = "browseWheel";
    public const string ZoomWheelReversed = "zoomWheelReversed";
    public const string BrowseWheelReversed = "browseWheelReversed";

    public static IReadOnlyList<GestureActionDefinition> Actions { get; } = new GestureActionDefinition[]
    {
        new(Default, "Use Glide/default behavior"),
        new(Nothing, "Do nothing"),
        new(Next, "Next image", GlideCommand.NextImage),
        new(Previous, "Previous image", GlideCommand.PreviousImage),
        new(ZoomIn, "Zoom in", GlideCommand.ZoomIn),
        new(ZoomOut, "Zoom out", GlideCommand.ZoomOut),
        new(Fit, "Fit image", GlideCommand.FitImage),
        new(Actual, "100% / actual size", GlideCommand.ActualSize),
        new(ToggleFitActual, "Toggle Fit / 100%", GlideCommand.ToggleFitActual),
        new(Fullscreen, "Toggle fullscreen", GlideCommand.ToggleFullscreen),
        new(ContextMenu, "Open context menu"),
        new(ClearSelection, "Clear selection", GlideCommand.ClearSelection),
        new(ToggleInfo, "Toggle image information", GlideCommand.ToggleImageInfo),
        new(Settings, "Open Settings", GlideCommand.Settings),
        new(Pan, "Pan image"),
        new(CreateSelection, "Create selection"),
        new(MoveWindow, "Move native window"),
        new(ResetOverlayZoom, "Reset selected overlay zoom", GlideCommand.ResetSelectedOverlayZoom),
        new(BringOverlayFront, "Bring selected overlay to front", GlideCommand.BringSelectedOverlayFront),
        new(ZoomWheel, "Zoom in/out"),
        new(ZoomWheelReversed, "Zoom in/out (reversed)"),
        new(BrowseWheel, "Previous/next image"),
        new(BrowseWheelReversed, "Previous/next image (reversed)")
    };

    public static IReadOnlyList<GestureSlotDefinition> Slots { get; } = new GestureSlotDefinition[]
    {
        new("windowed.leftClickImage", "Windowed: left click on image", "Windowed image", Default),
        new("windowed.rightClickImage", "Windowed: right click on image", "Windowed image", Default),
        new("windowed.middleClickImage", "Windowed: middle click on image", "Windowed image", Default),
        new("windowed.doubleLeftImage", "Windowed: double-left click on image", "Windowed image", Default),
        new("windowed.doubleRightImage", "Windowed: double-right click on image", "Windowed image", Default),
        new("fullscreen.leftClickImage", "Fullscreen: left click on image", "Fullscreen image", Default),
        new("fullscreen.rightClickImage", "Fullscreen: right click on image", "Fullscreen image", Default),
        new("fullscreen.middleClickImage", "Fullscreen: middle click on image", "Fullscreen image", Default),
        new("drag.leftImage", "Left-drag on image", "Image drag", Default),
        new("drag.rightImage", "Right-drag on image", "Image drag", Default),
        new("drag.middleImage", "Middle-drag on image", "Image drag", Default),
        new("drag.leftEmpty", "Left-drag on empty background", "Background drag", Default),
        new("drag.rightEmpty", "Right-drag on empty background", "Background drag", Default),
        new("wheel.windowed", "Windowed: mouse wheel", "Windowed wheel", Default),
        new("wheel.windowedCtrl", "Windowed: Ctrl + wheel", "Windowed wheel", Default),
        new("wheel.fullscreen", "Fullscreen: mouse wheel", "Fullscreen wheel", Default),
        new("wheel.fullscreenCtrl", "Fullscreen: Ctrl + wheel", "Fullscreen wheel", Default),
        // Default = ordinary wheel behaviour. Any other action consumes the right button, so the
        // context menu does not open when it is released after scrolling.
        new("wheel.rightHeld", "Wheel while holding the right button", "Right button + wheel", Default),
        new("selection.leftClick", "Left click inside active selection", "Selection", Default),
        new("selection.rightClick", "Right click inside active selection", "Selection", Default),
        new("overlay.doubleClick", "Overlay: double click", "Window in Window", Default),
        new("overlay.middleClick", "Overlay: middle click", "Window in Window", Default),
        new("overlay.wheel", "Overlay: mouse wheel", "Window in Window", Default),
        new("overlay.ctrlWheel", "Overlay: Ctrl + wheel", "Window in Window", Default),
        new("background.doubleLeft", "Double-click empty background", "Background", Default)
    };

    /// <summary>
    /// What "Use Glide/default behavior" does for each slot, so the gesture matrix can say it
    /// instead of leaving the default opaque. Slots whose default depends on several other
    /// settings are described by the setting that governs them.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultBehavior { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["windowed.leftClickImage"] = "nothing",
        ["windowed.rightClickImage"] = "context menu",
        ["windowed.middleClickImage"] = "nothing",
        ["windowed.doubleLeftImage"] = "enter fullscreen (if enabled)",
        ["windowed.doubleRightImage"] = "nothing",
        ["fullscreen.leftClickImage"] = "next image (if fullscreen clicks navigate)",
        ["fullscreen.rightClickImage"] = "previous image (if fullscreen clicks navigate)",
        ["fullscreen.middleClickImage"] = "nothing",
        ["drag.leftImage"] = "per the left-drag setting",
        ["drag.rightImage"] = "pan a zoomed image",
        ["drag.middleImage"] = "pan (if middle-drag pans)",
        ["drag.leftEmpty"] = "move the window",
        ["drag.rightEmpty"] = "context menu",
        ["wheel.windowed"] = "previous / next image",
        ["wheel.windowedCtrl"] = "zoom (if Ctrl+wheel zooms)",
        ["wheel.fullscreen"] = "previous / next image",
        ["wheel.fullscreenCtrl"] = "zoom (if Ctrl+wheel zooms)",
        ["wheel.rightHeld"] = "same as the wheel without the right button",
        ["selection.leftClick"] = "zoom into the selection",
        ["selection.rightClick"] = "zoom out from the selection",
        ["background.doubleLeft"] = "enter fullscreen (if enabled)"
    };

    public static bool IsWheelSlot(string? slotId) =>
        slotId is not null && slotId.Contains("wheel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Actions offered for a slot. Wheel slots offer only the merged direction-aware
    /// pairs (zoom in/out, previous/next) plus their reversed order; separate single-direction
    /// "Zoom in"/"Zoom out"/"Next image"/"Previous image" items are hidden there because the
    /// merged item already encodes wheel direction. Non-wheel slots keep the single-direction
    /// items and hide the wheel-direction items, which are meaningless for clicks/drags.</summary>
    public static IReadOnlyList<GestureActionDefinition> ActionsForSlot(string? slotId)
    {
        if (IsWheelSlot(slotId))
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ZoomIn, ZoomOut, Next, Previous };
            return Actions.Where(a => !hidden.Contains(a.Id)).ToArray();
        }
        var wheelOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ZoomWheel, ZoomWheelReversed, BrowseWheel, BrowseWheelReversed };
        return Actions.Where(a => !wheelOnly.Contains(a.Id)).ToArray();
    }

    /// <summary>Migrate a stored binding to the item the slot can offer. Legacy single-direction
    /// values on wheel slots become the merged direction-aware items ("Zoom in" and "Zoom out"
    /// both become "Zoom in/out"; "Next image"/"Previous image" both become "Previous/next image").
    /// Wheel-only items stored on non-wheel slots become Default.</summary>
    public static string NormalizeActionForSlot(string? slotId, string? actionId)
    {
        var normalized = NormalizeAction(actionId);
        if (IsWheelSlot(slotId))
        {
            return normalized switch
            {
                ZoomIn or ZoomOut or ZoomWheel => ZoomWheel,
                ZoomWheelReversed => ZoomWheelReversed,
                Next or Previous or BrowseWheel => BrowseWheel,
                BrowseWheelReversed => BrowseWheelReversed,
                _ => normalized
            };
        }
        if (normalized is ZoomWheel or ZoomWheelReversed or BrowseWheel or BrowseWheelReversed) return Default;
        return normalized;
    }

    public static Dictionary<string, string> CreateDefaultMap() =>
        Slots.ToDictionary(x => x.Id, x => x.DefaultActionId, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeAction(string? actionId) =>
        Actions.Any(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase))
            ? Actions.First(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase)).Id
            : Default;

    public static string ResolveAction(string slotId, IReadOnlyDictionary<string, string>? bindings)
    {
        if (bindings is not null && bindings.TryGetValue(slotId, out var action)) return NormalizeActionForSlot(slotId, action);
        return Slots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase))?.DefaultActionId ?? Default;
    }

    public static GlideCommand CommandForAction(string actionId) =>
        Actions.FirstOrDefault(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase))?.Command ?? GlideCommand.None;
}
