using System.Buffers.Binary;
using System.Text;
using Glide.Imaging;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// GPU texture blocks with hand-computed colours (BC1–BC7 incl. BC6H, ETC1/ETC2 planar, EAC, ASTC
/// void-extent and a direct-RGB 4x4 block) inside KTX, KTX2 (plain and zlib), PVR v3 and .astc files.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class TextureDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    // -- Block builders ---------------------------------------------------------------------------

    /// <summary>LSB-first bit writer for 128-bit BC6H/BC7/ASTC blocks.</summary>
    private sealed class Bits
    {
        private readonly byte[] _data = new byte[16];
        private int _position;

        public Bits Put(long value, int count)
        {
            for (var i = 0; i < count; i++, _position++)
                if (((value >> i) & 1) != 0) _data[_position >> 3] |= (byte)(1 << (_position & 7));
            return this;
        }

        public Bits Set(int bit)
        {
            _data[bit >> 3] |= (byte)(1 << (bit & 7));
            return this;
        }

        public byte[] ToArray() => (byte[])_data.Clone();
    }

    private static byte[] Bc1Block() => new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0xE4, 0xE4, 0xE4, 0xE4 };

    private static byte[] BigEndian64(ulong v) => Enumerable.Range(0, 8).Select(i => (byte)(v >> (56 - 8 * i))).ToArray();

    private static byte[] Etc1Block() => new byte[] { 0x8F, 0x40, 0x20, 0x00, 0, 0, 0, 0 };

    private static byte[] Etc2PlanarBlock()
    {
        ulong b = 0;
        void Field(int high, int low, ulong value) => b |= (value & ((1UL << (high - low + 1)) - 1)) << low;
        Field(47, 45, 7);      // free bits forcing the blue overflow
        Field(44, 43, 3);      // BO2
        Field(41, 39, 2);      // BO3
        Field(38, 34, 31);     // RH1
        Field(33, 33, 1);      // diff
        Field(32, 32, 1);      // RH2
        Field(24, 19, 26);     // BH
        Field(5, 0, 26);       // BV
        return BigEndian64(b);
    }

    private static byte[] EacBlock(int baseValue, int multiplier, int table, int firstIndex, int otherIndex)
    {
        ulong b = ((ulong)baseValue << 56) | ((ulong)multiplier << 52) | ((ulong)table << 48);
        for (var p = 0; p < 16; p++) b |= (ulong)(p == 0 ? firstIndex : otherIndex) << (45 - 3 * p);
        return BigEndian64(b);
    }

    private static byte[] Bc7Mode6()
    {
        var bits = new Bits().Put(1 << 6, 7);
        // R0 R1 G0 G1 B0 B1 A0 A1 (7 bits each), then P0 P1.
        bits.Put(0, 7).Put(127, 7).Put(0, 7).Put(127, 7).Put(0, 7).Put(127, 7).Put(127, 7).Put(127, 7);
        bits.Put(1, 1).Put(1, 1);
        // Indices: pixel 0 = 0 (3 bits), pixel 1 = 15, pixel 2 = 8, rest 15.
        bits.Put(0, 3).Put(15, 4).Put(8, 4);
        for (var i = 3; i < 16; i++) bits.Put(15, 4);
        return bits.ToArray();
    }

    private static byte[] Bc7Mode1Partition0()
    {
        var bits = new Bits().Put(1 << 1, 2).Put(0, 6);
        // R: subset0 e0,e1 = 63 ; subset1 = 0
        bits.Put(63, 6).Put(63, 6).Put(0, 6).Put(0, 6);
        // G: subset0 = 0 ; subset1 = 63
        bits.Put(0, 6).Put(0, 6).Put(63, 6).Put(63, 6);
        // B: all 0
        bits.Put(0, 6).Put(0, 6).Put(0, 6).Put(0, 6);
        bits.Put(1, 1).Put(1, 1); // shared p-bits
        // Indices: all zero (46 bits)
        return bits.ToArray();
    }

    private static byte[] Bc6HMode11(int w, int x, int firstIndex, int otherIndex)
    {
        var bits = new Bits().Put(3, 2).Put(0, 3);
        bits.Put(w, 10).Put(w, 10).Put(w, 10).Put(x, 10).Put(x, 10).Put(x, 10);
        bits.Put(firstIndex, 3);
        for (var i = 1; i < 16; i++) bits.Put(otherIndex, 4);
        return bits.ToArray();
    }

    private static readonly int[] AstcEndpoints = { 10, 200, 20, 210, 30, 220 };

    private static byte[] AstcRgbBlock(int[] weightIndices)
    {
        var bits = new Bits().Put(66, 11).Put(0, 2).Put(8, 4);
        foreach (var v in AstcEndpoints) bits.Put(v, 8);
        for (var i = 0; i < 16; i++)
            for (var b = 0; b < 2; b++)
                if (((weightIndices[i] >> b) & 1) != 0) bits.Set(127 - (2 * i + b));
        return bits.ToArray();
    }

    private static byte[] AstcVoidExtent(int r, int g, int b, int a)
    {
        var bits = new Bits().Put(0xDFC, 12).Put(-1L, 52);
        bits.Put(r * 257, 16).Put(g * 257, 16).Put(b * 257, 16).Put(a * 257, 16);
        return bits.ToArray();
    }

    // -- Containers ---------------------------------------------------------------------------------

    private static readonly byte[] Ktx1Id = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Ktx2Id = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] Ktx1(uint internalFormat, uint format, uint type, int width, int height, byte[] data)
    {
        var w = new Writer();
        w.Bytes(Ktx1Id).U32(0x04030201).U32(type).U32(1).U32(format).U32(internalFormat).U32(0x1908)
            .U32(width).U32(height).U32(0).U32(0).U32(1).U32(1).U32(0);
        w.U32(data.Length).Bytes(data);
        return w.ToArray();
    }

    private static byte[] Ktx2(uint vkFormat, int width, int height, byte[] data, int supercompression = 0, long uncompressed = -1)
    {
        var w = new Writer();
        w.Bytes(Ktx2Id).U32(vkFormat).U32(1).U32(width).U32(height).U32(0).U32(0).U32(1).U32(1).U32((uint)supercompression);
        w.U32(0).U32(0).U32(0).U32(0).U64(0).U64(0);
        w.U64(104).U64(data.Length).U64(uncompressed < 0 ? data.Length : uncompressed);
        w.Bytes(data);
        return w.ToArray();
    }

    private static byte[] Pvr(ulong pixelFormat, int width, int height, byte[] data, int channelType = 0)
    {
        var w = new Writer();
        w.U32(0x03525650).U32(0).U64((long)pixelFormat).U32(0).U32(channelType).U32(height).U32(width).U32(1).U32(1).U32(1).U32(1).U32(0);
        w.Bytes(data);
        return w.ToArray();
    }

    private static byte[] AstcFile(int bw, int bh, int width, int height, byte[] blocks)
    {
        var w = new Writer();
        w.U32(0x5CA1AB13).U8(bw).U8(bh).U8(1).U8(width & 0xFF).U8((width >> 8) & 0xFF).U8(width >> 16)
            .U8(height & 0xFF).U8((height >> 8) & 0xFF).U8(height >> 16).U8(1).U8(0).U8(0);
        w.Bytes(blocks);
        return w.ToArray();
    }

    // -- Tests -----------------------------------------------------------------------------------------

    private static byte[] KeyValue(string key, string value)
    {
        var entry = Encoding.ASCII.GetBytes(key + "\0" + value + "\0");
        var w = new Writer().U32(entry.Length).Bytes(entry);
        w.Zeros((4 - entry.Length % 4) % 4);
        return w.ToArray();
    }

    [Fact]
    public void Ktx_orientation_key_bottom_up_is_flipped_and_top_down_is_not()
    {
        // 1x2 RGBA8: stored row 0 = red, stored row 1 = blue.
        var rows = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 };
        var plain = Ktx1(0x8058, 0x1908, 0x1401, 1, 2, rows);
        AssertColor(Decode(Temp(_files, plain, ".ktx")).At(0, 0), 255, 0, 0);

        byte[] WithKtx1Key(string value)
        {
            var kv = KeyValue("KTXorientation", value);
            var file = plain.Take(64).Concat(kv).Concat(plain.Skip(64)).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), (uint)kv.Length);
            return file;
        }
        var up = Decode(Temp(_files, WithKtx1Key("S=r,T=u"), ".ktx"));
        AssertColor(up.At(0, 0), 0, 0, 255);   // T=u: the stored first row is the BOTTOM row
        AssertColor(up.At(0, 1), 255, 0, 0);
        var down = Decode(Temp(_files, WithKtx1Key("S=r,T=d"), ".ktx"));
        AssertColor(down.At(0, 0), 255, 0, 0);

        byte[] WithKtx2Key(string value)
        {
            var file = Ktx2(37, 1, 2, rows).Concat(KeyValue("KTXorientation", value)).ToArray();
            var length = KeyValue("KTXorientation", value).Length;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56), (uint)(file.Length - length));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), (uint)length);
            return file;
        }
        AssertColor(Decode(Temp(_files, WithKtx2Key("ru"), ".ktx2")).At(0, 0), 0, 0, 255);
        AssertColor(Decode(Temp(_files, WithKtx2Key("rd"), ".ktx2")).At(0, 0), 255, 0, 0);
    }

    [Fact]
    public void Ktx_bc1_four_colour_block()
    {
        var pixels = Decode(Temp(_files, Ktx1(0x83F1, 0, 0, 4, 4, Bc1Block()), ".ktx"));
        Assert.Equal((4, 4), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 3), 255, 0, 0);
        AssertColor(pixels.At(1, 3), 0, 0, 255);
        AssertColor(pixels.At(2, 0), 170, 0, 85, 1);
        AssertColor(pixels.At(3, 0), 85, 0, 170, 1);
    }

    [Fact]
    public void Bc1_punch_through_bc2_bc3_bc4_bc5()
    {
        // BC1 with c0 <= c1: index 3 is transparent black.
        var punch = new byte[] { 0x1F, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF };
        var p = Decode(Temp(_files, Ktx2(133, 4, 4, punch), ".ktx2"));
        Assert.Equal(0, p.At(1, 1).A);

        // BC2: explicit 4-bit alpha (pixel i -> i*17) + BC1 colour.
        var alpha = Enumerable.Range(0, 8).Select(i => (byte)((2 * i) | ((2 * i + 1) << 4))).ToArray();
        var bc2 = Decode(Temp(_files, Ktx2(135, 4, 4, alpha.Concat(Bc1Block()).ToArray()), ".ktx2"));
        Assert.Equal(0, bc2.At(0, 0).A);
        Assert.Equal(15 * 17, bc2.At(3, 3).A);
        AssertColor(bc2.At(1, 1), 0, 0, 255, a: 5 * 17);

        // BC3: interpolated alpha, all indices 2 -> (6*255 + 3) / 7 = 219.
        ulong indices = 0;
        for (var i = 0; i < 16; i++) indices |= 2UL << (3 * i);
        var alphaBlock = new byte[] { 255, 0 }.Concat(Enumerable.Range(0, 6).Select(i => (byte)(indices >> (8 * i)))).ToArray();
        var bc3 = Decode(Temp(_files, Ktx2(137, 4, 4, alphaBlock.Concat(Bc1Block()).ToArray()), ".ktx2"));
        Assert.Equal(219, bc3.At(2, 2).A);

        var bc4 = Decode(Temp(_files, Ktx2(139, 4, 4, alphaBlock), ".ktx2"));
        AssertColor(bc4.At(0, 0), 219, 219, 219);

        var bc5 = Decode(Temp(_files, Ktx2(141, 4, 4, alphaBlock.Concat(new byte[] { 0, 255, 0, 0, 0, 0, 0, 0 }).ToArray()), ".ktx2"));
        AssertColor(bc5.At(3, 1), 219, 0, 0);
    }

    [Fact]
    public void Bc7_mode6_and_mode1_partitions()
    {
        var m6 = Decode(Temp(_files, Ktx2(145, 4, 4, Bc7Mode6()), ".ktx2"));
        AssertColor(m6.At(0, 0), 1, 1, 1);
        AssertColor(m6.At(1, 0), 255, 255, 255);
        AssertColor(m6.At(2, 0), 136, 136, 136);

        var m1 = Decode(Temp(_files, Ktx1(0x8E8C, 0, 0, 4, 4, Bc7Mode1Partition0()), ".ktx"));
        AssertColor(m1.At(0, 0), 255, 2, 2);
        AssertColor(m1.At(1, 3), 255, 2, 2);
        AssertColor(m1.At(2, 0), 2, 255, 2);
        AssertColor(m1.At(3, 3), 2, 255, 2);
    }

    [Fact]
    public void Bc6h_unsigned_mode11_is_tone_mapped()
    {
        var flat = Decode(Temp(_files, Ktx2(143, 4, 4, Bc6HMode11(400, 400, 0, 0)), ".ktx2"));
        var half = (float)BitConverter.UInt16BitsToHalf((ushort)((((400 << 16) + 0x8000) >> 10) * 31 >> 6));
        var expected = (int)Math.Round((half <= 0.0031308 ? 12.92 * half : 1.055 * Math.Pow(half, 1 / 2.4) - 0.055) * 255);
        AssertColor(flat.At(2, 2), expected, expected, expected, 1);

        var ramp = Decode(Temp(_files, Ktx2(143, 4, 4, Bc6HMode11(0, 1023, 0, 15)), ".ktx2"));
        AssertColor(ramp.At(0, 0), 0, 0, 0);
        AssertColor(ramp.At(1, 0), 255, 255, 255);
    }

    [Fact]
    public void Etc1_etc2_planar_and_eac_in_pvr_and_ktx()
    {
        var etc1 = Decode(Temp(_files, Pvr(6, 4, 4, Etc1Block()), ".pvr"));
        AssertColor(etc1.At(0, 0), 138, 70, 36);
        AssertColor(etc1.At(3, 3), 255, 2, 2);

        var planar = Decode(Temp(_files, Ktx1(0x9274, 0, 0, 4, 4, Etc2PlanarBlock()), ".ktx"));
        Assert.Equal(new[] { 0, 64, 128, 191 }, Enumerable.Range(0, 4).Select(x => planar.At(x, 2).R).ToArray());
        Assert.Equal(105, planar.At(1, 1).B);
        Assert.Equal(0, planar.At(1, 1).G);

        var rgba = Decode(Temp(_files, Pvr(23, 4, 4, EacBlock(128, 2, 0, 0, 4).Concat(Etc1Block()).ToArray()), ".pvr"));
        Assert.Equal(122, rgba.At(0, 0).A);
        Assert.Equal(132, rgba.At(3, 1).A);
        AssertColor(rgba.At(0, 0), 138, 70, 36, a: 122);

        var r11 = Decode(Temp(_files, Ktx2(153, 4, 4, EacBlock(100, 1, 0, 4, 4)), ".ktx2"));
        AssertColor(r11.At(1, 1), 102, 102, 102);
    }

    [Fact]
    public void Astc_void_extent_and_direct_rgb_block()
    {
        var voidExtent = Decode(Temp(_files, AstcFile(4, 4, 6, 3, Enumerable.Repeat(AstcVoidExtent(12, 34, 56, 255), 2).SelectMany(b => b).ToArray()), ".astc"));
        Assert.Equal((6, 3), (voidExtent.Width, voidExtent.Height));
        AssertColor(voidExtent.At(5, 2), 12, 34, 56);

        var weights = Enumerable.Range(0, 16).Select(i => i % 4).ToArray();
        var block = Decode(Temp(_files, AstcFile(4, 4, 4, 4, AstcRgbBlock(weights)), ".astc"));
        int[] unquantized = { 0, 21, 43, 64 };
        for (var i = 0; i < 16; i++)
        {
            var w = unquantized[weights[i]];
            int Channel(int e0, int e1) => ((e0 * 257 * (64 - w) + e1 * 257 * w + 32) >> 6) >> 8;
            AssertColor(block.At(i % 4, i / 4), Channel(10, 200), Channel(20, 210), Channel(30, 220));
        }

        // Same block through KTX2 (ASTC 4x4 UNORM) and PVR (format 27).
        Assert.Equal(block.Bgra, Decode(Temp(_files, Ktx2(157, 4, 4, AstcRgbBlock(weights)), ".ktx2")).Bgra);
        Assert.Equal(block.Bgra, Decode(Temp(_files, Pvr(27, 4, 4, AstcRgbBlock(weights)), ".pvr")).Bgra);
    }

    [Fact]
    public void Astc_integer_sequence_tables_follow_the_specification()
    {
        // Every trit/quint combination is reachable from the packed encodings.
        var trits = new HashSet<int>();
        Span<int> digits = stackalloc int[5];
        for (var t = 0; t < 256; t++)
        {
            AstcDecoder.DecodeTrits(t, digits);
            Assert.All(digits.ToArray(), d => Assert.InRange(d, 0, 2));
            trits.Add(digits[0] + 3 * digits[1] + 9 * digits[2] + 27 * digits[3] + 81 * digits[4]);
        }
        Assert.Equal(243, trits.Count);
        var quints = new HashSet<int>();
        for (var q = 0; q < 128; q++)
        {
            AstcDecoder.DecodeQuints(q, digits);
            quints.Add(digits[0] + 5 * digits[1] + 25 * digits[2]);
        }
        Assert.Equal(125, quints.Count);

        int[] levels = { 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256 };
        for (var q = 4; q < levels.Length; q++)
        {
            var values = Enumerable.Range(0, levels[q]).Select(v => AstcDecoder.UnquantizeColor(q, v)).OrderBy(v => v).ToArray();
            Assert.Equal(0, values[0]);
            Assert.Equal(255, values[^1]);
            Assert.Equal(levels[q], values.Distinct().Count());
            var step = 255.0 / (levels[q] - 1);
            for (var i = 0; i < values.Length; i++) Assert.InRange(values[i], step * i - 2, step * i + 2);
        }
        for (var q = 0; q < 12; q++)
        {
            var values = Enumerable.Range(0, levels[q]).Select(v => AstcDecoder.UnquantizeWeight(q, v)).OrderBy(v => v).ToArray();
            Assert.Equal(0, values[0]);
            Assert.Equal(64, values[^1]);
            Assert.Equal(levels[q], values.Distinct().Count());
        }
    }

    [Fact]
    public void Uncompressed_textures_in_every_container()
    {
        // KTX1 RGB8 3x2 with 4-byte row alignment.
        var rows = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 0, 0, 0 };
        var ktx = Decode(Temp(_files, Ktx1(0x8051, 0x1907, 0x1401, 3, 2, rows), ".ktx"));
        AssertColor(ktx.At(2, 0), 0, 0, 255);
        AssertColor(ktx.At(1, 1), 40, 50, 60);

        var rgba = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var ktx2 = Decode(Temp(_files, Ktx2(37, 2, 1, rgba), ".ktx2"));
        AssertColor(ktx2.At(1, 0), 5, 6, 7, a: 8);

        var zlib = Decode(Temp(_files, Ktx2(37, 2, 1, Zlib(rgba), supercompression: 3, uncompressed: rgba.Length), ".ktx2"));
        AssertColor(zlib.At(0, 0), 1, 2, 3, a: 4);

        var pvr = Decode(Temp(_files, Pvr(0x08080808_61626772UL, 2, 1, rgba), ".pvr"));
        AssertColor(pvr.At(1, 0), 5, 6, 7, a: 8);

        var half = new Writer();
        foreach (var v in new[] { 1f, 0.5f, 0f, 1f }) half.U16(BitConverter.HalfToUInt16Bits((Half)v));
        var hdr = Decode(Temp(_files, Ktx2(97, 1, 1, half.ToArray()), ".ktx2"));
        AssertColor(hdr.At(0, 0), 255, 188, 0, 1);
    }

    [Fact]
    public void Textures_malformed_or_unsupported_fail_gracefully()
    {
        AssertFails(Temp(_files, Ktx1(0x83F1, 0, 0, 64, 64, Bc1Block()), ".ktx"));          // truncated data
        AssertFails(Temp(_files, Ktx2(37, 2, 1, Zlib(new byte[8]), supercompression: 2, uncompressed: 8), ".ktx2")); // zstd
        AssertFails(Temp(_files, Ktx2(0, 4, 4, new byte[16], supercompression: 1), ".ktx2"));  // BasisLZ
        AssertFails(Temp(_files, Pvr(3, 4, 4, new byte[8]), ".pvr"));                           // PVRTC
        AssertFails(Temp(_files, AstcFile(7, 7, 7, 7, new byte[16]), ".astc"));                 // invalid footprint
        AssertFails(Temp(_files, AstcFile(4, 4, 8, 8, new byte[16]), ".astc"));                 // truncated
        AssertFails(Temp(_files, Ktx1Id.Concat(new byte[10]).ToArray(), ".ktx"));
        AssertFails(Temp(_files, new byte[] { 0x13, 0xAB }, ".astc"));
        AssertFails(Temp(_files, Enumerable.Repeat((byte)0x42, 200).ToArray(), ".basis.ktx2"));
    }
}
