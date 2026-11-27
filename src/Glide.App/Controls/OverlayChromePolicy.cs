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
    /// <summary>Optional Overlay drag handle (option A, inset pill): 22 DIP tall, 28 DIP below the top edge (clear of the top edge handle), fades in/out.</summary>
    public const double DragStripDip = 22;
    public const double DragStripTopInsetDip = 28;
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

    /// <summary>
    /// The discovery zone for the drag handle (5.1.2): the middle quarter of the picture's top edge, down to just below
    /// the handle. Pointing here shows the handle even when "Show drag strip" is off.
    /// </summary>
    public static bool IsInTopCentreGripZone(Rect picture, Point p)
    {
        var zoneWidth = picture.Width * 0.25;
        var left = picture.X + (picture.Width - zoneWidth) / 2;
        return p.X >= left && p.X <= left + zoneWidth && p.Y >= picture.Y && p.Y <= picture.Y + DragStripTopInsetDip + DragStripDip + 12;
    }

    public static Rect BottomBarRect(Size size) => new(0, Math.Max(0, size.Height - BottomBarDip), size.Width, Math.Min(BottomBarDip, size.Height));

    // The chrome rectangles are the picture-in-picture ones (OverlayFrameGeometry) for a frame at the origin.
    private static Rect Frame(Size size) => new(0, 0, size.Width, size.Height);
    public static Rect CloseRect(Size size) => OverlayFrameGeometry.Close(Frame(size));
    /// <summary>The corner grip as drawn (its hit area is <see cref="ResizeHitRect"/>).</summary>
    public static Rect ResizeRect(Size size) => OverlayFrameGeometry.GripVisual(Frame(size));
    public static Rect ResizeHitRect(Size size) => OverlayFrameGeometry.GripHit(Frame(size));
    public static Rect ResizeWidthRect(Size size) => OverlayFrameGeometry.WidthHandle(Frame(size));
    public static Rect ResizeHeightRect(Size size) => OverlayFrameGeometry.HeightHandle(Frame(size));
    public static Rect SliderRect(Size size) => OverlayFrameGeometry.Slider(Frame(size));
    public static Rect ZoomOutRect(Size size) => OverlayFrameGeometry.ZoomOut(Frame(size));
    public static Rect ZoomInRect(Size size) => OverlayFrameGeometry.ZoomIn(Frame(size));

    // Whole-app Overlay opacity track: the picture-in-picture track (OverlayFrameGeometry.OpacityKnobY: the dot sits at
    // opacity x track length), so the dot is always exactly under the pointer while dragging; the window minimum (35 %) only
    // stops the value, like the 10 % minimum of a picture-in-picture overlay.
    public static double WholeWindowOpacityTrackY(Rect slider, double value) =>
        OverlayFrameGeometry.OpacityKnobY(slider, ClampWholeWindowOpacity(value));

    public static double WholeWindowOpacityFromTrackY(Rect slider, double y) =>
        OverlayFrameGeometry.OpacityFromY(slider, y, WholeWindowMinimumOpacity);

    public static double ClampItemOpacity(double value) => Math.Clamp(value, MinimumOpacity, 1.0);
    public static double ClampWholeWindowOpacity(double value) => Math.Clamp(value, WholeWindowMinimumOpacity, 1.0);

    public static bool ShouldDismissChrome(Point pointer, Size size)
    {
        var pad = HoverDismissPaddingDip;
        if (pointer.Y <= FrameDragBandDip + pad) return false;
        if (Inflate(CloseRect(size), pad).Contains(pointer)) return false;
        if (Inflate(SliderRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ResizeHitRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ResizeWidthRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ResizeHeightRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ZoomOutRect(size), pad).Contains(pointer)) return false;
        if (Inflate(ZoomInRect(size), pad).Contains(pointer)) return false;
        return true;
    }

    private static Rect Inflate(Rect rect, double amount) =>
        new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);
}
