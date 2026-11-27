using Avalonia;
using Glide.Core;

namespace Glide.App.Controls;

/// <summary>How a resize handle changes its frame: the corner grip scales it, the two edge handles crop one axis.</summary>
internal enum OverlayResizeMode { Proportional, WidthOnly, HeightOnly }

/// <summary>The chrome control under a point, in press priority order (round 4: one hit order for picture-in-picture and Overlay mode).</summary>
internal enum OverlayChromeZone { None, Close, ZoomOut, ZoomIn, Grip, WidthHandle, HeightHandle, Slider }

/// <summary>
/// Which chrome controls fit a frame of a given size without overlapping each other or leaving the frame (Overlay mode only; a
/// picture-in-picture item keeps its full chrome at every size). See <see cref="OverlayFrameGeometry.PartsFor"/>.
/// </summary>
internal readonly record struct OverlayChromeParts(bool Close, bool Grip, bool WidthHandle, bool HeightHandle, bool Slider, bool Zoom)
{
    /// <summary>Every control (a frame big enough for the full chrome).</summary>
    public static OverlayChromeParts All { get; } = new(true, true, true, true, true, true);
}

/// <summary>
/// The one definition of an overlay frame's chrome geometry. A picture-in-picture item passes its rectangle in viewer
/// coordinates; Overlay mode passes the picture rectangle of its window. Drawing (<see cref="OverlayFrameChromePainter"/>),
/// hit testing, the resize handles and the opacity track all read these rectangles, so the two hosts cannot drift apart.
/// </summary>
internal static class OverlayFrameGeometry
{
    /// <summary>The hover glow reaches this far outside the frame.</summary>
    public const double GlowReach = 2;
    /// <summary>The opacity line runs from this far below the pill top to this far above its bottom.</summary>
    public const double OpacityTrackInset = 10;

    public static Rect Close(Rect frame) => new(frame.X + frame.Width - 34, frame.Y + 7, 27, 27);
    /// <summary>The corner grip's hit area (larger than what is drawn).</summary>
    public static Rect GripHit(Rect frame) => new(frame.X + frame.Width - 36, frame.Y + frame.Height - 36, 40, 40);
    public static Rect GripVisual(Rect frame) => new(frame.X + frame.Width - 28, frame.Y + frame.Height - 28, 24, 24);
    /// <summary>Right-centre handle: crops (or, in Overlay mode, resizes) the width only.</summary>
    public static Rect WidthHandle(Rect frame) => new(frame.X + frame.Width - 22, frame.Y + frame.Height / 2 - 22, 22, 44);
    /// <summary>Top-centre handle: crops (or resizes) the height only, anchored at the bottom edge.</summary>
    public static Rect HeightHandle(Rect frame) => new(frame.X + frame.Width / 2 - 22, frame.Y, 44, 22);
    /// <summary>
    /// The rectangle of Overlay mode's frame / chrome host: the picture rectangle, clipped to the window's workspace so no handle,
    /// button or border can lie outside the window (and outside the window region cut from the same rectangle).
    /// </summary>
    public static Rect HostRect(Rect picture, Size workspace) =>
        workspace.Width > 0 && workspace.Height > 0 ? picture.Intersect(new Rect(default, workspace)) : picture;

    // ---- small frames (Overlay mode) ---------------------------------------------------------------------------------
    // At the 80 x 60 minimum the full chrome collides: the top handle sits on the close button, the right handle on the close
    // button and the corner grip, the opacity pill is taller than the frame and the zoom buttons cover it. Below these sizes the
    // secondary controls are left out (close and the corner grip stay as long as they fit), so every drawn control and hit area
    // lies inside the frame and none overlaps another. Derived from the rectangles above:
    //   close fits            : 41 x 41 (its box ends 7 + 27 = 34 from the top, 7 from the right)
    //   grip clear of close   : H >= 72  (grip box starts H - 28, close ends at 34, plus a gap)
    //   top handle clear of close : W >= 120 (W / 2 + 22 <= W - 34 - gap)
    //   right handle clear of close and grip : H >= 116 (H / 2 - 22 >= 34 + gap and H / 2 + 22 <= H - 36 - gap)
    //   zoom buttons clear of the grip and the close button : W >= 132, H >= 76
    //   opacity pill (70 high at least) above the zoom buttons : H >= 150
    public const double CloseMinSide = 41;
    public const double GripMinHeight = 72;
    public const double HeightHandleMinWidth = 120;
    public const double WidthHandleMinHeight = 116;
    public const double ZoomMinWidth = 132;
    public const double ZoomMinHeight = 76;
    public const double SliderMinHeight = 150;

