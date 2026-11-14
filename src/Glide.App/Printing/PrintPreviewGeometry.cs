namespace Glide.App.Printing;

/// <summary>Preview-only geometry helpers. Nothing here is used by the spool path.</summary>
public static class PrintPreviewGeometry
{
    /// <summary>Printable inset used for a provisional sheet when nothing better is known (a quarter inch).</summary>
    public const double DefaultInsetHundredths = 25;

    /// <summary>
    /// A provisional portrait geometry for <paramref name="paper"/>, drawn the moment the user picks a paper size while
    /// the driver is still being asked for the real printable rectangle. The sheet size is exact (it comes from the
    /// driver's paper list); the printable insets are borrowed from <paramref name="previous"/> (same printer, usually
    /// identical) or a quarter inch. Replaced by the driver's answer as soon as it arrives.
    /// </summary>
    public static PrintPaperGeometry Provisional(PrintPaperOption paper, PrintPaperGeometry? previous)
    {
        double w = Math.Max(1, paper.WidthHundredths);
        double h = Math.Max(1, paper.HeightHundredths);
        double left = DefaultInsetHundredths, top = DefaultInsetHundredths, right = DefaultInsetHundredths, bottom = DefaultInsetHundredths;
        if (previous is not null && previous.PrintableWidth > 0 && previous.PrintableHeight > 0)
        {
            left = Math.Max(0, previous.PrintableX);
            top = Math.Max(0, previous.PrintableY);
            right = Math.Max(0, previous.PaperWidthHundredths - previous.PrintableX - previous.PrintableWidth);
            bottom = Math.Max(0, previous.PaperHeightHundredths - previous.PrintableY - previous.PrintableHeight);
        }
        // Never let borrowed insets swallow a small sheet.
        if (left + right >= w * 0.5) { left = right = Math.Min(DefaultInsetHundredths, w / 8); }
        if (top + bottom >= h * 0.5) { top = bottom = Math.Min(DefaultInsetHundredths, h / 8); }
        return new PrintPaperGeometry(w, h, left, top, w - left - right, h - top - bottom);
    }
}
