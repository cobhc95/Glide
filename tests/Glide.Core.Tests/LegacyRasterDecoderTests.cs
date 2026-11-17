using Glide.Imaging;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.7: legacy rasters with no Windows codec, built in-test — packed and file-header DIB, ICNS (PNG
/// members), Sun raster (standard, RLE, colour-mapped), XWD (TrueColor and colour-mapped) and DPX
/// (16-bit little-endian, 10-bit filled big-endian). Each asserts size, colour placement and that
/// malformed input fails gracefully.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class LegacyRasterDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    // Every fixture is 8x4: left half red, right half blue.
    private const int W = 8, H = 4;
    private static (int R, int G, int B) ColorAt(int x) => x < W / 2 ? (255, 0, 0) : (0, 0, 255);

    private static void AssertRedBlue(Pixels pixels, int tolerance = 0)
    {
        Assert.Equal((W, H), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(1, 0), 255, 0, 0, tolerance);
        AssertColor(pixels.At(6, 3), 0, 0, 255, tolerance);
    }

    private static byte[] PackedDib24()
    {
        var w = new Writer();
        w.U32(40).U32(W).U32(H).U16(1).U16(24).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0);
        for (var y = 0; y < H; y++) // bottom-up rows of BGR, 4-byte aligned (8*3 = 24 already aligned)
            for (var x = 0; x < W; x++)
            {
                var c = ColorAt(x);
                w.U8(c.B).U8(c.G).U8(c.R);
            }
        return w.ToArray();
    }

    [Fact]
    public void Packed_dib_without_file_header_decodes()
        => AssertRedBlue(Decode(Temp(_files, PackedDib24(), ".dib")));

    [Fact]
    public void Dib_with_file_header_decodes()
    {
        var dib = PackedDib24();
        var w = new Writer();
        w.Ascii("BM").U32(14 + dib.Length).U32(0).U32(54).Bytes(dib);
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".dib")));
    }

    [Fact]
    public void Icns_shows_its_largest_png_member()
    {
        var small = Png(4, 2, (_, _) => SKColors.Lime);
        var large = Png(W, H, (x, _) => x < W / 2 ? SKColors.Red : SKColors.Blue);
        var w = new Writer { BigEndian = true };
        var total = 8 + 8 + small.Length + 8 + large.Length;
        w.Ascii("icns").U32(total);
        w.Ascii("ic11").U32(8 + small.Length).Bytes(small);
        w.Ascii("ic12").U32(8 + large.Length).Bytes(large);
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".icns")));
    }

    private static Writer SunHeader(int depth, int length, int type, int mapType = 0, int mapLength = 0)
    {
        var w = new Writer { BigEndian = true };
        w.U32(0x59A66A95).U32(W).U32(H).U32(depth).U32(length).U32(type).U32(mapType).U32(mapLength);
        return w;
    }

    [Fact]
    public void Sun_raster_standard_24_bit_is_bgr()
    {
        var w = SunHeader(24, W * H * 3, 1);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = ColorAt(x); w.U8(c.B).U8(c.G).U8(c.R); }
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".ras")));
    }

    [Fact]
    public void Sun_raster_byte_encoded_rle_decodes()
    {
        // Raw BGR rows, RLE-encoded: 0x80 n v = n+1 copies of v, 0x80 0x00 = a literal 0x80.
        var raw = new List<byte>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = ColorAt(x); raw.AddRange(new[] { (byte)c.B, (byte)c.G, (byte)c.R }); }
        raw[0] = 0x80; // a literal 0x80 byte must survive the 0x80 0x00 escape
        var encoded = new List<byte>();
        for (var i = 0; i < raw.Count;)
        {
            var run = 1;
            while (i + run < raw.Count && raw[i + run] == raw[i] && run < 256) run++;
            if (raw[i] == 0x80 && run == 1) { encoded.AddRange(new byte[] { 0x80, 0x00 }); i++; }
            else if (run >= 3 || raw[i] == 0x80) { encoded.AddRange(new[] { (byte)0x80, (byte)(run - 1), raw[i] }); i += run; }
            else { encoded.Add(raw[i]); i++; }
        }
        var w = SunHeader(24, encoded.Count, 2);
        w.Bytes(encoded.ToArray());
        var pixels = Decode(Temp(_files, w.ToArray(), ".ras"));
        Assert.Equal((W, H), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0x80);
        AssertColor(pixels.At(1, 0), 255, 0, 0);
        AssertColor(pixels.At(6, 3), 0, 0, 255);
    }

    [Fact]
    public void Sun_raster_8_bit_uses_its_colour_map_and_pads_rows_to_16_bits()
    {
        const int width = 3; // odd width: each row carries one padding byte
        var w = new Writer { BigEndian = true };
        w.U32(0x59A66A95).U32(width).U32(1).U32(8).U32(4).U32(1).U32(1).U32(6);
        w.U8(255).U8(0).U8(0).U8(0).U8(0).U8(255); // red plane, green plane, blue plane for 2 entries
        w.U8(0).U8(1).U8(0).U8(0);
        var pixels = Decode(Temp(_files, w.ToArray(), ".sun"));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(1, 0), 0, 0, 255);
    }

    private static Writer XwdHeader(int bitsPerPixel, int bytesPerLine, int visualClass, uint red, uint green, uint blue, int colors)
    {
        var w = new Writer { BigEndian = true };
        var name = "fixture\0"u8.ToArray();
        var headerSize = 100 + name.Length;
        w.U32(headerSize).U32(7).U32(2).U32(bitsPerPixel == 8 ? 8 : 24).U32(W).U32(H).U32(0)
         .U32(1).U32(32).U32(1).U32(32).U32(bitsPerPixel).U32(bytesPerLine).U32(visualClass)
         .U32(red).U32(green).U32(blue).U32(8).U32(colors).U32(colors).U32(W).U32(H).U32(0).U32(0).U32(0);
        w.Bytes(name);
        return w;
    }

    [Fact]
    public void Xwd_truecolor_32_bit_msb_first_decodes()
    {
        var w = XwdHeader(32, W * 4, 4, 0xFF0000, 0x00FF00, 0x0000FF, 0);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = ColorAt(x); w.U8(0).U8(c.R).U8(c.G).U8(c.B); }
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".xwd")));
    }

    [Fact]
    public void Xwd_8_bit_uses_its_colour_map()
    {
        var w = XwdHeader(8, W, 3, 0, 0, 0, 2);
        w.U32(0).U16(0xFFFF).U16(0).U16(0).U8(7).U8(0); // pixel 0 = red
        w.U32(1).U16(0).U16(0).U16(0xFFFF).U8(7).U8(0); // pixel 1 = blue
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) w.U8(x < W / 2 ? 0 : 1);
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".xwd")));
    }

    private static Writer DpxHeader(bool bigEndian, int descriptor, int bits, int packing)
    {
        var w = new Writer { BigEndian = bigEndian };
        w.Ascii(bigEndian ? "SDPX" : "XPDS").U32(2048).Ascii("V2.0\0\0\0\0").Zeros(768 - 16);
        w.U16(0).U16(1).U32(W).U32(H); // orientation, elements, pixels per line, lines
        w.Zeros(20).U8(descriptor).U8(2).U8(2).U8(bits).U16(packing).U16(0).U32(2048).U32(0).U32(0);
        w.Zeros(2048 - (int)w.Length);
        return w;
    }

    [Fact]
    public void Dpx_16_bit_little_endian_rgb_decodes()
    {
        var w = DpxHeader(false, 50, 16, 1);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = ColorAt(x); w.U16(c.R * 257).U16(c.G * 257).U16(c.B * 257); }
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".dpx")));
    }

    [Fact]
    public void Dpx_10_bit_filled_method_a_big_endian_decodes()
    {
        var w = DpxHeader(true, 50, 10, 1);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var c = ColorAt(x);
                uint Ten(int v) => (uint)(v * 1023 / 255);
                w.U32(Ten(c.R) << 22 | Ten(c.G) << 12 | Ten(c.B) << 2);
            }
        AssertRedBlue(Decode(Temp(_files, w.ToArray(), ".dpx")));
    }

    [Fact]
    public void Malformed_legacy_rasters_fail_gracefully()
    {
        var dib = PackedDib24();
        AssertFails(Temp(_files, dib.Take(60).ToArray(), ".dib"), "truncated dib");
        AssertFails(Temp(_files, "icns\0\0\0\x10ic07\0\0\0\x08"u8.ToArray(), ".icns"), "icns without png");
        var ras = SunHeader(24, W * H * 3, 1).Bytes(new byte[10]).ToArray();
        AssertFails(Temp(_files, ras, ".ras"), "truncated ras");
        AssertFails(Temp(_files, SunHeader(24, 100, 2).Bytes(new byte[] { 0x80, 0xFF }).ToArray(), ".ras"), "short rle");
        AssertFails(Temp(_files, XwdHeader(32, W * 4, 4, 0xFF0000, 0xFF00, 0xFF, 0).Bytes(new byte[8]).ToArray(), ".xwd"), "truncated xwd");
        AssertFails(Temp(_files, DpxHeader(false, 50, 16, 1).Bytes(new byte[8]).ToArray(), ".dpx"), "truncated dpx");
        AssertFails(Temp(_files, DpxHeader(false, 99, 16, 1).Bytes(new byte[W * H * 6]).ToArray(), ".dpx"), "unknown descriptor");
        var garbage = new byte[4096];
        new Random(5).NextBytes(garbage);
        foreach (var extension in new[] { ".dib", ".icns", ".ras", ".xwd", ".dpx" })
            AssertFails(Temp(_files, garbage, extension), "garbage " + extension);
    }
}
