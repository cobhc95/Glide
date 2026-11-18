namespace Glide.Core;

/// <summary>What a left-press on the whole-app Overlay picture does.</summary>
public enum OverlayLeftDrag
{
    /// <summary>Let the press through (the viewport pans a zoomed picture).</summary>
    Pan,
    /// <summary>Move the Overlay window.</summary>
    MoveWindow,
    /// <summary>Swallow the press: nothing to pan and the position is locked.</summary>
    Nothing
}

/// <summary>
/// Round 3 #20 (user decision): in Overlay interactions a left-drag anywhere moves the window while the picture fits;
/// when zoomed in, left-drag pans and the window moves by dragging the bottom button bar, the optional drag strip or
/// with Alt+drag. "Lock position" stops every drag from moving the window.
/// </summary>
public static class OverlayWindowDragPolicy
{
    public static OverlayLeftDrag Resolve(bool zoomedBeyondWindow, bool altDown, bool onMoveHandle, bool positionLocked)
    {
        if (positionLocked) return zoomedBeyondWindow ? OverlayLeftDrag.Pan : OverlayLeftDrag.Nothing;
        if (altDown || onMoveHandle) return OverlayLeftDrag.MoveWindow;
        return zoomedBeyondWindow ? OverlayLeftDrag.Pan : OverlayLeftDrag.MoveWindow;
    }

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
