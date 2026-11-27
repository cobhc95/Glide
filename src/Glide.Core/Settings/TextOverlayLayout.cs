namespace Glide.Core.Settings;

/// <summary>Text overlay placement maths over the picture (5.1.4).</summary>
public static class TextOverlayLayout
{
    /// <summary>
    /// Bottom inset of a bottom-positioned text overlay: <paramref name="gap"/> above the top edge of the status bar when it
    /// is shown and laid out, otherwise <paramref name="gap"/> above the bottom of the picture area.
    /// </summary>
    public static double BottomInset(double areaHeight, bool statusBarShown, double statusBarTop, double gap)
    {
        if (!statusBarShown || areaHeight <= 0 || statusBarTop <= 0 || statusBarTop >= areaHeight) return gap;
        return areaHeight - statusBarTop + gap;
    }

    /// <summary>
    /// Custom position (5.1.4) for an overlay of size <paramref name="w"/> x <paramref name="h"/> dropped with its top-left at
    /// (<paramref name="x"/>, <paramref name="y"/>) in a picture area <paramref name="areaW"/> x <paramref name="areaH"/>:
    /// the nearest picture anchor (thirds across, halves down) and the offset from that anchor's preset place. The drop is
    /// kept inside the area. Preset places: <paramref name="side"/> from the left/right edges (centred: middle),
    /// <paramref name="top"/> from the top, <paramref name="bottomInset"/> from the bottom (above the status bar).
    /// </summary>
    public static (string Anchor, int X, int Y) CustomFromDrop(double areaW, double areaH, double w, double h, double x, double y,
        double side, double top, double bottomInset, double edgeInset = 0)
    {
        x = ClampInside(x, areaW, w, edgeInset);
        y = ClampInside(y, areaH, h, edgeInset);
        var cx = x + w / 2; var cy = y + h / 2;
        var col = cx < areaW / 3 ? 0 : cx > areaW * 2 / 3 ? 2 : 1;
        var bottom = cy > areaH / 2;
        var anchor = (bottom ? "Bottom " : "Top ") + (col == 0 ? "left" : col == 1 ? "center" : "right");
        var px = col == 0 ? side : col == 1 ? (areaW - w) / 2 : areaW - side - w;
        var py = bottom ? areaH - bottomInset - h : top;
        return (anchor, (int)Math.Round(x - px), (int)Math.Round(y - py));
    }

    /// <summary>
    /// Round 4 B3: the offset actually drawn for an overlay of size <paramref name="w"/> x <paramref name="h"/> at
    /// <paramref name="anchor"/>, limited so its box stays inside the picture area (a saved offset from a larger window, or a
    /// stray drop, never puts it off-screen). The saved setting is not changed.
    /// </summary>
    public static (int X, int Y) ClampOffset(string anchor, double areaW, double areaH, double w, double h, int x, int y,
        double side, double top, double bottomInset, double edgeInset = 0)
    {
        if (areaW <= 0 || areaH <= 0 || w <= 0 || h <= 0) return (x, y);
        var a = anchor ?? string.Empty;
        var px = a.Contains("left", StringComparison.OrdinalIgnoreCase) ? side
            : a.Contains("center", StringComparison.OrdinalIgnoreCase) ? (areaW - w) / 2 : areaW - side - w;
        var py = a.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase) ? areaH - bottomInset - h : top;
        var cx = ClampInside(px + x, areaW, w, edgeInset);
        var cy = ClampInside(py + y, areaH, h, edgeInset);
        return ((int)Math.Round(cx - px), (int)Math.Round(cy - py));
    }

    /// <summary>
    /// Keeps a box of <paramref name="size"/> inside 0..<paramref name="area"/>, at least <paramref name="edgeInset"/> from either
    /// edge (less when the box leaves no room). With no inset the box could sit flush against the window edge, where the last
    /// glyph of a text line was clipped (5.1.7 UI test bug 10: X offset 5000).
    /// </summary>
    public static double ClampInside(double position, double area, double size, double edgeInset)
    {
        var inset = Math.Min(Math.Max(0, edgeInset), Math.Max(0, (area - size) / 2));
        return Math.Clamp(position, inset, Math.Max(inset, area - size - inset));
    }
}
