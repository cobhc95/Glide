using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Glide.App.Printing;

/// <summary>
/// WYSIWYG page preview. Renders the sheet, the driver's real printable rectangle,
/// the user margins and the image using the exact <see cref="PrintPageLayout"/> the
/// spool path prints — the same <see cref="PrintLayoutEngine.Compute"/> output.
/// </summary>
public sealed class PrintPreviewControl : Control
{
    public static readonly StyledProperty<PrintPaperGeometry?> PaperProperty =
        AvaloniaProperty.Register<PrintPreviewControl, PrintPaperGeometry?>(nameof(Paper));
    public static readonly StyledProperty<bool> LandscapeProperty =
        AvaloniaProperty.Register<PrintPreviewControl, bool>(nameof(Landscape));
    public static readonly StyledProperty<PrintPageLayout?> LayoutProperty =
        AvaloniaProperty.Register<PrintPreviewControl, PrintPageLayout?>(nameof(Layout));
    public static readonly StyledProperty<Bitmap?> PreviewBitmapProperty =
        AvaloniaProperty.Register<PrintPreviewControl, Bitmap?>(nameof(PreviewBitmap));
    public static readonly StyledProperty<double> MarginLeftProperty =
        AvaloniaProperty.Register<PrintPreviewControl, double>(nameof(MarginLeft), 50);
    public static readonly StyledProperty<double> MarginTopProperty =
        AvaloniaProperty.Register<PrintPreviewControl, double>(nameof(MarginTop), 50);
    public static readonly StyledProperty<double> MarginRightProperty =
        AvaloniaProperty.Register<PrintPreviewControl, double>(nameof(MarginRight), 50);
    public static readonly StyledProperty<double> MarginBottomProperty =
        AvaloniaProperty.Register<PrintPreviewControl, double>(nameof(MarginBottom), 50);

    static PrintPreviewControl()
    {
        AffectsRender<PrintPreviewControl>(
            PaperProperty, LandscapeProperty, LayoutProperty, PreviewBitmapProperty,
            MarginLeftProperty, MarginTopProperty, MarginRightProperty, MarginBottomProperty);
    }

    public PrintPaperGeometry? Paper { get => GetValue(PaperProperty); set => SetValue(PaperProperty, value); }
    public bool Landscape { get => GetValue(LandscapeProperty); set => SetValue(LandscapeProperty, value); }
    public PrintPageLayout? Layout { get => GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public Bitmap? PreviewBitmap { get => GetValue(PreviewBitmapProperty); set => SetValue(PreviewBitmapProperty, value); }
    public double MarginLeft { get => GetValue(MarginLeftProperty); set => SetValue(MarginLeftProperty, value); }
    public double MarginTop { get => GetValue(MarginTopProperty); set => SetValue(MarginTopProperty, value); }
    public double MarginRight { get => GetValue(MarginRightProperty); set => SetValue(MarginRightProperty, value); }
    public double MarginBottom { get => GetValue(MarginBottomProperty); set => SetValue(MarginBottomProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var paper = Paper;
        if (paper is null || paper.PaperWidthHundredths <= 0 || paper.PaperHeightHundredths <= 0)
        {
            var empty = new FormattedText("No printer", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.Gray);
            context.DrawText(empty, new Point(12, 12));
            return;
        }

        var paperW = Landscape ? paper.PaperHeightHundredths : paper.PaperWidthHundredths;
        var paperH = Landscape ? paper.PaperWidthHundredths : paper.PaperHeightHundredths;
        var scale = Math.Min(Bounds.Width / paperW, Bounds.Height / paperH);
        if (!(scale > 0)) return;
        var ox = (Bounds.Width - paperW * scale) / 2;
        var oy = (Bounds.Height - paperH * scale) / 2;
        double X(double h) => ox + h * scale;
        double Y(double v) => oy + v * scale;

        // Drop shadow + sheet.
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            new Rect(ox + 4, oy + 4, paperW * scale, paperH * scale));
        context.FillRectangle(Brushes.White, new Rect(ox, oy, paperW * scale, paperH * scale));

        // Non-printable edge shading (what the driver cannot reach).
        var (prX, prY, prW, prH) = Landscape
            ? (paper.PrintableY, paper.PrintableX, paper.PrintableHeight, paper.PrintableWidth)
            : (paper.PrintableX, paper.PrintableY, paper.PrintableWidth, paper.PrintableHeight);
        var shade = new SolidColorBrush(Color.FromArgb(38, 120, 120, 120));
        context.FillRectangle(shade, new Rect(ox, oy, paperW * scale, Y(prY) - oy));
        context.FillRectangle(shade, new Rect(ox, Y(prY + prH), paperW * scale, oy + paperH * scale - Y(prY + prH)));
        context.FillRectangle(shade, new Rect(ox, Y(prY), X(prX) - ox, prH * scale));
        context.FillRectangle(shade, new Rect(X(prX + prW), Y(prY), ox + paperW * scale - X(prX + prW), prH * scale));

        // User margins (dashed accent).
        var marginPen = new Pen(new SolidColorBrush(Color.FromRgb(0x38, 0xA9, 0xF5)), 1)
        { DashStyle = DashStyle.Dash };
        var mx = Math.Max(0, MarginLeft); var my = Math.Max(0, MarginTop);
        var mr = Math.Max(0, MarginRight); var mb = Math.Max(0, MarginBottom);
        context.DrawRectangle(marginPen, new Rect(X(mx), Y(my),
            Math.Max(0, (paperW - mx - mr) * scale), Math.Max(0, (paperH - my - mb) * scale)));

        // The image, clipped to its destination like the spool path.
        var layout = Layout;
        var bitmap = PreviewBitmap;
        if (layout is not null && bitmap is not null && layout.DestWidth > 0 && layout.DestHeight > 0)
        {
            var dest = new Rect(X(layout.DestX), Y(layout.DestY),
                layout.DestWidth * scale, layout.DestHeight * scale);
            using (context.PushClip(dest))
            {
                var src = new Rect(
                    layout.SrcX * bitmap.PixelSize.Width,
                    layout.SrcY * bitmap.PixelSize.Height,
                    Math.Max(1, layout.SrcWidth * bitmap.PixelSize.Width),
                    Math.Max(1, layout.SrcHeight * bitmap.PixelSize.Height));
                context.FillRectangle(Brushes.White, dest);
                context.DrawImage(bitmap, src, dest);
            }
            context.DrawRectangle(new Pen(Brushes.Gray, 1), dest);
        }

        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 1),
            new Rect(ox, oy, paperW * scale, paperH * scale));
    }
}
