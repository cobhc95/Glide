using System.Buffers.Binary;

namespace Glide.Imaging;

/// <summary>
/// Legacy interchange rasters that Windows has no codec for (5.0.7): headerless "packed" DIB, Apple ICNS,
/// Sun raster (RAS), X11 window dump (XWD) and SMPTE DPX. Every reader validates sizes against the buffer
/// before allocating and returns false on anything it does not understand.
/// </summary>
public static partial class BuiltInRasterDecoder
{
    // ---------------------------------------------------------------------------------------------
    // DIB: a BMP with or without its 14-byte BITMAPFILEHEADER ("packed DIB", as on the clipboard).
    // The bundled BMP codec does the pixel work; a packed DIB just gets the file header it lacks.
    // ---------------------------------------------------------------------------------------------

    private static bool TryBuildBmp(ReadOnlySpan<byte> data, out byte[] bmp)
    {
        bmp = Array.Empty<byte>();
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            bmp = data.ToArray();
            return true;
        }
        if (data.Length < 16) return false;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (headerSize is not (12 or 40 or 52 or 56 or 64 or 108 or 124) || headerSize >= data.Length) return false;
        int bitCount, colorsUsed = 0;
        uint compression = 0;
        if (headerSize == 12)
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        else
        {
            if (data.Length < 36) return false;
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
            compression = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            colorsUsed = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[32..]), 1u << 16);
        }
        if (bitCount is not (1 or 4 or 8 or 16 or 24 or 32)) return false;
        var paletteEntries = bitCount <= 8 ? (colorsUsed > 0 ? colorsUsed : 1 << bitCount) : colorsUsed;
        var masks = headerSize == 40 && compression is 3 or 6 ? (compression == 6 ? 16 : 12) : 0; // BI_BITFIELDS / BI_ALPHABITFIELDS
        var pixelOffset = 14L + headerSize + masks + paletteEntries * (headerSize == 12 ? 3L : 4L);
        if (pixelOffset - 14 > data.Length) return false;
        bmp = new byte[data.Length + 14];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)pixelOffset);
        data.CopyTo(bmp.AsSpan(14));
        return true;
    }

    private static bool TryProbeDib(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        var info = data.Length >= 2 && data[0] == 'B' && data[1] == 'M' ? data[Math.Min(14, data.Length)..] : data;
        if (info.Length < 16) return false;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info);
        int width, height;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(info[4..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(info[6..]);
        }
        else if (headerSize is 40 or 52 or 56 or 64 or 108 or 124)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(info[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(info[8..]);
        }
        else return false;
        height = height == int.MinValue ? 0 : Math.Abs(height);
        if (!IsValidDimensions(width, height)) return false;
        dimensions = new ImageDimensions(width, height);
        return true;
    }

    private static bool TryDecodeDib(ReadOnlySpan<byte> data, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        return TryBuildBmp(data, out var bmp) && ManagedCodecSupport.TryDecodeEncoded(bmp, maxLongestSide, out image, out _);
    }

    // ---------------------------------------------------------------------------------------------
    // ICNS: Apple icon family. Modern members (ic07–ic14, icp4–icp6, ic04/ic05) hold PNG streams; the
    // largest PNG member is shown. Legacy RLE/JPEG 2000 members are ignored.
    // ---------------------------------------------------------------------------------------------

    private static bool TryFindIcnsPng(ReadOnlySpan<byte> data, out int start, out int length, out int width, out int height)
    {
        start = length = width = height = 0;
        if (data.Length < 8 || !data[..4].SequenceEqual("icns"u8)) return false;
        var total = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[4..]), (uint)data.Length);
        var position = 8;
        for (var n = 0; n < 256 && position + 8 <= total; n++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            if (size < 8 || size > total - position) return false;
            var payload = data.Slice(position + 8, (int)size - 8);
            if (payload.Length > 24 && payload[0] == 0x89 && payload[1..8].SequenceEqual("PNG\r\n\x1a\n"u8) && payload[12..16].SequenceEqual("IHDR"u8))
            {
                var w = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(payload[16..]), int.MaxValue);
                var h = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(payload[20..]), int.MaxValue);
                if (IsValidDimensions(w, h) && (long)w * h > (long)width * height)
                {
                    (start, length, width, height) = (position + 8, payload.Length, w, h);
                }
            }
            position += (int)size;
        }
        return length > 0;
    }

    private static bool TryProbeIcns(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryFindIcnsPng(data, out _, out _, out var width, out var height)) return false;
        dimensions = new ImageDimensions(width, height);
        return true;
    }

    private static bool TryDecodeIcns(ReadOnlySpan<byte> data, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        return TryFindIcnsPng(data, out var start, out var length, out _, out _) &&
               ManagedCodecSupport.TryDecodeEncoded(data.Slice(start, length), maxLongestSide, out image, out _);
    }

    // ---------------------------------------------------------------------------------------------
    // Sun raster: 32-byte big-endian header; types 0/1 (BGR), 2 (byte-encoded RLE, BGR) and 3 (RGB);
    // depths 1, 8 (colour map or grey), 24 and 32 (XBGR/XRGB). Rows are padded to 16 bits.
    // ---------------------------------------------------------------------------------------------

    private const uint SunRasterMagic = 0x59A66A95;

    private static bool TryProbeSunRaster(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 32 || BinaryPrimitives.ReadUInt32BigEndian(data) != SunRasterMagic) return false;
        var width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[4..]), int.MaxValue);
        var height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[8..]), int.MaxValue);
        if (!IsValidDimensions(width, height)) return false;
        dimensions = new ImageDimensions(width, height);
        return true;
    }

    private static bool TryDecodeSunRaster(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (!TryProbeSunRaster(data, out var size)) return false;
        var depth = (int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        var type = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        var mapType = BinaryPrimitives.ReadUInt32BigEndian(data[24..]);
        var mapLength = BinaryPrimitives.ReadUInt32BigEndian(data[28..]);
        if (depth is not (1 or 8 or 24 or 32) || type > 3 || mapType > 1 || mapLength > data.Length - 32) return false;
        int width = size.Width, height = size.Height;
        var map = data.Slice(32, (int)mapLength);
        var mapEntries = mapType == 1 ? map.Length / 3 : 0;
        var stride = ((long)width * depth + 15) / 16 * 2;
        var needed = stride * height;
        if (needed > int.MaxValue) return false;
        var body = data[(32 + (int)mapLength)..];

        byte[] pixels;
        if (type == 2)
        {
            // Byte-encoded: 0x80 0x00 is a literal 0x80; 0x80 n v is n+1 copies of v.
            if (body.Length * 128L < needed) return false;
            pixels = new byte[needed];
            int read = 0, written = 0;
            while (written < pixels.Length && read < body.Length)
            {
                var b = body[read++];
                if (b != 0x80) { pixels[written++] = b; continue; }
                if (read >= body.Length) return false;
                var count = body[read++];
                if (count == 0) { pixels[written++] = 0x80; continue; }
                if (read >= body.Length) return false;
                var value = body[read++];
                var run = Math.Min(count + 1, pixels.Length - written);
                pixels.AsSpan(written, run).Fill(value);
                written += run;
            }
            if (written < pixels.Length) return false;
        }
        else
        {
            if (body.Length < needed) return false;
            pixels = body[..(int)needed].ToArray();
        }

        if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
        var rgbOrder = type == 3;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.AsSpan((int)(y * stride), (int)stride);
            for (var x = 0; x < width; x++)
            {
                byte r, g, b;
                switch (depth)
                {
                    case 1:
                        // Without a colour map, a set bit is black.
                        var bit = (row[x >> 3] >> (7 - (x & 7))) & 1;
                        if (mapEntries >= 2) { r = map[bit]; g = map[mapEntries + bit]; b = map[2 * mapEntries + bit]; }
                        else r = g = b = bit == 1 ? (byte)0 : (byte)255;
                        break;
                    case 8:
                        var index = row[x];
                        if (index < mapEntries) { r = map[index]; g = map[mapEntries + index]; b = map[2 * mapEntries + index]; }
                        else r = g = b = index;
                        break;
                    default:
                        var p = depth == 32 ? x * 4 + 1 : x * 3;
                        (r, g, b) = rgbOrder ? (row[p], row[p + 1], row[p + 2]) : (row[p + 2], row[p + 1], row[p]);
                        break;
                }
                var o = ((long)y * width + x) * 4;
                output[o] = b;
                output[o + 1] = g;
                output[o + 2] = r;
                output[o + 3] = 255;
            }
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // XWD (X11 window dump, version 7): ZPixmap images of 8 bits (colour map) or 16/24/32 bits
    // (TrueColor/DirectColor masks). The header is written in the producer's byte order.
    // ---------------------------------------------------------------------------------------------

    private readonly record struct XwdHeader(bool BigEndian, int HeaderSize, int Width, int Height, int PixelByteOrder,
        int BitsPerPixel, int BytesPerLine, int VisualClass, uint RedMask, uint GreenMask, uint BlueMask, int Colors);

    private static bool TryParseXwd(ReadOnlySpan<byte> data, out XwdHeader header)
    {
        header = default;
        if (data.Length < 100) return false;
        var bigEndian = BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == 7;
        if (!bigEndian && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 7) return false;
        var fields = new uint[25];
        for (var i = 0; i < fields.Length; i++) fields[i] = ReadU32(data, i * 4, bigEndian);
        uint U(int field) => fields[field];
        var headerSize = U(0);
        if (headerSize < 100 || headerSize > data.Length || U(2) != 2) return false; // ZPixmap only
        var width = (int)Math.Min(U(4), int.MaxValue);
        var height = (int)Math.Min(U(5), int.MaxValue);
        if (!IsValidDimensions(width, height)) return false;
        header = new XwdHeader(bigEndian, (int)headerSize, width, height, (int)U(7), (int)U(11), (int)Math.Min(U(12), int.MaxValue),
            (int)U(13), U(14), U(15), U(16), (int)Math.Min(U(19), 65536));
        return true;
    }

    private static bool TryProbeXwd(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryParseXwd(data, out var header)) return false;
        dimensions = new ImageDimensions(header.Width, header.Height);
        return true;
    }

    private static bool TryDecodeXwd(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (!TryParseXwd(data, out var h) || h.BitsPerPixel is not (8 or 16 or 24 or 32)) return false;
        var bytesPerPixel = h.BitsPerPixel / 8;
        if (h.BytesPerLine < (long)h.Width * bytesPerPixel) return false;
        var mapStart = (long)h.HeaderSize;
        var pixelStart = mapStart + h.Colors * 12L;
        if (pixelStart + (long)h.BytesPerLine * h.Height > data.Length) return false;

        var palette = Array.Empty<uint>();
        if (h.BitsPerPixel == 8)
        {
            // Colour map entries: pixel(4) red(2) green(2) blue(2) flags(1) pad(1), header byte order.
            palette = new uint[256];
            for (var i = 0; i < h.Colors; i++)
            {
                var entry = data.Slice((int)(mapStart + i * 12L), 12);
                var index = h.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(entry) : BinaryPrimitives.ReadUInt32LittleEndian(entry);
                if (index < 256)
                    palette[index] = (uint)((ReadU16(entry, 4, h.BigEndian) >> 8) << 16 | (ReadU16(entry, 6, h.BigEndian) >> 8) << 8 | ReadU16(entry, 8, h.BigEndian) >> 8);
            }
            if (h.Colors == 0) for (var i = 0; i < 256; i++) palette[i] = (uint)(i << 16 | i << 8 | i);
        }
        else if (h.RedMask == 0 || h.GreenMask == 0 || h.BlueMask == 0) return false;

        static (int Shift, int Bits) MaskInfo(uint mask)
        {
            var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
            return (shift, System.Numerics.BitOperations.PopCount(mask));
        }
        var (rs, rb) = MaskInfo(h.RedMask);
        var (gs, gb) = MaskInfo(h.GreenMask);
        var (bs, bb) = MaskInfo(h.BlueMask);
        static byte Scale(uint value, int bits) => bits >= 8 ? (byte)(value >> (bits - 8)) : (byte)(value * 255 / ((1u << bits) - 1));

        if (!ManagedCodecSupport.TryAllocatePixels(h.Width, h.Height, out var output)) return false;
        var msbFirst = h.PixelByteOrder == 1;
        for (var y = 0; y < h.Height; y++)
        {
            var row = data.Slice((int)(pixelStart + (long)y * h.BytesPerLine), h.Width * bytesPerPixel);
            for (var x = 0; x < h.Width; x++)
            {
                uint v = 0;
                var px = row.Slice(x * bytesPerPixel, bytesPerPixel);
                for (var i = 0; i < bytesPerPixel; i++)
                    v = msbFirst ? v << 8 | px[i] : v | (uint)px[i] << (8 * i);
                byte r, g, b;
                if (h.BitsPerPixel == 8)
                {
                    var c = palette[v & 0xFF];
                    (r, g, b) = ((byte)(c >> 16), (byte)(c >> 8), (byte)c);
                }
                else
                {
                    r = Scale((v & h.RedMask) >> rs, rb);
                    g = Scale((v & h.GreenMask) >> gs, gb);
                    b = Scale((v & h.BlueMask) >> bs, bb);
                }
                var o = ((long)y * h.Width + x) * 4;
                output[o] = b;
                output[o + 1] = g;
                output[o + 2] = r;
                output[o + 3] = 255;
            }
        }
        image = new DecodedImage(h.Width, h.Height, output);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // DPX (SMPTE 268M): first image element, uncompressed, descriptor RGB (50), RGBA (51) or luma (6);
    // 8-bit, 16-bit, 10-bit filled (method A/B) and 12-bit filled. Code values map linearly to 8 bits.
    // ---------------------------------------------------------------------------------------------

    private readonly record struct DpxElement(bool BigEndian, int Width, int Height, int Descriptor, int Bits, int Packing, long DataOffset, long LinePadding);

    private static bool TryParseDpx(ReadOnlySpan<byte> data, out DpxElement element)
    {
        element = default;
        if (data.Length < 820) return false;
        bool bigEndian;
        if (data[..4].SequenceEqual("SDPX"u8)) bigEndian = true;
        else if (data[..4].SequenceEqual("XPDS"u8)) bigEndian = false;
        else return false;
        var width = (int)Math.Min(ReadU32(data, 772, bigEndian), int.MaxValue);
        var height = (int)Math.Min(ReadU32(data, 776, bigEndian), int.MaxValue);
        if (ReadU16(data, 770, bigEndian) < 1 || !IsValidDimensions(width, height)) return false;
        if (ReadU16(data, 806, bigEndian) != 0) return false; // RLE-encoded element
        var dataOffset = ReadU32(data, 808, bigEndian);
        if (dataOffset is 0 or 0xFFFFFFFF) dataOffset = ReadU32(data, 4, bigEndian);
        var padding = ReadU32(data, 812, bigEndian);
        element = new DpxElement(bigEndian, width, height, data[800], data[803], ReadU16(data, 804, bigEndian), dataOffset, padding == 0xFFFFFFFF ? 0 : padding);
        return true;
    }

    private static bool TryProbeDpx(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryParseDpx(data, out var e)) return false;
        dimensions = new ImageDimensions(e.Width, e.Height);
        return true;
    }

    private static bool TryDecodeDpx(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (!TryParseDpx(data, out var e)) return false;
        var channels = e.Descriptor switch { 50 => 3, 51 or 52 => 4, 6 => 1, _ => 0 };
        if (channels == 0) return false;
        var samplesPerLine = (long)e.Width * channels;
        long lineBytes = (e.Bits, e.Packing) switch
        {
            (8, _) => (samplesPerLine + 3) / 4 * 4,
            (16, _) => samplesPerLine * 2,
            (10, 1 or 2) => (samplesPerLine + 2) / 3 * 4,
            (12, 1 or 2) => samplesPerLine * 2,
            _ => 0,
        };
        if (lineBytes == 0) return false;
        lineBytes += e.LinePadding;
        if (e.DataOffset + lineBytes * e.Height > data.Length) return false;
        if (!ManagedCodecSupport.TryAllocatePixels(e.Width, e.Height, out var output)) return false;

        for (var y = 0; y < e.Height; y++)
        {
            var line = data.Slice((int)(e.DataOffset + y * lineBytes), (int)lineBytes);
            for (var x = 0; x < e.Width; x++)
            {
                var s = (long)x * channels;
                byte r, g, b, a = 255;
                if (channels == 1) r = g = b = DpxSample(line, e, s);
                else if (e.Descriptor == 52) { a = DpxSample(line, e, s); b = DpxSample(line, e, s + 1); g = DpxSample(line, e, s + 2); r = DpxSample(line, e, s + 3); }
                else { r = DpxSample(line, e, s); g = DpxSample(line, e, s + 1); b = DpxSample(line, e, s + 2); if (channels == 4) a = DpxSample(line, e, s + 3); }
                var o = ((long)y * e.Width + x) * 4;
                output[o] = b;
                output[o + 1] = g;
                output[o + 2] = r;
                output[o + 3] = a;
            }
        }
        image = new DecodedImage(e.Width, e.Height, output);
        return true;
    }

    /// <summary>Sample <paramref name="i"/> of a DPX line, scaled to 8 bits.</summary>
    private static byte DpxSample(ReadOnlySpan<byte> line, DpxElement e, long i)
    {
        switch (e.Bits)
        {
            case 8: return line[(int)i];
            case 16: return (byte)(ReadU16(line, (int)(i * 2), e.BigEndian) >> 8);
            case 12:
                var v12 = ReadU16(line, (int)(i * 2), e.BigEndian);
                return (byte)((e.Packing == 1 ? v12 >> 4 : v12) >> 4); // method A is MSB-aligned
            default:
                var word = ReadU32(line, (int)(i / 3 * 4), e.BigEndian);
                var shift = (e.Packing == 1 ? 22 : 20) - (int)(i % 3) * 10; // method A pads the low 2 bits
                return (byte)(((word >> shift) & 0x3FF) >> 2);
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int at, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    private static int ReadU16(ReadOnlySpan<byte> data, int at, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
}
