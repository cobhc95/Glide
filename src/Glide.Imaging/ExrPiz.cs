using System.Buffers.Binary;

namespace Glide.Imaging;

/// <summary>
/// OpenEXR PIZ decompression (bitmap + reverse LUT, canonical Huffman with run-length symbol, and the
/// 14/16-bit Haar wavelet), ported from the reference algorithm. Every read is bounds-checked;
/// malformed streams return <c>false</c>.
/// </summary>
internal static class ExrPiz
{
    private const int UShortRange = 1 << 16;
    private const int BitmapSize = UShortRange >> 3;
    private const int HufEncBits = 16;
    private const int HufDecBits = 14;
    private const int HufEncSize = (1 << HufEncBits) + 1;
    private const int HufDecSize = 1 << HufDecBits;
    private const int HufDecMask = HufDecSize - 1;
    private const int ShortZeroCodeRun = 59;
    private const int LongZeroCodeRun = 63;
    private const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;

    /// <param name="channels">Per channel: (bytes per sample, samples per line, sampled lines, y sampling).</param>
    /// <param name="y0">Absolute first scanline of the chunk.</param>
    /// <param name="lines">Number of scanlines in the chunk.</param>
    public static bool TryDecode(ReadOnlySpan<byte> source, (int Size, int Nx, int Ny, int YSampling)[] channels, int y0, int lines,
        int expectedBytes, out byte[] output)
    {
        output = Array.Empty<byte>();
        try
        {
            if (source.Length < 4 || (expectedBytes & 1) != 0) return false;
            var bitmap = new byte[BitmapSize];
            int minNonZero = BinaryPrimitives.ReadUInt16LittleEndian(source);
            int maxNonZero = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
            var position = 4;
            if (maxNonZero >= BitmapSize) return false;
            if (minNonZero <= maxNonZero)
            {
                var count = maxNonZero - minNonZero + 1;
                if (position + count > source.Length) return false;
                source.Slice(position, count).CopyTo(bitmap.AsSpan(minNonZero));
                position += count;
            }

            var lut = new ushort[UShortRange];
            var maxValue = ReverseLutFromBitmap(bitmap, lut);

            if (position + 4 > source.Length) return false;
            var length = BinaryPrimitives.ReadInt32LittleEndian(source[position..]);
            position += 4;
            if (length < 0 || position + length > source.Length) return false;

            var values = new ushort[expectedBytes / 2];
            if (!HufUncompress(source.Slice(position, length), values)) return false;

            // Wavelet decode each channel's planes.
            var offset = 0;
            foreach (var (size, nx, ny, _) in channels)
            {
                var words = size / 2;
                var count = (long)nx * ny * words;
                if (offset + count > values.Length) return false;
                for (var j = 0; j < words; j++)
                    Wav2Decode(values, offset + j, nx, words, ny, nx * words, (ushort)maxValue);
                offset += (int)count;
            }
            if (offset != values.Length) return false;

            for (var i = 0; i < values.Length; i++) values[i] = lut[values[i]];

            // Rearrange from per-channel planes into per-line interleaved channel rows.
            output = new byte[expectedBytes];
            var cursors = new int[channels.Length];
            var starts = new int[channels.Length];
            var acc = 0;
            for (var c = 0; c < channels.Length; c++)
            {
                starts[c] = acc;
                acc += channels[c].Nx * channels[c].Ny * (channels[c].Size / 2);
            }
            var outPosition = 0;
            for (var y = y0; y < y0 + lines; y++)
            {
                for (var c = 0; c < channels.Length; c++)
                {
                    var m = y % channels[c].YSampling;
                    if (m != 0 && m + channels[c].YSampling != 0) continue;
                    var n = channels[c].Nx * (channels[c].Size / 2);
                    var from = starts[c] + cursors[c];
                    if (from + n > values.Length || outPosition + n * 2 > output.Length) return false;
                    for (var k = 0; k < n; k++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPosition), values[from + k]);
                        outPosition += 2;
                    }
                    cursors[c] += n;
                }
            }
            return outPosition == expectedBytes;
        }
        catch
        {
            output = Array.Empty<byte>();
            return false;
        }
    }

    private static int ReverseLutFromBitmap(byte[] bitmap, ushort[] lut)
    {
        var k = 0;
        for (var i = 0; i < UShortRange; i++)
            if (i == 0 || (bitmap[i >> 3] & (1 << (i & 7))) != 0) lut[k++] = (ushort)i;
        var n = k - 1;
        while (k < UShortRange) lut[k++] = 0;
        return n;
    }

    // ---------------------------------------------------------------------------------------------
    // Huffman
    // ---------------------------------------------------------------------------------------------

    private struct DecEntry
    {
        public int Len;
        public int Lit;
        public int[]? P;
    }

    internal static bool HufUncompress(ReadOnlySpan<byte> compressed, ushort[] raw)
    {
        if (raw.Length == 0) return true;
        if (compressed.Length < 20) return false;
        var im = BinaryPrimitives.ReadInt32LittleEndian(compressed);
        var iM = BinaryPrimitives.ReadInt32LittleEndian(compressed[4..]);
        var nBits = BinaryPrimitives.ReadInt32LittleEndian(compressed[12..]);
        if (im < 0 || im >= HufEncSize || iM < 0 || iM >= HufEncSize || im > iM || nBits < 0) return false;

        var codes = new long[HufEncSize];
        var position = 20;
        if (!UnpackEncTable(compressed, ref position, im, iM, codes)) return false;
        if ((long)position + ((long)nBits + 7) / 8 > compressed.Length) return false;

        var table = new DecEntry[HufDecSize];
        if (!BuildDecTable(codes, im, iM, table)) return false;
        return Decode(codes, table, compressed[position..], nBits, iM, raw);
    }

    private static bool GetBits(int count, ref long c, ref int lc, ReadOnlySpan<byte> data, ref int position, out int value)
    {
        value = 0;
        while (lc < count)
        {
            if (position >= data.Length) return false;
            c = (c << 8) | data[position++];
            lc += 8;
        }
        lc -= count;
        value = (int)((c >> lc) & ((1L << count) - 1));
        return true;
    }

    private static bool UnpackEncTable(ReadOnlySpan<byte> data, ref int position, int im, int iM, long[] codes)
    {
        long c = 0;
        var lc = 0;
        for (; im <= iM; im++)
        {
            if (!GetBits(6, ref c, ref lc, data, ref position, out var l)) return false;
            codes[im] = l;
            if (l == LongZeroCodeRun)
            {
                if (!GetBits(8, ref c, ref lc, data, ref position, out var run)) return false;
                var zerun = run + ShortestLongRun;
                if (im + zerun > iM + 1) return false;
                while (zerun-- > 0) codes[im++] = 0;
                im--;
            }
            else if (l >= ShortZeroCodeRun)
            {
                var zerun = l - ShortZeroCodeRun + 2;
                if (im + zerun > iM + 1) return false;
                while (zerun-- > 0) codes[im++] = 0;
                im--;
            }
        }
        CanonicalCodeTable(codes);
        return true;
    }

    private static void CanonicalCodeTable(long[] codes)
    {
        var n = new long[59];
        for (var i = 0; i < HufEncSize; i++) n[codes[i]]++;
        long c = 0;
        for (var i = 58; i > 0; i--)
        {
            var nc = (c + n[i]) >> 1;
            n[i] = c;
            c = nc;
        }
        for (var i = 0; i < HufEncSize; i++)
        {
            var l = (int)codes[i];
            if (l > 0) codes[i] = l | (n[l]++ << 6);
        }
    }

    private static bool BuildDecTable(long[] codes, int im, int iM, DecEntry[] table)
    {
        for (; im <= iM; im++)
        {
            var c = codes[im] >> 6;
            var l = (int)(codes[im] & 63);
            if (l > 0 && (c >> l) != 0) return false;
            if (l > HufDecBits)
            {
                ref var entry = ref table[(int)(c >> (l - HufDecBits)) & HufDecMask];
                if (entry.Len != 0) return false;
                entry.Lit++;
                var p = new int[entry.Lit];
                if (entry.P is not null) Array.Copy(entry.P, p, entry.P.Length);
                p[entry.Lit - 1] = im;
                entry.P = p;
            }
            else if (l > 0)
            {
                var start = (int)(c << (HufDecBits - l));
                var count = 1 << (HufDecBits - l);
                if (start + count > HufDecSize) return false;
                for (var i = 0; i < count; i++)
                {
                    ref var entry = ref table[start + i];
                    if (entry.Len != 0 || entry.P is not null) return false;
                    entry.Len = l;
                    entry.Lit = im;
                }
            }
        }
        return true;
    }

    private static bool GetCode(int po, int rlc, ref long c, ref int lc, ReadOnlySpan<byte> data, ref int position, ushort[] output, ref int outPosition)
    {
        if (po == rlc)
        {
            if (lc < 8)
            {
                if (position >= data.Length) return false;
                c = (c << 8) | data[position++];
                lc += 8;
            }
            lc -= 8;
            var cs = (int)((c >> lc) & 0xFF);
            if (outPosition + cs > output.Length || outPosition == 0) return false;
            var s = output[outPosition - 1];
            while (cs-- > 0) output[outPosition++] = s;
        }
        else if (outPosition < output.Length)
        {
            output[outPosition++] = (ushort)po;
        }
        else
        {
            return false;
        }
        return true;
    }

    private static bool Decode(long[] codes, DecEntry[] table, ReadOnlySpan<byte> data, int nBits, int rlc, ushort[] output)
    {
        long c = 0;
        var lc = 0;
        var outPosition = 0;
        var position = 0;
        var end = (int)(((long)nBits + 7) / 8);
        while (position < end)
        {
            c = (c << 8) | data[position++];
            lc += 8;
            while (lc >= HufDecBits)
            {
                var entry = table[(int)((c >> (lc - HufDecBits)) & HufDecMask)];
                if (entry.Len != 0)
                {
                    lc -= entry.Len;
                    if (!GetCode(entry.Lit, rlc, ref c, ref lc, data[..end], ref position, output, ref outPosition)) return false;
                }
                else
                {
                    if (entry.P is null) return false;
                    int j;
                    for (j = 0; j < entry.Lit; j++)
                    {
                        var l = (int)(codes[entry.P[j]] & 63);
                        while (lc < l && position < end)
                        {
                            c = (c << 8) | data[position++];
                            lc += 8;
                        }
                        if (lc >= l && (codes[entry.P[j]] >> 6) == ((c >> (lc - l)) & ((1L << l) - 1)))
                        {
                            lc -= l;
                            if (!GetCode(entry.P[j], rlc, ref c, ref lc, data[..end], ref position, output, ref outPosition)) return false;
                            break;
                        }
                    }
                    if (j == entry.Lit) return false;
                }
            }
        }

        var i = (8 - nBits) & 7;
        c >>= i;
        lc -= i;
        while (lc > 0)
        {
            var entry = table[(int)((c << (HufDecBits - lc)) & HufDecMask)];
            if (entry.Len == 0 || entry.Len > lc) return false;
            lc -= entry.Len;
            if (!GetCode(entry.Lit, rlc, ref c, ref lc, data[..end], ref position, output, ref outPosition)) return false;
        }
        return outPosition == output.Length;
    }

    // ---------------------------------------------------------------------------------------------
    // Wavelet
    // ---------------------------------------------------------------------------------------------

    private static void Wdec14(ushort l, ushort h, out ushort a, out ushort b)
    {
        var ls = (short)l;
        var hs = (short)h;
        int hi = hs;
        var ai = ls + (hi & 1) + (hi >> 1);
        a = (ushort)(short)ai;
        b = (ushort)(short)(ai - hi);
    }

    private static void Wdec16(ushort l, ushort h, out ushort a, out ushort b)
    {
        int m = l;
        int d = h;
        var bb = (m - (d >> 1)) & 0xFFFF;
        var aa = (d + bb - 0x8000) & 0xFFFF;
        b = (ushort)bb;
        a = (ushort)aa;
    }

    private static void Wav2Decode(ushort[] v, int start, int nx, int ox, int ny, int oy, ushort mx)
    {
        var w14 = mx < (1 << 14);
        var n = nx > ny ? ny : nx;
        var p = 1;
        while (p <= n) p <<= 1;
        p >>= 1;
        var p2 = p;
        p >>= 1;
        while (p >= 1)
        {
            var py = start;
            var ey = start + oy * (ny - p2);
            var oy1 = oy * p;
            var oy2 = oy * p2;
            var ox1 = ox * p;
            var ox2 = ox * p2;
            ushort i00, i01, i10, i11;
            for (; py <= ey; py += oy2)
            {
                var px = py;
                var ex = py + ox * (nx - p2);
                for (; px <= ex; px += ox2)
                {
                    var p01 = px + ox1;
                    var p10 = px + oy1;
                    var p11 = p10 + ox1;
                    if (w14)
                    {
                        Wdec14(v[px], v[p10], out i00, out i10);
                        Wdec14(v[p01], v[p11], out i01, out i11);
                        Wdec14(i00, i01, out v[px], out v[p01]);
                        Wdec14(i10, i11, out v[p10], out v[p11]);
                    }
                    else
                    {
                        Wdec16(v[px], v[p10], out i00, out i10);
                        Wdec16(v[p01], v[p11], out i01, out i11);
                        Wdec16(i00, i01, out v[px], out v[p01]);
                        Wdec16(i10, i11, out v[p10], out v[p11]);
                    }
                }
                if ((nx & p) != 0)
                {
                    var p10 = px + oy1;
                    if (w14) Wdec14(v[px], v[p10], out i00, out v[p10]);
                    else Wdec16(v[px], v[p10], out i00, out v[p10]);
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
                    if (w14) Wdec14(v[px], v[p01], out i00, out v[p01]);
                    else Wdec16(v[px], v[p01], out i00, out v[p01]);
                    v[px] = i00;
                }
            }
            p2 = p;
            p >>= 1;
        }
    }
}
