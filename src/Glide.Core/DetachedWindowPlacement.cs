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
}
