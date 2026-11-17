using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using Glide.App.Services;
using Glide.Imaging;
using SkiaSharp;

namespace Glide.Core.Tests;

/// <summary>
/// The Explorer thumbnails and the tab hover previews share <see cref="ThumbnailDecode"/>, which must apply the same
/// Skia size check as the main loader (<see cref="SkiaDecodeGuard"/>): a small file whose header declares a frame
/// beyond the pixel cap, or one that does not fit the free memory, is never decoded. Every "huge" image here is a
/// synthetic header; the decode seam throws before Skia could allocate anything.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ThumbnailDecodeGuardTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glide-thumbguard-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _decoded = new();

    public ThumbnailDecodeGuardTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Records every decode that would start (set in the test's own async flow). A refused image must never get here;
    /// if one does, it stops before Skia allocates its (multi-gigabyte) frame.
    /// </summary>
    private void WatchDecodes() => ThumbnailDecode.DecodingForTests = path =>
    {
        lock (_decoded) _decoded.Add(path);
        if (new FileInfo(path).Length < 1024) throw new OperationCanceledException("test: a full decode of a synthetic header started");
    };

    public void Dispose()
    {
        ThumbnailDecode.DecodingForTests = null;
        DecodeMemoryGuard.SnapshotOverrideForTests = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static void Chunk(List<byte> png, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.AddRange(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.AddRange(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.AddRange(crc);
    }

    /// <summary>A structurally valid PNG (correct CRCs) that declares <paramref name="width"/> x <paramref name="height"/> but holds almost no data.</summary>
    private string DeclaredPng(string name, int width, int height)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", new byte[] { 0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01 });
        Chunk(png, "IEND", Array.Empty<byte>());
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    private string RealPng(string name, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private Bitmap? Decode(string path, int width)
    {
        Bitmap? bitmap = null;
        var error = Record.Exception(() => bitmap = ThumbnailDecode.DecodeToWidth(path, width));
        lock (_decoded) Assert.True(_decoded.Count == 0, $"{Path.GetFileName(path)} was decoded although its header is beyond the guard ({error?.Message})");
        Assert.Null(error);
        return bitmap;
    }

    [Fact]
    public void A_thumbnail_is_not_decoded_when_the_header_declares_a_frame_beyond_the_pixel_cap()
    {
        WatchDecodes();
        var path = DeclaredPng("huge.png", 30_000, 20_000); // 600 MP, a 2.4 GB frame, in a 70-byte file
        Assert.True(new FileInfo(path).Length < 48L * 1024 * 1024); // well under the hover-preview file limit

        Assert.Null(Decode(path, 224));
    }

    [Fact]
    public void A_thumbnail_is_not_decoded_when_the_frame_does_not_fit_the_free_memory()
    {
        WatchDecodes();
        DecodeMemoryGuard.SnapshotOverrideForTests = () => new DecodeMemoryGuard.MemorySnapshot(512UL * 1024 * 1024, 4UL * GB);
        var path = DeclaredPng("big.png", 12_000, 12_000); // inside the cap, but a 576 MB frame

        Assert.Null(Decode(path, 224));
    }

    [Fact]
    public async Task A_tab_hover_preview_of_an_oversized_picture_is_skipped_without_decoding()
    {
        WatchDecodes();
        var path = DeclaredPng("huge-tab.png", 30_000, 20_000);
        using var cache = new TabPreviewCache();

        var preview = await cache.GetAsync(path, TabPreviewCache.PictureWidth, TabPreviewCache.PictureHeight);

        Assert.Null(preview);
        lock (_decoded) Assert.Empty(_decoded);
    }

    [Fact]
    public void An_ordinary_picture_still_gets_its_scaled_thumbnail()
    {
        WatchDecodes();
        var path = RealPng("normal.png", 800, 600);

        using var bitmap = ThumbnailDecode.DecodeToWidth(path, 200);

        Assert.NotNull(bitmap);
        Assert.Equal(200, bitmap!.PixelSize.Width);
        lock (_decoded) Assert.Single(_decoded);
    }
}