    /// <summary>The controls that fit a frame of <paramref name="size"/>; hidden ones are neither drawn nor hit-testable.</summary>
    public static OverlayChromeParts PartsFor(Size size)
    {
        var w = size.Width;
        var h = size.Height;
        var close = w >= CloseMinSide && h >= CloseMinSide;
        return new OverlayChromeParts(
            Close: close,
            Grip: close && h >= GripMinHeight,
            WidthHandle: w >= CloseMinSide && h >= WidthHandleMinHeight,
            HeightHandle: w >= HeightHandleMinWidth && h >= CloseMinSide,
            Slider: w >= ZoomMinWidth && h >= SliderMinHeight,
            Zoom: w >= ZoomMinWidth && h >= ZoomMinHeight);
    }

    public static Rect Slider(Rect frame, bool video = false) => ToHost(frame, OverlayVideoChromeLayout.OpacitySlider(frame.Width, frame.Height, video));
    public static Rect ZoomOut(Rect frame, bool video = false) => ToHost(frame, OverlayVideoChromeLayout.ZoomOut(frame.Width, frame.Height, video));
    public static Rect ZoomIn(Rect frame, bool video = false) => ToHost(frame, OverlayVideoChromeLayout.ZoomIn(frame.Width, frame.Height, video));

    private static Rect ToHost(Rect frame, ChromeRect r) => new(frame.X + r.X, frame.Y + r.Y, r.Width, r.Height);

    /// <summary>The chrome control under <paramref name="point"/>, in the same order a press resolves them (corner before edge handles before the pill).</summary>
    public static OverlayChromeZone ChromeZoneAt(Rect frame, Point point, bool video = false)
    {
        if (Close(frame).Contains(point)) return OverlayChromeZone.Close;
        if (ZoomOut(frame, video).Contains(point)) return OverlayChromeZone.ZoomOut;
        if (ZoomIn(frame, video).Contains(point)) return OverlayChromeZone.ZoomIn;
        if (GripHit(frame).Contains(point)) return OverlayChromeZone.Grip;
        if (WidthHandle(frame).Contains(point)) return OverlayChromeZone.WidthHandle;
        if (HeightHandle(frame).Contains(point)) return OverlayChromeZone.HeightHandle;
        if (Slider(frame, video).Contains(point)) return OverlayChromeZone.Slider;
        return OverlayChromeZone.None;
    }

    public static OverlayResizeMode? ResizeModeOf(OverlayChromeZone zone) => zone switch
    {
        OverlayChromeZone.Grip => OverlayResizeMode.Proportional,
        OverlayChromeZone.WidthHandle => OverlayResizeMode.WidthOnly,
        OverlayChromeZone.HeightHandle => OverlayResizeMode.HeightOnly,
        _ => null
    };

    /// <summary>Opacity pill dot position: opacity 0..1 spread over the track (so 100 % is the top end, 10 % just above the bottom).</summary>
    public static double OpacityKnobY(Rect slider, double opacity) =>
        slider.Bottom - OpacityTrackInset - (slider.Height - 2 * OpacityTrackInset) * opacity;

    /// <summary>The opacity under pointer height <paramref name="y"/>, clamped to the host's range.</summary>
    public static double OpacityFromY(Rect slider, double y, double minimum) =>
        Math.Clamp((slider.Bottom - OpacityTrackInset - y) / Math.Max(1.0, slider.Height - 2 * OpacityTrackInset), minimum, 1.0);

