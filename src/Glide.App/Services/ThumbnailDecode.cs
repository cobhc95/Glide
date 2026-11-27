using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// The small scaled decode shared by the Explorer tab's thumbnails and the tab hover previews. Order: the persistent
/// thumbnail disk cache (<see cref="ThumbnailDiskCache"/>, when switched on) -> libjpeg-turbo for JPEG -> the native
/// thumbnail bridge for WebP, TIFF, BMP and the other WIC formats (<see cref="NativeImageDecoder.TryDecodeThumbnail"/>) ->
/// Avalonia's own decoder reading the file sequentially (PNG, GIF, ICO, ...). Callers decide which formats qualify and run
/// it off the UI thread.
/// </summary>
internal static class ThumbnailDecode
{
    // Flows into Task.Run, so a test's hook never sees decodes from tests running in parallel.
    private static readonly AsyncLocal<Action<string>?> s_decodingForTests = new();

    /// <summary>Test seam: called with the path just before the pixels are decoded (never on a disk-cache hit).</summary>
    internal static Action<string>? DecodingForTests
    {
        get => s_decodingForTests.Value;
        set => s_decodingForTests.Value = value;
    }

    /// <summary>
    /// The picture scaled to <paramref name="width"/>, or null when the frame the decoder would allocate is beyond the pixel cap
    /// or does not fit the free memory (the main loader's <see cref="SkiaDecodeGuard"/> check: PNG, BMP and GIF decode at
    /// full size before scaling, so a small file can declare a multi-gigabyte frame). Callers then show no picture.
    /// A thumbnail found in the disk cache is returned without decoding the picture.
    /// </summary>
    public static Bitmap? DecodeToWidth(string path, int width)
    {
        var cache = ThumbnailDiskCache.Current;
        var stamp = cache is null ? default : cache.StampFor(path, width);
        if (cache is not null && stamp.IsValid && TryLoadFromCache(cache, stamp) is { } cached) return cached;
        var bitmap = DecodeUncached(path, width);
        if (bitmap is not null && cache is not null && stamp.IsValid) StoreInCache(cache, stamp, bitmap);
        return bitmap;
    }

    /// <summary>
    /// 5.2.0, the I/O stage's first look: the thumbnail from the disk cache when it holds one (no decode, the picture is not read;
    /// only its size and write time are). Always returns the stamp taken now, so a later <see cref="DecodePrepared"/> stores under
    /// the version of the file the bytes came from.
    /// </summary>
    internal static Bitmap? TryDiskCache(string path, int width, out ThumbnailDiskCache.Stamp stamp)
    {
        stamp = default;
        var cache = ThumbnailDiskCache.Current;
        if (cache is null) return null;
        stamp = cache.StampFor(path, width);
        return stamp.IsValid ? TryLoadFromCache(cache, stamp) : null;
    }

    /// <summary>
    /// 5.2.0, the decode lane's half of the pipeline: the same result as <see cref="DecodeToWidth"/> for a file the I/O stage already
    /// handled (disk cache missed). When the I/O stage read the file (JPEG, PNG and the other Skia formats) the bytes are decoded from
    /// memory, so the lane never opens the file; anything else decodes from the path. The result is stored in the disk cache under
    /// <paramref name="stamp"/> (taken before the read).
    /// </summary>
    internal static Bitmap? DecodePrepared(string path, int width, byte[]? bytes, int length, ThumbnailDiskCache.Stamp stamp, out double storeMs)
    {
        storeMs = 0;
        var bitmap = DecodeUncached(path, width, bytes, length);
        var cache = ThumbnailDiskCache.Current;
        if (bitmap is not null && cache is not null && stamp.IsValid)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            StoreInCache(cache, stamp, bitmap);
            storeMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return bitmap;
    }

    /// <summary>
    /// The decode without the disk cache (the cache's miss path, and the benchmark's decode cost). The decoder comes from
    /// <see cref="ThumbnailRoutes"/>: every format the viewer opens has one (5.2.0), the cheapest reduced-size path of that
    /// format; null leaves the tile on its file-type glyph.
    /// </summary>
    internal static Bitmap? DecodeUncached(string path, int width) => DecodeUncached(path, width, null, 0);

