using System.IO.Compression;
using System.Runtime.InteropServices;
using Avalonia.Headless;
using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SvgDecoderTests
{
    public SvgDecoderTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private const string RedRectSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"20\"><rect width=\"40\" height=\"20\" fill=\"#FF0000\"/></svg>";

    [Fact]
    public void Probes_intrinsic_viewbox_dimensions()
    {
        var path = WriteTemp(".svg", System.Text.Encoding.UTF8.GetBytes(RedRectSvg));
        try
        {
            Assert.True(SvgDecoder.TryProbeDimensions(path, out var dims));
            Assert.Equal(new ImageDimensions(40, 20), dims);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Renders_svg_to_bitmap_at_requested_resolution()
    {
        var path = WriteTemp(".svg", System.Text.Encoding.UTF8.GetBytes(RedRectSvg));
        try
        {
            AvaloniaHeadlessFixture.RunOnUIThread(() =>
            {
                Assert.True(SvgDecoder.TryDecode(path, 0, out var full));
                using (full)
                {
                    Assert.NotNull(full);
                    Assert.Equal(40, full!.PixelSize.Width);
                    Assert.Equal(20, full.PixelSize.Height);
                    var (b, g, r) = ReadPixel(full, 0, 0);
                    Assert.True(r > 200 && g < 40 && b < 40, $"expected red, got r={r} g={g} b={b}");
                }

                // Bounded render scales the longest side down.
                Assert.True(SvgDecoder.TryDecode(path, 10, out var small));
                using (small)
                {
                    Assert.NotNull(small);
                    Assert.Equal(10, small!.PixelSize.Width);
                    Assert.Equal(5, small.PixelSize.Height);
                }
            });
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Renders_gzipped_svgz()
    {
        var path = WriteTemp(".svgz", Gzip(System.Text.Encoding.UTF8.GetBytes(RedRectSvg)));
        try
        {
            AvaloniaHeadlessFixture.RunOnUIThread(() =>
            {
                Assert.True(SvgDecoder.TryProbeDimensions(path, out var dims));
                Assert.Equal(new ImageDimensions(40, 20), dims);
                Assert.True(SvgDecoder.TryDecode(path, 0, out var bmp));
                using (bmp) Assert.NotNull(bmp);
            });
        }
        finally { TryDelete(path); }
    }

    private static (byte B, byte G, byte R) ReadPixel(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        using var fb = ((Avalonia.Media.Imaging.WriteableBitmap)bitmap).Lock();
        var offset = y * fb.RowBytes + x * 4;
        var b = Marshal.ReadByte(fb.Address, offset);
        var g = Marshal.ReadByte(fb.Address, offset + 1);
        var r = Marshal.ReadByte(fb.Address, offset + 2);
        return (b, g, r);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "glide-svg-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
