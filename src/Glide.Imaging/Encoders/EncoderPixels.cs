namespace Glide.Imaging.Encoders;

/// <summary>A tightly packed straight-alpha (unassociated) BGRA8 image: the input every managed writer receives.</summary>
public readonly record struct StraightImage(int Width, int Height, byte[] Bgra)
{
    public bool HasAlpha
    {
        get
        {
            for (var i = 3; i < Bgra.Length; i += 4) if (Bgra[i] != 255) return true;
            return false;
        }
    }

    /// <summary>Rec. 601 luma of pixel <paramref name="index"/> (alpha ignored).</summary>
    public int Luma(int index)
    {
        var o = index * 4;
        return (Bgra[o] * 29 + Bgra[o + 1] * 150 + Bgra[o + 2] * 77 + 128) >> 8;
    }
}

/// <summary>Pixel conversions shared by the save pipeline.</summary>
public static class EncoderPixels
{
    /// <summary>Save background for formats without transparency: white, the same policy Print uses.</summary>
    public const uint DefaultBackground = 0xFFFFFFFF;

    public static bool HasTransparency(ReadOnlySpan<byte> bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4) if (bgra[i] != 255) return true;
        return false;
    }

    /// <summary>
    /// Composites premultiplied BGRA over an opaque <paramref name="background"/> (0xAARRGGBB, alpha ignored) in
    /// place: the result is opaque, so it is valid as premultiplied and as straight alpha.
    /// </summary>
    public static void CompositeOverInPlace(Span<byte> premultiplied, uint background = DefaultBackground)
    {
        var bb = (int)(background & 0xFF);
        var bg = (int)((background >> 8) & 0xFF);
        var br = (int)((background >> 16) & 0xFF);
        for (var i = 0; i + 3 < premultiplied.Length; i += 4)
        {
            var a = premultiplied[i + 3];
            if (a == 255) continue;
            var inv = 255 - a;
            premultiplied[i] = (byte)Math.Min(255, premultiplied[i] + (bb * inv + 127) / 255);
            premultiplied[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] + (bg * inv + 127) / 255);
            premultiplied[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] + (br * inv + 127) / 255);
            premultiplied[i + 3] = 255;
        }
    }

    /// <summary>Premultiplied BGRA (Avalonia's bitmap layout) to a new straight-alpha buffer.</summary>
    public static byte[] Unpremultiply(ReadOnlySpan<byte> premultiplied)
    {
        var output = new byte[premultiplied.Length];
        for (var i = 0; i + 3 < premultiplied.Length; i += 4)
        {
            var a = premultiplied[i + 3];
            output[i + 3] = a;
            if (a == 255)
            {
                output[i] = premultiplied[i]; output[i + 1] = premultiplied[i + 1]; output[i + 2] = premultiplied[i + 2];
            }
            else if (a != 0)
            {
                output[i] = (byte)Math.Min(255, (premultiplied[i] * 255 + a / 2) / a);
                output[i + 1] = (byte)Math.Min(255, (premultiplied[i + 1] * 255 + a / 2) / a);
                output[i + 2] = (byte)Math.Min(255, (premultiplied[i + 2] * 255 + a / 2) / a);
            }
        }
        return output;
    }

    /// <summary>Straight BGRA to a new premultiplied buffer (for handing managed results to the WIC encoder).</summary>
    public static byte[] Premultiply(ReadOnlySpan<byte> straight)
    {
        var output = new byte[straight.Length];
        for (var i = 0; i + 3 < straight.Length; i += 4)
        {
            var a = straight[i + 3];
            output[i + 3] = a;
            if (a == 255)
            {
                output[i] = straight[i]; output[i + 1] = straight[i + 1]; output[i + 2] = straight[i + 2];
            }
            else if (a != 0)
            {
                output[i] = (byte)((straight[i] * a + 127) / 255);
                output[i + 1] = (byte)((straight[i + 1] * a + 127) / 255);
                output[i + 2] = (byte)((straight[i + 2] * a + 127) / 255);
            }
        }
        return output;
    }

    /// <summary>sRGB 8-bit to scene-linear (the inverse of the transfer Glide's EXR/PFM decoders apply).</summary>
    public static float SrgbToLinear(byte value) => SrgbTable[value];

    private static readonly float[] SrgbTable = BuildSrgbTable();

    private static float[] BuildSrgbTable()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var v = i / 255.0;
            table[i] = (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
        }
        return table;
    }

    /// <summary>
    /// Area-average downscale of straight BGRA, weighting colour by alpha so transparent pixels do not bleed dark
    /// fringes (used for icons larger than 256 px).
    /// </summary>
    public static StraightImage Downscale(StraightImage source, int targetWidth, int targetHeight)
    {
        var output = new byte[checked(targetWidth * targetHeight * 4)];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = (int)((long)ty * source.Height / targetHeight);
            var y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * source.Height / targetHeight));
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = (int)((long)tx * source.Width / targetWidth);
                var x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * source.Width / targetWidth));
                long b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * source.Width;
                    for (var x = x0; x < x1; x++)
                    {
                        var o = (row + x) * 4;
                        long alpha = source.Bgra[o + 3];
                        b += source.Bgra[o] * alpha; g += source.Bgra[o + 1] * alpha; r += source.Bgra[o + 2] * alpha;
                        a += alpha; n++;
                    }
                }
                var d = (ty * targetWidth + tx) * 4;
                if (a > 0)
                {
                    output[d] = (byte)((b + a / 2) / a); output[d + 1] = (byte)((g + a / 2) / a); output[d + 2] = (byte)((r + a / 2) / a);
                }
                output[d + 3] = (byte)((a + n / 2) / n);
            }
        }
        return new StraightImage(targetWidth, targetHeight, output);
    }
}