    /// <summary>
    /// <see cref="DecodeUncached(string, int)"/> for a file whose first <paramref name="length"/> bytes are already in
    /// <paramref name="bytes"/> (the I/O stage read them): JPEG and the Skia formats decode from memory, the rest ignore the bytes.
    /// </summary>
    internal static Bitmap? DecodeUncached(string path, int width, byte[]? bytes, int length)
    {
        var route = ThumbnailRoutes.RouteFor(path);
        switch (route)
        {
            case ThumbnailRoute.None:
                return null;
            case ThumbnailRoute.Svg:
                DecodingForTests?.Invoke(path);
                return ThumbnailExtendedDecode.DecodeSvg(path, width);
            case ThumbnailRoute.BuiltIn:
            {
                DecodingForTests?.Invoke(path);
                if (ThumbnailExtendedDecode.DecodeBuiltIn(path, width) is { } managed) return managed;
                // Block-compressed DDS is not a built-in raster: the viewer reaches it through WIC, so does the tile.
                return path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? ThumbnailExtendedDecode.DecodeWic(path, width) : null;
            }
            case ThumbnailRoute.Provider:
                DecodingForTests?.Invoke(path);
                return ThumbnailExtendedDecode.DecodeProvider(path, width);
            case ThumbnailRoute.Wic:
                DecodingForTests?.Invoke(path);
                return ThumbnailExtendedDecode.DecodeWic(path, width);
        }

        // JPEG family (camera folders; .jfif / .jif / .mpo too): libjpeg-turbo DCT-scaled decode with the fast IDCT, about
        // 2-3x faster than Skia's scaled decode; anything it declines falls through to Skia below.
        if (route == ThumbnailRoute.Jpeg)
        {
            DecodingForTests?.Invoke(path);
            if (bytes is not null && length > 0 && ThumbnailRoutes.IsNativeJpegSuffix(path))
            {
                if (NativeImageDecoder.TryDecodeJpegThumbnailFromMemory(bytes.AsSpan(0, length), width, out var fromMemory) && fromMemory is not null) return fromMemory;
            }
            else if (NativeImageDecoder.TryDecodeJpegThumbnail(path, width, out var fast) && fast is not null) return fast;
        }
        // WebP (libwebp decodes straight to the tile size), TIFF (Skia cannot read it) and BMP (banded parallel decode) and the
        // other WIC formats; declined files (animated WebP, a missing codec, an older bridge) fall through to Skia.
        else if (route == ThumbnailRoute.NativeBridge)
        {
            DecodingForTests?.Invoke(path);
            if (NativeImageDecoder.TryDecodeThumbnail(path, width, out var native) && native is not null) return native;
        }
        using Stream stream = bytes is not null && length > 0 && route != ThumbnailRoute.NativeBridge
            ? new MemoryStream(bytes, 0, length, writable: false)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            SkiaDecodeGuard.EnsurePlausibleFormat(stream, path); // no 33x4 "WBMP" thumbnails of unrelated files
            SkiaDecodeGuard.EnsureAllowed(stream, width);
        }
        catch (Exception ex) when (SkiaDecodeGuard.IsRefusal(ex) || ex is InvalidDataException) { return null; }
        if (route is not ThumbnailRoute.Jpeg and not ThumbnailRoute.NativeBridge) DecodingForTests?.Invoke(path);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.LowQuality);
    }

    private static unsafe Bitmap? TryLoadFromCache(ThumbnailDiskCache cache, ThumbnailDiskCache.Stamp stamp)
    {
        try
        {
            using var hit = cache.TryGet(stamp);
            if (hit is null) return null;
            fixed (byte* pixels = hit.Pixels)
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (IntPtr)pixels, new PixelSize(hit.Width, hit.Height), new Vector(96, 96), hit.Width * 4);
        }
        catch { return null; } // a damaged entry or no memory for the bitmap: decode the picture instead
    }

    private static void StoreInCache(ThumbnailDiskCache cache, ThumbnailDiskCache.Stamp stamp, Bitmap bitmap)
    {
        try
        {
            // Only BGRA is stored, premultiplied: a straight-alpha frame (the managed decoders' output) is premultiplied in
            // the copy; any other pixel format is not cached.
            if (bitmap.Format != PixelFormat.Bgra8888 || bitmap.AlphaFormat is not (AlphaFormat.Premul or AlphaFormat.Unpremul)) return;
            var premultiply = bitmap.AlphaFormat == AlphaFormat.Unpremul;
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0 || size.Width > 2048 || size.Height > 2048) return;
            var stride = size.Width * 4;
            var length = stride * size.Height;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), length, stride); }
                finally { handle.Free(); }
                if (premultiply) PremultiplyInPlace(buffer, length);
                cache.Put(stamp, size.Width, size.Height, buffer.AsSpan(0, length), stride); // copies, then writes on the thread pool
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch { /* a thumbnail that cannot be stored is simply decoded again next time */ }
    }

    /// <summary>Straight BGRA to premultiplied BGRA (the form the cache stores and <see cref="TryLoadFromCache"/> returns).</summary>
    internal static void PremultiplyInPlace(byte[] pixels, int length)
    {
        for (var i = 0; i + 3 < length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255) continue;
            pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
        }
    }
}
