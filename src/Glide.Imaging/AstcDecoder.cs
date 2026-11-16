namespace Glide.Imaging;

/// <summary>
/// ASTC LDR decoder for 2-D blocks (4x4 … 12x12): block modes, 1–4 partitions with the standard
/// partition hash, dual-plane weights, integer-sequence (bits/trits/quints) colour and weight data,
/// weight-grid infill and all LDR colour endpoint modes. Void-extent blocks are supported; HDR endpoint
/// modes and HDR void extents decode to the ASTC error colour (magenta), as LDR decoders must.
/// </summary>
internal static class AstcDecoder
{
    // Quantisation levels: (bits, trits, quints) per level index.
    private static readonly (int Bits, bool Trit, bool Quint)[] Quant =
    {
        (1, false, false), (0, true, false), (2, false, false), (0, false, true), (1, true, false), (3, false, false),
        (1, false, true), (2, true, false), (4, false, false), (2, false, true), (3, true, false), (5, false, false),
        (3, false, true), (4, true, false), (6, false, false), (4, false, true), (5, true, false), (7, false, false),
        (5, false, true), (6, true, false), (8, false, false),
    };

    public static bool IsValidBlockSize(int bw, int bh)
        => (bw, bh) is (4, 4) or (5, 4) or (5, 5) or (6, 5) or (6, 6) or (8, 5) or (8, 6) or (8, 8) or
            (10, 5) or (10, 6) or (10, 8) or (10, 10) or (12, 10) or (12, 12);

    public static unsafe bool Decode(ReadOnlySpan<byte> data, int width, int height, int bw, int bh, byte[] bgra)
    {
        if (!IsValidBlockSize(bw, bh)) return false;
        var blocksX = (width + bw - 1) / bw;
        var blocksY = (height + bh - 1) / bh;
        if ((long)blocksX * blocksY * 16 > data.Length) return false;
        fixed (byte* pointer = data)
        {
            // Block rows are independent and write disjoint pixel rows (same result as a serial loop).
            var address = (nint)pointer;
            var length = data.Length;
            ManagedCodecSupport.ForBands(blocksY, (long)blocksX * bw * bh, (start, end) =>
            {
                var source = new ReadOnlySpan<byte>((void*)address, length);
                var texels = new byte[bw * bh * 4];
                for (var by = start; by < end; by++)
                {
                    for (var bx = 0; bx < blocksX; bx++)
                    {
                        DecodeBlock(source.Slice((by * blocksX + bx) * 16, 16), bw, bh, texels);
                        for (var ty = 0; ty < bh; ty++)
                        {
                            var y = by * bh + ty;
                            if (y >= height) break;
                            for (var tx = 0; tx < bw; tx++)
                            {
                                var x = bx * bw + tx;
                                if (x >= width) break;
                                var s = (ty * bw + tx) * 4;
                                var d = ((long)y * width + x) * 4;
                                bgra[d] = texels[s + 2]; bgra[d + 1] = texels[s + 1]; bgra[d + 2] = texels[s]; bgra[d + 3] = texels[s + 3];
                            }
                        }
                    }
                }
            });
        }
        return true;
    }

    private struct Bits128
    {
        public ulong Low, High;

        public readonly int Get(int start, int count)
        {
            if (count == 0) return 0;
            ulong value;
            if (start >= 64) value = High >> (start - 64);
            else if (start + count <= 64) value = Low >> start;
            else value = (Low >> start) | (High << (64 - start));
            return (int)(value & ((1UL << count) - 1));
        }

        public readonly Bits128 Reverse()
        {
            static ulong Rev(ulong v)
            {
                ulong r = 0;
                for (var i = 0; i < 64; i++) { r = (r << 1) | (v & 1); v >>= 1; }
                return r;
            }
            return new Bits128 { Low = Rev(High), High = Rev(Low) };
        }
    }

    private static void FillError(Span<byte> texels, int count)
    {
        for (var i = 0; i < count; i++)
        {
            texels[i * 4] = 255; texels[i * 4 + 1] = 0; texels[i * 4 + 2] = 255; texels[i * 4 + 3] = 255;
        }
    }

