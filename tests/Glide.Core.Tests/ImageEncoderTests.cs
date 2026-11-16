using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.App.Services;
using Glide.Imaging;
using Glide.Imaging.Encoders;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.4 Save / Save as formats: every writer round-trips through Glide's own decoders (built-in managed
/// decoders, WIC, Skia, the bundled codec provider), sizes/alpha/rotation survive, size limits refuse clearly,
/// metadata is carried over with Orientation reset, and the Save as list/name logic is consistent.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ImageEncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlideEncode_" + Guid.NewGuid().ToString("N"));

    public ImageEncoderTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        // Saving JPEG 2000/WebP resolves the bundled provider, which registers its decode suffixes process-wide.
        CodecCapabilityRegistry.ClearExternalProviders();
    }

    // -- Fixtures ------------------------------------------------------------------------------------

    /// <summary>Deterministic straight-alpha pattern; with <paramref name="alpha"/> it mixes 0/64/128/200/255 alpha.</summary>
    private static StraightImage Pattern(int w, int h, bool alpha)
    {
        var px = new byte[w * h * 4];
        byte[] alphas = [255, 0, 128, 255, 200, 64, 255];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                px[o] = (byte)(x * 7 + y * 3);
                px[o + 1] = (byte)(x * 3 + y * 11);
                px[o + 2] = (byte)((x * y) & 0xFF);
                if (x < w / 3) { px[o] = 40; px[o + 1] = 90; px[o + 2] = 200; } // flat area: exercises RLE runs
                px[o + 3] = alpha ? alphas[(x / 3 + y) % alphas.Length] : (byte)255;
            }
        return new StraightImage(w, h, px);
    }

    /// <summary>Smooth gradients: photo-like content for the lossy encoders' PSNR checks.</summary>
    private static StraightImage Smooth(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                px[o] = (byte)(40 + x * 180 / w); px[o + 1] = (byte)(60 + y * 150 / h); px[o + 2] = (byte)(200 - (x + y) * 100 / (w + h)); px[o + 3] = 255;
            }
        return new StraightImage(w, h, px);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertSamePixels(StraightImage expected, ManagedDecoderTestSupport.Pixels actual, int tolerance = 0, int minAlpha = 1, string label = "")
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (var i = 0; i < expected.Width * expected.Height; i++)
        {
            var o = i * 4;
            var a = expected.Bgra[o + 3];
            Assert.True(Math.Abs(a - actual.Bgra[o + 3]) <= tolerance, $"{label} alpha at {i}: {a} vs {actual.Bgra[o + 3]}");
            if (a < minAlpha) continue;
            for (var c = 0; c < 3; c++)
                Assert.True(Math.Abs(expected.Bgra[o + c] - actual.Bgra[o + c]) <= tolerance,
                    $"{label} channel {c} at pixel {i} (alpha {a}): expected {expected.Bgra[o + c]}, got {actual.Bgra[o + c]}");
        }
    }

    private static double Psnr(byte[] expected, byte[] actual)
    {
        double sum = 0; long n = 0;
        for (var i = 0; i < expected.Length; i += 4)
            for (var c = 0; c < 3; c++) { var d = expected[i + c] - actual[i + c]; sum += d * d; n++; }
        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (sum / n));
    }

    /// <summary>Decodes through the viewer's full decode chain (built-ins, WIC, Skia, codec provider) to straight BGRA.</summary>
    private static ManagedDecoderTestSupport.Pixels DecodeWithGlide(string path)
    {
        ManagedDecoderTestSupport.Pixels result = default;
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            using var backend = new CodecProviderDecoderBackend(lazyResolver: p => CodecProviderRuntime.Shared.ResolveProvider(p));
            using var stream = File.OpenRead(path);
            using var bitmap = backend.DecodeFullAsync(stream, path).GetAwaiter().GetResult();
            var (w, h) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            using var straight = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Unpremul);
            var bytes = new byte[w * h * 4];
            using (var frame = straight.Lock())
            {
                bitmap.CopyPixels(frame, AlphaFormat.Unpremul);
                for (var y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes), bytes, y * w * 4, w * 4);
            }
            result = new ManagedDecoderTestSupport.Pixels(w, h, bytes);
        });
        return result;
    }

    // -- Managed writers: lossless round trips -------------------------------------------------------

    [Theory]
    [InlineData("qoi", ".qoi")]
    [InlineData("tga", ".tga")]
    [InlineData("pam", ".pam")]
    [InlineData("sgi", ".sgi")]
    [InlineData("psd", ".psd")]
    [InlineData("psb", ".psb")]
    [InlineData("ktx", ".ktx")]
    public void Lossless_alpha_writers_round_trip_exactly(string id, string extension)
    {
        var format = SaveFormatCatalog.FromId(id)!;
        foreach (var alpha in new[] { true, false })
        {
            var image = Pattern(37, 23, alpha);
            var path = Write($"{id}-{alpha}{extension}", ImageFileEncoder.EncodeManaged(format, image));
            AssertSamePixels(image, ManagedDecoderTestSupport.Decode(path), label: $"{id} alpha={alpha}");
        }
    }

    [Fact]
    public void OpenRaster_is_a_valid_ora_with_stored_mimetype_first_and_round_trips()
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = Pattern(300, 40, alpha: true);
        var bytes = ImageFileEncoder.EncodeManaged(SaveFormatCatalog.OpenRaster, image);
        // Local file header of the first entry: "mimetype", stored (method 0), content image/openraster.
        Assert.Equal("PK\u0003\u0004", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal("mimetype", Encoding.ASCII.GetString(bytes, 30, 8));
        Assert.Equal("image/openraster", Encoding.ASCII.GetString(bytes, 30 + 8 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)), 16));
        var path = Write("doc.ora", bytes);
        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("stack.xml"));
            Assert.NotNull(zip.GetEntry("data/layer0.png"));
            Assert.NotNull(zip.GetEntry("mergedimage.png"));
            Assert.NotNull(zip.GetEntry("Thumbnails/thumbnail.png"));
            var stack = new StreamReader(zip.GetEntry("stack.xml")!.Open()).ReadToEnd();
            Assert.Contains("w=\"300\" h=\"40\"", stack);
        }
        // PNG stores straight alpha; Glide's PNG decode path may round low-alpha colours by a step.
        AssertSamePixels(image, ManagedDecoderTestSupport.Decode(path), tolerance: 2, minAlpha: 64, label: "ora");
    }

    [Fact]
    public void Opaque_and_bilevel_writers_round_trip()
    {
        var image = Pattern(29, 17, alpha: false);
        AssertSamePixels(image, ManagedDecoderTestSupport.Decode(Write("a.ppm", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Ppm, image))), label: "ppm");

        var grey = ManagedDecoderTestSupport.Decode(Write("a.pgm", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Pgm, image)));
        for (var i = 0; i < image.Width * image.Height; i++) Assert.Equal(image.Luma(i), grey.Bgra[i * 4]);

        foreach (var (format, ext) in new[] { (SaveFormatCatalog.Pbm, ".pbm"), (SaveFormatCatalog.Wbmp, ".wbmp"), (SaveFormatCatalog.Xbm, ".xbm") })
        {
            var decoded = ManagedDecoderTestSupport.Decode(Write("bw" + ext, ImageFileEncoder.EncodeManaged(format, image, new ImageEncodeOptions { Name = "my image-1" })));
            Assert.Equal((image.Width, image.Height), (decoded.Width, decoded.Height));
            for (var i = 0; i < image.Width * image.Height; i++)
                Assert.Equal(image.Luma(i) >= 128 ? 255 : 0, decoded.Bgra[i * 4]);
        }
        Assert.Contains("#define my_image_1_width 29", File.ReadAllText(Path.Combine(_dir, "bw.xbm")));
    }

    [Fact]
    public void Float_and_hdr_writers_round_trip_within_one_step()
    {
        var image = Pattern(33, 19, alpha: false);
        AssertSamePixels(image, ManagedDecoderTestSupport.Decode(Write("a.pfm", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Pfm, image))), tolerance: 1, label: "pfm");
        AssertSamePixels(image, ManagedDecoderTestSupport.Decode(Write("a.hdr", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Hdr, image))), tolerance: 1, label: "hdr");
        AssertSamePixels(image, ManagedDecoderTestSupport.Decode(Write("a.exr", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Exr, image))), tolerance: 1, label: "exr");
        // Narrow images take the flat (non-RLE) Radiance path.
        var narrow = Pattern(5, 4, alpha: false);
        AssertSamePixels(narrow, ManagedDecoderTestSupport.Decode(Write("n.hdr", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Hdr, narrow))), tolerance: 1, label: "hdr flat");
    }

    [Fact]
    public void Exr_keeps_alpha_and_uses_zip_compression()
    {
        var image = Pattern(40, 35, alpha: true);
        var bytes = ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Exr, image);
        Assert.Equal(20000630, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        var compression = Encoding.ASCII.GetBytes("compression\0compression\0");
        var at = bytes.AsSpan().IndexOf(compression);
        Assert.True(at > 0);
        Assert.Equal(3, bytes[at + compression.Length + 4]); // ZIP_COMPRESSION
        // Half precision + premultiplied storage: low alpha loses colour precision, so compare solid-ish pixels.
        AssertSamePixels(image, ManagedDecoderTestSupport.Decode(Write("alpha.exr", bytes)), tolerance: 2, minAlpha: 128, label: "exr alpha");
    }

    [Fact]
    public void Xpm_round_trips_with_transparency_and_refuses_too_many_colours()
    {
        var image = Pattern(40, 30, alpha: true);
        // Few colours: keep it a palette image (alpha 0/64 -> None, others opaque over white).
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            var o = i * 4;
            image.Bgra[o] = (byte)((i % 5) * 50); image.Bgra[o + 1] = 10; image.Bgra[o + 2] = (byte)((i % 3) * 100);
            image.Bgra[o + 3] = image.Bgra[o + 3] < 128 ? (byte)0 : (byte)255;
        }
        var decoded = ManagedDecoderTestSupport.Decode(Write("a.xpm", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Xpm, image, new ImageEncodeOptions { Name = "a" })));
        AssertSamePixels(image, decoded, label: "xpm");

        // Two characters per pixel above 92 colours.
        var many = Pattern(20, 10, alpha: false);
        for (var i = 0; i < 200; i++) { many.Bgra[i * 4] = (byte)i; many.Bgra[i * 4 + 1] = 0; many.Bgra[i * 4 + 2] = 0; }
        AssertSamePixels(many, ManagedDecoderTestSupport.Decode(Write("many.xpm", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Xpm, many))), label: "xpm 2cpp");

        var noisy = Pattern(100, 100, alpha: false);
        for (var i = 0; i < 100 * 100; i++) { noisy.Bgra[i * 4] = (byte)i; noisy.Bgra[i * 4 + 1] = (byte)(i >> 8); }
        var refused = Assert.Throws<ImageSaveRefusedException>(() => ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Xpm, noisy));
        Assert.Contains("at most 4,096 colours", refused.Message);
    }

    [Fact]
    public void Icon_is_png_compressed_and_scales_large_images_to_256()
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = Pattern(512, 300, alpha: true);
        var bytes = ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Ico, image);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0 }, bytes.Take(6));
        Assert.Equal(0, bytes[6]);           // width 256 is stored as 0
        Assert.Equal(150, bytes[7]);         // 300 * 256 / 512
        Assert.Equal(0x89, bytes[22]);       // PNG payload
        var decoded = DecodeWithGlide(Write("a.ico", bytes));
        Assert.Equal((256, 150), (decoded.Width, decoded.Height));

        var small = Pattern(48, 32, alpha: true);
        var smallDecoded = DecodeWithGlide(Write("small.ico", ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Ico, small)));
        AssertSamePixels(small, smallDecoded, tolerance: 2, minAlpha: 64, label: "ico");
    }

    [Fact]
    public void Dds_uses_the_legacy_b8g8r8a8_header_and_decodes_through_wic()
    {
        var image = Pattern(64, 32, alpha: true);
        var bytes = ImageFileEncoder.EncodeManaged(SaveFormatCatalog.Dds, image);
        Assert.Equal("DDS ", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84))); // no FourCC: no DX10 header
        Assert.Equal(128 + 64 * 32 * 4, bytes.Length);
        if (!OperatingSystem.IsWindows()) return;
        AssertSamePixels(image, DecodeWithGlide(Write("a.dds", bytes)), tolerance: 2, minAlpha: 64, label: "dds");
    }

    [Fact]
    public void PackBits_matches_the_reference_layout()
    {
        Span<byte> output = stackalloc byte[64];
        var n = DocumentEncoders.PackBits([1, 1, 1, 1, 2, 3, 4, 4], output);
        Assert.Equal(new byte[] { 253, 1, 3, 2, 3, 4, 4 }, output[..n].ToArray());
    }

    // -- Size limits ---------------------------------------------------------------------------------

    [Fact]
    public void Size_limits_refuse_with_clear_messages()
    {
        var psd = SaveFormatCatalog.Psd.ValidateSize(30001, 10);
        Assert.NotNull(psd);
        Assert.Contains("30,000 × 30,000", psd);
        Assert.Contains("PSB", psd);
        Assert.Null(SaveFormatCatalog.Psb.ValidateSize(30001, 10));
        Assert.NotNull(SaveFormatCatalog.Psb.ValidateSize(300001, 10));
        Assert.Contains("16,383", SaveFormatCatalog.WebP.ValidateSize(16384, 2));
        Assert.Contains("Direct3D", SaveFormatCatalog.Dds.ValidateSize(16385, 2));
        Assert.Contains("400,000,000 pixels", SaveFormatCatalog.Qoi.ValidateSize(20001, 20000));
        Assert.Null(SaveFormatCatalog.Ico.ValidateSize(4000, 3000)); // scaled down, not refused
        Assert.Contains("65,535", SaveFormatCatalog.Jpeg.ValidateSize(70000, 10));

        var path = Path.Combine(_dir, "big.psd");
        Assert.Throws<ImageSaveRefusedException>(() => ImageFileEncoder.Encode(SaveFormatCatalog.Dds, new byte[16385 * 1 * 4], 16385, 1, path));
        Assert.False(File.Exists(path));
    }

    // -- Full save pipeline (premultiplied input, rotation, alpha flattening, atomic replace) -----------

    private static byte[] PremultipliedPattern(int w, int h, bool alpha) => EncoderPixels.Premultiply(Pattern(w, h, alpha).Bgra);

    [Theory]
    [InlineData(".png", 0)]
    [InlineData(".tif", 0)]
    [InlineData(".bmp", 0)]
    [InlineData(".jpg", 32)]
    [InlineData(".jxr", 32)]
    [InlineData(".gif", 22)]
    [InlineData(".qoi", 0)]
    [InlineData(".tga", 0)]
    [InlineData(".psd", 0)]
    [InlineData(".jp2", 0)]
    [InlineData(".j2k", 0)]
    [InlineData(".webp", 30)]
    public void Save_writes_rotated_pixels_that_decode_back(string extension, int minPsnr)
    {
        if (!OperatingSystem.IsWindows()) return;
        var format = SaveFormatCatalog.FromExtension(extension)!;
        Assert.True(SaveFormatAvailability.IsWritable(format), $"{format.Name} should be writable on Windows with the bundled codecs");
        var source = minPsnr == 0 ? Pattern(31, 18, alpha: false) : Smooth(31, 18);
        var dest = Path.Combine(_dir, "rotated" + extension);
        ImageSaveService.EncodeAndReplace(EncoderPixels.Premultiply(source.Bgra), 31, 18, new ImageEditTransform(1, false, false), dest, 0.92f);
        var expected = new StraightImage(18, 31, new ImageEditTransform(1, false, false).Apply(source.Bgra, 31, 18, out _, out _));
        var decoded = DecodeWithGlide(dest);
        Assert.Equal((18, 31), (decoded.Width, decoded.Height));
        if (minPsnr == 0) AssertSamePixels(expected, decoded, label: extension);
        else Assert.True(Psnr(expected.Bgra, decoded.Bgra) >= minPsnr, $"{extension} PSNR {Psnr(expected.Bgra, decoded.Bgra):F1} dB");
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".tif")]
    [InlineData(".webp")]
    [InlineData(".jp2")]
    [InlineData(".jxr")]
    [InlineData(".qoi")]
    [InlineData(".pam")]
    [InlineData(".sgi")]
    public void Alpha_formats_keep_transparency(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        var dest = Path.Combine(_dir, "alpha" + extension);
        ImageSaveService.EncodeAndReplace(PremultipliedPattern(24, 14, alpha: true), 24, 14, default, dest, 0.92f);
        var decoded = DecodeWithGlide(dest);
        var source = Pattern(24, 14, alpha: true);
        // Lossy WebP / JPEG XR alpha is quantised a little; lossless formats keep it exactly.
        var tolerance = extension is ".webp" or ".jxr" ? 12 : 0;
        for (var i = 0; i < 24 * 14; i++)
            Assert.True(Math.Abs(source.Bgra[i * 4 + 3] - decoded.Bgra[i * 4 + 3]) <= tolerance, $"{extension} alpha at {i}");
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    [InlineData(".ppm")]
    [InlineData(".hdr")]
    public void Formats_without_alpha_composite_transparency_over_white(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        var px = new byte[8 * 8 * 4]; // fully transparent
        for (var i = 0; i < 16; i++) { px[i * 4 + 2] = 128; px[i * 4 + 3] = 128; } // first two rows: 50 % red (premultiplied)
        var dest = Path.Combine(_dir, "flat" + extension);
        ImageSaveService.EncodeAndReplace(px, 8, 8, default, dest, 0.95f);
        var decoded = DecodeWithGlide(dest);
        var tolerance = extension == ".jpg" ? 10 : 2;
        ManagedDecoderTestSupport.AssertColor(decoded.At(4, 6), 255, 255, 255, tolerance);       // transparent -> white
        ManagedDecoderTestSupport.AssertColor(decoded.At(4, 0), 255, 127, 127, tolerance + 2);   // 50 % red over white
    }

    [Fact]
    public void Gif_is_single_frame_with_a_transparent_palette_entry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dest = Path.Combine(_dir, "t.gif");
        ImageSaveService.EncodeAndReplace(PremultipliedPattern(20, 12, alpha: true), 20, 12, default, dest, 0.92f);
        Assert.Equal("GIF89a", Encoding.ASCII.GetString(File.ReadAllBytes(dest), 0, 6));
        Assert.Equal(1, ImageFrameProbe.CountFrames(dest));
        var decoded = DecodeWithGlide(dest);
        var source = Pattern(20, 12, alpha: true);
        for (var i = 0; i < 20 * 12; i++)
        {
            var a = source.Bgra[i * 4 + 3];
            if (a == 0) Assert.Equal(0, decoded.Bgra[i * 4 + 3]);
            if (a == 255) Assert.Equal(255, decoded.Bgra[i * 4 + 3]);
        }
    }

    [Fact]
    public void Codec_provider_encodes_lossy_jpeg2000_and_lossless_webp()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Lossy (quality -> compression ratio) on photo-like content; lossless is bit-exact even on noise.
        var smooth = Smooth(96, 64);
        var lossy = Path.Combine(_dir, "lossy.jp2");
        var smoothLossless = Path.Combine(_dir, "smooth-lossless.j2k");
        Assert.True(CodecProviderEncoder.TryEncode("jp2", smooth, lossy, 0.5f), "lossy JPEG 2000 encode");
        Assert.True(CodecProviderEncoder.TryEncode("j2k", smooth, smoothLossless, 1f), "lossless JPEG 2000 codestream encode");
        Assert.True(new FileInfo(lossy).Length < new FileInfo(smoothLossless).Length, $"lossy {new FileInfo(lossy).Length} B vs lossless {new FileInfo(smoothLossless).Length} B");
        var lossyPsnr = Psnr(smooth.Bgra, DecodeWithGlide(lossy).Bgra);
        Assert.True(lossyPsnr > 30, $"lossy JPEG 2000 PSNR {lossyPsnr:F1} dB");
        Assert.Equal(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, File.ReadAllBytes(smoothLossless).Take(4)); // SOC + SIZ: a raw codestream

        var image = Pattern(64, 48, alpha: false);
        var lossless = Path.Combine(_dir, "lossless.jp2");
        Assert.True(CodecProviderEncoder.TryEncode("jp2", image, lossless, 1f), "lossless JPEG 2000 encode");
        AssertSamePixels(image, DecodeWithGlide(lossless), label: "jp2 lossless");

        var withAlpha = Pattern(40, 30, alpha: true);
        var webp = Path.Combine(_dir, "lossless.webp");
        ImageFileEncoder.Encode(SaveFormatCatalog.WebP, EncoderPixels.Premultiply(withAlpha.Bgra), 40, 30, webp, new ImageEncodeOptions { Lossless = true });
        Assert.Equal("VP8L", Encoding.ASCII.GetString(File.ReadAllBytes(webp), 12, 4));
        Assert.True(WebPContainer.IsLossless(webp), "VP8L detection");
        // Alpha is exact; colour under partial alpha moves a step per premultiplied hand-off (save input, Skia decode).
        AssertSamePixels(withAlpha, DecodeWithGlide(webp), tolerance: 2, minAlpha: 200, label: "webp lossless");

        Assert.False(CodecProviderEncoder.IsAvailable("tiff"));
    }

    [Fact]
    public void Multi_frame_in_place_refusal_still_applies_to_new_formats()
    {
        var path = Path.Combine(_dir, "anim.gif");
        // Two-frame GIF: header, logical screen, two image descriptors.
        var gif = new List<byte>(Encoding.ASCII.GetBytes("GIF89a"));
        gif.AddRange([1, 0, 1, 0, 0, 0, 0]);
        for (var f = 0; f < 2; f++) gif.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0x80, 0, 0, 0, 255, 255, 255, 2, 2, 0x44, 1, 0]);
        gif.Add(0x3B);
        File.WriteAllBytes(path, gif.ToArray());
        if (ImageFrameProbe.CountFrames(path) < 2) return; // probe unavailable on this platform
        Assert.Throws<MultiFrameSaveRefusedException>(() =>
            ImageSaveService.EncodeAndReplace(new byte[4], 1, 1, default, path, 0.9f, sourcePath: path));
    }

    // -- Metadata --------------------------------------------------------------------------------------

    /// <summary>EXIF TIFF block: Make "GlideCam", Orientation 6, an Exif IFD with PixelX/YDimension, and an IFD1 thumbnail link.</summary>
    private static byte[] Exif(int pixelX, int pixelY)
    {
        var w = new ManagedDecoderTestSupport.Writer();
        w.Ascii("II").U16(42).U32(8);
        // IFD0 at 8: 3 entries -> 2 + 36 + 4 = 42 bytes, ends at 50. Make string at 50 (9 bytes, padded 10). Exif IFD at 60.
        w.U16(3);
        w.U16(271).U16(2).U32(9).U32(50);
        w.U16(274).U16(3).U32(1).U16(6).U16(0);
        w.U16(34665).U16(4).U32(1).U32(60);
        w.U32(90); // next IFD (IFD1, the thumbnail)
        w.Ascii("GlideCam\0").U8(0);
        // Exif IFD at 60: 2 entries -> 2 + 24 + 4 = 30, ends at 90.
        w.U16(2);
        w.U16(40962).U16(4).U32(1).U32((uint)pixelX);
        w.U16(40963).U16(4).U32(1).U32((uint)pixelY);
        w.U32(0);
        // IFD1 at 90: one entry (Compression = 6, JPEG thumbnail).
        w.U16(1).U16(259).U16(3).U32(1).U16(6).U16(0).U32(0);
        return w.ToArray();
    }

    private static readonly string? SrgbProfile = new[]
    {
        @"C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm",
    }.FirstOrDefault(File.Exists);

    /// <summary>A JPEG (31x18, Orientation 6) with EXIF, XMP and (when available) an ICC profile.</summary>
    private string SourceJpeg()
    {
        var jpeg = ManagedDecoderTestSupport.Jpeg(31, 18, SkiaSharp.SKColors.Red, SkiaSharp.SKColors.Blue);
        var segments = new List<byte>();
        void Segment(byte marker, byte[] payload)
        {
            segments.AddRange([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            segments.AddRange(payload);
        }
        Segment(0xE1, Encoding.ASCII.GetBytes("Exif\0\0").Concat(Exif(31, 18)).ToArray());
        Segment(0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description rdf:about='' xmlns:tiff='http://ns.adobe.com/tiff/1.0/' xmlns:dc='http://purl.org/dc/elements/1.1/' tiff:Orientation='6'>" +
            "<dc:creator><rdf:Seq><rdf:li>Glide Tester</rdf:li></rdf:Seq></dc:creator></rdf:Description></rdf:RDF></x:xmpmeta>")).ToArray());
        if (SrgbProfile is not null)
            Segment(0xE2, Encoding.ASCII.GetBytes("ICC_PROFILE\0").Concat(new byte[] { 1, 1 }).Concat(File.ReadAllBytes(SrgbProfile)).ToArray());
        var bytes = jpeg.Take(2).Concat(segments).Concat(jpeg.Skip(2)).ToArray();
        return Write("source.jpg", bytes);
    }

    private static (int Orientation, string? Make, long PixelX, long PixelY, long NextIfd) ReadExif(byte[] tiff)
    {
        var little = tiff[0] == 'I';
        int U16(int o) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(o));
        long U32(int o) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(o));
        int orientation = 0; string? make = null; long px = 0, py = 0;
        var ifd0 = (int)U32(4);
        var count = U16(ifd0);
        for (var i = 0; i < count; i++)
        {
            var e = ifd0 + 2 + i * 12;
            var tag = U16(e);
            var n = (int)U32(e + 4);
            if (tag == 274) orientation = U16(e + 8);
            if (tag == 271) make = Encoding.ASCII.GetString(tiff, n <= 4 ? e + 8 : (int)U32(e + 8), n).TrimEnd('\0');
            if (tag == 34665)
            {
                var sub = (int)U32(e + 8);
                for (var j = 0; j < U16(sub); j++)
                {
                    var s = sub + 2 + j * 12;
                    var value = U16(s + 2) == 3 ? U16(s + 8) : U32(s + 8);
                    if (U16(s) == 40962) px = value;
                    if (U16(s) == 40963) py = value;
                }
            }
        }
        return (orientation, make, px, py, U32(ifd0 + 2 + count * 12));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    public void Metadata_is_copied_with_orientation_reset_and_dimensions_updated(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = SourceJpeg();
        var dest = Path.Combine(_dir, "meta" + extension);
        ImageSaveService.EncodeAndReplace(PremultipliedPattern(31, 18, alpha: false), 31, 18, new ImageEditTransform(1, false, false), dest, 0.92f, sourcePath: source);
        var blobs = MetadataBlobs.Read(dest);
        Assert.NotNull(blobs.Exif);
        var exif = ReadExif(blobs.Exif!);
        Assert.Equal(1, exif.Orientation);
        Assert.Equal("GlideCam", exif.Make);
        Assert.Equal((18L, 31L), (exif.PixelX, exif.PixelY));
        Assert.Equal(0, exif.NextIfd); // the old (unrotated) thumbnail is not carried over
        Assert.NotNull(blobs.Xmp);
        var xmp = Encoding.UTF8.GetString(blobs.Xmp!);
        Assert.Contains("Glide Tester", xmp);
        Assert.DoesNotContain("Orientation='6'", xmp);
        Assert.DoesNotContain("Orientation=\"6\"", xmp);
        if (SrgbProfile is not null)
        {
            var icc = extension == ".png" ? ReadPngIcc(dest) : blobs.Icc;
            Assert.Equal(File.ReadAllBytes(SrgbProfile), icc);
        }
    }

    private static byte[]? ReadPngIcc(string path) => MetadataBlobs.Read(path).Icc;

    [Fact]
    public void Tiff_save_resets_orientation_from_a_rotated_jpeg_source()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = SourceJpeg();
        var dest = Path.Combine(_dir, "meta.tif");
        ImageSaveService.EncodeAndReplace(PremultipliedPattern(31, 18, alpha: false), 31, 18, new ImageEditTransform(1, false, false), dest, 0.92f, sourcePath: source);
        var exif = ReadExif(File.ReadAllBytes(dest));
        Assert.Equal(1, exif.Orientation);
        Assert.Equal("GlideCam", exif.Make);
    }

    [Fact]
    public void Exif_normalisation_patches_a_copy_and_rejects_non_tiff_blocks()
    {
        var little = Exif(31, 18);
        var patched = MetadataBlobs.NormalizeExif(little, 18, 31)!;
        var read = ReadExif(patched);
        Assert.Equal((1, 18L, 31L, 0L), (read.Orientation, read.PixelX, read.PixelY, read.NextIfd));
        Assert.Equal(6, ReadExif(little).Orientation); // the source block is not modified
        Assert.Null(MetadataBlobs.NormalizeExif([1, 2, 3, 4, 5, 6, 7, 8], 1, 1));
    }

    [Fact]
    public void Selection_export_writes_any_format_and_copies_only_the_colour_profile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = SourceJpeg();
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            using var bitmap = new WriteableBitmap(new PixelSize(31, 18), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = bitmap.Lock())
            {
                var px = PremultipliedPattern(31, 18, alpha: true);
                for (var y = 0; y < 18; y++) Marshal.Copy(px, y * 31 * 4, IntPtr.Add(frame.Address, y * frame.RowBytes), 31 * 4);
            }
            var dest = Path.Combine(_dir, "crop.webp");
            ImageSelectionExportService.ExportAsync(bitmap, new PixelRect(2, 3, 10, 7), dest, sourcePath: source).GetAwaiter().GetResult();
            var blobs = MetadataBlobs.Read(dest);
            Assert.Null(blobs.Exif);
            Assert.Null(blobs.Xmp);
            if (SrgbProfile is not null) Assert.NotNull(blobs.Icc);
            var qoi = Path.Combine(_dir, "crop.qoi");
            ImageSelectionExportService.ExportAsync(bitmap, new PixelRect(2, 3, 10, 7), qoi).GetAwaiter().GetResult();
            Assert.Equal((10, 7), (ManagedDecoderTestSupport.Decode(qoi).Width, ManagedDecoderTestSupport.Decode(qoi).Height));
        });
    }

    // -- Availability and the Save as dialog policy ----------------------------------------------------

    [Fact]
    public void Tier_two_formats_are_offered_only_when_the_probe_finds_an_encoder()
    {
        bool Without(SaveFormat f) => !f.NeedsWindowsExtension;
        var plain = SaveAsDialogPolicy.Choices(Without);
        Assert.DoesNotContain(SaveFormatCatalog.Heic, plain);
        Assert.DoesNotContain(SaveFormatCatalog.Avif, plain);
        Assert.DoesNotContain(SaveFormatCatalog.JpegXl, plain);

        var withHeic = SaveAsDialogPolicy.Choices(f => Without(f) || f == SaveFormatCatalog.Heic);
        Assert.Contains(SaveFormatCatalog.Heic, withHeic);
        Assert.DoesNotContain(SaveFormatCatalog.Avif, withHeic);
        // HEIC sits with the common formats, right after WebP.
        Assert.Equal(SaveFormatCatalog.WebP, withHeic[IndexOf(withHeic, SaveFormatCatalog.Heic) - 1]);

        // The real probe: tier 1 is always there on Windows; tier 2 matches what WIC reports, lazily.
        if (!OperatingSystem.IsWindows()) return;
        var writable = SaveFormatAvailability.Writable();
        foreach (var f in new[] { SaveFormatCatalog.Jpeg, SaveFormatCatalog.Png, SaveFormatCatalog.Bmp, SaveFormatCatalog.Tiff, SaveFormatCatalog.Gif, SaveFormatCatalog.JpegXr, SaveFormatCatalog.Qoi })
            Assert.Contains(f, writable);
        foreach (var f in new[] { SaveFormatCatalog.Heic, SaveFormatCatalog.Avif, SaveFormatCatalog.JpegXl })
            Assert.Equal(NativeImageEncoder.IsFormatAvailable(f.NativeId), writable.Contains(f));
    }

    private static int IndexOf(IReadOnlyList<SaveFormat> list, SaveFormat format)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == format) return i;
        return -1;
    }

    [Fact]
    public void Save_as_list_starts_with_common_formats_and_labels_are_unique()
    {
        var all = SaveAsDialogPolicy.Choices(_ => true);
        Assert.Equal(new[] { SaveFormatCatalog.Jpeg, SaveFormatCatalog.Png, SaveFormatCatalog.WebP }, all.Take(3));
        Assert.Equal(all.Count, all.Select(f => f.DialogLabel).Distinct().Count());
        Assert.Equal("JPEG (*.jpg; *.jpeg; *.jpe; *.jfif)", SaveFormatCatalog.Jpeg.DialogLabel);
        foreach (var f in all) Assert.Same(f, SaveAsDialogPolicy.FromDialogLabel(f.DialogLabel, all));
        // Every extension maps back to exactly one writer.
        foreach (var f in all)
            foreach (var e in f.Extensions)
                Assert.Same(f, SaveFormatCatalog.FromExtension(e.ToUpperInvariant()));
        Assert.DoesNotContain(all, f => f.HasExtension(".kra")); // Krita is not written (see docs/format-coverage.md)
    }

    [Theory]
    [InlineData(@"C:\p\shelf-christmas-decoration.png", "png", "shelf-christmas-decoration.png")]
    [InlineData(@"C:\p\photo.JPEG", "jpeg", "photo.jpeg")]
    [InlineData(@"C:\p\photo.jfif", "jpeg", "photo.jfif")]
    [InlineData(@"C:\p\scan.tiff", "tiff", "scan.tiff")]
    [InlineData(@"C:\p\raw.cr2", "png", "raw.png")]
    [InlineData(@"C:\p\vector.svg", "png", "vector.png")]
    [InlineData(@"C:\p\layers.psd", "psd", "layers.psd")]
    public void Default_type_follows_the_source_and_the_name_matches_it(string source, string expectedId, string expectedName)
    {
        var choices = SaveAsDialogPolicy.Choices(_ => true);
        var format = SaveAsDialogPolicy.DefaultFormat(source, choices);
        Assert.Equal(expectedId, format.Id);
        var name = SaveAsDialogPolicy.SuggestedFileName(source, format);
        Assert.Equal(expectedName, name);
        Assert.True(format.HasExtension(Path.GetExtension(name)), "the suggested name's extension must match the preselected type");
    }

    [Fact]
    public void Default_type_is_png_when_the_source_format_is_not_writable_here()
    {
        var withoutWebP = SaveAsDialogPolicy.Choices(f => f != SaveFormatCatalog.WebP);
        Assert.Same(SaveFormatCatalog.Png, SaveAsDialogPolicy.DefaultFormat(@"C:\p\a.webp", withoutWebP));
        Assert.Equal("a.png", SaveAsDialogPolicy.SuggestedFileName(@"C:\p\a.webp", SaveFormatCatalog.Png));
        Assert.Equal("a-selection.png", SaveAsDialogPolicy.SuggestedFileName(null, SaveFormatCatalog.Png, "a-selection"));
    }

    [Theory]
    // Typed extension of a writable type wins, whatever type is selected.
    [InlineData(@"C:\p\a.png", "jpeg", @"C:\p\a.png", "png")]
    [InlineData(@"C:\p\a.JPG", "png", @"C:\p\a.JPG", "jpeg")]
    [InlineData(@"C:\p\a.qoi", "png", @"C:\p\a.qoi", "qoi")]
    // The selected type wins among types sharing an extension.
    [InlineData(@"C:\p\a.jpeg", "jpeg", @"C:\p\a.jpeg", "jpeg")]
    // No or unknown extension: the selected type's extension is appended.
    [InlineData(@"C:\p\a", "jpeg", @"C:\p\a.jpg", "jpeg")]
    [InlineData(@"C:\p\a.", "webp", @"C:\p\a.webp", "webp")]
    [InlineData(@"C:\p\holiday.v2", "png", @"C:\p\holiday.v2.png", "png")]
    public void Picked_name_and_type_resolve_to_a_matching_destination(string picked, string selectedId, string expectedPath, string expectedId)
    {
        var choices = SaveAsDialogPolicy.Choices(_ => true);
        var (path, format) = SaveAsDialogPolicy.ResolveDestination(picked, SaveFormatCatalog.FromId(selectedId), choices);
        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedId, format.Id);
    }

    [Fact]
    public void A_typed_extension_this_pc_cannot_write_gets_the_selected_types_extension()
    {
        var withoutHeic = SaveAsDialogPolicy.Choices(f => !f.NeedsWindowsExtension);
        var (path, format) = SaveAsDialogPolicy.ResolveDestination(@"C:\p\a.heic", SaveFormatCatalog.Png, withoutHeic);
        Assert.Equal(@"C:\p\a.heic.png", path);
        Assert.Same(SaveFormatCatalog.Png, format);
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".png", true)]
    [InlineData(".webp", true)]
    [InlineData(".qoi", true)]
    [InlineData(".tga", true)]
    [InlineData(".psd", false)] // layers would be flattened
    [InlineData(".ora", false)]
    [InlineData(".exr", false)] // HDR precision would be lost
    [InlineData(".hdr", false)]
    [InlineData(".ico", false)] // other icon sizes would be lost
    [InlineData(".dds", false)]
    [InlineData(".kra", false)]
    [InlineData(".svg", false)]
    public void Plain_save_rewrites_in_place_only_when_nothing_is_lost(string extension, bool inPlace)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(inPlace, SaveFormatAvailability.CanSaveInPlace(extension));
    }
}
