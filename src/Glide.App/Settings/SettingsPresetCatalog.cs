using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// Stock interaction presets. Presets only touch interaction/hotkey policy and always start from
/// Glide's canonical gesture defaults, so stale advanced overrides cannot leak between presets.
/// </summary>
public static class SettingsPresetCatalog
{
    public static readonly string[] Names =
    {
        "Glide default",
        "Windows Photos",
        "IrfanView",
        "nomacs",
        "FastStone",
        "XnView MP"
    };

    public static GlideSettingsState Apply(string name, GlideSettingsState current)
    {
        var s = current.CloneState();
        s.Gestures = GestureCatalog.CreateDefaultMap();
        // Behaviour and keyboard profiles deliberately share the same opt-in preset name.
        // Glide default remains the startup/default state; no compatibility profile is auto-applied.
        s.Hotkeys = HotkeyCatalog.CreatePresetMap(name);

        // Start from Glide interaction defaults while preserving unrelated user choices such as
        // theme, external programs, status composition and window placement.
        var defaults = new GlideSettingsState();
        s.DoubleClickFullscreen = defaults.DoubleClickFullscreen;
        s.DoubleClickExitFullscreen = defaults.DoubleClickExitFullscreen;
        s.FullscreenClickNavigation = defaults.FullscreenClickNavigation;
        s.WindowedWheelZoom = defaults.WindowedWheelZoom;
        s.InvertWheelDirection = defaults.InvertWheelDirection;
        s.LeftDragMode = defaults.LeftDragMode;
        s.LeftWindowDragBehavior = defaults.LeftWindowDragBehavior;
        s.RightDragBehavior = defaults.RightDragBehavior;
        s.RightDragPan = defaults.RightDragPan;
        s.SelectionClickZoom = defaults.SelectionClickZoom;
        s.SelectionRightClickZoomOut = defaults.SelectionRightClickZoomOut;
        s.BackgroundDragWindow = defaults.BackgroundDragWindow;
        s.CtrlWheelZoom = defaults.CtrlWheelZoom;
        s.MiddleDragPan = defaults.MiddleDragPan;

        switch (name)
        {
            case "Windows Photos":
                s.WindowedWheelZoom = true;
                s.LeftDragMode = "Pan image";
                s.SelectionClickZoom = false;
                s.SelectionRightClickZoomOut = false;
                s.RightDragPan = false;
                s.RightDragBehavior = "Do nothing";
                break;
            case "IrfanView":
                s.WindowedWheelZoom = false;
                s.LeftDragMode = "Create selection (Glide)";
                s.RightDragPan = true;
                s.RightDragBehavior = "Smart (pan image / move window)";
                break;
            case "nomacs":
                s.WindowedWheelZoom = true;
                s.LeftDragMode = "Pan image";
                s.RightDragPan = false;
                s.RightDragBehavior = "Do nothing";
                s.SelectionClickZoom = false;
                s.SelectionRightClickZoomOut = false;
                break;
            case "FastStone":
                s.WindowedWheelZoom = false;
                s.LeftDragMode = "Pan image";
                s.RightDragPan = true;
                s.RightDragBehavior = "Smart (pan image / move window)";
                s.SelectionClickZoom = false;
                break;
            case "XnView MP":
                // XnView MP's familiar browsing profile uses wheel for image navigation and
                // Ctrl+wheel for zoom. Keep that distinction explicit.
                s.WindowedWheelZoom = false;
                s.CtrlWheelZoom = true;
                s.LeftDragMode = "Pan image";
                s.RightDragPan = true;
                s.RightDragBehavior = "Smart (pan image / move window)";
                s.SelectionClickZoom = false;
                break;
        }
        return s;
    }
}
