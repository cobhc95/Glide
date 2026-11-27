namespace Glide.Core;

/// <summary>What a left-press on the whole-app Overlay picture does.</summary>
public enum OverlayLeftDrag
{
    /// <summary>Move the Overlay window.</summary>
    MoveWindow,
    /// <summary>Swallow the press: the position is locked (a left-drag never pans in Overlay mode).</summary>
    Nothing
}

/// <summary>
/// 5.2.2 (user decision, replaces round 3 #20): in Overlay interactions a left-drag anywhere on the picture ALWAYS moves the
/// window, also when the picture is zoomed in or cropped. Panning is the right-drag (right-click, hold, drag), so the two no longer
/// duplicate each other. "Lock position" stops every left drag from moving the window (and it does not pan either). Presses on the
/// chrome (handles, buttons, slider) never reach this policy: they keep their own behaviour. Windowed interactions
/// ("Use normal windowed image interactions") keep the user's own left-drag binding and do not use this policy.
/// </summary>
public static class OverlayWindowDragPolicy
{
    public static OverlayLeftDrag Resolve(bool positionLocked) =>
        positionLocked ? OverlayLeftDrag.Nothing : OverlayLeftDrag.MoveWindow;

    /// <summary>Pixels the cursor must travel before an Overlay window drag starts moving.</summary>
    public const int DragThresholdPx = 4;

    /// <summary>
    /// Window position for a screen-space window drag. Both cursor points are SCREEN pixels, so moving the window
    /// never feeds back into the delta (window-relative coordinates would cancel out and stall the drag).
    /// Returns null while the drag has not yet passed the threshold.
    /// </summary>
    public static (int X, int Y)? DragTarget(int windowStartX, int windowStartY, int cursorStartX, int cursorStartY,
        int cursorX, int cursorY, bool alreadyMoving)
    {
        var dx = cursorX - cursorStartX;
        var dy = cursorY - cursorStartY;
        if (!alreadyMoving && Math.Abs(dx) < DragThresholdPx && Math.Abs(dy) < DragThresholdPx) return null;
        return (windowStartX + dx, windowStartY + dy);
    }
}
