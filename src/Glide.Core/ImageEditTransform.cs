namespace Glide.Core;

/// <summary>
/// Viewer edit state that Save bakes into pixels. Matches ImageViewport's display mapping:
/// rotate clockwise by QuarterTurns first, then flip within the rotated frame.
/// </summary>
public readonly record struct ImageEditTransform(int QuarterTurns, bool FlipHorizontal, bool FlipVertical)
{
    public int NormalizedTurns => ((QuarterTurns % 4) + 4) % 4;
    public bool IsIdentity => NormalizedTurns == 0 && !FlipHorizontal && !FlipVertical;

    public (int Width, int Height) OutputSize(int width, int height)
        => (NormalizedTurns & 1) == 0 ? (width, height) : (height, width);

    /// <summary>Transforms a tightly packed 32-bit (4 bytes/pixel) buffer.</summary>
    public byte[] Apply(ReadOnlySpan<byte> source, int width, int height, out int outWidth, out int outHeight)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (source.Length < checked(width * height * 4)) throw new ArgumentException("Buffer too small.", nameof(source));
        (outWidth, outHeight) = OutputSize(width, height);
        var dst = new byte[outWidth * outHeight * 4];
        if (IsIdentity) { source[..dst.Length].CopyTo(dst); return dst; }
        var turns = NormalizedTurns;
        var src32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source);
        var dst32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                int dx, dy;
                switch (turns)
                {
                    case 1: dx = height - 1 - y; dy = x; break;
                    case 2: dx = width - 1 - x; dy = height - 1 - y; break;
                    case 3: dx = y; dy = width - 1 - x; break;
                    default: dx = x; dy = y; break;
                }
                if (FlipHorizontal) dx = outWidth - 1 - dx;
                if (FlipVertical) dy = outHeight - 1 - dy;
                dst32[dy * outWidth + dx] = src32[row + x];
            }
        }
        return dst;
    }
}

/// <summary>Format decisions for Save / Save As, kept UI-free for testing.</summary>
public static class ImageSavePolicy
{
    public static readonly string[] SaveAsExtensions = [".jpg", ".png", ".bmp", ".tif"];

    /// <summary>True when a plain Save can re-encode the original in its own format.</summary>
    public static bool CanSaveInPlace(string path, Func<string, bool> canEncode)
        => canEncode(Path.GetExtension(path));

    /// <summary>Temp file in the destination folder (same volume => atomic replace); ignored by the folder watcher.</summary>
    public static string TempPathFor(string destination)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination)) ?? ".",
            "~glide-save-" + Guid.NewGuid().ToString("N") + ".tmp");

    public static string EnsureExtension(string destination, string fallbackExtension)
        => string.IsNullOrEmpty(Path.GetExtension(destination)) ? destination + fallbackExtension : destination;
}
