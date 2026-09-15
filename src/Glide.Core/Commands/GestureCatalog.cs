namespace Glide.Core.Commands;

/// <summary>
/// Framework-free contract for Glide's 24 configurable pointer gesture slots. UI adapters identify
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
        new(BringOverlayFront, "Bring selected overlay to front", GlideCommand.BringSelectedOverlayFront)
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
        new("selection.leftClick", "Left click inside active selection", "Selection", Default),
        new("selection.rightClick", "Right click inside active selection", "Selection", Default),
        new("overlay.doubleClick", "Overlay: double click", "Window in Window", Default),
        new("overlay.middleClick", "Overlay: middle click", "Window in Window", Default),
        new("overlay.wheel", "Overlay: mouse wheel", "Window in Window", Default),
        new("overlay.ctrlWheel", "Overlay: Ctrl + wheel", "Window in Window", Default),
        new("background.doubleLeft", "Double-click empty background", "Background", Default)
    };

    public static Dictionary<string, string> CreateDefaultMap() =>
        Slots.ToDictionary(x => x.Id, x => x.DefaultActionId, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeAction(string? actionId) =>
        Actions.Any(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase))
            ? Actions.First(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase)).Id
            : Default;

    public static string ResolveAction(string slotId, IReadOnlyDictionary<string, string>? bindings)
    {
        if (bindings is not null && bindings.TryGetValue(slotId, out var action)) return NormalizeAction(action);
        return Slots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase))?.DefaultActionId ?? Default;
    }

    public static GlideCommand CommandForAction(string actionId) =>
        Actions.FirstOrDefault(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase))?.Command ?? GlideCommand.None;
}
