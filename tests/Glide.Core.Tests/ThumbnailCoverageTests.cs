using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Glide.App.Platform;
using SkiaSharp;
using Xunit.Abstractions;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// Every format Glide can decode on a clean PC gets an Explorer thumbnail:
/// (a) the Recommended thumbnail registration routes it to Glide's provider on a clean-Windows handler table, or
///     it is deliberately left to a working in-box Windows thumbnailer (reason listed below), and
/// (b) the provider itself renders a synthetic fixture of it to a non-blank thumbnail, through the same
///     DllGetClassObject -> IInitializeWithStream -> GetThumbnail path Explorer uses (nothing is registered).
/// The per-extension matrix (decode route / thumbnail route) is written to the test output.
/// </summary>
[Collection("ShellThumbnailProvider")]
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailCoverageTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glide-thumb-coverage-" + Guid.NewGuid().ToString("N"));

    public ThumbnailCoverageTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { ShellThumbnailProviderTests.Provider.Instance?.ClearOverrides(); } catch { }
        TempDir.TryDelete(_directory);
    }

    /// <summary>Left to Windows on purpose: Glide never takes these from a working in-box thumbnailer.</summary>
    private static readonly Dictionary<string, string> LeftToWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "in-box WIC JPEG codec; Windows' own thumbnailer works on every PC",
        [".jpeg"] = "in-box WIC JPEG codec; Windows' own thumbnailer works on every PC",
        [".jpe"] = "in-box WIC JPEG codec; Windows' own thumbnailer works on every PC",
        [".png"] = "in-box WIC PNG codec; Windows' own thumbnailer works on every PC",
        [".bmp"] = "in-box WIC BMP codec; Windows' own thumbnailer works on every PC",
        [".gif"] = "in-box WIC GIF codec; Windows' own thumbnailer works on every PC",
        [".tif"] = "in-box WIC TIFF codec; Windows' own thumbnailer works on every PC",
        [".tiff"] = "in-box WIC TIFF codec; Windows' own thumbnailer works on every PC",
        [".ico"] = "in-box WIC ICO codec; Windows' own thumbnailer works on every PC",
    };

    /// <summary>Not thumbnailable by any handler: Explorer resolves handlers by the final suffix only.</summary>
    private static readonly Dictionary<string, string> NotAssociable = new(StringComparer.OrdinalIgnoreCase)
    {
        [".hdr.gz"] = "compound suffix: Explorer only consults .gz, and claiming .gz would take over every gzip archive",
        [".nii.gz"] = "compound suffix: Explorer only consults .gz, and claiming .gz would take over every gzip archive",
        [".fts.gz"] = "compound suffix: Explorer only consults .gz, and claiming .gz would take over every gzip archive",
    };

    /// <summary>A clean Windows 11: the in-box Photo Thumbnail Provider is registered for these, and no third-party handler.</summary>
    private static readonly string[] CleanWindowsPhotoProviderExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".rle", ".gif", ".tif", ".tiff", ".ico", ".jxr", ".wdp",
        ".heic", ".heif", ".avif", ".webp", ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".raf", ".orf", ".ori",
        ".rw2", ".rwl", ".pef", ".srw", ".sr2", ".srf", ".erf", ".kdc", ".dcr", ".mrw", ".raw", ".3fr", ".mef", ".mos", ".x3f",
    };

    /// <summary>Everything the viewer decodes without optional downloads: core, built-in decoders and glidecodecs.dll.</summary>
    private static IReadOnlyList<string> DecodableExtensions()
        => CodecCapabilityRegistry.All
            .Where(c => c.DecodeEnabled && c.Tier is CodecTier.NativeOs or CodecTier.BuiltIn)
            .Select(c => c.Extension)
            .Concat(BundledCodecLibraries.BundledExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Every_decodable_extension_is_routed_to_glide_or_left_to_a_working_windows_thumbnailer()
    {
        var photo = CleanWindowsPhotoProviderExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codecFormats = BundledCodecLibraries.BundledExtensions.Concat(BundledCodecLibraries.ManagedExtensions).ToArray();
        var recommended = WindowsThumbnailRegistration.ResolveExtensions(ThumbnailProviderMode.Recommended, ImageFormatRegistry.Extensions,
            extension => photo.Contains(extension) ? WindowsThumbnailRegistration.WindowsPhotoThumbnailProvider : null, codecFormats)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = DecodableExtensions()
            .Where(extension => !recommended.Contains(extension) && !LeftToWindows.ContainsKey(extension) && !NotAssociable.ContainsKey(extension))
            .ToArray();
        Assert.True(missing.Length == 0, "No thumbnail route for: " + string.Join(", ", missing));
        // The exception lists must not hide a format Glide's provider is meant to own.
        Assert.DoesNotContain(LeftToWindows.Keys, recommended.Contains);
        Assert.DoesNotContain(NotAssociable.Keys, extension => !DecodableExtensions().Contains(extension));
    }

    [Fact]
    public void Managed_codec_library_exports_the_extension_table_registration_relies_on()
    {
        var path = Path.Combine(RepositoryRoot(), "codecs", BundledCodecLibraries.ManagedDllName);
        if (!File.Exists(path)) return; // build.cmd publishes it before the tests run
        var module = NativeLibrary.Load(path); // never freed: a NativeAOT library cannot be unloaded
        var getInfo = Marshal.GetDelegateForFunctionPointer<GetInfoFn>(NativeLibrary.GetExport(module, "GlideCodecProvider_GetInfo"));
        var info = Marshal.PtrToStructure<ProviderInfo>(getInfo());
        Assert.Equal(2u, info.AbiVersion);
        Assert.Equal((uint)Marshal.SizeOf<ProviderInfo>(), info.StructSize);
        Assert.Equal(3u, info.Capabilities & 3u); // preview + full
        var extensions = Enumerable.Range(0, (int)info.ExtensionCount)
            .Select(i => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info.Extensions, i * IntPtr.Size))!)
            .ToArray();
        Assert.Equal(BundledCodecLibraries.ManagedExtensions, extensions);
        Assert.Equal("glide.managed", Marshal.PtrToStringUTF8(info.Id));
        // Every built-in managed family the native provider has no decoder of its own for is covered.
        foreach (var capability in CodecCapabilityRegistry.All.Where(c => c.Tier == CodecTier.BuiltIn && c.DecodeEnabled))
        {
            if (WindowsThumbnailRegistration.NativeProviderFormats.Contains(capability.Extension)) continue;
            Assert.Contains(capability.Extension, extensions);
        }
    }

    [Fact]
    public void Provider_pins_the_codec_libraries_shipped_in_codecs()
    {
        var provider = ShellThumbnailProviderTests.Provider.Require();
        if (!provider.HasCodecBridge) return; // stale DLL; build.cmd rebuilds it first
        var codecs = Path.Combine(RepositoryRoot(), "codecs");
        foreach (var (kind, name) in new[] { (0, BundledCodecLibraries.BundledDllName), (1, BundledCodecLibraries.ManagedDllName) })
        {
            var file = Path.Combine(codecs, name);
            if (!File.Exists(file)) continue;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            Assert.True(hash == provider.CodecPin(kind),
                $"{name}: provider pins {provider.CodecPin(kind)} but codecs\\{name} is {hash}. Rebuild the provider (build.cmd) after rebuilding the codec library.");
        }
    }

    [Fact]
    public void Every_decodable_extension_renders_a_non_blank_thumbnail_through_the_provider()
    {
        var provider = ShellThumbnailProviderTests.Provider.Require();
        if (!provider.HasCodecBridge) return; // stale DLL without the codec bridge; build.cmd rebuilds it first

        var photo = CleanWindowsPhotoProviderExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recommended = WindowsThumbnailRegistration.ResolveExtensions(ThumbnailProviderMode.Recommended, ImageFormatRegistry.Extensions,
            extension => photo.Contains(extension) ? WindowsThumbnailRegistration.WindowsPhotoThumbnailProvider : null,
            BundledCodecLibraries.BundledExtensions.Concat(BundledCodecLibraries.ManagedExtensions)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var failures = new List<string>();
        var matrix = new StringBuilder();
        matrix.AppendLine("| extension | viewer decode route | Explorer thumbnail (clean PC, Recommended) | provider decoder |");
        matrix.AppendLine("|---|---|---|---|");
        var cases = DecodableExtensions().Select(e => (Name: e, Extension: e)).Append((Name: "zip.psd", Extension: ".psd")).Append((Name: "zip.psb", Extension: ".psb"));
        foreach (var (name, extension) in cases)
        {
            var capability = CodecCapabilityRegistry.Describe(extension);
            var decodeRoute = BundledCodecLibraries.BundledExtensions.Contains(extension) && capability.Tier != CodecTier.BuiltIn
                ? "glidecodecs.dll (bundled codec)" : capability.Provider;
            var registration = NotAssociable.TryGetValue(extension, out var why) ? "none - " + why
                : recommended.Contains(extension) ? "Glide provider"
                : LeftToWindows.TryGetValue(extension, out var reason) ? "Windows - " + reason
                : "MISSING";

            if (NotAssociable.ContainsKey(extension))
            {
                matrix.AppendLine($"| {name} | {decodeRoute} | {registration} | n/a |");
                continue;
            }
            var bytes = Fixture(name);
            if (bytes is null)
            {
                failures.Add($"{name}: no synthetic fixture");
                continue;
            }
            var path = Path.Combine(_directory, "sample" + (name.StartsWith('.') ? name : "-" + name.Replace(extension, string.Empty, StringComparison.Ordinal) + extension));
            File.WriteAllBytes(path, bytes);
            var rendered = provider.TryRender(path, 256, quality: 1);
            if (rendered is null)
            {
                failures.Add($"{name}: provider declined the file");
                matrix.AppendLine($"| {name} | {decodeRoute} | {registration} | FAILED |");
                continue;
            }
            if (!HasContent(rendered.Bgra)) failures.Add($"{name}: blank thumbnail ({rendered.Diagnostics.Decoder})");
            matrix.AppendLine($"| {name} | {decodeRoute} | {registration} | {rendered.Diagnostics.Decoder} |");
        }
        _output.WriteLine(matrix.ToString());
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(1, provider.CodecState(0));
        Assert.Equal(1, provider.CodecState(1));
    }

    [Fact]
    public void Hostile_codec_files_fail_cleanly_inside_the_provider()
    {
        var provider = ShellThumbnailProviderTests.Provider.Require();
        if (!provider.HasCodecBridge) return;
        var codecs = Path.Combine(RepositoryRoot(), "tests", "Glide.Core.Tests", "Fixtures", "Codecs");
        foreach (var name in new[] { "garbage.heic", "garbage.jp2", "garbage.jxl", "truncated.avif", "truncated.j2k" })
        {
            var path = Path.Combine(_directory, name);
            File.Copy(Path.Combine(codecs, name), path, overwrite: true);
            var result = provider.TryRender(path, 256);
            Assert.True(result is null || HasContent(result.Bgra) || result.Width > 0, name); // never a crash
        }
        foreach (var (bytes, extension) in new[]
                 {
                     (new byte[] { 0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 0xFF, 0xFF }, ".cr2"),
                     ("v/1\u0001"u8.ToArray(), ".exr"),
                     (new byte[300], ".dcm"),
                     (Encoding.ASCII.GetBytes("PK\u0003\u0004 not a zip"), ".kra"),
                     (Gzip(new byte[64]), ".svgz"),
                 })
        {
            var path = Path.Combine(_directory, "hostile" + extension);
            File.WriteAllBytes(path, bytes);
            Assert.Null(provider.TryRender(path, 256));
        }
    }

    // -------------------------------------------------------------------------------- fixtures ----------

    private static readonly SKColor Red = new(220, 30, 30);
    private static readonly SKColor Blue = new(30, 30, 220);
    private const int W = 64, H = 48;

    private static SKColor At(int x) => x < W / 2 ? Red : Blue;

    private static byte[]? Fixture(string name) => name switch
    {
        ".jpg" or ".jpeg" or ".jpe" => Jpeg(W, H, Red, Blue),
        ".png" => Png(W, H, (x, _) => At(x)),
        ".webp" => Encode(SKEncodedImageFormat.Webp),
        ".bmp" => Bmp(),
        ".gif" or ".tif" or ".tiff" => GdiPlus(name),
        ".ico" => Ico(),
        ".svg" => Encoding.UTF8.GetBytes(Svg),
        ".svgz" => Gzip(Encoding.UTF8.GetBytes(Svg)),
        ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => Tga(),
        ".pcx" => Pcx(),
        ".pnm" or ".ppm" => Pnm("P6", 3),
        ".pgm" => Pnm("P5", 1),
        ".pbm" => Pbm(),
        ".pam" => Pam(),
        ".qoi" => Qoi(),
        ".hdr" or ".rgbe" => Hdr(),
        ".wbmp" => Wbmp(),
        ".xbm" => Encoding.ASCII.GetBytes("#define t_width 16\n#define t_height 4\nstatic char t_bits[] = {\n0xff,0x00,0xff,0x00,0xff,0x00,0xff,0x00};\n"),
        ".xpm" => Encoding.ASCII.GetBytes("/* XPM */\nstatic char *t[] = {\n\"4 2 2 1\",\n\"a c #DC1E1E\",\n\"b c #1E1EDC\",\n\"aabb\",\n\"aabb\"\n};\n"),
        ".sgi" or ".rgb" or ".rgba" or ".bw" => Sgi(),
        ".icns" => Icns(),
        ".ras" or ".sun" => SunRaster(),
        ".xwd" => Xwd(),
        ".dpx" => Dpx(),
        ".psd" or ".psb" or ".pdd" => Psd(compression: 0),
        "zip.psd" or "zip.psb" => Psd(compression: 2),
        ".exr" => Exr(),
        ".pfm" => Pfm(),
        ".dcm" or ".dicom" or ".ima" => Dicom(),
        ".nii" => Nifti(),
        ".fits" or ".fit" or ".fts" => Fits(),
        ".ora" => Zip(("mimetype", Encoding.ASCII.GetBytes("image/openraster")), ("mergedimage.png", Png(W, H, (x, _) => At(x)))),
        ".kra" => Zip(("mimetype", Encoding.ASCII.GetBytes("application/x-krita")), ("mergedimage.png", Png(W, H, (x, _) => At(x)))),
        ".pdn" => Pdn(),
        ".xcf" => Xcf(),
        ".ktx" => Ktx1(),
        ".ktx2" => Ktx2(),
        ".pvr" => Pvr(),
        ".astc" => Astc(),
        _ when CodecCapabilityRegistry.CameraRawPreviewExtensions.Contains(name) => RawTiff(),
        _ when BundledCodecLibraries.BundledExtensions.Contains(name) => CodecFixture(name),
        _ => null,
    };

    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"48\"><rect width=\"32\" height=\"48\" fill=\"#dc1e1e\"/><rect x=\"32\" width=\"32\" height=\"48\" fill=\"#1e1edc\"/></svg>";

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) bitmap.SetPixel(x, y, At(x));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Bmp()
    {
        var w = new Writer();
        var row = W * 3;
        w.Ascii("BM").U32(54 + row * H).U32(0).U32(54).U32(40).U32(W).U32(H).U16(1).U16(24).U32(0).U32(row * H).U32(2835).U32(2835).U32(0).U32(0);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = At(x); w.U8(c.Blue).U8(c.Green).U8(c.Red); }
        return w.ToArray();
    }

    private static byte[] GdiPlus(string extension)
    {
        using var bitmap = new System.Drawing.Bitmap(W, H);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = At(x); bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(c.Red, c.Green, c.Blue)); }
        using var stream = new MemoryStream();
        bitmap.Save(stream, extension == ".gif" ? System.Drawing.Imaging.ImageFormat.Gif : System.Drawing.Imaging.ImageFormat.Tiff);
        return stream.ToArray();
    }

    private static byte[] Ico()
    {
        var png = Png(32, 32, (x, _) => x < 16 ? Red : Blue);
        var w = new Writer();
        w.U16(0).U16(1).U16(1).U8(32).U8(32).U8(0).U8(0).U16(1).U16(32).U32(png.Length).U32(22).Bytes(png);
        return w.ToArray();
    }

    private static byte[] Tga()
    {
        var w = new Writer();
        w.U8(0).U8(0).U8(2).Zeros(5).U16(0).U16(0).U16(W).U16(H).U8(24).U8(0x20);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = At(x); w.U8(c.Blue).U8(c.Green).U8(c.Red); }
        return w.ToArray();
    }

    private static byte[] Pcx()
    {
        var w = new Writer();
        w.U8(10).U8(5).U8(1).U8(8).U16(0).U16(0).U16(W - 1).U16(H - 1).U16(72).U16(72).Zeros(48).U8(0).U8(3).U16(W).U16(1).Zeros(58);
        for (var y = 0; y < H; y++)
            for (var plane = 0; plane < 3; plane++)
                for (var x = 0; x < W; x++)
                {
                    var c = At(x);
                    w.U8(0xC1).U8(plane == 0 ? c.Red : plane == 1 ? c.Green : c.Blue);
                }
        return w.ToArray();
    }

    private static byte[] Pnm(string magic, int channels)
    {
        var w = new Writer().Ascii($"{magic}\n{W} {H}\n255\n");
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var c = At(x);
                if (channels == 3) w.U8(c.Red).U8(c.Green).U8(c.Blue); else w.U8(x < W / 2 ? 30 : 200);
            }
        return w.ToArray();
    }

    private static byte[] Pbm()
    {
        var w = new Writer().Ascii($"P4\n{W} {H}\n");
        for (var y = 0; y < H; y++) for (var b = 0; b < W / 8; b++) w.U8(b < W / 16 ? 0xFF : 0x00);
        return w.ToArray();
    }

    private static byte[] Pam()
    {
        var w = new Writer().Ascii($"P7\nWIDTH {W}\nHEIGHT {H}\nDEPTH 3\nMAXVAL 255\nTUPLTYPE RGB\nENDHDR\n");
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = At(x); w.U8(c.Red).U8(c.Green).U8(c.Blue); }
        return w.ToArray();
    }

    private static byte[] Qoi()
    {
        var w = new Writer { BigEndian = true };
        w.Ascii("qoif").U32(W).U32(H).U8(3).U8(0);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = At(x); w.U8(0xFE).U8(c.Red).U8(c.Green).U8(c.Blue); }
        w.Zeros(7).U8(1);
        return w.ToArray();
    }

    private static byte[] Hdr()
    {
        var w = new Writer().Ascii($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {H} +X {W}\n");
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (x < W / 2) w.U8(200).U8(25).U8(25).U8(128); else w.U8(25).U8(25).U8(200).U8(128);
        return w.ToArray();
    }

    private static byte[] Wbmp()
    {
        var w = new Writer().U8(0).U8(0).U8(W).U8(H);
        for (var y = 0; y < H; y++) for (var b = 0; b < W / 8; b++) w.U8(b < W / 16 ? 0x00 : 0xFF);
        return w.ToArray();
    }

    private static byte[] Sgi()
    {
        var w = new Writer { BigEndian = true };
        w.U16(474).U8(0).U8(1).U16(3).U16(W).U16(H).U16(3).U32(0).U32(255).Zeros(4).Zeros(80).U32(0).Zeros(404);
        for (var plane = 0; plane < 3; plane++)
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++) { var c = At(x); w.U8(plane == 0 ? c.Red : plane == 1 ? c.Green : c.Blue); }
        return w.ToArray();
    }

    private static byte[] Icns()
    {
        var png = Png(W, H, (x, _) => At(x));
        var w = new Writer { BigEndian = true };
        w.Ascii("icns").U32(16 + png.Length).Ascii("ic12").U32(8 + png.Length).Bytes(png);
        return w.ToArray();
    }

    private static byte[] SunRaster()
    {
        var w = new Writer { BigEndian = true };
        w.U32(0x59A66A95).U32(W).U32(H).U32(24).U32(W * H * 3).U32(1).U32(0).U32(0);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = At(x); w.U8(c.Blue).U8(c.Green).U8(c.Red); }
        return w.ToArray();
    }

    private static byte[] Xwd()
    {
        var w = new Writer { BigEndian = true };
        w.U32(100).U32(7).U32(2).U32(24).U32(W).U32(H).U32(0).U32(1).U32(32).U32(1).U32(32).U32(32).U32(W * 4).U32(4)
         .U32(0xFF0000).U32(0xFF00).U32(0xFF).U32(8).U32(0).U32(0).U32(W).U32(H).U32(0).U32(0).U32(0);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = At(x); w.U8(0).U8(c.Red).U8(c.Green).U8(c.Blue); }
        return w.ToArray();
    }

    private static byte[] Dpx()
    {
        var w = new Writer { BigEndian = true };
        w.Ascii("SDPX").U32(2048).Ascii("V2.0").Zeros(768 - 12).U16(0).U16(1).U32(W).U32(H);
        w.Zeros(20).U8(50).U8(2).U8(2).U8(8).U16(0).U16(0).U32(2048).U32(0).U32(0);
        w.Zeros(2048 - (int)w.Length);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++) { var c = At(x); w.U8(c.Red).U8(c.Green).U8(c.Blue); }
        return w.ToArray();
    }

    private static byte[] Psd(int compression)
    {
        var planes = new Writer();
        for (var plane = 0; plane < 3; plane++)
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++) { var c = At(x); planes.U8(plane == 0 ? c.Red : plane == 1 ? c.Green : c.Blue); }
        var data = compression == 2 ? ManagedDecoderTestSupport.Zlib(planes.ToArray()) : planes.ToArray();
        var w = new Writer { BigEndian = true };
        w.Ascii("8BPS").U16(1).Zeros(6).U16(3).U32(H).U32(W).U16(8).U16(3);
        w.U32(0).U32(0).U32(0);
        w.U16(compression).Bytes(data);
        return w.ToArray();
    }

    private static byte[] Exr()
    {
        // Uncompressed half-float RGB, 2x1: red then blue.
        var exr = new Writer();
        exr.U32(20000630).U32(2);
        void Attribute(string name, string type, byte[] value) => exr.Ascii(name).U8(0).Ascii(type).U8(0).U32(value.Length).Bytes(value);
        var channels = new Writer();
        foreach (var channel in new[] { "B", "G", "R" }) channels.Ascii(channel).U8(0).U32(1).U32(0).U32(1).U32(1);
        channels.U8(0);
        Attribute("channels", "chlist", channels.ToArray());
        Attribute("compression", "compression", new byte[] { 0 });
        Attribute("dataWindow", "box2i", new Writer().U32(0).U32(0).U32(1).U32(0).ToArray());
        exr.U8(0);
        var offset = exr.Length + 8;
        exr.U64(offset).U32(0).U32(12);
        foreach (var plane in new[] { new[] { 0f, 1f }, new[] { 0f, 0f }, new[] { 1f, 0f } })
            foreach (var v in plane) exr.U16(BitConverter.HalfToUInt16Bits((Half)v));
        return exr.ToArray();
    }

    private static byte[] Pfm()
    {
        var w = new Writer().Ascii("PF\n2 1\n-1.0\n");
        foreach (var v in new[] { 1f, 0f, 0f, 0f, 0f, 1f }) w.F32(v);
        return w.ToArray();
    }

    private static byte[] Dicom()
    {
        var w = new Writer();
        w.Zeros(128).Ascii("DICM");
        var ts = Encoding.ASCII.GetBytes("1.2.840.10008.1.2.1\0");
        w.U16(0x0002).U16(0x0010).Ascii("UI").U16(ts.Length).Bytes(ts);
        void Us(int element, int value) => w.U16(0x0028).U16(element).Ascii("US").U16(2).U16(value);
        Us(0x0002, 1);
        var photometric = Encoding.ASCII.GetBytes("MONOCHROME2 ");
        w.U16(0x0028).U16(0x0004).Ascii("CS").U16(photometric.Length).Bytes(photometric);
        Us(0x0010, 2); Us(0x0011, 2); Us(0x0100, 8); Us(0x0101, 8); Us(0x0102, 7); Us(0x0103, 0);
        var pixels = new byte[] { 0, 255, 128, 64 };
        w.U16(0x7FE0).U16(0x0010).Ascii("OB").U16(0).U32(pixels.Length).Bytes(pixels);
        return w.ToArray();
    }

    private static byte[] Nifti()
    {
        var w = new Writer();
        w.U32(348).Zeros(36);
        w.U16(3).U16(2).U16(2).U16(1).U16(1).U16(1).U16(1).U16(1);
        w.Zeros(70 - 56);
        w.U16(2).U16(8); // uint8
        w.Zeros(108 - 74);
        w.F32(352).F32(0).F32(0);
        w.Zeros(344 - 120);
        w.Ascii("n+1\0");
        w.Zeros(4);
        w.Bytes(new byte[] { 0, 255, 128, 64 });
        return w.ToArray();
    }

    private static byte[] Fits()
    {
        static string Card(string key, string value) => (key.PadRight(8) + "= " + value.PadLeft(20)).PadRight(80);
        var header = new StringBuilder();
        foreach (var card in new[] { Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "2"), Card("NAXIS1", "2"), Card("NAXIS2", "2") }) header.Append(card);
        header.Append("END".PadRight(80));
        while (header.Length % 2880 != 0) header.Append(' ');
        var data = new byte[2880];
        new byte[] { 0, 255, 128, 64 }.CopyTo(data, 0);
        return Encoding.ASCII.GetBytes(header.ToString()).Concat(data).ToArray();
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var entry = archive.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                entry.Write(data);
            }
        }
        return stream.ToArray();
    }

    private static byte[] Pdn()
    {
        var thumb = Convert.ToBase64String(Png(W, H, (x, _) => At(x)));
        var xml = Encoding.UTF8.GetBytes($"<pdnImage width=\"{W}\" height=\"{H}\" layerCount=\"1\"><custom><thumb png=\"{thumb}\" /></custom></pdnImage>");
        return new Writer().Ascii("PDN3").U8(xml.Length & 0xFF).U8((xml.Length >> 8) & 0xFF).U8((xml.Length >> 16) & 0xFF).Bytes(xml).Bytes(new byte[] { 0, 1, 2, 3, 4 }).ToArray();
    }

    /// <summary>GIMP XCF v0, uncompressed, one RGB layer covering the canvas.</summary>
    private static byte[] Xcf()
    {
        var w = new Writer { BigEndian = true };
        w.Ascii("gimp xcf file").U8(0).U32(W).U32(H).U32(0);
        w.U32(17).U32(1).U8(0); // compression: none
        w.U32(0).U32(0);
        var layerPointer = w.Position; w.U32(0); w.U32(0); // layer list
        w.U32(0);                                           // channel list
        w.PatchU32(layerPointer, w.Position);
        w.U32(W).U32(H).U32(0);
        var name = Encoding.UTF8.GetBytes("layer\0");
        w.U32(name.Length).Bytes(name);
        w.U32(6).U32(4).U32(255).U32(8).U32(4).U32(1).U32(15).U32(8).U32(0).U32(0).U32(0).U32(0);
        var hierarchy = w.Position; w.U32(0); w.U32(0); // hierarchy, mask
        w.PatchU32(hierarchy, w.Position);
        w.U32(W).U32(H).U32(3);
        var level = w.Position; w.U32(0); w.U32(0);
        w.PatchU32(level, w.Position);
        w.U32(W).U32(H);
        var tile = w.Position; w.U32(0); w.U32(0); // one 64x48 tile
        w.PatchU32(tile, w.Position);
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var c = At(x); w.U8(c.Red).U8(c.Green).U8(c.Blue); }
        return w.ToArray();
    }

    private static byte[] Rgba(int width, int height)
    {
        var w = new Writer();
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { var c = x < width / 2 ? Red : Blue; w.U8(c.Red).U8(c.Green).U8(c.Blue).U8(255); }
        return w.ToArray();
    }

    private static readonly byte[] Ktx1Id = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Ktx2Id = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] Ktx1()
    {
        var data = Rgba(4, 4);
        return new Writer().Bytes(Ktx1Id).U32(0x04030201).U32(0x1401).U32(1).U32(0x1908).U32(0x8058).U32(0x1908)
            .U32(4).U32(4).U32(0).U32(0).U32(1).U32(1).U32(0).U32(data.Length).Bytes(data).ToArray();
    }

    private static byte[] Ktx2()
    {
        var data = Rgba(4, 4);
        return new Writer().Bytes(Ktx2Id).U32(37).U32(1).U32(4).U32(4).U32(0).U32(0).U32(1).U32(1).U32(0)
            .U32(0).U32(0).U32(0).U32(0).U64(0).U64(0).U64(104).U64(data.Length).U64(data.Length).Bytes(data).ToArray();
    }

    private static byte[] Pvr()
    {
        var format = (long)('r' | ('g' << 8) | ('b' << 16) | ((long)'a' << 24)) | (8L << 32) | (8L << 40) | (8L << 48) | (8L << 56);
        return new Writer().U32(0x03525650).U32(0).U64(format).U32(0).U32(0).U32(4).U32(4).U32(1).U32(1).U32(1).U32(1).U32(0)
            .Bytes(Rgba(4, 4)).ToArray();
    }

    private static byte[] Astc()
    {
        // 4x4 blocks, 4x4 image, one void-extent (constant colour) block.
        var w = new Writer();
        w.U32(0x5CA1AB13).U8(4).U8(4).U8(1).U8(4).U8(0).U8(0).U8(4).U8(0).U8(0).U8(1).U8(0).U8(0);
        w.U64(unchecked((long)0xFFFFFFFFFFFFFDFCUL));
        w.U16(Red.Red * 257).U16(Red.Green * 257).U16(Red.Blue * 257).U16(255 * 257);
        return w.ToArray();
    }

    private static byte[] RawTiff()
    {
        var jpeg = Jpeg(W, H, Red, Blue);
        var tiff = new Writer();
        tiff.Ascii("II").U16(42).U32(8).U16(2);
        tiff.U16(0x0201).U16(4).U32(1).U32(8 + 2 + 24 + 4);
        tiff.U16(0x0202).U16(4).U32(1).U32(jpeg.Length);
        tiff.U32(0).Bytes(jpeg);
        return tiff.ToArray();
    }

    private static byte[]? CodecFixture(string extension)
    {
        var directory = Path.Combine(RepositoryRoot(), "tests", "Glide.Core.Tests", "Fixtures", "Codecs");
        var path = Path.Combine(directory, (extension == ".avifs" ? "anim" : "basic") + extension);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Non-blank: the thumbnail (premultiplied BGRA, transparency kept) has visible pixels that are clearly not white.</summary>
    private static bool HasContent(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
            if (bgra[i + 3] > 60 && bgra[i + 3] - Math.Min(bgra[i], Math.Min(bgra[i + 1], bgra[i + 2])) > 60) return true;
        return false;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Glide.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetInfoFn();

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderInfo
    {
        public uint AbiVersion;
        public uint StructSize;
        public IntPtr Id;
        public IntPtr Version;
        public IntPtr License;
        public IntPtr Extensions;
        public uint ExtensionCount;
        public uint Capabilities;
    }
}