    /// <summary>
    /// The chrome of one overlay frame. <paramref name="bounds"/> places every control; <paramref name="frameInset"/> pulls the
    /// drawn frame (and with it the glow) inward when the host is a window with no room outside its bounds.
    /// </summary>
    public static OverlayFrameChromeSpec BuildSpec(Rect bounds, double opacity, bool glow, bool video = false, bool showFrame = true, double frameInset = 0,
        bool adaptToSize = false)
    {
        var parts = adaptToSize ? PartsFor(bounds.Size) : OverlayChromeParts.All;
        var slider = Slider(bounds, video);
        return new OverlayFrameChromeSpec(
            Frame: frameInset > 0 ? bounds.Deflate(frameInset) : bounds,
            Close: Close(bounds),
            Slider: slider,
            KnobY: OpacityKnobY(slider, opacity),
            ZoomOut: ZoomOut(bounds, video),
            ZoomIn: ZoomIn(bounds, video),
            Glow: glow,
            ResizeGrip: parts.Grip ? GripVisual(bounds) : null,
            // The lower-right corner remains the true resize control. The centred right and top handles change one axis only.
            WidthHandle: parts.WidthHandle ? WidthHandle(bounds) : null,
            HeightHandle: parts.HeightHandle ? HeightHandle(bounds) : null,
            ShowFrame: showFrame,
            ShowClose: parts.Close,
            ShowSlider: parts.Slider,
            ShowZoom: parts.Zoom);
    }
}

/// <summary>What <see cref="OverlayResizeMath.Compute"/> decided: the new size and how far the top edge moved down.</summary>
internal readonly record struct OverlayResizeResult(double Width, double Height, double TopShift);

/// <summary>
/// The size rules of the resize handles, shared by the picture-in-picture overlays (which turn the result into a frame and
/// content scale) and Overlay mode (which resizes the real window): minimum 80 x 60, the corner keeps the aspect ratio
/// (stronger drag axis, scale 0.05..20), the edge handles change one axis within a maximum, the top handle keeps the bottom edge.
/// </summary>
internal static class OverlayResizeMath
{
    public const double MinimumWidth = 80;
    public const double MinimumHeight = 60;

    public static OverlayResizeResult Compute(OverlayResizeMode mode, double startWidth, double startHeight, double dx, double dy,
        bool preserveAspect, double maxWidth = double.PositiveInfinity, double maxHeight = double.PositiveInfinity)
    {
        var requestedWidth = Math.Max(MinimumWidth, startWidth + dx);
        var requestedHeight = mode == OverlayResizeMode.HeightOnly
            ? Math.Max(MinimumHeight, startHeight - dy)
            : Math.Max(MinimumHeight, startHeight + dy);

        switch (mode)
        {
            case OverlayResizeMode.WidthOnly:
                return new OverlayResizeResult(Math.Clamp(requestedWidth, MinimumWidth, Math.Max(startWidth, Math.Max(MinimumWidth, maxWidth))), startHeight, 0);
            case OverlayResizeMode.HeightOnly:
            {
                var height = Math.Clamp(requestedHeight, MinimumHeight, Math.Max(startHeight, Math.Max(MinimumHeight, maxHeight)));
                return new OverlayResizeResult(startWidth, height, startHeight - height);
            }
            default:
            {
                double width, height;
                if (preserveAspect)
                {
                    // Choose the stronger drag axis so the corner stays responsive while the aspect ratio is kept.
                    var sx = requestedWidth / Math.Max(1.0, startWidth);
                    var sy = requestedHeight / Math.Max(1.0, startHeight);
                    var scale = Math.Abs(sx - 1.0) >= Math.Abs(sy - 1.0) ? sx : sy;
                    scale = Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.05, 20.0);
                    width = Math.Max(MinimumWidth, startWidth * scale);
                    height = Math.Max(MinimumHeight, startHeight * scale);
                }
                else
                {
                    width = requestedWidth;
                    height = requestedHeight;
                }
                if (width > maxWidth || height > maxHeight)
                {
                    var fit = Math.Min(maxWidth / width, maxHeight / height);
                    width = Math.Max(MinimumWidth, width * fit);
                    height = Math.Max(MinimumHeight, height * fit);
                }
                return new OverlayResizeResult(width, height, 0);
            }
        }
    }
}
