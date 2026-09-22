using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;

#pragma warning disable CA1416 // PrintService is Windows-only by design; every entry point requires Windows first.

namespace Glide.App.Printing;

/// <summary>
/// The single image-printing job descriptor. The dialog resolves the printer/paper/source
/// selection into portrait-orientation driver geometry; the spool path re-queries the live
/// driver at print start and recomputes the page layout with <see cref="PrintLayoutEngine"/>
/// (the same function the preview uses), so preview and paper can never diverge.
/// </summary>
public sealed record PrintJobRequest(
    string ImagePath,
    int ImageWidthPx,
    int ImageHeightPx,
    double DpiX,
    double DpiY,
    string PrinterName,
    string PaperName,
    string PaperSourceName,
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
    PrintVAlign VAlign,
    short Copies,
    bool PrintColor,
    // Optional silent output path (Microsoft Print to PDF acceptance tests).
    // Null means normal spool (the driver shows its own Save-As prompt if needed).
    string? PrintToFilePath);

public sealed record PrintPaperOption(string Name, int WidthHundredths, int HeightHundredths, PaperKind Kind);
public sealed record PrintSourceOption(string Name, PaperSourceKind Kind);

public sealed record PrintPrinterCapabilities(
    string PrinterName,
    IReadOnlyList<PrintPaperOption> Papers,
    IReadOnlyList<PrintSourceOption> Sources,
    string DefaultPaperName,
    string DefaultSourceName,
    bool SupportsColor,
    bool SupportsDuplex);

public sealed class PrintNoPrinterException : Exception
{
    public PrintNoPrinterException(string message) : base(message) { }
}

