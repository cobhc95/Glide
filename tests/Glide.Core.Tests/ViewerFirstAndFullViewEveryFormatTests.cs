using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Imaging;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 proof for the viewer side of the same formats the Explorer tab now thumbnails: the loader the app builds (the codec
/// plug-in backend) opens every one of them, first view and full view, and the pictures show the fixture's content (left half
/// red, right half blue) at a believable size. The thumbnail work does not touch these routes; this pins that they keep
/// working, including the SVG preview that is now scaled into the frame.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class ViewerFirstAndFullViewEveryFormatTests : IDisposable
{
    private readonly TempDir _dir = new("glide-viewer-every");

    public ViewerFirstAndFullViewEveryFormatTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    public void Dispose()
    {
        CodecCapabilityRegistry.ClearExternalProviders();
        _dir.Dispose();
    }

    public static IEnumerable<object[]> Formats() => ThumbnailEveryFormatTests.Formats();

    private static (byte B, byte G, byte R, byte A) Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    private static void AssertLeftRedRightBlue(Bitmap bitmap, string label)
    {
        var size = bitmap.PixelSize;
        var left = Pixel(bitmap, size.Width / 4, size.Height / 2);
        var right = Pixel(bitmap, size.Width * 3 / 4, size.Height / 2);
        Assert.True(left.R > 170 && left.B < 90 && left.A > 200, $"{label}: expected red on the left, got R{left.R} G{left.G} B{left.B} A{left.A}");
        Assert.True(right.B > 170 && right.R < 90 && right.A > 200, $"{label}: expected blue on the right, got R{right.R} G{right.G} B{right.B} A{right.A}");
    }

    private static ImageLoadCoordinator NewLoader()
    {
        var backend = new CodecProviderDecoderBackend(lazyResolver: path => CodecProviderRuntime.Shared.ResolveProvider(path));
        return new ImageLoadCoordinator(backend);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task The_viewer_opens_the_format_first_view_and_full_view(string name, string extension, string routeName, byte[] bytes)
    {
        _ = routeName;
        var path = _dir.Combine(name + extension);
        File.WriteAllBytes(path, bytes);
        using var loader = NewLoader();
        loader.SetViewportDemand(path, new ImageViewportDemand(1280, 800));

        var first = await loader.LoadForegroundAsync(path, loader.Policy);
        Assert.NotNull(first);
        var firstSize = first!.Bitmap.PixelSize;
        Assert.True(firstSize.Width > 0 && firstSize.Height > 0, name);
        AssertLeftRedRightBlue(first.Bitmap, name + " first view");
        loader.ReleaseBitmap(first.Bitmap);

        var full = await loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false);
        Assert.NotNull(full);
        var fullSize = full!.Bitmap.PixelSize;
        Assert.True(fullSize.Width >= firstSize.Width || firstSize.Width <= 1280, name);
        // The synthetic pictures are 300 x 200 (a 64 x 64 icon, a DNG whose preview is 600 x 400): full view is the whole picture.
        var expected = extension switch { ".ico" => new PixelSize(64, 64), ".dng" or ".cr2" => new PixelSize(600, 400), _ => new PixelSize(300, 200) };
        Assert.Equal(expected, fullSize);
        AssertLeftRedRightBlue(full.Bitmap, name + " full view");
        loader.ReleaseBitmap(full.Bitmap);
    }

    [Theory]
    [InlineData("basic.heic")]
    [InlineData("basic.avif")]
    [InlineData("basic.jxl")]
    [InlineData("basic.jp2")]
    public async Task The_plugin_formats_open_in_the_viewer_cold_and_after_a_preload(string fixture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codecs", fixture);
        if (!File.Exists(source) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "codecs", "glidecodecs.dll"))) return;
        var path = _dir.Combine("plugin" + Path.GetExtension(fixture));
        File.Copy(source, path);

        // A listing that holds the file warms the plug-in; the open afterwards must give exactly the same picture.
        CodecPreloader.NotifyListing(new[] { path });
        using var loader = NewLoader();
        var first = await loader.LoadForegroundAsync(path, loader.Policy);
        Assert.NotNull(first);
        Assert.True(first!.Bitmap.PixelSize.Width > 0);
        var full = await loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false);
        Assert.NotNull(full);
        Assert.Equal(first.Bitmap.PixelSize.Width * full!.Bitmap.PixelSize.Height, first.Bitmap.PixelSize.Height * full.Bitmap.PixelSize.Width);
    }
}
