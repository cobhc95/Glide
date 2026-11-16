using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Glide.Imaging;

// Avalonia surface of BuiltInRasterDecoder: wraps the dependency-free pixel pipeline (BuiltInRasterDecoder.cs)
// in a WriteableBitmap. Kept in its own file so the decoders can also be compiled into the Avalonia-free
// Explorer thumbnail codec library (src/Glide.ThumbnailCodecs) without this file.
public static partial class BuiltInRasterDecoder
{
    /// <summary>Decodes the first frame/page at full resolution.</summary>
    public static bool TryDecode(string path, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!TryDecodeFullPixels(path, out var image)) return false;
        return TryCreateBitmap(image, out bitmap);
    }

    /// <summary>
    /// Decodes and, when the source is larger than <paramref name="maxLongestSide"/>, box-downsamples
    /// to fit. The side is clamped to [1, 32768].
    /// </summary>
    public static bool TryDecodeBounded(string path, int maxLongestSide, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!TryDecodeBoundedPixels(path, maxLongestSide, out var image)) return false;
        return TryCreateBitmap(image, out bitmap);
    }

    private static unsafe bool TryCreateBitmap(DecodedImage image, out Bitmap? bitmap)
    {
        bitmap = null;
        if (image.Bgra is null || image.Width <= 0 || image.Height <= 0) return false;
        int rowBytes;
        try { rowBytes = checked(image.Width * 4); }
        catch { return false; }
        if (image.Bgra.Length < (long)rowBytes * image.Height) return false;

        WriteableBitmap? candidate = null;
        try
        {
            candidate = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var frame = candidate.Lock())
            {
                if (frame.RowBytes < rowBytes) return false;
                fixed (byte* source = image.Bgra)
                {
                    for (var y = 0; y < image.Height; y++)
                    {
                        var src = source + (long)y * rowBytes;
                        var dst = (byte*)frame.Address + (long)y * frame.RowBytes;
                        Buffer.MemoryCopy(src, dst, frame.RowBytes, rowBytes);
                    }
                }
            }
            bitmap = candidate;
            candidate = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }
}
