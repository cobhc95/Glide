namespace Glide.Imaging;

/// <summary>GPU texture pixel formats the managed texture decoders understand.</summary>
internal enum TextureFormat
{
    Unknown,
    // Uncompressed
    Rgba8, Bgra8, Rgb8, Bgr8, R8, Rg8, La8, L8, A8, Rgb565, Bgr565, Rgba4444, Rgba5551, Rgba16F, Rgba32F, Rgb32F, R16F, R32F,
    // Block compressed
    Bc1, Bc1A, Bc2, Bc3, Bc4, Bc4S, Bc5, Bc5S, Bc6HU, Bc6HS, Bc7,
    Etc1, Etc2Rgb, Etc2A1, Etc2Rgba, EacR11, EacR11S, EacRg11, EacRg11S,
    Astc,
}

/// <summary>
/// Software decoders for GPU block-compressed textures: BC1–BC7 (incl. BC6H HDR, tone-mapped), ETC1,
/// ETC2 RGB/RGBA/punch-through, EAC R11/RG11 and (via <see cref="AstcDecoder"/>) ASTC LDR 2-D blocks,
/// plus common uncompressed layouts. Output is straight BGRA8. All inputs are length-checked first.
/// </summary>
internal static class TextureBlockDecoders
{
    public static bool IsBlockCompressed(TextureFormat format) => format >= TextureFormat.Bc1;

