using System.Drawing;
using System.Drawing.Imaging;

namespace Glide.App.Printing;

/// <summary>
/// Console acceptance harness for image printing. Produces a real PDF/spool output through the
/// exact same <see cref="PrintService.PrintAsync"/> geometry+render path the UI dialog uses, so
/// `--print-to-pdf` can verify portraits, landscapes, EXIF rotation, DPI handling, transparency
/// and clipping without touching the interactive viewer. Diagnostic/test-only; never invoked by
/// normal startup.
/// </summary>
public static class PrintAcceptance
{
    public sealed record SampleInfo(int WidthPx, int HeightPx, double DpiX, double DpiY, int Orientation, string Detail);

    public static SampleInfo Probe(string imagePath)
    {
        var (dx, dy, detail) = PrintDpiReader.GetDpi(imagePath);
        using var image = OpenOrientationAdjusted(imagePath, out var orientation);
        return new SampleInfo(image.Width, image.Height, dx, dy, orientation, detail);
    }

    /// <summary>Decodes with GDI+ and applies the EXIF orientation tag, mirroring the app's oriented bitmap.</summary>
    private static Bitmap OpenOrientationAdjusted(string imagePath, out int orientation)
    {
        var raw = new Bitmap(imagePath);
        orientation = 1;
        try
        {
            var prop = raw.GetPropertyItem(0x0112); // PropertyTagOrientation
            if (prop?.Value is { Length: >= 2 }) orientation = BitConverter.ToUInt16(prop.Value, 0);
        }
        catch { orientation = 1; }
        if (orientation is < 2 or > 8) { orientation = 1; return raw; }
        var copy = (Bitmap)raw.Clone();
        raw.Dispose();
        switch (orientation)
        {
            case 2: copy.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
            case 3: copy.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 4: copy.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
            case 5: copy.RotateFlip(RotateFlipType.Rotate90FlipX); break;
            case 6: copy.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 7: copy.RotateFlip(RotateFlipType.Rotate270FlipX); break;
            case 8: copy.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
        return copy;
    }

    public static async Task<int> PrintToFileAsync(
        string imagePath, string outputPath, string printerName,
        string scalingMode, bool landscape, double marginLeft, double marginTop,
        double marginRight, double marginBottom, bool keepAspect, bool autoRotate,
        string hAlign, string vAlign, double customScale, short copies, bool color,
        TextWriter log)
    {
        if (!OperatingSystem.IsWindows()) { log.WriteLine("Printing requires Windows."); return 2; }
        if (!File.Exists(imagePath)) { log.WriteLine($"Image not found: {imagePath}"); return 2; }
        try
        {
            var info = Probe(imagePath);
            log.WriteLine($"image={Path.GetFileName(imagePath)} oriented={info.WidthPx}x{info.HeightPx} dpi={info.DpiX:F1}x{info.DpiY:F1} ({info.Detail}) exifOrientation={info.Orientation}");

            // Re-encode the oriented pixels to lossless PNG exactly like the interactive path.
            byte[] png;
            using (var oriented = OpenOrientationAdjusted(imagePath, out _))
            using (var ms = new MemoryStream())
            {
                oriented.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            log.WriteLine($"encodedPng={png.Length} bytes; printer={printerName} scaling={scalingMode} landscape={landscape}");

            var caps = PrintService.GetCapabilities(printerName);
            log.WriteLine($"printer papers={caps.Papers.Count} sources={caps.Sources.Count} color={caps.SupportsColor} defaultPaper={caps.DefaultPaperName}");

            var scaling = scalingMode switch
            {
                "Fill page" => PrintScalingMode.FillPage,
                "Actual size" => PrintScalingMode.ActualSize,
                "Custom scale" => PrintScalingMode.CustomScale,
                "Stretch" => PrintScalingMode.Stretch,
                _ => PrintScalingMode.BestFit
            };
            var request = new PrintJobRequest(
                imagePath, info.WidthPx, info.HeightPx, info.DpiX, info.DpiY,
                printerName, caps.DefaultPaperName, caps.DefaultSourceName, landscape,
                marginLeft, marginTop, marginRight, marginBottom,
                scaling, customScale, keepAspect, autoRotate,
                hAlign == "Left" ? PrintHAlign.Left : hAlign == "Right" ? PrintHAlign.Right : PrintHAlign.Centre,
                vAlign == "Top" ? PrintVAlign.Top : vAlign == "Bottom" ? PrintVAlign.Bottom : PrintVAlign.Centre,
                copies, color, outputPath);

            var progress = new Progress<string>(m => log.WriteLine("  " + m));
            await PrintService.PrintAsync(() => new MemoryStream(png, writable: false), request, progress).ConfigureAwait(false);
            var produced = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            log.WriteLine($"output={outputPath} bytes={produced}");
            return produced > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.WriteLine("PRINT FAILED: " + ex);
            return 1;
        }
    }
}
