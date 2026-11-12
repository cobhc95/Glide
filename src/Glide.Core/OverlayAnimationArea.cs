namespace Glide.Core;

/// <summary>
/// A user-drawn rectangle an animated overlay is confined to. It is stored as fractions of the
/// viewer (0..1) so it keeps covering the same part of the view when the window is resized, maximised
/// or made fullscreen. The named regions (halves/quarters) remain in <see cref="OverlayAnimationPolicy"/>.
/// </summary>
public static class OverlayAnimationArea
{
    public const string CustomRegionName = "Custom area";

    /// <summary>Smallest drawable area, as a fraction of the viewer on each axis.</summary>
    public const double MinimumFraction = 0.02;

    public static bool IsCustom(string? region) =>
        string.Equals(region?.Trim(), CustomRegionName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamps a normalized area into the viewer; null when it is empty or too small.</summary>
    public static ChromeRect? Normalize(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)) return null;
        var left = Math.Clamp(x, 0, 1);
        var top = Math.Clamp(y, 0, 1);
        var right = Math.Clamp(x + width, 0, 1);
        var bottom = Math.Clamp(y + height, 0, 1);
        if (right - left < MinimumFraction || bottom - top < MinimumFraction) return null;
        return new ChromeRect(left, top, right - left, bottom - top);
    }

    /// <summary>Converts a rectangle dragged in viewer DIPs (any corner order) to a normalized area.</summary>
    public static ChromeRect? FromDrag(double x0, double y0, double x1, double y1, double hostWidth, double hostHeight)
    {
        if (hostWidth <= 0 || hostHeight <= 0) return null;
        return Normalize(Math.Min(x0, x1) / hostWidth, Math.Min(y0, y1) / hostHeight,
                         Math.Abs(x1 - x0) / hostWidth, Math.Abs(y1 - y0) / hostHeight);
    }

    public static ChromeRect ToHost(ChromeRect area, double hostWidth, double hostHeight) =>
        new(area.X * hostWidth, area.Y * hostHeight, area.Width * hostWidth, area.Height * hostHeight);

    /// <summary>
    /// Legal range for an overlay's top-left corner so the whole overlay stays inside the area. When
    /// the overlay is larger than the area on an axis it is centred on the area there instead, and
    /// never pushed outside the viewer.
    /// </summary>
    public static ChromeRect PositionRange(ChromeRect area, double hostWidth, double hostHeight, double overlayWidth, double overlayHeight)
    {
        var host = ToHost(area, hostWidth, hostHeight);
        var (minX, maxX) = Axis(host.X, host.Width, overlayWidth, hostWidth);
        var (minY, maxY) = Axis(host.Y, host.Height, overlayHeight, hostHeight);
        return new ChromeRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static (double Min, double Max) Axis(double start, double length, double size, double hostLength)
    {
        var limit = Math.Max(0, hostLength - size);
        if (size <= length) return (Math.Clamp(start, 0, limit), Math.Clamp(start + length - size, 0, limit));
        var centred = Math.Clamp(start + (length - size) / 2, 0, limit);
        return (centred, centred);
    }
}
