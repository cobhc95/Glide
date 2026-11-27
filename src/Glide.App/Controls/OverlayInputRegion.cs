using Avalonia;

namespace Glide.App.Controls;

/// <summary>A rectangle of the window region in physical pixels, relative to the window's top-left corner.</summary>
internal readonly record struct RegionRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// The pure geometry of Overlay mode's window region (the part of the transparent window that takes mouse input and paints):
/// the visible frames in window DIPs, scaled to physical pixels (outward, so a fractional edge keeps its pixel), clipped to the
/// window. Null means "no region": the whole window takes input (a frame that fills the window, or nothing laid out).
/// </summary>
internal static class OverlayInputRegion
{
    public static IReadOnlyList<RegionRect>? Compute(IReadOnlyList<Rect> frames, double scale, int windowWidth, int windowHeight)
    {
        if (!(scale > 0)) scale = 1.0;
        if (windowWidth <= 0 || windowHeight <= 0) return null;
        List<RegionRect>? result = null;
        foreach (var frame in frames)
        {
            if (frame.Width <= 0 || frame.Height <= 0 || !double.IsFinite(frame.X + frame.Y + frame.Width + frame.Height)) continue;
            var left = Math.Clamp((int)Math.Floor(frame.X * scale), 0, windowWidth);
            var top = Math.Clamp((int)Math.Floor(frame.Y * scale), 0, windowHeight);
            var right = Math.Clamp((int)Math.Ceiling(frame.Right * scale), 0, windowWidth);
            var bottom = Math.Clamp((int)Math.Ceiling(frame.Bottom * scale), 0, windowHeight);
            if (right <= left || bottom <= top) continue;
            // A frame that covers the window (within a pixel of rounding) means every point is visible: no region needed.
            if (left <= 1 && top <= 1 && right >= windowWidth - 1 && bottom >= windowHeight - 1) return null;
            (result ??= new List<RegionRect>()).Add(new RegionRect(left, top, right, bottom));
        }
        return result;
    }

    /// <summary>True when the point (window pixels) is inside the region; a null region contains every point.</summary>
    public static bool Contains(IReadOnlyList<RegionRect>? region, int x, int y)
    {
        if (region is null) return true;
        foreach (var r in region)
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return true;
        return false;
    }

    public static bool Same(IReadOnlyList<RegionRect>? a, IReadOnlyList<RegionRect>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
