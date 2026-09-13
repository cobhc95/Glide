using Avalonia;

namespace Glide.App.Controls;

/// <summary>
/// Shared geometry/interaction constants for internal image overlays and whole-application Overlay mode.
/// Host ownership remains intentionally different (canvas item vs top-level window), but hit affordances,
/// opacity bounds and hover reveal use one contract so they cannot silently drift.
/// </summary>
internal static class OverlayChromePolicy
{
    public const double MinimumOpacity = 0.10;
    public const double WholeWindowMinimumOpacity = 0.35;
    public const double RevealEdgeDip = 20;
    public const double HoverDismissPaddingDip = 16;
    public const double ResizeGripDip = 24;
    public const double FrameDragBandDip = 34;

    public static Rect CloseRect(Size size) => new(Math.Max(0, size.Width - 34), 7, 27, 27);
    public static Rect ResizeRect(Size size) => new(Math.Max(0, size.Width - 28), Math.Max(0, size.Height - 28), 24, 24);
    public static Rect SliderRect(Size size)
    {
        var h = Math.Min(130.0, Math.Max(70.0, size.Height - 80.0));
        return new Rect(8, Math.Max(0, (size.Height - h) / 2), 22, h);
    }
    public static Rect ZoomOutRect(Size size) => new(Math.Max(4, size.Width / 2 - 36), Math.Max(4, size.Height - 38), 30, 30);
    public static Rect ZoomInRect(Size size) => new(Math.Max(4, size.Width / 2 + 6), Math.Max(4, size.Height - 38), 30, 30);
    public static Rect TopmostRect(Size size) => new(Math.Max(4, size.Width / 2 + 48), Math.Max(4, size.Height - 38), 30, 30);

    public static double ClampItemOpacity(double value) => Math.Clamp(value, MinimumOpacity, 1.0);
    public static double ClampWholeWindowOpacity(double value) => Math.Clamp(value, WholeWindowMinimumOpacity, 1.0);
    public static double RevealEdge(double renderScaling) => Math.Max(16, RevealEdgeDip / Math.Max(1.0, renderScaling));
    public static bool IsNearFrameEdge(Point pointer, Size size, double threshold) =>
        pointer.X <= threshold || pointer.Y <= threshold ||
        pointer.X >= Math.Max(0, size.Width - threshold) ||
        pointer.Y >= Math.Max(0, size.Height - threshold);

    public static bool ShouldDismissChrome(Point pointer, Size size)
    {
        var pad = HoverDismissPaddingDip;
        if (pointer.Y <= FrameDragBandDip + pad) return false;
        if (Inflate(CloseRect(size), pad).Contains(pointer)) return false;
        if (Inflate(SliderRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ResizeRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ZoomOutRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ZoomInRect(size), pad).Contains(pointer)) return false;
        if (Inflate(TopmostRect(size), pad).Contains(pointer)) return false;
        return true;
    }

    private static Rect Inflate(Rect rect, double amount) =>
        new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);
}
