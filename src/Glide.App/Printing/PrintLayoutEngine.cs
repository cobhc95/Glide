namespace Glide.App.Printing;

/// <summary>
/// Image scaling behaviours offered by the Glide Print dialog. The names mirror the
/// mature-viewer vocabulary (IrfanView Best fit / Stretch / Original size / Custom,
/// XnView best-fit/crop/stretch, FastStone full page-layout control).
/// </summary>
public enum PrintScalingMode
{
    /// <summary>As large as possible inside the content box, aspect ratio preserved.</summary>
    BestFit,
    /// <summary>Cover the whole content box; the overflowing axis is centre/aligned-cropped.</summary>
    FillPage,
    /// <summary>One image pixel maps to 1/dpi inch (embedded DPI else <see cref="PrintDpiReader"/> default).</summary>
    ActualSize,
    /// <summary>Actual size scaled by <see cref="PrintLayoutInput.CustomScalePercent"/>.</summary>
    CustomScale,
    /// <summary>Stretch exactly to the content box, aspect ratio not preserved.</summary>
    Stretch
}

public enum PrintHAlign { Left, Centre, Right }
public enum PrintVAlign { Top, Centre, Bottom }

/// <summary>
/// Paper geometry in the printer's portrait orientation, hundredths of an inch
/// (the Win32/.NET print unit). The engine swaps to landscape itself so preview
/// and spooling can never disagree about orientation handling.
/// </summary>
public sealed record PrintPaperGeometry(
    double PaperWidthHundredths,
    double PaperHeightHundredths,
    double PrintableX,
    double PrintableY,
    double PrintableWidth,
    double PrintableHeight);

public sealed record PrintLayoutInput(
    int ImageWidthPx,
    int ImageHeightPx,
    double DpiX,
    double DpiY,
    PrintPaperGeometry PaperPortrait,
    bool Landscape,
    double MarginLeftHundredths,
    double MarginTopHundredths,
    double MarginRightHundredths,
    double MarginBottomHundredths,
    PrintScalingMode ScalingMode,
    double CustomScalePercent,
    bool KeepAspectRatio,
    bool AutoRotate,
    PrintHAlign HAlign,
    PrintVAlign VAlign);

/// <summary>
/// Single-page geometry in paper coordinates (hundredths of an inch, origin top-left
/// of the physical sheet in the *current* orientation). The same record drives the
/// Avalonia preview and the GDI spool path, so WYSIWYG divergence is impossible.
/// </summary>
public sealed record PrintPageLayout(
    bool Rotated,
    double DestX,
    double DestY,
    double DestWidth,
    double DestHeight,
    double SrcX,
    double SrcY,
    double SrcWidth,
    double SrcHeight,
    double ScaleUsed,
    bool Clipped,
    string Warning);

