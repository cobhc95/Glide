using System.Buffers.Binary;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;
using FileSource = Glide.Imaging.ManagedCodecSupport.FileSource;

namespace Glide.Imaging;

/// <summary>
/// Camera RAW support without a RAW developer: every mainstream RAW container embeds at least one
/// camera-rendered JPEG (often full size). This decoder walks the container structure — TIFF IFD
/// chains incl. SubIFDs/EXIF (DNG, CR2, NEF, ARW, ORF, RW2, PEF, SRW, …), ISO-BMFF (CR3), the RAF
/// header, CIFF heaps (CRW), X3F section directories and MRW blocks — collects every embedded JPEG,
/// validates each header (baseline/extended/progressive 8-bit only) and decodes the largest one with
/// the bundled JPEG codec. If the structure yields nothing large, the file is scanned for embedded JPEG
/// streams. Only the chosen JPEG's bytes are read; the raw sensor data is never touched.
/// Orientation: the preview's own EXIF orientation wins, otherwise the container's IFD0 orientation.
/// </summary>
internal static class RawPreviewDecoder
{
    private const int MaxCandidates = 64;
    private const long MaxJpegBytes = 256L * 1024 * 1024;
    private const int SmallPreviewLongestSide = 1024;

    public static bool IsRawExtension(string extension) => extension switch
    {
        ".dng" or ".cr2" or ".cr3" or ".crw" or ".nef" or ".nrw" or ".arw" or ".srf" or ".sr2" or ".raf" or
        ".orf" or ".ori" or ".rw2" or ".rwl" or ".pef" or ".ptx" or ".3fr" or ".fff" or ".iiq" or ".cap" or
        ".eip" or ".mef" or ".mos" or ".mrw" or ".x3f" or ".erf" or ".kdc" or ".dcr" or ".k25" or ".bay" or
        ".srw" or ".rwz" or ".gpr" or ".mdc" or ".raw" or ".dcs" or ".drf" or ".dsc" or ".r2d" or ".rw1" => true,
        _ => false,
    };

    private readonly record struct RawRef(long Offset, long Length, bool PatchSoi);

    private readonly record struct Candidate(long Offset, long Length, bool PatchSoi, int Width, int Height, int Orientation, long SosOffset);

