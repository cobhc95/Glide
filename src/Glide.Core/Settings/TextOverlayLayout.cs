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
        double side, double top, double bottomInset)
    {
        x = Math.Clamp(x, 0, Math.Max(0, areaW - w));
        y = Math.Clamp(y, 0, Math.Max(0, areaH - h));
        var cx = x + w / 2; var cy = y + h / 2;
        var col = cx < areaW / 3 ? 0 : cx > areaW * 2 / 3 ? 2 : 1;
        var bottom = cy > areaH / 2;
        var anchor = (bottom ? "Bottom " : "Top ") + (col == 0 ? "left" : col == 1 ? "center" : "right");
        var px = col == 0 ? side : col == 1 ? (areaW - w) / 2 : areaW - side - w;
        var py = bottom ? areaH - bottomInset - h : top;
        return (anchor, (int)Math.Round(x - px), (int)Math.Round(y - py));
    }
}
