using System.IO.Compression;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace Glide.Imaging;

/// <summary>
/// Vector SVG/SVGZ rasteriser. SVG is resolution independent, so "decoding" means rendering the
/// document at the resolution the caller needs: a bounded longest side for previews, or the
/// document's intrinsic size capped to a safe maximum for full frames. Uses Svg.Skia (the same
/// SkiaSharp stack Avalonia already ships).
/// </summary>
public static class SvgDecoder
{
    /// <summary>Cap for full-resolution vector renders so a huge viewBox cannot exhaust memory.</summary>
    public const int DefaultFullLongestSide = 4096;

    public static bool IsSvgPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase));

    public static bool TryDecode(string path, int maxLongestSide, out Bitmap? bitmap)
    {
        bitmap = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using var svg = new SKSvg();
            var picture = LoadPicture(svg, path);
            if (picture is null) return false;
            var cull = picture.CullRect;
            var intrinsicW = cull.Width > 0 ? cull.Width : 1000f;
            var intrinsicH = cull.Height > 0 ? cull.Height : 1000f;
            var longest = maxLongestSide > 0 ? Math.Min(maxLongestSide, 32768) : DefaultFullLongestSide;
            var scale = longest / Math.Max(intrinsicW, intrinsicH);
            // A bounded request is a preview and may upscale (vectors stay crisp). An unbounded
            // "full" request is the print/1:1 source, so only ever downscale to the safety cap.
            if (maxLongestSide <= 0) scale = Math.Min(1.0f, scale);
            var targetW = Math.Max(1, (int)Math.Round(intrinsicW * scale));
            var targetH = Math.Max(1, (int)Math.Round(intrinsicH * scale));
            if (targetW > 32768 || targetH > 32768) return false;
            bitmap = Render(picture, targetW, targetH);
            return bitmap is not null;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    public static bool TryProbeDimensions(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            if (!IsSvgPath(path) || !File.Exists(path)) return false;
            using var svg = new SKSvg();
            var picture = LoadPicture(svg, path);
            if (picture is null) return false;
            var cull = picture.CullRect;
            if (cull.Width <= 0 || cull.Height <= 0) return false;
            dimensions = new ImageDimensions((int)Math.Ceiling(cull.Width), (int)Math.Ceiling(cull.Height));
            return dimensions.IsValid;
        }
        catch { return false; }
    }

    private static SKPicture? LoadPicture(SKSvg svg, string path)
    {
        if (path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return svg.Load(gzip);
        }
        return svg.Load(path);
    }

    private static unsafe Bitmap? Render(SKPicture picture, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawPicture(picture);
        canvas.Flush();
        using var image = surface.Snapshot();
        using var skBitmap = SKBitmap.FromImage(image);
        if (skBitmap is null) return null;
        var sourceInfo = skBitmap.Info;
        var source = skBitmap.GetPixels();
        if (source == IntPtr.Zero) return null;

        var target = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using (var fb = target.Lock())
            {
                var rowBytes = (long)width * 4;
                if (fb.RowBytes < rowBytes) return null;
                for (var y = 0; y < height; y++)
                {
                    var src = (byte*)source + (long)y * sourceInfo.RowBytes;
                    var dst = (byte*)fb.Address + (long)y * fb.RowBytes;
                    Buffer.MemoryCopy(src, dst, fb.RowBytes, rowBytes);
                }
            }
            return target;
        }
        catch
        {
            target.Dispose();
            return null;
        }
    }
}