    public static bool TryProbe(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            using var source = FileSource.Open(path);
            if (source is null) return false;
            // Same selection as decode (the file is only scanned when the directories yield no large
            // preview), so the probed size always matches the decoded frame.
            if (!TrySelect(source, allowScan: true, out var best, out var containerOrientation)) return false;
            var orientation = best.Orientation > 1 ? best.Orientation : containerOrientation;
            dimensions = ManagedCodecSupport.SwapsAxes(orientation)
                ? new ImageDimensions(best.Height, best.Width)
                : new ImageDimensions(best.Width, best.Height);
            return dimensions.IsValid;
        }
        catch
        {
            dimensions = default;
            return false;
        }
    }

    public static bool TryDecode(string path, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        try
        {
            using var source = FileSource.Open(path);
            if (source is null) return false;
            if (!TrySelect(source, allowScan: true, out var best, out var containerOrientation)) return false;
            if (!ManagedCodecSupport.FitsMemory(best.Width, best.Height)) return false;

            // The preview must be complete (end with EOI): a truncated or corrupt preview is rejected so
            // the caller falls through to WIC/Shell instead of showing a half-grey frame.
            var bytes = ReadJpeg(source, best, scan: false);
            if (bytes is null || !ManagedCodecSupport.TryDecodeEncoded(bytes, maxLongestSide, out var decoded, out _))
            {
                // A wrong declared length: find the real end of the stream instead.
                if (best.Length <= 0) return false;
                bytes = ReadJpeg(source, best, scan: true);
                if (bytes is null || !ManagedCodecSupport.TryDecodeEncoded(bytes, maxLongestSide, out decoded, out _)) return false;
            }
            var orientation = best.Orientation > 1 ? best.Orientation : containerOrientation;
            image = ManagedCodecSupport.ApplyOrientation(decoded, orientation);
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Candidate selection
    // ---------------------------------------------------------------------------------------------

    private sealed class Context
    {
        public required FileSource Source;
        public readonly List<RawRef> Refs = new();
        public readonly HashSet<long> Visited = new();
        public int Orientation = 1;
        public bool MinoltaMakerNote;
        public int IfdBudget = 128;

        public void Add(long offset, long length, bool patch = false)
        {
            if (Refs.Count >= MaxCandidates || offset < 0 || offset >= Source.Length) return;
            foreach (var r in Refs) if (r.Offset == offset) return;
            Refs.Add(new RawRef(offset, length, patch));
        }
    }

    private static bool TrySelect(FileSource source, bool allowScan, out Candidate best, out int containerOrientation)
    {
        best = default;
        var context = new Context { Source = source };
        var head = source.Head;
        try
        {
            CollectStructured(context, head);
        }
        catch
        {
            // A broken directory must never prevent the fallback scan.
        }
        containerOrientation = context.Orientation is >= 1 and <= 8 ? context.Orientation : 1;

        var found = false;
        foreach (var r in context.Refs)
        {
            if (TryValidate(source, r, out var candidate) && (!found || Area(candidate) > Area(best)))
            {
                best = candidate;
                found = true;
            }
        }

        if (allowScan && (!found || Math.Max(best.Width, best.Height) < SmallPreviewLongestSide))
        {
            foreach (var offset in ScanForJpegs(source))
            {
                if (context.Refs.Exists(r => r.Offset == offset)) continue;
                if (TryValidate(source, new RawRef(offset, 0, false), out var candidate) && (!found || Area(candidate) > Area(best)))
                {
                    best = candidate;
                    found = true;
                }
            }
        }
        return found;
    }

    private static long Area(Candidate c) => (long)c.Width * c.Height;

    private static void CollectStructured(Context context, ReadOnlySpan<byte> head)
    {
        if (head.Length < 16) return;
        // TIFF family (incl. ORF "IIRO"/"IIRS"/"MMOR" and RW2/RWL "IIU\0").
        if ((head[0] == 'I' && head[1] == 'I' && (head[2] is 0x2A or 0x55 || (head[2] == 'R' && head[3] is (byte)'O' or (byte)'S'))) ||
            (head[0] == 'M' && head[1] == 'M' && ((head[2] == 0 && head[3] == 0x2A) || (head[2] == 'O' && head[3] == 'R'))))
        {
            var le = head[0] == 'I';
            var first = le ? BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) : BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            ParseTiffChain(context, 0, first, le, 0, isMain: true);
            return;
        }
        if (head[..8].SequenceEqual("FUJIFILM"u8))
        {
            ParseRaf(context, head);
            return;
        }
        if (head.Length >= 14 && head[0] == 'I' && head[1] == 'I' && head[6..14].SequenceEqual("HEAPCCDR"u8))
        {
            var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(head[2..]);
            ParseCiff(context, headerLength, context.Source.Length - headerLength, 0);
            return;
        }
        if (head[..4].SequenceEqual("FOVb"u8))
        {
            ParseX3f(context);
            return;
        }
        if (head[..4].SequenceEqual("\0MRM"u8))
        {
            ParseMrw(context, head);
            return;
        }
        if (head[4..8].SequenceEqual("ftyp"u8))
        {
            ParseBmff(context, 0, context.Source.Length, 0);
        }
    }

    // -- TIFF -----------------------------------------------------------------------------------

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    private static void ParseTiffChain(Context context, long tiffBase, uint firstIfd, bool le, int depth, bool isMain)
    {
        var ifd = firstIfd;
        for (var n = 0; ifd != 0 && n < 16; n++)
        {
            var next = ParseIfd(context, tiffBase, ifd, le, depth, isMain && n == 0);
            if (next is null) return;
            ifd = next.Value;
        }
    }

    /// <summary>Parses one IFD; returns the next-IFD offset (relative), or null on error.</summary>
    private static uint? ParseIfd(Context context, long tiffBase, uint ifdRelative, bool le, int depth, bool isIfd0, bool minolta = false)
    {
        if (depth > 6 || --context.IfdBudget < 0) return null;
        var source = context.Source;
        var ifd = tiffBase + ifdRelative;
        if (!context.Visited.Add(ifd)) return null;
        if (!source.TryU16(ifd, le, out var count) || count is 0 or > 1000) return null;
        var entries = source.ReadBytes(ifd + 2, count * 12L + 4);
        if (entries is null) return null;

        long jpegOffset = 0, jpegLength = 0;
        int compression = 0;
        long stripOffset = -1, stripCount = -1;
        int stripEntries = 0;

        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * 12, 12);
            int tag = le ? BinaryPrimitives.ReadUInt16LittleEndian(e) : BinaryPrimitives.ReadUInt16BigEndian(e);
            int type = le ? BinaryPrimitives.ReadUInt16LittleEndian(e[2..]) : BinaryPrimitives.ReadUInt16BigEndian(e[2..]);
            long valueCount = le ? BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) : BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            var size = TypeSize(type);
            if (size == 0) continue;
            var total = size * valueCount;
            var inline = total <= 4;
            uint raw32 = le ? BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) : BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            long valueOffset = inline ? ifd + 2 + i * 12L + 8 : tiffBase + raw32;

            var inlineShort = le ? BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) : BinaryPrimitives.ReadUInt16BigEndian(e[8..]);
            var inlineByte = e[8];

            if (minolta)
            {
                switch (tag)
                {
                    case 0x0088: jpegOffset = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                    case 0x0089: jpegLength = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                    case 0x0081: if (!inline) context.Add(valueOffset, total, patch: true); break;
                }
                continue;
            }

            switch (tag)
            {
                case 0x0112:
                    if (isIfd0 && depth == 0) context.Orientation = (int)ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le);
                    break;
                case 0x0201: jpegOffset = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                case 0x0202: jpegLength = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                case 0x0103: compression = (int)ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                case 0x0111: stripEntries = (int)Math.Min(valueCount, int.MaxValue); stripOffset = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                case 0x0117: stripCount = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le); break;
                case 0x002E:
                    // RW2/RWL "JpgFromRaw" stored as an UNDEFINED blob directly in IFD0.
                    if (type == 7 && !inline) context.Add(valueOffset, total);
                    break;
                case 0x014A:
                    for (long k = 0; k < Math.Min(valueCount, 16); k++)
                    {
                        uint sub;
                        if (inline && valueCount == 1) sub = raw32;
                        else if (!source.TryU32(valueOffset + k * 4, le, out sub)) break;
                        ParseTiffChain(context, tiffBase, sub, le, depth + 1, isMain: false);
                    }
                    break;
                case 0x8769:
                    {
                        var exif = ReadFirst(source, inline, type, raw32, inlineShort, inlineByte, valueOffset, le);
                        if (exif > 0 && exif <= uint.MaxValue) ParseIfd(context, tiffBase, (uint)exif, le, depth + 1, false);
                    }
                    break;
                case 0x927C:
                    if (context.MinoltaMakerNote && !inline)
                        ParseIfd(context, tiffBase, raw32, le, depth + 1, false, minolta: true);
                    break;
            }
        }

        if (jpegOffset > 0 && jpegLength > 0)
            context.Add(tiffBase + jpegOffset, jpegLength, patch: minolta);
        // Old-style/lossy JPEG strips (CR2 IFD0/IFD3, DNG previews, Sony/Pentax previews). Lossless
        // (SOF3) streams are rejected later by header validation.
        if (compression is 6 or 7 or 99 or 34892 && stripEntries == 1 && stripOffset > 0 && stripCount > 0)
            context.Add(tiffBase + stripOffset, stripCount);

        var nextOffset = ifd + 2 + count * 12L;
        if (!source.TryU32(nextOffset, le, out var next)) return 0;
        return next;
    }

    private static long ReadFirst(FileSource source, bool inline, int type, uint raw32, int inlineShort, byte inlineByte, long valueOffset, bool le)
    {
        if (inline) return type switch { 3 or 8 => inlineShort, 1 or 6 or 7 => inlineByte, _ => raw32 };
        if (type is 3 or 8) return source.TryU16(valueOffset, le, out var s) ? s : -1;
        return source.TryU32(valueOffset, le, out var l) ? l : -1;
    }

    // -- Fujifilm RAF --------------------------------------------------------------------------

    private static void ParseRaf(Context context, ReadOnlySpan<byte> head)
    {
        if (head.Length < 92) return;
        var offset = BinaryPrimitives.ReadUInt32BigEndian(head[84..]);
        var length = BinaryPrimitives.ReadUInt32BigEndian(head[88..]);
        if (offset > 0 && length > 0) context.Add(offset, length);
    }

    // -- Canon CRW (CIFF) ----------------------------------------------------------------------

    private static void ParseCiff(Context context, long start, long length, int depth)
    {
        if (depth > 4 || length < 6 || --context.IfdBudget < 0) return;
        var source = context.Source;
        if (!source.TryU32(start + length - 4, true, out var dirOffset)) return;
        var dir = start + dirOffset;
        if (!context.Visited.Add(dir)) return;
        if (!source.TryU16(dir, true, out var count) || count is 0 or > 1024) return;
        var entries = source.ReadBytes(dir + 2, count * 10L);
        if (entries is null) return;
        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * 10, 10);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(e);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(e[2..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(e[6..]);
            if ((tag & 0xC000) != 0) continue; // value stored in the record itself
            var dataType = tag & 0x3800;
            if (dataType is 0x2800 or 0x3000)
                ParseCiff(context, start + offset, size, depth + 1);
            else if ((tag & 0x3FFF) == 0x2007)
                context.Add(start + offset, size); // JpgFromRaw
        }
    }

    // -- Sigma X3F -----------------------------------------------------------------------------

    private static void ParseX3f(Context context)
    {
        var source = context.Source;
        if (!source.TryU32(source.Length - 4, true, out var dirOffset)) return;
        var header = source.ReadBytes(dirOffset, 12);
        if (header is null || !header.AsSpan(0, 4).SequenceEqual("SECd"u8)) return;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (count is 0 or > 256) return;
        var entries = source.ReadBytes(dirOffset + 12, count * 12L);
        if (entries is null) return;
        for (var i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * 12, 12);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(e);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            if (!e[8..12].SequenceEqual("IMA2"u8) && !e[8..12].SequenceEqual("IMAG"u8)) continue;
            var section = source.ReadBytes(offset, 28);
            if (section is null || !section.AsSpan(0, 4).SequenceEqual("SECi"u8) || length <= 28) continue;
            var format = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(12));
            if (format == 18) context.Add(offset + 28L, length - 28L);
        }
    }

    // -- Minolta MRW ---------------------------------------------------------------------------

    private static void ParseMrw(Context context, ReadOnlySpan<byte> head)
    {
        var source = context.Source;
        var dataOffset = 8L + BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        var position = 8L;
        for (var n = 0; n < 32 && position + 8 <= Math.Min(dataOffset, source.Length); n++)
        {
            var block = source.ReadBytes(position, 8);
            if (block is null) return;
            var length = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));
            if (block.AsSpan(0, 4).SequenceEqual("\0TTW"u8))
            {
                var tiff = source.ReadBytes(position + 8, 8);
                if (tiff is null) return;
                var le = tiff[0] == 'I';
                var first = le ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(4));
                context.MinoltaMakerNote = true;
                ParseTiffChain(context, position + 8, first, le, 0, isMain: true);
                context.MinoltaMakerNote = false;
            }
            position += 8 + length;
        }
    }

    // -- ISO-BMFF (Canon CR3) ------------------------------------------------------------------

    private static ReadOnlySpan<byte> CanonUuid => new byte[] { 0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48 };
    private static ReadOnlySpan<byte> PreviewUuid => new byte[] { 0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16 };

    private static void ParseBmff(Context context, long start, long end, int depth)
    {
        if (depth > 6) return;
        var source = context.Source;
        var position = start;
        for (var n = 0; n < 256 && position + 8 <= end; n++)
        {
            if (--context.IfdBudget < -512) return;
            var header = source.ReadBytes(position, Math.Min(16 + 16, end - position));
            if (header is null || header.Length < 8) return;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8;
            if (size == 1)
            {
                if (header.Length < 16) return;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize || position + size > end) return;
            var type = header.AsSpan(4, 4);
            var payload = position + headerSize;
            var payloadEnd = position + size;

            if (type.SequenceEqual("moov"u8) || type.SequenceEqual("trak"u8) || type.SequenceEqual("mdia"u8) ||
                type.SequenceEqual("minf"u8) || type.SequenceEqual("stbl"u8))
            {
                if (type.SequenceEqual("trak"u8)) ParseTrack(context, payload, payloadEnd);
                else ParseBmff(context, payload, payloadEnd, depth + 1);
            }
            else if (type.SequenceEqual("uuid"u8) && header.Length >= headerSize + 16)
            {
                var uuid = header.AsSpan(headerSize, 16);
                if (uuid.SequenceEqual(CanonUuid)) ParseBmff(context, payload + 16, payloadEnd, depth + 1);
                else if (uuid.SequenceEqual(PreviewUuid)) AddFirstJpegIn(context, payload + 16, payloadEnd);
            }
            else if (type.SequenceEqual("CMT1"u8))
            {
                var tiff = source.ReadBytes(payload, 8);
                if (tiff is not null && (tiff[0] == 'I' || tiff[0] == 'M'))
                {
                    var le = tiff[0] == 'I';
                    var first = le ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(4));
                    ParseTiffChain(context, payload, first, le, 0, isMain: true);
                }
            }
            else if (type.SequenceEqual("THMB"u8) || type.SequenceEqual("PRVW"u8))
            {
                AddFirstJpegIn(context, payload, payloadEnd);
            }
            position += size;
        }
    }

    private static void AddFirstJpegIn(Context context, long start, long end)
    {
        var window = context.Source.ReadBytes(start, Math.Min(96, end - start));
        if (window is null) return;
        for (var i = 0; i + 2 < window.Length; i++)
        {
            if (window[i] == 0xFF && window[i + 1] == 0xD8 && window[i + 2] == 0xFF)
            {
                context.Add(start + i, end - start - i);
                return;
            }
        }
    }

    private static void ParseTrack(Context context, long start, long end)
    {
        // Track sample tables: the first sample of each track (CR3 track 1 is a full-size JPEG).
        long chunkOffset = -1, sampleSize = -1;
        FindSampleTable(context, start, end, 0, ref chunkOffset, ref sampleSize);
        if (chunkOffset > 0 && sampleSize > 0) context.Add(chunkOffset, sampleSize);
    }

    private static void FindSampleTable(Context context, long start, long end, int depth, ref long chunkOffset, ref long sampleSize)
    {
        if (depth > 4) return;
        var source = context.Source;
        var position = start;
        for (var n = 0; n < 64 && position + 8 <= end; n++)
        {
            var header = source.ReadBytes(position, 8);
            if (header is null) return;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size < 8 || position + size > end) return;
            var type = header.AsSpan(4, 4);
            if (type.SequenceEqual("mdia"u8) || type.SequenceEqual("minf"u8) || type.SequenceEqual("stbl"u8))
            {
                FindSampleTable(context, position + 8, position + size, depth + 1, ref chunkOffset, ref sampleSize);
            }
            else if (type.SequenceEqual("stsz"u8) && size >= 20)
            {
                var body = source.ReadBytes(position + 8, Math.Min(16, size - 8));
                if (body is not null && body.Length >= 12)
                {
                    var fixedSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4));
                    var count = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8));
                    if (fixedSize != 0) sampleSize = fixedSize;
                    else if (count > 0 && body.Length >= 16) sampleSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(12));
                }
            }
            else if ((type.SequenceEqual("stco"u8) || type.SequenceEqual("co64"u8)) && size >= 20)
            {
                var body = source.ReadBytes(position + 8, Math.Min(16, size - 8));
                if (body is not null && body.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4)) > 0)
                {
                    chunkOffset = type[2] == 'c' && body.Length >= 12
                        ? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(8))
                        : body.Length >= 16 ? (long)BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(8)) : -1;
                }
            }
            position += size;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Generic embedded-JPEG scan (fallback)
    // ---------------------------------------------------------------------------------------------

    private static List<long> ScanForJpegs(FileSource source)
    {
        const int ChunkSize = 4 << 20;
        var results = new List<long>();
        var buffer = new byte[ChunkSize + 2];
        for (long position = 0; position < source.Length && results.Count < MaxCandidates; position += ChunkSize)
        {
            var length = (int)Math.Min(ChunkSize + 2, source.Length - position);
            if (length < 3 || !source.TryRead(position, buffer.AsSpan(0, length))) break;
            var span = buffer.AsSpan(0, length);
            var limit = Math.Min(length - 2, ChunkSize);
            var index = 0;
            while (index < limit)
            {
                var next = span[index..limit].IndexOf((byte)0xFF);
                if (next < 0) break;
                index += next;
                if (span[index + 1] == 0xD8 && span[index + 2] == 0xFF)
                {
                    results.Add(position + index);
                    if (results.Count >= MaxCandidates) break;
                }
                index++;
            }
        }
        return results;
    }

    // ---------------------------------------------------------------------------------------------
    // JPEG header validation and extraction
    // ---------------------------------------------------------------------------------------------

    private static bool TryValidate(FileSource source, RawRef r, out Candidate candidate)
    {
        candidate = default;
        if (!ParseJpegHeader(source, r.Offset, r.PatchSoi, out var width, out var height, out var orientation, out var sos)) return false;
        if (r.Length > 0 && sos > r.Offset + r.Length) return false;
        candidate = new Candidate(r.Offset, r.Length, r.PatchSoi, width, height, orientation, sos);
        return true;
    }

    /// <summary>
    /// Walks marker segments up to the first SOS. Accepts only 8-bit baseline/extended/progressive
    /// Huffman frames with 1 or 3 components (what the bundled JPEG codec decodes) and reads the
    /// EXIF orientation from APP1 if present.
    /// </summary>
    private static bool ParseJpegHeader(FileSource source, long offset, bool patchSoi, out int width, out int height,
        out int orientation, out long sosOffset)
    {
        width = height = 0;
        orientation = 1;
        sosOffset = 0;
        Span<byte> two = stackalloc byte[2];
        if (!source.TryRead(offset, two) || two[1] != 0xD8 || (two[0] != 0xFF && !(patchSoi && two[0] == 0))) return false;
        var position = offset + 2;
        var sawFrame = false;
        var sawTables = false;
        Span<byte> segment = stackalloc byte[10];
        for (var n = 0; n < 96; n++)
        {
            if (!source.TryRead(position, two) || two[0] != 0xFF) return false;
            var marker = two[1];
            var fill = 0;
            while (marker == 0xFF && fill++ < 16)
            {
                position++;
                if (!source.TryRead(position, two)) return false;
                marker = two[1];
            }
            position += 2;
            if (marker is 0x00 or 0xD8 or 0xD9) return false;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
            if (!source.TryU16(position, false, out var length) || length < 2) return false;
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (length < 8 || !source.TryRead(position + 2, segment[..6])) return false;
                    if (segment[0] != 8) return false;
                    height = (segment[1] << 8) | segment[2];
                    width = (segment[3] << 8) | segment[4];
                    if (segment[5] is not (1 or 3)) return false;
                    if (!ManagedCodecSupport.IsValidDimensions(width, height)) return false;
                    sawFrame = true;
                    break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    return false; // lossless / hierarchical / arithmetic: not a displayable preview
                case 0xDB:
                    sawTables = true;
                    break;
                case 0xE1:
                    if (length >= 16)
                    {
                        var app1 = source.ReadBytes(position + 2, length - 2);
                        if (app1 is not null && app1.AsSpan().StartsWith("Exif\0\0"u8))
                            orientation = ReadExifOrientation(app1.AsSpan(6));
                    }
                    break;
                case 0xDA:
                    if (!sawFrame || !sawTables) return false;
                    sosOffset = position - 2;
                    return true;
            }
            position += length;
        }
        return false;
    }

    internal static int ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        try
        {
            if (tiff.Length < 8) return 1;
            var le = tiff[0] == 'I';
            if (!le && tiff[0] != 'M') return 1;
            var ifd = le ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]);
            if (ifd + 2 > tiff.Length) return 1;
            int count = le ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[(int)ifd..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[(int)ifd..]);
            for (var i = 0; i < count; i++)
            {
                var e = (int)ifd + 2 + i * 12;
                if (e + 12 > tiff.Length) return 1;
                int tag = le ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[e..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[e..]);
                if (tag != 0x0112) continue;
                int value = le ? BinaryPrimitives.ReadUInt16LittleEndian(tiff[(e + 8)..]) : BinaryPrimitives.ReadUInt16BigEndian(tiff[(e + 8)..]);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        catch
        {
        }
        return 1;
    }

    private static byte[]? ReadJpeg(FileSource source, Candidate candidate, bool scan)
    {
        byte[]? bytes;
        if (!scan && candidate.Length > 0 && candidate.Offset + candidate.Length <= source.Length && candidate.Length <= MaxJpegBytes)
        {
            bytes = source.ReadBytes(candidate.Offset, candidate.Length);
            if (bytes is not null && !EndsWithEoi(bytes)) bytes = null;
        }
        else
        {
            bytes = ReadUntilEoi(source, candidate.Offset, candidate.SosOffset);
        }
        if (bytes is not null && candidate.PatchSoi && bytes.Length > 0) bytes[0] = 0xFF;
        return bytes;
    }

    /// <summary>True when an EOI marker sits at the end of the stream (allowing a little padding).</summary>
    private static bool EndsWithEoi(byte[] bytes)
    {
        var start = Math.Max(1, bytes.Length - 64);
        for (var i = bytes.Length - 1; i >= start; i--)
            if (bytes[i] == 0xD9 && bytes[i - 1] == 0xFF) return true;
        return false;
    }

    private static byte[]? ReadUntilEoi(FileSource source, long start, long sos)
    {
        const int Chunk = 1 << 20;
        using var output = new MemoryStream();
        var buffer = new byte[Chunk];
        var position = start;
        var previousWasFf = false;
        var scanFrom = sos - start;
        while (position < source.Length && output.Length < MaxJpegBytes)
        {
            var length = (int)Math.Min(Chunk, source.Length - position);
            if (!source.TryRead(position, buffer.AsSpan(0, length))) return null;
            var chunkStart = output.Length;
            for (var i = 0; i < length; i++)
            {
                var absolute = chunkStart + i;
                if (absolute > scanFrom && previousWasFf && buffer[i] == 0xD9)
                {
                    output.Write(buffer, 0, i + 1);
                    return output.ToArray();
                }
                previousWasFf = buffer[i] == 0xFF;
            }
            output.Write(buffer, 0, length);
            position += length;
        }
        return null; // no EOI before the end of the file: truncated
    }
}
