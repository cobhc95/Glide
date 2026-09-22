using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Verifies the built-in vector/raster decoders are actually reachable through the production
/// <see cref="AvaloniaImageDecoderBackend"/> (full decode, path preview, and dimension probe),
/// not just in isolation.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class DecoderBackendBuiltInFormatTests
{
    public DecoderBackendBuiltInFormatTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private const string RedRectSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\"><rect width=\"40\" height=\"20\" fill=\"#FF0000\"/></svg>";

    [Fact]
    public void Svg_is_decoded_and_previewed_through_the_backend()
    {
        var path = WriteTemp(".svg", Encoding.UTF8.GetBytes(RedRectSvg));
        try
        {
            AvaloniaHeadlessFixture.RunOnUIThread(() =>
            {
                var backend = new AvaloniaImageDecoderBackend();
                Assert.True(backend.SupportsPathPreview(path));

                using var stream = File.OpenRead(path);
                using var full = backend.DecodeFull(stream, path);
                Assert.Equal(40, full.PixelSize.Width);
                Assert.Equal(20, full.PixelSize.Height);
                var (b, g, r) = ReadPixel((WriteableBitmap)full, 0, 0);
                Assert.True(r > 200 && g < 40 && b < 40, $"expected red, got r={r} g={g} b={b}");

                var preview = backend
                    .TryDecodePreviewFromPathAsync(path, new ImageDimensions(40, 20), 16, 16, false)
                    .GetAwaiter().GetResult();
                Assert.NotNull(preview);
                using (preview!.Value.Bitmap)
                {
                    Assert.True(preview.Value.IsPreview);
                    Assert.Equal(16, preview.Value.Bitmap.PixelSize.Width);
                    Assert.Equal(8, preview.Value.Bitmap.PixelSize.Height);
                    Assert.Equal("svg-vector", preview.Value.Route);
                }
            });
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Tga_is_decoded_through_the_backend()
    {
        var path = WriteTemp(".tga", BuildTga24(2, 2, (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255)));
        try
        {
            AvaloniaHeadlessFixture.RunOnUIThread(() =>
            {
                var backend = new AvaloniaImageDecoderBackend();
                Assert.True(backend.SupportsPathPreview(path));

                using var stream = File.OpenRead(path);
                using var full = backend.DecodeFull(stream, path);
                Assert.Equal(2, full.PixelSize.Width);
                Assert.Equal(2, full.PixelSize.Height);
                var (b0, g0, r0) = ReadPixel((WriteableBitmap)full, 0, 0);
                Assert.Equal((255, 0, 0), (r0, g0, b0));

                var preview = backend
                    .TryDecodePreviewFromPathAsync(path, new ImageDimensions(2, 2), 64, 64, false)
                    .GetAwaiter().GetResult();
                Assert.NotNull(preview);
                using (preview!.Value.Bitmap)
                {
                    Assert.Equal(2, preview.Value.Bitmap.PixelSize.Width);
                    Assert.Equal("builtin-raster", preview.Value.Route);
                }
            });
        }
        finally { TryDelete(path); }
    }

    private static byte[] BuildTga24(int width, int height, params (byte R, byte G, byte B)[] pixels)
    {
        var data = new byte[18 + width * height * 3];
        data[2] = 2;                       // uncompressed true-colour
        data[12] = (byte)(width & 0xFF);
        data[13] = (byte)((width >> 8) & 0xFF);
        data[14] = (byte)(height & 0xFF);
        data[15] = (byte)((height >> 8) & 0xFF);
        data[16] = 24;                     // bits per pixel
        data[17] = 0x20;                   // top-left origin
        for (var i = 0; i < pixels.Length; i++)
        {
            data[18 + i * 3] = pixels[i].B;
            data[19 + i * 3] = pixels[i].G;
            data[20 + i * 3] = pixels[i].R;
        }
        return data;
    }

    private static (byte B, byte G, byte R) ReadPixel(WriteableBitmap bitmap, int x, int y)
    {
        using var fb = bitmap.Lock();
        var offset = y * fb.RowBytes + x * 4;
        var b = Marshal.ReadByte(fb.Address, offset);
        var g = Marshal.ReadByte(fb.Address, offset + 1);
        var r = Marshal.ReadByte(fb.Address, offset + 2);
        return (b, g, r);
    }

    private static string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-backend-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
