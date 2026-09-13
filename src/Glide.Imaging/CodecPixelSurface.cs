using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Glide.Imaging;

/// <summary>ABI-v2 decoded pixel lease. Current contract is premultiplied BGRA8.</summary>
public sealed class CodecPixelSurface : IDisposable
{
    private Action<nint, nuint>? _release;

    public CodecPixelSurface(nint data, nuint size, int width, int height, int stride, Action<nint, nuint> release)
    {
        if (data == 0) throw new ArgumentException("Surface pointer is null.", nameof(data));
        if (width <= 0 || height <= 0 || width > 1_000_000 || height > 1_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        if (stride < checked(width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        var required = checked((ulong)stride * (ulong)height);
        if (required > size.ToUInt64() || required > 2UL * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
        Data = data; Size = size; Width = width; Height = height; Stride = stride; _release = release;
    }

    public nint Data { get; }
    public nuint Size { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is not null) release(Data, Size);
    }
}

/// <summary>
/// One-copy bridge from a provider-owned decoded surface to Avalonia. There is no encoded
/// intermediate and therefore no second PNG/BMP/JPEG decode. The viewport receives the same Bitmap
/// abstraction as before, so zoom/pan/selection code is untouched.
/// </summary>
internal static class CodecSurfaceBitmapFactory
{
    public static unsafe Bitmap Create(CodecPixelSurface surface)
    {
        WriteableBitmap? candidate = new(new PixelSize(surface.Width, surface.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = candidate.Lock();
            var rowBytes = checked(surface.Width * 4);
            if (frame.RowBytes < rowBytes) throw new InvalidDataException("Destination surface stride is too small.");
            var sourceRequired = checked((ulong)surface.Stride * (ulong)surface.Height);
            if (sourceRequired > surface.Size.ToUInt64()) throw new InvalidDataException("Provider surface is truncated.");

            if (frame.RowBytes == surface.Stride)
            {
                var bytes = checked((long)surface.Stride * surface.Height);
                Buffer.MemoryCopy((void*)surface.Data, (void*)frame.Address, bytes, bytes);
            }
            else
            {
                for (var y = 0; y < surface.Height; y++)
                {
                    var src = (byte*)surface.Data + (nuint)(y * surface.Stride);
                    var dst = (byte*)frame.Address + (nuint)(y * frame.RowBytes);
                    Buffer.MemoryCopy(src, dst, (long)frame.RowBytes, rowBytes);
                }
            }

            var result = candidate;
            candidate = null;
            return result;
        }
        finally { candidate?.Dispose(); }
    }
}
