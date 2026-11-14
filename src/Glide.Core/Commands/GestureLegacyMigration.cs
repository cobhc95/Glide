namespace Glide.Core.Commands;

/// <summary>
/// The 4.7 mouse toggles that 4.8.0 folds into gesture rows. Every value defaults to the old stock
/// value, which is also the new built-in "Default" behaviour of the matching rows.
/// </summary>
public sealed record LegacyMouseToggles
{
    public bool DoubleClickFullscreen { get; init; } = true;
    public bool DoubleClickExitFullscreen { get; init; }
    public bool FullscreenClickNavigation { get; init; } = true;
    public bool WindowedWheelZoom { get; init; }
    public bool CtrlWheelZoom { get; init; } = true;
    public string LeftDragMode { get; init; } = "Create selection (Glide)";
    public bool SelectionClickZoom { get; init; } = true;
    public bool SelectionRightClickZoomOut { get; init; } = true;
    public bool MiddleDragPan { get; init; }
    public bool OverlayWheelZoom { get; init; } = true;
    public bool OverlayRightDragPan { get; init; } = true;
    public string SlideshowLeftClickAction { get; init; } = "Pause / resume slideshow";
    public bool SlideshowRightClickStops { get; init; } = true;
    public bool DoubleClickTabCloses { get; init; } = true;
    public bool MiddleClickTabCloses { get; init; } = true;
    public string ExplorerOpenImagesWith { get; init; } = ExplorerCurrentTab;

    public const string ExplorerCurrentTab = "Double-click: current tab, middle-click: new tab";
    public const string ExplorerNewTab = "Double-click: new tab, middle-click: current tab";
}

/// <summary>
/// One-time conversion of the folded 4.7 mouse toggles into explicit gesture bindings, so a user's
/// behaviour is identical after the upgrade. A slot that already holds an explicit (non-Default)
/// binding is never overwritten: in 4.7 an explicit gesture binding already won over the toggle.
/// </summary>
public static class GestureLegacyMigration
{
    public static void Apply(IDictionary<string, string> gestures, LegacyMouseToggles toggles)
    {
        void Set(string slot, string action)
        {
            if (gestures.TryGetValue(slot, out var current) && !string.Equals(GestureCatalog.NormalizeAction(current), GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
                return;
            gestures[slot] = action;
        }

        if (!toggles.DoubleClickFullscreen)
        {
            Set("windowed.doubleLeftImage", GestureCatalog.Nothing);
            Set("background.doubleLeft", GestureCatalog.Nothing);
        }
        if (toggles.DoubleClickExitFullscreen) Set("fullscreen.doubleLeftImage", GestureCatalog.Fullscreen);
        if (!toggles.FullscreenClickNavigation)
        {
            Set("fullscreen.leftClickImage", GestureCatalog.PassThrough);
            Set("fullscreen.rightClickImage", GestureCatalog.PassThrough);
        }
        // 4.7: zoom = WindowedWheelZoom || (Ctrl && CtrlWheelZoom), in windowed AND fullscreen.
        if (toggles.WindowedWheelZoom)
        {
            Set("wheel.windowed", GestureCatalog.ZoomWheel);
            Set("wheel.fullscreen", GestureCatalog.ZoomWheel);
            Set("wheel.windowedCtrl", GestureCatalog.ZoomWheel);
            Set("wheel.fullscreenCtrl", GestureCatalog.ZoomWheel);
        }
        else if (!toggles.CtrlWheelZoom)
        {
            Set("wheel.windowedCtrl", GestureCatalog.BrowseWheel);
            Set("wheel.fullscreenCtrl", GestureCatalog.BrowseWheel);
        }
        // The overlay Ctrl+wheel used the same toggle, independently of windowed wheel zoom.
        if (!toggles.CtrlWheelZoom) Set("overlay.ctrlWheel", GestureCatalog.Nothing);
        switch (toggles.LeftDragMode)
        {
            case "Pan image": Set("drag.leftImage", GestureCatalog.Pan); break;
            case "Move window": Set("drag.leftImage", GestureCatalog.MoveWindow); break;
        }
        if (!toggles.SelectionClickZoom) Set("selection.leftClick", GestureCatalog.PassThrough);
        if (!toggles.SelectionRightClickZoomOut) Set("selection.rightClick", GestureCatalog.PassThrough);
        if (toggles.MiddleDragPan) Set("drag.middleImage", GestureCatalog.Pan);
        if (!toggles.OverlayWheelZoom) Set("overlay.wheel", GestureCatalog.Nothing);
        if (!toggles.OverlayRightDragPan) Set("overlay.rightDrag", GestureCatalog.Nothing);
        switch (toggles.SlideshowLeftClickAction)
        {
            case "Next image": Set("slideshow.leftClick", GestureCatalog.Next); break;
            case "Previous image": Set("slideshow.leftClick", GestureCatalog.Previous); break;
            case "Stop slideshow": Set("slideshow.leftClick", GestureCatalog.StopSlideshow); break;
            // 4.7 "Do nothing" let the click fall through to the ordinary picture handling.
            case "Do nothing": Set("slideshow.leftClick", GestureCatalog.PassThrough); break;
        }
        if (!toggles.SlideshowRightClickStops) Set("slideshow.rightClick", GestureCatalog.PassThrough);
        if (!toggles.DoubleClickTabCloses) Set("tabs.doubleClick", GestureCatalog.Nothing);
        if (!toggles.MiddleClickTabCloses) Set("tabs.middleClick", GestureCatalog.Nothing);
        if (string.Equals(toggles.ExplorerOpenImagesWith, LegacyMouseToggles.ExplorerNewTab, StringComparison.OrdinalIgnoreCase))
        {
            Set("explorer.doubleClick", GestureCatalog.OpenInNewTab);
            Set("explorer.middleClick", GestureCatalog.OpenInCurrentTab);
        }
    }
}
