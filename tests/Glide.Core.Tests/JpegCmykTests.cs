using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 CMYK / YCCK JPEG (native/Glide.Native/GlideJpeg.cpp + GlideCmyk.cpp). libjpeg-turbo decodes the file (DCT scaled, banded and
/// multi-threaded above 4 MP) and the colours come from a lattice sampled once from Windows' own CMYK converter, so the picture looks
/// exactly as WIC showed it, without WIC's single-threaded converter (340 of the 428 ms a 4K CMYK JPEG used to take).
/// Reference = GlideDecodeImageW(path, 0), which is WIC only.
/// Fixtures: cmyk-adobe-320x240 (Adobe CMYK, Pillow), cmyk-plain-1001x667 (CMYK, libjpeg-turbo), cmyk-ycck-* (YCCK 4:2:0 / progressive /
/// 4:4:4 banded; written with libjpeg-turbo from inverted ink samples like Photoshop does), Fixtures/Jpeg/generate_cmyk_fixtures.py and mkycck.cpp.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class JpegCmykTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", name);

    public static IEnumerable<object[]> CmykFiles() =>
        new[] { "cmyk-adobe-320x240.jpg", "cmyk-plain-1001x667.jpg", "cmyk-ycck-420-1001x667.jpg", "cmyk-ycck-progressive-1001x667.jpg", "cmyk-ycck-444-2048x2000.jpg" }
            .Select(x => new object[] { x });

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

    private static (int Max, double Mean, double P99) Difference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var histogram = new long[256];
        long sum = 0;
        var max = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if ((i & 3) == 3) continue; // alpha
            var d = Math.Abs(a[i] - b[i]);
            histogram[d]++;
            sum += d;
            max = Math.Max(max, d);
        }
        var count = a.Length / 4 * 3;
        long running = 0;
        var p99 = 0;
        for (; p99 < 256 && running < count * 0.99; p99++) running += histogram[p99];
        return (max, (double)sum / count, p99 - 1);
    }

    [Theory]
    [MemberData(nameof(CmykFiles))]
    public void FullDecode_FollowsTheColoursOfTheWindowsDecoder(string name)
    {
        var path = Fixture(name);
        Assert.True(File.Exists(path), path);
        Assert.True(NativeImageDecoder.JpegTurboAcceptsForTests(path), "libjpeg-turbo should take CMYK / YCCK now");
        Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var wic));
        using (wic)
        {
            Assert.True(NativeImageDecoder.TryDecodeFullDirectForTests(path, out var direct));
            using (direct)
            {
                Assert.Equal(wic.PixelSize, direct.PixelSize);
                var (max, mean, p99) = Difference(Pixels(wic), Pixels(direct));
                // the lattice interpolation differs from the converter it samples by a fraction of a level on average and a few levels at the edge of the gamut
                Assert.True(mean <= 0.6 && p99 <= 3 && max <= 12, $"{name}: mean {mean:F3}, p99 {p99}, max {max}");
            }
        }
    }

    [Theory]
    [InlineData("cmyk-plain-1001x667.jpg", 480)]
    [InlineData("cmyk-ycck-420-1001x667.jpg", 480)]
    [InlineData("cmyk-ycck-progressive-1001x667.jpg", 480)]
    [InlineData("cmyk-ycck-444-2048x2000.jpg", 1000)]
    public void FirstView_IsADctScaledDecodeThatMatchesTheAverageOfTheWindowsFrame(string name, int box)
    {
        var path = Fixture(name);
        Assert.True(NativeImageDecoder.TryDecodeBounded(path, box, box, out var view));
        using (view)
        {
            Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var wic));
            using (wic)
            {
                var (sw, sh) = (wic.PixelSize.Width, wic.PixelSize.Height);
                var scale = Math.Min(1.0, Math.Min((double)box / sw, (double)box / sh));
                Assert.True(view.PixelSize.Width >= Math.Floor(sw * scale) && view.PixelSize.Width <= Math.Ceiling(sw * scale) * 2,
                    $"{name}: {view.PixelSize.Width} x {view.PixelSize.Height} for a {box} box");
                // compare at the view's own size: an exact area average of the WIC frame
                var reference = AreaAverage(Pixels(wic), sw, sh, view.PixelSize.Width, view.PixelSize.Height);
                var (max, mean, _) = Difference(reference, Pixels(view));
                Assert.True(mean <= 2.0, $"{name}: first view differs from the reduced Windows frame (mean {mean:F2}, max {max})");
            }
        }
    }

    [Theory]
    [InlineData("cmyk-plain-1001x667.jpg")]
    [InlineData("cmyk-ycck-420-1001x667.jpg")]
    public void ExplorerThumbnail_NowComesFromTheNativeJpegPath(string name)
    {
        // before 5.2.0 the native thumbnail declined CMYK and the Explorer tab fell back to Skia
        Assert.True(NativeImageDecoder.TryDecodeJpegThumbnail(Fixture(name), 236, out var thumb));
        using (thumb)
        {
            Assert.InRange(thumb!.PixelSize.Width, 230, 240);
            Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(Fixture(name), out var wic));
            using (wic)
            {
                var reference = AreaAverage(Pixels(wic), wic.PixelSize.Width, wic.PixelSize.Height, thumb.PixelSize.Width, thumb.PixelSize.Height);
                var (max, mean, _) = Difference(reference, Pixels(thumb));
                Assert.True(mean <= 2.5, $"{name}: thumbnail mean difference {mean:F2} (max {max})");
            }
        }
    }

    [Fact]
    public void PlainRgbJpeg_StaysOnItsFormerPath()
    {
        // CMYK support must not change anything for the formats that were already on libjpeg-turbo
        var path = Fixture("base420-odd-1001x667.jpg");
        Assert.True(NativeImageDecoder.JpegTurboAcceptsForTests(path));
        Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var wic));
        using (wic)
        {
            Assert.True(NativeImageDecoder.TryDecodeFullDirectForTests(path, out var direct));
            using (direct) Assert.True(Difference(Pixels(wic), Pixels(direct)).Max <= 1);
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
