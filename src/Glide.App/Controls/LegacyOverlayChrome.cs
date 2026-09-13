using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glide.App.Controls;

/// <summary>
/// Lightweight visual-only chrome matching Glide 1.2.125 Window-in-Window overlays.
/// Hit testing stays in WindowInWindowOverlayManager so pointer motion does not traverse a tree
/// of child buttons/sliders on every event.
/// </summary>
public sealed class LegacyOverlayChrome : Control
{
    public bool ChromeVisible { get; set; }
    public bool ShowResizeGrip { get; set; } = true;
    public bool TopmostActive { get; set; }
    public double OpacityValue { get; set; } = 1.0;
    public IBrush AccentBrush { get; set; } = new SolidColorBrush(Color.Parse("#36B8F4"));
    public IBrush ForegroundBrush { get; set; } = Brushes.White;

    public static Rect CloseRect(Size size) => OverlayChromePolicy.CloseRect(size);
    public static Rect ResizeRect(Size size) => OverlayChromePolicy.ResizeRect(size);
    public static Rect SliderRect(Size size) => OverlayChromePolicy.SliderRect(size);
    public static Rect ZoomOutRect(Size size) => OverlayChromePolicy.ZoomOutRect(size);
    public static Rect ZoomInRect(Size size) => OverlayChromePolicy.ZoomInRect(size);
    public static Rect TopmostRect(Size size) => OverlayChromePolicy.TopmostRect(size);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!ChromeVisible || Bounds.Width <= 1 || Bounds.Height <= 1) return;

        var accent = AccentBrush;
        var muted = ForegroundBrush;
        var frame = new Rect(.75, .75, Math.Max(0, Bounds.Width - 1.5), Math.Max(0, Bounds.Height - 1.5));
        context.DrawRectangle(null, new Pen(accent, 2.1), frame, 7, 7);

        var close = CloseRect(Bounds.Size);
        var cc = close.Center;
        var chromeFill = new SolidColorBrush(Color.FromArgb(0xE0, 0x22, 0x26, 0x2B));
        var glassFill = new SolidColorBrush(Color.FromArgb(0xC7, 0x22, 0x26, 0x2B));
        context.DrawEllipse(chromeFill, null, cc, 13, 13);
        context.DrawLine(new Pen(muted, 1.8), new Point(cc.X - 4, cc.Y - 4), new Point(cc.X + 4, cc.Y + 4));
        context.DrawLine(new Pen(muted, 1.8), new Point(cc.X + 4, cc.Y - 4), new Point(cc.X - 4, cc.Y + 4));

        if (ShowResizeGrip)
        {
            var resize = ResizeRect(Bounds.Size);
            context.DrawRectangle(glassFill, null, resize, 5, 5);
            for (var k = 0; k < 3; k++)
            {
                context.DrawLine(new Pen(accent, 1.25),
                    new Point(Bounds.Width - 7 - k * 5, Bounds.Height - 5),
                    new Point(Bounds.Width - 5, Bounds.Height - 7 - k * 5));
            }
        }

        var slider = SliderRect(Bounds.Size);
        context.DrawRectangle(glassFill, null, slider, 10, 10);
        var sx = slider.Center.X;
        context.DrawLine(new Pen(muted, 2), new Point(sx, slider.Top + 10), new Point(sx, slider.Bottom - 10));
        var t = OverlayChromePolicy.ClampItemOpacity(OpacityValue);
        var ty = slider.Bottom - 10 - (slider.Height - 20) * t;
        context.DrawEllipse(accent, null, new Point(sx, ty), 5, 5);

        DrawZoomButton(context, ZoomOutRect(Bounds.Size), "−", glassFill, accent, muted);
        DrawZoomButton(context, ZoomInRect(Bounds.Size), "+", glassFill, accent, muted);
        DrawTopmostButton(context, TopmostRect(Bounds.Size), glassFill, accent, muted, TopmostActive);
    }

    private static void DrawTopmostButton(DrawingContext context, Rect rect, IBrush fill, IBrush accent, IBrush foreground, bool active)
    {
        context.DrawRectangle(fill, new Pen(active ? accent : foreground, active ? 2.0 : 1.2), rect, 7, 7);
        var c = rect.Center;
        // Compact pin glyph: head + stem. The stronger accent border is the on/off state cue.
        context.DrawEllipse(active ? accent : foreground, null, new Point(c.X, c.Y - 4), 4, 4);
        context.DrawLine(new Pen(active ? accent : foreground, 2), new Point(c.X, c.Y), new Point(c.X, c.Y + 7));
        context.DrawLine(new Pen(active ? accent : foreground, 1.5), new Point(c.X - 4, c.Y + 7), new Point(c.X + 4, c.Y + 7));
    }

    private static void DrawZoomButton(DrawingContext context, Rect rect, string glyph, IBrush fill, IBrush accent, IBrush foreground)
    {
        context.DrawRectangle(fill, new Pen(accent, 1.2), rect, 7, 7);
        var c = rect.Center;
        context.DrawLine(new Pen(foreground, 2), new Point(c.X - 5, c.Y), new Point(c.X + 5, c.Y));
        if (glyph == "+") context.DrawLine(new Pen(foreground, 2), new Point(c.X, c.Y - 5), new Point(c.X, c.Y + 5));
    }
}
