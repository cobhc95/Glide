using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Core;
using System.Runtime.InteropServices;

namespace Glide.Imaging;

/// <summary>
/// Permanent codec boundary. HEIC/AVIF/JXL/RAW/native backends plug in here and inherit
/// the same probe -> preview -> refine -> cache -> prefetch scheduler.
/// </summary>
public interface IImageDecoderBackend
{
    Bitmap DecodeFull(Stream stream);
    Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode);

    CodecProbeResult? Probe(string path, CancellationToken cancellationToken = default) => null;
    Task<CodecProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Probe(path, cancellationToken), cancellationToken);

    Task<ImageMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<ImageMetadata?>(null);
    Task<IReadOnlyList<CodecFrameInfo>?> ReadFramesAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CodecFrameInfo>?>(null);

    Bitmap DecodeFull(Stream stream, string? sourcePath) => DecodeFull(stream);
    Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode, string? sourcePath)
        => DecodePreview(stream, source, longestSide, interpolationMode);

    Task<Bitmap> DecodeFullAsync(Stream stream, string? sourcePath, CancellationToken cancellationToken = default)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DecodeFull(stream, sourcePath));
        }
        return Task.Run(() => DecodeFull(stream, sourcePath), cancellationToken);
    }

    Task<Bitmap> DecodePreviewAsync(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode, string? sourcePath, CancellationToken cancellationToken = default)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DecodePreview(stream, source, longestSide, interpolationMode, sourcePath));
        }
        return Task.Run(() => DecodePreview(stream, source, longestSide, interpolationMode, sourcePath), cancellationToken);
    }
}


public readonly record struct PathPreviewDecodeResult(Bitmap Bitmap, bool IsPreview, string Route);

/// <summary>
/// Optional zero-redundant-open path for codecs that can decode a bounded first frame directly
/// from the source file. The generic stream backend remains the permanent fallback.
/// </summary>
public interface IPathOptimizedImageDecoderBackend
{
    bool SupportsPathPreview(string path);
    Task<PathPreviewDecodeResult?> TryDecodePreviewFromPathAsync(
        string path, ImageDimensions source, int maxWidth, int maxHeight, bool progressiveColorFirstPreview,
        CancellationToken cancellationToken = default);
}

public sealed class AvaloniaImageDecoderBackend : IImageDecoderBackend, IPathOptimizedImageDecoderBackend
{
    public Bitmap DecodeFull(Stream stream) => new(stream);

