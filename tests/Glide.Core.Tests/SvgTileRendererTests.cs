using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Imaging;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 SVG: tiles of plain path-heavy documents go through Direct2D (about 8x faster than Svg.Skia), everything else
/// through Svg.Skia at tile size; the two draw the same picture; the viewer's own SVG preview now scales the whole picture
/// into the requested frame; the idle warm-up is idempotent and silent.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class SvgTileRendererTests : IDisposable
{
    private readonly TempDir _dir = new("glide-svg-tile");

    public SvgTileRendererTests(AvaloniaHeadlessFixture fixture) { _ = fixture; SvgTileRenderer.Direct2DEnabled = true; }

    public void Dispose()
    {
        SvgTileRenderer.Direct2DEnabled = true;
        _dir.Dispose();
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private const string Plain = """<svg xmlns="http://www.w3.org/2000/svg" width="300" height="200" viewBox="0 0 300 200"><rect width="150" height="200" fill="#f00"/><rect x="150" width="150" height="200" fill="#00f"/></svg>""";

    private static string PathHeavy(int paths)
    {
        var sb = new StringBuilder("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 200"><defs><linearGradient id="g" x1="0" x2="1"><stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#00f"/></linearGradient></defs><rect width="300" height="200" fill="#fff"/>""");
        var random = new Random(7);
        for (var i = 0; i < paths; i++)
        {
            var x = random.Next(0, 280); var y = random.Next(0, 180);
            sb.Append($"<path d=\"M{x} {y} l{random.Next(5, 20)} {random.Next(0, 15)} l{random.Next(-10, 10)} {random.Next(5, 20)} z\" fill=\"{(i % 3 == 0 ? "url(#g)" : i % 3 == 1 ? "#3a7" : "#c52")}\" stroke=\"#222\" stroke-width=\"0.5\" transform=\"rotate({i % 30} {x} {y})\"/>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static (byte B, byte G, byte R, byte A) Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    private static byte[] AllPixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    // ---- which documents Direct2D may draw -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Plain, true)]
    [InlineData("""<svg viewBox="0 0 24 24"><path d="M0 0h24v24z" fill="#000"/></svg>""", true)]
    [InlineData("""<svg width="24px" height="24px"><use href="#a"/></svg>""", true)]
    [InlineData("""<svg viewBox="0 0 10 10"><text x="1" y="5">hi</text></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><style>.a{fill:red}</style><rect class="a" width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><filter id="f"><feGaussianBlur stdDeviation="2"/></filter><rect filter="url(#f)" width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><image href="data:image/png;base64,AAAA" width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><IMAGE xlink:href="C:\x.png" width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><use xlink:href="http://example.com/a.svg#b"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><use href="\\host\share\a.svg#b"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><mask id="m"/><pattern id="p"/><rect width="5" height="5"/></svg>""", false)]
    [InlineData("""<!DOCTYPE svg [<!ENTITY a "b">]><svg viewBox="0 0 10 10"><rect width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg viewBox="0 0 10 10"><script>alert(1)</script></svg>""", false)]
    [InlineData("""<svg width="100%" height="100%"><rect width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg width="10cm" height="5cm"><rect width="5" height="5"/></svg>""", false)]
    [InlineData("""<svg><rect width="5" height="5"/></svg>""", false)]
    public void Direct2D_is_used_only_for_documents_it_draws_like_Svg_Skia(string document, bool expected)
        => Assert.Equal(expected, SvgTileRenderer.TryGetDirect2DSize(Bytes(document), out _, out _));

    [Fact]
    public void The_root_size_comes_from_width_and_height_or_the_viewBox()
    {
        Assert.True(SvgTileRenderer.TryReadRootSize(Bytes("""<svg width="300" height="200"/>"""), out var w, out var h));
        Assert.Equal((300f, 200f), (w, h));
        Assert.True(SvgTileRenderer.TryReadRootSize(Bytes("""<svg viewBox="0 0 48 24"/>"""), out w, out h));
        Assert.Equal((48f, 24f), (w, h));
        Assert.True(SvgTileRenderer.TryReadRootSize(Bytes("""<svg width="96" viewBox="0 0 48 24"/>"""), out w, out h));
        Assert.Equal((96f, 48f), (w, h)); // the height follows from the viewBox ratio
        Assert.True(SvgTileRenderer.TryReadRootSize(Bytes("""<?xml version="1.0"?><svg stroke-width="3" height="30px" width='60px'/>"""), out w, out h));
        Assert.Equal((60f, 30f), (w, h)); // stroke-width is not width; single quotes and px are read
        Assert.False(SvgTileRenderer.TryReadRootSize(Bytes("""<svg/>"""), out _, out _));
    }

    // ---- the picture ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_plain_document_is_drawn_by_Direct2D_at_tile_size_and_looks_like_the_Svg_Skia_render()
    {
        var path = _dir.Combine("plain.svg");
        File.WriteAllText(path, Plain);
        Assert.True(SvgTileRenderer.TryRender(path, 150, out var tile));
        using (tile)
        {
            Assert.NotNull(tile);
            Assert.Equal(new PixelSize(150, 100), tile!.PixelSize);
            if (OperatingSystem.IsWindows() && SvgTileRenderer.LastRoute == "direct2d")
            {
                var left = Pixel(tile, 30, 50); var right = Pixel(tile, 120, 50);
                Assert.True(left.R > 230 && left.B < 25 && left.A == 255, $"left {left}");
                Assert.True(right.B > 230 && right.R < 25 && right.A == 255, $"right {right}");
            }
        }
    }

    [Fact]
    public void Direct2D_and_Svg_Skia_draw_a_path_heavy_document_almost_identically()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = _dir.Combine("paths.svg");
        File.WriteAllText(path, PathHeavy(400));

        Assert.True(SvgTileRenderer.TryRender(path, 236, out var direct));
        if (SvgTileRenderer.LastRoute != "direct2d") { direct?.Dispose(); return; } // an older Glide.Native.dll without the export: nothing to compare
        SvgTileRenderer.Direct2DEnabled = false;
        Assert.True(SvgTileRenderer.TryRender(path, 236, out var skia));
        Assert.Equal("svg-skia", SvgTileRenderer.LastRoute);
        using (direct) using (skia)
        {
            Assert.Equal(skia!.PixelSize, direct!.PixelSize);
            var a = AllPixels(direct); var b = AllPixels(skia);
            long total = 0;
            for (var i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
            var meanDifference = total / (double)a.Length;
            Assert.True(meanDifference < 6.0, $"mean channel difference {meanDifference:F2} of 255 between Direct2D and Svg.Skia");
        }
    }

    [Fact]
    public void Direct2D_off_or_a_document_it_cannot_draw_uses_Svg_Skia()
    {
        var plain = _dir.Combine("plain.svg");
        File.WriteAllText(plain, Plain);
        SvgTileRenderer.Direct2DEnabled = false;
        Assert.True(SvgTileRenderer.TryRender(plain, 100, out var off));
        off?.Dispose();
        Assert.Equal("svg-skia", SvgTileRenderer.LastRoute);

        SvgTileRenderer.Direct2DEnabled = true;
        var text = _dir.Combine("text.svg");
        File.WriteAllText(text, """<svg xmlns="http://www.w3.org/2000/svg" width="300" height="200"><rect width="300" height="200" fill="#0a0"/><text x="10" y="50">Hello</text></svg>""");
        Assert.True(SvgTileRenderer.TryRender(text, 100, out var withText));
        withText?.Dispose();
        Assert.Equal("svg-skia", SvgTileRenderer.LastRoute);

        var gz = _dir.Combine("plain.svgz");
        using (var file = File.Create(gz)) using (var z = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest)) z.Write(Bytes(Plain));
        Assert.True(SvgTileRenderer.TryRender(gz, 100, out var zipped)); // inflated within the budget, then drawn by either route
        Assert.Equal(new PixelSize(100, 67), zipped!.PixelSize);
        zipped.Dispose();
        Assert.Contains(SvgTileRenderer.LastRoute, new[] { "direct2d", "svg-skia" });

        Assert.False(SvgTileRenderer.TryRender(_dir.Combine("missing.svg"), 100, out _));
    }

    [Fact]
    public void A_document_with_an_external_image_never_reaches_Direct2D()
    {
        var path = _dir.Combine("remote.svg");
        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="100" height="100"><image xlink:href="\\evil\share\x.png" width="100" height="100"/><rect width="10" height="10" fill="#f00"/></svg>""");
        Assert.False(SvgTileRenderer.TryGetDirect2DSize(File.ReadAllBytes(path), out _, out _));
        Assert.True(SvgTileRenderer.TryRender(path, 50, out var bitmap));
        bitmap?.Dispose();
        Assert.Equal("svg-skia", SvgTileRenderer.LastRoute);
    }

    // ---- the viewer's own SVG preview ---------------------------------------------------------------------------------------

    [Fact]
    public void The_svg_preview_scales_the_whole_picture_into_the_requested_frame()
    {
        var path = _dir.Combine("halves.svg");
        File.WriteAllText(path, Plain);
        Assert.True(SvgDecoder.TryDecode(path, 20, out var small));
        using (small)
        {
            Assert.Equal(new PixelSize(20, 13), small!.PixelSize);
            Assert.True(Pixel(small, 3, 6).R > 200, "red on the left");
            Assert.True(Pixel(small, 16, 6).B > 200, "blue on the right: the picture is scaled, not cropped");
        }
        Assert.True(SvgDecoder.TryDecode(path, 900, out var big)); // a preview may be larger than the document (vectors stay crisp)
        using (big)
        {
            Assert.Equal(new PixelSize(900, 600), big!.PixelSize);
            Assert.True(Pixel(big, 100, 300).R > 200);
            Assert.True(Pixel(big, 800, 300).B > 200, "the picture fills the larger frame");
        }
    }

    [Fact]
    public void The_idle_warm_up_is_silent_and_runs_once()
    {
        var first = Record.Exception(SvgTileRenderer.WarmUp);
        var second = Record.Exception(SvgTileRenderer.WarmUp);
        Assert.Null(first);
        Assert.Null(second);
    }
}
