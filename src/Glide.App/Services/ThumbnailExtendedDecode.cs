using Avalonia;
using Avalonia.Media.Imaging;
using Glide.Core;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// The reduced-size decodes behind the Explorer thumbnails of the formats beyond the original 11 (5.2.0). Each takes the
/// cheapest path its decoder has: the camera's embedded preview for RAW, the bounded managed decoders for PSD / EXR / HDR /
/// TGA / ..., the codec plug-ins' reduced decode for HEIC / AVIF / JPEG XL / JPEG 2000, the vector rasteriser at tile size
/// for SVG. Every method returns null when it cannot (the caller then shows the file-type glyph) and never throws.
/// </summary>
internal static class ThumbnailExtendedDecode
{
    /// <summary>
    /// The longest side to ask a decoder for so that the picture comes out <paramref name="width"/> wide (a portrait picture
    /// needs a longer side than a landscape one). Never above the source's own longest side.
    /// </summary>
    internal static int LongestSideForWidth(int sourceWidth, int sourceHeight, int width)
    {
        width = Math.Max(1, width);
        if (sourceWidth <= 0 || sourceHeight <= 0) return width;
        var longest = sourceHeight > sourceWidth ? (int)Math.Ceiling(width * (double)sourceHeight / sourceWidth) : width;
        return Math.Clamp(longest, 1, Math.Max(sourceWidth, sourceHeight));
    }

    /// <summary>Picture wider than 1.5x the tile: reduced to the tile width (area average); otherwise returned as it is.</summary>
    internal static Bitmap FitToWidth(Bitmap bitmap, int width)
    {
        var size = bitmap.PixelSize;
        if (width <= 0 || size.Width <= width + width / 2) return bitmap;
        var height = Math.Max(1, (int)Math.Round(size.Height * (double)width / size.Width));
        var reduced = FrameDownscaler.TryAreaDownscale(bitmap, new PixelSize(width, height), maxWorkers: 1);
        if (reduced is null) return bitmap;
        bitmap.Dispose();
        return reduced;
    }

    /// <summary>Managed bundled decoders, bounded to the tile (camera RAW reads only the smallest embedded preview that covers it).</summary>
    public static Bitmap? DecodeBuiltIn(string path, int width)
    {
        try
        {
            var longest = BuiltInRasterDecoder.TryProbeDimensions(path, out var dimensions)
                ? LongestSideForWidth(dimensions.Width, dimensions.Height, width)
                : width;
            if (!BuiltInRasterDecoder.TryDecodeBounded(path, longest, out var bitmap) || bitmap is null) return null;
            return FitToWidth(bitmap, width);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>The viewer's WIC bridge bounded to the tile (a full decode then a reduction: used only where nothing cheaper exists).</summary>
    public static Bitmap? DecodeWic(string path, int width)
    {
        try
        {
            if (!NativeImageDecoder.TryDecodeBounded(path, width, 65535, out var bitmap) || bitmap is null) return null;
            return FitToWidth(bitmap, width);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// A bundled codec plug-in's reduced decode (HEIC, AVIF, JPEG XL, JPEG 2000). The plug-in is resolved through the shared
    /// runtime (hash check and load happen once per process, or earlier through <see cref="CodecPreloader"/>); null when the
    /// plug-in is missing or declines the file.
    /// </summary>
    public static Bitmap? DecodeProvider(string path, int width)
    {
        try
        {
            var provider = CodecProviderRuntime.Shared.ResolveProvider(path);
            if (provider is null || !provider.Info.Preview) return null;
            var probe = provider.Probe(path);
            var longest = probe.Supported
                ? LongestSideForWidth(probe.Dimensions.Width, probe.Dimensions.Height, width)
                : width;
            if (provider is ICodecSurfaceProvider surfaceProvider)
            {
                using var surface = surfaceProvider.DecodePreviewSurfaceAsync(path, longest).GetAwaiter().GetResult();
                return surface is null ? null : FitToWidth(CodecSurfaceBitmapFactory.Create(surface), width);
            }
            using var stream = provider.DecodePreviewAsync(path, longest).GetAwaiter().GetResult();
            return stream is null ? null : FitToWidth(new Bitmap(stream), width);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// SVG / SVGZ rendered at the tile size: Direct2D for plain path-heavy documents, Svg.Skia for the rest
    /// (<see cref="SvgTileRenderer"/>).
    /// </summary>
    public static Bitmap? DecodeSvg(string path, int width)
    {
        try
        {
            return SvgTileRenderer.TryRender(path, width, out var bitmap) && bitmap is not null ? bitmap : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
