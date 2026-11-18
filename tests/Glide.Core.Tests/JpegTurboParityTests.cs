using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.7 JPEG path (native/Glide.Native/GlideJpeg.cpp).
/// Full resolution: libjpeg-turbo must match the Windows (WIC) decode it replaces within 1 LSB per channel for
/// every JPEG kind it accepts (baseline 4:2:0 / 4:2:2 / 4:4:4, grayscale, progressive, restart markers, odd
/// sizes, banded multi-threaded decode, EXIF orientations 2-8); CMYK and damaged files are declined and keep
/// the WIC result. Reference = GlideDecodeImageW(path, 0) (WIC only); tested = GlideDecodeImageIntoW (turbo first).
/// First view: the camera's MPF large preview is used only when present, a large-thumbnail entry, the same
/// aspect ratio and large enough to cover the request alone; otherwise a libjpeg-turbo DCT-scaled decode (1/2, 1/4,
/// 1/8) of the main image (progressive: only for the unstaged prefetch decode; the staged first view stays on WIC).
/// Fixtures\Jpeg\*.jpg are synthetic (tools/bench-507/make_jpeg_fixtures.py, make_large_jpeg_fixtures.py).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class JpegTurboParityTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", name);

    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new Avalonia.PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride); }
        finally { handle.Free(); }
        return buffer;
    }

    internal static int MaxDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var max = 0;
        for (var i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    [Theory]
    [InlineData("base420-odd-1001x667.jpg", true)]
    [InlineData("base420-2048x2000.jpg", true)]      // >= 4 MP: banded multi-threaded decode
    [InlineData("base420-odd-2051x1999.jpg", true)]  // banded, partial last iMCU row and column
    [InlineData("base444-1001x667.jpg", true)]
    [InlineData("base422-1003x667.jpg", true)]
    [InlineData("gray-1001x667.jpg", true)]
    [InlineData("gray-2048x2000.jpg", true)]         // banded grayscale
    [InlineData("progressive-1001x667.jpg", true)]   // progressive is never banded
    [InlineData("progressive444-1001x667.jpg", true)]
    [InlineData("restart-2048x2000.jpg", true)]
    [InlineData("restart-1001x667.jpg", true)]
    [InlineData("exif2-1001x667.jpg", true)]
    [InlineData("exif3-1001x667.jpg", true)]
    [InlineData("exif4-1001x667.jpg", true)]
    [InlineData("exif5-1001x667.jpg", true)]
    [InlineData("exif6-1001x667.jpg", true)]
    [InlineData("exif7-1001x667.jpg", true)]
    [InlineData("exif8-1001x667.jpg", true)]
    [InlineData("exif6-2048x2000.jpg", true)]        // banded + rotated into the surface
    [InlineData("cmyk-adobe-320x240.jpg", false)]    // declined: CMYK stays on WIC
    public void FullDecode_MatchesTheWindowsDecoderWithinOneLsb(string name, bool turboAccepts)
    {
        var path = Fixture(name);
        Assert.True(File.Exists(path), path);
        Assert.Equal(turboAccepts, NativeImageDecoder.JpegTurboAcceptsForTests(path));
        Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var wic));
        using (wic)
        {
            Assert.True(NativeImageDecoder.TryDecodeFullDirectForTests(path, out var direct));
            using (direct)
            {
                Assert.Equal(wic.PixelSize, direct.PixelSize);
                var max = MaxDifference(Pixels(wic), Pixels(direct));
                Assert.True(max <= 1, $"{name}: max channel difference {max}");
            }
        }
    }

    [Fact]
    public void TruncatedJpeg_KeepsTheWindowsDecoderOutcome()
    {
        // libjpeg-turbo stops on the warning; WIC then decodes exactly as before.
        var path = Fixture("truncated-1001x667.jpg");
        var wicOk = NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var wic);
        var directOk = NativeImageDecoder.TryDecodeFullDirectForTests(path, out var direct);
        try
        {
            Assert.Equal(wicOk, directOk);
            if (wicOk) Assert.Equal(Pixels(wic), Pixels(direct));
        }
        finally
        {
            if (wicOk) wic.Dispose();
            if (directOk) direct.Dispose();
        }
    }

    private static (int W, int H, byte B, byte G, byte R) Bounded(string name, int maxW, int maxH)
    {
        Assert.True(NativeImageDecoder.TryDecodeBounded(Fixture(name), maxW, maxH, out var preview));
        using (preview)
        {
            var px = Pixels(preview);
            var mid = (preview.PixelSize.Height / 2 * preview.PixelSize.Width + preview.PixelSize.Width / 2) * 4;
            return (preview.PixelSize.Width, preview.PixelSize.Height, px[mid], px[mid + 1], px[mid + 2]);
        }
    }

    private static bool IsRed((int W, int H, byte B, byte G, byte R) p) => p.R > 200 && p.G < 60 && p.B < 60;

    [Fact]
    public void FirstView_UsesTheCameraMpfPreviewWhenItIsLargeEnough()
    {
        var p = Bounded("mpf-present-2400x1600.jpg", 1200, 1200);
        Assert.True(IsRed(p), $"expected the red MPF preview, got {p}");
        Assert.Equal((1200, 800), (p.W, p.H));
    }

    [Theory]
    [InlineData("mpf-absent-2400x1600.jpg")]
    [InlineData("mpf-toosmall-2400x1600.jpg")]   // 240 x 160 < half of the requested size
    [InlineData("mpf-aspect-2400x1600.jpg")]     // different aspect ratio
    [InlineData("mpf-corrupt-2400x1600.jpg")]    // preview bytes are not a JPEG
    [InlineData("mpf-badoffset-2400x1600.jpg")]  // offset points outside the file
    [InlineData("mpf-gainmap-2400x1600.jpg")]    // undefined MP type (gain/depth map): never a picture
    public void FirstView_FallsBackToTheMainImageWithoutAUsablePreview(string name)
    {
        var p = Bounded(name, 1200, 1200);
        Assert.False(IsRed(p), $"{name}: the MPF preview must not be used, got {p}");
        Assert.True(p.W <= 2400 && p.H <= 1600 && p.W > 0);
    }

    [Fact]
    public void FirstView_WithoutMpf_IsTheWindowsScaledView()
    {
        // 5.1.1: baseline first view is back on WIC (the 5.0.7 path); only progressive prefetch uses DCT scaling.
        var p = Bounded("base420-2048x2000.jpg", 1024, 1024);
        Assert.True(p.W <= 1024 && p.H <= 1024 || p.W < 2048);
    }

    // Decision table: sizes are physical; need = the source fitted into the request box.
    [Theory]
    [InlineData(4480, 6720, 960u, 1440u, 0u, true, 2, 4)]      // no preview: 1/4 (1120 x 1680) is the smallest covering scale
    [InlineData(4480, 6720, 480u, 720u, 0u, true, 2, 8)]       // 1/8 = 560 x 840
    [InlineData(4480, 6720, 1600u, 2400u, 0u, true, 2, 2)]     // 1/4 = 1680 is short: 1/2
    [InlineData(4480, 6720, 3000u, 4500u, 0u, true, 2, 1)]     // needs more than half: full scale
    [InlineData(4480, 6720, 960u, 1440u, 1620u, true, 1, 4)]   // preview long side 1620 >= 1440: embedded (cheaper)
    [InlineData(4480, 6720, 960u, 1440u, 1440u, true, 1, 4)]   // exactly covers: embedded
    [InlineData(4480, 6720, 1600u, 2400u, 1620u, true, 2, 2)]  // preview too small for the request: sharper scaled decode
    [InlineData(4480, 6720, 960u, 1440u, 0u, false, 0, 4)]     // scaling not allowed (staged progressive): WIC
    [InlineData(4480, 6720, 4480u, 6720u, 1620u, true, 0, 1)]  // request covers the image: no reduced first view
    [InlineData(4001, 2999, 501u, 375u, 0u, true, 2, 8)]       // odd size: 1/8 rounds up to 501 x 375, covers
    public void FirstViewPlan_PicksSourceAndSmallestCoveringScale(int w, int h, uint needW, uint needH, uint preview, bool scaled, int choice, int denom)
    {
        var plan = NativeImageDecoder.JpegPlanFirstViewForTests(w, h, needW, needH, preview, scaled);
        Assert.Equal(choice, plan.Choice);
        Assert.Equal(denom, plan.ScaleDenom);
    }

    [Theory]
    [InlineData(4480, 6720, 1120, 1681, 2)] // one pixel over 1/4: 1/2
    [InlineData(1, 1, 1, 1, 8)]             // TJSCALED rounds up: 1/8 of 1 px is 1 px
    [InlineData(17, 9, 3, 2, 8)]            // ceil(17/8)=3, ceil(9/8)=2
    [InlineData(17, 9, 4, 2, 4)]
    public void ScaleDenom_IsTheSmallestPowerOfTwoScaleThatCovers(int w, int h, uint needW, uint needH, int denom)
        => Assert.Equal(denom, NativeImageDecoder.JpegPlanFirstViewForTests(w, h, needW, needH, 0, true).ScaleDenom);

    private static (int W, int H, byte[] Px) Decode(string name, int longest)
    {
        // GlideDecodeImageW: the unstaged bounded decode used by neighbour prefetch (and progressive files' scaled view).
        Assert.True(NativeImageDecoder.TryDecode(Fixture(name), longest, out var bitmap));
        using (bitmap) return (bitmap.PixelSize.Width, bitmap.PixelSize.Height, Pixels(bitmap));
    }

    private static bool RedAt((int W, int H, byte[] Px) d, int x, int y)
    {
        var i = (y * d.W + x) * 4;
        return d.Px[i + 2] > 200 && d.Px[i + 1] < 60 && d.Px[i] < 60;
    }

    [Theory]
    [InlineData("big-progressive420-4480x6720.jpg")]
    public void LargeJpeg_DecodesAtTheQuarterDctScale(string name)
    {
        // 30 MP, no EXIF, into 1440 x 1440: needs 960 x 1440, so 1/4 = 1120 x 1680. The red block is the stored top-left.
        var d = Decode(name, 1440);
        Assert.Equal((1120, 1680), (d.W, d.H));
        Assert.True(RedAt(d, 100, 100), "stored top-left marker must stay top-left");
        Assert.False(RedAt(d, 1000, 1600));
    }

    [Fact]
    public void LargeJpeg_ScaledDecodeAppliesExifOrientation()
    {
        // Orientation 6 (rotate 90 clockwise): stored top-left lands top-right, output is landscape.
        var d = Decode("big-exif6-4480x6720.jpg", 1440);
        Assert.True(d.W > d.H, $"expected landscape, got {d.W}x{d.H}"); // baseline: WIC scaled view since 5.1.1
        Assert.True(RedAt(d, d.W - 100, 100), "stored top-left must land top-right");
        Assert.False(RedAt(d, 100, 100));
        Assert.False(RedAt(d, d.W - 100, d.H - 100));
    }
}