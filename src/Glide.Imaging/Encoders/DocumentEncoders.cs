using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Glide.Core;

namespace Glide.Imaging.Encoders;

/// <summary>
/// Container/document writers: OpenEXR (half RGBA, ZIP), Photoshop PSD/PSB (one layer + composite, RLE), ICO
/// (one PNG-compressed image) and OpenRaster (.ora). PNG payloads come from the Windows WIC PNG encoder.
/// </summary>
public static class DocumentEncoders
{
    // ------------------------------------------------------------------------------------------------
    // OpenEXR: single-part scanline, HALF channels (B, G, R and A when the image has transparency),
    // ZIP compression (16 scanlines per block). Values are scene-linear (sRGB decoded) and premultiplied
    // by alpha, as the format requires.
    // ------------------------------------------------------------------------------------------------
    public static byte[] Exr(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var alpha = image.HasAlpha;
        string[] channels = alpha ? ["A", "B", "G", "R"] : ["B", "G", "R"];
        var output = new MemoryStream();
        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(20000630); // magic 76 2f 31 01
        writer.Write(2);        // version 2, single-part scanline

        var chlist = new MemoryStream();
        using (var c = new BinaryWriter(chlist, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var name in channels)
            {
                c.Write(Encoding.ASCII.GetBytes(name)); c.Write((byte)0);
                c.Write(1);              // HALF
                c.Write((byte)0);        // pLinear
                c.Write((byte)0); c.Write((byte)0); c.Write((byte)0);
                c.Write(1); c.Write(1);  // x/y sampling
            }
            c.Write((byte)0);
        }
        Attribute(writer, "channels", "chlist", chlist.ToArray());
        Attribute(writer, "compression", "compression", [3]); // ZIP_COMPRESSION
        var box = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(8), w - 1);
        BinaryPrimitives.WriteInt32LittleEndian(box.AsSpan(12), h - 1);
        Attribute(writer, "dataWindow", "box2i", box);
        Attribute(writer, "displayWindow", "box2i", box);
        Attribute(writer, "lineOrder", "lineOrder", [0]);
        Attribute(writer, "pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attribute(writer, "screenWindowCenter", "v2f", new byte[8]);
        Attribute(writer, "screenWindowWidth", "float", BitConverter.GetBytes(1f));
        writer.Write((byte)0); // end of header

        const int linesPerBlock = 16;
        var blocks = (h + linesPerBlock - 1) / linesPerBlock;
        var tablePosition = output.Position;
        for (var i = 0; i < blocks; i++) writer.Write(0L);
        var offsets = new long[blocks];
        var lineBytes = w * channels.Length * 2;
        var raw = new byte[lineBytes * linesPerBlock];
        var reordered = new byte[raw.Length];
        var values = new float[4];
        for (var block = 0; block < blocks; block++)
        {
            var y0 = block * linesPerBlock;
            var lines = Math.Min(linesPerBlock, h - y0);
            var size = lines * lineBytes;
            for (var line = 0; line < lines; line++)
            {
                var y = y0 + line;
                var lineStart = line * lineBytes;
                for (var x = 0; x < w; x++)
                {
                    var o = (y * w + x) * 4;
                    var a = alpha ? image.Bgra[o + 3] / 255f : 1f;
                    var first = 0;
                    if (alpha) values[first++] = a;
                    values[first] = EncoderPixels.SrgbToLinear(image.Bgra[o]) * a;
                    values[first + 1] = EncoderPixels.SrgbToLinear(image.Bgra[o + 1]) * a;
                    values[first + 2] = EncoderPixels.SrgbToLinear(image.Bgra[o + 2]) * a;
                    for (var c = 0; c < channels.Length; c++)
                        BinaryPrimitives.WriteHalfLittleEndian(raw.AsSpan(lineStart + (c * w + x) * 2), (Half)values[c]);
                }
            }
            // ZIP predictor: split even/odd bytes, then delta-encode.
            int t1 = 0, t2 = (size + 1) / 2;
            for (var i = 0; i < size;)
            {
                reordered[t1++] = raw[i++];
                if (i < size) reordered[t2++] = raw[i++];
            }
            var previous = reordered[0];
            for (var i = 1; i < size; i++)
            {
                var current = reordered[i];
                reordered[i] = (byte)(current - previous + 128 + 256);
                previous = current;
            }
            var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(reordered, 0, size);
            offsets[block] = output.Position;
            writer.Write(y0);
            if (compressed.Length < size)
            {
                writer.Write((int)compressed.Length);
                writer.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
            }
            else
            {
                writer.Write(size);
                writer.Write(raw, 0, size);
            }
        }
        writer.Flush();
        var bytes = output.ToArray();
        for (var i = 0; i < blocks; i++)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan((int)tablePosition + i * 8), offsets[i]);
        return bytes;
    }

    private static void Attribute(BinaryWriter writer, string name, string type, byte[] value)
    {
        writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write((byte)0);
        writer.Write(Encoding.ASCII.GetBytes(type)); writer.Write((byte)0);
        writer.Write(value.Length);
        writer.Write(value);
    }

    // ------------------------------------------------------------------------------------------------
    // Photoshop PSD (version 1) / PSB (version 2): RGB, 8 bits, one normal layer ("Background copy" of the
    // image, with transparency) plus the flattened composite; both PackBits (RLE) compressed.
    // ------------------------------------------------------------------------------------------------
    public static byte[] Psd(StraightImage image, bool large)
    {
        int w = image.Width, h = image.Height;
        var output = new MemoryStream();
        var bw = new BigEndianWriter(output);
        bw.Bytes("8BPS"u8);
        bw.U16(large ? 2 : 1);
        bw.Zeros(6);
        bw.U16(4); // R, G, B, A
        bw.U32((uint)h);
        bw.U32((uint)w);
        bw.U16(8);
        bw.U16(3); // RGB
        bw.U32(0); // colour mode data
        bw.U32(0); // image resources

        // Channel planes (R, G, B, A) compressed once and reused for the layer and the composite.
        var planes = new (int[] RowLengths, byte[] Data)[4];
        int[] offsets = [2, 1, 0, 3];
        for (var c = 0; c < 4; c++) planes[c] = PackPlane(image, offsets[c]);
        var countSize = large ? 4 : 2;

        // Layer and mask information.
        var layerInfo = new MemoryStream();
        var li = new BigEndianWriter(layerInfo);
        li.U16(unchecked((ushort)-1)); // one layer; negative: the composite's alpha channel is transparency
        li.U32(0); li.U32(0); li.U32((uint)h); li.U32((uint)w); // top, left, bottom, right
        li.U16(4);
        short[] ids = [0, 1, 2, -1];
        for (var c = 0; c < 4; c++)
        {
            li.U16(unchecked((ushort)ids[c]));
            var length = 2L + (long)h * countSize + planes[c].Data.Length;
            if (large) li.U64((ulong)length); else li.U32(checked((uint)length));
        }
        li.Bytes("8BIM"u8);
        li.Bytes("norm"u8);
        li.U8(255); // opacity
        li.U8(0);   // clipping
        li.U8(0);   // flags (visible)
        li.U8(0);   // filler
        var name = Encoding.ASCII.GetBytes("Layer 1");
        var nameBlock = (1 + name.Length + 3) & ~3;
        li.U32((uint)(4 + 4 + nameBlock)); // extra data: mask, blending ranges, name
        li.U32(0);
        li.U32(0);
        li.U8(name.Length);
        li.Bytes(name);
        li.Zeros(nameBlock - 1 - name.Length);
        for (var c = 0; c < 4; c++)
        {
            li.U16(1); // RLE
            foreach (var count in planes[c].RowLengths) { if (large) li.U32((uint)count); else li.U16(count); }
            li.Bytes(planes[c].Data);
        }
        while (layerInfo.Length % 4 != 0) layerInfo.WriteByte(0);

        var layerInfoBytes = layerInfo.ToArray();
        var lengthSize = large ? 8 : 4;
        var sectionLength = lengthSize + layerInfoBytes.Length + 4; // layer info + global mask info
        if (large) { bw.U64((ulong)sectionLength); bw.U64((ulong)layerInfoBytes.Length); }
        else { bw.U32(checked((uint)sectionLength)); bw.U32(checked((uint)layerInfoBytes.Length)); }
        bw.Bytes(layerInfoBytes);
        bw.U32(0); // global layer mask info

        // Composite image data: one compression field, all row counts, then every plane.
        bw.U16(1);
        for (var c = 0; c < 4; c++)
            foreach (var count in planes[c].RowLengths) { if (large) bw.U32((uint)count); else bw.U16(count); }
        for (var c = 0; c < 4; c++) bw.Bytes(planes[c].Data);
        return output.ToArray();
    }

    private static (int[] RowLengths, byte[] Data) PackPlane(StraightImage image, int channelOffset)
    {
        int w = image.Width, h = image.Height;
        var row = new byte[w];
        var packed = new byte[w + (w + 127) / 128 + 2];
        var lengths = new int[h];
        var data = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) row[x] = image.Bgra[(y * w + x) * 4 + channelOffset];
            var length = PackBits(row, packed);
            lengths[y] = length;
            data.Write(packed, 0, length);
        }
        return (lengths, data.ToArray());
    }

    /// <summary>Apple PackBits: runs of 3..128 as (257 - n, byte), literals of 1..128 as (n - 1, bytes).</summary>
    public static int PackBits(ReadOnlySpan<byte> row, Span<byte> output)
    {
        var o = 0;
        var x = 0;
        var n = row.Length;
        while (x < n)
        {
            var run = 1;
            while (x + run < n && run < 128 && row[x + run] == row[x]) run++;
            if (run >= 3)
            {
                output[o++] = (byte)(257 - run);
                output[o++] = row[x];
                x += run;
                continue;
            }
            var start = x;
            var count = 0;
            while (x < n && count < 128)
            {
                if (x + 2 < n && row[x] == row[x + 1] && row[x] == row[x + 2]) break;
                x++; count++;
            }
            output[o++] = (byte)(count - 1);
            row.Slice(start, count).CopyTo(output[o..]);
            o += count;
        }
        return o;
    }

    // ------------------------------------------------------------------------------------------------
    // ICO: one PNG-compressed 32-bit image. Images larger than 256 x 256 (the format's limit) are scaled
    // down to fit, keeping the aspect ratio, with alpha-weighted area averaging.
    // ------------------------------------------------------------------------------------------------
    public const int MaxIconSide = 256;

    public static byte[] Ico(StraightImage image)
    {
        image = FitWithin(image, MaxIconSide);
        var png = PngBytes(image);
        var output = new byte[22 + png.Length];
        var s = output.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 1); // icon
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 1); // one image
        s[6] = (byte)(image.Width >= 256 ? 0 : image.Width);
        s[7] = (byte)(image.Height >= 256 ? 0 : image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 32); // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(s[14..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[18..], 22);
        png.CopyTo(output, 22);
        return output;
    }

    /// <summary>Scales down (never up) to fit a <paramref name="side"/> x <paramref name="side"/> box.</summary>
    public static StraightImage FitWithin(StraightImage image, int side)
    {
        if (image.Width <= side && image.Height <= side) return image;
        var scale = Math.Min((double)side / image.Width, (double)side / image.Height);
        var w = Math.Clamp((int)Math.Round(image.Width * scale), 1, side);
        var h = Math.Clamp((int)Math.Round(image.Height * scale), 1, side);
        return EncoderPixels.Downscale(image, w, h);
    }

    // ------------------------------------------------------------------------------------------------
    // OpenRaster (.ora, https://www.openraster.org/): mimetype (stored, first), stack.xml, one PNG layer,
    // mergedimage.png and Thumbnails/thumbnail.png (at most 256 px).
    // ------------------------------------------------------------------------------------------------
    public static byte[] OpenRaster(StraightImage image)
    {
        var png = PngBytes(image);
        var thumbnail = image.Width <= MaxIconSide && image.Height <= MaxIconSide ? png : PngBytes(FitWithin(image, MaxIconSide));
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mimetype", "image/openraster"u8.ToArray(), CompressionLevel.NoCompression);
            var stack = string.Format(CultureInfo.InvariantCulture,
                "<?xml version='1.0' encoding='UTF-8'?>\n<image version=\"0.0.5\" w=\"{0}\" h=\"{1}\" xres=\"72\" yres=\"72\">\n" +
                "  <stack>\n    <layer name=\"Layer 1\" src=\"data/layer0.png\" x=\"0\" y=\"0\" opacity=\"1.0\" visibility=\"visible\" composite-op=\"svg:src-over\"/>\n" +
                "  </stack>\n</image>\n", image.Width, image.Height);
            Add(zip, "stack.xml", Encoding.UTF8.GetBytes(stack), CompressionLevel.Optimal);
            // PNG data is already deflated: store it.
            Add(zip, "data/layer0.png", png, CompressionLevel.NoCompression);
            Add(zip, "Thumbnails/thumbnail.png", thumbnail, CompressionLevel.NoCompression);
            Add(zip, "mergedimage.png", png, CompressionLevel.NoCompression);
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data);
    }

    /// <summary>A PNG of <paramref name="image"/> from the Windows WIC PNG encoder (into memory).</summary>
    public static byte[] PngBytes(StraightImage image)
        => NativeImageEncoder.TryEncodeToMemory(EncoderPixels.Premultiply(image.Bgra), image.Width, image.Height, SaveFormatCatalog.WicPng)
           ?? throw new IOException("The Windows PNG encoder (Glide.Native) is unavailable.");

    private sealed class BigEndianWriter(Stream stream)
    {
        public void U8(int v) => stream.WriteByte((byte)v);
        public void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); stream.Write(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); stream.Write(b); }
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); stream.Write(b); }
        public void Bytes(ReadOnlySpan<byte> b) => stream.Write(b);
        public void Zeros(int n) { for (var i = 0; i < n; i++) stream.WriteByte(0); }
    }
}
