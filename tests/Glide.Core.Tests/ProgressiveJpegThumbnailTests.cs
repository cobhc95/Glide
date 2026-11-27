using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.1.7 second pass: an Explorer thumbnail of a progressive JPEG is made from its DC scan alone (libjpeg buffered-image mode, 1/8
/// scale, stops after the first scan) instead of entropy-decoding every scan. The hand-written libjpeg declarations
/// (GlideJpegLib.h) are only safe because the library checks the structure size and version; these tests also check that the
/// picture they produce is the right one (same as Skia's DCT-scaled decode of the same file), that anything unusual declines to
/// the previous route, and the thumbnail lane count rule.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ProgressiveJpegThumbnailTests
{
    public ProgressiveJpegThumbnailTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", name);

    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    private static double MeanDifference(Bitmap a, Bitmap b)
    {
        Assert.Equal(a.PixelSize, b.PixelSize);
        var pa = Pixels(a);
        var pb = Pixels(b);
        long total = 0;
        for (var i = 0; i < pa.Length; i++)
            if ((i & 3) != 3) total += Math.Abs(pa[i] - pb[i]);
        return total / (pa.Length * 0.75);
    }

    /// <summary>Largest difference between the mean colours of a 16 x 24 grid of cells: independent of the one-row size rounding of two decoders.</summary>
    private static double CellDifference(Bitmap a, Bitmap b)
    {
        const int cols = 16, rows = 24;
        double[] Means(Bitmap bitmap)
        {
            var size = bitmap.PixelSize;
            var pixels = Pixels(bitmap);
            var result = new double[cols * rows * 3];
            for (var cy = 0; cy < rows; cy++)
                for (var cx = 0; cx < cols; cx++)
                {
                    long r = 0, g = 0, bl = 0, n = 0;
                    for (var y = cy * size.Height / rows; y < (cy + 1) * size.Height / rows; y++)
                        for (var x = cx * size.Width / cols; x < (cx + 1) * size.Width / cols; x++)
                        {
                            var o = (y * size.Width + x) * 4;
                            bl += pixels[o]; g += pixels[o + 1]; r += pixels[o + 2]; n++;
                        }
                    var i = (cy * cols + cx) * 3;
                    result[i] = bl / (double)n; result[i + 1] = g / (double)n; result[i + 2] = r / (double)n;
                }
            return result;
        }
        var ma = Means(a);
        var mb = Means(b);
        return ma.Zip(mb, (x, y) => Math.Abs(x - y)).Max();
    }

    private static Bitmap SkiaScaled(string path, int width)
    {
        using var stream = File.OpenRead(path);
        return Bitmap.DecodeToWidth(stream, width);
    }

    [Fact]
    public void The_dc_scan_alone_gives_the_one_eighth_picture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Fixture("big-progressive420-4480x6720.jpg");

        Assert.True(NativeImageDecoder.TryDecodeProgressiveDcForTests(path, out var dc));
        using (dc)
        {
            Assert.Equal(new PixelSize(560, 840), dc!.PixelSize);
            using var reference = SkiaScaled(path, 560);
            var difference = MeanDifference(dc, reference);
            Assert.True(difference < 6, $"DC-only picture differs from Skia's 1/8 decode by {difference:F1} levels on average");
        }
    }

    [Theory]
    [InlineData(236)]
    [InlineData(152)]
    [InlineData(104)]
    public void An_explorer_thumbnail_of_a_progressive_jpeg_uses_the_dc_scan_and_matches_the_full_decode(int width)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Fixture("big-progressive420-4480x6720.jpg");

        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, width, out var thumbnail));
        using (thumbnail)
        {
            Assert.Equal(width, thumbnail!.PixelSize.Width);
            Assert.InRange(thumbnail.PixelSize.Height, width * 3 / 2 - 1, width * 3 / 2 + 1);
            using var reference = SkiaScaled(path, width);
            var difference = CellDifference(thumbnail, reference);
            Assert.True(difference < 8, $"thumbnail differs from Skia's by {difference:F1} levels per 16 x 24 cell");
        }
    }

    [Fact]
    public void A_baseline_jpeg_and_a_source_too_small_for_one_eighth_keep_the_one_shot_decode()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(NativeImageDecoder.TryDecodeProgressiveDcForTests(Fixture("big-base420-4480x6720.jpg"), out _));   // not progressive

        // 1001 x 667 progressive: 1/8 is 126 px wide, under a 236 px tile, so the DC scan is not enough: the one-shot scaled decode runs.
        var small = Fixture("progressive-1001x667.jpg");
        Assert.True(NativeImageDecoder.TryDecodeThumbnail(small, 236, out var thumbnail));
        using (thumbnail) Assert.Equal(new PixelSize(236, 157), thumbnail!.PixelSize);
    }

    [Fact]
    public void A_progressive_jpeg_cut_inside_its_dc_scan_is_declined_not_misdecoded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "glide-prog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = File.ReadAllBytes(Fixture("big-progressive420-4480x6720.jpg"));
            var cut = Path.Combine(directory, "cut.jpg");
            File.WriteAllBytes(cut, bytes[..3000]); // header and a sliver of the first scan
            Assert.False(NativeImageDecoder.TryDecodeProgressiveDcForTests(cut, out _));
            Assert.False(NativeImageDecoder.TryDecodeThumbnail(cut, 236, out _));

            var garbage = Path.Combine(directory, "garbage.jpg");
            File.WriteAllBytes(garbage, [0xFF, 0xD8, 0xFF, 0xC2, 0, 8, 8, 0, 8, 0, 8, 1, 1, 0x11, 0, 0xFF, 0xD9]);
            Assert.False(NativeImageDecoder.TryDecodeProgressiveDcForTests(garbage, out _));
        }
        finally { try { Directory.Delete(directory, true); } catch { /* temp */ } }
    }

    [Fact]
    public void The_thumbnail_lane_count_follows_the_hardware_tier_and_is_bounded()
    {
        Assert.Equal(12, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.HighEnd, 16));
        Assert.Equal(9, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.HighEnd, 12));
        Assert.Equal(12, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.HighEnd, 128));
        Assert.Equal(8, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.Mainstream, 16));
        Assert.Equal(4, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.Mainstream, 8));
        Assert.Equal(2, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.Constrained, 4));
        Assert.Equal(2, ImagePerformanceGovernor.ThumbnailLanesFor(GlideHardwareTier.Constrained, 1));
    }
}
