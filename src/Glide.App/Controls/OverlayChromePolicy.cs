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
    public const double HoverDismissPaddingDip = 16;
    public const double FrameDragBandDip = 34;
    /// <summary>Optional Overlay drag handle (option A, inset pill): 22 DIP tall, 12 DIP below the top edge, fades in/out.</summary>
    public const double DragStripDip = 22;
    public const double DragStripTopInsetDip = 12;
    public const double DragStripMaxWidthDip = 160;
    public const double DragStripMinWidthDip = 40;
    /// <summary>Side clearance: keeps the pill off the resize border and clear of the top-right close button.</summary>
    public const double DragStripSideClearanceDip = 46;
    /// <summary>How long the drag handle stays after the pointer leaves the Overlay, then how long it fades.</summary>
    public const int DragStripHideDelayMs = 2500;
    public const int DragStripFadeMs = 300;

    /// <summary>
    /// The drag handle's visibility state machine. Shown while the pointer is over the Overlay (picture, handle, bottom
    /// bar) or a window drag is running; after the pointer leaves it lingers <see cref="DragStripHideDelayMs"/>, then
    /// fades over <see cref="DragStripFadeMs"/>. Returns the opacity; above 0 the handle stays hit-testable.
    /// </summary>
    public static double DragStripOpacity(bool wanted, bool fits, bool pointerOver, bool dragging, double msSinceLeft)
    {
        if (!wanted || !fits) return 0;
        if (pointerOver || dragging || msSinceLeft <= DragStripHideDelayMs) return 1;
        var fade = (msSinceLeft - DragStripHideDelayMs) / DragStripFadeMs;
        return fade >= 1 ? 0 : 1 - fade;
    }

    /// <summary>
    /// The drag pill inside the picture rect: centred, inset from the top so it never sits in the window resize band,
    /// shrinking for small windows. Null when the picture is too small to fit it.
    /// </summary>
    public static Rect? DragStripRect(Rect picture)
    {
        var width = Math.Min(DragStripMaxWidthDip, picture.Width - 2 * DragStripSideClearanceDip);
        if (width < DragStripMinWidthDip || picture.Height < DragStripTopInsetDip + DragStripDip + BottomBarDip) return null;
        return new Rect(picture.X + (picture.Width - width) / 2, picture.Y + DragStripTopInsetDip, width, DragStripDip);
    }
    /// <summary>Height of the bottom button bar band that moves the Overlay window when dragged outside its buttons.</summary>
    public const double BottomBarDip = 46;

    public static Rect BottomBarRect(Size size) => new(0, Math.Max(0, size.Height - BottomBarDip), size.Width, Math.Min(BottomBarDip, size.Height));

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

    /// <summary>Opacity change per mouse-wheel notch over the whole-app Overlay opacity slider.</summary>
    public const double WholeWindowOpacityWheelStep = 0.05;

    // Whole-app Overlay opacity track: the drawn line runs from 10 DIP below the slider top to 10 DIP above its
    // bottom, and the minimum (35 %) sits at the bottom end, so the dot is always exactly under the pointer while
    // dragging (round 3 #22: the dot used to be drawn on a 0-100 % scale while the hit slider used 35-100 %).
    public static double WholeWindowOpacityTrackY(Rect slider, double value)
    {
        var t = (ClampWholeWindowOpacity(value) - WholeWindowMinimumOpacity) / (1.0 - WholeWindowMinimumOpacity);
        return slider.Bottom - 10 - Math.Max(0, slider.Height - 20) * t;
    }

    public static double WholeWindowOpacityFromTrackY(Rect slider, double y)
    {
        var span = Math.Max(1, slider.Height - 20);
        var t = Math.Clamp((slider.Bottom - 10 - y) / span, 0, 1);
        return WholeWindowMinimumOpacity + t * (1.0 - WholeWindowMinimumOpacity);
    }

    /// <summary>The slider's grab area: wider than the drawn pill so it is easy to hit.</summary>
    public static Rect WholeWindowOpacityHitRect(Size size)
    {
        var slider = SliderRect(size);
        return new Rect(0, slider.Y - 6, slider.Right + 12, slider.Height + 12);
    }
    public static double ClampItemOpacity(double value) => Math.Clamp(value, MinimumOpacity, 1.0);
    public static double ClampWholeWindowOpacity(double value) => Math.Clamp(value, WholeWindowMinimumOpacity, 1.0);

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