    public Bitmap DecodeFull(Stream stream, string? sourcePath)
    {
        // Vector SVG is resolution independent and has no raster "full" decode; render it.
        if (SvgDecoder.IsSvgPath(sourcePath) && SvgDecoder.TryDecode(sourcePath!, 0, out var svgFull) && svgFull is not null)
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("svg_full_decode_hit", sourcePath);
            return svgFull;
        }
        // Bundled managed decoders for the long tail of simple raster formats (TGA, PCX, PNM/PAM,
        // QOI, HDR, WBMP, XBM, XPM, SGI) that neither Skia nor WIC can be relied on for.
        if (BuiltInRasterDecoder.CanDecode(sourcePath) &&
            BuiltInRasterDecoder.TryDecode(sourcePath!, out var builtInFull) && builtInFull is not null)
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("builtin_raster_full_decode_hit", sourcePath);
            return builtInFull;
        }
        var nativeMissed = false;
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(sourcePath))
        {
            if (NativeImageDecoder.SupportsDirectNativeDecode(sourcePath))
            {
                NativeImageDecoder.ResetAllocationFailure();
                if (NativeImageDecoder.TryDecode(sourcePath, 0, out var native))
                {
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("native_full_decode_hit", sourcePath);
                    return native;
                }
                // The frame itself could not be allocated: Skia would need the same memory again. Let the
                // caller fall back to the preview (ImageLoadCoordinator handles allocation failures).
                if (NativeImageDecoder.ResetAllocationFailure())
                    throw new InsufficientMemoryException("There is not enough free memory to decode this image at full size.");
                nativeMissed = true;
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("native_full_decode_miss", sourcePath);
            }
        }

        try
        {
            SkiaDecodeGuard.EnsurePlausibleFormat(stream, sourcePath);
            SkiaDecodeGuard.EnsureAllowed(stream, 0);
            var managed = new Bitmap(stream);
            if (nativeMissed) NativeImageDecoder.MarkNativeCodecUnavailable(sourcePath);
            return managed;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(sourcePath) && !SkiaDecodeGuard.IsRefusal(ex))
        {
            // Preserve the known-good Avalonia/Skia path first. Only if it cannot decode do we try
            // Windows' installed WIC codecs, then a genuine Shell thumbnail/preview (never a generic
            // icon). The Shell last resort runs for ANY recognized suffix so formats whose only
            // handler is a registered Windows thumbnail provider still render instead of failing;
            // alpha may be flattened in that case, which is preferable to an unusable file.
            if (NativeImageDecoder.TryDecode(sourcePath!, 0, out var native)) return native;
            if (ImageFormatRegistry.IsSupported(sourcePath!) &&
                NativeImageDecoder.TryDecodeShellPreview(sourcePath!, 4096, out var shell)) return shell;
            throw;
        }
    }

    public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode)
    {
        longestSide = Math.Max(1, longestSide);
        try
        {
            // Skia decodes formats without a scaled codec (PNG, BMP, GIF) at full size before resizing.
            SkiaDecodeGuard.EnsureAllowed(stream, longestSide);
            if (source.Width >= source.Height)
                return Bitmap.DecodeToWidth(stream, Math.Min(longestSide, source.Width), interpolationMode);
            return Bitmap.DecodeToHeight(stream, Math.Min(longestSide, source.Height), interpolationMode);
        }
        catch (Exception ex) when (!SkiaDecodeGuard.IsRefusal(ex))
        {
            if (stream.CanSeek) stream.Position = 0;
            SkiaDecodeGuard.EnsureAllowed(stream, 0);
            return new Bitmap(stream);
        }
    }

    public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode, string? sourcePath)
    {
        // Vector SVG renders directly at the requested preview resolution.
        if (SvgDecoder.IsSvgPath(sourcePath) && SvgDecoder.TryDecode(sourcePath!, longestSide, out var svgPreview) && svgPreview is not null)
            return svgPreview;
        // Bundled managed raster decoders (bounded decode) before falling back to Skia.
        if (BuiltInRasterDecoder.CanDecode(sourcePath) &&
            BuiltInRasterDecoder.TryDecodeBounded(sourcePath!, longestSide, out var builtInPreview) && builtInPreview is not null)
            return builtInPreview;
        // Native previews use the native WIC bridge first. This avoids a full-size Skia
        // decode followed by managed downscaling on the critical browse path and lets WIC request
        // a decoder-scaled first frame where the installed codec supports it.
        var nativeMissed = false;
        if (NativeImageDecoder.SupportsDirectNativeDecode(sourcePath))
        {
            if (NativeImageDecoder.TryDecode(sourcePath!, longestSide, out var nativePreview)) return nativePreview;
            nativeMissed = true;
        }

        try
        {
            SkiaDecodeGuard.EnsurePlausibleFormat(stream, sourcePath);
            var managed = DecodePreview(stream, source, longestSide, interpolationMode);
            if (nativeMissed) NativeImageDecoder.MarkNativeCodecUnavailable(sourcePath);
            return managed;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(sourcePath) && !SkiaDecodeGuard.IsRefusal(ex))
        {
            if (NativeImageDecoder.TryDecode(sourcePath!, longestSide, out var native)) return native;
            if (ImageFormatRegistry.IsSupported(sourcePath!) &&
                NativeImageDecoder.TryDecodeShellPreview(sourcePath!, longestSide, out var shell)) return shell;
            throw;
        }
    }

    public bool SupportsPathPreview(string path) =>
        SvgDecoder.IsSvgPath(path) ||
        BuiltInRasterDecoder.CanDecode(path) ||
        (OperatingSystem.IsWindows() && ImageFormatRegistry.IsSupported(path));

    public Task<PathPreviewDecodeResult?> TryDecodePreviewFromPathAsync(
        string path, ImageDimensions source, int maxWidth, int maxHeight, bool progressiveColorFirstPreview,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsPathPreview(path))
            return Task.FromResult<PathPreviewDecodeResult?>(null);

        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(TryDecodePreviewFromPathCore(path, source, maxWidth, maxHeight, progressiveColorFirstPreview));
        }

        return Task.Run<PathPreviewDecodeResult?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryDecodePreviewFromPathCore(path, source, maxWidth, maxHeight, progressiveColorFirstPreview);
        }, cancellationToken);
    }

    private static PathPreviewDecodeResult? TryDecodePreviewFromPathCore(
        string path, ImageDimensions source, int maxWidth, int maxHeight, bool progressiveColorFirstPreview)
    {
        // Vector SVG renders at the requested preview resolution.
        if (SvgDecoder.IsSvgPath(path))
        {
            var longest = Math.Max(maxWidth, maxHeight);
            if (SvgDecoder.TryDecode(path, longest, out var svgBitmap) && svgBitmap is not null)
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("svg_preview_ready", $"output={svgBitmap.PixelSize.Width}x{svgBitmap.PixelSize.Height}");
                return new PathPreviewDecodeResult(svgBitmap, true, "svg-vector");
            }
            return null;
        }
        // Bundled managed raster decoders (bounded decode) for the long tail of simple formats.
        if (BuiltInRasterDecoder.CanDecode(path))
        {
            var longest = Math.Max(maxWidth, maxHeight);
            if (BuiltInRasterDecoder.TryDecodeBounded(path, longest, out var rasterBitmap) && rasterBitmap is not null)
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("builtin_raster_preview_ready", $"output={rasterBitmap.PixelSize.Width}x{rasterBitmap.PixelSize.Height}");
                return new PathPreviewDecodeResult(rasterBitmap, true, "builtin-raster");
            }
            return null;
        }
        // JPEG/JPEG-XR have a deterministic decoder-native reduced-resolution route. Put it
        // ahead of Shell-cache probing so a cold cache miss cannot add COM/Shell latency to the
        // exact class that exposed the regression. This mirrors legacy's fastest JPEG path while
        // keeping Shell cache as a fallback if the native transform is unavailable on that PC.
        if (NativeImageDecoder.SupportsNativeScaledPreview(path))
        {
            var nativeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (NativeImageDecoder.TryDecodeBounded(path, maxWidth, maxHeight, progressiveColorFirstPreview, out var nativeBitmap))
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("wic_native_preview_ready",
                        $"output={nativeBitmap.PixelSize.Width}x{nativeBitmap.PixelSize.Height};progressive={(progressiveColorFirstPreview ? "colour-first" : "level0-fast")};elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds:F3}ms");
                var sourcePixels = (long)Math.Max(1, source.Width) * Math.Max(1, source.Height);
                var outputPixels = (long)nativeBitmap.PixelSize.Width * nativeBitmap.PixelSize.Height;
                return new PathPreviewDecodeResult(nativeBitmap, outputPixels < sourcePixels, "wic-native-scale");
            }
            if (GlidePerformanceTrace.Enabled)
                GlidePerformanceTrace.Mark("wic_native_preview_miss",
                    $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds:F3}ms");
        }

        // Never use Explorer/Shell cached thumbnails for formats that may contain alpha. Windows
        // is allowed to pre-composite those thumbnail pixels against an opaque background; using
        // that cache would make a genuinely transparent PNG/WebP/etc appear grey in Overlay mode.
        // Return to the normal codec path instead, which preserves source alpha end-to-end.
        if (ImageFormatRegistry.MayContainTransparency(path))
        {
            if (GlidePerformanceTrace.Enabled)
                GlidePerformanceTrace.Mark("shell_cached_preview_skipped_alpha",
                    $"extension={ImageFormatRegistry.GetLongestExtension(path)}");
            return null;
        }

        // Opaque-only formats may opportunistically consume an already-existing Windows Shell
        // thumbnail. INCACHEONLY forbids extraction, so this never turns into hidden foreground
        // decode work. Tiny/icon-like cache entries are rejected and the normal codec path wins.
        var shellStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (NativeImageDecoder.TryDecodeShellCachedBounded(path, maxWidth, maxHeight, out var cached) &&
            IsUsefulCachedPreview(cached, source, maxWidth, maxHeight))
        {
            var cachedPixels = (long)cached.PixelSize.Width * cached.PixelSize.Height;
            var sourcePixels = (long)Math.Max(1, source.Width) * Math.Max(1, source.Height);
            if (GlidePerformanceTrace.Enabled)
                GlidePerformanceTrace.Mark("shell_cached_preview_hit",
                    $"output={cached.PixelSize.Width}x{cached.PixelSize.Height};elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(shellStarted).TotalMilliseconds:F3}ms");
            return new PathPreviewDecodeResult(cached, cachedPixels < sourcePixels, "shell-cache");
        }
        cached?.Dispose();
        if (GlidePerformanceTrace.Enabled)
            GlidePerformanceTrace.Mark("shell_cached_preview_miss",
                $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(shellStarted).TotalMilliseconds:F3}ms");
        return null;
    }

    private static bool IsUsefulCachedPreview(Bitmap bitmap, ImageDimensions source, int maxWidth, int maxHeight)
    {
        if (!source.IsValid || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) return false;
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        var wantedWidth = Math.Max(1, (int)Math.Ceiling(source.Width * scale));
        var wantedHeight = Math.Max(1, (int)Math.Ceiling(source.Height * scale));

        // A cached image must be genuinely presentation-worthy, not merely a tiny Explorer thumb.
        // 72% gives the compositor enough pixels for a crisp immediate fit while allowing common
        // 1024px Windows cache entries to win first paint; background refinement then replaces it.
        return bitmap.PixelSize.Width >= Math.Min(wantedWidth, Math.Max(256, (int)Math.Ceiling(wantedWidth * .72))) &&
               bitmap.PixelSize.Height >= Math.Min(wantedHeight, Math.Max(256, (int)Math.Ceiling(wantedHeight * .72)));
    }
}

