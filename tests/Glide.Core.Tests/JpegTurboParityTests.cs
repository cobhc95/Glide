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
/// aspect ratio and at least half the requested size; otherwise WIC's scaled first view as before.
/// Fixtures\Jpeg\*.jpg are synthetic (tools/bench-507/make_jpeg_fixtures.py).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
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
        // No turbo first view: a plain JPEG's bounded decode stays on WIC (the 5.0.6 path).
        var p = Bounded("base420-2048x2000.jpg", 1024, 1024);
        Assert.True(p.W <= 1024 && p.H <= 1024 || p.W < 2048);
    }
}
