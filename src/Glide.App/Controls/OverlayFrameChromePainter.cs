using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Glide.App.Controls;

/// <summary>
/// What one overlay frame's chrome shows. Built by <see cref="OverlayFrameGeometry.BuildSpec"/> from the frame rectangle
/// (a picture-in-picture item or the whole-app Overlay window's picture) and drawn by <see cref="OverlayFrameChromePainter"/>,
/// so the in-app picture-in-picture overlays and Overlay mode are one window chrome (round 4 K1).
/// </summary>
internal readonly record struct OverlayFrameChromeSpec(
    Rect Frame,
    Rect Close,
    Rect Slider,
    double KnobY,
    Rect ZoomOut,
    Rect ZoomIn,
    bool Glow = true,
    Rect? ResizeGrip = null,
    Rect? WidthHandle = null,
    Rect? HeightHandle = null,
    bool ShowFrame = true,
    bool ShowClose = true,
    bool ShowSlider = true,
    bool ShowZoom = true);

/// <summary>
/// The one painter for overlay window chrome: frame + hover glow, round close button, resize grip, crop handles,
/// opacity pill and zoom buttons. Used by the managed
/// picture-in-picture renderer, the compositor renderer and Overlay mode's <see cref="LegacyOverlayChrome"/>.
/// The native retained-window renderer rasterises the same shapes with <see cref="Palette"/> colours.
/// </summary>
internal static class OverlayFrameChromePainter
{
    /// <summary>Shared colours (ARGB).</summary>
    internal static class Palette
    {
        public static readonly Color Panel = Color.FromArgb(225, 32, 36, 42);
        public static readonly Color Glass = Color.FromArgb(205, 45, 50, 58);
        public static readonly Color Glow = Color.FromArgb(64, 56, 184, 244);
        public static readonly Color Foreground = Colors.White;
        public const double FrameStroke = 1.35;
        public const double FrameRadius = 7;
    }

    internal interface ICanvas
    {
        void Rect(Rect rect, Color? fill, Color? stroke, double strokeThickness, double radius);
        void Line(Point a, Point b, Color color, double thickness);
        void Ellipse(Point center, double radius, Color fill);
    }

    private sealed class ManagedCanvas(DrawingContext context) : ICanvas
    {
        public void Rect(Rect rect, Color? fill, Color? stroke, double strokeThickness, double radius) =>
            context.DrawRectangle(fill is { } f ? new SolidColorBrush(f) : null, stroke is { } s ? new Pen(new SolidColorBrush(s), strokeThickness) : null, rect, radius, radius);
        public void Line(Point a, Point b, Color color, double thickness) => context.DrawLine(new Pen(new SolidColorBrush(color), thickness), a, b);
        public void Ellipse(Point center, double radius, Color fill) => context.DrawEllipse(new SolidColorBrush(fill), null, center, radius, radius);
    }

    private sealed class ImmediateCanvas(ImmediateDrawingContext context) : ICanvas
    {
        public void Rect(Rect rect, Color? fill, Color? stroke, double strokeThickness, double radius) =>
            context.DrawRectangle(fill is { } f ? new ImmutableSolidColorBrush(f) : null, stroke is { } s ? new ImmutablePen(new ImmutableSolidColorBrush(s), strokeThickness) : null, rect, radius, radius);
        public void Line(Point a, Point b, Color color, double thickness) => context.DrawLine(new ImmutablePen(new ImmutableSolidColorBrush(color), thickness), a, b);
        public void Ellipse(Point center, double radius, Color fill) => context.DrawEllipse(new ImmutableSolidColorBrush(fill), null, center, radius, radius);
    }

    public static void Draw(DrawingContext context, in OverlayFrameChromeSpec spec, Color accent) => Draw(new ManagedCanvas(context), spec, accent);
    public static void Draw(ImmediateDrawingContext context, in OverlayFrameChromeSpec spec, Color accent) => Draw(new ImmediateCanvas(context), spec, accent);

