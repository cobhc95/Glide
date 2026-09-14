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
                Circle(12, 12, 7); L(17, 5, 19.5, 8.5); L(19.5, 8.5, 15.5, 8.5); break;
            case "First":
                L(7, 5, 7, 19); L(16.5, 5, 9.5, 12); L(9.5, 12, 16.5, 19); break;
            case "Last":
                L(17, 5, 17, 19); L(7.5, 5, 14.5, 12); L(14.5, 12, 7.5, 19); break;
            case "Open":
                L(3.5, 8, 8.5, 8); L(8.5, 8, 10.5, 5.5); L(10.5, 5.5, 20, 5.5); L(20, 5.5, 20.5, 9); Box(3.5, 8, 17, 11); break;
            case "FolderLocate":
                L(3.5, 8, 8.5, 8); L(8.5, 8, 10.5, 5.5); L(10.5, 5.5, 20, 5.5); Box(3.5, 8, 17, 11); Circle(15.5, 13.5, 2.2); L(17.1, 15.1, 20.5, 18.5); break;
            case "Fit":
                Box(5, 6, 14, 12); L(3, 9, 3, 3); L(3, 3, 9, 3); L(21, 9, 21, 3); L(21, 3, 15, 3); L(3, 15, 3, 21); L(3, 21, 9, 21); L(21, 15, 21, 21); L(21, 21, 15, 21); break;
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
                Box(4, 5, 16, 14); Box(7, 8, 10, 8); L(9, 18, 18, 9); L(13, 18, 20, 11); break;
            case "AlwaysOnTop":
                L(7, 5, 17, 5); L(9, 5, 10, 11); L(15, 5, 14, 11); L(8, 11, 16, 11); L(12, 11, 12, 20); break;
            case "Appearance":
                Circle(8, 8, 3, true); Circle(16, 9, 2.4, true); Circle(11, 16, 2.7, true); L(5, 19, 19, 5); break;
            case "Interface":
                Box(3.5, 4.5, 17, 15); L(3.5, 9, 20.5, 9); Circle(6.5, 6.8, 0.7, true); Circle(9.5, 6.8, 0.7, true); break;
            case "Viewing":
                Box(4, 5, 16, 11); L(9, 20, 15, 20); L(12, 16, 12, 20); break;
            case "Mouse":
                Box(7, 3, 10, 18); L(12, 3, 12, 9); L(7, 9, 17, 9); break;
            case "Performance":
                L(4, 17, 6, 12); L(6, 12, 10, 8); L(10, 8, 15, 7); L(15, 7, 20, 12); L(12, 14, 16, 9); break;
            case "Status":
                Circle(12, 12, 8); Circle(12, 12, 3, true); break;
            case "Slideshow":
                L(7, 4, 19, 12); L(19, 12, 7, 20); L(7, 20, 7, 4); break;
            case "Hotkeys":
                Box(3, 6, 18, 12); for (var x = 6; x <= 18; x += 4) { L(x, 9, x + 1, 9); L(x, 13, x + 1, 13); } L(8, 16, 16, 16); break;
            case "Tabs":
                Box(3, 7, 18, 12); L(7, 7, 7, 11); L(12, 7, 12, 11); break;
            case "Overlays":
                Box(3, 5, 14, 12); Box(8, 9, 13, 11); L(10, 16, 14, 12); L(14, 12, 18, 16); break;
            case "Profiles":
                Circle(9, 9, 3); Circle(16.5, 10, 2.5); L(4.5, 19, 5.5, 15); L(5.5, 15, 12.5, 15); L(12.5, 15, 13.5, 19); L(13, 19, 14, 16); L(14, 16, 20, 16); L(20, 16, 21, 19); break;
            case "Windows":
                Box(4, 4, 7, 7); Box(13, 4, 7, 7); Box(4, 13, 7, 7); Box(13, 13, 7, 7); break;
            case "Developer":
                Box(5, 4, 14, 16); L(8, 8, 16, 8); L(8, 12, 16, 12); L(8, 16, 14, 16); break;
            case "Fullscreen":
                L(4, 9, 4, 4); L(4, 4, 9, 4); L(20, 9, 20, 4); L(20, 4, 15, 4); L(4, 15, 4, 20); L(4, 20, 9, 20); L(20, 15, 20, 20); L(20, 20, 15, 20); break;
            default:
                Box(5, 5, 14, 14); break;
        }
    }
}
