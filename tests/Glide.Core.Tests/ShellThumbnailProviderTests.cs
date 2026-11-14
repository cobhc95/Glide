using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Glide.App.Platform;

namespace Glide.Core.Tests;

/// <summary>
/// Headless, in-process tests of the native Explorer thumbnail provider (Glide.ShellThumbnail.dll).
///
/// The DLL is loaded with LoadLibrary and driven exactly as Explorer's thumbnail host drives it
/// (DllGetClassObject -> IClassFactory -> IInitializeWithStream -> IThumbnailProvider), but nothing
/// is registered, Explorer is never involved and the thumbnail cache is never touched. Presets are
/// selected through the provider's in-process test override export instead of the registry.
///
/// These guard the 4.8.0 fixes: Fast/Balanced/High used to be ignored by the provider (it hard-coded
/// Balanced), embedded previews were upscaled and never rotated, GIF frames were not placed on their
/// logical screen, and every file was read into memory in full.
/// </summary>
[Collection("ShellThumbnailProvider")]
public sealed class ShellThumbnailProviderTests : IDisposable
{
    private const int Fast = 0, Balanced = 1, High = 2;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glide-thumb-tests-" + Guid.NewGuid().ToString("N"));

    public ShellThumbnailProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Provider.Instance?.ClearOverrides(); } catch { }
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ preset wiring ----------

    [Fact]
    public void Presets_resolve_to_distinct_native_profiles()
    {
        var provider = Provider.Require();
        var fast = provider.DescribeQuality(Fast);
        var balanced = provider.DescribeQuality(Balanced);
        var high = provider.DescribeQuality(High);
        Assert.NotEqual(fast, balanced);
        Assert.NotEqual(balanced, high);
        Assert.NotEqual(fast, high);

        // [supersample, embeddedMin%, interpolation, gifFrameSearch, webpBypassFiltering, webpNoFancy]
        Assert.Equal(1, fast[0]);
        Assert.Equal(50, fast[1]);                 // accepts small camera previews
        Assert.Equal(1, fast[2]);                  // WICBitmapInterpolationModeLinear
        Assert.Equal(1, fast[3]);                  // GIF: frame 0 only
        Assert.Equal(1, fast[4]);
        Assert.Equal(3, balanced[2]);              // Fant (area averaging)
        Assert.Equal(100, balanced[1]);            // never upscales a preview
        Assert.Equal(2, high[0]);                  // decodes at 2x the requested edge
        Assert.Equal(4, high[2]);                  // HighQualityCubic
        Assert.Equal(0, high[5]);                  // full WebP chroma upsampling
    }

    [Fact]
    public void Requested_size_from_the_host_is_clamped()
    {
        var provider = Provider.Require();
        // Providers built before 5.0 rendered whatever edge the host asked for (an 8000 px request made an
        // 8000 x 4000 canvas inside Explorer's thumbnail host). Built from current sources, it is clamped.
        if (!provider.HasExport("GlideThumbnailMaxEdge")) return; // stale DLL; build.cmd rebuilds it first
        var path = WriteBitmap("clamp.png", 64, 32, ImageFormat.Png);
        var rendered = provider.Render(path, 8000, Balanced);
        Assert.True(Math.Max(rendered.Width, rendered.Height) <= 2048, $"rendered {rendered.Width}x{rendered.Height}");
        Assert.Equal(2048, rendered.Width);
        var huge = provider.Render(path, int.MaxValue, Balanced);
        Assert.Equal(2048, Math.Max(huge.Width, huge.Height));
    }

    [Fact]
    public void Settings_map_to_the_registry_values_the_provider_reads()
    {
        Assert.Equal(0, ThumbnailProviderConfig.QualityCode("Fast"));
        Assert.Equal(1, ThumbnailProviderConfig.QualityCode("Balanced"));
        Assert.Equal(2, ThumbnailProviderConfig.QualityCode("High"));
        Assert.Equal(1, ThumbnailProviderConfig.QualityCode("bogus"));

        var config = ThumbnailProviderConfig.From(ThumbnailSettings.Default with { Quality = "High", MaxSourceSize = 1024, PreferEmbedded = false });
        Assert.Equal(new ThumbnailProviderConfig(2, 1024, false), config);
        Assert.Equal(0, ThumbnailProviderConfig.From(ThumbnailSettings.Default with { MaxSourceSize = 333 }).MaxSourceSize);

        // The managed writer and the native reader must agree on the key and value names.
        var native = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "native", "Glide.ShellThumbnail", "GlideThumbnailProvider.cpp"));
        Assert.Contains("L\"" + ThumbnailProviderConfig.KeyPath.Replace(@"\", @"\\") + "\"", native);
        foreach (var name in new[] { "Quality", "MaxSourceSize", "PreferEmbedded" })
            Assert.Contains($"L\"{name}\"", native);
        var store = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Glide.App", "Platform", "ThumbnailSettingsStore.cs"));
        Assert.Contains("ThumbnailProviderConfig.From(settings).Write()", store);
    }

    [Fact]
    public void Presets_produce_measurably_different_thumbnails()
    {
        var provider = Provider.Require();
        var path = WriteBitmap("detail.png", 2400, 1600, ImageFormat.Png);
        var fast = provider.Render(path, 256, Fast);
        var balanced = provider.Render(path, 256, Balanced);
        var high = provider.Render(path, 256, High);

        Assert.Equal((256, 170), (fast.Width, fast.Height));
        Assert.Equal((256, 170), (high.Width, high.Height));
        Assert.Equal((256u, 170u), (balanced.Diagnostics.DecodedWidth, balanced.Diagnostics.DecodedHeight));
        Assert.Equal((512u, 341u), (high.Diagnostics.DecodedWidth, high.Diagnostics.DecodedHeight));
        Assert.True(MeanAbsoluteDifference(fast, balanced) > 1.0, "Fast and Balanced rendered identical pixels");
        Assert.True(MeanAbsoluteDifference(balanced, high) > 0.5, "Balanced and High rendered identical pixels");
    }

    [Fact]
    public void Fast_is_not_slower_than_High_on_a_large_image()
    {
        var provider = Provider.Require();
        var path = WriteBitmap("large.bmp", 4000, 3000, ImageFormat.Bmp);
        provider.Render(path, 256, Fast);
        provider.Render(path, 256, High);
        var fast = Median(Enumerable.Range(0, 5).Select(_ => provider.Time(path, 256, Fast)));
        var high = Median(Enumerable.Range(0, 5).Select(_ => provider.Time(path, 256, High)));
        Assert.True(fast <= high * 1.10, $"Fast {fast:F1} ms was slower than High {high:F1} ms");
    }

    [Fact]
    public void Maximum_source_decode_size_caps_the_intermediate_decode()
    {
        var provider = Provider.Require();
        var path = WriteBitmap("cap.png", 3000, 2000, ImageFormat.Png);
        var automatic = provider.Render(path, 1024, Balanced, maxSource: 0);
        var capped = provider.Render(path, 1024, Balanced, maxSource: 512);
        Assert.Equal(1024u, automatic.Diagnostics.DecodedWidth);
        Assert.Equal(512u, capped.Diagnostics.DecodedWidth);
        Assert.Equal((1024, 682), (capped.Width, capped.Height)); // still the size Explorer asked for
    }

    // ------------------------------------------------------------- embedded previews ---------

    [Fact]
    public void Embedded_preview_use_depends_on_preset_size_and_toggle()
    {
        var provider = Provider.Require();
        var path = WriteExifJpeg("camera.jpg", 1600, 1200, thumbnailEdge: 160, orientation: 1);

        Assert.True(provider.Render(path, 256, Fast).Diagnostics.Embedded);          // 160 >= 50% of 256
        Assert.False(provider.Render(path, 256, Balanced).Diagnostics.Embedded);     // would need upscaling
        Assert.True(provider.Render(path, 96, Balanced).Diagnostics.Embedded);       // big enough for 96 px
        Assert.False(provider.Render(path, 256, Fast, preferEmbedded: false).Diagnostics.Embedded);
    }

    [Fact]
    public void Embedded_preview_follows_exif_orientation()
    {
        var provider = Provider.Require();
        var path = WriteExifJpeg("rotated.jpg", 1600, 1200, thumbnailEdge: 320, orientation: 6);
        var result = provider.Render(path, 256, Balanced);
        Assert.True(result.Diagnostics.Embedded);
        Assert.True(result.Height > result.Width, $"rotated preview should be portrait, got {result.Width}x{result.Height}");
    }

    // ------------------------------------------------------------------------- GIF -----------

    [Fact]
    public void Gif_first_frame_is_placed_on_its_logical_screen()
    {
        var provider = Provider.Require();
        // 400x300 canvas; frame 0 is a 200x150 red block at (100,75); the rest is transparent.
        var path = Path.Combine(_directory, "offset.gif");
        File.WriteAllBytes(path, GifWriter.Write(400, 300, new[] { (220, 30, 30), (30, 30, 220), (0, 0, 0) },
            new GifWriter.Frame(100, 75, 200, 150, Fill(200 * 150, 0), Transparent: 2),
            new GifWriter.Frame(0, 0, 400, 300, Fill(400 * 300, 1), Transparent: 2)));
        foreach (var quality in new[] { Fast, Balanced, High })
        {
            var result = provider.Render(path, 256, quality);
            Assert.Equal((256, 192), (result.Width, result.Height));
            AssertColor(result.Pixel(128, 96), (220, 30, 30), $"centre (quality {quality})");
            AssertColor(result.Pixel(8, 8), (255, 255, 255), $"transparent corner (quality {quality})");
        }
    }

    [Fact]
    public void Gif_with_blank_first_frame_shows_content_except_in_fast()
    {
        var provider = Provider.Require();
        var path = Path.Combine(_directory, "fade-in.gif");
        File.WriteAllBytes(path, GifWriter.Write(320, 240, new[] { (30, 160, 30), (0, 0, 0) },
            new GifWriter.Frame(0, 0, 320, 240, Fill(320 * 240, 1), Transparent: 1),
            new GifWriter.Frame(40, 30, 240, 180, Fill(240 * 180, 0), Transparent: 1)));
        AssertColor(provider.Render(path, 256, Balanced).Pixel(128, 96), (30, 160, 30), "balanced shows frame 2");
        AssertColor(provider.Render(path, 256, Fast).Pixel(128, 96), (255, 255, 255), "fast keeps frame 1");
    }

    [Fact]
    public void Large_animated_gif_reads_only_what_frame_zero_needs()
    {
        var provider = Provider.Require();
        var frames = Enumerable.Range(0, 12)
            .Select(i => new GifWriter.Frame(0, 0, 1000, 800, Fill(1000 * 800, (byte)(i % 2))))
            .ToArray();
        var path = Path.Combine(_directory, "large-animation.gif");
        File.WriteAllBytes(path, GifWriter.Write(1000, 800, new[] { (200, 40, 40), (40, 40, 200) }, frames));
        var size = new FileInfo(path).Length;
        var result = provider.Render(path, 256, Balanced);
        AssertColor(result.Pixel(128, 100), (200, 40, 40), "frame 0 colour");
        Assert.True(result.Diagnostics.BytesRead < (ulong)size / 4,
                    $"read {result.Diagnostics.BytesRead} of {size} bytes for frame 0");
    }

    // ------------------------------------------------------------ status line & modes --------

    [Fact]
    public void Status_reports_the_registered_provider_for_portable_and_installed_layouts()
    {
        var settings = ThumbnailSettings.Default;
        const string installed = @"C:\Program Files\Glide\Glide.ShellThumbnail.dll";
        const string portable = @"D:\Portable\Glide\Glide.ShellThumbnail.dll";

        // Standalone/portable Glide.exe without its own DLL, installed provider registered:
        // this used to say "Provider: missing | Registered (this user): yes".
        var standalone = new ThumbnailProviderFacts(portable, false, null, installed, installed, true, "4.8.0", 150, "4.8.0");
        var text = ThumbnailProviderStatus.Describe(standalone, settings);
        Assert.StartsWith("Provider: active 4.8.0 (" + installed + ")", text);
        Assert.Contains("this Glide.exe has no provider of its own", text);
        Assert.DoesNotContain("missing", text);

        // Installed copy describing itself.
        var self = new ThumbnailProviderFacts(installed, true, "4.8.0", installed, installed, true, "4.8.0", 150, "4.8.0");
        var selfText = ThumbnailProviderStatus.Describe(self, settings);
        Assert.StartsWith("Provider: active 4.8.0 (" + installed + ") |", selfText);
        Assert.Contains("Registered: this user yes, all users yes", selfText);

        // Per-user registration wins over machine-wide, and an old unversioned DLL is called out.
        var older = new ThumbnailProviderFacts(portable, true, "4.8.0", installed, null, true, null, 12, "4.8.0");
        Assert.Contains("unversioned, pre-5.0", ThumbnailProviderStatus.Describe(older, settings));
        Assert.Contains("a different Glide copy", ThumbnailProviderStatus.Describe(older, settings));

        // Registered path that no longer exists is the only "missing".
        var stale = new ThumbnailProviderFacts(portable, true, "4.8.0", @"E:\gone\Glide.ShellThumbnail.dll", null, false, null, 0, "4.8.0");
        Assert.StartsWith("Provider: missing (registered path E:\\gone", ThumbnailProviderStatus.Describe(stale, settings));

        var none = new ThumbnailProviderFacts(portable, true, "4.8.0", null, null, false, null, 0, "4.8.0");
        Assert.StartsWith("Provider: available, not registered", ThumbnailProviderStatus.Describe(none, settings));
    }

    [Fact]
    public void Custom_mode_never_registers_an_empty_set()
    {
        IReadOnlyList<string> Recommended() => new[] { ".tga", ".webp" };
        var saved = ThumbnailSettingsStore.ResolveCustomExtensions(new[] { ".qoi", ".nope" }, () => new[] { ".tga" }, Recommended, out _);
        Assert.Equal(new[] { ".qoi" }, saved);

        var current = ThumbnailSettingsStore.ResolveCustomExtensions(null, () => new[] { ".tga", ".svg" }, Recommended, out var note);
        Assert.Equal(new[] { ".tga", ".svg" }, current);
        Assert.Contains("currently routed", note);

        var fallback = ThumbnailSettingsStore.ResolveCustomExtensions(null, Array.Empty<string>, Recommended, out note);
        Assert.Equal(Recommended(), fallback);
        Assert.Contains("Recommended", note);
    }

    // ---------------------------------------------------------------------- helpers ----------

    private static byte[] Fill(int count, byte value)
    {
        var data = new byte[count];
        Array.Fill(data, value);
        return data;
    }

    private static void AssertColor((int R, int G, int B) actual, (int R, int G, int B) expected, string what)
    {
        var close = Math.Abs(actual.R - expected.R) <= 12 && Math.Abs(actual.G - expected.G) <= 12 && Math.Abs(actual.B - expected.B) <= 12;
        Assert.True(close, $"{what}: expected {expected}, got {actual}");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static double MeanAbsoluteDifference(Rendered a, Rendered b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        for (var i = 0; i < a.Bgra.Length; i += 4)
            sum += Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
        return sum / (a.Bgra.Length / 4 * 3);
    }

    /// <summary>Fine concentric rings plus gradients: filter quality and aliasing show up clearly.</summary>
    private static byte[] ZonePlate(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        double cx = width / 2.0, cy = height / 2.0, scale = Math.Max(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r2 = ((x - cx) * (x - cx) + (y - cy) * (y - cy)) / scale;
                var i = (y * width + x) * 4;
                pixels[i + 0] = (byte)(y * 255 / height);
                pixels[i + 1] = (byte)(x * 255 / width);
                pixels[i + 2] = (byte)((Math.Sin(r2 * 0.9) * 0.5 + 0.5) * 255);
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    private static Bitmap ZonePlateBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = ZonePlate(width, height);
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private string WriteBitmap(string name, int width, int height, ImageFormat format)
    {
        var path = Path.Combine(_directory, name);
        using var bitmap = ZonePlateBitmap(width, height);
        if (format.Equals(ImageFormat.Bmp))
        {
            using var rgb = bitmap.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format24bppRgb);
            rgb.Save(path, format);
        }
        else
        {
            bitmap.Save(path, format);
        }
        return path;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }

    /// <summary>JPEG with an Exif APP1 carrying IFD0 Orientation and an IFD1 JPEG thumbnail.</summary>
    private string WriteExifJpeg(string name, int width, int height, int thumbnailEdge, int orientation)
    {
        using var main = ZonePlateBitmap(width, height);
        var ratio = Math.Min((double)thumbnailEdge / width, (double)thumbnailEdge / height);
        using var thumb = new Bitmap(main, Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)));
        var mainJpeg = EncodeJpeg(main);
        var thumbJpeg = EncodeJpeg(thumb);

        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(long v) { for (var s = 0; s < 32; s += 8) tiff.Add((byte)(v >> s)); }
        tiff.AddRange("II*\0"u8.ToArray());
        U32(8);
        const int ifd1Offset = 8 + 2 + 12 + 4;
        U16(1); U16(0x0112); U16(3); U32(1); U16(orientation); U16(0); U32(ifd1Offset);
        const int thumbOffset = ifd1Offset + 2 + 3 * 12 + 4;
        U16(3);
        U16(0x0103); U16(3); U32(1); U16(6); U16(0);
        U16(0x0201); U16(4); U32(1); U32(thumbOffset);
        U16(0x0202); U16(4); U32(1); U32(thumbJpeg.Length);
        U32(0);
        tiff.AddRange(thumbJpeg);
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segmentLength = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(segmentLength >> 8), (byte)segmentLength }.Concat(payload);

        // Drop GDI+'s JFIF APP0 so the Exif segment comes first, as in camera files.
        var body = mainJpeg.AsEnumerable().Skip(2);
        if (mainJpeg[2] == 0xFF && mainJpeg[3] == 0xE0)
            body = mainJpeg.Skip(4 + ((mainJpeg[4] << 8) | mainJpeg[5]));
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8 }.Concat(app1).Concat(body).ToArray());
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Glide.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    // --------------------------------------------------------------------- GIF writer --------

    /// <summary>Minimal GIF89a writer (literal-only LZW) for frames with offsets and transparency.</summary>
    private static class GifWriter
    {
        public sealed record Frame(int Left, int Top, int Width, int Height, byte[] Indices, int? Transparent = null, int Disposal = 1);

        public static byte[] Write(int width, int height, (int R, int G, int B)[] palette, params Frame[] frames)
        {
            var data = new List<byte>();
            data.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
            void U16(int v) { data.Add((byte)v); data.Add((byte)(v >> 8)); }
            U16(width); U16(height);
            data.Add(0xF7); data.Add(0); data.Add(0);
            for (var i = 0; i < 256; i++)
            {
                var c = i < palette.Length ? palette[i] : (0, 0, 0);
                data.Add((byte)c.Item1); data.Add((byte)c.Item2); data.Add((byte)c.Item3);
            }
            data.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
            data.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            data.AddRange(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
            foreach (var frame in frames)
            {
                data.AddRange(new byte[] { 0x21, 0xF9, 0x04 });
                data.Add((byte)((frame.Disposal << 2) | (frame.Transparent.HasValue ? 1 : 0)));
                U16(10);
                data.Add((byte)(frame.Transparent ?? 0));
                data.Add(0);
                data.Add(0x2C);
                U16(frame.Left); U16(frame.Top); U16(frame.Width); U16(frame.Height);
                data.Add(0);
                data.Add(8);
                var stream = Lzw(frame.Indices);
                for (var start = 0; start < stream.Count; start += 255)
                {
                    var count = Math.Min(255, stream.Count - start);
                    data.Add((byte)count);
                    data.AddRange(stream.GetRange(start, count));
                }
                data.Add(0);
            }
            data.Add(0x3B);
            return data.ToArray();
        }

        private static List<byte> Lzw(byte[] indices)
        {
            var output = new List<byte>(indices.Length * 9 / 8 + 16);
            int buffer = 0, bits = 0, run = 0;
            void Emit(int code)
            {
                buffer |= code << bits;
                bits += 9;
                while (bits >= 8) { output.Add((byte)buffer); buffer >>= 8; bits -= 8; }
            }
            Emit(256);
            foreach (var value in indices)
            {
                if (run == 250) { Emit(256); run = 0; }
                Emit(value);
                run++;
            }
            Emit(257);
            if (bits > 0) output.Add((byte)buffer);
            return output;
        }
    }

    // ---------------------------------------------------------------- provider host ----------

    internal readonly record struct Diagnostics(int Quality, bool Embedded, uint DecodedWidth, uint DecodedHeight, ulong BytesRead, string Decoder);

    internal sealed record Rendered(int Width, int Height, byte[] Bgra, Diagnostics Diagnostics)
    {
        public (int R, int G, int B) Pixel(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        }
    }

    /// <summary>Loads the freshly built provider DLL into the test process and calls it like Explorer.</summary>
    internal sealed class Provider
    {
        private static readonly Lazy<Provider?> Loaded = new(Load);
        public static Provider? Instance => Loaded.IsValueCreated ? Loaded.Value : null;

        private readonly IntPtr _module;
        private readonly SetOverridesFn _setOverrides;
        private readonly GetDiagnosticsFn _getDiagnostics;
        private readonly DescribeQualityFn _describeQuality;
        private readonly IClassFactory _factory;

        public static Provider Require()
            => Loaded.Value ?? throw new InvalidOperationException(
                "Glide.ShellThumbnail.dll was not found next to the tests or under native\\Glide.ShellThumbnail\\build\\Release. Run build.cmd (it builds the provider before the tests).");

        private static Provider? Load()
        {
            if (!OperatingSystem.IsWindows()) return null;
            var candidates = new[]
            {
                // Lets a scratch native build be tested without copying it over the build output.
                Environment.GetEnvironmentVariable("GLIDE_TEST_SHELLTHUMBNAIL_DLL") ?? string.Empty,
                Path.Combine(AppContext.BaseDirectory, "Glide.ShellThumbnail.dll"),
                Path.Combine(FindRepositoryRoot(), "native", "Glide.ShellThumbnail", "build", "Release", "Glide.ShellThumbnail.dll"),
            };
            var path = candidates.FirstOrDefault(File.Exists);
            return path is null ? null : new Provider(path);
        }

        private Provider(string path)
        {
            _ = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED; WIC needs COM on this thread
            _module = NativeLibrary.Load(path);
            _setOverrides = Marshal.GetDelegateForFunctionPointer<SetOverridesFn>(NativeLibrary.GetExport(_module, "GlideThumbnailSetTestOverrides"));
            _getDiagnostics = Marshal.GetDelegateForFunctionPointer<GetDiagnosticsFn>(NativeLibrary.GetExport(_module, "GlideThumbnailGetLastDiagnostics"));
            _describeQuality = Marshal.GetDelegateForFunctionPointer<DescribeQualityFn>(NativeLibrary.GetExport(_module, "GlideThumbnailDescribeQuality"));
            var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectFn>(NativeLibrary.GetExport(_module, "DllGetClassObject"));
            var clsid = new Guid(WindowsThumbnailRegistration.ProviderClsid);
            var iid = typeof(IClassFactory).GUID;
            Marshal.ThrowExceptionForHR(getClassObject(ref clsid, ref iid, out var factory));
            _factory = (IClassFactory)Marshal.GetObjectForIUnknown(factory);
            Marshal.Release(factory);
        }

        public void ClearOverrides() => _setOverrides(-1, 0, 1);

        /// <summary>True when the loaded provider exports <paramref name="name"/> (newer builds add test exports).</summary>
        public bool HasExport(string name) => NativeLibrary.TryGetExport(_module, name, out _);

        public int[] DescribeQuality(int quality)
        {
            var values = new int[6];
            Assert.Equal(6, _describeQuality(quality, values, values.Length));
            return values;
        }

        public double Time(string path, int edge, int quality)
        {
            var watch = Stopwatch.StartNew();
            Render(path, edge, quality);
            return watch.Elapsed.TotalMilliseconds;
        }

        public Rendered Render(string path, int edge, int quality, int maxSource = 0, bool preferEmbedded = true)
        {
            _ = CoInitializeEx(IntPtr.Zero, 0); // xUnit may run each test on a different thread
            _setOverrides(quality, (uint)maxSource, preferEmbedded ? 1 : 0);
            var iid = typeof(IThumbnailProvider).GUID;
            _factory.CreateInstance(IntPtr.Zero, ref iid, out var instance);
            IStream? stream = null;
            var bitmap = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x20, 0, false, IntPtr.Zero, out stream));
                ((IInitializeWithStream)instance).Initialize(stream, 0);
                ((IThumbnailProvider)instance).GetThumbnail((uint)edge, out bitmap, out _);
                Assert.NotEqual(IntPtr.Zero, bitmap);
                var section = new DIBSECTION();
                Assert.NotEqual(0, GetObject(bitmap, Marshal.SizeOf<DIBSECTION>(), ref section));
                var width = section.dsBm.bmWidth;
                var height = Math.Abs(section.dsBm.bmHeight);
                var pixels = new byte[width * height * 4];
                for (var y = 0; y < height; y++) // top-down DIB section, as created by the provider
                    Marshal.Copy(section.dsBm.bmBits + y * section.dsBm.bmWidthBytes, pixels, y * width * 4, width * 4);

                var raw = new RawDiagnostics { Size = (uint)Marshal.SizeOf<RawDiagnostics>() };
                Assert.Equal(1, _getDiagnostics(ref raw));
                var decoder = Encoding.ASCII.GetString(raw.Decoder).TrimEnd('\0');
                return new Rendered(width, height, pixels,
                    new Diagnostics(raw.Quality, raw.Embedded != 0, raw.DecodedWidth, raw.DecodedHeight, raw.BytesRead, decoder));
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (stream is not null) Marshal.ReleaseComObject(stream);
                Marshal.ReleaseComObject(instance);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetOverridesFn(int quality, uint maxSource, int preferEmbedded);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDiagnosticsFn(ref RawDiagnostics diagnostics);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DescribeQualityFn(int quality, [Out] int[] values, int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DllGetClassObjectFn(ref Guid clsid, ref Guid iid, out IntPtr factory);

        [StructLayout(LayoutKind.Sequential)]
        private struct RawDiagnostics
        {
            public uint Size;
            public int Quality;
            public int Embedded;
            public uint SourceWidth, SourceHeight, DecodedWidth, DecodedHeight, OutputWidth, OutputHeight;
            public ulong DecodeMicros, TotalMicros, BytesRead;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Decoder;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DIBSECTION
        {
            public BITMAP dsBm;
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
            public uint dsBitfields0, dsBitfields1, dsBitfields2; public IntPtr dshSection; public uint dsOffset;
        }

        [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IClassFactory
        {
            void CreateInstance(IntPtr outer, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            void LockServer(bool lockServer);
        }

        [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }

        [ComImport, Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IThumbnailProvider { void GetThumbnail(uint cx, out IntPtr bitmap, out uint alpha); }

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint init);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr handle, int size, ref DIBSECTION section);
    }
}

[CollectionDefinition("ShellThumbnailProvider", DisableParallelization = true)]
public sealed class ShellThumbnailProviderCollection { }
