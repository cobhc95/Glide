using System.Text;
using Glide.Imaging;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>DICOM, NIfTI, FITS, PFM and gzip'd Radiance HDR fixtures written in-test.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ScientificDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    // ============================================================================================
    // DICOM
    // ============================================================================================

    private const string ImplicitLe = "1.2.840.10008.1.2";
    private const string ExplicitLe = "1.2.840.10008.1.2.1";
    private const string ExplicitBe = "1.2.840.10008.1.2.2";
    private const string Deflated = "1.2.840.10008.1.2.1.99";
    private const string JpegBaseline = "1.2.840.10008.1.2.4.50";
    private const string RleLossless = "1.2.840.10008.1.2.5";

    private sealed class DicomWriter
    {
        public readonly Writer Out;
        private readonly bool _explicit;

        public DicomWriter(bool explicitVr, bool bigEndian)
        {
            _explicit = explicitVr;
            Out = new Writer { BigEndian = bigEndian };
        }

        public DicomWriter Element(int group, int element, string vr, byte[] value)
        {
            if (value.Length % 2 == 1) value = value.Concat(new byte[] { vr is "UI" or "OB" ? (byte)0 : (byte)' ' }).ToArray();
            Out.U16(group).U16(element);
            if (!_explicit)
            {
                Out.U32(value.Length);
            }
            else if (vr is "OB" or "OW" or "SQ" or "UN" or "UT")
            {
                Out.Ascii(vr).U16(0).U32(value.Length);
            }
            else
            {
                Out.Ascii(vr).U16(value.Length);
            }
            Out.Bytes(value);
            return this;
        }

        public DicomWriter Us(int group, int element, int value)
        {
            var w = new Writer { BigEndian = Out.BigEndian };
            w.U16(value);
            return Element(group, element, "US", w.ToArray());
        }

        public DicomWriter Text(int group, int element, string vr, string value) => Element(group, element, vr, Encoding.ASCII.GetBytes(value));

        public DicomWriter Image(int rows, int columns, int bits, int samples, string photometric, int pixelRepresentation = 0)
        {
            Us(0x0028, 0x0002, samples);
            Text(0x0028, 0x0004, "CS", photometric);
            if (samples > 1) Us(0x0028, 0x0006, 0);
            Us(0x0028, 0x0010, rows);
            Us(0x0028, 0x0011, columns);
            Us(0x0028, 0x0100, bits);
            Us(0x0028, 0x0101, bits);
            Us(0x0028, 0x0102, bits - 1);
            Us(0x0028, 0x0103, pixelRepresentation);
            return this;
        }

        /// <summary>A sequence of undefined length holding one undefined-length item, to exercise skipping.</summary>
        public DicomWriter UndefinedSequence()
        {
            Out.U16(0x0008).U16(0x1140);
            if (_explicit) Out.Ascii("SQ").U16(0);
            Out.U32(0xFFFFFFFF);
            Out.U16(0xFFFE).U16(0xE000).U32(0xFFFFFFFF);
            Text(0x0008, 0x1150, "UI", "1.2.3");
            Out.U16(0xFFFE).U16(0xE00D).U32(0);
            Out.U16(0xFFFE).U16(0xE0DD).U32(0);
            return this;
        }
    }

    private static byte[] DicomFile(string transferSyntax, byte[] dataset, bool preamble = true)
    {
        var w = new Writer();
        if (preamble)
        {
            w.Zeros(128).Ascii("DICM");
            var ts = Encoding.ASCII.GetBytes(transferSyntax);
            if (ts.Length % 2 == 1) ts = ts.Concat(new byte[] { 0 }).ToArray();
            w.U16(0x0002).U16(0x0010).Ascii("UI").U16(ts.Length).Bytes(ts);
        }
        w.Bytes(dataset);
        return w.ToArray();
    }

    private static byte[] Words16(bool bigEndian, params int[] values)
    {
        var w = new Writer { BigEndian = bigEndian };
        foreach (var v in values) w.U16(v);
        return w.ToArray();
    }

    [Fact]
    public void Dicom_explicit_le_16bit_with_voi_window_and_sequence()
    {
        var d = new DicomWriter(true, false);
        d.UndefinedSequence();
        d.Image(2, 2, 16, 1, "MONOCHROME2");
        d.Text(0x0028, 0x1050, "DS", "100").Text(0x0028, 0x1051, "DS", "200");
        d.Element(0x7FE0, 0x0010, "OW", Words16(false, 0, 100, 200, 50));
        var pixels = Decode(Temp(_files, DicomFile(ExplicitLe, d.Out.ToArray()), ".dcm"));
        Assert.Equal((2, 2), (pixels.Width, pixels.Height));
        Assert.Equal(0, pixels.At(0, 0).R);
        Assert.Equal(128, pixels.At(1, 0).R);
        Assert.Equal(255, pixels.At(0, 1).R);
        Assert.Equal(64, pixels.At(1, 1).R);
    }

    [Fact]
    public void Dicom_implicit_le_8bit_min_max_stretch()
    {
        var d = new DicomWriter(false, false);
        d.Image(1, 4, 8, 1, "MONOCHROME2");
        d.Element(0x7FE0, 0x0010, "OB", new byte[] { 10, 20, 30, 40 });
        var pixels = Decode(Temp(_files, DicomFile(ImplicitLe, d.Out.ToArray()), ".dicom"));
        Assert.Equal(new[] { 0, 85, 170, 255 }, Enumerable.Range(0, 4).Select(x => pixels.At(x, 0).R).ToArray());
    }

    [Fact]
    public void Dicom_monochrome1_signed_rescaled_is_inverted()
    {
        var d = new DicomWriter(true, false);
        d.Image(1, 4, 16, 1, "MONOCHROME1", pixelRepresentation: 1);
        d.Text(0x0028, 0x1052, "DS", "-1024").Text(0x0028, 0x1053, "DS", "2");
        d.Element(0x7FE0, 0x0010, "OW", Words16(false, unchecked((ushort)-100), 0, 100, 300));
        var pixels = Decode(Temp(_files, DicomFile(ExplicitLe, d.Out.ToArray()), ".ima"));
        // min/max over -1224..-424 -> 0..255, then inverted.
        Assert.Equal(255, pixels.At(0, 0).R);
        Assert.Equal(0, pixels.At(3, 0).R);
        Assert.Equal(255 - (int)Math.Round(200 / 800.0 * 255), pixels.At(1, 0).R);
    }

    [Fact]
    public void Dicom_rgb_and_big_endian()
    {
        var d = new DicomWriter(true, false);
        d.Image(1, 2, 8, 3, "RGB");
        d.Element(0x7FE0, 0x0010, "OB", new byte[] { 255, 0, 0, 0, 0, 255 });
        var rgb = Decode(Temp(_files, DicomFile(ExplicitLe, d.Out.ToArray()), ".dcm"));
        AssertColor(rgb.At(0, 0), 255, 0, 0);
        AssertColor(rgb.At(1, 0), 0, 0, 255);

        var be = new DicomWriter(true, true);
        be.Image(1, 2, 16, 1, "MONOCHROME2");
        be.Element(0x7FE0, 0x0010, "OW", Words16(true, 1000, 3000));
        var big = Decode(Temp(_files, DicomFile(ExplicitBe, be.Out.ToArray()), ".dcm"));
        Assert.Equal(0, big.At(0, 0).R);
        Assert.Equal(255, big.At(1, 0).R);
    }

    [Fact]
    public void Dicom_encapsulated_baseline_jpeg()
    {
        var jpeg = Jpeg(16, 8, new SKColor(200, 200, 200), new SKColor(200, 200, 200));
        var d = new DicomWriter(true, false);
        d.Image(8, 16, 8, 1, "MONOCHROME2");
        var frame = jpeg.Length % 2 == 1 ? jpeg.Concat(new byte[] { 0 }).ToArray() : jpeg;
        d.Out.U16(0x7FE0).U16(0x0010).Ascii("OB").U16(0).U32(0xFFFFFFFF);
        d.Out.U16(0xFFFE).U16(0xE000).U32(0); // empty basic offset table
        d.Out.U16(0xFFFE).U16(0xE000).U32(frame.Length).Bytes(frame);
        d.Out.U16(0xFFFE).U16(0xE0DD).U32(0);
        var pixels = Decode(Temp(_files, DicomFile(JpegBaseline, d.Out.ToArray()), ".dcm"));
        Assert.Equal((16, 8), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(4, 4), 200, 200, 200, 4);
    }

    /// <summary>Removes the JFIF APP0 segment, like encoders that write plain component data for DICOM.</summary>
    private static byte[] WithoutJfif(byte[] jpeg)
    {
        Assert.Equal(0xE0, jpeg[3]);
        var length = (jpeg[4] << 8) | jpeg[5];
        return jpeg.Take(2).Concat(jpeg.Skip(4 + length)).ToArray();
    }

    private byte[] DicomJpeg(string photometric, byte[] jpeg)
    {
        var d = new DicomWriter(true, false);
        d.Image(8, 16, 8, 3, photometric);
        var frame = jpeg.Length % 2 == 1 ? jpeg.Concat(new byte[] { 0 }).ToArray() : jpeg;
        d.Out.U16(0x7FE0).U16(0x0010).Ascii("OB").U16(0).U32(0xFFFFFFFF);
        d.Out.U16(0xFFFE).U16(0xE000).U32(0);
        d.Out.U16(0xFFFE).U16(0xE000).U32(frame.Length).Bytes(frame);
        d.Out.U16(0xFFFE).U16(0xE0DD).U32(0);
        return DicomFile(JpegBaseline, d.Out.ToArray());
    }

    [Fact]
    public void Dicom_rgb_photometric_jpeg_without_jfif_keeps_components_as_rgb()
    {
        // Y/Cb/Cr components of (200, 100, 50): (124, 86, 182). A JPEG with no JFIF/Adobe marker is YCbCr to
        // libjpeg, but DICOM says "RGB" means the three components already are R, G and B.
        var jpeg = WithoutJfif(Jpeg(16, 8, new SKColor(200, 100, 50), new SKColor(200, 100, 50)));
        var rgb = Decode(Temp(_files, DicomJpeg("RGB", jpeg), ".dcm"));
        AssertColor(rgb.At(4, 4), 124, 86, 182, 5);
        // The same bytes labelled YBR_FULL_422 are converted from YCbCr back to the original colour.
        var ybr = Decode(Temp(_files, DicomJpeg("YBR_FULL_422", jpeg), ".dcm"));
        AssertColor(ybr.At(4, 4), 200, 100, 50, 5);
    }

    [Fact]
    public void Dicom_rle_deflated_and_no_preamble()
    {
        // RLE Lossless: one segment, PackBits.
        var segment = new byte[] { 0x03, 10, 20, 30, 40, 0xFD, 50 }; // 4 literals, then 50 x4
        var header = new Writer().U32(1).U32(64);
        header.Zeros(64 - 8);
        var rleFrame = header.ToArray().Concat(segment).Concat(new byte[] { 0 }).ToArray();
        var d = new DicomWriter(true, false);
        d.Image(2, 4, 8, 1, "MONOCHROME2");
        d.Out.U16(0x7FE0).U16(0x0010).Ascii("OB").U16(0).U32(0xFFFFFFFF);
        d.Out.U16(0xFFFE).U16(0xE000).U32(0);
        d.Out.U16(0xFFFE).U16(0xE000).U32(rleFrame.Length).Bytes(rleFrame);
        d.Out.U16(0xFFFE).U16(0xE0DD).U32(0);
        var rle = Decode(Temp(_files, DicomFile(RleLossless, d.Out.ToArray()), ".dcm"));
        Assert.Equal(0, rle.At(0, 0).R);
        Assert.Equal(255, rle.At(3, 1).R);

        var plain = new DicomWriter(true, false);
        plain.Image(1, 2, 8, 1, "MONOCHROME2");
        plain.Element(0x7FE0, 0x0010, "OB", new byte[] { 0, 255 });
        var deflated = Decode(Temp(_files, DicomFile(Deflated, Deflate(plain.Out.ToArray())), ".dcm"));
        Assert.Equal((2, 1), (deflated.Width, deflated.Height));
        Assert.Equal(255, deflated.At(1, 0).R);

        var raw = new DicomWriter(false, false);
        raw.Text(0x0008, 0x0060, "CS", "OT");
        raw.Image(1, 2, 8, 1, "MONOCHROME2");
        raw.Element(0x7FE0, 0x0010, "OB", new byte[] { 0, 255 });
        var bare = Decode(Temp(_files, raw.Out.ToArray(), ".dcm"));
        Assert.Equal(255, bare.At(1, 0).R);
    }

    [Fact]
    public void Dicom_malformed_fails_gracefully()
    {
        var d = new DicomWriter(true, false);
        d.Image(64, 64, 16, 1, "MONOCHROME2");
        d.Element(0x7FE0, 0x0010, "OW", new byte[64]); // far too little pixel data
        AssertFails(Temp(_files, DicomFile(ExplicitLe, d.Out.ToArray()), ".dcm"));
        AssertFails(Temp(_files, new byte[132], ".dcm"));
        var palette = new DicomWriter(true, false);
        palette.Image(1, 2, 8, 1, "PALETTE COLOR");
        palette.Element(0x7FE0, 0x0010, "OB", new byte[] { 0, 1 });
        AssertFails(Temp(_files, DicomFile(ExplicitLe, palette.Out.ToArray()), ".dcm"));
        var lossless = new DicomWriter(true, false);
        lossless.Image(1, 2, 8, 1, "MONOCHROME2");
        lossless.Element(0x7FE0, 0x0010, "OB", new byte[] { 0, 1 });
        AssertFails(Temp(_files, DicomFile("1.2.840.10008.1.2.4.70", lossless.Out.ToArray()), ".dcm"));
    }

    // ============================================================================================
    // NIfTI
    // ============================================================================================

    private static byte[] Nifti1(int nx, int ny, int nz, int datatype, int bitpix, byte[] voxels, bool bigEndian = false, string magic = "n+1\0")
    {
        var w = new Writer { BigEndian = bigEndian };
        w.U32(348).Zeros(36);           // 0..39
        w.U16(3).U16(nx).U16(ny).U16(nz).U16(1).U16(1).U16(1).U16(1); // dim[8] at 40
        w.Zeros(70 - 56);
        w.U16(datatype).U16(bitpix);    // 70, 72
        w.Zeros(108 - 74);
        w.F32(352).F32(0).F32(0);       // vox_offset, scl_slope, scl_inter
        w.Zeros(344 - 120);
        w.Ascii(magic);
        w.Zeros(4);                     // extension flag
        w.Bytes(voxels);
        return w.ToArray();
    }

    private static byte[] Int16Volume(int nx, int ny, int nz, bool bigEndian = false)
    {
        var w = new Writer { BigEndian = bigEndian };
        for (var z = 0; z < nz; z++)
            for (var y = 0; y < ny; y++)
                for (var x = 0; x < nx; x++)
                    w.U16(z == nz / 2 ? x + y * 10 : 999);
        return w.ToArray();
    }

    [Fact]
    public void Nifti_middle_axial_slice_normalised_and_flipped()
    {
        var path = Temp(_files, Nifti1(3, 2, 5, 4, 16, Int16Volume(3, 2, 5)), ".nii");
        var pixels = Decode(path);
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        // Slice values: row0 = 0,1,2 ; row1 = 10,11,12 (stored first row ends up at the bottom).
        Assert.Equal(0, pixels.At(0, 1).R);
        Assert.Equal(255, pixels.At(2, 0).R);
        Assert.Equal((int)(10 / 12.0 * 255 + 0.5), pixels.At(0, 0).R);
    }

    [Fact]
    public void Nifti_gz_big_endian_and_rgb()
    {
        var gz = Temp(_files, Gzip(Nifti1(3, 2, 5, 4, 16, Int16Volume(3, 2, 5, bigEndian: true), bigEndian: true)), ".nii.gz");
        var pixels = Decode(gz);
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        Assert.Equal(255, pixels.At(2, 0).R);

        var rgb = Temp(_files, Nifti1(2, 1, 1, 128, 24, new byte[] { 255, 0, 0, 0, 0, 255 }), ".nii");
        var colour = Decode(rgb);
        AssertColor(colour.At(0, 0), 255, 0, 0);
        AssertColor(colour.At(1, 0), 0, 0, 255);
    }

    [Fact]
    public void Nifti_malformed_fails_gracefully()
    {
        AssertFails(Temp(_files, Nifti1(3, 2, 5, 4, 16, Int16Volume(3, 2, 5), magic: "ni1\0"), ".nii"));
        AssertFails(Temp(_files, Nifti1(300, 200, 50, 4, 16, new byte[100]), ".nii"));
        AssertFails(Temp(_files, Gzip(new byte[200]), ".nii.gz"));
        AssertFails(Temp(_files, new byte[] { 1, 2, 3 }, ".nii"));
    }

    // ============================================================================================
    // FITS
    // ============================================================================================

    private static byte[] FitsHeader(params string[] cards)
    {
        var sb = new StringBuilder();
        foreach (var card in cards.Append("END")) sb.Append(card.PadRight(80));
        while (sb.Length % 2880 != 0) sb.Append(' ');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Card(string key, string value) => key.PadRight(8) + "= " + value.PadLeft(20);

    private static byte[] FitsData(int bitpix, double[] values)
    {
        var w = new Writer { BigEndian = true };
        foreach (var v in values)
        {
            switch (bitpix)
            {
                case 8: w.U8((int)v); break;
                case 16: w.U16((ushort)(short)v); break;
                case 32: w.U32((uint)(int)v); break;
                case 64: w.U64((long)v); break;
                case -32: w.F32((float)v); break;
                default: w.F64(v); break;
            }
        }
        w.Align(2880);
        return w.ToArray();
    }

    private static byte[] Fits(int bitpix, int width, int height, double[] values, params string[] extra)
    {
        var cards = new List<string> { Card("SIMPLE", "T"), Card("BITPIX", bitpix.ToString()), Card("NAXIS", "2"),
            Card("NAXIS1", width.ToString()), Card("NAXIS2", height.ToString()) };
        cards.AddRange(extra);
        return FitsHeader(cards.ToArray()).Concat(FitsData(bitpix, values)).ToArray();
    }

    [Theory]
    [InlineData(8, ".fits")]
    [InlineData(16, ".fit")]
    [InlineData(32, ".fts")]
    [InlineData(64, ".fits")]
    [InlineData(-32, ".fits")]
    [InlineData(-64, ".fits")]
    public void Fits_bitpix_variants_stretch_and_flip(int bitpix, string extension)
    {
        // 4x2, values 0..70 step 10; first stored row is the bottom row.
        var values = Enumerable.Range(0, 8).Select(i => i * 10.0).ToArray();
        var pixels = Decode(Temp(_files, Fits(bitpix, 4, 2, values), extension));
        Assert.Equal((4, 2), (pixels.Width, pixels.Height));
        Assert.Equal(0, pixels.At(0, 1).R);
        Assert.Equal(255, pixels.At(3, 0).R);
        Assert.Equal((int)Math.Round(40 / 70.0 * 255), pixels.At(0, 0).R);
    }

    [Fact]
    public void Fits_bzero_blank_image_extension_and_gzip()
    {
        // Unsigned 16-bit convention: BZERO 32768.
        var values = new double[] { -32768, -32768 + 1000, 32767, -32768 + 500 };
        var unsigned = Decode(Temp(_files, Fits(16, 2, 2, values, Card("BZERO", "32768"), Card("BSCALE", "1")), ".fits"));
        Assert.Equal(0, unsigned.At(0, 1).R);
        Assert.Equal(255, unsigned.At(0, 0).R);

        var blank = Decode(Temp(_files, Fits(16, 3, 1, new double[] { -1, 5, 10 }, Card("BLANK", "-1")), ".fits"));
        Assert.Equal(0, blank.At(0, 0).R);
        Assert.Equal(255, blank.At(2, 0).R);

        var primary = FitsHeader(Card("SIMPLE", "T"), Card("BITPIX", "8"), Card("NAXIS", "0"), Card("EXTEND", "T"));
        var extension = FitsHeader(Card("XTENSION", "'IMAGE   '"), Card("BITPIX", "-32"), Card("NAXIS", "2"), Card("NAXIS1", "2"),
            Card("NAXIS2", "1"), Card("PCOUNT", "0"), Card("GCOUNT", "1"));
        var file = primary.Concat(extension).Concat(FitsData(-32, new double[] { 1.5, 2.5 })).ToArray();
        var ext = Decode(Temp(_files, file, ".fits"));
        Assert.Equal((2, 1), (ext.Width, ext.Height));
        Assert.Equal(255, ext.At(1, 0).R);

        var gz = Decode(Temp(_files, Gzip(Fits(8, 2, 1, new double[] { 0, 9 })), ".fts.gz"));
        Assert.Equal(255, gz.At(1, 0).R);
    }

    [Fact]
    public void Fits_malformed_fails_gracefully()
    {
        var good = Fits(16, 64, 64, new double[64 * 64]);
        AssertFails(Temp(_files, good.Take(2880 + 100).ToArray(), ".fits"));
        AssertFails(Temp(_files, Encoding.ASCII.GetBytes(Card("SIMPLE", "T").PadRight(2880)), ".fits"));
        AssertFails(Temp(_files, Fits(12, 2, 2, new double[4]), ".fits"));
        AssertFails(Temp(_files, Gzip(new byte[10]).Take(15).ToArray(), ".fts.gz"));
    }

    // ============================================================================================
    // PFM and HDR.gz
    // ============================================================================================

    [Fact]
    public void Pfm_colour_little_endian_and_grey_big_endian()
    {
        var w = new Writer();
        w.Ascii("PF\n2 2\n-1.0\n");
        // bottom row first: (0,0,0) (1,1,1) ; top row: (1,0,0) (0.5,0.5,0.5)
        foreach (var v in new float[] { 0, 0, 0, 1, 1, 1, 1, 0, 0, 0.5f, 0.5f, 0.5f }) w.F32(v);
        var pixels = Decode(Temp(_files, w.ToArray(), ".pfm"));
        Assert.Equal((2, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(1, 0), 188, 188, 188, 1);
        AssertColor(pixels.At(0, 1), 0, 0, 0);
        AssertColor(pixels.At(1, 1), 255, 255, 255);

        var g = new Writer { BigEndian = true };
        g.Ascii("Pf\n3 1\n1.0\n");
        foreach (var v in new float[] { 0, 0.5f, 4f }) g.F32(v);
        var grey = Decode(Temp(_files, g.ToArray(), ".pfm"));
        Assert.Equal(new[] { 0, 188, 255 }, Enumerable.Range(0, 3).Select(x => grey.At(x, 0).R).ToArray());

        AssertFails(Temp(_files, Encoding.ASCII.GetBytes("PF\n100 100\n-1.0\n").Concat(new byte[16]).ToArray(), ".pfm"));
        AssertFails(Temp(_files, Encoding.ASCII.GetBytes("PF\nx y\n-1.0\n"), ".pfm"));
    }

    [Fact]
    public void Hdr_gz_decodes_and_truncation_fails()
    {
        var hdr = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 2\n")
            .Concat(new byte[] { 128, 0, 0, 129, 0, 0, 128, 129 }).ToArray();
        var pixels = Decode(Temp(_files, Gzip(hdr), ".hdr.gz"));
        Assert.Equal((2, 1), (pixels.Width, pixels.Height));
        Assert.Equal(255, pixels.At(0, 0).R);
        Assert.Equal(255, pixels.At(1, 0).B);
        AssertFails(Temp(_files, Gzip(hdr).Take(12).ToArray(), ".hdr.gz"));
    }
}
