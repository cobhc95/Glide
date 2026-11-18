using System.IO.Compression;
using System.Runtime.InteropServices;
using Glide.Imaging;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
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

    // Security regression: Svg.Model fetched <image>/<feImage> hrefs with WebRequest, so opening an SVG
    // contacted remote hosts (tracking, and NTLM credential leaks through \\host\share UNC paths).
    [Fact]
    public void External_image_references_are_never_fetched_and_the_rest_still_renders()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var url = $"http://127.0.0.1:{port}/pixel";
            var svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"40\" height=\"20\">" +
                "<rect width=\"40\" height=\"20\" fill=\"#FF0000\"/>" +
                $"<image width=\"40\" height=\"20\" xlink:href=\"{url}.png\"/>" +
                $"<image width=\"40\" height=\"20\" href=\"{url}2.png\"/>" +
                $"<filter id=\"f\"><feImage xlink:href=\"{url}3.png\"/></filter><rect width=\"1\" height=\"1\" filter=\"url(#f)\"/>" +
                $"<image width=\"40\" height=\"20\" xlink:href=\"{NestedSvgDataUri($"{url}4.png")}\"/>" +
                "</svg>";
            var path = WriteTemp(".svg", System.Text.Encoding.UTF8.GetBytes(svg));
            try
            {
                AvaloniaHeadlessFixture.RunOnUIThread(() =>
                {
                    Assert.True(SvgDecoder.TryProbeDimensions(path, out var dims));
                    Assert.Equal(new ImageDimensions(40, 20), dims);
                    Assert.True(SvgDecoder.TryDecode(path, 0, out var bitmap));
                    using (bitmap)
                    {
                        var (b, g, r) = ReadPixel(bitmap!, 20, 10);
                        Assert.True(r > 200 && g < 40 && b < 40, $"expected the red rect, got r={r} g={g} b={b}");
                    }
                });
                Thread.Sleep(200);
                Assert.False(listener.Pending(), "Rendering an SVG must not open network connections.");
            }
            finally { TryDelete(path); }
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Embedded_data_uri_images_still_render()
    {
        const string redPixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"20\">" +
                  $"<image width=\"20\" height=\"20\" preserveAspectRatio=\"none\" xlink:href=\"data:image/png;base64,{redPixelPng}\"/></svg>";
        var path = WriteTemp(".svg", System.Text.Encoding.UTF8.GetBytes(svg));
        try
        {
            AvaloniaHeadlessFixture.RunOnUIThread(() =>
            {
                Assert.True(SvgDecoder.TryDecode(path, 0, out var bitmap));
                using (bitmap)
                {
                    var (b, g, r) = ReadPixel(bitmap!, 10, 10);
                    Assert.True(r > 200 && g < 40 && b < 40, $"expected the embedded red image, got r={r} g={g} b={b}");
                }
            });
        }
        finally { TryDelete(path); }
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("#local", true)]
    [InlineData(@"C:\Pictures\linked.png", true)]
    [InlineData("file:///C:/Pictures/linked.png", true)]
    [InlineData("http://example.com/x.png", false)]
    [InlineData("https://example.com/x.png", false)]
    [InlineData("ftp://example.com/x.png", false)]
    [InlineData(@"\\attacker\share\x.png", false)]
    [InlineData("//attacker/share/x.png", false)]
    [InlineData("file://attacker/share/x.png", false)]
    [InlineData(@"\\?\UNC\attacker\share\x.png", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData("relative.png", false)]
    public void Image_reference_policy_allows_only_embedded_or_local_sources(string href, bool expected)
        => Assert.Equal(expected, SvgDecoder.IsSafeImageReference(href));

    [Fact]
    public void Embedded_svg_images_are_checked_for_their_own_external_references()
    {
        // Svg.Model renders data:image/svg+xml images as nested documents, which fetched their hrefs too.
        Assert.False(SvgDecoder.IsSafeImageReference(NestedSvgDataUri("http://example.com/x.png")));
        Assert.False(SvgDecoder.IsSafeImageReference(NestedSvgDataUri(@"\\attacker\share\x.png")));
        Assert.True(SvgDecoder.IsSafeImageReference(NestedSvgDataUri("#local")));
        Assert.False(SvgDecoder.IsSafeImageReference("data:image/svg+xml;base64,@@not-base64@@"));
    }

    private static string NestedSvgDataUri(string innerHref)
    {
        var inner = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"8\" height=\"8\">" +
                    $"<image width=\"8\" height=\"8\" xlink:href=\"{System.Security.SecurityElement.Escape(innerHref)}\"/></svg>";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(inner));
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
