using System.Buffers.Binary;

namespace Glide.Imaging;

/// <summary>
/// OpenEXR DWAA/DWAB decompression (the two differ only in lines per chunk), ported from the reference
/// DwaCompressor. A chunk is an 11 x uint64 size header, optional channel rules (version 2), then four
/// streams: UNKNOWN channels (zlib), AC coefficients (static Huffman or zlib), DC coefficients (zlib +
/// ZIP predictor/interleave) and RLE channels (zlib of EXR-RLE of byte planes). Lossy channels are 8x8
/// blocks of half DCT coefficients (quantisation is implicit), optionally Y'CbCr for an R/G/B triple,
/// stored in a perceptual (nonlinear) encoding. Every size is validated before allocation; malformed
/// chunks return <c>false</c>.
/// </summary>
internal static class ExrDwa
{
    private const int Unknown = 0, LossyDct = 1, Rle = 2;
    private const int StaticHuffman = 0, Deflate = 1;
    private const int HeaderSizes = 11;

    /// <summary>One channel of the chunk: name and pixel type (0 uint, 1 half, 2 float) plus its sample grid.</summary>
    public readonly record struct Channel(string Name, int Type, int Nx, int Ny, int YSampling, bool PLinear)
    {
        public int Size => Type == 1 ? 2 : 4;
    }

