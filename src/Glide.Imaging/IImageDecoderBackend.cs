using Avalonia;
using Avalonia.Media;
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
        => Task.Run(() => DecodeFull(stream, sourcePath), cancellationToken);
    Task<Bitmap> DecodePreviewAsync(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode, string? sourcePath, CancellationToken cancellationToken = default)
        => Task.Run(() => DecodePreview(stream, source, longestSide, interpolationMode, sourcePath), cancellationToken);
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
        try
        {
            return new Bitmap(stream);
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(sourcePath))
        {
            // Preserve the known-good Avalonia/Skia path first. Only if it cannot decode do we try
            // Windows' installed WIC codecs, then a genuine Shell thumbnail (never a generic icon).
            if (NativeImageDecoder.TryDecode(sourcePath!, 0, out var native)) return native;
            if (ShouldTryShellFallback(sourcePath!) && NativeImageDecoder.TryDecodeShellPreview(sourcePath!, 4096, out var shell)) return shell;
            throw;
        }
    }

    public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode)
    {
        longestSide = Math.Max(1, longestSide);
        try
        {
            if (source.Width >= source.Height)
                return Bitmap.DecodeToWidth(stream, Math.Min(longestSide, source.Width), interpolationMode);
            return Bitmap.DecodeToHeight(stream, Math.Min(longestSide, source.Height), interpolationMode);
        }
        catch
        {
            if (stream.CanSeek) stream.Position = 0;
            return new Bitmap(stream);
        }
    }

    public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode, string? sourcePath)
    {
        // Large JPEG/TIFF previews use the native WIC bridge first. This avoids a full-size Skia
        // decode followed by managed downscaling on the critical browse path and lets WIC request
        // a decoder-scaled first frame where the installed codec supports it.
        if ((NativeImageDecoder.IsTiffPath(sourcePath) || NativeImageDecoder.IsJpegPath(sourcePath)) &&
            NativeImageDecoder.TryDecode(sourcePath!, longestSide, out var nativePreview))
            return nativePreview;

        try
        {
            return DecodePreview(stream, source, longestSide, interpolationMode);
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(sourcePath))
        {
            if (NativeImageDecoder.TryDecode(sourcePath!, longestSide, out var native)) return native;
            if (ShouldTryShellFallback(sourcePath!) && NativeImageDecoder.TryDecodeShellPreview(sourcePath!, longestSide, out var shell)) return shell;
            throw;
        }
    }

    public bool SupportsPathPreview(string path) =>
        OperatingSystem.IsWindows() && ImageFormatRegistry.IsSupported(path);

    public Task<PathPreviewDecodeResult?> TryDecodePreviewFromPathAsync(
        string path, ImageDimensions source, int maxWidth, int maxHeight, bool progressiveColorFirstPreview,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsPathPreview(path))
            return Task.FromResult<PathPreviewDecodeResult?>(null);

        return Task.Run<PathPreviewDecodeResult?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            // All supported formats may opportunistically consume an already-existing Windows Shell
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
        }, cancellationToken);
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

    private static bool ShouldTryShellFallback(string path) =>
        ImageFormatRegistry.IsSupported(path) && !ImageFormatRegistry.IsCoreFastPath(path);
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

    public static bool SupportsNativeScaledPreview(string? path) => IsJpegPath(path) || IsJpegXrPath(path);

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

    private static unsafe bool TryDecodeCore(string path, int maxLongestSide, bool shell, out Bitmap bitmap)
    {
        bitmap = null!;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
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
                decoded.Width > int.MaxValue || decoded.Height > int.MaxValue || decoded.Stride < checked(decoded.Width * 4u)) return false;
            var requiredBytes = checked((ulong)decoded.Stride * decoded.Height);
            if (decoded.BufferBytes < requiredBytes || decoded.BufferBytes > 2UL * 1024 * 1024 * 1024) return false;

            WriteableBitmap? candidate = new(new PixelSize(checked((int)decoded.Width), checked((int)decoded.Height)),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDecodedImage
    {
        public uint Width;
        public uint Height;
        public uint Stride;
        public ulong BufferBytes;
        public IntPtr Data;
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeImageW(string path, uint maxLongestSide, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeImageBoundedExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeImageBoundedExW(string path, uint maxWidth, uint maxHeight, int progressiveColorFirstPreview, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeShellPreviewW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeShellPreviewW(string path, uint maxLongestSide, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideDecodeShellCachedPreviewW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideDecodeShellCachedPreviewW(string path, uint maxWidth, uint maxHeight, out NativeDecodedImage image);

    [LibraryImport("Glide.Native", EntryPoint = "GlideFreeImageBuffer")]
    private static partial void GlideFreeImageBuffer(IntPtr data);
}

public readonly record struct ImageDimensions(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
    public int LongestSide => Math.Max(Width, Height);
}
