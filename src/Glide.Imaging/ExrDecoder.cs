using System.Buffers.Binary;
using System.Text;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

/// <summary>
/// Managed OpenEXR reader for single-part scanline and tiled images (level 0 of mip/rip-mapped
/// files). Compressions: NONE, RLE, ZIPS, ZIP, PIZ, PXR24 and B44/B44A; channel types HALF, FLOAT and UINT.
/// Channels R/G/B/A (or a layer *.R/*.G/*.B/*.A, luminance/chroma Y+RY+BY, luminance Y, or the first
/// channel as grey) are tone-mapped to 8-bit sRGB by clamping scene-linear values to [0,1]; premultiplied
/// alpha is undone and an alpha channel that is zero everywhere is ignored.
/// DWAA/DWAB, deep and multi-part files are reported as undecodable.
/// </summary>
internal static class ExrDecoder
{
    private const uint Magic = 20000630;

    private enum Compression { None = 0, Rle = 1, Zips = 2, Zip = 3, Piz = 4, Pxr24 = 5, B44 = 6, B44A = 7 }

    private sealed class Channel
    {
        public required string Name;
        public int Type; // 0 uint, 1 half, 2 float
        public int XSampling = 1, YSampling = 1;
        public bool PLinear;
        public int PlaneWidth, PlaneHeight; // samples stored for this channel over the whole data window
        public int Size => Type == 1 ? 2 : 4;
    }

