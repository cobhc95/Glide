using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Imaging;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// The capability registry, the built-in dispatch and the production decoder backend agree on which
/// suffixes the bundled managed decoders own.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ManagedFormatRoutingTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    public static readonly string[] ManagedExtensions =
    {
        ".exr", ".pfm", ".hdr.gz", ".dcm", ".dicom", ".ima", ".nii", ".nii.gz", ".fits", ".fit", ".fts", ".fts.gz",
        ".ora", ".kra", ".pdn", ".xcf", ".ktx", ".ktx2", ".pvr", ".astc",
    };

    [Fact]
    public void Every_managed_and_raw_suffix_is_built_in_and_routed()
    {
        foreach (var extension in ManagedExtensions.Concat(CodecCapabilityRegistry.CameraRawPreviewExtensions))
        {
            var capability = CodecCapabilityRegistry.Describe(extension);
            Assert.True(capability.DecodeEnabled, extension);
            Assert.Equal(CodecTier.BuiltIn, capability.Tier);
            Assert.StartsWith("Glide built-in", capability.Provider, StringComparison.Ordinal);
            Assert.True(BuiltInRasterDecoder.CanDecode("sample" + extension), extension);
            Assert.True(ImageFormatRegistry.IsSupported("sample" + extension), extension);
        }
        Assert.Equal(40, CodecCapabilityRegistry.CameraRawPreviewExtensions.Count);
    }

    [Theory]
    [InlineData(".r3d")]
    [InlineData(".ari")]
    [InlineData(".cinema")]
    [InlineData(".basis")]
    public void Cinema_raw_and_basis_stay_unclaimed(string extension)
    {
        Assert.False(BuiltInRasterDecoder.CanDecode("clip" + extension));
        Assert.False(CodecCapabilityRegistry.Describe(extension).DecodeEnabled);
    }

    [Fact]
    public void Every_decode_enabled_built_in_suffix_has_a_decoder()
    {
        foreach (var capability in CodecCapabilityRegistry.All.Where(c => c.Tier == CodecTier.BuiltIn && c.DecodeEnabled))
        {
            if (capability.Extension is ".svg" or ".svgz") continue;
            Assert.True(BuiltInRasterDecoder.CanDecode("x" + capability.Extension), capability.Extension);
        }
    }

    [Fact]
    public void Raw_and_exr_flow_through_the_production_backend()
    {
        var jpeg = Jpeg(80, 40, new SKColor(255, 0, 0), new SKColor(0, 0, 255));
        var tiff = new Writer();
        tiff.Ascii("II").U16(42).U32(8).U16(2);
        tiff.U16(0x0201).U16(4).U32(1).U32(8 + 2 + 24 + 4);
        tiff.U16(0x0202).U16(4).U32(1).U32(jpeg.Length);
        tiff.U32(0).Bytes(jpeg);
        var raw = Temp(_files, tiff.ToArray(), ".nef");

        // Minimal uncompressed half-float EXR, 2x1: red then white.
        var exr = new Writer();
        exr.U32(20000630).U32(2);
        void Attribute(string name, string type, byte[] value) => exr.Ascii(name).U8(0).Ascii(type).U8(0).U32(value.Length).Bytes(value);
        var channels = new Writer();
        foreach (var name in new[] { "B", "G", "R" }) channels.Ascii(name).U8(0).U32(1).U32(0).U32(1).U32(1);
        channels.U8(0);
        Attribute("channels", "chlist", channels.ToArray());
        Attribute("compression", "compression", new byte[] { 0 });
        Attribute("dataWindow", "box2i", new Writer().U32(0).U32(0).U32(1).U32(0).ToArray());
        exr.U8(0);
        var offset = exr.Length + 8;
        exr.U64(offset).U32(0).U32(12);
        foreach (var plane in new[] { new[] { 0f, 1f }, new[] { 0f, 1f }, new[] { 1f, 1f } })
            foreach (var v in plane) exr.U16(BitConverter.HalfToUInt16Bits((Half)v));
        var exrPath = Temp(_files, exr.ToArray(), ".exr");

        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var backend = new AvaloniaImageDecoderBackend();
            foreach (var (path, width, height) in new[] { (raw, 80, 40), (exrPath, 2, 1) })
            {
                Assert.True(backend.SupportsPathPreview(path));
                using var stream = File.OpenRead(path);
                using var full = backend.DecodeFull(stream, path);
                Assert.Equal(width, full.PixelSize.Width);
                Assert.Equal(height, full.PixelSize.Height);
                var (b, g, r) = ReadPixel((WriteableBitmap)full, 0, 0);
                Assert.True(r > 200 && g < 40 && b < 40, $"expected red at origin of {Path.GetExtension(path)}, got {r},{g},{b}");

                var preview = backend.TryDecodePreviewFromPathAsync(path, new ImageDimensions(width, height), 40, 40, false).GetAwaiter().GetResult();
                Assert.NotNull(preview);
                using (preview!.Value.Bitmap)
                {
                    Assert.Equal("builtin-raster", preview.Value.Route);
                    Assert.True(preview.Value.Bitmap.PixelSize.Width <= 40);
                }
            }
        });
    }

    [Fact]
    public void Managed_decoders_release_the_source_file()
    {
        var jpeg = Jpeg(16, 8, new SKColor(255, 0, 0), new SKColor(0, 0, 255));
        var tiff = new Writer();
        tiff.Ascii("II").U16(42).U32(8).U16(2);
        tiff.U16(0x0201).U16(4).U32(1).U32(8 + 2 + 24 + 4);
        tiff.U16(0x0202).U16(4).U32(1).U32(jpeg.Length);
        tiff.U32(0).Bytes(jpeg);
        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("mergedimage.png").Open();
            entry.Write(Png(2, 2, (_, _) => new SKColor(0, 255, 0)));
        }
        foreach (var (payload, extension) in new[] { (tiff.ToArray(), ".cr2"), (zip.ToArray(), ".ora") })
        {
            var path = Temp(_files, payload, extension);
            Assert.True(BuiltInRasterDecoder.TryProbeDimensions(path, out _));
            Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(path, 0, out _, out _, out _));
            var moved = path + ".moved";
            File.Move(path, moved); // throws if a handle without delete sharing survived
            File.Move(moved, path);
        }
    }

    private static (byte B, byte G, byte R) ReadPixel(WriteableBitmap bitmap, int x, int y)
    {
        using var frame = bitmap.Lock();
        var pixel = new byte[4];
        Marshal.Copy(IntPtr.Add(frame.Address, y * frame.RowBytes + x * 4), pixel, 0, 4);
        return (pixel[0], pixel[1], pixel[2]);
    }
}