    /// <summary>A channel rule: channel-name suffix + pixel type -> scheme (and R/G/B slot for colour conversion).</summary>
    private readonly record struct Rule(string Suffix, int Scheme, int Type, int CscIndex, bool CaseInsensitive)
    {
        public bool Matches(string suffix, int type) =>
            type == Type && string.Equals(suffix, Suffix, CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Rules implied by version-1 chunks (written before rules were stored in the stream).</summary>
    private static readonly Rule[] LegacyRules = BuildLegacyRules();

    private static Rule[] BuildLegacyRules()
    {
        var rules = new List<Rule>();
        foreach (var type in new[] { 1, 2 })
        {
            foreach (var (suffix, csc) in new[] { ("r", 0), ("red", 0), ("g", 1), ("grn", 1), ("green", 1), ("b", 2), ("blu", 2), ("blue", 2), ("y", -1), ("by", -1), ("ry", -1) })
                rules.Add(new Rule(suffix, LossyDct, type, csc, true));
        }
        foreach (var type in new[] { 0, 1, 2 }) rules.Add(new Rule("a", Rle, type, -1, true));
        return rules.ToArray();
    }

    /// <param name="channels">Channels in file (alphabetical) order.</param>
    /// <param name="y0">Absolute first scanline of the chunk.</param>
    /// <param name="lines">Number of scanlines in the chunk.</param>
    /// <param name="expectedBytes">Size of the scanline/channel-interleaved output.</param>
    public static bool TryDecode(ReadOnlySpan<byte> source, Channel[] channels, int y0, int lines, int expectedBytes, out byte[] output)
    {
        output = Array.Empty<byte>();
        try
        {
            return Decode(source, channels, y0, lines, expectedBytes, out output);
        }
        catch
        {
            output = Array.Empty<byte>();
            return false;
        }
    }

    private static bool Decode(ReadOnlySpan<byte> source, Channel[] channels, int y0, int lines, int expectedBytes, out byte[] output)
    {
        output = Array.Empty<byte>();
        if (source.Length < HeaderSizes * 8) return false;
        Span<long> sizes = stackalloc long[HeaderSizes];
        for (var i = 0; i < HeaderSizes; i++)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(source[(i * 8)..]);
            if (value > int.MaxValue) return false;
            sizes[i] = (long)value;
        }
        long version = sizes[0], unknownUncompressed = sizes[1], unknownCompressed = sizes[2], acCompressed = sizes[3],
            dcCompressed = sizes[4], rleCompressed = sizes[5], rleUncompressed = sizes[6], rleRaw = sizes[7],
            acCount = sizes[8], dcCount = sizes[9], acCompression = sizes[10];
        var position = HeaderSizes * 8;

        // Channel rules.
        Rule[] rules;
        if (version == 1)
        {
            rules = LegacyRules;
        }
        else if (version == 2)
        {
            if (position + 2 > source.Length) return false;
            int ruleSize = BinaryPrimitives.ReadUInt16LittleEndian(source[position..]);
            if (ruleSize < 2 || position + ruleSize > source.Length) return false;
            if (!ParseRules(source.Slice(position + 2, ruleSize - 2), out rules)) return false;
            position += ruleSize;
        }
        else
        {
            return false;
        }

        // Classify channels: the last matching rule wins; R/G/B slots of one layer form a colour set.
        var schemes = new int[channels.Length];
        var cscSlots = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        for (var c = 0; c < channels.Length; c++)
        {
            var name = channels[c].Name;
            var dot = name.LastIndexOf('.');
            var suffix = dot >= 0 ? name[(dot + 1)..] : name;
            var prefix = dot >= 0 ? name[..(dot + 1)] : "";
            schemes[c] = Unknown;
            foreach (var rule in rules)
            {
                if (!rule.Matches(suffix, channels[c].Type)) continue;
                schemes[c] = rule.Scheme;
                if (rule.CscIndex >= 0)
                {
                    if (!cscSlots.TryGetValue(prefix, out var slots)) cscSlots[prefix] = slots = new[] { -1, -1, -1 };
                    slots[rule.CscIndex] = c;
                }
            }
            if (schemes[c] == LossyDct && channels[c].Type == 0) return false; // the DCT path carries half/float only
        }
        var cscSets = new List<int[]>();
        var inCsc = new bool[channels.Length];
        foreach (var slots in cscSlots.Values)
        {
            if (slots[0] < 0 || slots[1] < 0 || slots[2] < 0) continue;
            if (slots.Any(s => schemes[s] != LossyDct)) continue;
            var first = channels[slots[0]];
            if (slots.Any(s => channels[s].Nx != first.Nx || channels[s].Ny != first.Ny)) return false;
            cscSets.Add(slots);
            foreach (var s in slots) inCsc[s] = true;
        }

        // Expected stream sizes, from the channel grid alone (before trusting anything in the header).
        long unknownBytes = 0, rleBytes = 0, blocks = 0;
        for (var c = 0; c < channels.Length; c++)
        {
            var ch = channels[c];
            long samples = (long)ch.Nx * ch.Ny;
            switch (schemes[c])
            {
                case Unknown: unknownBytes += samples * ch.Size; break;
                case Rle: rleBytes += samples * ch.Size; break;
                default: blocks += ((ch.Nx + 7L) / 8) * ((ch.Ny + 7L) / 8); break;
            }
        }
        if (unknownUncompressed != unknownBytes || rleRaw != rleBytes || dcCount != blocks) return false;
        if (acCount > blocks * 63) return false; // at most 63 AC values per block
        if (rleUncompressed > rleRaw * 2 + 16) return false; // EXR-RLE never more than doubles its input
        if ((unknownBytes > 0) != (unknownCompressed > 0) || (rleBytes > 0) != (rleCompressed > 0)) return false;
        if ((blocks > 0) != (dcCompressed > 0) || (acCount > 0) != (acCompressed > 0)) return false;
        if (position + unknownCompressed + acCompressed + dcCompressed + rleCompressed > source.Length) return false;

        var unknownStream = source.Slice(position, (int)unknownCompressed); position += (int)unknownCompressed;
        var acStream = source.Slice(position, (int)acCompressed); position += (int)acCompressed;
        var dcStream = source.Slice(position, (int)dcCompressed); position += (int)dcCompressed;
        var rleStream = source.Slice(position, (int)rleCompressed);

        // UNKNOWN: plain zlib of each channel's samples, channel after channel.
        var unknown = Array.Empty<byte>();
        if (unknownBytes > 0 && !ManagedCodecSupport.TryInflateExact(unknownStream, (int)unknownBytes, ManagedCodecSupport.Wrapper.Zlib, out unknown)) return false;

        // RLE: zlib, then EXR run-length, giving per channel its byte planes (low byte plane first).
        var rle = Array.Empty<byte>();
        if (rleBytes > 0)
        {
            if (rleUncompressed <= 0 || !ManagedCodecSupport.TryInflateExact(rleStream, (int)rleUncompressed, ManagedCodecSupport.Wrapper.Zlib, out var packedRle)) return false;
            if (!ExrDecoder.RleDecode(packedRle, (int)rleBytes, out rle)) return false;
        }

        // AC: one shared stream of run-length-coded coefficients, block by block, component by component.
        var ac = new ushort[acCount];
        if (acCount > 0)
        {
            if (acCompression == StaticHuffman)
            {
                if (!ExrPiz.HufUncompress(acStream, ac)) return false;
            }
            else if (acCompression == Deflate)
            {
                if (!ManagedCodecSupport.TryInflateExact(acStream, (int)acCount * 2, ManagedCodecSupport.Wrapper.Zlib, out var acBytes)) return false;
                for (var i = 0; i < ac.Length; i++) ac[i] = BinaryPrimitives.ReadUInt16LittleEndian(acBytes.AsSpan(i * 2));
            }
            else
            {
                return false;
            }
        }

        // DC: zlib + the ZIP predictor/interleave; per decoder, all blocks of component 0, then 1, ...
        var dc = Array.Empty<ushort>();
        if (blocks > 0)
        {
            if (!ManagedCodecSupport.TryInflateExact(dcStream, (int)blocks * 2, ManagedCodecSupport.Wrapper.Zlib, out var dcBytes)) return false;
            dcBytes = ExrDecoder.Reconstruct(dcBytes);
            dc = new ushort[blocks];
            for (var i = 0; i < dc.Length; i++) dc[i] = BinaryPrimitives.ReadUInt16LittleEndian(dcBytes.AsSpan(i * 2));
        }

        // Lossy channels decode into planar half planes: colour sets first, then the remaining channels in order.
        var planes = new ushort[channels.Length][];
        int acPosition = 0, dcPosition = 0;
        foreach (var set in cscSets)
        {
            if (!DecodeLossy(channels, set, planes, ac, ref acPosition, dc, ref dcPosition)) return false;
        }
        for (var c = 0; c < channels.Length; c++)
        {
            if (schemes[c] != LossyDct || inCsc[c]) continue;
            if (!DecodeLossy(channels, new[] { c }, planes, ac, ref acPosition, dc, ref dcPosition)) return false;
        }
        if (acPosition != ac.Length || dcPosition != dc.Length) return false;

        // Planar offsets of each UNKNOWN/RLE channel inside its stream.
        var offsets = new int[channels.Length];
        int unknownOffset = 0, rleOffset = 0;
        for (var c = 0; c < channels.Length; c++)
        {
            var bytes = channels[c].Nx * channels[c].Ny * channels[c].Size;
            if (schemes[c] == Unknown) { offsets[c] = unknownOffset; unknownOffset += bytes; }
            else if (schemes[c] == Rle) { offsets[c] = rleOffset; rleOffset += bytes; }
        }

        // Interleave into the plain layout: for each scanline, each sampled channel's samples (little-endian).
        output = new byte[expectedBytes];
        var rows = new int[channels.Length];
        var outPosition = 0;
        for (var y = y0; y < y0 + lines; y++)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                var ch = channels[c];
                if (ExrDecoder.Mod(y, ch.YSampling) != 0) continue;
                var row = rows[c]++;
                var bytes = ch.Nx * ch.Size;
                if (row >= ch.Ny || outPosition + bytes > output.Length) return false;
                var target = output.AsSpan(outPosition, bytes);
                switch (schemes[c])
                {
                    case Unknown:
                        unknown.AsSpan(offsets[c] + row * bytes, bytes).CopyTo(target);
                        break;
                    case Rle:
                    {
                        var planeSize = ch.Nx * ch.Ny;
                        var start = offsets[c] + row * ch.Nx;
                        for (var x = 0; x < ch.Nx; x++)
                            for (var b = 0; b < ch.Size; b++)
                                target[x * ch.Size + b] = rle[start + b * planeSize + x];
                        break;
                    }
                    default:
                    {
                        var plane = planes[c].AsSpan(row * ch.Nx, ch.Nx);
                        if (ch.Type == 1)
                        {
                            for (var x = 0; x < plane.Length; x++) BinaryPrimitives.WriteUInt16LittleEndian(target[(x * 2)..], plane[x]);
                        }
                        else
                        {
                            for (var x = 0; x < plane.Length; x++) BinaryPrimitives.WriteSingleLittleEndian(target[(x * 4)..], (float)BitConverter.UInt16BitsToHalf(plane[x]));
                        }
                        break;
                    }
                }
                outPosition += bytes;
            }
        }
        return outPosition == expectedBytes;
    }

    /// <summary>Version-2 rules: per rule a NUL-terminated suffix, a packed byte (csc+1)&lt;&lt;4 | scheme&lt;&lt;2 | caseInsensitive, and the pixel type.</summary>
    private static bool ParseRules(ReadOnlySpan<byte> data, out Rule[] rules)
    {
        var list = new List<Rule>();
        rules = Array.Empty<Rule>();
        var position = 0;
        while (position < data.Length)
        {
            var end = data[position..].IndexOf((byte)0);
            if (end < 0 || end > 128 || position + end + 3 > data.Length) return false;
            var suffix = System.Text.Encoding.ASCII.GetString(data.Slice(position, end));
            position += end + 1;
            int packed = data[position++];
            int type = data[position++];
            var csc = (packed >> 4) - 1;
            var scheme = (packed >> 2) & 3;
            if (csc > 2 || scheme > Rle || type > 2) return false;
            list.Add(new Rule(suffix, scheme, type, csc, (packed & 1) != 0));
        }
        rules = list.ToArray();
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Lossy DCT
    // ---------------------------------------------------------------------------------------------

    /// <summary>Natural (row-major) index of each zig-zag position.</summary>
    private static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    /// <summary>
    /// Decodes one lossy decoder's components (1, or 3 for a Y'CbCr colour set) into half planes. DC values
    /// are stored component-major; AC values are consumed block by block, component by component.
    /// </summary>
    private static bool DecodeLossy(Channel[] channels, int[] components, ushort[][] planes, ushort[] ac, ref int acPosition, ushort[] dc, ref int dcPosition)
    {
        var first = channels[components[0]];
        int width = first.Nx, height = first.Ny;
        var blocksX = (width + 7) / 8;
        var blocksY = (height + 7) / 8;
        var count = blocksX * blocksY;
        var n = components.Length;
        if ((long)dcPosition + (long)count * n > dc.Length) return false;
        var toLinear = new ushort[n][];
        for (var k = 0; k < n; k++)
        {
            planes[components[k]] = new ushort[width * height];
            toLinear[k] = channels[components[k]].PLinear ? null! : ToLinearTable();
        }
        var blocksData = new float[n][];
        for (var k = 0; k < n; k++) blocksData[k] = new float[64];
        Span<ushort> zig = stackalloc ushort[64];

        var block = 0;
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++, block++)
            {
                for (var k = 0; k < n; k++)
                {
                    zig.Clear();
                    zig[0] = dc[dcPosition + k * count + block];
                    // Undo the AC run-length: 0xff00 ends the block, 0xffNN skips NN zeros.
                    var lastNonZero = 0;
                    var index = 1;
                    while (index < 64)
                    {
                        if (acPosition >= ac.Length) return false;
                        var value = ac[acPosition++];
                        if (value == 0xff00) index = 64;
                        else if (value >> 8 == 0xff) index += value & 0xff;
                        else { lastNonZero = index; zig[index++] = value; }
                    }
                    var data = blocksData[k];
                    if (lastNonZero == 0)
                    {
                        // DC only: the inverse DCT is a constant (orthonormal scale 1/8).
                        Array.Fill(data, (float)BitConverter.UInt16BitsToHalf(zig[0]) / 8f);
                    }
                    else
                    {
                        for (var i = 0; i < 64; i++) data[ZigZag[i]] = (float)BitConverter.UInt16BitsToHalf(zig[i]);
                        InverseDct8x8(data);
                    }
                }
                if (n == 3)
                {
                    // Rec.709 Y'CbCr -> R'G'B'.
                    float[] y = blocksData[0], cb = blocksData[1], cr = blocksData[2];
                    for (var i = 0; i < 64; i++)
                    {
                        float l = y[i], u = cb[i], v = cr[i];
                        y[i] = l + 1.5747f * v;
                        cb[i] = l - 0.1873f * u - 0.4682f * v;
                        cr[i] = l + 1.8556f * u;
                    }
                }
                // Crop the (edge-padded) block into the planes, rounding to half and undoing the nonlinearity.
                var maxY = Math.Min(8, height - by * 8);
                var maxX = Math.Min(8, width - bx * 8);
                for (var k = 0; k < n; k++)
                {
                    var plane = planes[components[k]];
                    var data = blocksData[k];
                    var lut = toLinear[k];
                    for (var yy = 0; yy < maxY; yy++)
                    {
                        var rowStart = (by * 8 + yy) * width + bx * 8;
                        for (var xx = 0; xx < maxX; xx++)
                        {
                            var bits = BitConverter.HalfToUInt16Bits((Half)data[yy * 8 + xx]);
                            plane[rowStart + xx] = lut is null ? bits : lut[bits];
                        }
                    }
                }
            }
        }
        dcPosition += count * n;
        return true;
    }

    /// <summary>Separable orthonormal 8x8 inverse DCT, the reference's butterfly (rows, then columns).</summary>
    private static void InverseDct8x8(float[] data)
    {
        const float pi = 3.14159f;
        float a = .5f * MathF.Cos(pi / 4f), b = .5f * MathF.Cos(pi / 16f), c = .5f * MathF.Cos(pi / 8f), d = .5f * MathF.Cos(3f * pi / 16f);
        float e = .5f * MathF.Cos(5f * pi / 16f), f = .5f * MathF.Cos(3f * pi / 8f), g = .5f * MathF.Cos(7f * pi / 16f);
        for (var pass = 0; pass < 2; pass++)
        {
            var step = pass == 0 ? 1 : 8; // element stride within a row/column
            var next = pass == 0 ? 8 : 1; // stride between rows/columns
            for (var line = 0; line < 8; line++)
            {
                var o = line * next;
                float x0 = data[o], x1 = data[o + step], x2 = data[o + 2 * step], x3 = data[o + 3 * step];
                float x4 = data[o + 4 * step], x5 = data[o + 5 * step], x6 = data[o + 6 * step], x7 = data[o + 7 * step];
                float alpha0 = c * x2, alpha1 = f * x2, alpha2 = c * x6, alpha3 = f * x6;
                var beta0 = b * x1 + d * x3 + e * x5 + g * x7;
                var beta1 = d * x1 - g * x3 - b * x5 - e * x7;
                var beta2 = e * x1 - b * x3 + g * x5 + d * x7;
                var beta3 = g * x1 - e * x3 + d * x5 - b * x7;
                var theta0 = a * (x0 + x4);
                var theta3 = a * (x0 - x4);
                var theta1 = alpha0 + alpha3;
                var theta2 = alpha1 - alpha2;
                float gamma0 = theta0 + theta1, gamma1 = theta3 + theta2, gamma2 = theta3 - theta2, gamma3 = theta0 - theta1;
                data[o] = gamma0 + beta0;
                data[o + step] = gamma1 + beta1;
                data[o + 2 * step] = gamma2 + beta2;
                data[o + 3 * step] = gamma3 + beta3;
                data[o + 4 * step] = gamma3 - beta3;
                data[o + 5 * step] = gamma2 - beta2;
                data[o + 6 * step] = gamma1 - beta1;
                data[o + 7 * step] = gamma0 - beta0;
            }
        }
    }

    private static ushort[]? _toLinear;

    /// <summary>
    /// Inverse of the reference's perceptual encoding toNonlinear(x) = x^(1/2.2) for |x| &lt;= 1 and
    /// ln|x|/2.2 + 1 above (sign preserved): toLinear(v) = v^2.2, or e^(2.2(v-1)) above 1. Inf/NaN map to 0.
    /// </summary>
    internal static ushort[] ToLinearTable()
    {
        var table = _toLinear;
        if (table is not null) return table;
        table = new ushort[65536];
        for (var i = 0; i < table.Length; i++)
        {
            var h = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            if (float.IsNaN(h) || float.IsInfinity(h)) continue;
            var magnitude = MathF.Abs(h);
            var linear = magnitude <= 1f ? MathF.Pow(magnitude, 2.2f) : MathF.Exp(2.2f * (magnitude - 1f));
            table[i] = BitConverter.HalfToUInt16Bits((Half)(h < 0 ? -linear : linear));
        }
        _toLinear = table;
        return table;
    }
}