/// <summary>
/// Windows-only image print pipeline (GDI+/System.Drawing). The assembly is loaded only
/// through this type, which is first touched by the Print command — never at startup.
/// All public methods no-op/throw a friendly <see cref="PrintNoPrinterException"/>
/// off Windows or when no printer exists.
/// </summary>
public static class PrintService
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static IReadOnlyList<string> GetPrinterNames()
    {
        RequireWindows();
        var names = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            names.Add(name);
        return names;
    }

    public static string? GetDefaultPrinterName()
    {
        RequireWindows();
        try
        {
            using var doc = new PrintDocument();
            return string.IsNullOrWhiteSpace(doc.PrinterSettings.PrinterName) ? null : doc.PrinterSettings.PrinterName;
        }
        catch { return null; }
    }

    public static PrintPrinterCapabilities GetCapabilities(string printerName)
    {
        RequireWindows();
        using var doc = new PrintDocument();
        if (!string.IsNullOrWhiteSpace(printerName)) doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new PrintNoPrinterException($"Printer '{printerName}' is not available.");
        var papers = new List<PrintPaperOption>();
        foreach (PaperSize size in doc.PrinterSettings.PaperSizes)
        {
            if (size.Width <= 0 || size.Height <= 0) continue;
            papers.Add(new PrintPaperOption(size.PaperName, size.Width, size.Height, size.Kind));
        }
        var sources = new List<PrintSourceOption>();
        foreach (PaperSource source in doc.PrinterSettings.PaperSources)
            sources.Add(new PrintSourceOption(source.SourceName, source.Kind));
        var defPaper = doc.DefaultPageSettings.PaperSize?.PaperName ?? papers.FirstOrDefault()?.Name ?? "";
        var defSource = doc.DefaultPageSettings.PaperSource?.SourceName ?? sources.FirstOrDefault()?.Name ?? "";
        return new PrintPrinterCapabilities(
            doc.PrinterSettings.PrinterName, papers, sources,
            defPaper, defSource,
            doc.PrinterSettings.SupportsColor, true);
    }

    /// <summary>
    /// Reads the driver's real paper + printable-area geometry for a paper/source/orientation
    /// choice. Both preview and spooling build their <see cref="PrintPaperGeometry"/> here.
    /// </summary>
    public static PrintPaperGeometry GetPaperGeometry(string printerName, string paperName, string sourceName, bool landscape)
    {
        RequireWindows();
        using var doc = new PrintDocument();
        if (!string.IsNullOrWhiteSpace(printerName)) doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new PrintNoPrinterException($"Printer '{printerName}' is not available.");
        ApplyPaperSelection(doc, paperName, sourceName);
        doc.DefaultPageSettings.Landscape = landscape;
        var size = doc.DefaultPageSettings.PaperSize
            ?? throw new PrintNoPrinterException("The printer reported no paper sizes.");
        var area = doc.DefaultPageSettings.PrintableArea; // hundredths of an inch, paper origin
        var geo = new PrintPaperGeometry(size.Width, size.Height, area.X, area.Y, area.Width, area.Height);
        if (geo.PrintableWidth <= 0 || geo.PrintableHeight <= 0)
            geo = geo with { PrintableX = 0, PrintableY = 0, PrintableWidth = size.Width, PrintableHeight = size.Height };
        return geo;
    }

    public static async Task PrintAsync(
        Func<Stream> openGdiSourceStream,
        PrintJobRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireWindows();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Preparing image…");

        // Decode once on a worker thread. The stream supplier re-encodes the already
        // EXIF-oriented full-resolution Avalonia bitmap to lossless PNG, so GDI+ sees
        // exactly the pixels Glide displays (no viewport chrome, zoom or pan baked in).
        using var gdi = await Task.Run(() =>
        {
            using var stream = openGdiSourceStream();
            var bmp = new Bitmap(stream);
            return bmp;
        }, cancellationToken).ConfigureAwait(false);

        using var doc = new PrintDocument
        {
            PrinterSettings = { PrinterName = request.PrinterName },
            DocumentName = MakeDocumentName(request.ImagePath)
        };
        if (!doc.PrinterSettings.IsValid)
            throw new PrintNoPrinterException($"Printer '{request.PrinterName}' is not available.");
        ApplyPaperSelection(doc, request.PaperName, request.PaperSourceName);
        doc.DefaultPageSettings.Landscape = request.Landscape;
        doc.DefaultPageSettings.Color = doc.PrinterSettings.SupportsColor && request.PrintColor;
        doc.PrinterSettings.Copies = (short)Math.Clamp((int)request.Copies, 1, 99);
        if (!string.IsNullOrWhiteSpace(request.PrintToFilePath))
        {
            doc.PrinterSettings.PrintToFile = true;
            doc.PrinterSettings.PrintFileName = request.PrintToFilePath;
        }

        // Snapshot the live driver geometry and compute the page with the shared engine.
        var live = GetPaperGeometry(request.PrinterName, request.PaperName, request.PaperSourceName, request.Landscape);
        var layout = PrintLayoutEngine.Compute(new PrintLayoutInput(
            request.ImageWidthPx, request.ImageHeightPx, request.DpiX, request.DpiY,
            live, request.Landscape,
            request.MarginLeftHundredths, request.MarginTopHundredths,
            request.MarginRightHundredths, request.MarginBottomHundredths,
            request.ScalingMode, request.CustomScalePercent, request.KeepAspectRatio,
            request.AutoRotate, request.HAlign, request.VAlign));

        var cancelled = false;
        using var registration = cancellationToken.Register(() => cancelled = true);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;

        doc.PrintPage += (_, e) =>
        {
            try
            {
                if (cancelled || cancellationToken.IsCancellationRequested) { e.Cancel = true; e.HasMorePages = false; return; }
                if (e.Graphics is null) { e.Cancel = true; e.HasMorePages = false; return; }
                RenderPage(e.Graphics, gdi, layout);
                e.HasMorePages = false;
            }
            catch (Exception ex) { failure = ex; e.Cancel = true; e.HasMorePages = false; }
        };
        doc.EndPrint += (_, _) => done.TrySetResult();

        progress?.Report("Printing…");
        await Task.Run(() =>
        {
            try { doc.Print(); }
            catch (Exception ex) { failure = ex; done.TrySetResult(); }
        }, CancellationToken.None).ConfigureAwait(false);
        await done.Task.ConfigureAwait(false);
        if (failure is not null)
            throw new InvalidOperationException("Printing failed: " + failure.Message, failure);
        if (cancelled) throw new OperationCanceledException(cancellationToken);
        progress?.Report("Done.");
    }

    internal static void RenderPage(System.Drawing.Graphics g, Image image, PrintPageLayout layout)
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;

        float UnitScale() => g.PageUnit switch
        {
            // Layout is in hundredths of an inch. PrintPage graphics default to Display
            // (1/100 inch) on printers, but be explicit for every possible page unit.
            GraphicsUnit.Pixel => (float)g.DpiX,
            GraphicsUnit.Inch => 0.01f,
            GraphicsUnit.Millimeter => 0.254f,
            GraphicsUnit.Point => 0.72f,
            _ => 1f
        };
        var unit = UnitScale();
        float H(double hundredths) => (float)(hundredths * unit);
        float V(double hundredths) => (float)(hundredths * unit);

        var dest = new RectangleF(H(layout.DestX), V(layout.DestY), H(layout.DestWidth), V(layout.DestHeight));
        if (string.Equals(Environment.GetEnvironmentVariable("GLIDE_PRINT_DEBUG"), "1", StringComparison.Ordinal))
            Console.WriteLine($"  render dest=({dest.X:F0},{dest.Y:F0},{dest.Width:F0},{dest.Height:F0}) pageUnit={g.PageUnit} clip={g.VisibleClipBounds}");
        // Alpha has nowhere to go on paper: composite against white (the dest-sized white
        // fill doubles as the page background behind the image).
        using (var white = new SolidBrush(Color.White))
            g.FillRectangle(white, dest);

        var sx = (float)(layout.SrcX * image.Width);
        var sy = (float)(layout.SrcY * image.Height);
        var sw = Math.Max(1f, (float)(layout.SrcWidth * image.Width));
        var sh = Math.Max(1f, (float)(layout.SrcHeight * image.Height));
        var src = new RectangleF(sx, sy, sw, sh);
        // Clip to the destination so an Actual/Custom-size overflow cannot paint the margins.
        var clip = g.Clip.Clone();
        try
        {
            g.SetClip(dest);
            g.DrawImage(image, dest, src, GraphicsUnit.Pixel);
        }
        finally { g.Clip = clip; clip.Dispose(); }
    }

    private static void ApplyPaperSelection(PrintDocument doc, string paperName, string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(paperName))
        {
            foreach (PaperSize size in doc.PrinterSettings.PaperSizes)
            {
                if (string.Equals(size.PaperName, paperName, StringComparison.OrdinalIgnoreCase))
                {
                    doc.DefaultPageSettings.PaperSize = size;
                    break;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            foreach (PaperSource source in doc.PrinterSettings.PaperSources)
            {
                if (string.Equals(source.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                {
                    doc.DefaultPageSettings.PaperSource = source;
                    break;
                }
            }
        }
    }

    private static string MakeDocumentName(string imagePath)
    {
        try
        {
            var name = Path.GetFileName(imagePath);
            return string.IsNullOrWhiteSpace(name) ? "Glide image" : $"Glide — {name}";
        }
        catch { return "Glide image"; }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Printing is only supported on Windows.");
    }
}
