using System.Buffers.Binary;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.App.Services;
using Glide.Imaging;
using Glide.Tests.Support;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0: the Explorer tab produces a thumbnail for every format the viewer decodes (it was 11 suffixes). Per format: the
/// route, a real fixture decoded to a tile that shows the picture (left half red, right half blue) at the right size, the
/// glyph fallback for a file that is not what its suffix says, and the disk cache (every result is stored and the second
/// request is a hit). The scheduler (timeout, queue order, look-ahead) is covered in <see cref="ThumbnailSchedulerTests"/>.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailEveryFormatTests : IDisposable
{
    private const int TileWidth = 100;
    private readonly TempDir _dir = new("glide-thumb-every");
    private readonly List<string> _decoded = new();

    public ThumbnailEveryFormatTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        ThumbnailDiskCache.Configure(false, null, 0);
        ThumbnailDecode.DecodingForTests = path => { lock (_decoded) _decoded.Add(path); };
    }

    public void Dispose()
    {
        ThumbnailDiskCache.Configure(false, null, 0);
        ThumbnailDecode.DecodingForTests = null;
        _dir.Dispose();
    }

    // ---- fixtures: 300 x 200, the left half red and the right half blue -------------------------------------------------------

    private static (byte R, byte G, byte B) Side(int x, int width) => x < width / 2 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255);

    private static byte[] Ppm(int w, int h)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        var data = new byte[header.Length + w * h * 3];
        header.CopyTo(data, 0);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = Side(x, w);
                var o = header.Length + (y * w + x) * 3;
                data[o] = r; data[o + 1] = g; data[o + 2] = b;
            }
        return data;
    }

    private static byte[] Tga(int w, int h)
    {
        var data = new byte[18 + w * h * 3];
        data[2] = 2; // uncompressed true colour
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)h);
        data[16] = 24;
        data[17] = 0x20; // top-left origin
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = Side(x, w);
                var o = 18 + (y * w + x) * 3;
                data[o] = b; data[o + 1] = g; data[o + 2] = r;
            }
        return data;
    }

    private static byte[] Psd(int w, int h)
    {
        var plane = w * h;
        var data = new byte[26 + 4 + 4 + 4 + 2 + plane * 3];
        Encoding.ASCII.GetBytes("8BPS").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(14), (uint)h);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(18), (uint)w);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(24), 3); // RGB
        var pixels = 26 + 4 + 4 + 4 + 2; // colour mode data, image resources and layer / mask lengths are 0; compression 0 (raw)
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = Side(x, w);
                data[pixels + y * w + x] = r;
                data[pixels + plane + y * w + x] = g;
                data[pixels + 2 * plane + y * w + x] = b;
            }
        return data;
    }

    private static byte[] Hdr(int w, int h)
    {
        var header = Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n");
        var data = new byte[header.Length + w * h * 4];
        header.CopyTo(data, 0);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, _, b) = Side(x, w);
                var o = header.Length + (y * w + x) * 4;
                // 128 * 2^(e-136) = 1.0 for mantissa 128, exponent 129
                data[o] = (byte)(r > 0 ? 128 : 0); data[o + 1] = 0; data[o + 2] = (byte)(b > 0 ? 128 : 0); data[o + 3] = 129;
            }
        return data;
    }

    private static byte[] DdsRgba(int w, int h)
    {
        var data = new byte[128 + w * h * 4];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x0000100F | 0x8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)h);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)(w * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(88), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(92), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(96), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(100), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(104), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(108), 0x1000);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = Side(x, w);
                var o = 128 + (y * w + x) * 4;
                data[o] = b; data[o + 1] = g; data[o + 2] = r; data[o + 3] = 255;
            }
        return data;
    }

    private static byte[] Bmp24(int w, int h)
    {
        var rowBytes = (w * 3 + 3) & ~3;
        var data = new byte[54 + rowBytes * h];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), -h); // top-down
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 24);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = Side(x, w);
                var o = 54 + y * rowBytes + x * 3;
                data[o] = b; data[o + 1] = g; data[o + 2] = r;
            }
        return data;
    }

    private static byte[] HalfPng(int w, int h) => Png(w, h, (x, _) => x < w / 2 ? SKColors.Red : SKColors.Blue);

    private static byte[] HalfJpeg(int w, int h) => Jpeg(w, h, SKColors.Red, SKColors.Blue);

    private static byte[] HalfWebp(int w, int h)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) bitmap.SetPixel(x, y, x < w / 2 ? SKColors.Red : SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 95);
        return data.ToArray();
    }

    /// <summary>An ICO with one 64 x 64 PNG image (a PNG-in-ICO, what current icon tools write).</summary>
    private static byte[] Ico()
    {
        var png = HalfPng(64, 64);
        var data = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1);
        data[6] = 64; data[7] = 64;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(18), 22);
        png.CopyTo(data, 22);
        return data;
    }

    /// <summary>A DNG-style TIFF whose only picture is an embedded JPEG preview (the camera RAW route).</summary>
    private static byte[] RawWithPreview(int w, int h)
    {
        var jpeg = HalfJpeg(w, h);
        var writer = new Writer();
        writer.Bytes("II"u8).U16(42).U32(8);
        writer.U16(5);
        void Entry(int tag, int type, long count, long value) => writer.U16(tag).U16(type).U32(count).U32(value);
        var jpegOffset = 8 + 2 + 5 * 12 + 4;
        Entry(0x0100, 4, 1, 4000);
        Entry(0x0101, 4, 1, 3000);
        Entry(0x0112, 3, 1, 1);
        Entry(0x0201, 4, 1, jpegOffset);
        Entry(0x0202, 4, 1, jpeg.Length);
        writer.U32(0);
        writer.Bytes(jpeg);
        writer.Zeros(256);
        return writer.ToArray();
    }

    private const string PathSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="300" height="200" viewBox="0 0 300 200">
          <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#ff0000"/><stop offset="1" stop-color="#ff0000"/></linearGradient></defs>
          <rect x="0" y="0" width="150" height="200" fill="url(#g)"/>
          <path d="M150 0 H300 V200 H150 Z" fill="#0000ff"/>
          <g transform="translate(0,0)"><circle cx="40" cy="40" r="12" fill="#ff0000"/><circle cx="260" cy="160" r="12" fill="#0000ff"/></g>
        </svg>
        """;

    public static IEnumerable<object[]> Formats()
    {
        // name, extension, expected route, bytes
        yield return ["jpeg", ".jpg", "Jpeg", HalfJpeg(300, 200)];
        yield return ["jfif", ".jfif", "Jpeg", HalfJpeg(300, 200)];
        yield return ["jpe", ".jpe", "Jpeg", HalfJpeg(300, 200)];
        yield return ["jif", ".jif", "Jpeg", HalfJpeg(300, 200)];
        yield return ["mpo", ".mpo", "Jpeg", HalfJpeg(300, 200)];
        yield return ["png", ".png", "Skia", HalfPng(300, 200)];
        yield return ["apng", ".apng", "Skia", HalfPng(300, 200)];
        yield return ["ico", ".ico", "Skia", Ico()];
        yield return ["bmp", ".bmp", "NativeBridge", Bmp24(300, 200)];
        yield return ["webp", ".webp", "NativeBridge", HalfWebp(300, 200)];
        yield return ["svg", ".svg", "Svg", Encoding.UTF8.GetBytes(PathSvg)];
        yield return ["psd", ".psd", "BuiltIn", Psd(300, 200)];
        yield return ["tga", ".tga", "BuiltIn", Tga(300, 200)];
        yield return ["ppm", ".ppm", "BuiltIn", Ppm(300, 200)];
        yield return ["hdr", ".hdr", "BuiltIn", Hdr(300, 200)];
        yield return ["dds", ".dds", "BuiltIn", DdsRgba(300, 200)];
        yield return ["dng", ".dng", "BuiltIn", RawWithPreview(600, 400)];
        yield return ["cr2", ".cr2", "BuiltIn", RawWithPreview(600, 400)];
    }

    private static (byte B, byte G, byte R, byte A) Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    private string Write(string name, string extension, byte[] bytes)
    {
        var path = _dir.Combine(name + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertLeftRedRightBlue(Bitmap bitmap, string label)
    {
        var size = bitmap.PixelSize;
        var left = Pixel(bitmap, size.Width / 4, size.Height / 2);
        var right = Pixel(bitmap, size.Width * 3 / 4, size.Height / 2);
        Assert.True(left.R > 170 && left.B < 90 && left.A > 200, $"{label}: expected red on the left, got R{left.R} G{left.G} B{left.B} A{left.A}");
        Assert.True(right.B > 170 && right.R < 90 && right.A > 200, $"{label}: expected blue on the right, got R{right.R} G{right.G} B{right.B} A{right.A}");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Every_format_has_a_route_and_produces_a_tile_that_shows_the_picture(string name, string extension, string routeName, byte[] bytes)
    {
        var route = Enum.Parse<ThumbnailRoute>(routeName);
        if (route == ThumbnailRoute.NativeBridge && !OperatingSystem.IsWindows()) return;
        var path = Write(name, extension, bytes);
        Assert.Equal(route, ThumbnailRoutes.RouteFor(path));
        Assert.True(ThumbnailRoutes.CanThumbnail(path));

        using var bitmap = ThumbnailDecode.DecodeToWidth(path, TileWidth);
        Assert.NotNull(bitmap);
        var size = bitmap!.PixelSize;
        Assert.InRange(size.Width, TileWidth / 2, TileWidth + TileWidth / 2);
        // 3:2 pictures (the 64 x 64 icon is square)
        var expectedAspect = extension == ".ico" ? 1.0 : 1.5;
        Assert.InRange((double)size.Width / size.Height, expectedAspect - 0.12, expectedAspect + 0.12);
        AssertLeftRedRightBlue(bitmap, name);
        Assert.NotEmpty(_decoded);
    }

    [Theory]
    [InlineData("basic.heic")]
    [InlineData("basic.avif")]
    [InlineData("basic.jxl")]
    [InlineData("basic.jp2")]
    [InlineData("basic.j2k")]
    public void The_codec_plugin_formats_produce_a_tile_through_the_provider(string fixture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codecs", fixture);
        if (!File.Exists(source) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "codecs", "glidecodecs.dll"))) return; // no plug-in in this build output
        var path = Write("plugin", Path.GetExtension(fixture), File.ReadAllBytes(source));
        Assert.Equal(ThumbnailRoute.Provider, ThumbnailRoutes.RouteFor(path));
        using var bitmap = ThumbnailDecode.DecodeToWidth(path, TileWidth);
        Assert.NotNull(bitmap);
        Assert.True(bitmap!.PixelSize.Width > 0 && bitmap.PixelSize.Height > 0);
        Assert.True(bitmap.PixelSize.Width <= TileWidth + TileWidth / 2);
        var centre = Pixel(bitmap, bitmap.PixelSize.Width / 2, bitmap.PixelSize.Height / 2);
        Assert.True(centre.A > 0, "the tile is not blank");
    }

    /// <summary>
    /// The Explorer tile of a HEIC goes through the plug-in's DecodePreviewSurfaceAsync (ThumbnailExtendedDecode.DecodeProvider), so it gets the
    /// native thumbnail-item shortcut: heic-thumb.heic holds a 1600 x 1200 picture and a 320 x 240 thumbnail item with the colours INVERTED,
    /// so a tile the item covers shows the inverted picture and a tile wider than the item shows the main picture.
    /// </summary>
    [Fact]
    public void A_heic_tile_is_served_from_the_thumbnail_item_through_the_provider_preview_call()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codecs", "heic-thumb.heic");
        if (!File.Exists(source) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "codecs", "glidecodecs.dll"))) return; // no plug-in in this build output
        var path = Write("thumbitem", ".heic", File.ReadAllBytes(source));
        Assert.Equal(ThumbnailRoute.Provider, ThumbnailRoutes.RouteFor(path));

        static (int B, int G, int R) Centre(Bitmap b)
        {
            var (blue, green, red, _) = Pixel(b, b.PixelSize.Width / 2, b.PixelSize.Height / 2);
            return (blue, green, red);
        }

        using var main = ThumbnailExtendedDecode.DecodeProvider(path, 480); // wider than the 320 px item: the main picture
        using var tile = ThumbnailExtendedDecode.DecodeProvider(path, 236);  // covered by the item
        Assert.NotNull(main);
        Assert.NotNull(tile);
        var (mb, mg, mr) = Centre(main!);
        var (tb, tg, tr) = Centre(tile!);
        Assert.True(Math.Abs(mb + tb - 255) < 24 && Math.Abs(mg + tg - 255) < 24 && Math.Abs(mr + tr - 255) < 24,
            $"expected the inverted thumbnail item for a 236 px tile, got {tb},{tg},{tr} against the picture's {mb},{mg},{mr}");
    }

    [Fact]
    public void Suffixes_the_viewer_cannot_decode_keep_the_glyph_and_the_old_eleven_still_have_thumbnails()
    {
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jxr", ".wdp", ".hdp" })
            Assert.True(ThumbnailRoutes.CanThumbnail("x" + extension), extension);
        foreach (var extension in new[] { ".txt", ".pdf", ".mp4", ".zip", ".dwg", ".skp", ".eps", "" })
            Assert.False(ThumbnailRoutes.CanThumbnail("x" + extension), extension);
        Assert.Null(ThumbnailDecode.DecodeUncached(_dir.Combine("notes.txt"), TileWidth));
    }

    [Fact]
    public void Every_extension_the_viewer_decodes_without_a_download_has_a_thumbnail_route()
    {
        var missing = CodecCapabilityRegistry.All
            .Where(c => c.Tier is CodecTier.NativeOs or CodecTier.BuiltIn)
            .Select(c => c.Extension)
            .Concat(Glide.App.Platform.BundledCodecLibraries.BundledExtensions)
            .Concat(Glide.App.Platform.BundledCodecLibraries.ManagedExtensions)
            .Concat([".svg", ".svgz", ".mpo", ".jfif", ".jif"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(extension => !ThumbnailRoutes.CanThumbnail("x" + extension))
            .ToArray();
        Assert.True(missing.Length == 0, "no thumbnail route for: " + string.Join(" ", missing));
    }

    [Theory]
    [InlineData(".jpg")] [InlineData(".jfif")] [InlineData(".mpo")] [InlineData(".png")] [InlineData(".ico")] [InlineData(".webp")] [InlineData(".bmp")]
    [InlineData(".svg")] [InlineData(".psd")] [InlineData(".tga")] [InlineData(".ppm")] [InlineData(".hdr")] [InlineData(".dds")] [InlineData(".dng")] [InlineData(".nef")]
    [InlineData(".exr")] [InlineData(".heic")] [InlineData(".avif")] [InlineData(".jxl")] [InlineData(".jp2")] [InlineData(".qoi")] [InlineData(".pcx")] [InlineData(".tif")]
    public void A_file_that_is_not_what_its_suffix_says_falls_back_to_no_picture_without_throwing(string extension)
    {
        var path = Write("broken", extension, [.. "this is not a picture, not even close"u8, .. new byte[64]]);
        Bitmap? bitmap = null;
        try
        {
            var exception = Record.Exception(() => bitmap = ThumbnailDecode.DecodeToWidth(path, TileWidth));
            // The Skia family throws for undecodable data (the scheduler turns that into "no picture"); everything else returns null.
            if (exception is null) Assert.Null(bitmap);
        }
        finally { bitmap?.Dispose(); }
    }

    [Theory]
    [InlineData("a", ".psd")] [InlineData("b", ".tga")] [InlineData("c", ".ppm")] [InlineData("d", ".svg")] [InlineData("e", ".dng")]
    [InlineData("f", ".png")] [InlineData("g", ".jpg")] [InlineData("h", ".dds")] [InlineData("i", ".hdr")] [InlineData("j", ".ico")]
    public void Every_result_goes_through_the_disk_cache_and_the_second_request_is_a_hit(string name, string extension)
    {
        var bytes = Formats().Select(f => (object[])f).First(f => (string)f[1] == extension)[3] as byte[];
        var path = Write(name, extension, bytes!);
        ThumbnailDiskCache.Configure(true, _dir.Combine("cache"), 64L * 1024 * 1024);
        var cache = ThumbnailDiskCache.Current!;

        using (var first = ThumbnailDecode.DecodeToWidth(path, TileWidth))
        {
            Assert.NotNull(first);
            AssertLeftRedRightBlue(first!, extension + " (decoded)");
        }
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));
        var decodedAfterFirst = _decoded.Count;
        Assert.True(decodedAfterFirst >= 1);

        using var second = ThumbnailDecode.DecodeToWidth(path, TileWidth);
        Assert.NotNull(second);
        Assert.Equal(decodedAfterFirst, _decoded.Count); // served from the cache: no decode started
        AssertLeftRedRightBlue(second!, extension + " (cached)");
    }

    [Fact]
    public void A_straight_alpha_decoder_result_is_stored_premultiplied()
    {
        var pixels = new byte[] { 200, 100, 50, 128, 10, 20, 30, 255, 255, 255, 255, 0 };
        ThumbnailDecode.PremultiplyInPlace(pixels, pixels.Length);
        Assert.Equal(new byte[] { 100, 50, 25, 128, 10, 20, 30, 255, 0, 0, 0, 0 }, pixels);
    }

    [Fact]
    public void Portrait_and_landscape_sources_ask_the_decoder_for_the_tile_width()
    {
        Assert.Equal(236, ThumbnailExtendedDecode.LongestSideForWidth(4000, 3000, 236));
        Assert.Equal(354, ThumbnailExtendedDecode.LongestSideForWidth(3000, 4500, 236)); // 2:3 portrait: the long side is 1.5x the width
        Assert.Equal(236, ThumbnailExtendedDecode.LongestSideForWidth(0, 0, 236));      // unknown size
        Assert.Equal(100, ThumbnailExtendedDecode.LongestSideForWidth(100, 80, 236));    // never above the source
    }
}