    internal static void DecodeBlock(ReadOnlySpan<byte> block, int bw, int bh, Span<byte> texels)
    {
        var texelCount = bw * bh;
        var bits = new Bits128
        {
            Low = BitConverter.ToUInt64(block),
            High = BitConverter.ToUInt64(block[8..]),
        };
        var blockMode = bits.Get(0, 11);

        // Void extent: a single constant colour.
        if ((blockMode & 0x1FF) == 0x1FC)
        {
            if ((blockMode & 0x200) != 0) { FillError(texels, texelCount); return; } // HDR void extent
            var r = (byte)(bits.Get(64, 16) >> 8);
            var g = (byte)(bits.Get(80, 16) >> 8);
            var b = (byte)(bits.Get(96, 16) >> 8);
            var a = (byte)(bits.Get(112, 16) >> 8);
            for (var i = 0; i < texelCount; i++)
            {
                texels[i * 4] = r; texels[i * 4 + 1] = g; texels[i * 4 + 2] = b; texels[i * 4 + 3] = a;
            }
            return;
        }

        if (!DecodeBlockMode(blockMode, out var xw, out var yw, out var dual, out var weightQuant) || xw > bw || yw > bh)
        {
            FillError(texels, texelCount);
            return;
        }
        var partitions = bits.Get(11, 2) + 1;
        if (dual && partitions == 4) { FillError(texels, texelCount); return; }

        var weightCount = xw * yw * (dual ? 2 : 1);
        var weightBits = IseBits(weightCount, weightQuant);
        if (weightCount > 64 || weightBits < 24 || weightBits > 96) { FillError(texels, texelCount); return; }

        Span<int> cem = stackalloc int[4];
        var partitionIndex = 0;
        var extraCemBits = 0;
        int colorStart;
        if (partitions == 1)
        {
            cem[0] = bits.Get(13, 4);
            colorStart = 17;
        }
        else
        {
            partitionIndex = bits.Get(13, 10);
            var cemField = bits.Get(23, 6);
            colorStart = 29;
            if ((cemField & 3) == 0)
            {
                for (var i = 0; i < partitions; i++) cem[i] = cemField >> 2;
            }
            else
            {
                extraCemBits = 3 * partitions - 4;
                var high = bits.Get(128 - weightBits - extraCemBits, extraCemBits);
                var encoded = cemField | (high << 6);
                var baseClass = (encoded & 3) - 1;
                for (var i = 0; i < partitions; i++)
                {
                    var classBit = (encoded >> (2 + i)) & 1;
                    var modeBits = (encoded >> (2 + partitions + 2 * i)) & 3;
                    cem[i] = ((baseClass + classBit) << 2) | modeBits;
                }
            }
        }

        var plane2Component = -1;
        var belowWeights = 128 - weightBits - extraCemBits;
        if (dual)
        {
            belowWeights -= 2;
            plane2Component = bits.Get(belowWeights, 2);
        }

        var colorValueCount = 0;
        for (var i = 0; i < partitions; i++) colorValueCount += ((cem[i] >> 2) + 1) * 2;
        if (colorValueCount > 18) { FillError(texels, texelCount); return; }
        var colorBitsAvailable = belowWeights - colorStart;
        var colorQuant = -1;
        for (var q = 20; q >= 0; q--)
        {
            if (IseBits(colorValueCount, q) <= colorBitsAvailable) { colorQuant = q; break; }
        }
        if (colorQuant < 4) { FillError(texels, texelCount); return; }

        Span<int> colorValues = stackalloc int[18];
        DecodeIse(bits, colorStart, colorValueCount, colorQuant, colorValues);
        for (var i = 0; i < colorValueCount; i++) colorValues[i] = UnquantizeColor(colorQuant, colorValues[i]);

        Span<int> weights = stackalloc int[64 + 16];
        weights.Clear();
        DecodeIse(bits.Reverse(), 0, weightCount, weightQuant, weights);
        for (var i = 0; i < weightCount; i++) weights[i] = UnquantizeWeight(weightQuant, weights[i]);

        // Endpoints per partition (RGBA, 8-bit).
        Span<int> endpoints = stackalloc int[4 * 8];
        var valueOffset = 0;
        for (var p = 0; p < partitions; p++)
        {
            var count = ((cem[p] >> 2) + 1) * 2;
            if (!DecodeEndpoints(cem[p], colorValues.Slice(valueOffset, count), endpoints.Slice(p * 8, 8)))
            {
                FillError(texels, texelCount);
                return;
            }
            valueOffset += count;
        }

        // Weight grid infill (separate grids per plane when dual-plane).
        var planes = dual ? 2 : 1;
        Span<int> plane0 = stackalloc int[64 + 16];
        Span<int> plane1 = stackalloc int[64 + 16];
        plane0.Clear();
        plane1.Clear();
        for (var i = 0; i < xw * yw; i++)
        {
            plane0[i] = weights[i * planes];
            if (dual) plane1[i] = weights[i * 2 + 1];
        }

        var ds = (1024 + bw / 2) / (bw - 1);
        var dt = (1024 + bh / 2) / (bh - 1);
        var smallBlock = texelCount < 31;
        for (var t = 0; t < bh; t++)
        {
            for (var s = 0; s < bw; s++)
            {
                var gs = (ds * s * (xw - 1) + 32) >> 6;
                var gt = (dt * t * (yw - 1) + 32) >> 6;
                int js = gs >> 4, fs = gs & 0xF, jt = gt >> 4, ft = gt & 0xF;
                var w11 = (fs * ft + 8) >> 4;
                var w10 = ft - w11;
                var w01 = fs - w11;
                var w00 = 16 - fs - ft + w11;
                var v0 = js + jt * xw;
                var weight0 = Infill(plane0, v0, xw, w00, w01, w10, w11);
                var weight1 = dual ? Infill(plane1, v0, xw, w00, w01, w10, w11) : 0;

                var partition = partitions > 1 ? SelectPartition(partitionIndex, s, t, 0, partitions, smallBlock) : 0;
                var e = partition * 8;
                var o = (t * bw + s) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var w = c == plane2Component ? weight1 : weight0;
                    var c0 = endpoints[e + c] * 257;
                    var c1 = endpoints[e + 4 + c] * 257;
                    var value = (c0 * (64 - w) + c1 * w + 32) >> 6;
                    texels[o + c] = (byte)(value >> 8);
                }
            }
        }
    }

    private static int Infill(Span<int> grid, int v0, int xw, int w00, int w01, int w10, int w11)
    {
        var p00 = grid[v0];
        var p01 = grid[v0 + 1];
        var p10 = grid[v0 + xw];
        var p11 = grid[v0 + xw + 1];
        return (p00 * w00 + p01 * w01 + p10 * w10 + p11 * w11 + 8) >> 4;
    }

    private static bool DecodeBlockMode(int mode, out int xw, out int yw, out bool dual, out int quant)
    {
        xw = yw = quant = 0;
        dual = false;
        var baseQuant = (mode >> 4) & 1;
        var h = (mode >> 9) & 1;
        var d = (mode >> 10) & 1;
        var a = (mode >> 5) & 3;
        if ((mode & 3) != 0)
        {
            baseQuant |= (mode & 3) << 1;
            var b = (mode >> 7) & 3;
            switch ((mode >> 2) & 3)
            {
                case 0: xw = b + 4; yw = a + 2; break;
                case 1: xw = b + 8; yw = a + 2; break;
                case 2: xw = a + 2; yw = b + 8; break;
                default:
                    b &= 1;
                    if ((mode & 0x100) != 0) { xw = b + 2; yw = a + 2; }
                    else { xw = a + 2; yw = b + 6; }
                    break;
            }
        }
        else
        {
            if (((mode >> 2) & 3) == 0) return false;
            baseQuant |= ((mode >> 2) & 3) << 1;
            var b = (mode >> 9) & 3;
            switch ((mode >> 7) & 3)
            {
                case 0: xw = 12; yw = a + 2; break;
                case 1: xw = a + 2; yw = 12; break;
                case 2: xw = a + 6; yw = b + 6; d = 0; h = 0; break;
                default:
                    switch ((mode >> 5) & 3)
                    {
                        case 0: xw = 6; yw = 10; break;
                        case 1: xw = 10; yw = 6; break;
                        default: return false;
                    }
                    break;
            }
        }
        if (baseQuant < 2) return false;
        quant = baseQuant - 2 + 6 * h;
        dual = d == 1;
        return true;
    }

    internal static int IseBits(int count, int quant)
    {
        var (bits, trit, quint) = Quant[quant];
        var total = count * bits;
        if (trit) total += (8 * count + 4) / 5;
        if (quint) total += (7 * count + 2) / 3;
        return total;
    }

    private static void DecodeIse(Bits128 source, int start, int count, int quant, Span<int> output)
    {
        var (n, trit, quint) = Quant[quant];
        var end = start + IseBits(count, quant);
        var position = start;
        int Read(int width)
        {
            var value = 0;
            for (var i = 0; i < width; i++)
            {
                if (position < end && position < 128) value |= source.Get(position, 1) << i;
                position++;
            }
            return value;
        }

        if (!trit && !quint)
        {
            for (var i = 0; i < count; i++) output[i] = Read(n);
            return;
        }

        Span<int> m = stackalloc int[5];
        Span<int> digits = stackalloc int[5];
        if (trit)
        {
            for (var i = 0; i < count; i += 5)
            {
                m[0] = Read(n); var t = Read(2);
                m[1] = Read(n); t |= Read(2) << 2;
                m[2] = Read(n); t |= Read(1) << 4;
                m[3] = Read(n); t |= Read(2) << 5;
                m[4] = Read(n); t |= Read(1) << 7;
                DecodeTrits(t, digits);
                for (var j = 0; j < 5 && i + j < count; j++) output[i + j] = (digits[j] << n) | m[j];
            }
        }
        else
        {
            for (var i = 0; i < count; i += 3)
            {
                m[0] = Read(n); var q = Read(3);
                m[1] = Read(n); q |= Read(2) << 3;
                m[2] = Read(n); q |= Read(2) << 5;
                DecodeQuints(q, digits);
                for (var j = 0; j < 3 && i + j < count; j++) output[i + j] = (digits[j] << n) | m[j];
            }
        }
    }

    private static int Bit(int value, int bit) => (value >> bit) & 1;

    internal static void DecodeTrits(int t, Span<int> trits)
    {
        int c, t4, t3, t2, t1, t0;
        if (((t >> 2) & 7) == 7)
        {
            c = (((t >> 5) & 7) << 2) | (t & 3);
            t4 = 2;
            t3 = 2;
        }
        else
        {
            c = t & 0x1F;
            if (((t >> 5) & 3) == 3)
            {
                t4 = 2;
                t3 = Bit(t, 7);
            }
            else
            {
                t4 = Bit(t, 7);
                t3 = (t >> 5) & 3;
            }
        }
        if ((c & 3) == 3)
        {
            t2 = 2;
            t1 = Bit(c, 4);
            t0 = (Bit(c, 3) << 1) | (Bit(c, 2) & ~Bit(c, 3) & 1);
        }
        else if (((c >> 2) & 3) == 3)
        {
            t2 = 2;
            t1 = 2;
            t0 = c & 3;
        }
        else
        {
            t2 = Bit(c, 4);
            t1 = (c >> 2) & 3;
            t0 = (Bit(c, 1) << 1) | (Bit(c, 0) & ~Bit(c, 1) & 1);
        }
        trits[0] = t0; trits[1] = t1; trits[2] = t2; trits[3] = t3; trits[4] = t4;
    }

    internal static void DecodeQuints(int q, Span<int> quints)
    {
        int q0, q1, q2;
        if (((q >> 1) & 3) == 3 && ((q >> 5) & 3) == 0)
        {
            q2 = (Bit(q, 0) << 2) | ((Bit(q, 4) & ~Bit(q, 0) & 1) << 1) | (Bit(q, 3) & ~Bit(q, 0) & 1);
            q1 = 4;
            q0 = 4;
        }
        else
        {
            int c;
            if (((q >> 1) & 3) == 3)
            {
                q2 = 4;
                c = (((q >> 3) & 3) << 3) | ((~(q >> 5) & 3) << 1) | (q & 1);
            }
            else
            {
                q2 = (q >> 5) & 3;
                c = q & 0x1F;
            }
            if ((c & 7) == 5)
            {
                q1 = 4;
                q0 = (c >> 3) & 3;
            }
            else
            {
                q1 = (c >> 3) & 3;
                q0 = c & 7;
            }
        }
        quints[0] = q0; quints[1] = q1; quints[2] = q2;
    }

    private static int Replicate(int value, int fromBits, int toBits)
    {
        if (fromBits == 0) return 0;
        var result = 0;
        var shift = toBits;
        while (shift > 0)
        {
            shift -= fromBits;
            result |= shift >= 0 ? value << shift : value >> -shift;
        }
        return result & ((1 << toBits) - 1);
    }

    internal static int UnquantizeColor(int quant, int value)
    {
        var (n, trit, quint) = Quant[quant];
        if (!trit && !quint) return Replicate(value, n, 8);
        var m = value & ((1 << n) - 1);
        var digit = value >> n;
        var a = (m & 1) != 0 ? 0x1FF : 0;
        int b = Bit(m, 1), c = Bit(m, 2), d = Bit(m, 3), e = Bit(m, 4), f = Bit(m, 5);
        int bb, cc;
        if (trit)
        {
            switch (n)
            {
                case 1: bb = 0; cc = 204; break;
                case 2: bb = (b << 8) | (b << 4) | (b << 2) | (b << 1); cc = 93; break;
                case 3: bb = (c << 8) | (b << 7) | (c << 3) | (b << 2) | (c << 1) | b; cc = 44; break;
                case 4: bb = (d << 8) | (c << 7) | (b << 6) | (d << 2) | (c << 1) | b; cc = 22; break;
                case 5: bb = (e << 8) | (d << 7) | (c << 6) | (b << 5) | (e << 1) | d; cc = 11; break;
                default: bb = (f << 8) | (e << 7) | (d << 6) | (c << 5) | (b << 4) | f; cc = 5; break;
            }
        }
        else
        {
            switch (n)
            {
                case 1: bb = 0; cc = 113; break;
                case 2: bb = (b << 8) | (b << 3) | (b << 2); cc = 54; break;
                case 3: bb = (c << 8) | (b << 7) | (c << 2) | (b << 1) | c; cc = 26; break;
                case 4: bb = (d << 8) | (c << 7) | (b << 6) | (d << 1) | c; cc = 13; break;
                default: bb = (e << 8) | (d << 7) | (c << 6) | (b << 5) | e; cc = 6; break;
            }
        }
        var t = digit * cc + bb;
        t ^= a;
        return (a & 0x80) | (t >> 2);
    }

    internal static int UnquantizeWeight(int quant, int value)
    {
        var (n, trit, quint) = Quant[quant];
        int result;
        if (!trit && !quint)
        {
            result = Replicate(value, n, 6);
        }
        else if (n == 0)
        {
            return trit ? value * 32 : value * 16;
        }
        else
        {
            var m = value & ((1 << n) - 1);
            var digit = value >> n;
            var a = (m & 1) != 0 ? 0x7F : 0;
            int b = Bit(m, 1), c = Bit(m, 2);
            int bb, cc;
            if (trit)
            {
                switch (n)
                {
                    case 1: bb = 0; cc = 50; break;
                    case 2: bb = (b << 6) | (b << 2) | b; cc = 23; break;
                    default: bb = (c << 6) | (b << 5) | (c << 1) | b; cc = 11; break;
                }
            }
            else
            {
                switch (n)
                {
                    case 1: bb = 0; cc = 28; break;
                    default: bb = (b << 6) | (b << 1); cc = 13; break;
                }
            }
            var t = digit * cc + bb;
            t ^= a;
            result = (a & 0x20) | (t >> 2);
        }
        if (result > 32) result++;
        return result;
    }

    private static void BitTransferSigned(ref int a, ref int b)
    {
        b >>= 1;
        b |= a & 0x80;
        a >>= 1;
        a &= 0x3F;
        if ((a & 0x20) != 0) a -= 0x40;
    }

    private static void BlueContract(Span<int> e)
    {
        e[0] = (e[0] + e[2]) >> 1;
        e[1] = (e[1] + e[2]) >> 1;
    }

    private static void Set(Span<int> e, int r, int g, int b, int a)
    {
        e[0] = Math.Clamp(r, 0, 255); e[1] = Math.Clamp(g, 0, 255); e[2] = Math.Clamp(b, 0, 255); e[3] = Math.Clamp(a, 0, 255);
    }

    private static bool DecodeEndpoints(int mode, Span<int> v, Span<int> output)
    {
        var e0 = output[..4];
        var e1 = output.Slice(4, 4);
        switch (mode)
        {
            case 0:
                Set(e0, v[0], v[0], v[0], 255); Set(e1, v[1], v[1], v[1], 255);
                return true;
            case 1:
            {
                var l0 = (v[0] >> 2) | (v[1] & 0xC0);
                var l1 = Math.Min(l0 + (v[1] & 0x3F), 255);
                Set(e0, l0, l0, l0, 255); Set(e1, l1, l1, l1, 255);
                return true;
            }
            case 4:
                Set(e0, v[0], v[0], v[0], v[2]); Set(e1, v[1], v[1], v[1], v[3]);
                return true;
            case 5:
            {
                int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3];
                BitTransferSigned(ref v1, ref v0);
                BitTransferSigned(ref v3, ref v2);
                Set(e0, v0, v0, v0, v2); Set(e1, v0 + v1, v0 + v1, v0 + v1, v2 + v3);
                return true;
            }
            case 6:
                Set(e0, (v[0] * v[3]) >> 8, (v[1] * v[3]) >> 8, (v[2] * v[3]) >> 8, 255);
                Set(e1, v[0], v[1], v[2], 255);
                return true;
            case 8:
                if (v[1] + v[3] + v[5] >= v[0] + v[2] + v[4])
                {
                    Set(e0, v[0], v[2], v[4], 255); Set(e1, v[1], v[3], v[5], 255);
                }
                else
                {
                    Set(e0, v[1], v[3], v[5], 255); BlueContract(e0);
                    Set(e1, v[0], v[2], v[4], 255); BlueContract(e1);
                }
                return true;
            case 9:
            {
                int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3], v4 = v[4], v5 = v[5];
                BitTransferSigned(ref v1, ref v0);
                BitTransferSigned(ref v3, ref v2);
                BitTransferSigned(ref v5, ref v4);
                if (v1 + v3 + v5 >= 0)
                {
                    Set(e0, v0, v2, v4, 255); Set(e1, v0 + v1, v2 + v3, v4 + v5, 255);
                }
                else
                {
                    Set(e0, v0 + v1, v2 + v3, v4 + v5, 255); BlueContract(e0);
                    Set(e1, v0, v2, v4, 255); BlueContract(e1);
                }
                return true;
            }
            case 10:
                Set(e0, (v[0] * v[3]) >> 8, (v[1] * v[3]) >> 8, (v[2] * v[3]) >> 8, v[4]);
                Set(e1, v[0], v[1], v[2], v[5]);
                return true;
            case 12:
                if (v[1] + v[3] + v[5] >= v[0] + v[2] + v[4])
                {
                    Set(e0, v[0], v[2], v[4], v[6]); Set(e1, v[1], v[3], v[5], v[7]);
                }
                else
                {
                    Set(e0, v[1], v[3], v[5], v[7]); BlueContract(e0);
                    Set(e1, v[0], v[2], v[4], v[6]); BlueContract(e1);
                }
                return true;
            case 13:
            {
                int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3], v4 = v[4], v5 = v[5], v6 = v[6], v7 = v[7];
                BitTransferSigned(ref v1, ref v0);
                BitTransferSigned(ref v3, ref v2);
                BitTransferSigned(ref v5, ref v4);
                BitTransferSigned(ref v7, ref v6);
                if (v1 + v3 + v5 >= 0)
                {
                    Set(e0, v0, v2, v4, v6); Set(e1, v0 + v1, v2 + v3, v4 + v5, v6 + v7);
                }
                else
                {
                    Set(e0, v0 + v1, v2 + v3, v4 + v5, v6 + v7); BlueContract(e0);
                    Set(e1, v0, v2, v4, v6); BlueContract(e1);
                }
                return true;
            }
            default:
                return false; // HDR endpoint modes
        }
    }

    private static uint Hash52(uint p)
    {
        p ^= p >> 15; p -= p << 17; p += p << 7; p += p << 4;
        p ^= p >> 5; p += p << 16; p ^= p >> 7; p ^= p >> 3;
        p ^= p << 6; p ^= p >> 17;
        return p;
    }

    internal static int SelectPartition(int seed, int x, int y, int z, int partitionCount, bool smallBlock)
    {
        if (smallBlock) { x <<= 1; y <<= 1; z <<= 1; }
        seed += (partitionCount - 1) * 1024;
        var rnum = Hash52((uint)seed);
        int s1 = (int)(rnum & 0xF), s2 = (int)((rnum >> 4) & 0xF), s3 = (int)((rnum >> 8) & 0xF), s4 = (int)((rnum >> 12) & 0xF);
        int s5 = (int)((rnum >> 16) & 0xF), s6 = (int)((rnum >> 20) & 0xF), s7 = (int)((rnum >> 24) & 0xF), s8 = (int)((rnum >> 28) & 0xF);
        int s9 = (int)((rnum >> 18) & 0xF), s10 = (int)((rnum >> 22) & 0xF), s11 = (int)((rnum >> 26) & 0xF);
        var s12 = (int)(((rnum >> 30) | (rnum << 2)) & 0xF);
        s1 *= s1; s2 *= s2; s3 *= s3; s4 *= s4; s5 *= s5; s6 *= s6; s7 *= s7; s8 *= s8; s9 *= s9; s10 *= s10; s11 *= s11; s12 *= s12;
        int sh1, sh2;
        if ((seed & 1) != 0)
        {
            sh1 = (seed & 2) != 0 ? 4 : 5;
            sh2 = partitionCount == 3 ? 6 : 5;
        }
        else
        {
            sh1 = partitionCount == 3 ? 6 : 5;
            sh2 = (seed & 2) != 0 ? 4 : 5;
        }
        var sh3 = (seed & 0x10) != 0 ? sh1 : sh2;
        s1 >>= sh1; s2 >>= sh2; s3 >>= sh1; s4 >>= sh2; s5 >>= sh1; s6 >>= sh2; s7 >>= sh1; s8 >>= sh2;
        s9 >>= sh3; s10 >>= sh3; s11 >>= sh3; s12 >>= sh3;
        var a = (int)((s1 * x + s2 * y + s11 * z + (rnum >> 14)) & 0x3F);
        var b = (int)((s3 * x + s4 * y + s12 * z + (rnum >> 10)) & 0x3F);
        var c = (int)((s5 * x + s6 * y + s9 * z + (rnum >> 6)) & 0x3F);
        var d = (int)((s7 * x + s8 * y + s10 * z + (rnum >> 2)) & 0x3F);
        if (partitionCount < 4) d = 0;
        if (partitionCount < 3) c = 0;
        if (a >= b && a >= c && a >= d) return 0;
        if (b >= c && b >= d) return 1;
        if (c >= d) return 2;
        return 3;
    }
}