    public static int BlockBytes(TextureFormat format) => format switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc1A or TextureFormat.Bc4 or TextureFormat.Bc4S or
        TextureFormat.Etc1 or TextureFormat.Etc2Rgb or TextureFormat.Etc2A1 or TextureFormat.EacR11 or TextureFormat.EacR11S => 8,
        _ => 16,
    };

    public static int PixelBytes(TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 or TextureFormat.Bgra8 => 4,
        TextureFormat.Rgb8 or TextureFormat.Bgr8 => 3,
        TextureFormat.R8 or TextureFormat.L8 or TextureFormat.A8 => 1,
        TextureFormat.Rg8 or TextureFormat.La8 or TextureFormat.Rgb565 or TextureFormat.Bgr565 or TextureFormat.Rgba4444 or
        TextureFormat.Rgba5551 or TextureFormat.R16F => 2,
        TextureFormat.Rgba16F => 8,
        TextureFormat.Rgba32F => 16,
        TextureFormat.Rgb32F => 12,
        TextureFormat.R32F => 4,
        _ => 0,
    };

    /// <summary>Bytes needed for one 2-D image of this size (uncompressed rows tightly packed or with a row pitch).</summary>
    public static long ImageBytes(TextureFormat format, int width, int height, int blockWidth = 4, int blockHeight = 4, int rowAlignment = 1)
    {
        if (IsBlockCompressed(format))
            return (long)((width + blockWidth - 1) / blockWidth) * ((height + blockHeight - 1) / blockHeight) * BlockBytes(format);
        var row = (long)width * PixelBytes(format);
        row = (row + rowAlignment - 1) / rowAlignment * rowAlignment;
        return row * height;
    }

    public static bool TryDecode(TextureFormat format, ReadOnlySpan<byte> data, int width, int height, int blockWidth, int blockHeight,
        int rowAlignment, out byte[] bgra)
    {
        bgra = Array.Empty<byte>();
        try
        {
            if (format == TextureFormat.Unknown || !ManagedCodecSupport.FitsMemory(width, height)) return false;
            if (ImageBytes(format, width, height, blockWidth, blockHeight, rowAlignment) > data.Length) return false;
            if (!ManagedCodecSupport.TryAllocatePixels(width, height, out bgra)) return false;
            if (!IsBlockCompressed(format)) return DecodeUncompressed(format, data, width, height, rowAlignment, bgra);

            if (format == TextureFormat.Astc)
                return AstcDecoder.Decode(data, width, height, blockWidth, blockHeight, bgra);

            var blocksX = (width + 3) / 4;
            var blocksY = (height + 3) / 4;
            var blockBytes = BlockBytes(format);
            var output = bgra;
            unsafe
            {
                fixed (byte* pointer = data)
                {
                    // Block rows are independent and write disjoint pixel rows (same result as a serial loop).
                    var address = (nint)pointer;
                    var length = data.Length;
                    ManagedCodecSupport.ForBands(blocksY, (long)blocksX * 16, (start, end) =>
                    {
                        var source = new ReadOnlySpan<byte>((void*)address, length);
                        Span<byte> block = stackalloc byte[64]; // 16 RGBA pixels
                        for (var by = start; by < end; by++)
                        {
                            for (var bx = 0; bx < blocksX; bx++)
                            {
                                var src = source.Slice((by * blocksX + bx) * blockBytes, blockBytes);
                                DecodeBlock(format, src, block);
                                for (var py = 0; py < 4; py++)
                                {
                                    var y = by * 4 + py;
                                    if (y >= height) break;
                                    for (var px = 0; px < 4; px++)
                                    {
                                        var x = bx * 4 + px;
                                        if (x >= width) break;
                                        var s = (py * 4 + px) * 4;
                                        var d = ((long)y * width + x) * 4;
                                        output[d] = block[s + 2]; output[d + 1] = block[s + 1]; output[d + 2] = block[s]; output[d + 3] = block[s + 3];
                                    }
                                }
                            }
                        }
                    });
                }
            }
            return true;
        }
        catch
        {
            bgra = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>Decodes one 4x4 block into 16 RGBA pixels.</summary>
    internal static void DecodeBlock(TextureFormat format, ReadOnlySpan<byte> src, Span<byte> rgba)
    {
        switch (format)
        {
            case TextureFormat.Bc1: DecodeBc1(src, rgba, alphaMode: false, fourColor: false); break;
            case TextureFormat.Bc1A: DecodeBc1(src, rgba, alphaMode: true, fourColor: false); break;
            case TextureFormat.Bc2:
                DecodeBc1(src[8..], rgba, false, fourColor: true);
                for (var i = 0; i < 16; i++)
                {
                    var nibble = (src[i / 2] >> (i % 2 * 4)) & 0xF;
                    rgba[i * 4 + 3] = (byte)(nibble * 17);
                }
                break;
            case TextureFormat.Bc3:
                DecodeBc1(src[8..], rgba, false, fourColor: true);
                DecodeBc4Channel(src, rgba, 3, signed: false);
                break;
            case TextureFormat.Bc4:
            case TextureFormat.Bc4S:
                DecodeBc4Channel(src, rgba, 0, format == TextureFormat.Bc4S);
                for (var i = 0; i < 16; i++) { rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4]; rgba[i * 4 + 3] = 255; }
                break;
            case TextureFormat.Bc5:
            case TextureFormat.Bc5S:
                DecodeBc4Channel(src, rgba, 0, format == TextureFormat.Bc5S);
                DecodeBc4Channel(src[8..], rgba, 1, format == TextureFormat.Bc5S);
                for (var i = 0; i < 16; i++) { rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 255; }
                break;
            case TextureFormat.Bc6HU:
            case TextureFormat.Bc6HS:
                Bc6H.Decode(src, rgba, format == TextureFormat.Bc6HS);
                break;
            case TextureFormat.Bc7: Bc7.Decode(src, rgba); break;
            case TextureFormat.Etc1:
            case TextureFormat.Etc2Rgb:
                Etc.DecodeEtc2Rgb(src, rgba, punchThrough: false, etc1Only: format == TextureFormat.Etc1);
                break;
            case TextureFormat.Etc2A1: Etc.DecodeEtc2Rgb(src, rgba, punchThrough: true, etc1Only: false); break;
            case TextureFormat.Etc2Rgba:
                Etc.DecodeEtc2Rgb(src[8..], rgba, punchThrough: false, etc1Only: false);
                Etc.DecodeEacAlpha(src, rgba, 3);
                break;
            case TextureFormat.EacR11:
            case TextureFormat.EacR11S:
                Etc.DecodeEac11(src, rgba, 0, format == TextureFormat.EacR11S);
                for (var i = 0; i < 16; i++) { rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4]; rgba[i * 4 + 3] = 255; }
                break;
            case TextureFormat.EacRg11:
            case TextureFormat.EacRg11S:
                Etc.DecodeEac11(src, rgba, 0, format == TextureFormat.EacRg11S);
                Etc.DecodeEac11(src[8..], rgba, 1, format == TextureFormat.EacRg11S);
                for (var i = 0; i < 16; i++) { rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 255; }
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Uncompressed
    // ---------------------------------------------------------------------------------------------

    private static bool DecodeUncompressed(TextureFormat format, ReadOnlySpan<byte> data, int width, int height, int rowAlignment, byte[] bgra)
    {
        var pixelBytes = PixelBytes(format);
        if (pixelBytes == 0) return false;
        var row = (long)width * pixelBytes;
        row = (row + rowAlignment - 1) / rowAlignment * rowAlignment;
        for (var y = 0; y < height; y++)
        {
            var line = data.Slice((int)(y * row), width * pixelBytes);
            for (var x = 0; x < width; x++)
            {
                var p = line.Slice(x * pixelBytes, pixelBytes);
                byte r, g, b, a = 255;
                switch (format)
                {
                    case TextureFormat.Rgba8: r = p[0]; g = p[1]; b = p[2]; a = p[3]; break;
                    case TextureFormat.Bgra8: b = p[0]; g = p[1]; r = p[2]; a = p[3]; break;
                    case TextureFormat.Rgb8: r = p[0]; g = p[1]; b = p[2]; break;
                    case TextureFormat.Bgr8: b = p[0]; g = p[1]; r = p[2]; break;
                    case TextureFormat.R8: r = p[0]; g = 0; b = 0; break;
                    case TextureFormat.L8: r = g = b = p[0]; break;
                    case TextureFormat.A8: r = g = b = 0; a = p[0]; break;
                    case TextureFormat.Rg8: r = p[0]; g = p[1]; b = 0; break;
                    case TextureFormat.La8: r = g = b = p[0]; a = p[1]; break;
                    case TextureFormat.Rgb565:
                    case TextureFormat.Bgr565:
                    {
                        var v = p[0] | (p[1] << 8);
                        var hi = (byte)(((v >> 11) & 31) * 255 / 31);
                        g = (byte)(((v >> 5) & 63) * 255 / 63);
                        var lo = (byte)((v & 31) * 255 / 31);
                        if (format == TextureFormat.Rgb565) { r = hi; b = lo; } else { b = hi; r = lo; }
                        break;
                    }
                    case TextureFormat.Rgba4444:
                    {
                        var v = p[0] | (p[1] << 8);
                        r = (byte)(((v >> 12) & 15) * 17); g = (byte)(((v >> 8) & 15) * 17); b = (byte)(((v >> 4) & 15) * 17); a = (byte)((v & 15) * 17);
                        break;
                    }
                    case TextureFormat.Rgba5551:
                    {
                        var v = p[0] | (p[1] << 8);
                        r = (byte)(((v >> 11) & 31) * 255 / 31); g = (byte)(((v >> 6) & 31) * 255 / 31); b = (byte)(((v >> 1) & 31) * 255 / 31);
                        a = (byte)((v & 1) * 255);
                        break;
                    }
                    case TextureFormat.R16F:
                        r = ManagedCodecSupport.LinearToSrgb8((float)BitConverter.UInt16BitsToHalf((ushort)(p[0] | (p[1] << 8)))); g = b = 0;
                        break;
                    case TextureFormat.R32F:
                        r = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p)); g = b = 0;
                        break;
                    case TextureFormat.Rgba16F:
                        r = ManagedCodecSupport.LinearToSrgb8(Half(p, 0)); g = ManagedCodecSupport.LinearToSrgb8(Half(p, 2));
                        b = ManagedCodecSupport.LinearToSrgb8(Half(p, 4)); a = ManagedCodecSupport.UnitToByte(Half(p, 6));
                        break;
                    case TextureFormat.Rgba32F:
                        r = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p)); g = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p[4..]));
                        b = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p[8..])); a = ManagedCodecSupport.UnitToByte(BitConverter.ToSingle(p[12..]));
                        break;
                    case TextureFormat.Rgb32F:
                        r = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p)); g = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p[4..]));
                        b = ManagedCodecSupport.LinearToSrgb8(BitConverter.ToSingle(p[8..]));
                        break;
                    default: return false;
                }
                var d = ((long)y * width + x) * 4;
                bgra[d] = b; bgra[d + 1] = g; bgra[d + 2] = r; bgra[d + 3] = a;
            }
        }
        return true;
    }

    private static float Half(ReadOnlySpan<byte> p, int offset) => (float)BitConverter.UInt16BitsToHalf((ushort)(p[offset] | (p[offset + 1] << 8)));

    // ---------------------------------------------------------------------------------------------
    // BC1 / BC4
    // ---------------------------------------------------------------------------------------------

    private static void DecodeBc1(ReadOnlySpan<byte> src, Span<byte> rgba, bool alphaMode, bool fourColor)
    {
        var c0 = src[0] | (src[1] << 8);
        var c1 = src[2] | (src[3] << 8);
        Span<byte> palette = stackalloc byte[16];
        Expand565(c0, palette[..4]);
        Expand565(c1, palette.Slice(4, 4));
        if (fourColor || c0 > c1)
        {
            for (var c = 0; c < 3; c++)
            {
                palette[8 + c] = (byte)((2 * palette[c] + palette[4 + c] + 1) / 3);
                palette[12 + c] = (byte)((palette[c] + 2 * palette[4 + c] + 1) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (var c = 0; c < 3; c++) palette[8 + c] = (byte)((palette[c] + palette[4 + c] + 1) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = 0;
            palette[15] = alphaMode ? (byte)0 : (byte)255;
        }
        var indices = (uint)(src[4] | (src[5] << 8) | (src[6] << 16) | (src[7] << 24));
        for (var i = 0; i < 16; i++)
        {
            var index = (int)((indices >> (i * 2)) & 3);
            palette.Slice(index * 4, 4).CopyTo(rgba.Slice(i * 4, 4));
        }
    }

    private static void Expand565(int v, Span<byte> rgba)
    {
        var r = (v >> 11) & 31;
        var g = (v >> 5) & 63;
        var b = v & 31;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }

    private static void DecodeBc4Channel(ReadOnlySpan<byte> src, Span<byte> rgba, int channel, bool signed)
    {
        Span<int> values = stackalloc int[8];
        int a0, a1;
        if (signed)
        {
            a0 = Math.Max(-127, (int)(sbyte)src[0]);
            a1 = Math.Max(-127, (int)(sbyte)src[1]);
        }
        else
        {
            a0 = src[0];
            a1 = src[1];
        }
        values[0] = a0;
        values[1] = a1;
        if (a0 > a1)
        {
            for (var i = 1; i < 7; i++) values[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        }
        else
        {
            for (var i = 1; i < 5; i++) values[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            values[6] = signed ? -127 : 0;
            values[7] = signed ? 127 : 255;
        }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++)
        {
            var v = values[(int)((bits >> (3 * i)) & 7)];
            rgba[i * 4 + channel] = signed ? (byte)((v + 127) * 255 / 254) : (byte)v;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // BC7
    // ---------------------------------------------------------------------------------------------

    internal static class Bc7
    {
        internal static readonly ushort[] Partitions2 =
        {
            0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
            0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
            0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
            0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
        };

        // 3-subset partitions, 2 bits per pixel (pixel 0 in the low bits).
        private static readonly string[] Partitions3Text =
        {
            "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111", "0000000011221122", "0011001100220022",
            "0022002211111111", "0011001122112211", "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
            "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220", "0001001101121122", "0111001120012200",
            "0000112211221122", "0022002200221111", "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
            "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221", "0022110211020022", "0110011020022222",
            "0011012201220011", "0000200022112221", "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
            "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011", "0011112222000011", "0101010122222222",
            "0000000021212121", "0022112200221122", "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
            "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112", "0222011101110222", "0002111211120002",
            "0110011001102222", "0000000021122112", "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
            "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
        };

        internal static readonly byte[] Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };

        private static readonly byte[] Anchor3A =
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
        };

        private static readonly byte[] Anchor3B =
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
        };

        internal static readonly int[] Weights2 = { 0, 21, 43, 64 };
        internal static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        internal static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        // mode: subsets, partition bits, rotation bits, index-selection bits, colour bits, alpha bits, endpoint p-bits, shared p-bits, index bits, index2 bits
        private static readonly int[,] Modes =
        {
            { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
            { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
            { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
            { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
            { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
            { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
            { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
            { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
        };

        internal static int Subset(int subsets, int partition, int pixel)
        {
            if (subsets == 1) return 0;
            if (subsets == 2) return (Partitions2[partition] >> pixel) & 1;
            return Partitions3Text[partition][pixel] - '0';
        }

        private static bool IsAnchor(int subsets, int partition, int pixel)
        {
            if (pixel == 0) return true;
            if (subsets == 2) return pixel == Anchor2[partition];
            if (subsets == 3) return pixel == Anchor3A[partition] || pixel == Anchor3B[partition];
            return false;
        }

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> rgba)
        {
            var bits = new BitReader(src);
            var mode = 0;
            while (mode < 8 && bits.Read(1) == 0) mode++;
            if (mode == 8)
            {
                rgba[..64].Clear();
                return;
            }
            int subsets = Modes[mode, 0], partitionBits = Modes[mode, 1], rotationBits = Modes[mode, 2], selectionBits = Modes[mode, 3];
            int colorBits = Modes[mode, 4], alphaBits = Modes[mode, 5], endpointPBits = Modes[mode, 6], sharedPBits = Modes[mode, 7];
            int indexBits = Modes[mode, 8], index2Bits = Modes[mode, 9];

            var partition = bits.Read(partitionBits);
            var rotation = bits.Read(rotationBits);
            var selection = bits.Read(selectionBits);

            Span<int> endpoints = stackalloc int[6 * 4]; // [endpoint][channel]
            var count = subsets * 2;
            for (var c = 0; c < 3; c++)
                for (var e = 0; e < count; e++)
                    endpoints[e * 4 + c] = bits.Read(colorBits);
            for (var e = 0; e < count; e++)
                endpoints[e * 4 + 3] = alphaBits > 0 ? bits.Read(alphaBits) : 255;

            var cb = colorBits;
            var ab = alphaBits;
            if (endpointPBits > 0)
            {
                for (var e = 0; e < count; e++)
                {
                    var p = bits.Read(1);
                    for (var c = 0; c < 4; c++)
                    {
                        if (c == 3 && alphaBits == 0) continue;
                        endpoints[e * 4 + c] = (endpoints[e * 4 + c] << 1) | p;
                    }
                }
                cb++;
                if (ab > 0) ab++;
            }
            else if (sharedPBits > 0)
            {
                for (var s = 0; s < subsets; s++)
                {
                    var p = bits.Read(1);
                    for (var e = s * 2; e < s * 2 + 2; e++)
                        for (var c = 0; c < 3; c++)
                            endpoints[e * 4 + c] = (endpoints[e * 4 + c] << 1) | p;
                }
                cb++;
            }
            for (var e = 0; e < count; e++)
            {
                for (var c = 0; c < 3; c++) endpoints[e * 4 + c] = Expand(endpoints[e * 4 + c], cb);
                if (alphaBits > 0) endpoints[e * 4 + 3] = Expand(endpoints[e * 4 + 3], ab);
            }

            Span<int> index1 = stackalloc int[16];
            Span<int> index2 = stackalloc int[16];
            for (var i = 0; i < 16; i++)
                index1[i] = bits.Read(IsAnchor(subsets, partition, i) ? indexBits - 1 : indexBits);
            if (index2Bits > 0)
                for (var i = 0; i < 16; i++)
                    index2[i] = bits.Read(i == 0 ? index2Bits - 1 : index2Bits);

            for (var i = 0; i < 16; i++)
            {
                var subset = Subset(subsets, partition, i);
                var e0 = subset * 2 * 4;
                var e1 = e0 + 4;
                int colorIndex, alphaIndex, colorWeightBits, alphaWeightBits;
                if (index2Bits == 0)
                {
                    colorIndex = alphaIndex = index1[i];
                    colorWeightBits = alphaWeightBits = indexBits;
                }
                else if (selection == 0)
                {
                    colorIndex = index1[i]; colorWeightBits = indexBits;
                    alphaIndex = index2[i]; alphaWeightBits = index2Bits;
                }
                else
                {
                    colorIndex = index2[i]; colorWeightBits = index2Bits;
                    alphaIndex = index1[i]; alphaWeightBits = indexBits;
                }
                var cw = Weight(colorWeightBits, colorIndex);
                var aw = Weight(alphaWeightBits, alphaIndex);
                var o = i * 4;
                for (var c = 0; c < 3; c++)
                    rgba[o + c] = (byte)(((64 - cw) * endpoints[e0 + c] + cw * endpoints[e1 + c] + 32) >> 6);
                rgba[o + 3] = (byte)(((64 - aw) * endpoints[e0 + 3] + aw * endpoints[e1 + 3] + 32) >> 6);
                if (rotation > 0)
                {
                    var target = o + rotation - 1;
                    (rgba[target], rgba[o + 3]) = (rgba[o + 3], rgba[target]);
                }
            }
        }

        internal static int Weight(int bits, int index) => bits switch
        {
            2 => Weights2[index],
            3 => Weights3[index],
            _ => Weights4[index],
        };

        private static int Expand(int value, int bits) => bits >= 8 ? value : (value << (8 - bits)) | (value >> (2 * bits - 8));
    }

    /// <summary>LSB-first bit reader over a 128-bit block.</summary>
    internal ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly ulong _low, _high;
        private readonly bool _block128;
        private int _position;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
            // BC6H/BC7 blocks are exactly 128 bits: keep them in two registers and extract fields with
            // shifts instead of one bit at a time. Bits past the end still read as zero.
            _block128 = data.Length == 16;
            _low = _block128 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data) : 0;
            _high = _block128 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[8..]) : 0;
        }

        public int Read(int count)
        {
            if (_block128 && count is > 0 and <= 32)
            {
                var p = _position;
                _position += count;
                ulong v;
                if (p >= 128) v = 0;
                else if (p >= 64) v = _high >> (p - 64);
                else if (p == 0) v = _low;
                else v = (_low >> p) | (_high << (64 - p));
                return (int)(v & ((1UL << count) - 1));
            }
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var bit = _position < _data.Length * 8 ? (_data[_position >> 3] >> (_position & 7)) & 1 : 0;
                value |= bit << i;
                _position++;
            }
            return value;
        }

        public int Position => _position;
    }

    // ---------------------------------------------------------------------------------------------
    // BC6H (HDR, tone-mapped for display)
    // ---------------------------------------------------------------------------------------------

    internal static class Bc6H
    {
        // Per mode (1..14): header field layout after the mode bits; tokens "nameA:B" read bits A..B in
        // stream order, "nameN" a single bit. Names: r/g/b + w/x/y/z endpoint.
        private static readonly string[] Layouts =
        {
            "",
            "gy4 by4 bz4 rw0:9 gw0:9 bw0:9 rx0:4 gz4 gy0:3 gx0:4 bz0 gz0:3 bx0:4 bz1 by0:3 ry0:4 bz2 rz0:4 bz3",
            "gy5 gz4 gz5 rw0:6 bz0 bz1 by4 gw0:6 by5 bz2 gy4 bw0:6 bz3 bz5 bz4 rx0:5 gy0:3 gx0:5 gz0:3 bx0:5 by0:3 ry0:5 rz0:5",
            "rw0:9 gw0:9 bw0:9 rx0:4 rw10 gy0:3 gx0:3 gw10 bz0 gz0:3 bx0:3 bw10 bz1 by0:3 ry0:4 bz2 rz0:4 bz3",
            "rw0:9 gw0:9 bw0:9 rx0:3 rw10 gz4 gy0:3 gx0:4 gw10 gz0:3 bx0:3 bw10 bz1 by0:3 ry0:3 bz0 bz2 rz0:3 gy4 bz3",
            "rw0:9 gw0:9 bw0:9 rx0:3 rw10 by4 gy0:3 gx0:3 gw10 bz0 gz0:3 bx0:4 bw10 by0:3 ry0:3 bz1 bz2 rz0:3 bz4 bz3",
            "rw0:8 by4 gw0:8 gy4 bw0:8 bz4 rx0:4 gz4 gy0:3 gx0:4 bz0 gz0:3 bx0:4 bz1 by0:3 ry0:4 bz2 rz0:4 bz3",
            "rw0:7 gz4 by4 gw0:7 bz2 gy4 bw0:7 bz3 bz4 rx0:5 gy0:3 gx0:4 bz0 gz0:3 bx0:4 bz1 by0:3 ry0:5 rz0:5",
            "rw0:7 bz0 by4 gw0:7 gy5 gy4 bw0:7 gz5 bz4 rx0:4 gz4 gy0:3 gx0:5 gz0:3 bx0:4 bz1 by0:3 ry0:4 bz2 rz0:4 bz3",
            "rw0:7 bz1 by4 gw0:7 by5 gy4 bw0:7 bz5 bz4 rx0:4 gz4 gy0:3 gx0:4 bz0 gz0:3 bx0:5 by0:3 ry0:4 bz2 rz0:4 bz3",
            "rw0:5 gz4 bz0 bz1 by4 gw0:5 gy5 by5 bz2 gy4 bw0:5 gz5 bz3 bz5 bz4 rx0:5 gy0:3 gx0:5 gz0:3 bx0:5 by0:3 ry0:5 rz0:5",
            "rw0:9 gw0:9 bw0:9 rx0:9 gx0:9 bx0:9",
            "rw0:9 gw0:9 bw0:9 rx0:8 rw10 gx0:8 gw10 bx0:8 bw10",
            "rw0:9 gw0:9 bw0:9 rx0:7 rw11:10 gx0:7 gw11:10 bx0:7 bw11:10",
            "rw0:9 gw0:9 bw0:9 rx0:3 rw15:10 gx0:3 gw15:10 bx0:3 bw15:10",
        };

        // Header layouts flattened to one entry per stream bit: (value index << 8) | bit number. Built on first BC6H use.
        private static readonly int[][] ParsedLayouts = ParseLayouts();

        private static int[][] ParseLayouts()
        {
            var result = new int[Layouts.Length][];
            for (var mode = 0; mode < Layouts.Length; mode++)
            {
                var steps = new List<int>();
                if (Layouts[mode].Length > 0)
                {
                    foreach (var token in Layouts[mode].Split(' '))
                    {
                        var channel = token[0] == 'r' ? 0 : token[0] == 'g' ? 1 : 2;
                        var endpoint = token[1] - 'w';
                        var spec = token[2..];
                        var colon = spec.IndexOf(':');
                        int from, to;
                        if (colon < 0) { from = to = int.Parse(spec, System.Globalization.CultureInfo.InvariantCulture); }
                        else
                        {
                            from = int.Parse(spec[..colon], System.Globalization.CultureInfo.InvariantCulture);
                            to = int.Parse(spec[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                        }
                        var direction = to >= from ? 1 : -1;
                        for (var bit = from; ; bit += direction)
                        {
                            steps.Add(((channel * 4 + endpoint) << 8) | bit);
                            if (bit == to) break;
                        }
                    }
                }
                result[mode] = steps.ToArray();
            }
            return result;
        }

        // endpoint bits, delta bits r/g/b, transformed, regions
        private static readonly int[,] Parameters =
        {
            { 0, 0, 0, 0, 0, 0 },
            { 10, 5, 5, 5, 1, 2 }, { 7, 6, 6, 6, 1, 2 }, { 11, 5, 4, 4, 1, 2 }, { 11, 4, 5, 4, 1, 2 }, { 11, 4, 4, 5, 1, 2 },
            { 9, 5, 5, 5, 1, 2 }, { 8, 6, 5, 5, 1, 2 }, { 8, 5, 6, 5, 1, 2 }, { 8, 5, 5, 6, 1, 2 }, { 6, 6, 6, 6, 0, 2 },
            { 10, 10, 10, 10, 0, 1 }, { 11, 9, 9, 9, 1, 1 }, { 12, 8, 8, 8, 1, 1 }, { 16, 4, 4, 4, 1, 1 },
        };

        private static int ModeFromBits(int bits) => bits switch
        {
            0x02 => 3, 0x06 => 4, 0x0A => 5, 0x0E => 6, 0x12 => 7, 0x16 => 8, 0x1A => 9, 0x1E => 10,
            0x03 => 11, 0x07 => 12, 0x0B => 13, 0x0F => 14, _ => 0,
        };

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> rgba, bool signed)
        {
            var bits = new BitReader(src);
            var m = bits.Read(2);
            int mode;
            if (m < 2) mode = m + 1;
            else mode = ModeFromBits(m | (bits.Read(3) << 2));
            if (mode == 0)
            {
                for (var i = 0; i < 16; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 255; }
                return;
            }

            // values[channel * 4 + endpoint], channel r/g/b, endpoint w/x/y/z
            Span<int> values = stackalloc int[12];
            values.Clear();
            foreach (var step in ParsedLayouts[mode])
                values[step >> 8] |= bits.Read(1) << (step & 0xFF);
            int endpointBits = Parameters[mode, 0], transformed = Parameters[mode, 4], regions = Parameters[mode, 5];
            var partition = regions == 2 ? bits.Read(5) : 0;

            for (var c = 0; c < 3; c++)
            {
                var deltaBits = Parameters[mode, 1 + c];
                if (signed) values[c * 4] = SignExtend(values[c * 4], endpointBits);
                for (var e = 1; e < 4; e++)
                {
                    if (regions == 1 && e > 1) break;
                    if (transformed == 1)
                    {
                        var delta = SignExtend(values[c * 4 + e], deltaBits);
                        var v = (values[c * 4] + delta) & ((1 << endpointBits) - 1);
                        values[c * 4 + e] = signed ? SignExtend(v, endpointBits) : v;
                    }
                    else if (signed)
                    {
                        values[c * 4 + e] = SignExtend(values[c * 4 + e], endpointBits);
                    }
                }
                for (var e = 0; e < 4; e++) values[c * 4 + e] = Unquantize(values[c * 4 + e], endpointBits, signed);
            }

            var indexBits = regions == 2 ? 3 : 4;
            for (var i = 0; i < 16; i++)
            {
                var anchor = i == 0 || (regions == 2 && i == Bc7.Anchor2[partition]);
                var index = bits.Read(anchor ? indexBits - 1 : indexBits);
                var region = regions == 2 ? (Bc7.Partitions2[partition] >> i) & 1 : 0;
                var weight = indexBits == 3 ? Bc7.Weights3[index] : Bc7.Weights4[index];
                for (var c = 0; c < 3; c++)
                {
                    var a = values[c * 4 + region * 2];
                    var b = values[c * 4 + region * 2 + 1];
                    var interpolated = ((64 - weight) * a + weight * b + 32) >> 6;
                    rgba[i * 4 + c] = ManagedCodecSupport.LinearToSrgb8(FinishToFloat(interpolated, signed));
                }
                rgba[i * 4 + 3] = 255;
            }
        }

        private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

        private static int Unquantize(int value, int bits, bool signed)
        {
            if (!signed)
            {
                if (bits >= 15) return value;
                if (value == 0) return 0;
                if (value == (1 << bits) - 1) return 0xFFFF;
                return ((value << 16) + 0x8000) >> bits;
            }
            if (bits >= 16) return value;
            var negative = value < 0;
            if (negative) value = -value;
            int result;
            if (value == 0) result = 0;
            else if (value >= (1 << (bits - 1)) - 1) result = 0x7FFF;
            else result = ((value << 15) + 0x4000) >> (bits - 1);
            return negative ? -result : result;
        }

        internal static float FinishToFloat(int value, bool signed)
        {
            ushort half;
            if (!signed) half = (ushort)((value * 31) >> 6);
            else half = value < 0 ? (ushort)(0x8000 | ((-value * 31) >> 5)) : (ushort)((value * 31) >> 5);
            return (float)BitConverter.UInt16BitsToHalf(half);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // ETC1 / ETC2 / EAC
    // ---------------------------------------------------------------------------------------------

    internal static class Etc
    {
        private static readonly int[,] Modifiers =
        {
            { 2, 8, -2, -8 }, { 5, 17, -5, -17 }, { 9, 29, -9, -29 }, { 13, 42, -13, -42 },
            { 18, 60, -18, -60 }, { 24, 80, -24, -80 }, { 33, 106, -33, -106 }, { 47, 183, -47, -183 },
        };

        private static readonly int[] Distances = { 3, 6, 11, 16, 23, 32, 41, 64 };

        private static readonly int[,] EacModifiers =
        {
            { -3, -6, -9, -15, 2, 5, 8, 14 }, { -3, -7, -10, -13, 2, 6, 9, 12 }, { -2, -5, -8, -13, 1, 4, 7, 12 },
            { -2, -4, -6, -13, 1, 3, 5, 12 }, { -3, -6, -8, -12, 2, 5, 7, 11 }, { -3, -7, -9, -11, 2, 6, 8, 10 },
            { -4, -7, -8, -11, 3, 6, 7, 10 }, { -3, -5, -8, -11, 2, 4, 7, 10 }, { -2, -6, -8, -10, 1, 5, 7, 9 },
            { -2, -5, -8, -10, 1, 4, 7, 9 }, { -2, -4, -8, -10, 1, 3, 7, 9 }, { -2, -5, -7, -10, 1, 4, 6, 9 },
            { -3, -4, -7, -10, 2, 3, 6, 9 }, { -1, -2, -3, -10, 0, 1, 2, 9 }, { -4, -6, -8, -9, 3, 5, 7, 8 },
            { -3, -5, -7, -9, 2, 4, 6, 8 },
        };

        private static ulong ReadBigEndian64(ReadOnlySpan<byte> src)
        {
            ulong v = 0;
            for (var i = 0; i < 8; i++) v = (v << 8) | src[i];
            return v;
        }

        private static int Bits(ulong v, int high, int low) => (int)((v >> low) & ((1UL << (high - low + 1)) - 1));

        private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

        private static int Extend4(int v) => (v << 4) | v;
        private static int Extend5(int v) => (v << 3) | (v >> 2);

        public static void DecodeEtc2Rgb(ReadOnlySpan<byte> src, Span<byte> rgba, bool punchThrough, bool etc1Only)
        {
            var block = ReadBigEndian64(src);
            var diff = Bits(block, 33, 33) == 1;
            var flip = Bits(block, 32, 32) == 1;
            var opaque = !punchThrough || diff; // in punch-through mode bit 33 is the "opaque" flag
            if (punchThrough) diff = true;

            int r1, g1, b1, r2, g2, b2;
            if (!diff)
            {
                r1 = Extend4(Bits(block, 63, 60)); r2 = Extend4(Bits(block, 59, 56));
                g1 = Extend4(Bits(block, 55, 52)); g2 = Extend4(Bits(block, 51, 48));
                b1 = Extend4(Bits(block, 47, 44)); b2 = Extend4(Bits(block, 43, 40));
            }
            else
            {
                var rb = Bits(block, 63, 59); var rd = SignExtend3(Bits(block, 58, 56));
                var gb = Bits(block, 55, 51); var gd = SignExtend3(Bits(block, 50, 48));
                var bb = Bits(block, 47, 43); var bd = SignExtend3(Bits(block, 42, 40));
                if (!etc1Only)
                {
                    if (rb + rd is < 0 or > 31) { DecodeT(block, rgba, punchThrough && !opaque); return; }
                    if (gb + gd is < 0 or > 31) { DecodeH(block, rgba, punchThrough && !opaque); return; }
                    if (bb + bd is < 0 or > 31) { DecodePlanar(block, rgba); return; }
                }
                r1 = Extend5(rb); g1 = Extend5(gb); b1 = Extend5(bb);
                r2 = Extend5((rb + rd) & 31); g2 = Extend5((gb + gd) & 31); b2 = Extend5((bb + bd) & 31);
            }
            var table1 = Bits(block, 39, 37);
            var table2 = Bits(block, 36, 34);
            for (var x = 0; x < 4; x++)
            {
                for (var y = 0; y < 4; y++)
                {
                    var p = x * 4 + y; // column-major pixel index
                    var second = flip ? y >= 2 : x >= 2;
                    var msb = (int)((block >> (16 + p)) & 1);
                    var lsb = (int)((block >> p) & 1);
                    var index = (msb << 1) | lsb;
                    var o = (y * 4 + x) * 4;
                    if (punchThrough && !opaque && index == 2)
                    {
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                        continue;
                    }
                    var modifier = Modifiers[second ? table2 : table1, index];
                    if (punchThrough && !opaque && index == 0) modifier = 0;
                    rgba[o] = Clamp((second ? r2 : r1) + modifier);
                    rgba[o + 1] = Clamp((second ? g2 : g1) + modifier);
                    rgba[o + 2] = Clamp((second ? b2 : b1) + modifier);
                    rgba[o + 3] = 255;
                }
            }
        }

        private static int SignExtend3(int v) => (v & 4) != 0 ? v - 8 : v;

        private static void WritePaint(ulong block, Span<byte> rgba, Span<int> paint, bool transparentIndex2)
        {
            for (var x = 0; x < 4; x++)
            {
                for (var y = 0; y < 4; y++)
                {
                    var p = x * 4 + y;
                    var index = (int)((((block >> (16 + p)) & 1) << 1) | ((block >> p) & 1));
                    var o = (y * 4 + x) * 4;
                    if (transparentIndex2 && index == 2)
                    {
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                        continue;
                    }
                    rgba[o] = (byte)paint[index * 3];
                    rgba[o + 1] = (byte)paint[index * 3 + 1];
                    rgba[o + 2] = (byte)paint[index * 3 + 2];
                    rgba[o + 3] = 255;
                }
            }
        }

        private static void DecodeT(ulong block, Span<byte> rgba, bool transparent)
        {
            var r1 = Extend4((Bits(block, 60, 59) << 2) | Bits(block, 57, 56));
            var g1 = Extend4(Bits(block, 55, 52));
            var b1 = Extend4(Bits(block, 51, 48));
            var r2 = Extend4(Bits(block, 47, 44));
            var g2 = Extend4(Bits(block, 43, 40));
            var b2 = Extend4(Bits(block, 39, 36));
            var d = Distances[(Bits(block, 35, 34) << 1) | Bits(block, 32, 32)];
            Span<int> paint = stackalloc int[12];
            paint[0] = r1; paint[1] = g1; paint[2] = b1;
            paint[3] = Clamp(r2 + d); paint[4] = Clamp(g2 + d); paint[5] = Clamp(b2 + d);
            paint[6] = r2; paint[7] = g2; paint[8] = b2;
            paint[9] = Clamp(r2 - d); paint[10] = Clamp(g2 - d); paint[11] = Clamp(b2 - d);
            WritePaint(block, rgba, paint, transparent);
        }

        private static void DecodeH(ulong block, Span<byte> rgba, bool transparent)
        {
            var r1 = Bits(block, 62, 59);
            var g1 = (Bits(block, 58, 56) << 1) | Bits(block, 52, 52);
            var b1 = (Bits(block, 51, 51) << 3) | Bits(block, 49, 47);
            var r2 = Bits(block, 46, 43);
            var g2 = Bits(block, 42, 39);
            var b2 = Bits(block, 38, 35);
            var c1 = (r1 << 8) | (g1 << 4) | b1;
            var c2 = (r2 << 8) | (g2 << 4) | b2;
            var index = (Bits(block, 34, 34) << 2) | (Bits(block, 32, 32) << 1) | (c1 >= c2 ? 1 : 0);
            var d = Distances[index];
            r1 = Extend4(r1); g1 = Extend4(g1); b1 = Extend4(b1);
            r2 = Extend4(r2); g2 = Extend4(g2); b2 = Extend4(b2);
            Span<int> paint = stackalloc int[12];
            paint[0] = Clamp(r1 + d); paint[1] = Clamp(g1 + d); paint[2] = Clamp(b1 + d);
            paint[3] = Clamp(r1 - d); paint[4] = Clamp(g1 - d); paint[5] = Clamp(b1 - d);
            paint[6] = Clamp(r2 + d); paint[7] = Clamp(g2 + d); paint[8] = Clamp(b2 + d);
            paint[9] = Clamp(r2 - d); paint[10] = Clamp(g2 - d); paint[11] = Clamp(b2 - d);
            WritePaint(block, rgba, paint, transparent);
        }

        private static void DecodePlanar(ulong block, Span<byte> rgba)
        {
            var ro = Bits(block, 62, 57);
            var go = (Bits(block, 56, 56) << 6) | Bits(block, 54, 49);
            var bo = (Bits(block, 48, 48) << 5) | (Bits(block, 44, 43) << 3) | Bits(block, 41, 39);
            var rh = (Bits(block, 38, 34) << 1) | Bits(block, 32, 32);
            var gh = Bits(block, 31, 25);
            var bh = Bits(block, 24, 19);
            var rv = Bits(block, 18, 13);
            var gv = Bits(block, 12, 6);
            var bv = Bits(block, 5, 0);
            static int E6(int v) => (v << 2) | (v >> 4);
            static int E7(int v) => (v << 1) | (v >> 6);
            ro = E6(ro); rh = E6(rh); rv = E6(rv);
            go = E7(go); gh = E7(gh); gv = E7(gv);
            bo = E6(bo); bh = E6(bh); bv = E6(bv);
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var o = (y * 4 + x) * 4;
                    rgba[o] = Clamp((x * (rh - ro) + y * (rv - ro) + 4 * ro + 2) >> 2);
                    rgba[o + 1] = Clamp((x * (gh - go) + y * (gv - go) + 4 * go + 2) >> 2);
                    rgba[o + 2] = Clamp((x * (bh - bo) + y * (bv - bo) + 4 * bo + 2) >> 2);
                    rgba[o + 3] = 255;
                }
            }
        }

        public static void DecodeEacAlpha(ReadOnlySpan<byte> src, Span<byte> rgba, int channel)
        {
            var block = ReadBigEndian64(src);
            var baseValue = Bits(block, 63, 56);
            var multiplier = Bits(block, 55, 52);
            var table = Bits(block, 51, 48);
            for (var p = 0; p < 16; p++)
            {
                var index = (int)((block >> (45 - p * 3)) & 7);
                var x = p / 4;
                var y = p % 4;
                rgba[(y * 4 + x) * 4 + channel] = Clamp(baseValue + EacModifiers[table, index] * multiplier);
            }
        }

        public static void DecodeEac11(ReadOnlySpan<byte> src, Span<byte> rgba, int channel, bool signed)
        {
            var block = ReadBigEndian64(src);
            var baseValue = Bits(block, 63, 56);
            var multiplier = Bits(block, 55, 52);
            var table = Bits(block, 51, 48);
            for (var p = 0; p < 16; p++)
            {
                var index = (int)((block >> (45 - p * 3)) & 7);
                var modifier = EacModifiers[table, index];
                int value;
                if (!signed)
                {
                    value = baseValue * 8 + 4 + (multiplier != 0 ? modifier * multiplier * 8 : modifier);
                    value = Math.Clamp(value, 0, 2047) >> 3;
                }
                else
                {
                    var b = Math.Max(-127, (int)(sbyte)(byte)baseValue);
                    value = b * 8 + (multiplier != 0 ? modifier * multiplier * 8 : modifier);
                    value = Math.Clamp(value, -1023, 1023);
                    value = (value + 1023) * 255 / 2046;
                }
                var x = p / 4;
                var y = p % 4;
                rgba[(y * 4 + x) * 4 + channel] = (byte)value;
            }
        }
    }
}
