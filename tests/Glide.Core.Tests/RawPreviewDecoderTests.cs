using Glide.Imaging;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// Camera RAW containers built in-test around real JPEG previews: TIFF/IFD flavours (incl. ORF and RW2
/// magics, big-endian DNG with SubIFD strips), CR3 (ISO-BMFF), RAF, CRW (CIFF), X3F, MRW and the
/// generic embedded-JPEG scan. Each asserts size, colour placement and EXIF/container orientation.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class RawPreviewDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    public static IEnumerable<object[]> TiffFlavours() => new[]
    {
        ".dng", ".cr2", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".pef", ".ptx", ".3fr", ".fff", ".iiq", ".cap", ".eip", ".mef",
        ".mos", ".erf", ".kdc", ".dcr", ".k25", ".bay", ".srw", ".rwz", ".gpr", ".mdc", ".dcs", ".drf", ".dsc", ".r2d", ".rw1",
    }.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(TiffFlavours))]
    public void Tiff_based_raw_decodes_its_embedded_jpeg(string extension)
    {
        var path = Temp(_files, SimpleTiff("II*\0"u8.ToArray(), Jpeg(64, 48, Red, Blue), orientation: 1), extension);
        var pixels = Decode(path);
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(8, 24), 255, 0, 0, 16);
        AssertColor(pixels.At(56, 24), 0, 0, 255, 16);
    }

    [Theory]
    [InlineData(".orf")]
    [InlineData(".ori")]
    public void Olympus_magic_is_accepted(string extension)
    {
        var path = Temp(_files, SimpleTiff("IIRO"u8.ToArray(), Jpeg(64, 48, Red, Blue), orientation: 1), extension);
        Assert.Equal((64, 48), (Decode(path).Width, Decode(path).Height));
    }

    [Theory]
    [InlineData(".rw2")]
    [InlineData(".rwl")]
    [InlineData(".raw")]
    public void Panasonic_jpg_from_raw_tag_is_used(string extension)
    {
        var path = Temp(_files, SimpleTiff("IIU\0"u8.ToArray(), Jpeg(64, 48, Red, Blue), orientation: 1, jpgFromRawTag: true), extension);
        var pixels = Decode(path);
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(8, 8), 255, 0, 0, 16);
    }

    [Fact]
    public void Container_orientation_is_applied()
    {
        var path = Temp(_files, SimpleTiff("II*\0"u8.ToArray(), Jpeg(64, 48, Red, Blue), orientation: 6), ".nef");
        var pixels = Decode(path);
        Assert.Equal((48, 64), (pixels.Width, pixels.Height));
        // Rotated 90° clockwise: the source's left (red) half is now on top.
        AssertColor(pixels.At(24, 8), 255, 0, 0, 16);
        AssertColor(pixels.At(24, 56), 0, 0, 255, 16);
    }

    [Fact]
    public void Big_endian_dng_prefers_the_largest_preview_from_a_subifd_strip()
    {
        var path = Temp(_files, BigEndianDng(Jpeg(16, 12, Blue, Blue), Jpeg(64, 48, Red, Blue)), ".dng");
        var pixels = Decode(path);
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(8, 24), 255, 0, 0, 16);
    }

    [Fact]
    public void Cr3_uses_the_full_size_track_and_cmt1_orientation()
    {
        var path = Temp(_files, Cr3(Jpeg(32, 24, Blue, Blue), Jpeg(64, 48, Red, Blue), orientation: 6), ".cr3");
        var pixels = Decode(path);
        Assert.Equal((48, 64), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(24, 8), 255, 0, 0, 16);
        AssertColor(pixels.At(24, 56), 0, 0, 255, 16);
    }

    [Fact]
    public void Raf_header_jpeg_with_exif_orientation()
    {
        var w = new Writer { BigEndian = true };
        w.Ascii("FUJIFILMCCD-RAW 0201FF383501").Zeros(84 - 28);
        var jpeg = Jpeg(64, 48, Red, Blue, exifOrientation: 8);
        w.U32(160).U32(jpeg.Length).Zeros(160 - 92).Bytes(jpeg).Zeros(64);
        var pixels = Decode(Temp(_files, w.ToArray(), ".raf"));
        Assert.Equal((48, 64), (pixels.Width, pixels.Height));
        // Orientation 8 (rotate 90° counter-clockwise): the right (blue) half ends up on top.
        AssertColor(pixels.At(24, 8), 0, 0, 255, 16);
        AssertColor(pixels.At(24, 56), 255, 0, 0, 16);
    }

    [Fact]
    public void Crw_ciff_heap_jpg_from_raw()
    {
        var jpeg = Jpeg(64, 48, Red, Blue);
        var w = new Writer();
        w.Ascii("II").U32(26).Ascii("HEAPCCDR").U16(1).U16(2).Zeros(26 - 18);
        w.Bytes(jpeg).Align(2);
        var dirOffset = w.Position - 26;
        w.U16(1).U16(0x2007).U32(jpeg.Length).U32(0);
        w.U32(dirOffset);
        var pixels = Decode(Temp(_files, w.ToArray(), ".crw"));
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
    }

    [Fact]
    public void X3f_section_directory_jpeg()
    {
        var jpeg = Jpeg(64, 48, Red, Blue);
        var w = new Writer();
        w.Ascii("FOVb").U32(0x00020002).Zeros(56);
        var section = w.Position;
        w.Ascii("SECi").U32(0x00020000).U32(2).U32(18).U32(64).U32(48).U32(0).Bytes(jpeg).Align(4);
        var directory = w.Position;
        w.Ascii("SECd").U32(0x00020000).U32(1).U32(section).U32(28 + jpeg.Length).Ascii("IMA2");
        w.U32(directory);
        var pixels = Decode(Temp(_files, w.ToArray(), ".x3f"));
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
    }

    [Fact]
    public void Mrw_makernote_preview_with_minolta_soi_quirk()
    {
        var jpeg = Jpeg(64, 48, Red, Blue);
        jpeg[0] = 0x00; // Minolta stores the preview's first byte as 0x00
        var tiff = new Writer { BigEndian = true };
        tiff.Ascii("MM").U16(42).U32(8);
        // IFD0: ExifIFD pointer
        tiff.U16(1); Entry(tiff, 0x8769, 4, 1, 26); tiff.U32(0);
        // Exif IFD at 26: MakerNote
        tiff.U16(1); Entry(tiff, 0x927C, 7, 30, 44); tiff.U32(0);
        // MakerNote IFD at 44
        var jpegOffset = 44 + 2 + 2 * 12 + 4;
        tiff.U16(2); Entry(tiff, 0x0088, 4, 1, jpegOffset); Entry(tiff, 0x0089, 4, 1, jpeg.Length); tiff.U32(0);
        tiff.Bytes(jpeg);
        var ttw = tiff.ToArray();

        var w = new Writer { BigEndian = true };
        w.Ascii("\0MRM").U32(8 + 24 + 8 + ttw.Length);
        w.Ascii("\0PRD").U32(24).Zeros(24);
        w.Ascii("\0TTW").U32(ttw.Length).Bytes(ttw);
        w.Zeros(32);
        var pixels = Decode(Temp(_files, w.ToArray(), ".mrw"));
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(8, 24), 255, 0, 0, 16);
    }

    [Fact]
    public void Unknown_container_falls_back_to_scanning_for_embedded_jpeg()
    {
        var random = new Random(1234);
        var junk = new byte[3000];
        random.NextBytes(junk);
        for (var i = 0; i < junk.Length; i++) if (junk[i] == 0xFF) junk[i] = 0;
        var payload = junk.Take(1500).Concat(Jpeg(64, 48, Red, Blue)).Concat(junk.Skip(1500)).ToArray();
        var pixels = Decode(Temp(_files, payload, ".iiq"));
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
    }

    [Fact]
    public void Bounded_decode_uses_jpeg_downscaling()
    {
        var path = Temp(_files, SimpleTiff("II*\0"u8.ToArray(), Jpeg(64, 48, Red, Blue), orientation: 1), ".arw");
        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(path, 16, out var w, out var h, out _));
        Assert.True(w >= 16 && w < 64, $"width {w}");
        Assert.True(h >= 12 && h < 48, $"height {h}");
        Assert.True(BuiltInRasterDecoder.TryDecodeBounded(path, 16, out var bitmap));
        using (bitmap) Assert.Equal(16, bitmap!.PixelSize.Width);
    }

    [Fact]
    public void Lossless_only_and_malformed_raws_fail_gracefully()
    {
        // A lossless (SOF3) stream is never a displayable preview.
        var lossless = new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0, 0, 0xFF, 0xC3, 0x00, 0x0B, 8, 0, 16, 0, 16, 1, 1, 0x11, 0, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 0x3F, 0, 0xFF, 0xD9 };
        AssertFails(Temp(_files, SimpleTiff("II*\0"u8.ToArray(), lossless, 1), ".dng"));

        // IFD pointing far past the end of the file.
        AssertFails(Temp(_files, new byte[] { (byte)'I', (byte)'I', 42, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0 }, ".cr2"), "ifd");

        // Truncated preview (header intact, entropy data cut).
        var jpeg = Jpeg(64, 48, Red, Blue);
        AssertFails(Temp(_files, SimpleTiff("II*\0"u8.ToArray(), jpeg.Take(jpeg.Length - 24).ToArray(), 1), ".nef"), "truncated preview");
        var whole = SimpleTiff("II*\0"u8.ToArray(), jpeg, 1);
        AssertFails(Temp(_files, whole.Take(74 + jpeg.Length - 24).ToArray(), ".nef"), "truncated file");

        // Garbage and empty files.
        var garbage = new byte[4096];
        new Random(7).NextBytes(garbage);
        AssertFails(Temp(_files, garbage, ".arw"), "garbage");
        AssertFails(Temp(_files, Array.Empty<byte>(), ".orf"));
        AssertFails(Temp(_files, "FUJIFILMCCD-RAW 0201"u8.ToArray(), ".raf"));
    }

    // -- Builders -------------------------------------------------------------------------------

    private static void Entry(Writer w, int tag, int type, long count, long value)
    {
        w.U16(tag).U16(type).U32(count);
        if (type == 3 && count == 1) w.U16((int)value).U16(0);
        else w.U32(value);
    }

    private static byte[] SimpleTiff(byte[] magic, byte[] jpeg, int orientation, bool jpgFromRawTag = false)
    {
        var w = new Writer();
        w.Bytes(magic).U32(8);
        var entries = jpgFromRawTag ? 4 : 5;
        var jpegOffset = 8 + 2 + entries * 12 + 4;
        w.U16(entries);
        Entry(w, 0x0100, 4, 1, 4000);
        Entry(w, 0x0101, 4, 1, 3000);
        if (jpgFromRawTag)
        {
            Entry(w, 0x002E, 7, jpeg.Length, jpegOffset);
            Entry(w, 0x0112, 3, 1, orientation);
        }
        else
        {
            Entry(w, 0x0112, 3, 1, orientation);
            Entry(w, 0x0201, 4, 1, jpegOffset);
            Entry(w, 0x0202, 4, 1, jpeg.Length);
        }
        w.U32(0);
        w.Bytes(jpeg);
        w.Zeros(256); // stand-in for sensor data
        return w.ToArray();
    }

    private static byte[] BigEndianDng(byte[] thumbnail, byte[] preview)
    {
        var w = new Writer { BigEndian = true };
        w.Ascii("MM").U16(42).U32(8);
        // IFD0 (thumbnail + SubIFDs) at 8: 5 entries
        var ifd0Size = 2 + 5 * 12 + 4;
        var subIfd = 8 + ifd0Size;
        var subIfdSize = 2 + 4 * 12 + 4;
        var thumbOffset = subIfd + subIfdSize;
        var previewOffset = thumbOffset + thumbnail.Length;
        w.U16(5);
        Entry(w, 0x00FE, 4, 1, 1);
        Entry(w, 0x0112, 3, 1, 1);
        Entry(w, 0x014A, 4, 1, subIfd);
        Entry(w, 0x0201, 4, 1, thumbOffset);
        Entry(w, 0x0202, 4, 1, thumbnail.Length);
        w.U32(0);
        w.U16(4);
        Entry(w, 0x0103, 3, 1, 7);
        Entry(w, 0x0111, 4, 1, previewOffset);
        Entry(w, 0x0115, 3, 1, 3);
        Entry(w, 0x0117, 4, 1, preview.Length);
        w.U32(0);
        w.Bytes(thumbnail).Bytes(preview).Zeros(128);
        return w.ToArray();
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        var w = new Writer { BigEndian = true };
        var length = children.Sum(c => c.Length);
        w.U32(8 + length).Ascii(type);
        foreach (var child in children) w.Bytes(child);
        return w.ToArray();
    }

    private static byte[] Raw(Action<Writer> build)
    {
        var w = new Writer { BigEndian = true };
        build(w);
        return w.ToArray();
    }

    private static byte[] Cr3(byte[] prvw, byte[] full, int orientation)
    {
        var canonUuid = new byte[] { 0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48 };
        var previewUuid = new byte[] { 0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16 };
        var tiff = new Writer();
        tiff.Ascii("II").U16(42).U32(8).U16(1);
        Entry(tiff, 0x0112, 3, 1, orientation);
        tiff.U32(0);

        byte[] Build(long mdatPayload)
        {
            var ftyp = Raw(w => w.U32(24).Ascii("ftyp").Ascii("crx ").U32(1).Ascii("crx ").Ascii("isom"));
            var canon = Box("uuid", canonUuid, Box("CMT1", tiff.ToArray()));
            var stsz = Box("stsz", Raw(w => w.U32(0).U32(0).U32(1).U32(full.Length)));
            var stco = Box("stco", Raw(w => w.U32(0).U32(1).U32(mdatPayload)));
            var trak = Box("trak", Box("mdia", Box("minf", Box("stbl", stsz, stco))));
            var moov = Box("moov", canon, trak);
            var prvwBox = Raw(w => w.U32(8 + 16 + prvw.Length).Ascii("PRVW").U32(0).U16(1).U16(32).U16(24).U16(1).U32(prvw.Length).Bytes(prvw));
            var preview = Box("uuid", previewUuid, new byte[8], prvwBox);
            var prefix = ftyp.Concat(moov).Concat(preview).ToArray();
            var mdat = Box("mdat", full);
            return prefix.Concat(mdat).ToArray();
        }

        var first = Build(0);
        var mdatPayloadOffset = first.Length - full.Length;
        return Build(mdatPayloadOffset);
    }
}
