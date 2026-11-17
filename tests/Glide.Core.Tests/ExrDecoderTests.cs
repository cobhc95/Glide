using System.Buffers.Binary;
using System.Text;
using Glide.Imaging;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// OpenEXR fixtures written by an in-test encoder: scanline and tiled layouts, every supported
/// compression (NONE, RLE, ZIPS, ZIP, PIZ — the latter with a reference-style wavelet + Huffman
/// encoder — plus PXR24 and B44), HALF/FLOAT/UINT channels, premultiplied alpha and luminance/chroma files.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ExrDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    private sealed record Channel(string Name, int Type);

    // Test image: R ramps with x, G ramps with y, B constant 0.25, A = 1 except pixel (0,0) = 0.5.
    private static float Sample(string name, int x, int y, int width, int height) => name switch
    {
        "R" => x / (float)Math.Max(1, width - 1),
        "G" => y / (float)Math.Max(1, height - 1),
        "B" => 0.25f,
        "A" => !float.IsNaN(_alphaOverride) ? _alphaOverride : x == 0 && y == 0 ? 0.5f : 1f,
        _ => 0f,
    };

    private static byte Srgb(float v)
    {
        double d = Math.Clamp(v, 0f, 1f);
        d = d <= 0.0031308 ? 12.92 * d : 1.055 * Math.Pow(d, 1 / 2.4) - 0.055;
        return (byte)Math.Round(d * 255);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var compression in new[] { 0, 1, 2, 3, 4 })
        {
            yield return new object[] { compression, false, 1 };
            yield return new object[] { compression, true, 1 };
        }
        yield return new object[] { 3, false, 2 };
        yield return new object[] { 0, false, 0 };
        yield return new object[] { 4, true, 2 };
        // PXR24 (zlib of delta-coded byte planes) and B44 (4x4 HALF blocks; other types stored raw).
        foreach (var type in new[] { 0, 1, 2 })
        {
            yield return new object[] { 5, false, type };
            yield return new object[] { 5, true, type };
        }
        yield return new object[] { 6, false, 1 };
        yield return new object[] { 6, true, 1 };
        yield return new object[] { 6, false, 2 };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decodes_compression_layout_and_type(int compression, bool tiled, int channelType)
    {
        const int width = 19, height = 21;
        var channels = new[] { new Channel("A", channelType), new Channel("B", channelType), new Channel("G", channelType), new Channel("R", channelType) };
        var path = Temp(_files, WriteExr(width, height, channels, compression, tiled, 8, 6), ".exr");
        var pixels = Decode(path);
        Assert.Equal((width, height), (pixels.Width, pixels.Height));
        for (var y = 0; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 3)
            {
                if (x == 0 && y == 0) continue;
                var (r, g, b, a) = pixels.At(x, y);
                var expected = (Srgb(Quantize(Sample("R", x, y, width, height), channelType)), Srgb(Quantize(Sample("G", x, y, width, height), channelType)), Srgb(0.25f));
                var tolerance = compression == 6 && channelType == 1 ? 10 : 2; // B44 HALF blocks are lossy
                Assert.True(Math.Abs(r - expected.Item1) <= tolerance && Math.Abs(g - expected.Item2) <= tolerance && Math.Abs(b - expected.Item3) <= tolerance,
                    $"({x},{y}) expected {expected} got ({r},{g},{b}) [c={compression} tiled={tiled} type={channelType}]");
                Assert.Equal(255, a);
            }
        }
        // Premultiplied colour is un-premultiplied: stored R=0, G=0, B=0.125 at alpha 0.5.
        var p0 = pixels.At(0, 0);
        Assert.Equal(128, p0.A, 1.0);
        Assert.InRange(p0.B, Srgb(0.25f) - 3, Srgb(0.25f) + 3);
    }

    [Fact]
    public void Luminance_chroma_file_is_reconstructed_as_colour()
    {
        // Y at full resolution, RY/BY subsampled 2x2 (the layout of OpenEXR's RgbaOutputFile with WRITE_YC).
        const float r = 0.5f, g = 0.25f, b = 0.1f;
        const float yw = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        var path = Temp(_files, WriteYcExr(8, 6, yw, (r - yw) / yw, (b - yw) / yw), ".exr");
        var pixels = Decode(path);
        Assert.Equal((8, 6), (pixels.Width, pixels.Height));
        foreach (var (x, y) in new[] { (0, 0), (3, 2), (7, 5) })
            AssertColor(pixels.At(x, y), Srgb(r), Srgb(g), Srgb(b), 2);
    }

    [Fact]
    public void Alpha_channel_that_is_zero_everywhere_is_ignored_and_a_lone_green_channel_is_grey()
    {
        var withZeroAlpha = new[] { new Channel("A", 1), new Channel("B", 1), new Channel("G", 1), new Channel("R", 1) };
        var bytes = WriteExr(4, 4, withZeroAlpha, 0, false, 0, 0, alphaOverride: 0f);
        var pixels = Decode(Temp(_files, bytes, ".exr"));
        Assert.Equal(255, pixels.At(2, 2).A);
        var green = Decode(Temp(_files, WriteExr(4, 4, new[] { new Channel("G", 2) }, 3, false, 0, 0), ".exr"));
        var (gr, gg, gb, _) = green.At(3, 3);
        Assert.Equal(gg, gr);
        Assert.Equal(gg, gb);
        Assert.InRange(gg, 250, 255); // G = y / (height - 1) = 1 on the last row
    }

    [Fact]
    public void Luminance_only_and_layered_channel_names()
    {
        var gray = Temp(_files, WriteExr(4, 2, new[] { new Channel("Y", 1) }, 3, false, 0, 0), ".exr");
        var pixels = Decode(gray);
        Assert.Equal((4, 2), (pixels.Width, pixels.Height));
        var layered = Temp(_files, WriteExr(4, 2, new[] { new Channel("diffuse.B", 2), new Channel("diffuse.G", 2), new Channel("diffuse.R", 2) }, 2, false, 0, 0), ".exr");
        var lp = Decode(layered);
        AssertColor(lp.At(3, 1), 255, 255, Srgb(0.25f), 2);
    }

    [Fact]
    public void Malformed_exr_fails_gracefully()
    {
        var channels = new[] { new Channel("B", 1), new Channel("G", 1), new Channel("R", 1) };
        var good = WriteExr(8, 8, channels, 3, false, 0, 0);
        AssertFails(Temp(_files, good.Take(good.Length - 20).ToArray(), ".exr"));
        AssertFails(Temp(_files, good.Take(40).ToArray(), ".exr"));
        var corrupt = (byte[])good.Clone();
        for (var i = good.Length - 30; i < good.Length; i++) corrupt[i] ^= 0x5A;
        AssertFails(Temp(_files, corrupt, ".exr"));
        // An unknown compression id (10) is reported, not guessed at.
        var dwaa = WriteExr(8, 8, channels, 0, false, 0, 0);
        var index = IndexOf(dwaa, Encoding.ASCII.GetBytes("compression\0compression\0"));
        dwaa[index + 24 + 4] = 10;
        AssertFails(Temp(_files, dwaa, ".exr"));
        // B44 / PXR24 with garbage payloads fail instead of producing noise.
        foreach (var compression in new[] { 5, 6 })
        {
            var broken = WriteExr(8, 8, channels, compression, false, 0, 0);
            for (var i = broken.Length - 30; i < broken.Length; i++) broken[i] ^= 0xA5;
            var ok = BuiltInRasterDecoder.TryDecodePixelsForTests(Temp(_files, broken.Take(broken.Length - 25).ToArray(), ".exr"), 0, out _, out _, out _);
            Assert.False(ok);
        }
        // Piz with garbage payload.
        var piz = WriteExr(8, 8, channels, 4, false, 0, 0);
        for (var i = piz.Length - 40; i < piz.Length; i++) piz[i] = 0xFF;
        AssertFails(Temp(_files, piz, ".exr"));
    }

    // -- DWAA / DWAB ------------------------------------------------------------------------------

    // Two 8x8 blocks side by side, each a flat colour (scene-linear).
    private static readonly (float R, float G, float B)[] DwaBlocks = { (0.5f, 0.25f, 0.1f), (0.2f, 0.6f, 0.9f) };

    [Theory]
    [InlineData(8, 0)] // DWAA, static Huffman AC stream
    [InlineData(9, 1)] // DWAB, zlib AC stream
    public void Dwa_lossy_rgb_rle_alpha_and_unknown_channel_decode(int compression, int acCompression)
    {
        var pixels = Decode(Temp(_files, WriteDwaExr(compression, acCompression), ".exr"));
        Assert.Equal((16, 8), (pixels.Width, pixels.Height));
        foreach (var (x, y) in new[] { (0, 0), (5, 7), (7, 3), (8, 0), (12, 5), (15, 7) })
        {
            var (r, g, b) = DwaBlocks[x / 8];
            AssertColor(pixels.At(x, y), Srgb(r), Srgb(g), Srgb(b), 3);
            Assert.Equal(255, pixels.At(x, y).A);
        }
    }

    [Fact]
    public void Malformed_dwa_chunks_fail_gracefully()
    {
        var good = WriteDwaExr(8, 0);
        var chunk = good.Length - DwaChunkLength; // start of the DWA size header
        // DC count that does not match the block grid.
        var badDc = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(badDc.AsSpan(chunk + 9 * 8), 7);
        AssertFails(Temp(_files, badDc, ".exr"));
        // Absurd sizes must not allocate.
        var huge = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(huge.AsSpan(chunk + 8 * 8), ulong.MaxValue / 3);
        AssertFails(Temp(_files, huge, ".exr"));
        var bigRle = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(bigRle.AsSpan(chunk + 6 * 8), int.MaxValue);
        AssertFails(Temp(_files, bigRle, ".exr"));
        // Unknown version, truncated payload and garbage streams.
        var badVersion = (byte[])good.Clone();
        badVersion[chunk] = 7;
        AssertFails(Temp(_files, badVersion, ".exr"));
        AssertFails(Temp(_files, good.Take(good.Length - 10).ToArray(), ".exr"));
        var garbage = (byte[])good.Clone();
        for (var i = good.Length - 40; i < good.Length; i++) garbage[i] ^= 0x5A;
        AssertFails(Temp(_files, garbage, ".exr"));
    }

    private static int DwaChunkLength;

    /// <summary>
    /// 16x8 file with channels A (RLE), B/G/R (lossy DCT as one Y'CbCr set, DC-only blocks) and Z (UNKNOWN, zlib),
    /// hand-encoded like OpenEXR's DwaCompressor: version-2 rules, perceptual (x^(1/2.2)) values, orthonormal DC = 8 x mean.
    /// </summary>
    private static byte[] WriteDwaExr(int compression, int acCompression)
    {
        const int width = 16, height = 8;
        var channels = new[] { new Channel("A", 1), new Channel("B", 1), new Channel("G", 1), new Channel("R", 1), new Channel("Z", 1) };
        var plain = WriteExr(width, height, channels, 0, false, 0, 0);
        // The uncompressed file has one chunk per scanline; keep only its attribute header.
        var headerEnd = plain.Length - height * (8 + 8 + width * channels.Length * 2);
        var index = IndexOf(plain, Encoding.ASCII.GetBytes("compression\0compression\0"));
        plain[index + 24 + 4] = (byte)compression;

        // DC per component (Y, then Cb, then Cr), one value per block; nonlinear R'G'B' -> Y'CbCr (Rec.709).
        var dc = new ushort[6];
        for (var block = 0; block < 2; block++)
        {
            var (r, g, b) = DwaBlocks[block];
            float nr = MathF.Pow(r, 1 / 2.2f), ng = MathF.Pow(g, 1 / 2.2f), nb = MathF.Pow(b, 1 / 2.2f);
            var yv = 0.2126f * nr + 0.7152f * ng + 0.0722f * nb;
            dc[block] = BitConverter.HalfToUInt16Bits((Half)(8 * yv));
            dc[2 + block] = BitConverter.HalfToUInt16Bits((Half)(8 * (nb - yv) / 1.8556f));
            dc[4 + block] = BitConverter.HalfToUInt16Bits((Half)(8 * (nr - yv) / 1.5747f));
        }
        var dcBytes = new Writer();
        foreach (var v in dc) dcBytes.U16(v);
        var dcStream = Zlib(Predict(dcBytes.ToArray()));
        // AC: every block/component is just "end of block".
        var ac = Enumerable.Repeat((ushort)0xff00, 6).ToArray();
        byte[] acStream;
        if (acCompression == 0) acStream = HufCompress(ac);
        else
        {
            var acBytes = new Writer();
            foreach (var v in ac) acBytes.U16(v);
            acStream = Zlib(acBytes.ToArray());
        }
        // A = 1.0: RLE of its byte planes (all low bytes, then all high bytes), then zlib.
        var one = BitConverter.HalfToUInt16Bits((Half)1f);
        var planes = new byte[width * height * 2];
        planes.AsSpan(0, width * height).Fill((byte)one);
        planes.AsSpan(width * height).Fill((byte)(one >> 8));
        var rlePacked = Rle(planes);
        var rleStream = Zlib(rlePacked);
        // Z = 0.5: plain zlib.
        var z = new Writer();
        for (var i = 0; i < width * height; i++) z.U16(BitConverter.HalfToUInt16Bits((Half)0.5f));
        var unknownRaw = z.ToArray();
        var unknownStream = Zlib(unknownRaw);

        var rules = new Writer();
        foreach (var (suffix, csc) in new[] { ("R", 0), ("G", 1), ("B", 2) }) rules.Ascii(suffix).U8(0).U8((csc + 1) << 4 | 1 << 2).U8(1);
        rules.Ascii("A").U8(0).U8(2 << 2).U8(1);
        var ruleBytes = rules.ToArray();

        var chunk = new Writer();
        foreach (var size in new long[] { 2, unknownRaw.Length, unknownStream.Length, acStream.Length, dcStream.Length, rleStream.Length, rlePacked.Length, planes.Length, ac.Length, dc.Length, acCompression })
            chunk.U64(size);
        chunk.U16(ruleBytes.Length + 2).Bytes(ruleBytes).Bytes(unknownStream).Bytes(acStream).Bytes(dcStream).Bytes(rleStream);
        var payload = chunk.ToArray();
        DwaChunkLength = payload.Length;
        return new Writer().Bytes(plain.AsSpan(0, headerEnd)).U64(headerEnd + 8).U32(0).U32(payload.Length).Bytes(payload).ToArray();
    }
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    private static float Quantize(float v, int type) => type switch
    {
        1 => (float)(Half)v,
        0 => (uint)Math.Round(v * 4294967295.0) / 4294967295f,
        _ => v,
    };

    // -- Writer ---------------------------------------------------------------------------------

    private static float _alphaOverride = float.NaN;

    private static byte[] WriteExr(int width, int height, Channel[] channels, int compression, bool tiled, int tileWidth, int tileHeight, float alphaOverride = float.NaN)
    {
        _alphaOverride = alphaOverride;
        channels = channels.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        var header = new Writer();
        header.U32(20000630).U32(2 | (tiled ? 0x200 : 0));
        void Attribute(string name, string type, byte[] value)
        {
            header.Ascii(name).U8(0).Ascii(type).U8(0).U32(value.Length).Bytes(value);
        }
        var chlist = new Writer();
        foreach (var c in channels) chlist.Ascii(c.Name).U8(0).U32(c.Type).U8(0).Zeros(3).U32(1).U32(1);
        chlist.U8(0);
        Attribute("channels", "chlist", chlist.ToArray());
        Attribute("compression", "compression", new[] { (byte)compression });
        var box = new Writer().U32(0).U32(0).U32(width - 1).U32(height - 1).ToArray();
        Attribute("dataWindow", "box2i", box);
        Attribute("displayWindow", "box2i", box);
        Attribute("lineOrder", "lineOrder", new byte[] { 0 });
        Attribute("pixelAspectRatio", "float", new Writer().F32(1).ToArray());
        Attribute("screenWindowCenter", "v2f", new Writer().F32(0).F32(0).ToArray());
        Attribute("screenWindowWidth", "float", new Writer().F32(1).ToArray());
        if (tiled) Attribute("tiles", "tiledesc", new Writer().U32(tileWidth).U32(tileHeight).U8(0).ToArray());
        header.U8(0);

        var chunks = new List<byte[]>();
        if (tiled)
        {
            for (var ty = 0; ty * tileHeight < height; ty++)
            for (var tx = 0; tx * tileWidth < width; tx++)
            {
                var x0 = tx * tileWidth; var y0 = ty * tileHeight;
                var w = Math.Min(tileWidth, width - x0); var h = Math.Min(tileHeight, height - y0);
                var raw = RawRegion(channels, width, height, x0, y0, w, h);
                var packed = Compress(raw, compression, channels, w, h);
                chunks.Add(new Writer().U32(tx).U32(ty).U32(0).U32(0).U32(packed.Length).Bytes(packed).ToArray());
            }
        }
        else
        {
            var lines = compression switch { 3 or 5 => 16, 4 or 6 => 32, _ => 1 };
            for (var y = 0; y < height; y += lines)
            {
                var h = Math.Min(lines, height - y);
                var raw = RawRegion(channels, width, height, 0, y, width, h);
                var packed = Compress(raw, compression, channels, width, h);
                chunks.Add(new Writer().U32(y).U32(packed.Length).Bytes(packed).ToArray());
            }
        }

        var headerBytes = header.ToArray();
        var output = new Writer();
        output.Bytes(headerBytes);
        long offset = headerBytes.Length + chunks.Count * 8L;
        foreach (var chunk in chunks)
        {
            output.U64(offset);
            offset += chunk.Length;
        }
        foreach (var chunk in chunks) output.Bytes(chunk);
        return output.ToArray();
    }

    private static byte[] RawRegion(Channel[] channels, int width, int height, int x0, int y0, int w, int h)
    {
        var raw = new Writer();
        for (var y = y0; y < y0 + h; y++)
        {
            foreach (var c in channels)
            {
                for (var x = x0; x < x0 + w; x++)
                {
                    var v = Sample(c.Name.Contains('.') ? c.Name[(c.Name.LastIndexOf('.') + 1)..] : c.Name, x, y, width, height);
                    if (c.Name == "Y") v = 0.5f;
                    if (c.Name != "A" && Sample("A", x, y, width, height) < 1f && channels.Any(k => k.Name == "A")) v *= 0.5f; // premultiply
                    switch (c.Type)
                    {
                        case 1: raw.U16(BitConverter.HalfToUInt16Bits((Half)v)); break;
                        case 2: raw.F32(v); break;
                        default: raw.U32((uint)Math.Round(v * 4294967295.0)); break;
                    }
                }
            }
        }
        return raw.ToArray();
    }

    private static byte[] Compress(byte[] raw, int compression, Channel[] channels, int w, int h)
    {
        switch (compression)
        {
            case 0: return raw;
            case 1: return Rle(Predict(raw));
            case 2:
            case 3: return Zlib(Predict(raw));
            case 5: return Pxr24(raw, channels, w, h);
            case 6: return B44(raw, channels, w, h);
            default: return Piz(raw, channels, w, h);
        }
    }

    /// <summary>PXR24: per scanline and channel, byte planes of the sample deltas (FLOAT keeps 24 bits), zlib-compressed.</summary>
    private static byte[] Pxr24(byte[] raw, Channel[] channels, int w, int h)
    {
        var output = new List<byte>();
        var position = 0;
        for (var y = 0; y < h; y++)
        {
            foreach (var c in channels)
            {
                var n = w;
                switch (c.Type)
                {
                    case 1:
                    {
                        var hi = new byte[n]; var lo = new byte[n]; uint previous = 0;
                        for (var i = 0; i < n; i++)
                        {
                            uint value = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position + i * 2));
                            var diff = (value - previous) & 0xFFFF; previous = value;
                            hi[i] = (byte)(diff >> 8); lo[i] = (byte)diff;
                        }
                        output.AddRange(hi); output.AddRange(lo); position += n * 2;
                        break;
                    }
                    case 2:
                    {
                        var p0 = new byte[n]; var p1 = new byte[n]; var p2 = new byte[n]; uint previous = 0;
                        for (var i = 0; i < n; i++)
                        {
                            var value = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(position + i * 4)) & 0xFFFFFF00u;
                            var diff = value - previous; previous = value;
                            p0[i] = (byte)(diff >> 24); p1[i] = (byte)(diff >> 16); p2[i] = (byte)(diff >> 8);
                        }
                        output.AddRange(p0); output.AddRange(p1); output.AddRange(p2); position += n * 4;
                        break;
                    }
                    default:
                    {
                        var p0 = new byte[n]; var p1 = new byte[n]; var p2 = new byte[n]; var p3 = new byte[n]; uint previous = 0;
                        for (var i = 0; i < n; i++)
                        {
                            var value = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(position + i * 4));
                            var diff = value - previous; previous = value;
                            p0[i] = (byte)(diff >> 24); p1[i] = (byte)(diff >> 16); p2[i] = (byte)(diff >> 8); p3[i] = (byte)diff;
                        }
                        output.AddRange(p0); output.AddRange(p1); output.AddRange(p2); output.AddRange(p3); position += n * 4;
                        break;
                    }
                }
            }
        }
        return Zlib(output.ToArray());
    }

    /// <summary>B44: per channel (planar), HALF channels as 4x4 blocks of 14 bytes (3 when flat), others raw.</summary>
    private static byte[] B44(byte[] raw, Channel[] channels, int w, int h)
    {
        // With no HALF channel there is nothing to pack: the data is not smaller, so it is stored as is.
        if (channels.All(c => c.Type != 1)) return raw;
        var planes = channels.Select(c => new byte[w * h * (c.Type == 1 ? 2 : 4)]).ToArray();
        var position = 0;
        for (var y = 0; y < h; y++)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                var bytes = w * (channels[c].Type == 1 ? 2 : 4);
                Buffer.BlockCopy(raw, position, planes[c], y * bytes, bytes);
                position += bytes;
            }
        }
        var output = new List<byte>();
        for (var c = 0; c < channels.Length; c++)
        {
            if (channels[c].Type != 1) { output.AddRange(planes[c]); continue; }
            for (var y = 0; y < h; y += 4)
            {
                for (var x = 0; x < w; x += 4)
                {
                    var s = new ushort[16];
                    for (var row = 0; row < 4; row++)
                        for (var col = 0; col < 4; col++)
                            s[row * 4 + col] = BinaryPrimitives.ReadUInt16LittleEndian(planes[c].AsSpan((Math.Min(y + row, h - 1) * w + Math.Min(x + col, w - 1)) * 2));
                    output.AddRange(PackB44(s));
                }
            }
        }
        return output.ToArray();
    }

    private static byte[] PackB44(ushort[] s)
    {
        var t = new int[16];
        var tMax = 0;
        for (var i = 0; i < 16; i++)
        {
            t[i] = (s[i] & 0x7C00) == 0x7C00 ? 0x8000 : (s[i] & 0x8000) != 0 ? (ushort)~s[i] : s[i] | 0x8000;
            tMax = Math.Max(tMax, t[i]);
        }
        const int bias = 0x20;
        var shift = -1;
        var d = new int[16];
        var r = new int[15];
        int rMin, rMax;
        do
        {
            shift++;
            for (var i = 0; i < 16; i++) d[i] = ShiftAndRound(tMax - t[i], shift);
            r[0] = d[0] - d[4] + bias; r[1] = d[4] - d[8] + bias; r[2] = d[8] - d[12] + bias;
            r[3] = d[0] - d[1] + bias; r[4] = d[4] - d[5] + bias; r[5] = d[8] - d[9] + bias; r[6] = d[12] - d[13] + bias;
            r[7] = d[1] - d[2] + bias; r[8] = d[5] - d[6] + bias; r[9] = d[9] - d[10] + bias; r[10] = d[13] - d[14] + bias;
            r[11] = d[2] - d[3] + bias; r[12] = d[6] - d[7] + bias; r[13] = d[10] - d[11] + bias; r[14] = d[14] - d[15] + bias;
            rMin = r.Min(); rMax = r.Max();
        } while (rMin < 0 || rMax > 0x3F);
        if (rMin == bias && rMax == bias)
            return new[] { (byte)(t[0] >> 8), (byte)t[0], (byte)0xFC };
        return new[]
        {
            (byte)(t[0] >> 8), (byte)t[0], (byte)((shift << 2) | (r[0] >> 4)), (byte)((r[0] << 4) | (r[1] >> 2)), (byte)((r[1] << 6) | r[2]),
            (byte)((r[3] << 2) | (r[4] >> 4)), (byte)((r[4] << 4) | (r[5] >> 2)), (byte)((r[5] << 6) | r[6]),
            (byte)((r[7] << 2) | (r[8] >> 4)), (byte)((r[8] << 4) | (r[9] >> 2)), (byte)((r[9] << 6) | r[10]),
            (byte)((r[11] << 2) | (r[12] >> 4)), (byte)((r[12] << 4) | (r[13] >> 2)), (byte)((r[13] << 6) | r[14]),
        };
    }

    private static int ShiftAndRound(int x, int shift)
    {
        x <<= 1;
        var a = (1 << shift) - 1;
        shift += 1;
        var b = (x >> shift) & 1;
        return (x + a + b) >> shift;
    }

    /// <summary>Uncompressed Y/RY/BY file: Y full resolution, RY and BY sampled every second pixel and line.</summary>
    private static byte[] WriteYcExr(int width, int height, float y, float ry, float by)
    {
        var header = new Writer();
        header.U32(20000630).U32(2);
        void Attribute(string name, string type, byte[] value) => header.Ascii(name).U8(0).Ascii(type).U8(0).U32(value.Length).Bytes(value);
        var chlist = new Writer();
        chlist.Ascii("BY").U8(0).U32(1).U8(0).Zeros(3).U32(2).U32(2);
        chlist.Ascii("RY").U8(0).U32(1).U8(0).Zeros(3).U32(2).U32(2);
        chlist.Ascii("Y").U8(0).U32(1).U8(0).Zeros(3).U32(1).U32(1);
        chlist.U8(0);
        Attribute("channels", "chlist", chlist.ToArray());
        Attribute("compression", "compression", new byte[] { 0 });
        var box = new Writer().U32(0).U32(0).U32(width - 1).U32(height - 1).ToArray();
        Attribute("dataWindow", "box2i", box);
        Attribute("displayWindow", "box2i", box);
        Attribute("lineOrder", "lineOrder", new byte[] { 0 });
        Attribute("pixelAspectRatio", "float", new Writer().F32(1).ToArray());
        Attribute("screenWindowCenter", "v2f", new Writer().F32(0).F32(0).ToArray());
        Attribute("screenWindowWidth", "float", new Writer().F32(1).ToArray());
        header.U8(0);
        var chunks = new List<byte[]>();
        for (var line = 0; line < height; line++)
        {
            var body = new Writer();
            if (line % 2 == 0)
            {
                for (var x = 0; x < width; x += 2) body.U16(BitConverter.HalfToUInt16Bits((Half)by));
                for (var x = 0; x < width; x += 2) body.U16(BitConverter.HalfToUInt16Bits((Half)ry));
            }
            for (var x = 0; x < width; x++) body.U16(BitConverter.HalfToUInt16Bits((Half)y));
            var bytes = body.ToArray();
            chunks.Add(new Writer().U32(line).U32(bytes.Length).Bytes(bytes).ToArray());
        }
        var headerBytes = header.ToArray();
        var output = new Writer();
        output.Bytes(headerBytes);
        long offset = headerBytes.Length + chunks.Count * 8L;
        foreach (var chunk in chunks) { output.U64(offset); offset += chunk.Length; }
        foreach (var chunk in chunks) output.Bytes(chunk);
        return output.ToArray();
    }

    /// <summary>Even/odd byte split followed by the delta predictor (inverse of the decoder).</summary>
    private static byte[] Predict(byte[] raw)
    {
        var t = new byte[raw.Length];
        var half = (raw.Length + 1) / 2;
        for (int i = 0, a = 0, b = half; i < raw.Length; i++)
        {
            if (i % 2 == 0) t[a++] = raw[i]; else t[b++] = raw[i];
        }
        var output = new byte[t.Length];
        var previous = 0;
        for (var i = 0; i < t.Length; i++)
        {
            output[i] = i == 0 ? t[0] : (byte)(t[i] - previous + 128 + 256);
            previous = t[i];
        }
        return output;
    }

    private static byte[] Rle(byte[] data)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < data.Length)
        {
            var run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 128) run++;
            if (run >= 3)
            {
                output.Add((byte)(run - 1));
                output.Add(data[i]);
                i += run;
                continue;
            }
            var literal = Math.Min(127, data.Length - i);
            var n = 0;
            while (n < literal && !(i + n + 2 < data.Length && data[i + n] == data[i + n + 1] && data[i + n] == data[i + n + 2])) n++;
            if (n == 0) n = 1;
            output.Add((byte)(sbyte)(-n));
            for (var k = 0; k < n; k++) output.Add(data[i + k]);
            i += n;
        }
        return output.ToArray();
    }

    // -- PIZ encoder (bitmap/LUT + Haar wavelet + canonical Huffman), mirroring OpenEXR ----------

    private static byte[] Piz(byte[] raw, Channel[] channels, int w, int h)
    {
        var words = raw.Length / 2;
        var data = new ushort[words];
        // Line-interleaved -> per-channel planes.
        var starts = new int[channels.Length];
        var acc = 0;
        for (var c = 0; c < channels.Length; c++)
        {
            starts[c] = acc;
            acc += w * h * (channels[c].Type == 1 ? 1 : 2);
        }
        var cursors = new int[channels.Length];
        var position = 0;
        for (var y = 0; y < h; y++)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                var n = w * (channels[c].Type == 1 ? 1 : 2);
                for (var k = 0; k < n; k++)
                {
                    data[starts[c] + cursors[c]++] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(position));
                    position += 2;
                }
            }
        }

        var bitmap = new byte[8192];
        foreach (var v in data) bitmap[v >> 3] |= (byte)(1 << (v & 7));
        bitmap[0] &= 0xFE;
        int minNonZero = 8191, maxNonZero = 0;
        for (var i = 0; i < 8192; i++)
        {
            if (bitmap[i] == 0) continue;
            minNonZero = Math.Min(minNonZero, i);
            maxNonZero = Math.Max(maxNonZero, i);
        }
        var lut = new ushort[65536];
        var k2 = 0;
        for (var i = 0; i < 65536; i++)
            if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0) lut[i] = (ushort)k2++;
        var maxValue = k2 - 1;
        for (var i = 0; i < data.Length; i++) data[i] = lut[data[i]];

        for (var c = 0; c < channels.Length; c++)
        {
            var size = channels[c].Type == 1 ? 1 : 2;
            for (var j = 0; j < size; j++) Wav2Encode(data, starts[c] + j, w, size, h, w * size, (ushort)maxValue);
        }

        var huffman = HufCompress(data);
        var output = new Writer();
        output.U16(minNonZero).U16(maxNonZero);
        if (minNonZero <= maxNonZero) output.Bytes(bitmap.AsSpan(minNonZero, maxNonZero - minNonZero + 1));
        output.U32(huffman.Length).Bytes(huffman);
        return output.ToArray();
    }

    private static void Wenc14(ushort a, ushort b, out ushort l, out ushort h)
    {
        var a_s = (short)a; var b_s = (short)b;
        l = (ushort)(short)((a_s + b_s) >> 1);
        h = (ushort)(short)(a_s - b_s);
    }

    private static void Wav2Encode(ushort[] v, int start, int nx, int ox, int ny, int oy, ushort mx)
    {
        Assert.True(mx < (1 << 14), "test encoder only implements the 14-bit wavelet");
        var n = nx > ny ? ny : nx;
        var p = 1;
        var p2 = 2;
        while (p2 <= n)
        {
            var py = start;
            var ey = start + oy * (ny - p2);
            int oy1 = oy * p, oy2 = oy * p2, ox1 = ox * p, ox2 = ox * p2;
            ushort i00, i01, i10, i11;
            for (; py <= ey; py += oy2)
            {
                var px = py;
                var ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    var p01 = px + ox1; var p10 = px + oy1; var p11 = p10 + ox1;
                    Wenc14(v[px], v[p01], out i00, out i01);
                    Wenc14(v[p10], v[p11], out i10, out i11);
                    Wenc14(i00, i10, out v[px], out v[p10]);
                    Wenc14(i01, i11, out v[p01], out v[p11]);
                }
                if ((nx & p) != 0)
                {
                    var p10 = px + oy1;
                    Wenc14(v[px], v[p10], out i00, out v[p10]);
                    v[px] = i00;
                }
            }
            if ((ny & p) != 0)
            {
                var px = py;
                var ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    var p01 = px + ox1;
                    Wenc14(v[px], v[p01], out i00, out v[p01]);
                    v[px] = i00;
                }
            }
            p = p2;
            p2 <<= 1;
        }
    }

    /// <summary>
    /// Fixed-length canonical Huffman code over the used symbols plus the run-length pseudo-symbol
    /// (never emitted), packed exactly like OpenEXR's hufCompress output.
    /// </summary>
    private static byte[] HufCompress(ushort[] data)
    {
        var used = data.Distinct().OrderBy(x => x).ToList();
        int im = used[0];
        var rlc = used[^1] + 1;
        var symbolCount = used.Count + 1;
        var length = 1;
        while ((1 << length) < symbolCount) length++;
        Assert.True(length <= 14);

        var lengths = new Dictionary<int, int>();
        foreach (var s in used) lengths[s] = length;
        lengths[rlc] = length;
        // Canonical codes: equal lengths -> consecutive codes in symbol order.
        var codes = new Dictionary<int, int>();
        var next = 0;
        for (var s = im; s <= rlc; s++)
            if (lengths.ContainsKey(s)) codes[s] = next++;

        var table = new BitWriter();
        for (var s = im; s <= rlc; s++) table.Write(lengths.TryGetValue(s, out var l) ? l : 0, 6);
        var tableBytes = table.Finish();

        var bits = new BitWriter();
        foreach (var value in data) bits.Write(codes[value], length);
        var nBits = bits.BitCount;
        var dataBytes = bits.Finish();

        var output = new Writer();
        output.U32(im).U32(rlc).U32(tableBytes.Length).U32(nBits).U32(0).Bytes(tableBytes).Bytes(dataBytes);
        return output.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private long _c;
        private int _lc;
        public int BitCount { get; private set; }

        public void Write(int value, int count)
        {
            _c = (_c << count) | (uint)value;
            _lc += count;
            BitCount += count;
            while (_lc >= 8)
            {
                _lc -= 8;
                _bytes.Add((byte)(_c >> _lc));
            }
        }

        public byte[] Finish()
        {
            if (_lc > 0) _bytes.Add((byte)(_c << (8 - _lc)));
            _lc = 0;
            return _bytes.ToArray();
        }
    }
}
