using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace Glide.App.Printing;

/// <summary>
/// WYSIWYG page preview. Renders the sheet, the driver's real printable rectangle,
/// the user margins and the image using the exact <see cref="PrintPageLayout"/> the
/// spool path prints — the same <see cref="PrintLayoutEngine.Compute"/> output.
/// </summary>
public sealed class PrintPreviewControl : Control
{
    public PrintPreviewControl()
    {
        // A huge Actual/Fill destination must never paint outside this control over the dialog
        // buttons; Avalonia does not clip a control to its bounds by default.
        ClipToBounds = true;
    }

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

    public PrintPaperGeometry? Paper { get => GetValue(PaperProperty); set => SetValue(PaperProperty, value); }    public bool Landscape { get => GetValue(LandscapeProperty); set => SetValue(LandscapeProperty, value); }
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

        // The image, clipped to the sheet and to its destination like the spool path.
        var layout = Layout;
        var bitmap = PreviewBitmap;
        if (layout is not null && bitmap is not null && layout.DestWidth > 0 && layout.DestHeight > 0)
        {
            var dest = new Rect(X(layout.DestX), Y(layout.DestY),
                layout.DestWidth * scale, layout.DestHeight * scale);
            var src = new Rect(
                layout.SrcX * bitmap.PixelSize.Width,
                layout.SrcY * bitmap.PixelSize.Height,
                Math.Max(1, layout.SrcWidth * bitmap.PixelSize.Width),
                Math.Max(1, layout.SrcHeight * bitmap.PixelSize.Height));
            // Clip to the content box (margins ∩ printable) so an oversized Actual/Custom image
            // cannot paint over the margins, then to the sheet for safety.
            var box = new Rect(X(layout.BoxX), Y(layout.BoxY),
                Math.Max(0, layout.BoxWidth * scale), Math.Max(0, layout.BoxHeight * scale));
            var pageRect = new Rect(ox, oy, paperW * scale, paperH * scale);
            using (context.PushClip(pageRect))
            using (context.PushClip(box))
            {
                context.FillRectangle(Brushes.White, box);
                if (layout.Rotated)
                {
                    // Auto-rotate turns the page content 90 degrees; draw the real image rotated
                    // instead of stretching the unrotated bitmap into the rotated destination.
                    var cx = dest.X + dest.Width / 2;
                    var cy = dest.Y + dest.Height / 2;
                    var transform = Matrix.CreateTranslation(-cx, -cy)
                        * Matrix.CreateRotation(Math.PI / 2)
                        * Matrix.CreateTranslation(cx, cy);
                    using (context.PushTransform(transform))
                        context.DrawImage(bitmap, src, new Rect(cx - dest.Height / 2, cy - dest.Width / 2, dest.Height, dest.Width));
                }
                else
                {
                    context.DrawImage(bitmap, src, dest);
                }
            }
            context.DrawRectangle(new Pen(Brushes.Gray, 1), dest);
        }

        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 1),
            new Rect(ox, oy, paperW * scale, paperH * scale));
    }

    /// <summary>
    /// Produces a luminance-grayscale copy of a bitmap for the grayscale preview. Runs off the UI
    /// thread; the caller owns and disposes the result.
    /// </summary>
    public static Bitmap? CreateGrayscale(Bitmap source)
    {
        try
        {
            var w = source.PixelSize.Width;
            var h = source.PixelSize.Height;
            if (w <= 0 || h <= 0) return null;
            var stride = w * 4;
            var byteCount = (long)stride * h;
            if (byteCount <= 0 || byteCount > int.MaxValue) return null;
            var target = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            try
            {
                var bytes = new byte[(int)byteCount];
                using (var fb = target.Lock())
                {
                    source.CopyPixels(fb, AlphaFormat.Unpremul);
                    Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
                }
                for (var i = 0; i + 3 < bytes.Length; i += 4)
                {
                    var b = bytes[i];
                    var g = bytes[i + 1];
                    var r = bytes[i + 2];
                    var y = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
                    bytes[i] = y;
                    bytes[i + 1] = y;
                    bytes[i + 2] = y;
                }
                using (var fb = target.Lock())
                    Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
                return target;
            }
            catch { target.Dispose(); return null; }
        }
        catch { return null; }
    }
}
