using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// "Animate GIF previews on hover" (5.0.6): the frames of an animated GIF tab's hover preview, decoded off the UI thread
/// with the viewer's own GIF decoder and scaled down to the preview size as they are composited (only the small frames
/// are kept). Within limits so hovering never costs much: files over <see cref="MaxFileBytes"/>, canvases the main
/// decoder's pixel cap or free memory refuses, and animations whose small frames together exceed
/// <see cref="MaxFramesBytes"/> are not animated (the preview stays the still first frame).
/// </summary>
internal static class TabGifPreview
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxFramesBytes = 32L * 1024 * 1024;
    /// <summary>Budget for the decoder's full-size composition canvas (it refuses canvases over half of it).</summary>
    private const long CanvasBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Test seam: overrides <see cref="MaxFramesBytes"/>.</summary>
    internal static long? MaxFramesBytesOverrideForTests { get; set; }

    /// <summary>
    /// Background thread. The frames scaled to fit <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, with their
    /// delays; null when the file is not an animated GIF or is over a limit. The caller owns (disposes) the bitmaps.
    /// </summary>
    public static List<(WriteableBitmap Bitmap, int DelayMs)>? Decode(string path, int maxWidth, int maxHeight, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFileBytes) return null;
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > MaxFileBytes) return null;
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        token.ThrowIfCancellationRequested();

        var budget = MaxFramesBytesOverrideForTests ?? MaxFramesBytes;
        var frames = new List<(WriteableBitmap Bitmap, int DelayMs)>();
        var refused = false;
        long total = 0;
        try
        {
            var size = GifAnimationDecoder.DecodeFrames(bytes, (canvas, width, height, delayMs) =>
            {
                token.ThrowIfCancellationRequested();
                var dimensions = new ImageDimensions(width, height);
                if (frames.Count == 0 && (DecodeMemoryGuard.ExceedsPixelCap(dimensions) || !DecodeMemoryGuard.FitsAvailableMemory(dimensions)))
                {
                    refused = true;
                    return false;
                }
                var (targetWidth, targetHeight) = FitWithin(width, height, maxWidth, maxHeight);
                total += (long)targetWidth * targetHeight * 4;
                if (total > budget)
                {
                    refused = true;
                    return false;
                }
                var bitmap = new WriteableBitmap(new PixelSize(targetWidth, targetHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                try
                {
                    using var locked = bitmap.Lock();
                    Downscale(canvas, width, height, locked.Address, locked.RowBytes, targetWidth, targetHeight);
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
                frames.Add((bitmap, delayMs));
                return true;
            }, CanvasBudgetBytes, token);
            if (size is null || refused || frames.Count < 2)
            {
                Dispose(frames);
                return null;
            }
            return frames;
        }
        catch
        {
            Dispose(frames);
            throw;
        }
    }

    public static void Dispose(IEnumerable<(WriteableBitmap Bitmap, int DelayMs)> frames)
    {
        foreach (var (bitmap, _) in frames) bitmap.Dispose();
    }

    /// <summary>The size that fits within the box keeping the aspect ratio, never enlarged, at least 1x1.</summary>
    internal static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Box-filters a decoder canvas (ARGB, each pixel opaque or fully transparent) into premultiplied BGRA8 at
    /// <paramref name="targetWidth"/> x <paramref name="targetHeight"/>.
    /// </summary>
    internal static void Downscale(uint[] canvas, int width, int height, IntPtr destination, int destinationStride, int targetWidth, int targetHeight)
    {
        var row = new byte[targetWidth * 4];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = (int)((long)ty * height / targetHeight);
            var y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * height / targetHeight));
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = (int)((long)tx * width / targetWidth);
                var x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * width / targetWidth));
                long a = 0, r = 0, g = 0, b = 0;
                for (var y = y0; y < y1; y++)
                {
                    var offset = y * width;
                    for (var x = x0; x < x1; x++)
                    {
                        var pixel = canvas[offset + x];
                        if (pixel >> 24 == 0) continue;
                        a += 255;
                        r += (pixel >> 16) & 0xFF;
                        g += (pixel >> 8) & 0xFF;
                        b += pixel & 0xFF;
                    }
                }
                var count = (long)(y1 - y0) * (x1 - x0);
                var p = tx * 4;
                row[p] = (byte)(b / count);
                row[p + 1] = (byte)(g / count);
                row[p + 2] = (byte)(r / count);
                row[p + 3] = (byte)(a / count);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, destination + ty * destinationStride, row.Length);
        }
    }
}
