namespace Glide.Imaging;

/// <summary>
/// Size check for the managed Skia decoder (<c>new Bitmap(stream)</c>, <c>Bitmap.DecodeToWidth</c>), which
/// allocates its output before decoding. When no probe could read an image's size the loader could not
/// apply the pixel cap, so the size is read from Skia's own codec header first (header only, no pixels).
/// </summary>
internal static class SkiaDecodeGuard
{
    private sealed class RefusedException(string message) : IOException(message);

    /// <summary>True for the refusals thrown by <see cref="EnsureAllowed"/> (callers must not retry another full decoder).</summary>
    public static bool IsRefusal(Exception exception) => exception is RefusedException or InsufficientMemoryException;

    /// <summary>
    /// Throws when the frame Skia would allocate (full size, or the codec's scaled size for
    /// <paramref name="longestSide"/> &gt; 0) is beyond the pixel cap or does not fit the free memory.
    /// Leaves the stream position unchanged. Unknown formats pass (Skia then fails on its own).
    /// </summary>
    public static void EnsureAllowed(Stream stream, int longestSide)
    {
        if (!TryReadOutputSize(stream, longestSide, out var size)) return;
        if (DecodeMemoryGuard.ExceedsPixelCap(size))
            throw new RefusedException($"Image dimensions ({size.Width}x{size.Height}) exceed the safe decode limit of {DecodeMemoryGuard.MaxTotalPixels:N0} pixels.");
        if (!DecodeMemoryGuard.FitsAvailableMemory(size))
            throw new InsufficientMemoryException($"There is not enough free memory to decode this {size.Width}x{size.Height} image.");
    }

    internal static bool TryReadOutputSize(Stream stream, int longestSide, out ImageDimensions size)
    {
        size = default;
        if (!stream.CanSeek || !stream.CanRead) return false;
        var start = stream.Position;
        try
        {
            // disposeManagedStream: false - the codec owns the wrapper, never the caller's stream.
            var wrapper = new SkiaSharp.SKManagedStream(stream, false);
            using var codec = SkiaSharp.SKCodec.Create(wrapper);
            if (codec is null) return false;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return false;
            size = new ImageDimensions(info.Width, info.Height);
            var longest = Math.Max(info.Width, info.Height);
            if (longestSide > 0 && longestSide < longest)
            {
                var scaled = codec.GetScaledDimensions((float)longestSide / longest);
                if (scaled.Width > 0 && scaled.Height > 0) size = new ImageDimensions(scaled.Width, scaled.Height);
            }
            GC.KeepAlive(wrapper);
            return true;
        }
        catch { return false; }
        finally
        {
            try { stream.Position = start; } catch { /* non-seekable stream; the position reset is best effort */ }
        }
    }
}
