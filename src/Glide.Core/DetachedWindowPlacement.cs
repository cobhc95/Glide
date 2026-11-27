namespace Glide.Core;

/// <summary>
/// Where a tab moved to a new window opens (5.1.0). Tab menu "Move tab to new window": the source window's normal
/// (not maximised) box offset by a small cascade, or centred on the work area when that does not fit. A drag-out keeps
/// the drop point under the pointer. Either way the window ends up fully inside the monitor's work area (above the taskbar).
/// </summary>
public static class DetachedWindowPlacement
{
    public const int Cascade = 32;

    /// <summary>Shrinks <paramref name="box"/> to fit the work area if needed and moves it fully inside.</summary>
    public static PixelBox ClampToWorkArea(PixelBox box, PixelBox workArea) => OverlayWindowSizing.KeepInside(box, workArea);

    private static bool Inside(PixelBox box, PixelBox area) =>
        box.X >= area.X && box.Y >= area.Y && box.Right <= area.Right && box.Bottom <= area.Bottom;

    /// <summary>Menu move: the source's normal box + cascade, else centred on the work area; always clamped.</summary>
    public static PixelBox ForMenuMove(PixelBox sourceNormal, PixelBox workArea)
    {
        var cascaded = sourceNormal with { X = sourceNormal.X + Cascade, Y = sourceNormal.Y + Cascade };
        if (Inside(cascaded, workArea)) return cascaded;
        var centred = new PixelBox(workArea.X + (workArea.Width - sourceNormal.Width) / 2, workArea.Y + (workArea.Height - sourceNormal.Height) / 2,
            sourceNormal.Width, sourceNormal.Height);
        return ClampToWorkArea(centred, workArea);
    }

    /// <summary>Drag-out: the window's top-left goes to the pointer minus <paramref name="grabX"/>/<paramref name="grabY"/>, clamped.</summary>
    public static PixelBox ForDragOut(int pointerX, int pointerY, int grabX, int grabY, int width, int height, PixelBox workArea) =>
        ClampToWorkArea(new PixelBox(pointerX - grabX, pointerY - grabY, width, height), workArea);

    /// <summary>Where the grabbed tab sits in a fresh window when the layout cannot be measured: x, y in device-independent pixels.</summary>
    public const double DefaultGrabXDip = 120, DefaultGrabYDip = 22;

    /// <summary>
    /// Held tear-off (5.2.0): the new window is opened with the tab header under the pointer. <paramref name="grabXDip"/> /
    /// <paramref name="grabYDip"/> are where that header sits in the new window and the window size is in DIPs; both are
    /// converted with the monitor's <paramref name="scale"/> (200 % on a 2778 x 1736 display), and the result is in physical pixels
    /// of <paramref name="workArea"/>, entirely inside it.
    /// </summary>
    public static PixelBox ForTearOff(int pointerX, int pointerY, double scale, double grabXDip, double grabYDip, double widthDip, double heightDip, PixelBox workArea)
    {
        var s = scale > 0 ? scale : 1.0;
        return ForDragOut(pointerX, pointerY, (int)Math.Round(grabXDip * s), (int)Math.Round(grabYDip * s),
            Math.Max(1, (int)Math.Round(widthDip * s)), Math.Max(1, (int)Math.Round(heightDip * s)), workArea);
    }

    /// <summary>
    /// A window the user has just dropped (the native move loop of a held tear-off ended): everything inside the work area.
    /// The top-left stays where it is (the grabbed tab header is under the pointer there), so a window that reaches past the right or
    /// bottom edge gives up size first, down to <paramref name="minWidth"/> x <paramref name="minHeight"/>, and is shifted only if it
    /// still does not fit; a top-left outside the work area moves in.
    /// </summary>
    public static PixelBox FitAfterDrop(PixelBox window, PixelBox workArea, int minWidth, int minHeight)
    {
        var width = Math.Max(1, Math.Min(window.Width, workArea.Width));
        var height = Math.Max(1, Math.Min(window.Height, workArea.Height));
        var x = Math.Max(window.X, workArea.X);
        var y = Math.Max(window.Y, workArea.Y);
        if (x + width > workArea.Right) width = Math.Max(Math.Min(minWidth, workArea.Width), workArea.Right - x);
        if (y + height > workArea.Bottom) height = Math.Max(Math.Min(minHeight, workArea.Height), workArea.Bottom - y);
        if (x + width > workArea.Right) x = workArea.Right - width;
        if (y + height > workArea.Bottom) y = workArea.Bottom - height;
        return new PixelBox(Math.Max(x, workArea.X), Math.Max(y, workArea.Y), width, height);
    }
}
