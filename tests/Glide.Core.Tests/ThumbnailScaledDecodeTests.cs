using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.App.Services;
using Glide.Imaging;
using Glide.Imaging.Encoders;
using Glide.Core;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>
/// The 5.1.7 reduced-size decodes behind the Explorer tab's thumbnails (Glide.Native <c>GlideDecodeThumbnailW</c>): TIFF and
/// BMP through parallel row bands with a box reduction (Skia cannot read TIFF at all), still WebP through libwebp's scaled decode,
/// and the viewer's WebP first view / full decode through the same libwebp. Pictures are smooth gradients (red grows with x, green with
/// y), so a correct reduction has a known colour at every thumbnail pixel.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailScaledDecodeTests : IDisposable
{
    private readonly TempDir _dir = new("glide-thumbscaled");

    public ThumbnailScaledDecodeTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        ThumbnailDiskCache.Configure(false, null, 0);
    }

    public void Dispose()
    {
        _dir.Dispose();
        CodecCapabilityRegistry.ClearExternalProviders(); // saving WebP resolves the bundled provider
    }

    private static Bitmap32 Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                pixels[o] = 128;                                  // B
                pixels[o + 1] = (byte)(y * 255 / (height - 1));   // G
                pixels[o + 2] = (byte)(x * 255 / (width - 1));    // R
                pixels[o + 3] = 255;
            }
        return new Bitmap32(width, height, pixels);
    }

    private sealed record Bitmap32(int Width, int Height, byte[] Bgra);

    /// <summary>A System.Drawing bitmap (24-bit) of the gradient, saved as TIFF or BMP.</summary>
    private string Save(string name, ImageFormat format, int width, int height)
    {
        var source = Gradient(width, height);
        using var bitmap = new System.Drawing.Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 4;
                    row[x * 3] = source.Bgra[o]; row[x * 3 + 1] = source.Bgra[o + 1]; row[x * 3 + 2] = source.Bgra[o + 2];
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally { bitmap.UnlockBits(data); }
        var path = _dir.Combine(name);
        bitmap.Save(path, format);
        return path;
    }

    private string SaveWebp(string name, int width, int height)
    {
        var source = Gradient(width, height);
        var path = _dir.Combine(name);
        ImageFileEncoder.Encode(SaveFormatCatalog.WebP, EncoderPixels.Premultiply(source.Bgra), width, height, path, new ImageEncodeOptions { Lossless = true });
        return path;
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    /// <summary>Width exact; height within a row (the banded route rounds to nearest, the WIC route floors).</summary>
    private static void AssertSize(Bitmap bitmap, int width, int expectedHeight)
    {
        Assert.Equal(width, bitmap.PixelSize.Width);
        Assert.InRange(bitmap.PixelSize.Height, expectedHeight - 1, expectedHeight);
    }

    /// <summary>Every thumbnail pixel must be close to the gradient colour at the matching source position.</summary>
    private static void AssertIsTheGradient(Bitmap bitmap, int tolerance, string label)
    {
        var size = bitmap.PixelSize;
        var pixels = Pixels(bitmap);
        var worst = 0;
        for (var y = 2; y < size.Height - 2; y += 3)
            for (var x = 2; x < size.Width - 2; x += 3)
            {
                var o = (y * size.Width + x) * 4;
                var expectedR = (x + 0.5) / size.Width * 255;
                var expectedG = (y + 0.5) / size.Height * 255;
                worst = Math.Max(worst, (int)Math.Max(Math.Abs(pixels[o + 2] - expectedR), Math.Max(Math.Abs(pixels[o + 1] - expectedG), Math.Abs(pixels[o] - 128))));
                Assert.Equal(255, pixels[o + 3]);
            }
        Assert.True(worst <= tolerance, $"{label}: worst channel error {worst} > {tolerance}");
    }

    [Theory]
    [InlineData("big.tif", 2000, 1200, 236, 142)]   // 2.4 MP: parallel banded decode with a box reduction
    [InlineData("wide.tif", 3000, 800, 300, 80)]
    [InlineData("small.tif", 640, 480, 236, 177)]   // under 2 MP: the generic WIC route
    public void A_tiff_gets_a_correct_native_thumbnail_where_skia_has_no_decoder(string name, int width, int height, int request, int expectedHeight)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Save(name, ImageFormat.Tiff, width, height);
        Assert.True(NativeImageDecoder.SupportsNativeThumbnail(path));

        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, request, out var thumbnail));
        using (thumbnail)
        {
            AssertSize(thumbnail!, request, expectedHeight);
            AssertIsTheGradient(thumbnail!, tolerance: 6, label: name);
        }
    }

    [Theory]
    [InlineData("big.bmp", 2000, 1200, 236, 142)]
    [InlineData("small.bmp", 500, 300, 236, 142)]
    public void A_bmp_gets_a_correct_native_thumbnail(string name, int width, int height, int request, int expectedHeight)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Save(name, ImageFormat.Bmp, width, height);

        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, request, out var thumbnail));
        using (thumbnail)
        {
            AssertSize(thumbnail!, request, expectedHeight);
            AssertIsTheGradient(thumbnail!, tolerance: 6, label: name);
        }
    }

    [Fact]
    public void The_banded_reduction_agrees_with_the_wic_scaler()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Save("compare.tif", ImageFormat.Tiff, 2400, 1500);
        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, 300, out var banded));
        Assert.True(NativeImageDecoder.TryDecodeBounded(path, 300, 188, out var scaled));
        using (banded) using (scaled)
        {
            Assert.Equal(banded!.PixelSize.Width, scaled.PixelSize.Width);
            Assert.InRange(banded.PixelSize.Height, scaled.PixelSize.Height, scaled.PixelSize.Height + 1); // nearest vs floor
            var rows = Math.Min(banded.PixelSize.Height, scaled.PixelSize.Height);
            var a = Pixels(banded);
            var b = Pixels(scaled);
            var total = 0L;
            var count = banded.PixelSize.Width * rows * 4;
            for (var i = 0; i < count; i++) total += Math.Abs(a[i] - b[i]);
            Assert.True(total / (double)count < 3, $"mean difference {total / (double)count:F2} per channel");
        }
    }

    [Fact]
    public void A_thumbnail_is_never_larger_than_the_picture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Save("tiny.tif", ImageFormat.Tiff, 120, 80);
        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, 236, out var thumbnail));
        using (thumbnail) Assert.Equal(new PixelSize(120, 80), thumbnail!.PixelSize);
    }

    [Fact]
    public void The_explorer_decode_entry_point_now_returns_a_thumbnail_for_tiff_and_bmp()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var (name, format) in new[] { ("entry.tif", ImageFormat.Tiff), ("entry.bmp", ImageFormat.Bmp) })
        {
            var path = Save(name, format, 1600, 1000);
            using var thumbnail = ThumbnailDecode.DecodeToWidth(path, 200);
            Assert.NotNull(thumbnail);
            Assert.Equal(new PixelSize(200, 125), thumbnail!.PixelSize);
        }
    }

    [Fact]
    public void A_webp_is_decoded_straight_to_the_thumbnail_size_by_libwebp()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = SaveWebp("scaled.webp", 800, 600);
        Assert.True(NativeImageDecoder.SupportsNativeThumbnail(path));

        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, 200, out var thumbnail));
        using (thumbnail)
        {
            Assert.Equal(new PixelSize(200, 150), thumbnail!.PixelSize);
            AssertIsTheGradient(thumbnail!, tolerance: 8, label: "webp thumbnail");
        }
    }

    [Fact]
    public void The_viewers_webp_first_view_uses_libwebp_and_the_full_frame_is_exact_when_lossless()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = SaveWebp("full.webp", 640, 480);
        var source = Gradient(640, 480);

        Assert.True(NativeImageDecoder.TryDecodeBounded(path, 320, 320, out var first));   // reduced first view, decoded by libwebp
        using (first)
        {
            Assert.Equal(new PixelSize(320, 240), first.PixelSize);
            AssertIsTheGradient(first, tolerance: 8, label: "webp first view");
        }

        // The full frame stays with Skia (libwebp measured slower there): the backend still returns every pixel exactly.
        using var stream = File.OpenRead(path);
        using var full = new AvaloniaImageDecoderBackend().DecodeFull(stream, path);
        Assert.Equal(new PixelSize(640, 480), full.PixelSize);
        Assert.True(Pixels(full).AsSpan().SequenceEqual(source.Bgra), "lossless WebP must decode bit-exact");
    }

    [Fact]
    public void A_webp_with_transparency_keeps_its_alpha_in_the_thumbnail()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Gradient(400, 300);
        for (var i = 3; i < source.Bgra.Length; i += 4) source.Bgra[i] = 128;
        var path = _dir.Combine("alpha.webp");
        ImageFileEncoder.Encode(SaveFormatCatalog.WebP, EncoderPixels.Premultiply(source.Bgra), 400, 300, path, new ImageEncodeOptions { Lossless = true });

        Assert.True(NativeImageDecoder.TryDecodeThumbnail(path, 100, out var thumbnail));
        using (thumbnail)
        {
            Assert.Equal(new PixelSize(100, 75), thumbnail!.PixelSize);
            var pixels = Pixels(thumbnail);
            var centre = (37 * 100 + 50) * 4;
            Assert.InRange(pixels[centre + 3], 126, 130);          // alpha 128 survives
            Assert.True(pixels[centre + 1] <= pixels[centre + 3]); // premultiplied: no channel above alpha
        }
    }

    [Fact]
    public void A_webp_that_is_not_a_still_picture_is_declined_not_misdecoded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var garbage = _dir.Combine("garbage.webp");
        File.WriteAllBytes(garbage, [.. "RIFF"u8, 0x10, 0, 0, 0, .. "WEBPVP8 "u8, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.False(NativeImageDecoder.TryDecodeThumbnail(garbage, 200, out _));
        var empty = _dir.Combine("empty.webp");
        File.WriteAllBytes(empty, []);
        Assert.False(NativeImageDecoder.TryDecodeThumbnail(empty, 200, out _));
    }

    [Fact]
    public void The_explorer_offers_thumbnails_for_the_new_formats()
    {
        var method = typeof(Glide.App.MainWindow).GetMethod("CanUseBrowserThumbnailDecoder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var extension in new[] { ".jpg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp" })
            Assert.True((bool)method.Invoke(null, [@"C:\x\a" + extension])!, extension);
        Assert.False((bool)method.Invoke(null, [@"C:\x\a.txt"])!);
    }
}
