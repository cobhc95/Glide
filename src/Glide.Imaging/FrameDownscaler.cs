using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Glide.Imaging;

/// <summary>
/// Box-filter (area average) downscale of a 32-bit bitmap, off the UI thread. Reads the source in row bands (never a
/// second full-size copy), uses pooled band/row buffers (no large-object-heap garbage) and writes straight into the
/// target. Used by the print preview proxy and by the 5.1.1 memory trims (frames you left kept at fit size).
/// </summary>
public static class FrameDownscaler
{
    /// <summary>A <paramref name="size"/> copy of <paramref name="source"/>; null when the format is not 32-bit or scaling failed.</summary>
    public static Bitmap? TryAreaDownscale(Bitmap source, PixelSize size, int maxWorkers = 1)
    {
        var format = source.Format;
        if (format is null || (format != PixelFormat.Bgra8888 && format != PixelFormat.Rgba8888)) return null;
        var sw = source.PixelSize.Width;
        var sh = source.PixelSize.Height;
        var dw = size.Width;
        var dh = size.Height;
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0 || dw > sw || dh > sh) return null;
        var alpha = source.AlphaFormat ?? AlphaFormat.Premul;
        WriteableBitmap? target = null;
        try
        {
            target = new WriteableBitmap(size, new Vector(96, 96), format.Value, alpha);
            var x0 = new int[dw];
            var x1 = new int[dw];
            for (var x = 0; x < dw; x++)
            {
                x0[x] = (int)((long)x * sw / dw);
                x1[x] = Math.Max(x0[x] + 1, (int)((long)(x + 1) * sw / dw));
            }
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxWorkers) };
            const int outRowsPerBand = 32;
            var stride = sw * 4;
            using var fb = target.Lock();
            var dstBase = fb.Address;
            var dstStride = fb.RowBytes;
            for (var bandStart = 0; bandStart < dh; bandStart += outRowsPerBand)
            {
                var bandEnd = Math.Min(dh, bandStart + outRowsPerBand);
                var srcTop = (int)((long)bandStart * sh / dh);
                var srcBottom = Math.Max(srcTop + 1, (int)((long)bandEnd * sh / dh));
                var rows = srcBottom - srcTop;
                var bandLength = rows * stride;
                var band = ArrayPool<byte>.Shared.Rent(bandLength);
                try
                {
                    var handle = GCHandle.Alloc(band, GCHandleType.Pinned);
                    try { source.CopyPixels(new PixelRect(0, srcTop, sw, rows), handle.AddrOfPinnedObject(), bandLength, stride); }
                    finally { handle.Free(); }
                    Parallel.For(bandStart, bandEnd, options, oy =>
                    {
                        var y0 = (int)((long)oy * sh / dh);
                        var y1 = Math.Max(y0 + 1, (int)((long)(oy + 1) * sh / dh));
                        var outRow = ArrayPool<byte>.Shared.Rent(dw * 4);
                        try
                        {
                            for (var ox = 0; ox < dw; ox++)
                            {
                                long b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                                for (var y = y0; y < y1; y++)
                                {
                                    var row = (y - srcTop) * stride;
                                    for (var x = x0[ox]; x < x1[ox]; x++)
                                    {
                                        var i = row + x * 4;
                                        b0 += band[i]; b1 += band[i + 1]; b2 += band[i + 2]; b3 += band[i + 3];
                                    }
                                }
                                var n = (long)(y1 - y0) * (x1[ox] - x0[ox]);
                                var o = ox * 4;
                                outRow[o] = (byte)((b0 + n / 2) / n);
                                outRow[o + 1] = (byte)((b1 + n / 2) / n);
                                outRow[o + 2] = (byte)((b2 + n / 2) / n);
                                outRow[o + 3] = (byte)((b3 + n / 2) / n);
                            }
                            Marshal.Copy(outRow, 0, dstBase + oy * dstStride, dw * 4);
                        }
                        finally { ArrayPool<byte>.Shared.Return(outRow); }
                    });
                }
                finally { ArrayPool<byte>.Shared.Return(band); }
            }
            return target;
        }
        catch
        {
            target?.Dispose();
            return null;
        }
    }
}
