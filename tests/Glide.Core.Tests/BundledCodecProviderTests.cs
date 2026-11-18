using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// End-to-end checks for the bundled optional codec provider (codecs\glidecodecs.dll, built by
/// tools\build-codecs.ps1): the generated index routes every suffix to it, the runtime hashes,
/// ABI-checks and loads it lazily, and the synthetic fixtures in Fixtures\Codecs (see
/// generate_codec_fixtures.py) decode with the right size, orientation, alpha and colours.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class BundledCodecProviderTests : IDisposable
{
    private const string ProviderId = "glide.codecs";
    private const int Tolerance = 10; // lossy AV1/HEVC/JXL round trips on 8x8 colour blocks

    // One runtime for the whole class, like the app's process-wide CodecProviderRuntime.Shared: the
    // provider DLL is hashed and loaded once and stays loaded (native modules are never unloaded).
    private static readonly Lazy<CodecProviderRuntime> SharedRuntime = new(CodecProviderRuntime.CreateDefault);

    private static readonly string[] BundledSuffixes =
    {
        ".avif", ".avifs", ".heic", ".heif", ".heics", ".heifs", ".hif", ".jxl",
        ".jp2", ".j2k", ".j2c", ".jpc", ".jpx", ".jpf", ".jpm", ".mj2"
    };

    // BGRA, premultiplied.
    private static readonly byte[] Red = { 0, 0, 255, 255 }, Green = { 0, 255, 0, 255 }, Blue = { 255, 0, 0, 255 },
        White = { 255, 255, 255, 255 }, HalfBlue = { 128, 0, 0, 128 };

    public BundledCodecProviderTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // Resolving the provider registers its suffixes in the process-wide capability registry. Undo that
    // after each test so registry-shape tests elsewhere (e.g. "some modular suffix is still unloaded")
    // see a fresh process. This collection never runs in parallel with other collections.
    public void Dispose() => CodecCapabilityRegistry.ClearExternalProviders();

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codecs");
    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    private static ICodecSurfaceProvider Provider(string suffix)
    {
        var provider = SharedRuntime.Value.ResolveProvider("fixture" + suffix);
        Assert.NotNull(provider);
        Assert.Equal(ProviderId, provider!.Info.Id);
        return Assert.IsAssignableFrom<ICodecSurfaceProvider>(provider);
    }

    public static IEnumerable<object[]> Fixtures()
    {
        foreach (var name in new[]
        {
            "basic.avif", "10bit.avif", "anim.avifs",
            "basic.heic", "10bit.heic", "basic.heif", "basic.hif", "basic.heics", "basic.heifs",
            "basic.jxl", "lossy.jxl", "16bit.jxl",
            "basic.j2k", "basic.j2c", "basic.jpc", "basic.jp2", "16bit.jp2", "basic.jpx", "basic.jpf", "basic.jpm", "basic.mj2"
        })
            yield return new object[] { name, "quadrants" };
        foreach (var name in new[] { "rotated.avif", "rotated.heic", "rotated.jxl" })
            yield return new object[] { name, "rotated" };
        foreach (var name in new[] { "alpha.avif", "alpha.heic", "alpha.jxl", "alpha.jp2" })
            yield return new object[] { name, "alpha" };
    }

    public static IEnumerable<object[]> MalformedFixtures() =>
        new[] { "truncated.avif", "garbage.heic", "garbage.jxl", "truncated.j2k", "garbage.jp2" }.Select(x => new object[] { x });

    [Fact]
    public void Generated_index_routes_every_bundled_suffix_to_one_lazily_loaded_provider()
    {
        var runtime = SharedRuntime.Value;
        Assert.True(runtime.GeneratedIndexPresent, "codecs\\codecs.index.json was not copied next to the tests");
        Assert.Contains(runtime.Artifacts, a => a.Id == ProviderId);

        var first = Provider(".avif");
        Assert.Equal(CodecProviderAbi.CurrentVersion, first.Info.AbiVersion);
        Assert.True(first.Info.Preview && first.Info.Full);
        Assert.Equal(BundledSuffixes.OrderBy(x => x), first.Info.Extensions.OrderBy(x => x));
        foreach (var suffix in BundledSuffixes)
            Assert.Same(first, Provider(suffix));
        Assert.Contains(runtime.Artifacts, a => a.Id == ProviderId && a.AbiValid);

        // Every routed suffix has at least one real fixture exercised below.
        var fixtureSuffixes = Directory.EnumerateFiles(FixtureDirectory).Select(Path.GetExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(BundledSuffixes, suffix => Assert.Contains(suffix, fixtureSuffixes));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Fixture_decodes_with_expected_size_orientation_alpha_and_colours(string name, string layout)
    {
        var path = Fixture(name);
        var provider = Provider(Path.GetExtension(name));
        var (width, height) = layout == "rotated" ? (16, 32) : (32, 16);

        var probe = provider.Probe(path);
        Assert.True(probe.Supported, probe.Error);
        Assert.Equal(width, probe.Dimensions.Width);
        Assert.Equal(height, probe.Dimensions.Height);

        using (var surface = await provider.DecodeFullSurfaceAsync(path))
        {
            Assert.NotNull(surface);
            Assert.Equal(width, surface!.Width);
            Assert.Equal(height, surface.Height);
            Assert.True(surface.Stride >= width * 4);
            switch (layout)
            {
                case "quadrants":
                    AssertPixel(surface, 8, 4, Red, name); AssertPixel(surface, 24, 4, Green, name);
                    AssertPixel(surface, 8, 12, Blue, name); AssertPixel(surface, 24, 12, White, name);
                    break;
                case "rotated": // orientation 6: stored image rotated 90 degrees clockwise for display
                    AssertPixel(surface, 4, 8, Blue, name); AssertPixel(surface, 12, 8, Red, name);
                    AssertPixel(surface, 4, 24, White, name); AssertPixel(surface, 12, 24, Green, name);
                    break;
                default: // left opaque red, right blue at alpha 128 (premultiplied)
                    AssertPixel(surface, 8, 8, Red, name); AssertPixel(surface, 24, 8, HalfBlue, name);
                    break;
            }
        }

        // Preview honours the requested longest side and keeps the display orientation.
        using var preview = await provider.DecodePreviewSurfaceAsync(path, 8);
        Assert.NotNull(preview);
        Assert.Equal(layout == "rotated" ? 4 : 8, preview!.Width);
        Assert.Equal(layout == "rotated" ? 8 : 4, preview.Height);
    }

    [Fact]
    public async Task Jxl_icc_profile_is_colour_managed_to_srgb()
    {
        // Lossless (non-XYB) image tagged with a profile whose device R/G/B are sRGB green/blue/red.
        // Without colour management the stored quadrants (red/green/blue/white) would be shown as is.
        var provider = Provider(".jxl");
        using var surface = await provider.DecodeFullSurfaceAsync(Fixture("icc-permuted.jxl"));
        Assert.NotNull(surface);
        Assert.Equal((32, 16), (surface!.Width, surface.Height));
        AssertPixel(surface, 8, 4, Green, "icc TL");
        AssertPixel(surface, 24, 4, Blue, "icc TR");
        AssertPixel(surface, 8, 12, Red, "icc BL");
        AssertPixel(surface, 24, 12, White, "icc BR");
    }

    /// <summary>
    /// A minimal codestream whose packets are all empty (every wavelet coefficient decodes to zero, so the
    /// picture is mid grey) and whose components are subsampled by <paramref name="dx"/>.
    /// </summary>
    private static byte[] EmptyJ2k(int size, int[] dx)
    {
        var b = new List<byte> { 0xFF, 0x4F };
        void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        b.AddRange(new byte[] { 0xFF, 0x51 }); U16(38 + 3 * dx.Length);
        U16(0); U32(size); U32(size); U32(0); U32(0); U32(size); U32(size); U32(0); U32(0); U16(dx.Length);
        foreach (var d in dx) b.AddRange(new byte[] { 7, (byte)d, (byte)d });
        b.AddRange(new byte[] { 0xFF, 0x52 }); U16(12); b.AddRange(new byte[] { 0, 0 }); U16(1); b.AddRange(new byte[] { 0, 0, 4, 4, 0, 1 });
        b.AddRange(new byte[] { 0xFF, 0x5C }); U16(4); b.AddRange(new byte[] { 0x40, 0x40 });
        b.AddRange(new byte[] { 0xFF, 0x90 }); U16(10); U16(0); U32(0); b.AddRange(new byte[] { 0, 1 });
        b.AddRange(new byte[] { 0xFF, 0x93 });
        b.AddRange(new byte[dx.Length]); // one empty packet per component
        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }

    [Fact]
    public async Task J2k_with_every_component_subsampled_still_decodes_the_whole_reference_grid()
    {
        // 16x16 grid, components sampled every 4th pixel (4x4 samples each). The surface must be the
        // 16x16 grid the probe reports, not component 0's 4x4.
        var path = Path.Combine(Path.GetTempPath(), "glide-j2k-sub4-" + Guid.NewGuid().ToString("N") + ".j2k");
        File.WriteAllBytes(path, EmptyJ2k(16, new[] { 4, 4, 4 }));
        try
        {
            var provider = Provider(".j2k");
            var probe = provider.Probe(path);
            Assert.True(probe.Supported, probe.Error);
            Assert.Equal((16, 16), (probe.Dimensions.Width, probe.Dimensions.Height));
            using var surface = await provider.DecodeFullSurfaceAsync(path);
            Assert.NotNull(surface);
            Assert.Equal((16, 16), (surface!.Width, surface.Height));
            AssertPixel(surface, 0, 0, new byte[] { 128, 128, 128, 255 }, "sub4");
            AssertPixel(surface, 15, 15, new byte[] { 128, 128, 128, 255 }, "sub4");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(MalformedFixtures))]
    public async Task Malformed_file_fails_cleanly_and_the_provider_keeps_working(string name)
    {
        var provider = Provider(Path.GetExtension(name));
        var path = Fixture(name);

        _ = provider.Probe(path); // may report header dimensions for a truncated body; must not fault
        Assert.Null(await provider.DecodeFullSurfaceAsync(path));
        Assert.Null(await provider.DecodePreviewSurfaceAsync(path, 64));

        var missing = Path.Combine(Path.GetTempPath(), "glide-missing-" + Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        Assert.False(provider.Probe(missing).Supported);
        Assert.Null(await provider.DecodeFullSurfaceAsync(missing));

        using var good = await provider.DecodeFullSurfaceAsync(Fixture("basic.jxl"));
        Assert.NotNull(good);
        Assert.Equal(32, good!.Width);
    }

    [Fact]
    public void Decoder_backend_uses_the_lazy_runtime_and_returns_bitmaps()
    {
        var resolutions = 0;
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            using var backend = new CodecProviderDecoderBackend(lazyResolver: path => { resolutions++; return SharedRuntime.Value.ResolveProvider(path); });
            foreach (var name in new[] { "basic.avif", "basic.heic", "basic.jxl", "basic.jp2" })
            {
                var path = Fixture(name);
                using var stream = File.OpenRead(path);
                using var full = backend.DecodeFullAsync(stream, path).GetAwaiter().GetResult();
                Assert.Equal(new PixelSize(32, 16), full.PixelSize);
                var (b, g, r) = ReadPixel((WriteableBitmap)full, 24, 4);
                Assert.True(g > 200 && r < 40 && b < 40, $"{name}: expected green, got r={r} g={g} b={b}");

                stream.Position = 0;
                using var preview = backend.DecodePreviewAsync(stream, new ImageDimensions(32, 16), 16,
                    BitmapInterpolationMode.HighQuality, path).GetAwaiter().GetResult();
                Assert.Equal(new PixelSize(16, 8), preview.PixelSize);
            }
        });
        // The first suffix resolves the provider; registering it maps all of its suffixes, so the
        // other three formats never wake the resolver again.
        Assert.Equal(1, resolutions);
    }

    private static void AssertPixel(CodecPixelSurface surface, int x, int y, byte[] expected, string name)
    {
        var offset = y * surface.Stride + x * 4;
        for (var c = 0; c < 4; c++)
        {
            var actual = Marshal.ReadByte(surface.Data, offset + c);
            Assert.True(Math.Abs(actual - expected[c]) <= Tolerance,
                $"{name} ({x},{y}) channel {"BGRA"[c]}: expected {expected[c]}, got {actual}");
        }
    }

    private static (byte B, byte G, byte R) ReadPixel(WriteableBitmap bitmap, int x, int y)
    {
        using var frame = bitmap.Lock();
        var offset = y * frame.RowBytes + x * 4;
        return (Marshal.ReadByte(frame.Address, offset), Marshal.ReadByte(frame.Address, offset + 1), Marshal.ReadByte(frame.Address, offset + 2));
    }
}
