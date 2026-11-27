namespace Glide.Core;

/// <summary>A screen-pixel rectangle (a monitor work area or a window), Avalonia-free.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Pure geometry for the whole-app Overlay window: the Size menu (25 % ... 200 %) and keeping the window,
/// and with it the bottom button bar, inside the monitor's work area (above the taskbar).
/// </summary>
public static class OverlayWindowSizing
{
    /// <summary>The Size submenu choices, in menu order.</summary>
    public static IReadOnlyList<int> SizePercents { get; } = [25, 50, 75, 100, 125, 150, 200];

    /// <summary>
    /// Window rectangle for a Size-menu choice. The size is <paramref name="percent"/> of the picture (DIP = source
    /// pixels at 100 %), capped so the whole window fits the work area, kept centred on the current window and then
    /// moved fully inside the work area.
    /// </summary>
    public static PixelBox ForPercent(int percent, int sourceWidth, int sourceHeight, PixelBox workArea, double scale, PixelBox current)
    {
        scale = scale > 0 ? scale : 1.0;
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);
        var factor = Math.Min(percent / 100.0,
            Math.Min(workArea.Width / (scale * sourceWidth), workArea.Height / (scale * sourceHeight)));
        var widthPx = Math.Max(1, (int)Math.Floor(sourceWidth * factor * scale));
        var heightPx = Math.Max(1, (int)Math.Floor(sourceHeight * factor * scale));
        var x = current.X + (current.Width - widthPx) / 2;
        var y = current.Y + (current.Height - heightPx) / 2;
        return KeepInside(new PixelBox(x, y, widthPx, heightPx), workArea);
    }

    /// <summary>
    /// Moves (and if needed shrinks, keeping the aspect ratio) a window so it lies entirely inside the work area.
    /// A window already inside is returned unchanged.
    /// </summary>
    public static PixelBox KeepInside(PixelBox window, PixelBox workArea)
    {
        var width = Math.Max(1, window.Width);
        var height = Math.Max(1, window.Height);
        if (width > workArea.Width || height > workArea.Height)
        {
            var shrink = Math.Min(workArea.Width / (double)width, workArea.Height / (double)height);
            width = Math.Max(1, (int)Math.Floor(width * shrink));
            height = Math.Max(1, (int)Math.Floor(height * shrink));
        }
        var x = Math.Clamp(window.X, workArea.X, workArea.Right - width);
        var y = Math.Clamp(window.Y, workArea.Y, workArea.Bottom - height);
        return new PixelBox(x, y, width, height);
    }

    /// <summary>
    /// The size to give the Overlay window when it is entered again with a remembered placement: the remembered size, never larger
    /// than the work area (shrunk with its aspect ratio) and never smaller than the minimum. A size that is not a finite positive
    /// number yields null (the caller uses the default placement instead).
    /// </summary>
    public static (double Width, double Height)? RestoreSize(double rememberedWidth, double rememberedHeight, PixelBox workArea, double scale,
        double minWidth = 80, double minHeight = 80)
    {
        if (!double.IsFinite(rememberedWidth) || !double.IsFinite(rememberedHeight) || rememberedWidth <= 0 || rememberedHeight <= 0) return null;
        scale = scale > 0 ? scale : 1.0;
        double width = rememberedWidth, height = rememberedHeight;
        var maxWidth = Math.Max(minWidth, workArea.Width / scale);
        var maxHeight = Math.Max(minHeight, workArea.Height / scale);
        if (width > maxWidth || height > maxHeight)
        {
            var shrink = Math.Min(maxWidth / width, maxHeight / height);
            width *= shrink;
            height *= shrink;
        }
        return (Math.Max(minWidth, width), Math.Max(minHeight, height));
    }

    /// <summary>
    /// The Size-menu choice the window currently matches (within about 1.5 % or 2 px), or null when the window was
    /// resized to something else. A choice capped by the work area matches when the window is at that cap.
    /// </summary>
    public static int? CurrentPercent(PixelBox window, int sourceWidth, int sourceHeight, PixelBox workArea, double scale, int? lastChosen = null)
    {
        if (window.Width <= 0 || sourceWidth <= 0) return null;
        // Several large choices can be capped to the same size; the one the user picked last wins, then the smallest.
        if (lastChosen is { } chosen && Matches(chosen, window, sourceWidth, sourceHeight, workArea, scale)) return chosen;
        foreach (var percent in SizePercents)
            if (Matches(percent, window, sourceWidth, sourceHeight, workArea, scale)) return percent;
        return null;
    }

    private static bool Matches(int percent, PixelBox window, int sourceWidth, int sourceHeight, PixelBox workArea, double scale)
    {
        var expected = ForPercent(percent, sourceWidth, sourceHeight, workArea, scale, window).Width;
        return Math.Abs(expected - window.Width) <= Math.Max(2.0, expected * 0.015);
    }
}
