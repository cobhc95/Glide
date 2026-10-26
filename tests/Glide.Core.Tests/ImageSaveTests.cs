using Glide.App.Services;
using Glide.Core;
using Glide.Imaging;
using Xunit;

namespace Glide.Core.Tests;

public sealed class ImageSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlideSave_" + Guid.NewGuid().ToString("N"));
    public ImageSaveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // 3x2 image, pixel value = index (one uint per pixel) for easy position checks.
    private static byte[] Grid(int w, int h)
    {
        var b = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) BitConverter.GetBytes((uint)i).CopyTo(b, i * 4);
        return b;
    }
    private static uint At(byte[] b, int w, int x, int y) => BitConverter.ToUInt32(b, (y * w + x) * 4);

    [Fact]
    public void Identity_CopiesPixels()
    {
        var src = Grid(3, 2);
        var dst = new ImageEditTransform(0, false, false).Apply(src, 3, 2, out var w, out var h);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal(src, dst);
    }

    [Fact]
    public void RotateRight_IsClockwise()
    {
        // src:  0 1 2      cw:  3 0
        //       3 4 5           4 1
        //                       5 2
        var dst = new ImageEditTransform(1, false, false).Apply(Grid(3, 2), 3, 2, out var w, out var h);
        Assert.Equal((2, 3), (w, h));
        Assert.Equal(new uint[] { 3, 0, 4, 1, 5, 2 }, Enumerable.Range(0, 6).Select(i => At(dst, 2, i % 2, i / 2)));
    }

    [Theory]
    [InlineData(2, false, false, new uint[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(0, true, false, new uint[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(0, false, true, new uint[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(0, true, true, new uint[] { 5, 4, 3, 2, 1, 0 })]
    public void Rotate180_AndFlips(int turns, bool fh, bool fv, uint[] expected)
    {
        var dst = new ImageEditTransform(turns, fh, fv).Apply(Grid(3, 2), 3, 2, out var w, out _);
        Assert.Equal(expected, Enumerable.Range(0, 6).Select(i => At(dst, w, i % 3, i / 3)));
    }

    [Fact]
    public void RotateLeft_ThenFlipH_MatchesViewportOrder()
    {
        // turns=3 (ccw): 2 5 / 1 4 / 0 3 ; then flip H within rotated frame: 5 2 / 4 1 / 3 0
        var dst = new ImageEditTransform(3, true, false).Apply(Grid(3, 2), 3, 2, out var w, out var h);
        Assert.Equal((2, 3), (w, h));
        Assert.Equal(new uint[] { 5, 2, 4, 1, 3, 0 }, Enumerable.Range(0, 6).Select(i => At(dst, 2, i % 2, i / 2)));
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".JPEG", true)]
    [InlineData(".png", true)]
    [InlineData(".bmp", true)]
    [InlineData(".tif", true)]
    [InlineData(".tiff", true)]
    [InlineData(".webp", false)]
    [InlineData(".gif", false)]
    public void FormatSupport(string ext, bool supported)
    {
        Assert.Equal(supported, NativeImageEncoder.CanEncode(ext));
        Assert.Equal(supported, ImageSavePolicy.CanSaveInPlace("x" + ext, NativeImageEncoder.CanEncode));
    }

    [Fact]
    public void TempPath_IsInDestinationFolder_AndIgnoredByWatcher()
    {
        var temp = ImageSavePolicy.TempPathFor(Path.Combine(_dir, "a.jpg"));
        Assert.Equal(_dir, Path.GetDirectoryName(temp));
        Assert.True(FolderChangeBatcher.IsIgnoredName(temp));
    }

    [Theory]
    [InlineData(".png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(".jpg", new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(".bmp", new byte[] { 0x42, 0x4D })]
    [InlineData(".tif", new byte[] { 0x49, 0x49 })]
    public void EncodeAndReplace_WritesRealFormat_WithRotatedDimensions(string ext, byte[] magic)
    {
        if (!OperatingSystem.IsWindows()) return;
        var dest = Path.Combine(_dir, "out" + ext);
        var px = new byte[4 * 3 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 20; px[i + 2] = 30; px[i + 3] = 255; }
        ImageSaveService.EncodeAndReplace(px, 4, 3, new ImageEditTransform(1, false, false), dest, 0.9f);
        var bytes = File.ReadAllBytes(dest);
        Assert.Equal(magic, bytes.Take(magic.Length));
        Assert.True(ImageHeaderProbe.TryProbe(dest, out var dims));
        Assert.Equal((3, 4), (dims.Width, dims.Height));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void EncodeAndReplace_OverwritesExisting_AndFailureLeavesOriginalIntact()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dest = Path.Combine(_dir, "orig.png");
        File.WriteAllBytes(dest, [1, 2, 3]);
        var px = new byte[2 * 2 * 4];
        ImageSaveService.EncodeAndReplace(px, 2, 2, default, dest, 0.9f);
        Assert.Equal(0x89, File.ReadAllBytes(dest)[0]);

        var keep = Path.Combine(_dir, "keep.webp");
        File.WriteAllBytes(keep, [9, 9, 9]);
        Assert.ThrowsAny<Exception>(() => ImageSaveService.EncodeAndReplace(px, 2, 2, default, keep, 0.9f));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(keep));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }
}
