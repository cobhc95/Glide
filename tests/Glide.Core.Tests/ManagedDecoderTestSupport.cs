using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Glide.Imaging;
using SkiaSharp;

namespace Glide.Core.Tests;

/// <summary>Shared synthetic-fixture helpers for the bundled managed decoder tests.</summary>
internal static class ManagedDecoderTestSupport
{
    public static string Temp(List<string> cleanup, byte[] payload, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-managed-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, payload);
        cleanup.Add(path);
        return path;
    }

    public static void Cleanup(List<string> files)
    {
        foreach (var file in files)
        {
            try { File.Delete(file); } catch { }
        }
    }

    public readonly record struct Pixels(int Width, int Height, byte[] Bgra)
    {
        public (int R, int G, int B, int A) At(int x, int y)
        {
            var o = (y * Width + x) * 4;
            return (Bgra[o + 2], Bgra[o + 1], Bgra[o], Bgra[o + 3]);
        }
    }

    /// <summary>Decodes through the production dispatch and checks the probe agrees with the frame size.</summary>
    public static Pixels Decode(string path, int maxLongestSide = 0, bool checkProbe = true)
    {
        Assert.True(BuiltInRasterDecoder.CanDecode(path), $"CanDecode should accept {Path.GetExtension(path)}");
        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(path, maxLongestSide, out var w, out var h, out var bgra), "Decode should succeed.");
        if (checkProbe && maxLongestSide == 0)
        {
            Assert.True(BuiltInRasterDecoder.TryProbeDimensions(path, out var dims), "Probe should succeed.");
            Assert.Equal(w, dims.Width);
            Assert.Equal(h, dims.Height);
        }
        return new Pixels(w, h, bgra);
    }

    public static void AssertFails(string path, string? label = null)
    {
        var ok = BuiltInRasterDecoder.TryDecodePixelsForTests(path, 0, out var w, out var h, out _);
        Assert.False(ok, $"Malformed input must fail ({label ?? Path.GetExtension(path)}; decoded {w}x{h}).");
        Assert.False(BuiltInRasterDecoder.TryDecode(path, out var bitmap));
        Assert.Null(bitmap);
        Assert.False(BuiltInRasterDecoder.TryDecodeBounded(path, 64, out _));
    }

    public static void AssertColor((int R, int G, int B, int A) actual, int r, int g, int b, int tolerance = 0, int a = 255)
    {
        Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance &&
                    Math.Abs(actual.A - a) <= tolerance,
            $"expected ({r},{g},{b},{a}) ±{tolerance}, got ({actual.R},{actual.G},{actual.B},{actual.A})");
    }

    // -- Encoded sub-images -------------------------------------------------------------------

    /// <summary>A JPEG whose left half is <paramref name="left"/> and right half <paramref name="right"/>.</summary>
    public static byte[] Jpeg(int width, int height, SKColor left, SKColor right, int exifOrientation = 0)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, x < width / 2 ? left : right);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var bytes = data.ToArray();
        if (exifOrientation == 0) return bytes;
        // Insert an APP1 Exif segment carrying only the orientation tag right after SOI.
        var tiff = new byte[] { (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)exifOrientation, 0, 0, 0, 0, 0, 0, 0 };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var app1 = new byte[4 + payload.Length];
        app1[0] = 0xFF; app1[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(app1.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(app1, 4);
        return bytes.Take(2).Concat(app1).Concat(bytes.Skip(2)).ToArray();
    }

    public static byte[] Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, color(x, y));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    /// <summary>Little helper for building binary fixtures.</summary>
    public sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        public bool BigEndian { get; set; }
        public long Position => _stream.Position;
        public long Length => _stream.Length;

        public Writer U8(int v) { _stream.WriteByte((byte)v); return this; }
        public Writer Bytes(ReadOnlySpan<byte> b) { _stream.Write(b); return this; }
        public Writer Ascii(string s) { _stream.Write(Encoding.ASCII.GetBytes(s)); return this; }
        public Writer Zeros(int n) { _stream.Write(new byte[n]); return this; }

        public Writer U16(int v)
        {
            Span<byte> b = stackalloc byte[2];
            if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
            _stream.Write(b);
            return this;
        }

        public Writer U32(long v)
        {
            Span<byte> b = stackalloc byte[4];
            if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v);
            _stream.Write(b);
            return this;
        }

        public Writer U64(long v)
        {
            Span<byte> b = stackalloc byte[8];
            if (BigEndian) BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); else BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)v);
            _stream.Write(b);
            return this;
        }

        public Writer F32(float v)
        {
            Span<byte> b = stackalloc byte[4];
            if (BigEndian) BinaryPrimitives.WriteSingleBigEndian(b, v); else BinaryPrimitives.WriteSingleLittleEndian(b, v);
            _stream.Write(b);
            return this;
        }

        public Writer F64(double v)
        {
            Span<byte> b = stackalloc byte[8];
            if (BigEndian) BinaryPrimitives.WriteDoubleBigEndian(b, v); else BinaryPrimitives.WriteDoubleLittleEndian(b, v);
            _stream.Write(b);
            return this;
        }

        public Writer Align(int alignment, byte fill = 0)
        {
            while (_stream.Length % alignment != 0) _stream.WriteByte(fill);
            return this;
        }

        /// <summary>Overwrites a 32-bit value at an earlier position.</summary>
        public void PatchU32(long position, long value)
        {
            var current = _stream.Position;
            _stream.Position = position;
            U32(value);
            _stream.Position = current;
        }

        public void PatchU64(long position, long value)
        {
            var current = _stream.Position;
            _stream.Position = position;
            U64(value);
            _stream.Position = current;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