/// <summary>
/// Pure page-geometry calculator for image printing. No UI, no printer handles, no
/// bitmaps: fully unit-testable. All dimensions are hundredths of an inch unless noted.
/// </summary>
public static class PrintLayoutEngine
{
    /// <summary>
    /// Computes where the image lands on the sheet. The content box is the user-margin
    /// box intersected with the driver's real printable rectangle: a zero user margin
    /// therefore means "to the printable edge", never "over the unprintable edge"
    /// (the historical XnView preview/print divergence).
    /// </summary>
    public static PrintPageLayout Compute(PrintLayoutInput input)
    {
        var imgW = Math.Max(1, input.ImageWidthPx);
        var imgH = Math.Max(1, input.ImageHeightPx);
        var dpiX = PrintDpiReader.NormalizeDpi(input.DpiX);
        var dpiY = PrintDpiReader.NormalizeDpi(input.DpiY);

        // Current-orientation sheet + printable rect.
        var paperW = input.PaperPortrait.PaperWidthHundredths;
        var paperH = input.PaperPortrait.PaperHeightHundredths;
        var prX = input.PaperPortrait.PrintableX;
        var prY = input.PaperPortrait.PrintableY;
        var prW = input.PaperPortrait.PrintableWidth;
        var prH = input.PaperPortrait.PrintableHeight;
        if (input.Landscape)
        {
            (paperW, paperH) = (paperH, paperW);
            (prX, prY, prW, prH) = (prY, prX, prH, prW);
        }
        if (paperW <= 0 || paperH <= 0) throw new ArgumentOutOfRangeException(nameof(input), "Paper size must be positive.");
        if (prW <= 0 || prH <= 0)
        {
            prX = 0; prY = 0; prW = paperW; prH = paperH;
        }

        // User-margin box, then intersect with the printable rect.
        var boxX = Math.Max(0, input.MarginLeftHundredths);
        var boxY = Math.Max(0, input.MarginTopHundredths);
        var boxR = Math.Min(paperW, paperW - Math.Max(0, input.MarginRightHundredths));
        var boxB = Math.Min(paperH, paperH - Math.Max(0, input.MarginBottomHundredths));
        var boxW = boxR - boxX;
        var boxH = boxB - boxY;
        var clampedToPrintable = false;
        var ix = Math.Max(boxX, prX);
        var iy = Math.Max(boxY, prY);
        var ir = Math.Min(boxR, prX + prW);
        var ib = Math.Min(boxB, prY + prH);
        if (ir - ix <= 0 || ib - iy <= 0)
        {
            ix = prX; iy = prY; ir = prX + prW; ib = prY + prH;
            clampedToPrintable = true;
        }
        else if (ix != boxX || iy != boxY || ir != boxR || ib != boxB)
        {
            clampedToPrintable = true;
        }
        boxX = ix; boxY = iy; boxW = ir - ix; boxH = ib - iy;

        // Image size at actual size, in hundredths of an inch.
        var natW = imgW / dpiX * 100.0;
        var natH = imgH / dpiY * 100.0;

        // Auto-rotate: pick the image orientation that fits best.
        var rotated = false;
        if (input.AutoRotate && imgW != imgH)
        {
            rotated = RotateFitsBetter(input.ScalingMode, natW, natH, boxW, boxH);
            if (rotated) (natW, natH) = (natH, natW);
        }

        var custom = Math.Clamp(input.CustomScalePercent <= 0 ? 100 : input.CustomScalePercent, 1, 3200);
        var keepAspect = input.KeepingAspect();

        double dw, dh, sx = 0, sy = 0, sw = 1, sh = 1, scale;
        bool clipped;
        string warning = clampedToPrintable ? "Limited by the printer's non-printable edge. " : "";

        switch (input.ScalingMode)
        {
            case PrintScalingMode.FillPage:
            {
                // Cover the box; crop the overflowing axis. Alignment selects the kept window.
                // Aspect is preserved by construction (crop, never squeeze).
                var boxAspect = boxW / boxH;
                var imgAspect = natW / natH;
                if (imgAspect > boxAspect)
                {
                    sw = boxAspect / imgAspect;
                    sx = input.HAlign switch { PrintHAlign.Left => 0, PrintHAlign.Right => 1 - sw, _ => (1 - sw) / 2 };
                }
                else
                {
                    sh = imgAspect / boxAspect;
                    sy = input.VAlign switch { PrintVAlign.Top => 0, PrintVAlign.Bottom => 1 - sh, _ => (1 - sh) / 2 };
                }
                dw = boxW; dh = boxH;
                scale = Math.Max(boxW / natW, boxH / natH);
                clipped = true;
                break;
            }
            case PrintScalingMode.ActualSize:
            case PrintScalingMode.CustomScale:
            {
                var factor = input.ScalingMode == PrintScalingMode.CustomScale ? custom / 100.0 : 1.0;
                dw = natW * factor; dh = natH * factor;
                scale = factor;
                clipped = dw > boxW + 1e-9 || dh > boxH + 1e-9;
                if (clipped) warning += "Actual size is larger than the printable area; only the aligned visible part prints. ";
                else if (Math.Max(dw, dh) < Math.Max(boxW, boxH) * 0.2)
                    warning += "The image is small at actual size and will appear tiny on the page. ";
                break;
            }
            case PrintScalingMode.Stretch:
            case PrintScalingMode.BestFit when !keepAspect:
                dw = boxW; dh = boxH;
                scale = Math.Min(boxW / natW, boxH / natH);
                clipped = false;
                if (input.ScalingMode == PrintScalingMode.Stretch || !keepAspect)
                    warning += "Aspect ratio is not preserved in stretch mode. ";
                break;
            default: // BestFit, aspect preserved
                scale = Math.Min(boxW / natW, boxH / natH);
                dw = natW * scale; dh = natH * scale;
                clipped = false;
                break;
        }

        var dx = input.HAlign switch { PrintHAlign.Left => boxX, PrintHAlign.Right => boxX + boxW - dw, _ => boxX + (boxW - dw) / 2 };
        var dy = input.VAlign switch { PrintVAlign.Top => boxY, PrintVAlign.Bottom => boxY + boxH - dh, _ => boxY + (boxH - dh) / 2 };

        return new PrintPageLayout(
            rotated, dx, dy, dw, dh,
            Clamp01(sx), Clamp01(sy), Clamp01(sw), Clamp01(sh),
            scale, clipped, warning.Trim());
    }

    private static bool KeepingAspect(this PrintLayoutInput input) =>
        input.ScalingMode == PrintScalingMode.Stretch ? false : input.KeepAspectRatio;

    private static bool RotateFitsBetter(PrintScalingMode mode, double natW, double natH, double boxW, double boxH)
    {
        if (mode == PrintScalingMode.FillPage)
        {
            // Minimise the cropped fraction.
            var boxAspect = boxW / boxH;
            double Crop(double w, double h)
            {
                var aspect = w / h;
                return aspect > boxAspect ? 1 - boxAspect / aspect : 1 - aspect / boxAspect;
            }
            var straight = Crop(natW, natH);
            var swapped = Crop(natH, natW);
            return swapped < straight - 1e-9;
        }
        if (mode == PrintScalingMode.ActualSize || mode == PrintScalingMode.CustomScale)
        {
            // Maximise the visible (overlapping) area.
            double Visible(double w, double h) => Math.Min(w, boxW) * Math.Min(h, boxH);
            return Visible(natH, natW) > Visible(natW, natH) + 1e-9;
        }
        // BestFit / Stretch: maximise the fit scale.
        return Math.Min(boxW / natH, boxH / natW) > Math.Min(boxW / natW, boxH / natH) + 1e-9;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}

/// <summary>
/// Locale-aware physical-unit helpers. Hundredths of an inch are the Win32 print unit;
/// metric locales (including en-GB) display millimetres, US-style locales display inches.
/// </summary>
public static class PrintUnits
{
    public static bool UseMillimetres => System.Globalization.RegionInfo.CurrentRegion.IsMetric;

    public static string UnitLabel => UseMillimetres ? "mm" : "in";

    public static double ToDisplay(double hundredths) =>
        UseMillimetres ? hundredths / 100.0 * 25.4 : hundredths / 100.0;

    public static double FromDisplay(double display) =>
        UseMillimetres ? display / 25.4 * 100.0 : display * 100.0;

    public static string Format(double hundredths) =>
        UseMillimetres ? $"{ToDisplay(hundredths):F1} mm" : $"{ToDisplay(hundredths):F2} in";
}
