using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 native TIFF reader (native/Glide.Native/GlideTiff.cpp): parallel strips and tiles with its own inflate / LZW / PackBits, the
/// horizontal predictor, 8 and 16 bit gray / RGB / RGBA / CMYK in either byte order, classic TIFF and BigTIFF, and the reduced-resolution
/// pyramid (SubIFD levels and reduced pages). WIC is the reference for every classic file it can read; a classic twin with the same pixels
/// stands in for the BigTIFF and pyramid files. Fixtures\Tiff\*.tif come from Fixtures\Tiff\generate_tiff_fixtures.py (a minimal writer, so
/// every structure under test is written deliberately).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class NativeTiffReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tiff", name);

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

    private static (int Max, double Mean) Difference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var max = 0;
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            sum += d;
        }
        return (max, a.Length == 0 ? 0 : (double)sum / a.Length);
    }

    private static Bitmap Wic(string name)
    {
        Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(Fixture(name), out var bitmap), "WIC could not decode " + name);
        return bitmap;
    }

    private static Bitmap Own(string name, int maxWidth = 0, int maxHeight = 0)
    {
        Assert.True(NativeImageDecoder.TryDecodeTiffOwnForTests(Fixture(name), maxWidth, maxHeight, out var bitmap), "the native TIFF reader declined " + name);
        return bitmap!;
    }

    /// <summary>The 2x2-box-reduced frame of a BGRA picture, rounding like the native reader (n / 2 added before the division).</summary>
    private static (byte[] Pixels, int Width, int Height) BoxReduce(byte[] src, int w, int h, int k)
    {
        var ow = (w + k - 1) / k;
        var oh = (h + k - 1) / k;
        var dst = new byte[ow * oh * 4];
        for (var y = 0; y < oh; y++)
            for (var x = 0; x < ow; x++)
            {
                var sums = new int[4];
                var n = 0;
                for (var yy = y * k; yy < Math.Min(h, y * k + k); yy++)
                    for (var xx = x * k; xx < Math.Min(w, x * k + k); xx++)
                    {
                        for (var c = 0; c < 4; c++) sums[c] += src[(yy * w + xx) * 4 + c];
                        n++;
                    }
                for (var c = 0; c < 4; c++) dst[(y * ow + x) * 4 + c] = (byte)((sums[c] + n / 2) / n);
            }
        return (dst, ow, oh);
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Pixel exactness against WIC

    [Theory]
    [InlineData("tiled-deflate-256.tif")]       // partial tiles at the right and bottom edge
    [InlineData("tiled-lzw-pred2-128.tif")]
    [InlineData("tiled-packbits-64.tif")]
    [InlineData("tiled-none-96x32.tif")]        // uncompressed, non-square tiles
    [InlineData("tiled-gray-deflate.tif")]
    [InlineData("tiled-graywhite-deflate.tif")] // WhiteIsZero
    [InlineData("tiled-rgb16-deflate-pred2.tif")]
    [InlineData("tiled-rgb16-be.tif")]          // big endian, 16 bit, predictor
    [InlineData("tiled-rgb-be-lzw.tif")]        // big endian, LZW, predictor
    [InlineData("tiled-rgba-unassoc.tif")]
    [InlineData("tiled-rgba-assoc.tif")]
    [InlineData("tiled-orient6.tif")]           // EXIF orientation applied to the whole frame
    [InlineData("strips-lzw-twin.tif")]         // odd rows per strip (37), predictor
    [InlineData("pyramid-twin-classic.tif")]
    public void FullDecode_IsPixelIdenticalToWic(string name)
    {
        using var wic = Wic(name);
        using var own = Own(name);
        Assert.Equal(wic.PixelSize, own.PixelSize);
        var (max, _) = Difference(Pixels(wic), Pixels(own));
        Assert.True(max <= 1, $"{name}: max channel difference {max}");
    }

    [Theory]
    [InlineData("tiled-cmyk.tif")]
    [InlineData("strips-cmyk.tif")]
    public void CmykFullDecode_FollowsTheColoursWindowsShows(string name)
    {
        // WIC converts CMYK through its ICC managed converter; the native reader samples that converter into a lattice and interpolates.
        using var wic = Wic(name);
        using var own = Own(name);
        Assert.Equal(wic.PixelSize, own.PixelSize);
        var (max, mean) = Difference(Pixels(wic), Pixels(own));
        Assert.True(mean <= 0.6 && max <= 12, $"{name}: mean {mean:F3}, max {max}");
    }

    [Theory]
    [InlineData("pyramid-subifd.tif")]
    [InlineData("pyramid-pages.tif")]
    public void PyramidFile_FullDecodeIsTheFirstImage(string name)
    {
        using var twin = Wic("pyramid-twin-classic.tif");
        using var own = Own(name);
        Assert.Equal(twin.PixelSize, own.PixelSize);
        Assert.Equal(Pixels(twin), Pixels(own));
    }

    [Fact]
    public void DirectFullDecode_OfATiledFile_IsPixelIdenticalToTheBufferedWicPath()
    {
        var path = Fixture("tiled-deflate-256.tif");
        Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var buffered));
        using (buffered)
        {
            if (!NativeImageDecoder.TryDecodeFullDirectForTests(path, out var direct))
            {
                Assert.NotEqual("1", Environment.GetEnvironmentVariable("GLIDE_EXPECT_DIRECT_DECODE"));
                return;
            }
            using (direct)
            {
                Assert.Equal(buffered.PixelSize, direct.PixelSize);
                Assert.Equal(Pixels(buffered), Pixels(direct));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // BigTIFF

    [Theory]
    [InlineData("bigtiff-strips-lzw-be.tif", "strips-lzw-twin.tif")]
    [InlineData("bigtiff-tiled-deflate.tif", "tiled-deflate-256.tif")]
    [InlineData("bigtiff-gray16-deflate.tif", "bigtiff-twin-gray16.tif")]
    public void BigTiff_OpensAndMatchesItsClassicTwin(string bigName, string twinName)
    {
        // WIC cannot open a BigTIFF at all; the full decode goes through GlideDecodeImageIntoW's native reader.
        using var twin = Wic(twinName);
        Assert.False(NativeImageDecoder.TryDecodeFullBufferedForTests(Fixture(bigName), out var viaWic), "WIC unexpectedly decoded a BigTIFF");
        viaWic?.Dispose();
        Assert.True(NativeImageDecoder.TryDecode(Fixture(bigName), 0, out var big), "the native reader should open " + bigName);
        using (big)
        {
            Assert.Equal(twin.PixelSize, big.PixelSize);
            Assert.Equal(Pixels(twin), Pixels(big));
        }
    }

    [Theory]
    [InlineData("bigtiff-strips-lzw-be.tif")]
    [InlineData("bigtiff-tiled-deflate.tif")]
    [InlineData("bigtiff-gray16-deflate.tif")]
    public void BigTiff_DimensionsAreProbedWithoutWic(string name)
    {
        Assert.True(ImageHeaderProbe.TryProbe(Fixture(name), out var header));
        Assert.Equal((1001, 667), (header.Width, header.Height));
        Assert.True(NativeImageProbe.TryProbe(Fixture(name), out var native));
        Assert.Equal((1001u, 667u, 1u), (native.Width, native.Height, native.FrameCount));
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Pyramids and reduced decodes

    [Theory]
    [InlineData("pyramid-subifd.tif")]
    [InlineData("pyramid-pages.tif")]
    public void Pyramid_ChoosesTheSmallestLevelThatCoversTheBox(string name)
    {
        // levels below the 1200 x 900 first image: 600 x 450, 300 x 225, 150 x 112
        foreach (var (box, level) in new[] { (400, (600, 450)), (200, (300, 225)), (100, (150, 112)), (590, (600, 450)) })
        {
            Assert.True(NativeImageDecoder.TryGetTiffPlan(Fixture(name), box, box, out var plan));
            Assert.Equal(3, plan.Levels);
            Assert.True(plan.PyramidLevel, $"{name} box {box}");
            Assert.Equal(level, (plan.LevelWidth, plan.LevelHeight));
            Assert.Equal(1, plan.Reduction);
        }
        // a box the first image only just covers, or that it does not need reducing for, never takes a level
        Assert.True(NativeImageDecoder.TryGetTiffPlan(Fixture(name), 800, 800, out var big));
        Assert.False(big.PyramidLevel);
        Assert.Equal((1200, 900), (big.LevelWidth, big.LevelHeight));
        Assert.True(NativeImageDecoder.TryGetTiffPlan(Fixture(name), 4000, 4000, out var whole));
        Assert.False(whole.PyramidLevel);
        Assert.Equal(1, whole.Reduction);
        Assert.True(NativeImageDecoder.TiffReducedLevelCovers(Fixture(name), 480, 480));
        Assert.False(NativeImageDecoder.TiffReducedLevelCovers(Fixture(name), 4000, 4000));
    }

    [Fact]
    public void FileWithoutAPyramid_BoxReducesAPowerOfTwoInsteadOfDecodingEverything()
    {
        Assert.True(NativeImageDecoder.TryGetTiffPlan(Fixture("pyramid-twin-classic.tif"), 100, 100, out var plan));
        Assert.False(plan.PyramidLevel);
        Assert.Equal(0, plan.Levels);
        Assert.True(plan.Tiled && plan.OwnReader);
        Assert.Equal(8, plan.Reduction); // 1200 / 8 = 150 >= 100, 1200 / 16 = 75 < 100
        Assert.False(NativeImageDecoder.TiffReducedLevelCovers(Fixture("pyramid-twin-classic.tif"), 480, 480));
        Assert.False(NativeImageDecoder.TryGetTiffPlan(Fixture("tiled-deflate-256.tif"), 0, 100, out _));
    }

    [Theory]
    [InlineData("pyramid-subifd.tif")]
    [InlineData("pyramid-pages.tif")]
    public void PyramidLevel_IsTheReducedPictureOfTheFirstImage(string name)
    {
        using var level = Own(name, 480, 480);
        Assert.Equal((600, 450), (level.PixelSize.Width, level.PixelSize.Height));
        using var twin = Wic("pyramid-twin-classic.tif");
        var (expected, w, h) = BoxReduce(Pixels(twin), twin.PixelSize.Width, twin.PixelSize.Height, 2);
        Assert.Equal((600, 450), (w, h));
        var (max, mean) = Difference(expected, Pixels(level)); // the fixture's levels were made with the same 2 x 2 box
        Assert.True(max <= 2 && mean < 0.2, $"level differs from the box-reduced first image: max {max}, mean {mean:F3}");
    }

    [Fact]
    public void ReducedDecode_OfAFileWithoutAPyramid_IsTheBoxReducedPicture()
    {
        using var full = Wic("tiled-deflate-256.tif");
        var fullPixels = Pixels(full);
        // 1001 x 667 into a 250 x 167 box: 1 / 4 would leave 166 rows, so the reduction is 2 (501 x 334)
        using var own = Own("tiled-deflate-256.tif", 250, 167);
        Assert.Equal((501, 334), (own.PixelSize.Width, own.PixelSize.Height));
        var (expected, _, _) = BoxReduce(fullPixels, full.PixelSize.Width, full.PixelSize.Height, 2);
        Assert.Equal(expected, Pixels(own));
        using var smaller = Own("tiled-deflate-256.tif", 120, 80);
        Assert.Equal((126, 84), (smaller.PixelSize.Width, smaller.PixelSize.Height)); // 1 / 8: 125 >= 120 columns, 83 >= 80 rows
        var (expected8, _, _) = BoxReduce(fullPixels, full.PixelSize.Width, full.PixelSize.Height, 8);
        Assert.Equal(expected8, Pixels(smaller));
    }

    [Fact]
    public void ReducedDecode_AppliesTheExifOrientation()
    {
        using var own = Own("tiled-orient6.tif", 150, 150);
        using var upright = Own("tiled-deflate-256.tif", 150, 150);
        Assert.Equal((upright.PixelSize.Height, upright.PixelSize.Width), (own.PixelSize.Width, own.PixelSize.Height));
        var a = Pixels(own);
        var b = Pixels(upright);
        var (w, h) = (own.PixelSize.Width, own.PixelSize.Height);
        var max = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                    max = Math.Max(max, Math.Abs(a[(y * w + x) * 4 + c] - b[((w - 1 - x) * h + y) * 4 + c])); // orientation 6 = rotate 90 clockwise: dest (x, y) = upright (y, w - 1 - x)
        Assert.True(max <= 1, "orientation 6 must rotate the reduced frame, max difference " + max);
    }

    [Theory]
    [InlineData("tiled-deflate-256.tif")]
    [InlineData("pyramid-subifd.tif")]
    [InlineData("bigtiff-tiled-deflate.tif")]
    [InlineData("tiled-cmyk.tif")]
    public void Thumbnail_IsCloseToTheAreaAverageOfTheFullPicture(string name)
    {
        Assert.True(NativeImageDecoder.TryDecodeThumbnail(Fixture(name), 236, out var thumb), "no thumbnail for " + name);
        using (thumb)
        {
            var (tw, th) = (thumb!.PixelSize.Width, thumb.PixelSize.Height);
            Assert.True(tw is >= 234 and <= 236, $"{name}: {tw} x {th}");
            var reference = name.StartsWith("bigtiff", StringComparison.Ordinal) ? "tiled-deflate-256.tif" : name.StartsWith("pyramid", StringComparison.Ordinal) ? "pyramid-twin-classic.tif" : name;
            using var full = Wic(reference);
            Assert.Equal((double)full.PixelSize.Width / full.PixelSize.Height, (double)tw / th, 2);
            // The tile is the picture box-reduced by a power of two (or the pyramid level standing for that), then averaged to the exact size:
            // rebuild exactly that from the WIC frame. (An exact one-step area average differs by the half pixel the ceil in the box width
            // adds, which is a large error on the fixture's 9 pixel stripes.)
            Assert.True(NativeImageDecoder.TryGetTiffPlan(Fixture(name), 236, 65535, out var plan));
            var k = plan.PyramidLevel ? 1200 / plan.LevelWidth * plan.Reduction : plan.Reduction; // a level is the picture reduced by 1200 / level width
            var (boxed, bw, bh) = BoxReduce(Pixels(full), full.PixelSize.Width, full.PixelSize.Height, k);
            var exact = AreaAverage(boxed, bw, bh, tw, th);
            var (max, mean) = Difference(exact, Pixels(thumb));
            Assert.True(mean <= 1.0, $"{name}: thumbnail differs from the box-reduced, averaged picture (mean {mean:F2}, max {max})");
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

    // ---------------------------------------------------------------------------------------------------------------------------
    // Hostile input

    [Theory]
    [InlineData("damaged-truncated.tif")]
    [InlineData("damaged-header-scribble.tif")]
    [InlineData("damaged-deflate-data.tif")]
    public void DamagedFile_NeverCrashesAndNeverReturnsABadFrame(string name)
    {
        foreach (var (w, h) in new[] { (0, 0), (200, 200), (4000, 4000) })
        {
            if (!NativeImageDecoder.TryDecodeTiffOwnForTests(Fixture(name), w, h, out var bitmap)) continue;
            using (bitmap)
                Assert.True(bitmap!.PixelSize.Width is > 0 and <= 1001 && bitmap.PixelSize.Height is > 0 and <= 667);
        }
        _ = NativeImageDecoder.TryDecode(Fixture(name), 0, out var viaBridge);
        viaBridge?.Dispose();
        _ = NativeImageDecoder.TryDecodeThumbnail(Fixture(name), 236, out var thumb);
        thumb?.Dispose();
    }

    [Fact]
    public void RandomlyCorruptedFiles_AreDeclinedOrDecodedCleanly()
    {
        var source = File.ReadAllBytes(Fixture("tiled-lzw-pred2-128.tif"));
        var bigSource = File.ReadAllBytes(Fixture("bigtiff-tiled-deflate.tif"));
        var dir = Path.Combine(Path.GetTempPath(), "glide-tiff-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var random = new Random(52);
            for (var round = 0; round < 60; round++)
            {
                var data = (byte[])(round % 2 == 0 ? source : bigSource).Clone();
                var edits = random.Next(1, 40);
                for (var i = 0; i < edits; i++)
                {
                    // half of the edits hit the header and the IFD area at the end of the file, half the strip / tile data
                    var at = random.Next(2) == 0 ? random.Next(0, 16) : data.Length - 1 - random.Next(0, Math.Min(1400, data.Length - 16));
                    if (random.Next(3) == 0) at = random.Next(16, data.Length);
                    data[at] = (byte)random.Next(256);
                }
                if (round % 7 == 0) data = data[..random.Next(8, data.Length)];
                var path = Path.Combine(dir, $"fuzz{round}.tif");
                File.WriteAllBytes(path, data);
                foreach (var (w, h) in new[] { (0, 0), (250, 200) })
                    if (NativeImageDecoder.TryDecodeTiffOwnForTests(path, w, h, out var bitmap))
                        using (bitmap) Assert.True(bitmap!.PixelSize.Width > 0 && bitmap.PixelSize.Height > 0);
            }
        }
        finally { TempDir.TryDelete(dir); }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // The viewer

    private static Glide.Imaging.ImagePerformancePolicy Policy(int previewBox) =>
        (ImagePerformancePolicy.ForProfile("Balanced") with
        {
            DecodedCacheMegabytes = 256,
            PreviewLongestSide = previewBox,
            PreviewMaxWidth = previewBox,
            PreviewMaxHeight = previewBox,
            PredictivePrefetch = false,
            RefinementDelayMs = 5
        }).Normalize();

    [Fact]
    public async Task Viewer_OpensABigTiffThatWicCannotRead()
    {
        using var twin = Wic("strips-lzw-twin.tif");
        using var loader = new ImageLoadCoordinator { Policy = Policy(4096) };
        var result = await loader.LoadForegroundAsync(Fixture("bigtiff-strips-lzw-be.tif"), loader.Policy);
        Assert.NotNull(result);
        Assert.False(result!.IsPreview);
        Assert.Equal(twin.PixelSize, result.Bitmap.PixelSize);
        Assert.Equal(Pixels(twin), Pixels(result.Bitmap));
        loader.ReleaseBitmap(result.Bitmap);
    }

    [Fact]
    public async Task Viewer_FirstViewOfAPyramidTiff_IsTheLevelThatCoversTheBox()
    {
        var path = Fixture("pyramid-subifd.tif");
        var policy = Policy(480);
        Assert.False(ImageLoadCoordinator.PrefersFullFirstFrame(path, new ImageDimensions(1200, 900), policy));
        Assert.True(ImageLoadCoordinator.PrefersFullFirstFrame(Fixture("pyramid-twin-classic.tif"), new ImageDimensions(1200, 900), policy));
        using var loader = new ImageLoadCoordinator { Policy = policy };
        var first = await loader.LoadForegroundAsync(path, policy);
        Assert.NotNull(first);
        Assert.True(first!.IsPreview);
        Assert.Equal("tiff-pyramid-level", first.DecodeRoute);
        Assert.Equal((600, 450), (first.Bitmap.PixelSize.Width, first.Bitmap.PixelSize.Height));
        Assert.Equal((1200, 900), (first.SourceWidth, first.SourceHeight));
        loader.ReleaseBitmap(first.Bitmap);
        // the full frame is still available on demand
        var full = await loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false);
        Assert.NotNull(full);
        Assert.Equal((1200, 900), (full!.Bitmap.PixelSize.Width, full.Bitmap.PixelSize.Height));
        loader.ReleaseBitmap(full.Bitmap);
    }

    [Fact]
    public async Task Viewer_FirstViewOfATiffWithoutAPyramid_StaysTheFullDecode()
    {
        var path = Fixture("tiled-deflate-256.tif");
        using var loader = new ImageLoadCoordinator { Policy = Policy(480) };
        var first = await loader.LoadForegroundAsync(path, loader.Policy);
        Assert.NotNull(first);
        Assert.False(first!.IsPreview);
        Assert.Equal((1001, 667), (first.Bitmap.PixelSize.Width, first.Bitmap.PixelSize.Height));
        loader.ReleaseBitmap(first.Bitmap);
    }
}
