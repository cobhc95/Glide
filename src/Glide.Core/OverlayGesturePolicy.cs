using Glide.Core.Commands;

namespace Glide.Core;

public enum OverlayGestureAction { None, ZoomIn, ZoomOut, ResetZoom, BringToFront }

/// <summary>Pure overlay gesture resolution shared by UI handlers and tests.</summary>
public static class OverlayGesturePolicy
{
    public static OverlayGestureAction Wheel(string? configured, bool enabled, double deltaY)
    {
        var action = Normalize(configured);
        // The setting gates the default zoom gesture only. Explicit reset/front/zoom bindings
        // are commands in their own right and must remain configurable when default wheel zoom
        // is disabled.
        if (!enabled && action == GestureCatalog.Default) return OverlayGestureAction.None;
        return action switch
        {
            GestureCatalog.Nothing => OverlayGestureAction.None,
            GestureCatalog.BringOverlayFront => OverlayGestureAction.BringToFront,
            GestureCatalog.ResetOverlayZoom => OverlayGestureAction.ResetZoom,
            GestureCatalog.ZoomIn => OverlayGestureAction.ZoomIn,
            GestureCatalog.ZoomOut => OverlayGestureAction.ZoomOut,
            _ => deltaY > 0 ? OverlayGestureAction.ZoomIn : OverlayGestureAction.ZoomOut
        };
    }

    public static OverlayGestureAction Click(string? configured) => Normalize(configured) switch
    {
        GestureCatalog.Nothing => OverlayGestureAction.None,
        GestureCatalog.BringOverlayFront => OverlayGestureAction.BringToFront,
        GestureCatalog.ResetOverlayZoom or GestureCatalog.Default => OverlayGestureAction.ResetZoom,
        _ => OverlayGestureAction.None
    };

    private static string Normalize(string? action) => GestureCatalog.NormalizeAction(action ?? GestureCatalog.Default);
}