    internal static void Draw(ICanvas canvas, in OverlayFrameChromeSpec spec, Color accent)
    {
        var fg = Palette.Foreground;
        var frame = spec.Frame;
        if (spec.Glow)
            // A soft ring just outside the frame, never a fill: a filled glow rect tinted the whole picture on hover.
            canvas.Rect(new Rect(frame.X - 2, frame.Y - 2, frame.Width + 4, frame.Height + 4), null, Palette.Glow, 2, Palette.FrameRadius + 2);
        if (spec.ShowFrame) canvas.Rect(frame, null, accent, Palette.FrameStroke, Palette.FrameRadius);

        if (spec.ShowClose)
        {
            var cc = spec.Close.Center;
            canvas.Ellipse(cc, 13, Palette.Panel);
            canvas.Line(new Point(cc.X - 4, cc.Y - 4), new Point(cc.X + 4, cc.Y + 4), fg, 1.8);
            canvas.Line(new Point(cc.X + 4, cc.Y - 4), new Point(cc.X - 4, cc.Y + 4), fg, 1.8);
        }

        if (spec.ResizeGrip is { } resize)
        {
            canvas.Rect(resize, Palette.Glass, null, 0, 5);
            for (var k = 0; k < 3; k++)
                canvas.Line(new Point(resize.Right - 3 - k * 5, resize.Bottom - 1),
                    new Point(resize.Right - 1, resize.Bottom - 3 - k * 5), accent, 1.25);
        }

        if (spec.WidthHandle is { } widthHandle)
        {
            canvas.Rect(widthHandle, Palette.Glass, accent, 1.2, 6);
            var wc = widthHandle.Center;
            canvas.Line(new Point(wc.X - 7, wc.Y), new Point(wc.X + 7, wc.Y), fg, 2);
            canvas.Line(new Point(wc.X - 7, wc.Y), new Point(wc.X - 3, wc.Y - 4), fg, 2);
            canvas.Line(new Point(wc.X - 7, wc.Y), new Point(wc.X - 3, wc.Y + 4), fg, 2);
            canvas.Line(new Point(wc.X + 7, wc.Y), new Point(wc.X + 3, wc.Y - 4), fg, 2);
            canvas.Line(new Point(wc.X + 7, wc.Y), new Point(wc.X + 3, wc.Y + 4), fg, 2);
        }
        if (spec.HeightHandle is { } heightHandle)
        {
            canvas.Rect(heightHandle, Palette.Glass, accent, 1.2, 6);
            var hc = heightHandle.Center;
            canvas.Line(new Point(hc.X, hc.Y - 6), new Point(hc.X, hc.Y + 6), fg, 2);
            canvas.Line(new Point(hc.X, hc.Y - 6), new Point(hc.X - 4, hc.Y - 2), fg, 2);
            canvas.Line(new Point(hc.X, hc.Y - 6), new Point(hc.X + 4, hc.Y - 2), fg, 2);
            canvas.Line(new Point(hc.X, hc.Y + 6), new Point(hc.X - 4, hc.Y + 2), fg, 2);
            canvas.Line(new Point(hc.X, hc.Y + 6), new Point(hc.X + 4, hc.Y + 2), fg, 2);
        }

        if (spec.ShowSlider)
        {
            var slider = spec.Slider;
            canvas.Rect(slider, Palette.Glass, null, 0, 10);
            var sx = slider.Center.X;
            canvas.Line(new Point(sx, slider.Top + OverlayFrameGeometry.OpacityTrackInset), new Point(sx, slider.Bottom - OverlayFrameGeometry.OpacityTrackInset), fg, 2);
            canvas.Ellipse(new Point(sx, spec.KnobY), 5, accent);
        }

        if (!spec.ShowZoom) return;
        DrawZoomButton(canvas, spec.ZoomOut, plus: false, accent);
        DrawZoomButton(canvas, spec.ZoomIn, plus: true, accent);
    }

    private static void DrawZoomButton(ICanvas canvas, Rect rect, bool plus, Color accent)
    {
        canvas.Rect(rect, Palette.Glass, accent, 1.2, 7);
        var c = rect.Center;
        canvas.Line(new Point(c.X - 5, c.Y), new Point(c.X + 5, c.Y), Palette.Foreground, 2);
        if (plus) canvas.Line(new Point(c.X, c.Y - 5), new Point(c.X, c.Y + 5), Palette.Foreground, 2);
    }
}