/// <summary>
/// Windows native raster bridge. WIC is allowed to attempt any declared suffix because installed
/// Windows codecs can extend it without Glide loading an optional provider. The Shell path is a
/// final lazy fallback and explicitly requests thumbnails only, rejecting generic icons.
/// </summary>
public static partial class NativeImageDecoder
{
    public static bool IsTiffPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsJpegPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsJpegXrPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".jxr", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wdp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".hdp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPngPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".apng", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBmpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dib", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWebpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGifPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsIcoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        return extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cur", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsDirectNativeDecode(string? path)
        => (IsJpegPath(path) || IsTiffPath(path) || IsJpegXrPath(path) || IsPngPath(path) || IsBmpPath(path) ||
            IsWebpPath(path) || IsGifPath(path) || IsIcoPath(path)) && !IsNativeCodecKnownUnavailable(path);

    // Extensions for which this PC's WIC has no working codec (e.g. WebP without the Store "WebP Image
    // Extensions"): the native attempt failed and the managed Skia decoder then succeeded. Skipping the
    // doomed native attempt saves ~4 ms (decoder enumeration) on every later decode of that format.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> NativeUnavailable = new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> NativeMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The native codec failed on <paramref name="path"/> and the managed decoder succeeded. After three such
    /// files of one extension (so one odd file cannot switch a format off) the native attempt is skipped.
    /// </summary>
    internal static void MarkNativeCodecUnavailable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        if (string.IsNullOrEmpty(extension)) return;
        if (NativeMisses.AddOrUpdate(extension, 1, (_, n) => n + 1) >= 3) NativeUnavailable.TryAdd(extension, 0);
    }

    internal static bool IsNativeCodecKnownUnavailable(string? path)
        => !NativeUnavailable.IsEmpty && !string.IsNullOrWhiteSpace(path) &&
           NativeUnavailable.ContainsKey(ImageFormatRegistry.GetLongestExtension(path));

    public static bool SupportsNativeScaledPreview(string? path) => IsJpegPath(path) || IsJpegXrPath(path);

    /// <summary>
    /// Core formats whose Windows/Skia codecs can only decode at source resolution: any reduced
    /// "preview" is a full decode followed by a resample.
    /// </summary>
    public static bool IsFullDecodeOnlyFormat(string? path)
        => IsPngPath(path) || IsWebpPath(path) || IsTiffPath(path) || IsBmpPath(path) || IsGifPath(path) || IsIcoPath(path);

    public static bool TryDecode(string path, out Bitmap bitmap) => TryDecode(path, 0, out bitmap);
    public static bool TryDecode(string path, int maxLongestSide, out Bitmap bitmap)
        => TryDecodeCore(path, maxLongestSide, shell: false, out bitmap);
    public static bool TryDecodeShellPreview(string path, int maxLongestSide, out Bitmap bitmap)
        => TryDecodeCore(path, maxLongestSide, shell: true, out bitmap);
    public static bool TryDecodeShellCachedBounded(string path, int maxWidth, int maxHeight, out Bitmap bitmap)
        => TryDecodeShellCachedBoundedCore(path, maxWidth, maxHeight, out bitmap);
    public static bool TryDecodeBounded(string path, int maxWidth, int maxHeight, out Bitmap bitmap)
        => TryDecodeBoundedCore(path, maxWidth, maxHeight, true, out bitmap);
    public static bool TryDecodeBounded(string path, int maxWidth, int maxHeight, bool progressiveColorFirstPreview, out Bitmap bitmap)
        => TryDecodeBoundedCore(path, maxWidth, maxHeight, progressiveColorFirstPreview, out bitmap);

    private static unsafe bool TryDecodeShellCachedBoundedCore(string path, int maxWidth, int maxHeight, out Bitmap bitmap)
    {
        bitmap = null!;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            maxWidth = Math.Clamp(maxWidth, 64, 8192);
            maxHeight = Math.Clamp(maxHeight, 64, 8192);
            var status = GlideDecodeShellCachedPreviewW(path, checked((uint)maxWidth), checked((uint)maxHeight), out var decoded);
            return TryCreateBitmap(decoded, status, out bitmap);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (OverflowException) { return false; }
        catch { return false; }
    }

    private static unsafe bool TryDecodeBoundedCore(string path, int maxWidth, int maxHeight, bool progressiveColorFirstPreview, out Bitmap bitmap)
    {
        bitmap = null!;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            maxWidth = Math.Clamp(maxWidth, 1, 32768);
            maxHeight = Math.Clamp(maxHeight, 1, 32768);
            var status = GlideDecodeImageBoundedExW(path, checked((uint)maxWidth), checked((uint)maxHeight),
                progressiveColorFirstPreview ? 1 : 0, out var decoded);
            return TryCreateBitmap(decoded, status, out bitmap);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (OverflowException) { return false; }
        catch { return false; }
    }

    // 1 = the native bridge exports GlideDecodeImageIntoW, 0 = unknown, -1 = older bridge (fall back).
    private static int _decodeIntoAvailable;

    private sealed class SurfaceTarget
    {
        public WriteableBitmap? Bitmap;
        public ILockedFramebuffer? Locked;
        public bool AllocationFailed;
    }

    // Set when the last full-resolution decode on this thread failed because its frame could not be
    // allocated, so the caller can fall back to a preview instead of retrying another full decoder.
    [ThreadStatic] private static bool _allocationFailed;

    /// <summary>Returns and clears this thread's "full frame could not be allocated" flag.</summary>
    internal static bool ResetAllocationFailure()
    {
        var failed = _allocationFailed;
        _allocationFailed = false;
        return failed;
    }


    /// <summary>Native allocator callback: creates the presentation bitmap and hands WIC its locked pixels.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe IntPtr AllocateSurface(IntPtr context, uint width, uint height, uint* stride)
    {
        try
        {
            if (width == 0 || height == 0 || width > 65536 || height > 65536) return IntPtr.Zero;
            if (GCHandle.FromIntPtr(context).Target is not SurfaceTarget target) return IntPtr.Zero;
            // Backstop for the decoded-pixel cap: the native bridge checks it too (5.0+), but an older
            // bridge only limits each side, so refuse an over-cap surface here as well.
            if ((ulong)width * height > (ulong)DecodeMemoryGuard.MaxTotalPixels) return IntPtr.Zero;
            WriteableBitmap bitmap;
            try
            {
                bitmap = new WriteableBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            catch (OutOfMemoryException)
            {
                target.AllocationFailed = true;
                return IntPtr.Zero;
            }
            target.Bitmap = bitmap;
            var locked = bitmap.Lock();
            target.Locked = locked;
            *stride = (uint)locked.RowBytes;
            return locked.Address;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Full-resolution decode straight into the presentation bitmap (no intermediate native buffer and
    /// copy). Since 5.0.7 the bridge also applies EXIF orientation into the bitmap and decodes TIFF/BMP of
    /// 4 MP and more in parallel bands (benchmark: docs/perf/benchmark-report-5.0.7.md). Returns false when
    /// the bridge is older (status 2 = "needs EXIF rotation" from pre-5.0.7 bridges) or decoding failed, so
    /// the caller falls back to the buffered path with identical pixels.
    /// </summary>
    private static unsafe bool TryDecodeFullInto(string path, out Bitmap bitmap, out int status)
    {
        bitmap = null!;
        status = -1;
        if (Volatile.Read(ref _decodeIntoAvailable) < 0) return false;
        var target = new SurfaceTarget();
        var handle = GCHandle.Alloc(target);
        status = 0;
        try
        {
            delegate* unmanaged[Cdecl]<IntPtr, uint, uint, uint*, IntPtr> allocate = &AllocateSurface;
            status = GlideDecodeImageIntoW(path, (IntPtr)allocate, GCHandle.ToIntPtr(handle));
            Volatile.Write(ref _decodeIntoAvailable, 1);
        }
        catch (EntryPointNotFoundException) { Volatile.Write(ref _decodeIntoAvailable, -1); status = -1; }
        catch (DllNotFoundException) { Volatile.Write(ref _decodeIntoAvailable, -1); status = -1; }
        catch { status = 0; }
        finally
        {
            handle.Free();
            target.Locked?.Dispose();
        }
        if (status == 1 && target.Bitmap is not null)
        {
            bitmap = target.Bitmap;
            return true;
        }
        if (target.AllocationFailed) _allocationFailed = true;
        target.Bitmap?.Dispose();
        return false;
    }

    /// <summary>True when the loaded Glide.Native bridge supports direct full decodes (diagnostics/tests).</summary>
    internal static bool DirectFullDecodeAvailable => Volatile.Read(ref _decodeIntoAvailable) > 0;

    /// <summary>Tests: the buffered (decode + copy) full-resolution path, for pixel-identity checks.</summary>
    internal static bool TryDecodeFullBufferedForTests(string path, out Bitmap bitmap) => TryDecodeCore(path, 0, shell: false, out bitmap, allowDirect: false);

    /// <summary>Tests: the direct (decode into the bitmap) full-resolution path; false on an older bridge.</summary>
    internal static bool TryDecodeFullDirectForTests(string path, out Bitmap bitmap) => TryDecodeFullInto(path, out bitmap, out _);

    private static unsafe bool TryDecodeCore(string path, int maxLongestSide, bool shell, out Bitmap bitmap, bool allowDirect = true)
    {
        bitmap = null!;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        // Full-resolution decodes (refinement, quality recovery, full-first frames, full neighbours) go
        // straight into the bitmap: half the peak memory and no full-frame memcpy.
        if (allowDirect && !shell && maxLongestSide <= 0)
        {
            if (TryDecodeFullInto(path, out bitmap, out var directStatus)) return true;
            // The codec itself failed (not an EXIF rotation, not an older bridge): the buffered path
            // runs the same WIC decoder and would fail the same way.
            if (directStatus == 0 && Volatile.Read(ref _decodeIntoAvailable) > 0) return false;
        }
        try
        {
            maxLongestSide = Math.Clamp(maxLongestSide, 0, 32768);
            NativeDecodedImage decoded;
            var status = shell
                ? GlideDecodeShellPreviewW(path, checked((uint)Math.Max(64, maxLongestSide)), out decoded)
                : GlideDecodeImageW(path, checked((uint)maxLongestSide), out decoded);
            return TryCreateBitmap(decoded, status, out bitmap);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        catch (OverflowException) { return false; }
        catch { return false; }
    }

    private static unsafe bool TryCreateBitmap(NativeDecodedImage decoded, int status, out Bitmap bitmap)
    {
        bitmap = null!;
        if (decoded.Data == IntPtr.Zero) return false;
        if (status != 1) { GlideFreeImageBuffer(decoded.Data); return false; }
        try
        {
            if (decoded.Width == 0 || decoded.Height == 0 || decoded.Width > 65536 || decoded.Height > 65536 ||
                decoded.Width > int.MaxValue || decoded.Height > int.MaxValue || decoded.Stride < checked(decoded.Width * 4u) ||
                (ulong)decoded.Width * decoded.Height > (ulong)DecodeMemoryGuard.MaxTotalPixels) return false;
            var requiredBytes = checked((ulong)decoded.Stride * decoded.Height);
            if (decoded.BufferBytes < requiredBytes || decoded.BufferBytes > 2UL * 1024 * 1024 * 1024) return false;

            WriteableBitmap? candidate;
            try
            {
                candidate = new(new PixelSize(checked((int)decoded.Width), checked((int)decoded.Height)),
                    new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            catch (OutOfMemoryException)
            {
                _allocationFailed = true;
                return false;
            }
            try
            {
                using var frame = candidate.Lock();
                var rowBytes = checked((int)decoded.Width * 4);
                if (frame.RowBytes < rowBytes) return false;
                if (frame.RowBytes == decoded.Stride)
                {
                    var bytes = checked((long)decoded.Stride * decoded.Height);
                    Buffer.MemoryCopy((void*)decoded.Data, (void*)frame.Address, bytes, bytes);
                }
                else
                {
                    for (var y = 0; y < decoded.Height; y++)
                    {
                        var src = (byte*)decoded.Data + (nuint)(y * decoded.Stride);
                        var dst = (byte*)frame.Address + (nuint)(y * (uint)frame.RowBytes);
                        Buffer.MemoryCopy(src, dst, (long)frame.RowBytes, rowBytes);
                    }
                }
                bitmap = candidate;
                candidate = null;
                return true;
            }
            finally { candidate?.Dispose(); }
        }
        finally { GlideFreeImageBuffer(decoded.Data); }
    }

    internal static unsafe bool TryCreateBitmapFromNative(NativeDecodedImage decoded, int status, out Bitmap bitmap)
        => TryCreateBitmap(decoded, status, out bitmap);

    internal static int RawGlideDecodeImage(string path, uint maxLongestSide, out NativeDecodedImage image)
        => GlideDecodeImageW(path, maxLongestSide, out image);

    /// <summary>Bounded, orientation-aware, decoder-scaled decode (colour-first progressive level).</summary>
    internal static int RawGlideDecodeImageBounded(string path, uint maxWidth, uint maxHeight, out NativeDecodedImage image)
        => GlideDecodeImageBoundedExW(path, maxWidth, maxHeight, 1, out image);

    internal static void RawGlideFreeBuffer(IntPtr data)
        => GlideFreeImageBuffer(data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDecodedImage
    {
        public uint Width;
        public uint Height;
        public uint Stride;
        public ulong BufferBytes;
        public IntPtr Data;
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeImageW(string path, uint maxLongestSide, out NativeDecodedImage image);

    /// <summary>Tests: true when the 5.0.7 libjpeg-turbo path accepts this JPEG (false = WIC decodes it).</summary>
    internal static bool JpegTurboAcceptsForTests(string path) => GlideJpegTurboAcceptsW(path) == 1;

    /// <summary>Tests: the native JPEG first-view decision (0 WIC, 1 embedded preview, 2 DCT-scaled) and scale denominator.</summary>
    internal static (int Choice, int ScaleDenom) JpegPlanFirstViewForTests(int width, int height, uint needW, uint needH, uint previewLongSide, bool scaledAllowed)
    {
        var choice = GlideJpegPlanFirstView(width, height, needW, needH, previewLongSide, scaledAllowed ? 1 : 0, out var denom);
        return (choice, denom);
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideJpegPlanFirstView")]
    private static partial int GlideJpegPlanFirstView(int width, int height, uint needW, uint needH, uint previewLongSide, int scaledAllowed, out int scaleDenom);

    [LibraryImport("Glide.Native", EntryPoint = "GlideJpegTurboAcceptsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideJpegTurboAcceptsW(string path);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeImageIntoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeImageIntoW(string path, IntPtr allocate, IntPtr context);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeImageBoundedExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeImageBoundedExW(string path, uint maxWidth, uint maxHeight, int progressiveColorFirstPreview, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeShellPreviewW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeShellPreviewW(string path, uint maxLongestSide, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeShellCachedPreviewW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeShellCachedPreviewW(string path, uint maxWidth, uint maxHeight, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideFreeImageBuffer")]
    private static partial void GlideFreeImageBuffer(IntPtr data);
}
