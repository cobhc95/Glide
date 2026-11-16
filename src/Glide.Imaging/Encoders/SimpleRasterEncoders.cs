using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Glide.Core;

namespace Glide.Imaging.Encoders;

/// <summary>
/// Small pure-C# writers for the simple raster formats Glide also reads: QOI, PNM (PPM/PGM/PBM/PAM), PFM, TGA,
/// WBMP, XBM, Radiance HDR, SGI, XPM, DDS and KTX. Every writer takes straight-alpha BGRA; formats without
/// transparency receive pixels already composited over the save background.
/// </summary>
public static class SimpleRasterEncoders
{
    // ------------------------------------------------------------------------------------------------
    // QOI (https://qoiformat.org/qoi-specification.pdf)
    // ------------------------------------------------------------------------------------------------
    public static byte[] Qoi(StraightImage image)
    {
        var channels = image.HasAlpha ? 4 : 3;
        var output = new MemoryStream(14 + image.Width * image.Height + 8);
        Span<byte> header = stackalloc byte[14];
        "qoif"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)image.Height);
        header[12] = (byte)channels;
        header[13] = 0; // sRGB with linear alpha
        output.Write(header);

        Span<uint> index = stackalloc uint[64];
        index.Clear();
        uint previous = 0xFF000000; // a=255, r=g=b=0, packed as ARGB
        var run = 0;
        var px = image.Bgra;
        var total = image.Width * image.Height;
        for (var i = 0; i < total; i++)
        {
            var o = i * 4;
            byte b = px[o], g = px[o + 1], r = px[o + 2], a = px[o + 3];
            var current = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            if (current == previous)
            {
                run++;
                if (run == 62 || i == total - 1) { output.WriteByte((byte)(0xC0 | (run - 1))); run = 0; }
                continue;
            }
            if (run > 0) { output.WriteByte((byte)(0xC0 | (run - 1))); run = 0; }
            var hash = (r * 3 + g * 5 + b * 7 + a * 11) % 64;
            if (index[hash] == current)
            {
                output.WriteByte((byte)hash);
            }
            else
            {
                index[hash] = current;
                var pa = (byte)(previous >> 24);
                if (a == pa)
                {
                    var pr = (byte)(previous >> 16); var pg = (byte)(previous >> 8); var pb = (byte)previous;
                    var dr = (sbyte)(byte)(r - pr); var dg = (sbyte)(byte)(g - pg); var db = (sbyte)(byte)(b - pb);
                    var drg = dr - dg; var dbg = db - dg;
                    if (dr is >= -2 and <= 1 && dg is >= -2 and <= 1 && db is >= -2 and <= 1)
                        output.WriteByte((byte)(0x40 | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2)));
                    else if (dg is >= -32 and <= 31 && drg is >= -8 and <= 7 && dbg is >= -8 and <= 7)
                    {
                        output.WriteByte((byte)(0x80 | (dg + 32)));
                        output.WriteByte((byte)(((drg + 8) << 4) | (dbg + 8)));
                    }
                    else { output.WriteByte(0xFE); output.WriteByte(r); output.WriteByte(g); output.WriteByte(b); }
                }
                else { output.WriteByte(0xFF); output.WriteByte(r); output.WriteByte(g); output.WriteByte(b); output.WriteByte(a); }
            }
            previous = current;
        }
        output.Write([0, 0, 0, 0, 0, 0, 0, 1]);
        return output.ToArray();
    }

    // ------------------------------------------------------------------------------------------------
    // Netpbm: P6 (PPM), P5 (PGM), P4 (PBM), P7 (PAM)
    // ------------------------------------------------------------------------------------------------
    public static byte[] Pnm(StraightImage image, SaveFormat format)
    {
        int w = image.Width, h = image.Height, n = w * h;
        var px = image.Bgra;
        if (format.Id == SaveFormatCatalog.Pbm.Id)
        {
            var rowBytes = (w + 7) / 8;
            var header = Ascii($"P4\n{w} {h}\n");
            var output = new byte[header.Length + rowBytes * h];
            header.CopyTo(output, 0);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (image.Luma(y * w + x) < 128) output[header.Length + y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7)); // 1 = black
            return output;
        }
        if (format.Id == SaveFormatCatalog.Pgm.Id)
        {
            var header = Ascii($"P5\n{w} {h}\n255\n");
            var output = new byte[header.Length + n];
            header.CopyTo(output, 0);
            for (var i = 0; i < n; i++) output[header.Length + i] = (byte)image.Luma(i);
            return output;
        }
        if (format.Id == SaveFormatCatalog.Pam.Id)
        {
            var alpha = image.HasAlpha;
            var depth = alpha ? 4 : 3;
            var header = Ascii($"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH {depth}\nMAXVAL 255\nTUPLTYPE {(alpha ? "RGB_ALPHA" : "RGB")}\nENDHDR\n");
            var output = new byte[header.Length + (long)n * depth];
            header.CopyTo(output, 0);
            var d = header.Length;
            for (var i = 0; i < n; i++)
            {
                var o = i * 4;
                output[d++] = px[o + 2]; output[d++] = px[o + 1]; output[d++] = px[o];
                if (alpha) output[d++] = px[o + 3];
            }
            return output;
        }
        {
            var header = Ascii($"P6\n{w} {h}\n255\n");
            var output = new byte[header.Length + (long)n * 3];
            header.CopyTo(output, 0);
            var d = header.Length;
            for (var i = 0; i < n; i++)
            {
                var o = i * 4;
                output[d++] = px[o + 2]; output[d++] = px[o + 1]; output[d++] = px[o];
            }
            return output;
        }
    }

    /// <summary>PFM: little-endian RGB float, bottom-up rows, scene-linear values (sRGB decoded).</summary>
    public static byte[] Pfm(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var header = Ascii($"PF\n{w} {h}\n-1.0\n");
        var output = new byte[header.Length + (long)w * h * 12];
        header.CopyTo(output, 0);
        var d = header.Length;
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(d), EncoderPixels.SrgbToLinear(image.Bgra[o + 2]));
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(d + 4), EncoderPixels.SrgbToLinear(image.Bgra[o + 1]));
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(d + 8), EncoderPixels.SrgbToLinear(image.Bgra[o]));
                d += 12;
            }
        }
        return output;
    }

    // ------------------------------------------------------------------------------------------------
    // TGA: type 10 (RLE true colour), 32-bit BGRA, top-left origin, TGA 2.0 footer.
    // ------------------------------------------------------------------------------------------------
    public static byte[] Tga(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var output = new MemoryStream(18 + w * h * 4 / 2 + 26);
        Span<byte> header = stackalloc byte[18];
        header.Clear();
        header[2] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)h);
        header[16] = 32;
        header[17] = 0x28; // 8 alpha bits, top-left origin
        output.Write(header);
        var px = image.Bgra;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var x = 0;
            while (x < w)
            {
                // Run of identical pixels (packets never cross rows, as the TGA spec recommends).
                var run = 1;
                while (x + run < w && run < 128 && SamePixel(px, row + x, row + x + run)) run++;
                if (run > 1)
                {
                    output.WriteByte((byte)(0x80 | (run - 1)));
                    output.Write(px, (row + x) * 4, 4);
                    x += run;
                    continue;
                }
                var raw = 1;
                while (x + raw < w && raw < 128 && !(x + raw + 1 < w && SamePixel(px, row + x + raw, row + x + raw + 1))) raw++;
                output.WriteByte((byte)(raw - 1));
                output.Write(px, (row + x) * 4, raw * 4);
                x += raw;
            }
        }
        Span<byte> footer = stackalloc byte[26];
        footer.Clear();
        "TRUEVISION-XFILE.\0"u8.CopyTo(footer[8..]);
        output.Write(footer);
        return output.ToArray();
    }

    private static bool SamePixel(byte[] px, int a, int b)
        => BinaryPrimitives.ReadUInt32LittleEndian(px.AsSpan(a * 4)) == BinaryPrimitives.ReadUInt32LittleEndian(px.AsSpan(b * 4));

    // ------------------------------------------------------------------------------------------------
    // WBMP (type 0) and XBM: black and white, luma threshold 50 %.
    // ------------------------------------------------------------------------------------------------
    public static byte[] Wbmp(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var output = new MemoryStream();
        output.WriteByte(0); // type 0
        output.WriteByte(0); // fixed header
        WriteMultiByte(output, w);
        WriteMultiByte(output, h);
        var rowBytes = (w + 7) / 8;
        var row = new byte[rowBytes];
        for (var y = 0; y < h; y++)
        {
            Array.Clear(row);
            for (var x = 0; x < w; x++)
                if (image.Luma(y * w + x) >= 128) row[x >> 3] |= (byte)(0x80 >> (x & 7)); // 1 = white
            output.Write(row);
        }
        return output.ToArray();
    }

    private static void WriteMultiByte(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var count = 0;
        do { buffer[count++] = (byte)(value & 0x7F); value >>= 7; } while (value > 0);
        for (var i = count - 1; i >= 0; i--) output.WriteByte((byte)(buffer[i] | (i > 0 ? 0x80 : 0)));
    }

    public static byte[] Xbm(StraightImage image, string name)
    {
        int w = image.Width, h = image.Height;
        var id = CIdentifier(name);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"#define {id}_width {w}\n#define {id}_height {h}\nstatic unsigned char {id}_bits[] = {{\n");
        var rowBytes = (w + 7) / 8;
        var count = 0;
        var total = rowBytes * h;
        for (var y = 0; y < h; y++)
        {
            for (var bx = 0; bx < rowBytes; bx++)
            {
                var value = 0;
                for (var bit = 0; bit < 8; bit++)
                {
                    var x = bx * 8 + bit;
                    if (x < w && image.Luma(y * w + x) < 128) value |= 1 << bit; // LSB first, 1 = black
                }
                text.Append(count % 12 == 0 ? "   " : " ");
                text.Append(CultureInfo.InvariantCulture, $"0x{value:x2}");
                count++;
                if (count < total) text.Append(',');
                if (count % 12 == 0 || count == total) text.Append('\n');
            }
        }
        text.Append("};\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>A C identifier from a file name ("my photo-1" -> "my_photo_1").</summary>
    public static string CIdentifier(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name)
            builder.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
        if (builder.Length == 0 || char.IsDigit(builder[0])) builder.Insert(0, "image_");
        return builder.ToString();
    }

    // ------------------------------------------------------------------------------------------------
    // Radiance HDR (RGBE, new-style scanline RLE). Glide shows HDR values as display values (no transfer
    // curve), so 8-bit pixels are written as value / 255.
    // ------------------------------------------------------------------------------------------------
    public static byte[] Hdr(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var output = new MemoryStream();
        output.Write(Ascii($"#?RADIANCE\n# Written by Glide Image Viewer\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n"));
        var rgbe = new byte[w * 4];
        var channel = new byte[w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                ToRgbe(image.Bgra[o + 2] / 255f, image.Bgra[o + 1] / 255f, image.Bgra[o] / 255f, rgbe.AsSpan(x * 4, 4));
            }
            if (w < 8 || w > 32767)
            {
                output.Write(rgbe);
                continue;
            }
            output.Write([2, 2, (byte)(w >> 8), (byte)(w & 0xFF)]);
            for (var c = 0; c < 4; c++)
            {
                for (var x = 0; x < w; x++) channel[x] = rgbe[x * 4 + c];
                WriteHdrRle(output, channel);
            }
        }
        return output.ToArray();
    }

    private static void ToRgbe(float r, float g, float b, Span<byte> rgbe)
    {
        var max = Math.Max(r, Math.Max(g, b));
        if (max < 1e-32f) { rgbe.Clear(); return; }
        var exponent = (int)Math.Floor(Math.Log2(max)) + 1;
        var scale = (float)Math.ScaleB(1.0, -exponent) * 256f;
        // Guard against rounding up to 256 in the mantissa.
        if (max * scale >= 255.5f) { exponent++; scale *= 0.5f; }
        rgbe[0] = (byte)Math.Clamp((int)(r * scale + 0.5f), 0, 255);
        rgbe[1] = (byte)Math.Clamp((int)(g * scale + 0.5f), 0, 255);
        rgbe[2] = (byte)Math.Clamp((int)(b * scale + 0.5f), 0, 255);
        rgbe[3] = (byte)(exponent + 128);
    }

    private static void WriteHdrRle(Stream output, byte[] data)
    {
        var x = 0;
        var n = data.Length;
        while (x < n)
        {
            // Find the next run of >= 4 identical bytes.
            var runStart = x;
            var runLength = 0;
            while (runStart < n)
            {
                runLength = 1;
                while (runLength < 127 && runStart + runLength < n && data[runStart + runLength] == data[runStart]) runLength++;
                if (runLength >= 4) break;
                runStart += runLength;
                runLength = 0;
            }
            // Literals before the run, in chunks of at most 128.
            while (x < runStart)
            {
                var count = Math.Min(128, runStart - x);
                output.WriteByte((byte)count);
                output.Write(data, x, count);
                x += count;
            }
            if (runLength >= 4)
            {
                output.WriteByte((byte)(128 + runLength));
                output.WriteByte(data[runStart]);
                x = runStart + runLength;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // SGI image (RLE, 8 bits per channel, RGB or RGBA, bottom-up rows).
    // ------------------------------------------------------------------------------------------------
    public static byte[] Sgi(StraightImage image, string name)
    {
        int w = image.Width, h = image.Height;
        var channels = image.HasAlpha ? 4 : 3;
        var header = new byte[512];
        BinaryPrimitives.WriteUInt16BigEndian(header, 474);
        header[2] = 1; // RLE
        header[3] = 1; // 1 byte per channel
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)channels);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 255);
        var title = Encoding.ASCII.GetBytes(name.Length > 79 ? name[..79] : name);
        for (var i = 0; i < title.Length; i++) header[24 + i] = title[i] is >= 32 and < 127 ? title[i] : (byte)'_';

        var tableEntries = h * channels;
        var starts = new uint[tableEntries];
        var lengths = new uint[tableEntries];
        var data = new MemoryStream();
        var dataOffset = 512 + tableEntries * 8;
        var row = new byte[w];
        var packed = new byte[w * 2 + 2];
        int[] channelOffset = [2, 1, 0, 3];
        for (var c = 0; c < channels; c++)
        {
            for (var y = 0; y < h; y++)
            {
                var sourceRow = h - 1 - y; // SGI rows run bottom-up
                for (var x = 0; x < w; x++) row[x] = image.Bgra[(sourceRow * w + x) * 4 + channelOffset[c]];
                var length = SgiPackRow(row, packed);
                var entry = c * h + y;
                starts[entry] = (uint)(dataOffset + data.Length);
                lengths[entry] = (uint)length;
                data.Write(packed, 0, length);
            }
        }
        var output = new byte[dataOffset + data.Length];
        header.CopyTo(output, 0);
        for (var i = 0; i < tableEntries; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(512 + i * 4), starts[i]);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(512 + tableEntries * 4 + i * 4), lengths[i]);
        }
        data.ToArray().CopyTo(output, dataOffset);
        return output;
    }

    private static int SgiPackRow(byte[] row, byte[] output)
    {
        var o = 0;
        var x = 0;
        var n = row.Length;
        while (x < n)
        {
            var run = 1;
            while (x + run < n && run < 127 && row[x + run] == row[x]) run++;
            if (run >= 3)
            {
                output[o++] = (byte)run;
                output[o++] = row[x];
                x += run;
                continue;
            }
            var start = x;
            var count = 0;
            while (x < n && count < 127)
            {
                if (x + 2 < n && row[x] == row[x + 1] && row[x] == row[x + 2]) break;
                x++; count++;
            }
            output[o++] = (byte)(0x80 | count);
            Array.Copy(row, start, output, o, count);
            o += count;
        }
        output[o++] = 0;
        return o;
    }

    // ------------------------------------------------------------------------------------------------
    // XPM: palette images. Pixels with alpha < 50 % become "None"; others are composited over the save
    // background. More than MaxXpmColors distinct colours is refused (XPM is a palette format).
    // ------------------------------------------------------------------------------------------------
    public const int MaxXpmColors = 4096;

    // Printable ASCII without '"' and '\\' (quote/escape) so keys survive C-string parsing.
    private const string XpmAlphabet = " .+@#$%&*=-;>,')!~{]^/(_:<[}|1234567890abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ`";

    public static byte[] Xpm(StraightImage image, string name, uint background = EncoderPixels.DefaultBackground)
    {
        int w = image.Width, h = image.Height, n = w * h;
        var keys = new uint[n];
        var palette = new Dictionary<uint, int>();
        var order = new List<uint>();
        const uint Transparent = 0x00000000; // never produced by an opaque colour (alpha bit is set below)
        for (var i = 0; i < n; i++)
        {
            var o = i * 4;
            uint key;
            var a = image.Bgra[o + 3];
            if (a < 128) key = Transparent;
            else
            {
                int b = image.Bgra[o], g = image.Bgra[o + 1], r = image.Bgra[o + 2];
                if (a != 255)
                {
                    b = (b * a + (int)(background & 0xFF) * (255 - a) + 127) / 255;
                    g = (g * a + (int)((background >> 8) & 0xFF) * (255 - a) + 127) / 255;
                    r = (r * a + (int)((background >> 16) & 0xFF) * (255 - a) + 127) / 255;
                }
                key = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
            keys[i] = key;
            if (!palette.ContainsKey(key))
            {
                if (palette.Count >= MaxXpmColors)
                    throw new ImageSaveRefusedException(string.Format(CultureInfo.InvariantCulture,
                        "XPM is a palette format and Glide writes at most {0:N0} colours; this image has more. Choose PNG for photos.", MaxXpmColors));
                palette[key] = palette.Count;
                order.Add(key);
            }
        }
        var charsPerPixel = order.Count <= XpmAlphabet.Length ? 1 : 2;
        string Key(int index) => charsPerPixel == 1
            ? XpmAlphabet[index].ToString()
            : string.Concat(XpmAlphabet[index / XpmAlphabet.Length], XpmAlphabet[index % XpmAlphabet.Length]);

        var id = CIdentifier(name);
        var text = new StringBuilder();
        text.Append("/* XPM */\n");
        text.Append(CultureInfo.InvariantCulture, $"static char *{id}[] = {{\n");
        text.Append(CultureInfo.InvariantCulture, $"\"{w} {h} {order.Count} {charsPerPixel}\",\n");
        for (var i = 0; i < order.Count; i++)
        {
            var color = order[i] == Transparent ? "None" : "#" + (order[i] & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
            text.Append('"').Append(Key(i)).Append(" c ").Append(color).Append("\",\n");
        }
        var rowText = new StringBuilder(w * charsPerPixel);
        for (var y = 0; y < h; y++)
        {
            rowText.Clear();
            for (var x = 0; x < w; x++) rowText.Append(Key(palette[keys[y * w + x]]));
            text.Append('"').Append(rowText).Append(y == h - 1 ? "\"\n" : "\",\n");
        }
        text.Append("};\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    // ------------------------------------------------------------------------------------------------
    // DDS: uncompressed B8G8R8A8 with the legacy (DX9) header — no DX10 extension, no mipmaps.
    // ------------------------------------------------------------------------------------------------
    public static byte[] Dds(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var output = new byte[4 + 124 + (long)w * h * 4];
        var s = output.AsSpan();
        "DDS "u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x1 | 0x2 | 0x4 | 0x8 | 0x1000); // CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)h);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)w);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)(w * 4)); // pitch
        // ddspf at offset 4 + 72
        var pf = s[76..];
        BinaryPrimitives.WriteUInt32LittleEndian(pf, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(pf[4..], 0x40 | 0x1); // RGB | ALPHAPIXELS
        BinaryPrimitives.WriteUInt32LittleEndian(pf[12..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(pf[16..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(pf[20..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(pf[24..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(pf[28..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(s[108..], 0x1000); // DDSCAPS_TEXTURE
        image.Bgra.CopyTo(output, 128);
        return output;
    }

    // ------------------------------------------------------------------------------------------------
    // KTX 1.1: uncompressed GL_RGBA8, one face, one mip level, top-down rows (KTXorientation S=r,T=d).
    // ------------------------------------------------------------------------------------------------
    public static byte[] Ktx(StraightImage image)
    {
        int w = image.Width, h = image.Height;
        var keyValue = Encoding.ASCII.GetBytes("KTXorientation\0S=r,T=d\0");
        var kvEntry = 4 + keyValue.Length;
        var kvPadded = (kvEntry + 3) & ~3;
        var imageSize = (long)w * h * 4;
        var output = new byte[64 + kvPadded + 4 + imageSize];
        var s = output.AsSpan();
        ReadOnlySpan<byte> identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        identifier.CopyTo(s);
        uint[] fields = [0x04030201, 0x1401, 1, 0x1908, 0x8058, 0x1908, (uint)w, (uint)h, 0, 0, 1, 1, (uint)kvPadded];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(s[(12 + i * 4)..], fields[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], (uint)keyValue.Length);
        keyValue.CopyTo(output, 68);
        var at = 64 + kvPadded;
        BinaryPrimitives.WriteUInt32LittleEndian(s[at..], (uint)imageSize);
        at += 4;
        var px = image.Bgra;
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            output[at++] = px[o + 2]; output[at++] = px[o + 1]; output[at++] = px[o]; output[at++] = px[o + 3];
        }
        return output;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
