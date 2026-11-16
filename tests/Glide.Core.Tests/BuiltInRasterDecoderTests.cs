using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// Round-trip coverage for the dependency-free raster decoder. Each fixture is a minimal hand-built
/// file so the test fails loudly if any per-format parser regresses, and every assertion is exact
/// straight-BGRA, so alpha/orientation mistakes cannot hide behind a Skia-rendered comparison.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class BuiltInRasterDecoderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    // -- TGA ---------------------------------------------------------------------------------

    [Fact]
    public void Decodes_2x2_uncompressed_24bit_tga()
    {
        var path = Temp(Tga2x2(), ".tga");
        Assert.True(BuiltInRasterDecoder.CanDecode(path));
        AssertDimensions(path, 2, 2);
        AssertBgra(path, new byte[]
        {
            0, 0, 255, 255,       // red
            0, 255, 0, 255,       // green
            255, 0, 0, 255,       // blue
            255, 255, 255, 255,   // white
        });
    }

    // -- QOI ---------------------------------------------------------------------------------

    [Fact]
    public void Decodes_3x1_qoi()
    {
        var path = Temp(Qoi3x1(), ".qoi");
        Assert.True(BuiltInRasterDecoder.CanDecode(path));
        AssertDimensions(path, 3, 1);
        AssertBgra(path, new byte[]
        {
            30, 20, 10, 255,
            60, 50, 40, 255,
            90, 80, 70, 255,
        });
    }

    // -- Netpbm ------------------------------------------------------------------------------

    [Fact]
    public void Decodes_p6_ppm()
    {
        var path = Temp(PpmP6(), ".ppm");
        AssertDimensions(path, 2, 1);
        AssertBgra(path, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 });
    }

    [Fact]
    public void Decodes_p5_pgm()
    {
        var path = Temp(PgmP5(), ".pgm");
        AssertDimensions(path, 2, 1);
        AssertBgra(path, new byte[] { 0, 0, 0, 255, 128, 128, 128, 255 });
    }

    [Fact]
    public void Decodes_p4_pbm()
    {
        var path = Temp(PbmP4(), ".pbm");
        AssertDimensions(path, 2, 1);
        AssertBgra(path, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 });
    }

    // -- WBMP --------------------------------------------------------------------------------

    [Fact]
    public void Decodes_type0_wbmp()
    {
        var path = Temp(Wbmp2x1(), ".wbmp");
        AssertDimensions(path, 2, 1);
        // 0x80: the first pixel's bit is set. WBMP type 0 defines 1 = white, 0 = black (fixed in 5.0.4; was inverted).
        AssertBgra(path, new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 });
    }

    // -- X11 ---------------------------------------------------------------------------------

    [Fact]
    public void Decodes_small_xbm()
    {
        var path = Temp(Xbm2x1(), ".xbm");
        AssertDimensions(path, 2, 1);
        AssertBgra(path, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 });
    }

    [Fact]
    public void Decodes_small_xpm()
    {
        var path = Temp(Xpm2x1(), ".xpm");
        AssertDimensions(path, 2, 1);
        AssertBgra(path, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 });
    }

    // -- SGI ---------------------------------------------------------------------------------

    [Fact]
    public void Decodes_2x2_verbatim_sgi()
    {
        var path = Temp(Sgi2x2(), ".sgi");
        Assert.True(BuiltInRasterDecoder.CanDecode(path));
        AssertDimensions(path, 2, 2);
        // File row 0 is the BOTTOM row (SGI stores scanlines bottom-to-top).
        AssertBgra(path, new byte[]
        {
            255, 0, 0, 255,       // top-left: blue (file row 1)
            0, 0, 0, 255,         // top-right: black
            0, 0, 255, 255,       // bottom-left: red (file row 0)
            0, 255, 0, 255,       // bottom-right: green
        });
    }

    // -- Real-world regressions found by decoding public sample files (fixtures here are synthetic) --------

    [Fact]
    public void Rle_tga_run_packets_repeat_one_pixel_and_raw_packets_copy_pixels()
    {
        // Packet header bit 7 set = run-length (repeat); clear = raw. 24-bit, 5x1, top-left origin.
        var data = new byte[18 + 1 + 3 + 1 + 6];
        data[2] = 10; data[12] = 5; data[14] = 1; data[16] = 24; data[17] = 0x20;
        var p = 18;
        data[p++] = 0x82; Write(data, ref p, 0, 0, 255);                          // run of 3 red
        data[p++] = 0x01; Write(data, ref p, 0, 255, 0); Write(data, ref p, 255, 0, 0); // raw: green, blue
        var path = Temp(data, ".tga");
        AssertBgra(path, new byte[]
        {
            0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
        });
    }

    [Fact]
    public void Rle_tga_grey_alpha_16bit_decodes()
    {
        // Type 11 (RLE grey), 16 bpp = grey + alpha: one run of 2 pixels (grey 100, alpha 50).
        var data = new byte[18 + 1 + 2];
        data[2] = 11; data[12] = 2; data[14] = 1; data[16] = 16; data[17] = 0x28;
        data[18] = 0x81; data[19] = 100; data[20] = 50;
        AssertBgra(Temp(data, ".tga"), new byte[] { 100, 100, 100, 50, 100, 100, 100, 50 });
    }

    [Fact]
    public void Xpm_pixel_key_may_be_a_space()
    {
        var xpm = "/* XPM */\nstatic char * k[] = {\n\"2 1 2 1\",\n\"  c #FF0000\",\n\"a c #00FF00\",\n\" a\"\n};\n"u8.ToArray();
        AssertBgra(Temp(xpm, ".xpm"), new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 });
    }

    [Fact]
    public void Pcx_four_planar_one_bit_planes_use_the_header_palette()
    {
        var data = new byte[128 + 4 * 2];
        data[0] = 0x0A; data[1] = 5; data[2] = 0; data[3] = 1; data[8] = 7; data[10] = 0; // 8x1, raw, 1 bit per plane
        data[16 + 3] = 255;                                    // palette index 1 = red
        data[16 + 6 + 1] = 255;                                // palette index 2 = green
        data[65] = 4; data[66] = 2;                            // four planes, 2 bytes per plane line
        data[128 + 0] = 0b1000_0000;                           // plane 0: pixel 0 -> index bit 0
        data[128 + 2 + 0] = 0b0100_0000;                       // plane 1: pixel 1 -> index bit 1
        var path = Temp(data, ".pcx");
        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(path, 0, out var w, out var h, out var bgra));
        Assert.Equal((8, 1), (w, h));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, bgra[..4]);   // red
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, bgra[4..8]);  // green
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, bgra[8..12]);   // index 0 = black
    }

    // -- Bounded / defensive ----------------------------------------------------------------

    [Fact]
    public void Bounded_decode_box_downscales_to_one_pixel()
    {
        var path = Temp(Tga2x2(), ".tga");
        Assert.True(BuiltInRasterDecoder.TryDecodeBounded(path, 1, out var bitmap));
        Assert.NotNull(bitmap);
        using var bmp = bitmap!;
        Assert.Equal(1, bmp.PixelSize.Width);
        Assert.Equal(1, bmp.PixelSize.Height);
        Assert.Equal(new byte[] { 127, 127, 127, 255 }, ReadBgra(bmp));
    }

    [Fact]
    public void Malformed_input_never_throws_and_returns_false()
    {
        var path = Temp(new byte[] { 1, 2, 3, 4 }, ".tga");
        Assert.False(BuiltInRasterDecoder.TryDecode(path, out var bitmap));
        Assert.Null(bitmap);
        Assert.False(BuiltInRasterDecoder.TryDecodeBounded(path, 8, out _));
        Assert.False(BuiltInRasterDecoder.TryProbeDimensions(path, out _));
    }

    [Fact]
    public void Non_decodable_extension_is_rejected()
    {
        var path = Temp(PpmP6(), ".png");
        Assert.False(BuiltInRasterDecoder.CanDecode(path));
        Assert.False(BuiltInRasterDecoder.TryDecode(path, out var bitmap));
        Assert.Null(bitmap);
    }

    [Theory]
    [InlineData("image.tga", true)]
    [InlineData("image.TARGA", true)]
    [InlineData("image.icb", true)]
    [InlineData("image.vda", true)]
    [InlineData("image.vst", true)]
    [InlineData("image.pcx", true)]
    [InlineData("image.pnm", true)]
    [InlineData("image.ppm", true)]
    [InlineData("image.pgm", true)]
    [InlineData("image.pbm", true)]
    [InlineData("image.pam", true)]
    [InlineData("image.qoi", true)]
    [InlineData("image.hdr", true)]
    [InlineData("image.rgbe", true)]
    [InlineData("image.wbmp", true)]
    [InlineData("image.xbm", true)]
    [InlineData("image.xpm", true)]
    [InlineData("image.sgi", true)]
    [InlineData("image.rgb", true)]
    [InlineData("image.rgba", true)]
    [InlineData("image.bw", true)]
    [InlineData("image.png", false)]
    [InlineData("image.jpg", false)]
    [InlineData("", false)]
    public void CanDecode_matches_declared_suffixes(string path, bool expected)
        => Assert.Equal(expected, BuiltInRasterDecoder.CanDecode(path));

    // -- Fixtures ----------------------------------------------------------------------------

    private static byte[] Tga2x2()
    {
        var data = new byte[18 + 2 * 2 * 3];
        data[2] = 2;           // uncompressed truecolor
        data[12] = 2;          // width low
        data[13] = 0;          // width high
        data[14] = 2;          // height low
        data[15] = 0;          // height high
        data[16] = 24;         // bits per pixel
        data[17] = 0x20;       // top-left origin, no alpha bits
        var p = 18;
        Write(data, ref p, 0, 0, 255);       // red   (B,G,R)
        Write(data, ref p, 0, 255, 0);       // green
        Write(data, ref p, 255, 0, 0);       // blue
        Write(data, ref p, 255, 255, 255);   // white
        return data;
    }

    private static byte[] Qoi3x1()
    {
        var data = new byte[14 + 3 * 4 + 8];
        "qoif"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 1);
        data[12] = 3; // channels
        data[13] = 0; // colorspace
        var p = 14;
        data[p++] = 0xFE; data[p++] = 10; data[p++] = 20; data[p++] = 30;
        data[p++] = 0xFE; data[p++] = 40; data[p++] = 50; data[p++] = 60;
        data[p++] = 0xFE; data[p++] = 70; data[p++] = 80; data[p++] = 90;
        data[p++] = 0; data[p++] = 0; data[p++] = 0; data[p++] = 0;
        data[p++] = 0; data[p++] = 0; data[p++] = 0; data[p++] = 1; // end marker
        return data;
    }

    private static byte[] PpmP6()
    {
        var header = "P6\n2 1\n255\n"u8.ToArray();
        var data = new byte[header.Length + 6];
        header.CopyTo(data, 0);
        var p = header.Length;
        Write(data, ref p, 255, 0, 0);
        Write(data, ref p, 0, 255, 0);
        return data;
    }

    private static byte[] PgmP5()
    {
        var header = "P5\n2 1\n255\n"u8.ToArray();
        var data = new byte[header.Length + 2];
        header.CopyTo(data, 0);
        var p = header.Length;
        data[p++] = 0;
        data[p++] = 128;
        return data;
    }

    private static byte[] PbmP4()
    {
        var header = "P4\n2 1\n"u8.ToArray();
        var data = new byte[header.Length + 1];
        header.CopyTo(data, 0);
        data[header.Length] = 0x80; // first pixel black, second white
        return data;
    }

    private static byte[] Wbmp2x1() => new byte[] { 0, 0, 2, 1, 0x80 };

    private static byte[] Xbm2x1() => "/* XBM */\n#define sample_width 2\n#define sample_height 1\nstatic unsigned char sample_bits[] = { 0x01 };\n"u8.ToArray();

    private static byte[] Xpm2x1() =>
        "/* XPM */\nstatic char * sample_xpm[] = {\n\"2 1 2 1\",\n\"a c #FF0000\",\n\"b c #00FF00\",\n\"ab\"\n};\n"u8.ToArray();

    private static byte[] Sgi2x2()
    {
        var data = new byte[512 + 2 * 2 * 3];
        data[0] = 0x01;
        data[1] = 0xDA;
        data[2] = 0; // verbatim
        data[3] = 1; // bytes per channel
        WriteU16BigEndian(data, 4, 3);  // dimension
        WriteU16BigEndian(data, 6, 2);  // xsize
        WriteU16BigEndian(data, 8, 2);  // ysize
        WriteU16BigEndian(data, 10, 3); // channels

        var p = 512;
        // Red plane: row0 = [255, 0], row1 = [0, 0]
        data[p++] = 255; data[p++] = 0;
        data[p++] = 0; data[p++] = 0;
        // Green plane: row0 = [0, 255], row1 = [0, 0]
        data[p++] = 0; data[p++] = 255;
        data[p++] = 0; data[p++] = 0;
        // Blue plane: row0 = [0, 0], row1 = [255, 0]
        data[p++] = 0; data[p++] = 0;
        data[p++] = 255; data[p++] = 0;
        return data;
    }

    // -- Helpers -----------------------------------------------------------------------------

    private string Temp(byte[] payload, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-raster-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, payload);
        _tempFiles.Add(path);
        return path;
    }

    private static void AssertDimensions(string path, int width, int height)
    {
        Assert.True(BuiltInRasterDecoder.TryProbeDimensions(path, out var dimensions), "Probe should succeed.");
        Assert.Equal(width, dimensions.Width);
        Assert.Equal(height, dimensions.Height);
    }

    private void AssertBgra(string path, byte[] expected)
    {
        Assert.True(BuiltInRasterDecoder.TryDecode(path, out var bitmap), "Decode should succeed.");
        Assert.NotNull(bitmap);
        using var bmp = bitmap!;
        Assert.Equal(expected, ReadBgra(bmp));
    }

    private static byte[] ReadBgra(Bitmap bitmap)
    {
        var writeable = Assert.IsType<WriteableBitmap>(bitmap);
        var width = writeable.PixelSize.Width;
        var height = writeable.PixelSize.Height;
        var result = new byte[width * height * 4];
        using var frame = writeable.Lock();
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes), result, y * width * 4, width * 4);
        }
        return result;
    }

    private static void Write(byte[] data, ref int position, byte b, byte g, byte r)
    {
        data[position++] = b;
        data[position++] = g;
        data[position++] = r;
    }

    private static void WriteU16BigEndian(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
