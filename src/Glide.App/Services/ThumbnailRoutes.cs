using System.Collections.Concurrent;
using Glide.App.Platform;
using Glide.Core;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>The decoder family an Explorer thumbnail of a file comes from (5.2.0).</summary>
internal enum ThumbnailRoute
{
    /// <summary>No thumbnail: the tile keeps the file-type glyph.</summary>
    None,
    /// <summary>libjpeg-turbo DCT-scaled decode (the JPEG family, MPO included).</summary>
    Jpeg,
    /// <summary>The native thumbnail bridge: libwebp, banded TIFF / BMP, JPEG XR decoder-native size.</summary>
    NativeBridge,
    /// <summary>The vector rasteriser at tile size.</summary>
    Svg,
    /// <summary>The bundled managed decoders (camera RAW embedded preview, PSD, EXR, HDR, TGA, PPM, QOI, DDS, ...), bounded to the tile.</summary>
    BuiltIn,
    /// <summary>A bundled codec plug-in (HEIC, AVIF, JPEG XL, JPEG 2000), its reduced-size decode.</summary>
    Provider,
    /// <summary>Avalonia's / Skia's scaled decode (PNG, APNG, GIF, ICO, CUR).</summary>
    Skia,
    /// <summary>The viewer's WIC bridge, bounded to the tile.</summary>
    Wic,
}

/// <summary>
/// Which decoder produces the Explorer-tab thumbnail of a file, decided by extension only (so a tile never reads the file to
/// choose). Before 5.2.0 the tab asked for a thumbnail for 11 suffixes; now every format the viewer opens has a route
/// (the cheapest reduced-size path of its decoder), and <see cref="ThumbnailRoute.None"/> is only returned for suffixes the
/// viewer itself cannot decode here (they keep the file-type glyph).
/// </summary>
internal static class ThumbnailRoutes
{
    /// <summary>The JPEG family libjpeg-turbo reads by content; MPO is a JPEG whose first image is the picture.</summary>
    private static readonly HashSet<string> JpegFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".jif", ".jfi", ".pjpeg", ".pjpg", ".mpo",
    };

    private static readonly HashSet<string> SkiaFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".apng", ".gif", ".ico", ".cur",
    };

    private static readonly HashSet<string> ProviderFamily = new(BundledCodecLibraries.BundledExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ThumbnailRoute> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: forget the per-extension decisions (a test that changes what the platform offers).</summary>
    internal static void ResetForTests() => ByExtension.Clear();

    public static ThumbnailRoute RouteFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ThumbnailRoute.None;
        var extension = ImageFormatRegistry.Match(path);
        return extension is null ? ThumbnailRoute.None : ByExtension.GetOrAdd(extension, Classify);
    }

    public static bool CanThumbnail(string? path) => RouteFor(path) != ThumbnailRoute.None;

    /// <summary>True for the JPEG family the native JPEG thumbnail reads (the viewer's own <c>IsJpegPath</c> is narrower on purpose).</summary>
    public static bool IsJpegFamily(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.Match(path);
        return extension is not null && JpegFamily.Contains(extension);
    }

    /// <summary>The suffixes <c>GlideDecodeJpegThumbnailW</c> accepts (the bridge checks the name); the in-memory export is used for the same ones.</summary>
    public static bool IsNativeJpegSuffix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jif", StringComparison.OrdinalIgnoreCase);
    }

    private static ThumbnailRoute Classify(string extension)
    {
        if (JpegFamily.Contains(extension)) return ThumbnailRoute.Jpeg;
        if (extension is ".svg" or ".svgz") return ThumbnailRoute.Svg;
        var probe = "x" + extension;
        if (NativeImageDecoder.SupportsNativeThumbnail(probe)) return ThumbnailRoute.NativeBridge;
        if (SkiaFamily.Contains(extension)) return ThumbnailRoute.Skia;
        if (BuiltInRasterDecoder.CanDecode(probe)) return ThumbnailRoute.BuiltIn;
        if (ProviderFamily.Contains(extension)) return ThumbnailRoute.Provider;
        var tier = CodecCapabilityRegistry.Describe(extension).Tier;
        return tier is CodecTier.NativeOs or CodecTier.BuiltIn ? ThumbnailRoute.Wic : ThumbnailRoute.None;
    }
}