    private sealed class Header
    {
        public readonly List<Channel> Channels = new();
        public int Compression = -1;
        public int XMin, YMin, XMax, YMax;
        public bool Tiled;
        public int TileWidth, TileHeight, LevelMode;
        public int HeaderEnd;
        public float[]? Chromaticities; // red xy, green xy, blue xy, white xy
        public int Width => XMax - XMin + 1;
        public int Height => YMax - YMin + 1;
    }

    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryParseHeader(data, out var header)) return false;
        dimensions = new ImageDimensions(header.Width, header.Height);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        try
        {
            if (!TryParseHeader(data, out var header)) return false;
            if (header.Compression is < 0 or > 7) return false;
            if (!ManagedCodecSupport.FitsMemory(header.Width, header.Height)) return false;

            // Channel selection.
            var sorted = header.Channels; // already alphabetical in the file
            int Find(string name)
            {
                var exact = sorted.FindIndex(c => c.Name == name && c.XSampling == 1 && c.YSampling == 1);
                if (exact >= 0) return exact;
                return sorted.FindIndex(c => c.Name.EndsWith("." + name, StringComparison.Ordinal) && c.XSampling == 1 && c.YSampling == 1);
            }
            int r = Find("R"), g = Find("G"), b = Find("B"), a = Find("A");
            int ry = -1, by = -1, yc = -1; // luminance/chroma (YC) files: Y full size, RY/BY subsampled
            if (r < 0 && g < 0 && b < 0)
            {
                var y = Find("Y");
                ry = sorted.FindIndex(c => c.Name == "RY");
                by = sorted.FindIndex(c => c.Name == "BY");
                if (y >= 0 && ry >= 0 && by >= 0) yc = y;
                else
                {
                    ry = by = -1;
                    if (y < 0) y = sorted.FindIndex(c => c.XSampling == 1 && c.YSampling == 1);
                    if (y < 0) return false;
                    r = g = b = y;
                }
            }

            if (yc < 0)
            {
                // A lone colour channel (for example only G) is a single-channel image: show it as grey.
                var found = (r >= 0 ? 1 : 0) + (g >= 0 ? 1 : 0) + (b >= 0 ? 1 : 0);
                if (found == 1) r = g = b = r >= 0 ? r : g >= 0 ? g : b;
            }

            int width = header.Width, height = header.Height;
            var wanted = new HashSet<int>();
            if (yc >= 0) { wanted.Add(yc); wanted.Add(ry); wanted.Add(by); }
            else { wanted.Add(r); wanted.Add(g); wanted.Add(b); }
            if (a >= 0) wanted.Add(a);
            // Security: validate the chunk table and that the stored data can plausibly expand to the
            // declared image before allocating float planes (up to four of them) and the output frame.
            if (!ChunksArePlausible(data, header)) return false;
            var planeBytes = (long)width * height * 4;
            if (!DecodeMemoryGuard.FitsAvailableMemory(planeBytes * wanted.Count + planeBytes * 2, DecodeMemoryGuard.GetSnapshot())) return false;
            var planes = new float[header.Channels.Count][];
            foreach (var index in wanted)
                if (index >= 0) planes[index] = new float[(long)header.Channels[index].PlaneWidth * header.Channels[index].PlaneHeight];

            var ok = header.Tiled ? DecodeTiles(data, header, planes) : DecodeScanlines(data, header, planes);
            if (!ok) return false;

            if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
            // Pixels are independent: convert row bands in parallel (identical to the serial loop).
            float[]? rp = r >= 0 ? planes[r] : null, gp = g >= 0 ? planes[g] : null, bp = b >= 0 ? planes[b] : null, ap = a >= 0 ? planes[a] : null;
            // An alpha channel that is zero everywhere carries no coverage information: show the colour.
            if (ap is not null && !HasNonZero(ap)) ap = null;
            if (yc >= 0)
            {
                var (wr, wg, wb) = LuminanceWeights(header.Chromaticities);
                var yp = planes[yc];
                var ryp = planes[ry];
                var byp = planes[by];
                Channel chromaR = header.Channels[ry], chromaB = header.Channels[by];
                var firstX = header.XMin;
                var firstY = header.YMin;
                var alphaPlane = ap;
                ManagedCodecSupport.ForBands(height, width, (start, end) =>
                {
                    for (var yy = start; yy < end; yy++)
                    {
                        for (var xx = 0; xx < width; xx++)
                        {
                            var i = (long)yy * width + xx;
                            var luma = yp[i];
                            var rel = ChromaAt(ryp, chromaR, xx, yy, firstX, firstY);
                            var bel = ChromaAt(byp, chromaB, xx, yy, firstX, firstY);
                            // Y/RY/BY -> RGB (OpenEXR RgbaYca): R = (RY+1)Y, B = (BY+1)Y, G from the luminance weights.
                            var rv = (rel + 1f) * luma;
                            var bv = (bel + 1f) * luma;
                            var gv = (luma - rv * wr - bv * wb) / wg;
                            var av = alphaPlane is not null ? alphaPlane[i] : 1f;
                            ManagedCodecSupport.WriteLinearPixel(output, i, rv, gv, bv, av, premultiplied: alphaPlane is not null);
                        }
                    }
                });
            }
            else
            {
                ManagedCodecSupport.ForBands(height, width, (start, end) =>
                {
                    for (var i = (long)start * width; i < (long)end * width; i++)
                    {
                        var rv = rp is not null ? rp[i] : 0f;
                        var gv = gp is not null ? gp[i] : 0f;
                        var bv = bp is not null ? bp[i] : 0f;
                        var av = ap is not null ? ap[i] : 1f;
                        ManagedCodecSupport.WriteLinearPixel(output, i, rv, gv, bv, av, premultiplied: ap is not null);
                    }
                });
            }
            image = new DecodedImage(width, height, output);
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Header
    // ---------------------------------------------------------------------------------------------

    private static bool TryParseHeader(ReadOnlySpan<byte> data, out Header header)
    {
        header = new Header();
        try
        {
            if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic) return false;
            var version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            if ((version & 0xFF) != 2) return false;
            if ((version & 0x800) != 0 || (version & 0x1000) != 0) return false; // deep / multi-part
            header.Tiled = (version & 0x200) != 0;
            var position = 8;
            var sawWindow = false;
            for (var n = 0; n < 1024; n++)
            {
                if (!ReadCString(data, ref position, out var name)) return false;
                if (name.Length == 0) break;
                if (!ReadCString(data, ref position, out var type)) return false;
                if (position + 4 > data.Length) return false;
                var size = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
                position += 4;
                if (size < 0 || position + size > data.Length) return false;
                var value = data.Slice(position, size);
                position += size;
                switch (name)
                {
                    case "channels" when type == "chlist":
                        if (!ParseChannels(value, header.Channels)) return false;
                        break;
                    case "compression" when size >= 1:
                        header.Compression = value[0];
                        break;
                    case "dataWindow" when size >= 16:
                        header.XMin = BinaryPrimitives.ReadInt32LittleEndian(value);
                        header.YMin = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
                        header.XMax = BinaryPrimitives.ReadInt32LittleEndian(value[8..]);
                        header.YMax = BinaryPrimitives.ReadInt32LittleEndian(value[12..]);
                        sawWindow = true;
                        break;
                    case "tiles" when size >= 9:
                        header.TileWidth = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(value), int.MaxValue);
                        header.TileHeight = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(value[4..]), int.MaxValue);
                        header.LevelMode = value[8] & 0xF;
                        break;
                    case "chromaticities" when size >= 32 && type == "chromaticities":
                        header.Chromaticities = new float[8];
                        for (var k = 0; k < 8; k++) header.Chromaticities[k] = BinaryPrimitives.ReadSingleLittleEndian(value[(k * 4)..]);
                        break;
                    case "type":
                        var partType = Encoding.ASCII.GetString(value.TrimEnd((byte)0));
                        if (partType is "deepscanline" or "deeptile") return false;
                        break;
                }
            }
            header.HeaderEnd = position;
            if (!sawWindow || header.Channels.Count == 0) return false;
            if ((long)header.XMax - header.XMin + 1 <= 0 || (long)header.YMax - header.YMin + 1 <= 0) return false;
            if (!ManagedCodecSupport.IsValidDimensions((long)header.XMax - header.XMin + 1, (long)header.YMax - header.YMin + 1)) return false;
            foreach (var channel in header.Channels)
            {
                channel.PlaneWidth = SampleCount(header.XMin, header.Width, channel.XSampling);
                channel.PlaneHeight = SampleCount(header.YMin, header.Height, channel.YSampling);
            }
            if (header.Tiled && (header.TileWidth <= 0 || header.TileHeight <= 0 || header.TileWidth > 65536 || header.TileHeight > 65536 || header.LevelMode > 2))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadCString(ReadOnlySpan<byte> data, ref int position, out string value)
    {
        value = string.Empty;
        if (position >= data.Length) return false;
        var end = data[position..].IndexOf((byte)0);
        if (end < 0 || end > 255) return false;
        value = Encoding.ASCII.GetString(data.Slice(position, end));
        position += end + 1;
        return true;
    }

    private static bool ParseChannels(ReadOnlySpan<byte> value, List<Channel> channels)
    {
        var position = 0;
        while (position < value.Length && value[position] != 0)
        {
            if (!ReadCString(value, ref position, out var name)) return false;
            if (position + 16 > value.Length) return false;
            var channel = new Channel
            {
                Name = name,
                Type = BinaryPrimitives.ReadInt32LittleEndian(value[position..]),
                PLinear = value[position + 4] != 0,
                XSampling = BinaryPrimitives.ReadInt32LittleEndian(value[(position + 8)..]),
                YSampling = BinaryPrimitives.ReadInt32LittleEndian(value[(position + 12)..]),
            };
            position += 16;
            if (channel.Type is < 0 or > 2 || channel.XSampling is < 1 or > 64 || channel.YSampling is < 1 or > 64) return false;
            channels.Add(channel);
            if (channels.Count > 1024) return false;
        }
        return channels.Count > 0;
    }

    // ---------------------------------------------------------------------------------------------
    // Chunks
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every level-0 chunk offset lies inside the file and the stored bytes are enough to produce the
    /// declared samples (uncompressed: all of them; compressed: within the codec's maximum expansion).
    /// </summary>
    private static bool ChunksArePlausible(ReadOnlySpan<byte> data, Header header)
    {
        long count;
        int headerBytes;
        if (header.Tiled)
        {
            count = (((long)header.Width + header.TileWidth - 1) / header.TileWidth) * (((long)header.Height + header.TileHeight - 1) / header.TileHeight);
            headerBytes = 20;
        }
        else
        {
            var lines = LinesPerBlock(header.Compression);
            count = (header.Height + lines - 1) / lines;
            headerBytes = 8;
        }
        if (count <= 0 || count > 16_777_216 || header.HeaderEnd + count * 8 > data.Length) return false;
        long stored = 0;
        for (long i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data[(int)(header.HeaderEnd + i * 8)..]);
            if (offset > (ulong)data.Length - (ulong)headerBytes) return false;
            var packed = BinaryPrimitives.ReadInt32LittleEndian(data[((int)offset + headerBytes - 4)..]);
            if (packed < 0 || (long)offset + headerBytes + packed > data.Length) return false;
            stored += packed;
        }
        long expected = 0; // subsampled channels (RY/BY) store fewer samples than the full grid
        foreach (var channel in header.Channels) expected += (long)channel.PlaneWidth * channel.PlaneHeight * channel.Size;
        long maxRatio = header.Compression switch { 0 => 1, 1 => 128, _ => 1100 };
        return stored * maxRatio >= expected;
    }

    private static int LinesPerBlock(int compression) => compression switch
    {
        3 or 5 => 16, // ZIP, PXR24
        4 or 6 or 7 => 32, // PIZ, B44, B44A
        _ => 1,
    };

    /// <summary>One validated chunk: payload location and the image-relative region it covers.</summary>
    private readonly record struct ChunkJob(int Start, int Length, int X0, int Y0, int Width, int Height);

    private static bool DecodeScanlines(ReadOnlySpan<byte> data, Header header, float[][] planes)
    {
        var lines = LinesPerBlock(header.Compression);
        var blocks = (header.Height + lines - 1) / lines;
        var table = header.HeaderEnd;
        if ((long)table + blocks * 8L > data.Length) return false;
        var jobs = new ChunkJob[blocks];
        for (var block = 0; block < blocks; block++)
        {
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data[(table + block * 8)..]);
            if (offset > (ulong)data.Length - 8) return false;
            var chunk = (int)offset;
            var y = BinaryPrimitives.ReadInt32LittleEndian(data[chunk..]);
            var packed = BinaryPrimitives.ReadInt32LittleEndian(data[(chunk + 4)..]);
            if (packed < 0 || chunk + 8L + packed > data.Length) return false;
            if (y < header.YMin || y > header.YMax) return false;
            var count = Math.Min(lines, header.YMax - y + 1);
            jobs[block] = new ChunkJob(chunk + 8, packed, 0, y - header.YMin, header.Width, count);
        }
        return DecodeChunks(data, header, planes, jobs, jobs.Length);
    }

    private static bool DecodeTiles(ReadOnlySpan<byte> data, Header header, float[][] planes)
    {
        var tilesX = (int)(((long)header.Width + header.TileWidth - 1) / header.TileWidth);
        var tilesY = (int)(((long)header.Height + header.TileHeight - 1) / header.TileHeight);
        var count = (long)tilesX * tilesY;
        var table = header.HeaderEnd;
        if (count > 16_777_216 || table + count * 8 > data.Length) return false;
        var jobs = new List<ChunkJob>((int)Math.Min(count, 65536));
        for (long t = 0; t < count; t++)
        {
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data[(int)(table + t * 8)..]);
            if (offset > (ulong)data.Length - 20) return false;
            var chunk = (int)offset;
            var tx = BinaryPrimitives.ReadInt32LittleEndian(data[chunk..]);
            var ty = BinaryPrimitives.ReadInt32LittleEndian(data[(chunk + 4)..]);
            var lx = BinaryPrimitives.ReadInt32LittleEndian(data[(chunk + 8)..]);
            var ly = BinaryPrimitives.ReadInt32LittleEndian(data[(chunk + 12)..]);
            var packed = BinaryPrimitives.ReadInt32LittleEndian(data[(chunk + 16)..]);
            if (lx != 0 || ly != 0) continue; // a lower mip/rip level
            if (tx < 0 || ty < 0 || tx >= tilesX || ty >= tilesY) return false;
            if (packed < 0 || chunk + 20L + packed > data.Length) return false;
            var x0 = tx * header.TileWidth;
            var y0 = ty * header.TileHeight;
            var w = Math.Min(header.TileWidth, header.Width - x0);
            var h = Math.Min(header.TileHeight, header.Height - y0);
            jobs.Add(new ChunkJob(chunk + 20, packed, x0, y0, w, h));
        }
        return DecodeChunks(data, header, planes, jobs.ToArray(), jobs.Count);
    }

    /// <summary>
    /// Chunks are independent (each owns its own lines/tile of the float planes), so they decompress in
    /// parallel; the planes receive exactly the values the serial loop would write.
    /// </summary>
    private static unsafe bool DecodeChunks(ReadOnlySpan<byte> data, Header header, float[][] planes, ChunkJob[] jobs, int jobCount)
    {
        var failed = 0;
        fixed (byte* pointer = data)
        {
            var address = (nint)pointer;
            var length = data.Length;
            var pixelsPerChunk = jobCount > 0 ? (long)header.Width * header.Height / jobCount : 0;
            ManagedCodecSupport.ForBands(jobCount, pixelsPerChunk * header.Channels.Count, (start, end) =>
            {
                var source = new ReadOnlySpan<byte>((void*)address, length);
                for (var i = start; i < end && Volatile.Read(ref failed) == 0; i++)
                {
                    var job = jobs[i];
                    if (!DecodeRegion(source.Slice(job.Start, job.Length), header, planes, job.X0, job.Y0, job.Width, job.Height))
                        Interlocked.Exchange(ref failed, 1);
                }
            });
        }
        return failed == 0;
    }

    /// <summary>Decodes one chunk covering [x0, x0+w) x [y0, y0+h) (image-relative coordinates).</summary>
    private static bool DecodeRegion(ReadOnlySpan<byte> packed, Header header, float[][] planes, int x0, int y0, int w, int h)
    {
        // Uncompressed size of this chunk, honouring channel subsampling.
        long expected = 0;
        var absoluteY0 = y0 + header.YMin;
        foreach (var channel in header.Channels)
        {
            var samplesX = SampleCount(x0 + header.XMin, w, channel.XSampling);
            var rows = SampleCount(absoluteY0, h, channel.YSampling);
            expected += (long)samplesX * rows * channel.Size;
        }
        if (expected <= 0 || expected > int.MaxValue) return false;

        byte[] raw;
        if (packed.Length == expected || header.Compression == (int)Compression.None)
        {
            if (packed.Length < expected) return false;
            raw = packed[..(int)expected].ToArray();
        }
        else
        {
            switch ((Compression)header.Compression)
            {
                case Compression.Rle:
                    if (!RleDecode(packed, (int)expected, out var rle)) return false;
                    raw = Reconstruct(rle);
                    break;
                case Compression.Zips:
                case Compression.Zip:
                    if (!ManagedCodecSupport.TryInflateExact(packed, (int)expected, ManagedCodecSupport.Wrapper.Zlib, out var inflated)) return false;
                    raw = Reconstruct(inflated);
                    break;
                case Compression.Pxr24:
                    if (!TryDecodePxr24(packed, header, x0, absoluteY0, w, h, (int)expected, out raw)) return false;
                    break;
                case Compression.B44:
                case Compression.B44A:
                    if (!TryDecodeB44(packed, header, x0, absoluteY0, w, h, (int)expected, out raw)) return false;
                    break;
                case Compression.Piz:
                    if (!ExrPiz.TryDecode(packed, header.Channels.Select(c => (c.Size, SampleCount(x0 + header.XMin, w, c.XSampling), SampleCount(absoluteY0, h, c.YSampling), c.YSampling)).ToArray(),
                            absoluteY0, h, (int)expected, out raw)) return false;
                    break;
                default:
                    return false;
            }
        }

        // Layout: for each scanline, for each channel (alphabetical), the samples of that line.
        var position = 0;
        for (var line = 0; line < h; line++)
        {
            var absoluteY = absoluteY0 + line;
            for (var c = 0; c < header.Channels.Count; c++)
            {
                var channel = header.Channels[c];
                if (Mod(absoluteY, channel.YSampling) != 0) continue;
                var samples = SampleCount(x0 + header.XMin, w, channel.XSampling);
                var bytes = samples * channel.Size;
                if (position + bytes > raw.Length) return false;
                var plane = planes[c];
                if (plane is not null && samples > 0)
                {
                    // Subsampled channels (e.g. RY/BY) store only the sampled positions, row by row.
                    var firstSampleX = header.XMin + Mod(-header.XMin, channel.XSampling);
                    var chunkFirstX = x0 + header.XMin + Mod(-(x0 + header.XMin), channel.XSampling);
                    var firstSampleY = header.YMin + Mod(-header.YMin, channel.YSampling);
                    var column = (chunkFirstX - firstSampleX) / channel.XSampling;
                    var sampleRow = (absoluteY - firstSampleY) / channel.YSampling;
                    if (column < 0 || sampleRow < 0 || column + samples > channel.PlaneWidth || sampleRow >= channel.PlaneHeight) return false;
                    var rowBase = (long)sampleRow * channel.PlaneWidth + column;
                    var span = raw.AsSpan(position, bytes);
                    var target = plane.AsSpan((int)rowBase, samples);
                    switch (channel.Type)
                    {
                        case 1:
                            for (var x = 0; x < target.Length; x++)
                                target[x] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span[(x * 2)..]));
                            break;
                        case 2:
                            for (var x = 0; x < target.Length; x++)
                                target[x] = BinaryPrimitives.ReadSingleLittleEndian(span[(x * 4)..]);
                            break;
                        default:
                            for (var x = 0; x < target.Length; x++)
                                target[x] = BinaryPrimitives.ReadUInt32LittleEndian(span[(x * 4)..]) / 4294967295f;
                            break;
                    }
                }
                position += bytes;
            }
        }
        return true;
    }

    private static bool HasNonZero(float[] plane)
    {
        foreach (var v in plane)
            if (v != 0f && !float.IsNaN(v)) return true;
        return false;
    }

    /// <summary>Luminance weights (Y of the red/green/blue primaries) from the chromaticities, Rec.709 by default.</summary>
    private static (float R, float G, float B) LuminanceWeights(float[]? c)
    {
        var rec709 = (0.2126f, 0.7152f, 0.0722f);
        if (c is null || c.Length < 8) return rec709;
        double xr = c[0], yr = c[1], xg = c[2], yg = c[3], xb = c[4], yb = c[5], xw = c[6], yw = c[7];
        if (yr <= 0 || yg <= 0 || yb <= 0 || yw <= 0) return rec709;
        // Solve [Xr Xg Xb; 1 1 1; Zr Zg Zb] * S = [Xw; 1; Zw]; S are the primaries' Y contributions.
        double Xr = xr / yr, Zr = (1 - xr - yr) / yr, Xg = xg / yg, Zg = (1 - xg - yg) / yg, Xb = xb / yb, Zb = (1 - xb - yb) / yb;
        double Xw = xw / yw, Zw = (1 - xw - yw) / yw;
        double det = Xr * (Zb - Zg) - Xg * (Zb - Zr) + Xb * (Zg - Zr);
        // Cramer's rule on rows (X, 1, Z) with columns (r, g, b).
        double m00 = Xr, m01 = Xg, m02 = Xb, m10 = 1, m11 = 1, m12 = 1, m20 = Zr, m21 = Zg, m22 = Zb;
        det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-12) return rec709;
        double sr = (Xw * (m11 * m22 - m12 * m21) - m01 * (1 * m22 - m12 * Zw) + m02 * (1 * m21 - m11 * Zw)) / det;
        double sg = (m00 * (1 * m22 - m12 * Zw) - Xw * (m10 * m22 - m12 * m20) + m02 * (m10 * Zw - 1 * m20)) / det;
        double sb = (m00 * (m11 * Zw - 1 * m21) - m01 * (m10 * Zw - 1 * m20) + Xw * (m10 * m21 - m11 * m20)) / det;
        if (!(sr > 0 && sg > 0 && sb > 0)) return rec709;
        return ((float)sr, (float)sg, (float)sb);
    }

    /// <summary>Chroma sample for full-resolution pixel (x, y) from a subsampled plane (nearest sample).</summary>
    private static float ChromaAt(float[] plane, Channel channel, int x, int y, int xMin, int yMin)
    {
        var firstX = xMin + Mod(-xMin, channel.XSampling);
        var firstY = yMin + Mod(-yMin, channel.YSampling);
        var sx = Math.Clamp((x + xMin - firstX) / channel.XSampling, 0, channel.PlaneWidth - 1);
        var sy = Math.Clamp((y + yMin - firstY) / channel.YSampling, 0, channel.PlaneHeight - 1);
        // Positions left of/above the first sample (negative numerator) clamp to the first sample.
        if (x + xMin < firstX) sx = 0;
        if (y + yMin < firstY) sy = 0;
        return plane[(long)sy * channel.PlaneWidth + sx];
    }

    /// <summary>
    /// PXR24: zlib of, per scanline and channel, byte planes of the delta-coded samples
    /// (HALF 2 planes, FLOAT 3 planes holding the top 24 bits, UINT 4 planes). Rebuilds the plain
    /// scanline/channel layout (little-endian samples).
    /// </summary>
    private static bool TryDecodePxr24(ReadOnlySpan<byte> packed, Header header, int x0, int absoluteY0, int w, int h, int expected, out byte[] raw)
    {
        raw = Array.Empty<byte>();
        long intermediate = 0;
        for (var line = 0; line < h; line++)
        {
            var absoluteY = absoluteY0 + line;
            foreach (var channel in header.Channels)
            {
                if (Mod(absoluteY, channel.YSampling) != 0) continue;
                var n = SampleCount(x0 + header.XMin, w, channel.XSampling);
                intermediate += (long)n * (channel.Type == 1 ? 2 : channel.Type == 2 ? 3 : 4);
            }
        }
        if (intermediate <= 0 || intermediate > int.MaxValue) return false;
        if (!ManagedCodecSupport.TryInflateExact(packed, (int)intermediate, ManagedCodecSupport.Wrapper.Zlib, out var data)) return false;
        raw = new byte[expected];
        var input = 0;
        var output = 0;
        for (var line = 0; line < h; line++)
        {
            var absoluteY = absoluteY0 + line;
            foreach (var channel in header.Channels)
            {
                if (Mod(absoluteY, channel.YSampling) != 0) continue;
                var n = SampleCount(x0 + header.XMin, w, channel.XSampling);
                if (n == 0) continue;
                switch (channel.Type)
                {
                    case 1:
                    {
                        if (input + 2L * n > data.Length || output + 2L * n > raw.Length) return false;
                        uint pixel = 0;
                        for (var i = 0; i < n; i++)
                        {
                            pixel = (pixel + (uint)((data[input + i] << 8) | data[input + n + i])) & 0xFFFF;
                            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(output + i * 2), (ushort)pixel);
                        }
                        input += 2 * n;
                        output += 2 * n;
                        break;
                    }
                    case 2:
                    {
                        if (input + 3L * n > data.Length || output + 4L * n > raw.Length) return false;
                        uint pixel = 0;
                        for (var i = 0; i < n; i++)
                        {
                            pixel += ((uint)data[input + i] << 24) | ((uint)data[input + n + i] << 16) | ((uint)data[input + 2 * n + i] << 8);
                            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(output + i * 4), pixel);
                        }
                        input += 3 * n;
                        output += 4 * n;
                        break;
                    }
                    default:
                    {
                        if (input + 4L * n > data.Length || output + 4L * n > raw.Length) return false;
                        uint pixel = 0;
                        for (var i = 0; i < n; i++)
                        {
                            pixel += ((uint)data[input + i] << 24) | ((uint)data[input + n + i] << 16) | ((uint)data[input + 2 * n + i] << 8) | data[input + 3 * n + i];
                            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(output + i * 4), pixel);
                        }
                        input += 4 * n;
                        output += 4 * n;
                        break;
                    }
                }
            }
        }
        return output == expected;
    }

    private static ushort[]? _b44Exp;

    /// <summary>pLinear channels are stored as 8*log(v) in B44; this maps a stored half back to linear.</summary>
    private static ushort[] B44ExpTable()
    {
        var table = _b44Exp;
        if (table is not null) return table;
        table = new ushort[65536];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            table[i] = float.IsFinite(value) ? BitConverter.HalfToUInt16Bits((Half)MathF.Exp(value / 8f)) : (ushort)i;
        }
        _b44Exp = table;
        return table;
    }

    /// <summary>
    /// B44/B44A: HALF channels are 4x4 blocks of 14 bytes (or 3 bytes for a flat block), other channel
    /// types are stored raw; all planar per channel. Rebuilds the plain scanline/channel layout.
    /// </summary>
    private static bool TryDecodeB44(ReadOnlySpan<byte> packed, Header header, int x0, int absoluteY0, int w, int h, int expected, out byte[] raw)
    {
        raw = Array.Empty<byte>();
        var count = header.Channels.Count;
        var planesBytes = new byte[count][];
        var planeNx = new int[count];
        var planeNy = new int[count];
        var input = 0;
        Span<ushort> s = stackalloc ushort[16];
        for (var c = 0; c < count; c++)
        {
            var channel = header.Channels[c];
            var nx = SampleCount(x0 + header.XMin, w, channel.XSampling);
            var ny = SampleCount(absoluteY0, h, channel.YSampling);
            planeNx[c] = nx;
            planeNy[c] = ny;
            var bytes = (long)nx * ny * channel.Size;
            if (bytes < 0 || bytes > int.MaxValue) return false;
            var plane = new byte[bytes];
            planesBytes[c] = plane;
            if (nx == 0 || ny == 0) continue;
            if (channel.Type != 1)
            {
                if (input + bytes > packed.Length) return false;
                packed.Slice(input, (int)bytes).CopyTo(plane);
                input += (int)bytes;
                continue;
            }
            var exp = channel.PLinear ? B44ExpTable() : null;
            for (var y = 0; y < ny; y += 4)
            {
                for (var x = 0; x < nx; x += 4)
                {
                    if (input + 3 > packed.Length) return false;
                    if (packed[input + 2] >= (13 << 2))
                    {
                        Unpack3(packed.Slice(input, 3), s);
                        input += 3;
                    }
                    else
                    {
                        if (input + 14 > packed.Length) return false;
                        Unpack14(packed.Slice(input, 14), s);
                        input += 14;
                    }
                    if (exp is not null)
                        for (var i = 0; i < 16; i++) s[i] = exp[s[i]];
                    var columns = Math.Min(4, nx - x);
                    for (var row = 0; row < 4 && y + row < ny; row++)
                        for (var col = 0; col < columns; col++)
                            BinaryPrimitives.WriteUInt16LittleEndian(plane.AsSpan(((y + row) * nx + x + col) * 2), s[row * 4 + col]);
                }
            }
        }

        raw = new byte[expected];
        var output = 0;
        var rows = new int[count];
        for (var line = 0; line < h; line++)
        {
            var absoluteY = absoluteY0 + line;
            for (var c = 0; c < count; c++)
            {
                var channel = header.Channels[c];
                if (Mod(absoluteY, channel.YSampling) != 0) continue;
                var nx = planeNx[c];
                var bytes = nx * channel.Size;
                if (nx == 0) continue;
                if (rows[c] >= planeNy[c] || output + bytes > raw.Length) return false;
                Buffer.BlockCopy(planesBytes[c], rows[c]++ * bytes, raw, output, bytes);
                output += bytes;
            }
        }
        return output == expected;
    }

    private static void Unpack3(ReadOnlySpan<byte> b, Span<ushort> s)
    {
        var v = (ushort)((b[0] << 8) | b[1]);
        v = (v & 0x8000) != 0 ? (ushort)(v & 0x7FFF) : (ushort)~v;
        s.Fill(v);
    }

    private static void Unpack14(ReadOnlySpan<byte> b, Span<ushort> s)
    {
        s[0] = (ushort)((b[0] << 8) | b[1]);
        var shift = b[2] >> 2;
        var bias = 0x20 << shift;
        s[4] = (ushort)(s[0] + ((((b[2] << 4) | (b[3] >> 4)) & 0x3F) << shift) - bias);
        s[8] = (ushort)(s[4] + ((((b[3] << 2) | (b[4] >> 6)) & 0x3F) << shift) - bias);
        s[12] = (ushort)(s[8] + ((b[4] & 0x3F) << shift) - bias);

        s[1] = (ushort)(s[0] + ((b[5] >> 2) << shift) - bias);
        s[5] = (ushort)(s[4] + ((((b[5] << 4) | (b[6] >> 4)) & 0x3F) << shift) - bias);
        s[9] = (ushort)(s[8] + ((((b[6] << 2) | (b[7] >> 6)) & 0x3F) << shift) - bias);
        s[13] = (ushort)(s[12] + ((b[7] & 0x3F) << shift) - bias);

        s[2] = (ushort)(s[1] + ((b[8] >> 2) << shift) - bias);
        s[6] = (ushort)(s[5] + ((((b[8] << 4) | (b[9] >> 4)) & 0x3F) << shift) - bias);
        s[10] = (ushort)(s[9] + ((((b[9] << 2) | (b[10] >> 6)) & 0x3F) << shift) - bias);
        s[14] = (ushort)(s[13] + ((b[10] & 0x3F) << shift) - bias);

        s[3] = (ushort)(s[2] + ((b[11] >> 2) << shift) - bias);
        s[7] = (ushort)(s[6] + ((((b[11] << 4) | (b[12] >> 4)) & 0x3F) << shift) - bias);
        s[11] = (ushort)(s[10] + ((((b[12] << 2) | (b[13] >> 6)) & 0x3F) << shift) - bias);
        s[15] = (ushort)(s[14] + ((b[13] & 0x3F) << shift) - bias);

        for (var i = 0; i < 16; i++)
            s[i] = (s[i] & 0x8000) != 0 ? (ushort)(s[i] & 0x7FFF) : (ushort)~s[i];
    }

    private static int Mod(int value, int divisor)
    {
        var m = value % divisor;
        return m < 0 ? m + divisor : m;
    }

    /// <summary>Number of coordinates c in [start, start+count) with c % sampling == 0.</summary>
    private static int SampleCount(int start, int count, int sampling)
    {
        if (sampling == 1) return count;
        var n = 0;
        for (var c = start; c < start + count; c++) if (Mod(c, sampling) == 0) n++;
        return n;
    }

    private static bool RleDecode(ReadOnlySpan<byte> source, int expected, out byte[] output)
    {
        output = new byte[expected];
        int input = 0, produced = 0;
        while (input < source.Length)
        {
            int count = (sbyte)source[input++];
            if (count < 0)
            {
                count = -count;
                if (input + count > source.Length || produced + count > expected) return false;
                source.Slice(input, count).CopyTo(output.AsSpan(produced));
                input += count;
                produced += count;
            }
            else
            {
                count += 1;
                if (input >= source.Length || produced + count > expected) return false;
                output.AsSpan(produced, count).Fill(source[input++]);
                produced += count;
            }
        }
        return produced == expected;
    }

    /// <summary>Undoes the ZIP/RLE byte predictor and the even/odd byte split.</summary>
    private static byte[] Reconstruct(byte[] data)
    {
        for (var i = 1; i < data.Length; i++) data[i] = (byte)(data[i - 1] + data[i] - 128);
        var output = new byte[data.Length];
        var half = (data.Length + 1) / 2;
        int t1 = 0, t2 = half, o = 0;
        while (true)
        {
            if (o < output.Length) output[o++] = data[t1++]; else break;
            if (o < output.Length) output[o++] = data[t2++]; else break;
        }
        return output;
    }
}
