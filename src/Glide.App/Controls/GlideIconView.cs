using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glide.App.Controls;

/// <summary>
/// Small vector icon renderer used by Glide chrome and Settings.  Keeping persistent icons here avoids
/// font/emoji substitution and guarantees one stroke language at every DPI.  Icons are intentionally
/// simple: they are UI affordances, not decorative artwork.
/// </summary>
public sealed class GlideIconView : Control
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<GlideIconView, string>(nameof(Kind), "Image");

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<GlideIconView, IBrush?>(nameof(IconBrush));

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<GlideIconView, double>(nameof(StrokeWidth), 1.6);

    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? IconBrush { get => GetValue(IconBrushProperty); set => SetValue(IconBrushProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    static GlideIconView() => AffectsRender<GlideIconView>(KindProperty, IconBrushProperty, StrokeWidthProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 1 || Bounds.Height <= 1) return;

        var brush = IconBrush ?? Brushes.White;
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Point P(double x, double y) => new(x * Bounds.Width / 24.0, y * Bounds.Height / 24.0);
        Rect R(double x, double y, double w, double h) => new(x * Bounds.Width / 24.0, y * Bounds.Height / 24.0, w * Bounds.Width / 24.0, h * Bounds.Height / 24.0);
        void L(double x1, double y1, double x2, double y2) => context.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Box(double x, double y, double w, double h) => context.DrawRectangle(null, pen, R(x, y, w, h));
        void Circle(double x, double y, double r, bool fill = false) => context.DrawEllipse(fill ? brush : null, fill ? null : pen, P(x, y), r * Bounds.Width / 24.0, r * Bounds.Height / 24.0);
        // Arc along a circle; angles in degrees, increasing clockwise from 3 o'clock (y points down).
        void Arc(double cx, double cy, double r, double fromDeg, double toDeg)
        {
            var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(toDeg - fromDeg) / 12.0));
            var prev = P(cx + r * Math.Cos(fromDeg * Math.PI / 180), cy + r * Math.Sin(fromDeg * Math.PI / 180));
            for (var i = 1; i <= steps; i++)
            {
                var a = (fromDeg + (toDeg - fromDeg) * i / steps) * Math.PI / 180;
                var next = P(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
                context.DrawLine(pen, prev, next);
                prev = next;
            }
        }
        // Open arrowhead whose tip is at (x, y) and which points along (dx, dy).
        void Head(double x, double y, double dx, double dy, double size = 4)
        {
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return;
            dx /= len; dy /= len;
            foreach (var sign in new[] { -1, 1 })
            {
                var ang = sign * 0.55;
                var bx = -(dx * Math.Cos(ang) - dy * Math.Sin(ang));
                var by = -(dx * Math.Sin(ang) + dy * Math.Cos(ang));
                L(x, y, x + bx * size, y + by * size);
            }
        }

        switch (Kind)
        {
            case "Home":
                L(4, 11, 12, 4); L(12, 4, 20, 11); Box(6.5, 10, 11, 9); L(10, 19, 10, 14); L(14, 19, 14, 14); break;
            case "Image":
                Box(4, 5, 16, 14); Circle(9, 10, 1.3); L(5.5, 17, 10.5, 12.5); L(10.5, 12.5, 13.5, 15); L(13.5, 15, 17, 11.5); L(17, 11.5, 19, 14); break;
            case "Browser":
                Box(3.5, 5.5, 17, 13); L(3.5, 9, 20.5, 9); L(8, 5.5, 8, 9); break;
            case "Previous":
                L(15.5, 5, 8.5, 12); L(8.5, 12, 15.5, 19); break;
            case "Next":
                L(8.5, 5, 15.5, 12); L(15.5, 12, 8.5, 19); break;
            case "FolderPrevious":
                L(3.5, 8, 8.5, 8); L(8.5, 8, 10.5, 5.5); L(10.5, 5.5, 20, 5.5); L(20, 5.5, 20.5, 18.5); L(3.5, 8, 3.5, 18.5); L(3.5, 18.5, 20.5, 18.5); L(14.5, 10, 10, 14); L(10, 14, 14.5, 18); break;
            case "FolderNext":
                L(3.5, 8, 8.5, 8); L(8.5, 8, 10.5, 5.5); L(10.5, 5.5, 20, 5.5); L(20, 5.5, 20.5, 18.5); L(3.5, 8, 3.5, 18.5); L(3.5, 18.5, 20.5, 18.5); L(9.5, 10, 14, 14); L(14, 14, 9.5, 18); break;
            case "Up":
                L(5.5, 13.5, 12, 6.5); L(12, 6.5, 18.5, 13.5); L(12, 6.5, 12, 18.5); break;
            case "Down":
                L(5.5, 10.5, 12, 17.5); L(12, 17.5, 18.5, 10.5); L(12, 5.5, 12, 17.5); break;
            case "Refresh":
                // Open circular arrow: a clockwise arc with a gap at the top and the head at its end.
                Arc(12, 12, 7, -50, 250); Head(12 + 7 * Math.Cos(250 * Math.PI / 180), 12 + 7 * Math.Sin(250 * Math.PI / 180), 0.94, -0.34); break;
            case "RotateLeft":
                Arc(12, 13, 6.5, -70, 200); Head(12 + 6.5 * Math.Cos(-70 * Math.PI / 180), 13 + 6.5 * Math.Sin(-70 * Math.PI / 180), -0.94, -0.34); break;
            case "RotateRight":
                Arc(12, 13, 6.5, -20, 250); Head(12 + 6.5 * Math.Cos(250 * Math.PI / 180), 13 + 6.5 * Math.Sin(250 * Math.PI / 180), 0.94, -0.34); break;
            case "FlipHorizontal":
                L(12, 3.5, 12, 20.5); L(9, 7.5, 4, 17.5); L(4, 17.5, 9, 17.5); L(9, 17.5, 9, 7.5); L(15, 7.5, 20, 17.5); L(20, 17.5, 15, 17.5); L(15, 17.5, 15, 7.5); break;
            case "FlipVertical":
                L(3.5, 12, 20.5, 12); L(7.5, 9, 17.5, 4); L(17.5, 4, 17.5, 9); L(17.5, 9, 7.5, 9); L(7.5, 15, 17.5, 20); L(17.5, 20, 17.5, 15); L(17.5, 15, 7.5, 15); break;
            case "Copy":
                Box(9, 9, 11, 11); L(5, 15, 5, 5); L(5, 5, 15, 5); L(15, 5, 15, 9); break;
            case "File":
                L(6, 3, 14, 3); L(14, 3, 19, 8); L(19, 8, 19, 21); L(19, 21, 6, 21); L(6, 21, 6, 3); L(14, 3, 14, 8); L(14, 8, 19, 8); L(9, 13, 16, 13); L(9, 17, 14, 17); break;
            case "Rename":
                L(5, 19, 6.5, 14); L(6.5, 14, 16, 4.5); L(16, 4.5, 19.5, 8); L(19.5, 8, 10, 17.5); L(10, 17.5, 5, 19); L(13.5, 7, 17, 10.5); break;
            case "Delete":
                L(4.5, 7, 19.5, 7); L(9.5, 7, 9.5, 4.5); L(9.5, 4.5, 14.5, 4.5); L(14.5, 4.5, 14.5, 7); L(6.5, 7, 7.5, 20); L(7.5, 20, 16.5, 20); L(16.5, 20, 17.5, 7); L(10.5, 10.5, 10.5, 16.5); L(13.5, 10.5, 13.5, 16.5); break;
            case "Eye":
                L(3, 12, 7.5, 7.5); L(7.5, 7.5, 12, 6); L(12, 6, 16.5, 7.5); L(16.5, 7.5, 21, 12); L(21, 12, 16.5, 16.5); L(16.5, 16.5, 12, 18); L(12, 18, 7.5, 16.5); L(7.5, 16.5, 3, 12); Circle(12, 12, 2.6); break;
            case "BringToFront":
                Box(4, 4, 11, 11); Box(9, 9, 11, 11); L(9, 9, 15, 9); L(9, 9, 9, 15); break;
            case "OpenWith":
                L(11, 6, 5.5, 6); L(5.5, 6, 5.5, 19); L(5.5, 19, 18, 19); L(18, 19, 18, 13); L(13, 4.5, 19.5, 4.5); L(19.5, 4.5, 19.5, 11); L(19.5, 4.5, 10.5, 13.5); break;
            case "Volume":
                L(4, 10, 8, 10); L(8, 10, 12, 6.5); L(12, 6.5, 12, 17.5); L(12, 17.5, 8, 14); L(8, 14, 4, 14); L(4, 14, 4, 10); Arc(12, 12, 5, -45, 45); Arc(12, 12, 8.5, -45, 45); break;
            case "Crop":
                L(7, 3, 7, 17); L(7, 17, 21, 17); L(3, 7, 17, 7); L(17, 7, 17, 21); break;
            case "Check":
                L(5, 12.5, 10, 17.5); L(10, 17.5, 19, 7); break;
            case "Navigate":
                Circle(12, 12, 8); L(15.5, 8.5, 13.2, 13.2); L(13.2, 13.2, 8.5, 15.5); L(8.5, 15.5, 10.8, 10.8); L(10.8, 10.8, 15.5, 8.5); break;
            case "Present":
                Box(3.5, 5, 17, 11); L(12, 16, 12, 20); L(8.5, 20, 15.5, 20); L(10.5, 8.5, 14.5, 10.5); L(14.5, 10.5, 10.5, 12.5); L(10.5, 12.5, 10.5, 8.5); break;
            case "Move":
                L(12, 3.5, 12, 20.5); L(3.5, 12, 20.5, 12); Head(12, 3.5, 0, -1); Head(12, 20.5, 0, 1); Head(3.5, 12, -1, 0); Head(20.5, 12, 1, 0); break;
            case "First":
                L(7, 5, 7, 19); L(16.5, 5, 9.5, 12); L(9.5, 12, 16.5, 19); break;
            case "Last":
                L(17, 5, 17, 19); L(7.5, 5, 14.5, 12); L(14.5, 12, 7.5, 19); break;
            case "Open":
                L(3.5, 8, 8.5, 8); L(8.5, 8, 10.5, 5.5); L(10.5, 5.5, 20, 5.5); L(20, 5.5, 20.5, 9); Box(3.5, 8, 17, 11); break;
            case "Print":
                Box(7, 3, 10, 5); Box(4, 8, 16, 7); Box(7, 13, 10, 8); L(9.5, 17.5, 14.5, 17.5); break;
            case "FolderLocate":
                L(3.5, 8, 8.5, 8); L(8.5, 8, 10.5, 5.5); L(10.5, 5.5, 20, 5.5); Box(3.5, 8, 17, 11); Circle(15.5, 13.5, 2.2); L(17.1, 15.1, 20.5, 18.5); break;
            case "Fit":
                Box(5, 6, 14, 12); L(3, 9, 3, 3); L(3, 3, 9, 3); L(21, 9, 21, 3); L(21, 3, 15, 3); L(3, 15, 3, 21); L(3, 21, 9, 21); L(21, 15, 21, 21); L(21, 21, 15, 21); break;
            case "FitWindow":
                // Picture with four outward diagonal arrows: grow (or shrink) to the window.
                Box(8, 8, 8, 8); L(8, 8, 3.5, 3.5); L(3.5, 3.5, 3.5, 7); L(3.5, 3.5, 7, 3.5); L(16, 8, 20.5, 3.5); L(20.5, 3.5, 17, 3.5); L(20.5, 3.5, 20.5, 7);
                L(8, 16, 3.5, 20.5); L(3.5, 20.5, 3.5, 17); L(3.5, 20.5, 7, 20.5); L(16, 16, 20.5, 20.5); L(20.5, 20.5, 17, 20.5); L(20.5, 20.5, 20.5, 17); break;
            case "FitWidth":
                Box(5, 7, 14, 10); L(2.5, 12, 7, 12); L(2.5, 12, 5, 9.5); L(2.5, 12, 5, 14.5); L(21.5, 12, 17, 12); L(21.5, 12, 19, 9.5); L(21.5, 12, 19, 14.5); break;
            case "FitHeight":
                Box(7, 5, 10, 14); L(12, 2.5, 12, 7); L(12, 2.5, 9.5, 5); L(12, 2.5, 14.5, 5); L(12, 21.5, 12, 17); L(12, 21.5, 9.5, 19); L(12, 21.5, 14.5, 19); break;
            case "ZoomIn":
                Circle(10.5, 10.5, 5.8); L(15, 15, 20, 20); L(7.5, 10.5, 13.5, 10.5); L(10.5, 7.5, 10.5, 13.5); break;
            case "ZoomOut":
                Circle(10.5, 10.5, 5.8); L(15, 15, 20, 20); L(7.5, 10.5, 13.5, 10.5); break;
            case "Play":
                L(8, 5, 18, 12); L(18, 12, 8, 19); L(8, 19, 8, 5); break;
            case "Pause":
                L(9, 5, 9, 19); L(15, 5, 15, 19); break;
            case "History":
                Circle(12, 12, 8); L(12, 7.5, 12, 12); L(12, 12, 16, 14); break;
            case "Stop":
                Box(7, 7, 10, 10); break;
            case "Collapse":
                L(6, 12, 18, 12); break;
            case "Expand":
                L(7, 15, 12, 10); L(12, 10, 17, 15); break;
            case "Info":
                Circle(12, 12, 8); Circle(12, 8, .8, true); L(12, 11, 12, 17); break;
            case "OverlayAdd":
                Box(4, 5, 13, 11); Box(8, 9, 12, 10); L(17, 4, 17, 9); L(14.5, 6.5, 19.5, 6.5); break;
            case "SaveFile":
                // Floppy disk: body, shutter at the top, label panel at the bottom.
                Box(4.5, 4.5, 15, 15); Box(8, 4.5, 8, 4.5); Box(7.5, 13, 9, 6.5); break;
            case "SaveAs":
                // Same floppy, drawn smaller with a "+" so it reads as a sibling of Save.
                Box(3.5, 3.5, 13, 13); Box(6.5, 3.5, 7, 3.8); Box(6.5, 10.5, 7, 6); L(18, 14, 18, 21); L(14.5, 17.5, 21.5, 17.5); break;
            case "OverlaySave":
                Box(4, 4, 16, 16); L(12, 17, 12, 8); L(8.5, 11.5, 12, 8); L(12, 8, 15.5, 11.5); break;
            case "OverlayLoad":
                L(5, 7, 19, 7); L(12, 8, 12, 17); L(8.5, 13.5, 12, 17); L(12, 17, 15.5, 13.5); break;
            case "Settings":
                // Modern tuning/settings fallback. Visible Settings buttons use the Windows Fluent
                // Settings glyph; this vector remains as a crisp portable fallback.
                L(4, 7, 9, 7); Circle(12, 7, 2); L(15, 7, 20, 7);
                L(4, 12, 5, 12); Circle(8, 12, 2); L(11, 12, 20, 12);
                L(4, 17, 12, 17); Circle(15, 17, 2); L(18, 17, 20, 17); break;
            case "General":
                Circle(12, 12, 3.3); Circle(12, 12, 7); for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4; var x1 = 12 + Math.Cos(a) * 7; var y1 = 12 + Math.Sin(a) * 7; var x2 = 12 + Math.Cos(a) * 9; var y2 = 12 + Math.Sin(a) * 9; context.DrawLine(pen, P(x1, y1), P(x2, y2)); } break;
            case "More":
                Circle(6, 12, 1.1, true); Circle(12, 12, 1.1, true); Circle(18, 12, 1.1, true); break;
            case "Minimize":
                L(5, 16, 19, 16); break;
            case "Maximize":
                Box(5.5, 5.5, 13, 13); break;
            case "Restore":
                Box(7.5, 5.5, 11, 11); L(5.5, 8, 5.5, 18.5); L(5.5, 18.5, 16, 18.5); break;
            case "Close":
                L(6, 6, 18, 18); L(18, 6, 6, 18); break;
            case "Plus":
                L(5, 12, 19, 12); L(12, 5, 12, 19); break;
            case "Actual":
                Box(5, 5, 14, 14); L(7.5, 12, 16.5, 12); L(12, 7.5, 12, 16.5); break;
            case "Transparency":
            case "WindowOpacity":
                // Water drop, left half hatched: reads as "partly see-through".
                L(12, 3.5, 18, 11); L(18, 11, 18, 15); Arc(12, 15, 6, 0, 180); L(6, 15, 6, 11); L(6, 11, 12, 3.5); L(7.5, 13.5, 12, 9); L(7, 17, 12, 12); break;
            case "AlwaysOnTop":
                L(7, 5, 17, 5); L(9, 5, 10, 11); L(15, 5, 14, 11); L(8, 11, 16, 11); L(12, 11, 12, 20); break;
            case "Appearance":
                // Paint palette: round body, three colour wells and a thumb hole.
                Circle(12, 12, 8.5); Circle(8, 10.5, 1.2, true); Circle(12, 7.5, 1.2, true); Circle(16.2, 10.5, 1.2, true); Circle(14.5, 16, 2); break;
            case "Interface":
                Box(3.5, 4.5, 17, 15); L(3.5, 9, 20.5, 9); Circle(6.5, 6.8, 0.7, true); Circle(9.5, 6.8, 0.7, true); break;
            case "Viewing":
                Box(4, 5, 16, 11); L(9, 20, 15, 20); L(12, 16, 12, 20); break;
            case "Animation":
            case "Transitions":
            case "Transition":
                Box(4, 5, 16, 14); L(4, 9, 20, 9); L(4, 15, 20, 15); L(8, 5, 8, 9); L(16, 5, 16, 9); L(8, 15, 8, 19); L(16, 15, 16, 19); break;
            case "Mouse":
                Box(7, 3, 10, 18); L(12, 3, 12, 9); L(7, 9, 17, 9); break;
            case "Performance":
                // Speedometer: half-dial with ticks and a needle.
                Arc(12, 16, 8, 180, 360); L(4, 16, 6, 16); L(18, 16, 20, 16); L(12, 8, 12, 10); L(12, 16, 16.5, 10.5); Circle(12, 16, 1.1, true); break;
            case "Status":
                Circle(12, 12, 8); Circle(12, 12, 3, true); break;
            case "Slideshow":
                L(7, 4, 19, 12); L(19, 12, 7, 20); L(7, 20, 7, 4); break;
            case "HighQuality":
                // Legacy high-quality badge retained for compatibility with older layouts.
                // The live slideshow-playing indicator now uses the dedicated Slideshow glyph.
                Box(3, 4, 18, 16); L(7, 8, 7, 16); L(11, 8, 11, 16); L(7, 12, 11, 12);
                Circle(16, 11, 3.2); L(18.3, 13.3, 20.5, 15.5); break;
            case "Hotkeys":
                Box(3, 6, 18, 12); for (var x = 6; x <= 18; x += 4) { L(x, 9, x + 1, 9); L(x, 13, x + 1, 13); } L(8, 16, 16, 16); break;
            case "Tabs":
                Box(3, 9, 18, 11); L(3, 9, 3, 6); L(3, 6, 9, 6); L(9, 6, 11, 9); L(14, 6, 20, 6); L(14, 6, 14, 9); break;
            case "Overlays":
                Box(3, 5, 14, 12); Box(8, 9, 13, 11); L(10, 16, 14, 12); L(14, 12, 18, 16); break;
            case "Profiles":
                Circle(9, 9, 3); Circle(16.5, 10, 2.5); L(4.5, 19, 5.5, 15); L(5.5, 15, 12.5, 15); L(12.5, 15, 13.5, 19); L(13, 19, 14, 16); L(14, 16, 20, 16); L(20, 16, 21, 19); break;
            case "Windows":
                Box(4, 4, 7, 7); Box(13, 4, 7, 7); Box(4, 13, 7, 7); Box(13, 13, 7, 7); break;
            case "Developer":
                L(9, 7, 4, 12); L(4, 12, 9, 17); L(15, 7, 20, 12); L(20, 12, 15, 17); L(13.5, 5, 10.5, 19); break;
            case "Fullscreen":
                L(4, 9, 4, 4); L(4, 4, 9, 4); L(20, 9, 20, 4); L(20, 4, 15, 4); L(4, 15, 4, 20); L(4, 20, 9, 20); L(20, 15, 20, 20); L(20, 20, 15, 20); break;
            default:
                Box(5, 5, 14, 14); break;
        }
    }
}
