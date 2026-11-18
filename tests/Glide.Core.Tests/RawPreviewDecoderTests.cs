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
[Trait("Area", "Decode")]
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

    // -- Uncompressed RGB previews and the bounded scan (5.0.7) -------------------------------------

    /// <summary>Solid-colour chunky RGB strip data (8 or 16 bits, little-endian).</summary>
    private static byte[] RgbStrip(int width, int height, int bits, (int R, int G, int B) left, (int R, int G, int B) right, int max = 255)
    {
        var w = new Writer();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = x < width / 2 ? left : right;
                foreach (var v in new[] { c.R, c.G, c.B })
                    if (bits == 8) w.U8(v); else w.U16(v * max / 255);
            }
        return w.ToArray();
    }

    private static Field[] RgbIfd(int width, int height, int bits, int blob, int subfileType = 1) => new[]
    {
        new Field(0x00FE, 4, subfileType), new Field(0x0100, 4, width), new Field(0x0101, 4, height),
        new Field(0x0102, 3, bits, bits, bits), new Field(0x0103, 3, 1), new Field(0x0106, 3, 2),
        new Field(0x0111, 4, Blob(blob)), new Field(0x0115, 3, 3), new Field(0x0117, 4, (long)width * height * 3 * bits / 8),
    };

    [Fact]
    public void Largest_preview_wins_even_when_it_is_an_uncompressed_rgb_subifd()
    {
        // MEF/3FR shape: a small JPEG thumbnail in IFD0, a larger uncompressed RGB preview in a SubIFD.
        var jpeg = Jpeg(16, 12, Blue, Blue);
        var rgb = RgbStrip(64, 48, 8, (255, 0, 0), (0, 0, 255));
        var file = Tiff(new[]
        {
            new[] { new Field(0x0112, 3, 6), new Field(0x014A, 4, IfdRef(1)), new Field(0x0201, 4, Blob(0)), new Field(0x0202, 4, jpeg.Length) },
            RgbIfd(64, 48, 8, blob: 1),
        }, jpeg, rgb);
        var pixels = Decode(Temp(_files, file, ".mef"));
        Assert.Equal((48, 64), (pixels.Width, pixels.Height)); // container orientation 6 applies
        AssertColor(pixels.At(24, 8), 255, 0, 0);
        AssertColor(pixels.At(24, 56), 0, 0, 255);
    }

    [Fact]
    public void Sixteen_bit_rgb_preview_is_scaled_by_its_own_range()
    {
        // Kodak DCR stores ~10-bit code values in 16-bit samples: full scale must still reach white.
        var rgb = RgbStrip(32, 24, 16, (255, 255, 255), (0, 128, 0), max: 1023);
        var file = Tiff(new[] { RgbIfd(32, 24, 16, blob: 0) }, rgb);
        var pixels = Decode(Temp(_files, file, ".dcr"));
        Assert.Equal((32, 24), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(4, 4), 255, 255, 255, 1);
        AssertColor(pixels.At(28, 4), 0, 128, 0, 1);
    }

    [Fact]
    public void Equal_size_previews_prefer_eight_bit_rgb_over_sixteen_bit()
    {
        // Hasselblad FFF: IFD0 8-bit and a SubIFD 16-bit preview of the same size.
        var eight = RgbStrip(32, 24, 8, (255, 0, 0), (255, 0, 0));
        var sixteen = RgbStrip(32, 24, 16, (0, 0, 255), (0, 0, 255), max: 65535);
        var ifd0 = RgbIfd(32, 24, 8, blob: 0).Append(new Field(0x014A, 4, IfdRef(1))).ToArray();
        var file = Tiff(new[] { ifd0, RgbIfd(32, 24, 16, blob: 1) }, eight, sixteen);
        AssertColor(Decode(Temp(_files, file, ".fff")).At(16, 12), 255, 0, 0);
    }

    [Fact]
    public void Dng_with_only_a_small_rgb_thumbnail_is_left_to_the_platform_codec()
    {
        var rgb = RgbStrip(32, 24, 8, (255, 0, 0), (255, 0, 0));
        var ifd0 = RgbIfd(32, 24, 8, blob: 0).Append(new Field(0xC612, 1, 1, 4, 0, 0)).ToArray();
        AssertFails(Temp(_files, Tiff(new[] { ifd0 }, rgb), ".dng"), "small DNG thumbnail");
        // The same container without DNGVersion (e.g. ERF) shows its thumbnail.
        Assert.Equal(32, Decode(Temp(_files, Tiff(new[] { RgbIfd(32, 24, 8, blob: 0) }, rgb), ".erf")).Width);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fallback_scan_skips_declared_sensor_data(bool hiddenInSensorData)
    {
        // A JPEG inside the CFA strip is sensor data, not a preview; outside any declared data it is found.
        var thumb = RgbStrip(8, 6, 8, (0, 255, 0), (0, 255, 0));
        var jpeg = Jpeg(64, 48, Red, Blue);
        var sensor = new byte[512].Concat(hiddenInSensorData ? jpeg : Array.Empty<byte>()).Concat(new byte[512]).ToArray();
        var loose = hiddenInSensorData ? new byte[16] : jpeg;
        var cfa = new[]
        {
            new Field(0x00FE, 4, 0), new Field(0x0100, 4, 16), new Field(0x0101, 4, 16), new Field(0x0102, 3, 16), new Field(0x0103, 3, 1),
            new Field(0x0106, 3, 32803), new Field(0x0111, 4, Blob(1)), new Field(0x0115, 3, 1), new Field(0x0117, 4, sensor.Length),
        };
        var ifd0 = RgbIfd(8, 6, 8, blob: 0).Append(new Field(0x014A, 4, IfdRef(1))).ToArray();
        var pixels = Decode(Temp(_files, Tiff(new[] { ifd0, cfa }, thumb, sensor, loose), ".3fr"));
        Assert.Equal(hiddenInSensorData ? 8 : 64, pixels.Width);
    }

    [Fact]
    public void Phase_one_makernote_is_not_scanned()
    {
        // IIQ: the raw lives in the "IIII" + "CwaR" makernote; IFD0 holds the uncompressed preview.
        var thumb = RgbStrip(16, 12, 8, (0, 255, 0), (0, 255, 0));
        var makerNote = "IIIICwaR"u8.ToArray().Concat(new byte[256]).Concat(Jpeg(64, 48, Red, Blue)).Concat(new byte[256]).ToArray();
        var ifd0 = RgbIfd(16, 12, 8, blob: 0).Append(new Field(0x8769, 4, IfdRef(1))).ToArray();
        var exif = new[] { new Field(0x927C, 7, Blob(1)) { Count = makerNote.Length } };
        var pixels = Decode(Temp(_files, Tiff(new[] { ifd0, exif }, thumb, makerNote), ".iiq"));
        Assert.Equal((16, 12), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(8, 6), 0, 255, 0);
    }

    [Fact]
    public void Truncated_rgb_preview_fails_gracefully()
    {
        var rgb = RgbStrip(32, 24, 8, (255, 0, 0), (255, 0, 0));
        var file = Tiff(new[] { RgbIfd(32, 24, 8, blob: 0) }, rgb);
        AssertFails(Temp(_files, file.Take(file.Length - 100).ToArray(), ".erf"), "truncated rgb");
    }

    // -- Kodak compression 65000 previews, Epson maker-note previews (5.0.7) -----------------------

    /// <summary>
    /// Encodes blocks of Kodak 65000 differences the way kodak_65000_decode reads them: a nibble length per value,
    /// then the values LSB-first in big-endian 16-bit words, fetched 32 bits at a time (16 first when size % 8 == 4).
    /// </summary>
    private static byte[] Kodak65000(IEnumerable<int[]> blocks)
    {
        var w = new Writer();
        foreach (var block in blocks)
        {
            var size = (block.Length + 3) & ~3;
            var values = block.Concat(new int[size - block.Length]).ToArray();
            var lengths = values.Select(v => v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(v))).ToArray();
            for (var i = 0; i < size; i += 2) w.U8(lengths[i] | lengths[i + 1] << 4);
            var bits = new List<bool>();
            for (var i = 0; i < size; i++)
            {
                var raw = values[i] > 0 ? values[i] : values[i] + (1 << lengths[i]) - 1;
                for (var b = 0; b < lengths[i]; b++) bits.Add((raw >> b & 1) != 0);
            }
            var next = 0;
            void Word()
            {
                var word = 0;
                for (var b = 0; b < 16; b++, next++) if (next < bits.Count && bits[next]) word |= 1 << b;
                w.U8(word >> 8).U8(word & 0xFF);
            }
            var available = 0;
            if ((size & 7) == 4) { Word(); available = 16; }
            foreach (var length in lengths)
            {
                if (available < length) { Word(); Word(); available += 32; }
                available -= length;
            }
        }
        return w.ToArray();
    }

    private static Field[] KodakIfd(int width, int height, int photometric, int blob, int length) => new[]
    {
        new Field(0x00FE, 4, 1), new Field(0x0100, 4, width), new Field(0x0101, 4, height),
        new Field(0x0102, 3, 12, 12, 12), new Field(0x0103, 3, 65000), new Field(0x0106, 3, photometric),
        new Field(0x0111, 4, Blob(blob)), new Field(0x0115, 3, 3), new Field(0x0117, 4, length),
    };

    [Fact]
    public void Kodak_65000_rgb_preview_decodes()
    {
        // 4x2, left half red, right half blue: per-channel running differences per row.
        var row = new[] { 4095, 0, 0, 0, 0, 0, -4095, 0, 4095, 0, 0, 0 };
        var strip = Kodak65000(new[] { row, row });
        var pixels = Decode(Temp(_files, Tiff(new[] { KodakIfd(4, 2, 2, blob: 0, strip.Length) }, strip), ".dcr"));
        Assert.Equal((4, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 1), 255, 0, 0, 1);
        AssertColor(pixels.At(3, 1), 0, 0, 255, 1);
    }

    /// <summary>A Kodak 65000 block stored raw: absolute 12-bit values packed 8 per six big-endian words.</summary>
    private static byte[] Kodak65000Raw(int[] values)
    {
        var v = values.Concat(new int[(8 - values.Length % 8) % 8]).ToArray();
        var w = new Writer();
        for (var k = 0; k < v.Length; k += 8)
        {
            int[] high = { v[k] >> 8, v[k + 1] >> 8, v[k] >> 4 & 15, v[k + 1] >> 4 & 15, v[k] & 15, v[k + 1] & 15 };
            for (var j = 0; j < 6; j++) { var raw = high[j] << 12 | (v[k + 2 + j] & 0xFFF); w.U8(raw >> 8).U8(raw & 0xFF); }
        }
        return w.ToArray();
    }

    [Fact]
    public void Kodak_65000_raw_block_holds_absolute_values()
    {
        // 86L57188.DCR: most blocks of its 760x504 RGB preview are stored raw; they are values, not differences.
        var raw = Kodak65000Raw(new[] { 4095, 0, 0, 4095, 0, 0, 0, 0, 4095, 0, 0, 4095 });
        var coded = Kodak65000(new[] { new[] { 4095, 0, 0, 0, 0, 0, -4095, 0, 4095, 0, 0, 0 } });
        var strip = raw.Concat(coded).ToArray();
        var pixels = Decode(Temp(_files, Tiff(new[] { KodakIfd(4, 2, 2, blob: 0, strip.Length) }, strip), ".dcr"));
        foreach (var y in new[] { 0, 1 })
        {
            AssertColor(pixels.At(1, y), 255, 0, 0, 1);
            AssertColor(pixels.At(3, y), 0, 0, 255, 1);
        }
    }

    [Fact]
    public void Kodak_65000_ycbcr_preview_with_an_odd_width_decodes()
    {
        // DCS Pro 14n shape: YCbCr 2x2 blocks; a declared 3x2 preview is coded 4 columns wide (1125x750 holds 1126).
        // Pair 0: Y 800 (white after scaling), pair 1: Y 400; no chroma.
        var block = new[] { 800, 0, 800, 0, 0, 0, -400, 0, -400, 0, 0, 0 };
        var strip = Kodak65000(new[] { block });
        var pixels = Decode(Temp(_files, Tiff(new[] { KodakIfd(3, 2, 6, blob: 0, strip.Length) }, strip), ".dcr"));
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 255, 255, 1);
        AssertColor(pixels.At(1, 1), 255, 255, 255, 1);
        AssertColor(pixels.At(2, 0), 127, 127, 127, 1);
    }

    [Fact]
    public void Kodak_65000_preview_beats_a_smaller_uncompressed_one_and_a_corrupt_one_falls_back()
    {
        var thumb = RgbStrip(2, 2, 8, (0, 255, 0), (0, 255, 0));
        var row = new[] { 4095, 0, 0, 0, 0, 0, -4095, 0, 4095, 0, 0, 0 };
        var strip = Kodak65000(new[] { row, row });
        var ifd0 = RgbIfd(2, 2, 8, blob: 0).Append(new Field(0x014A, 4, IfdRef(1))).ToArray();
        Assert.Equal(4, Decode(Temp(_files, Tiff(new[] { ifd0, KodakIfd(4, 2, 2, blob: 1, strip.Length) }, thumb, strip), ".dcr")).Width);
        // A strip that ends early cannot be decoded: the uncompressed thumbnail is shown instead of nothing.
        var broken = Tiff(new[] { ifd0, KodakIfd(4, 2, 2, blob: 1, strip.Length) }, thumb, strip.Take(3).ToArray());
        var pixels = Decode(Temp(_files, broken, ".dcr"), checkProbe: false);
        Assert.Equal((2, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(1, 1), 0, 255, 0);
    }

    [Fact]
    public void Epson_maker_note_preview_beats_the_ifd0_thumbnail()
    {
        // ERF: IFD0 holds a 160x120 RGB thumbnail; the 640x424 JPEG lives in the "EPSON" maker note (tag 0x0280)
        // with its first byte stored as 0xEE instead of 0xFF. Maker-note offsets are file-relative.
        var thumb = RgbStrip(16, 12, 8, (0, 255, 0), (0, 255, 0));
        var jpeg = Jpeg(64, 48, Red, Blue);
        var stored = jpeg.ToArray();
        stored[0] = 0xEE;
        byte[] MakerNote(long jpegOffset) => new Writer().Ascii("EPSON").U8(0).U8(1).U8(0)
            .U16(1).U16(0x0280).U16(7).U32(stored.Length).U32(jpegOffset).U32(0).ToArray();
        byte[] Build(long jpegOffset)
        {
            var ifd0 = RgbIfd(16, 12, 8, blob: 0).Append(new Field(0x8769, 4, IfdRef(1))).ToArray();
            var note = MakerNote(jpegOffset);
            var exif = new[] { new Field(0x927C, 7, Blob(1)) { Count = note.Length } };
            return Tiff(new[] { ifd0, exif }, thumb, note, stored);
        }
        var first = Build(0);
        var at = first.AsSpan().IndexOf(stored.AsSpan(0, 64));
        var pixels = Decode(Temp(_files, Build(at), ".erf"));
        Assert.Equal((64, 48), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(8, 24), 255, 0, 0, 16);
        AssertColor(pixels.At(56, 24), 0, 0, 255, 16);
    }

    // A tiny little-endian TIFF writer: each IFD is a list of fields; values may reference another IFD
    // (IfdRef) or a data blob appended after the IFDs (Blob). IFDs are not chained (SubIFD/Exif pointers).
    private const long RefBase = -1_000_000;
    private static long IfdRef(int index) => RefBase - index;
    private static long Blob(int index) => RefBase - 1000 - index;

    private sealed record Field(int Tag, int Type, params long[] Values)
    {
        /// <summary>Set for an UNDEFINED blob: the count, with the single value being the blob pointer.</summary>
        public int? Count { get; init; }
    }

    private static byte[] Tiff(Field[][] ifds, params byte[][] blobs)
    {
        static int Size(int type) => type switch { 1 or 2 or 7 => 1, 3 => 2, _ => 4 };
        static long Extra(Field f) => f.Count is null && f.Values.Length * Size(f.Type) > 4 ? (f.Values.Length * Size(f.Type) + 1) / 2 * 2 : 0;
        var ifdOffsets = new long[ifds.Length];
        long position = 8;
        for (var i = 0; i < ifds.Length; i++)
        {
            ifdOffsets[i] = position;
            position += 2 + ifds[i].Length * 12 + 4 + ifds[i].Sum(Extra);
        }
        var blobOffsets = new long[blobs.Length];
        for (var i = 0; i < blobs.Length; i++) { blobOffsets[i] = position; position += (blobs[i].Length + 1) / 2 * 2; }
        long Resolve(long v) => v > RefBase ? v : v > RefBase - 1000 ? ifdOffsets[RefBase - v] : blobOffsets[RefBase - 1000 - v];

        var w = new Writer();
        w.Ascii("II").U16(42).U32(ifdOffsets.Length > 0 ? ifdOffsets[0] : 0);
        for (var i = 0; i < ifds.Length; i++)
        {
            var fields = ifds[i].OrderBy(f => f.Tag).ToArray();
            var extraAt = ifdOffsets[i] + 2 + fields.Length * 12 + 4;
            var extras = new Writer();
            w.U16(fields.Length);
            foreach (var f in fields)
            {
                var values = f.Values.Select(Resolve).ToArray();
                w.U16(f.Tag).U16(f.Type).U32(f.Count ?? values.Length);
                if (f.Count is not null) { w.U32(values[0]); continue; }
                var bytes = values.Length * Size(f.Type);
                var target = bytes > 4 ? extras : w;
                if (bytes > 4) w.U32(extraAt + extras.Length);
                foreach (var v in values)
                    if (Size(f.Type) == 1) target.U8((int)v); else if (Size(f.Type) == 2) target.U16((int)v); else target.U32(v);
                if (bytes <= 4) w.Zeros(4 - bytes);
                else extras.Align(2);
            }
            w.U32(0).Bytes(extras.ToArray());
        }
        foreach (var blob in blobs) w.Bytes(blob).Align(2);
        return w.ToArray();
    }
}
