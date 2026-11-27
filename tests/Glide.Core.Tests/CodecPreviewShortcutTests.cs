using System.Runtime.InteropServices;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 preview shortcuts of the bundled codec provider (native/Glide.Codecs/GlideCodecs.cpp), reached through the provider's ordinary
/// preview call (DecodePreviewSurface with a longest side), which is what the viewer's reduced first frame and the Explorer thumbnails use:
///  - HEIC / AVIF: an embedded thumbnail item ('thmb') that is at least as large as the request and has the picture's aspect ratio is
///    decoded instead of the whole HEVC / AV1 picture;
///  - JPEG XL: a request of one eighth of the picture or less is served from the DC image (libjxl progression step), lossless / alpha /
///    non-progressive files still decode in full.
/// Fixtures\Codecs\heic-thumb.heic holds a 1600 x 1200 picture and a 320 x 240 thumbnail item with the INVERTED colours, so the test can see
/// which of the two answered; jxl-dc.jxl is a lossy VarDCT picture of the same scene (generate_thumbnail_fixtures.py).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class CodecPreviewShortcutTests : IDisposable
{
    private static readonly Lazy<CodecProviderRuntime> SharedRuntime = new(CodecProviderRuntime.CreateDefault);

    public CodecPreviewShortcutTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    public void Dispose() => CodecCapabilityRegistry.ClearExternalProviders();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codecs", name);

    private static ICodecSurfaceProvider Provider(string suffix)
    {
        var provider = SharedRuntime.Value.ResolveProvider("fixture" + suffix);
        Assert.NotNull(provider);
        return Assert.IsAssignableFrom<ICodecSurfaceProvider>(provider);
    }

    private static byte[] Copy(CodecPixelSurface s)
    {
        var buffer = new byte[s.Width * s.Height * 4];
        for (var y = 0; y < s.Height; y++) Marshal.Copy(s.Data + y * s.Stride, buffer, y * s.Width * 4, s.Width * 4);
        return buffer;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (var i = 0; i < a.Length; i++) if ((i & 3) != 3) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / (a.Length / 4 * 3);
    }

    /// <summary>Mean BGR of the whole picture: the scene's gradients average to a mid colour, so the inverted thumbnail lands on the opposite side.</summary>
    private static (double B, double G, double R) Mean(byte[] p)
    {
        double b = 0, g = 0, r = 0;
        for (var i = 0; i < p.Length; i += 4) { b += p[i]; g += p[i + 1]; r += p[i + 2]; }
        var n = p.Length / 4.0;
        return (b / n, g / n, r / n);
    }

    [Fact]
    public async Task Heic_PreviewThatTheThumbnailCovers_IsServedFromTheThumbnailItem()
    {
        var provider = Provider(".heic");
        var path = Fixture("heic-thumb.heic");
        using var main = await provider.DecodeFullSurfaceAsync(path);
        Assert.NotNull(main);
        Assert.Equal((1600, 1200), (main!.Width, main.Height));
        var mainPixels = Copy(main);

        // 240 and 320 are covered by the 320 x 240 thumbnail item (inverted colours): the result is the thumbnail, not the main picture
        foreach (var side in new[] { 200, 320 })
        {
            using var preview = await provider.DecodePreviewSurfaceAsync(path, side);
            Assert.NotNull(preview);
            Assert.Equal(side, Math.Max(preview!.Width, preview.Height));
            var (mb, mg, mr) = Mean(Copy(preview));
            var (b, g, r) = Mean(mainPixels);
            Assert.True(Math.Abs(mb + b - 255) < 12 && Math.Abs(mg + g - 255) < 12 && Math.Abs(mr + r - 255) < 12,
                $"preview {side}: expected the inverted thumbnail, got mean {mb:F0},{mg:F0},{mr:F0} against the picture's {b:F0},{g:F0},{r:F0}");
        }

        // 321 and more is not covered: the main picture, averaged down
        foreach (var side in new[] { 321, 480, 800 })
        {
            using var preview = await provider.DecodePreviewSurfaceAsync(path, side);
            Assert.NotNull(preview);
            Assert.Equal(side, Math.Max(preview!.Width, preview.Height));
            var (mb, mg, mr) = Mean(Copy(preview));
            var (b, g, r) = Mean(mainPixels);
            Assert.True(Math.Abs(mb - b) < 6 && Math.Abs(mg - g) < 6 && Math.Abs(mr - r) < 6, $"preview {side} should come from the main picture");
        }
    }

    [Fact]
    public async Task Heic_WithoutAThumbnailItem_DecodesTheMainPictureAsBefore()
    {
        var provider = Provider(".heic");
        var path = Fixture("basic.heic"); // 32 x 16, no thumbnail item
        using var full = await provider.DecodeFullSurfaceAsync(path);
        using var preview = await provider.DecodePreviewSurfaceAsync(path, 8);
        Assert.NotNull(full);
        Assert.NotNull(preview);
        Assert.Equal((8, 4), (preview!.Width, preview.Height));
    }

    [Fact]
    public async Task Jxl_SmallPreview_ComesFromTheDcImageAndMatchesTheAveragedPicture()
    {
        var provider = Provider(".jxl");
        var path = Fixture("jxl-dc.jxl");
        using var full = await provider.DecodeFullSurfaceAsync(path);
        Assert.NotNull(full);
        Assert.Equal((1600, 1200), (full!.Width, full.Height));
        var fullPixels = Copy(full);
        foreach (var side in new[] { 100, 160, 200 }) // 1600 / 8 = 200: the DC image is exactly this big
        {
            using var preview = await provider.DecodePreviewSurfaceAsync(path, side);
            Assert.NotNull(preview);
            Assert.Equal(side, Math.Max(preview!.Width, preview.Height));
            Assert.Equal(side * 3 / 4, preview.Height);
            var reference = AreaAverage(fullPixels, full.Width, full.Height, preview.Width, preview.Height);
            Assert.True(MeanDifference(reference, Copy(preview)) < 2.0, $"DC preview {side} differs from the averaged picture");
        }
        // above a eighth the picture decodes in full (the exact result of the former route)
        using var big = await provider.DecodePreviewSurfaceAsync(path, 320);
        Assert.NotNull(big);
        Assert.Equal((320, 240), (big!.Width, big.Height));
        Assert.True(MeanDifference(AreaAverage(fullPixels, full.Width, full.Height, 320, 240), Copy(big)) < 1.0);
    }

    [Theory]
    [InlineData("lossy.jxl")]   // 32 x 16, far too small for a DC step to matter
    [InlineData("alpha.jxl")]
    [InlineData("rotated.jxl")]
    public async Task Jxl_OtherFiles_KeepTheirOutputForAnyPreviewSize(string name)
    {
        var provider = Provider(".jxl");
        var path = Fixture(name);
        using var full = await provider.DecodeFullSurfaceAsync(path);
        Assert.NotNull(full);
        foreach (var side in new[] { 2, 4, 8, 64 })
        {
            using var preview = await provider.DecodePreviewSurfaceAsync(path, side);
            Assert.NotNull(preview);
            Assert.True(preview!.Width > 0 && preview.Height > 0 && Math.Max(preview.Width, preview.Height) <= Math.Max(side, Math.Max(full!.Width, full.Height)));
        }
    }

    private static byte[] AreaAverage(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (var y = 0; y < dh; y++)
            for (var x = 0; x < dw; x++)
            {
                double a = x * fx, b = Math.Min(sw, (x + 1) * fx), c = y * fy, d = Math.Min(sh, (y + 1) * fy);
                var acc = new double[4];
                double wsum = 0;
                for (var yy = (int)c; yy < Math.Ceiling(d); yy++)
                {
                    var wy = Math.Min(d, yy + 1) - Math.Max(c, yy);
                    for (var xx = (int)a; xx < Math.Ceiling(b); xx++)
                    {
                        var wx = Math.Min(b, xx + 1) - Math.Max(a, xx);
                        var wgt = wx * wy;
                        for (var ch = 0; ch < 4; ch++) acc[ch] += src[(Math.Min(yy, sh - 1) * sw + Math.Min(xx, sw - 1)) * 4 + ch] * wgt;
                        wsum += wgt;
                    }
                }
                for (var ch = 0; ch < 4; ch++) dst[(y * dw + x) * 4 + ch] = (byte)Math.Min(255, acc[ch] / wsum + 0.5);
            }
        return dst;
    }
}
