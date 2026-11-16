using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Glide.Imaging.Encoders;

/// <summary>
/// Raw metadata carried into PNG and WebP saves (WIC handles JPEG/TIFF/JPEG XR/HEIF natively): the EXIF TIFF
/// block, the ICC profile and the XMP packet, read from a JPEG, PNG or WebP source. Reading is bounded and
/// never throws; anything malformed is simply skipped.
/// </summary>
public sealed record MetadataBlobs(byte[]? Exif, byte[]? Icc, byte[]? Xmp)
{
    private const int MaxBlob = 16 * 1024 * 1024;
    private const long MaxScan = 64L * 1024 * 1024;
    private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    private static readonly byte[] IccHeader = "ICC_PROFILE\0"u8.ToArray();

    public bool IsEmpty => Exif is null && Icc is null && Xmp is null;

    public static MetadataBlobs Read(string? path)
    {
        var empty = new MetadataBlobs(null, null, null);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            Span<byte> head = stackalloc byte[12];
            if (stream.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12) return empty;
            stream.Position = 0;
            if (head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(stream);
            if (head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ReadPng(stream);
            if (head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return ReadWebP(stream);
            return empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return empty;
        }
    }

    private static MetadataBlobs ReadJpeg(Stream stream)
    {
        byte[]? exif = null, xmp = null;
        var iccParts = new SortedDictionary<int, byte[]>();
        stream.Position = 2;
        Span<byte> marker = stackalloc byte[4];
        while (stream.Position < Math.Min(stream.Length, MaxScan))
        {
            if (stream.ReadAtLeast(marker, 4, throwOnEndOfStream: false) < 4 || marker[0] != 0xFF) break;
            var code = marker[1];
            if (code == 0xFF) { stream.Position -= 3; continue; } // fill byte before a marker
            if (code == 0xD9 || code == 0xDA) break; // EOI / start of scan: metadata precedes image data
            if (code is >= 0xD0 and <= 0xD7 || code == 0x01) { stream.Position -= 2; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]) - 2;
            if (length < 0) break;
            if (code is 0xE1 or 0xE2)
            {
                var payload = new byte[length];
                if (stream.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length) break;
                if (code == 0xE1 && exif is null && payload.AsSpan().StartsWith(ExifHeader)) exif = payload[ExifHeader.Length..];
                else if (code == 0xE1 && xmp is null && payload.AsSpan().StartsWith(XmpHeader)) xmp = payload[XmpHeader.Length..];
                else if (code == 0xE2 && payload.Length > IccHeader.Length + 2 && payload.AsSpan().StartsWith(IccHeader))
                    iccParts[payload[IccHeader.Length]] = payload[(IccHeader.Length + 2)..];
            }
            else stream.Position += length;
        }
        byte[]? icc = null;
        if (iccParts.Count > 0)
        {
            var total = iccParts.Values.Sum(p => (long)p.Length);
            if (total <= MaxBlob) icc = iccParts.Values.SelectMany(p => p).ToArray();
        }
        return new MetadataBlobs(exif, icc, xmp);
    }

    private static MetadataBlobs ReadPng(Stream stream)
    {
        byte[]? exif = null, icc = null, xmp = null;
        stream.Position = 8;
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < Math.Min(stream.Length, MaxScan))
        {
            if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8) break;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..]);
            if (length > MaxBlob || type is "IDAT" or "IEND") break;
            var data = new byte[length];
            if (stream.ReadAtLeast(data, (int)length, throwOnEndOfStream: false) < length) break;
            stream.Position += 4; // CRC
            switch (type)
            {
                case "eXIf": exif ??= data; break;
                case "iCCP":
                {
                    var zero = Array.IndexOf(data, (byte)0);
                    if (zero > 0 && zero + 2 <= data.Length && data[zero + 1] == 0) icc ??= Inflate(data.AsSpan(zero + 2));
                    break;
                }
                case "iTXt":
                {
                    var key = Array.IndexOf(data, (byte)0);
                    if (key < 0 || Encoding.ASCII.GetString(data, 0, key) != "XML:com.adobe.xmp" || key + 3 > data.Length) break;
                    var compressed = data[key + 1] == 1;
                    var at = key + 3;
                    var language = Array.IndexOf(data, (byte)0, at); if (language < 0) break;
                    var translated = Array.IndexOf(data, (byte)0, language + 1); if (translated < 0) break;
                    var text = data.AsSpan(translated + 1);
                    xmp ??= compressed ? Inflate(text) : text.ToArray();
                    break;
                }
            }
        }
        return new MetadataBlobs(exif, icc, xmp);
    }

    private static MetadataBlobs ReadWebP(Stream stream)
    {
        byte[]? exif = null, icc = null, xmp = null;
        stream.Position = 12;
        Span<byte> header = stackalloc byte[8];
        while (stream.Position + 8 <= Math.Min(stream.Length, MaxScan))
        {
            if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8) break;
            var fourcc = Encoding.ASCII.GetString(header[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var padded = size + (size & 1);
            if (fourcc is "ICCP" or "EXIF" or "XMP ")
            {
                if (size > MaxBlob) break;
                var data = new byte[size];
                if (stream.ReadAtLeast(data, (int)size, throwOnEndOfStream: false) < size) break;
                stream.Position += padded - size;
                if (fourcc == "ICCP") icc ??= data;
                else if (fourcc == "XMP ") xmp ??= data;
                else exif ??= data.AsSpan().StartsWith(ExifHeader) ? data[ExifHeader.Length..] : data;
            }
            else stream.Position += padded;
        }
        return new MetadataBlobs(exif, icc, xmp);
    }

    private static byte[]? Inflate(ReadOnlySpan<byte> zlib)
    {
        try
        {
            using var input = new MemoryStream(zlib.ToArray());
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxBlob) return null;
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// The metadata as it should be written next to rotated/flipped pixels: EXIF Orientation set to 1, the Exif
    /// IFD's pixel dimensions set to the saved size, the IFD1 thumbnail unlinked (it would show the old
    /// orientation), and the XMP tiff:Orientation set to 1. <paramref name="iccOnly"/> keeps just the profile.
    /// </summary>
    public MetadataBlobs ForSavedImage(int width, int height, bool iccOnly = false)
    {
        if (iccOnly) return new MetadataBlobs(null, Icc, null);
        var exif = Exif is null ? null : NormalizeExif(Exif, width, height);
        var xmp = Xmp is null ? null : NormalizeXmp(Xmp);
        return new MetadataBlobs(exif, Icc, xmp);
    }

    /// <summary>Patches a copy of a TIFF-structured EXIF block; null when it is not valid TIFF.</summary>
    public static byte[]? NormalizeExif(byte[] exif, int width, int height)
    {
        if (exif.Length < 8) return null;
        var tiff = (byte[])exif.Clone();
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return null;
        if (U16(tiff, 2, little) != 42) return null;
        var ifd0 = U32(tiff, 4, little);
        if (!PatchIfd(tiff, ifd0, little, width, height, isExifIfd: false, out var next)) return null;
        // Unlink IFD1 (the thumbnail) by zeroing IFD0's next-IFD pointer.
        if (next >= 0) W32(tiff, next, 0, little);
        return tiff;
    }

    private static bool PatchIfd(byte[] tiff, long offset, bool little, int width, int height, bool isExifIfd, out long nextPointer)
    {
        nextPointer = -1;
        if (offset < 8 || offset + 2 > tiff.Length) return false;
        var count = U16(tiff, (int)offset, little);
        var entries = offset + 2;
        if (entries + count * 12L + 4 > tiff.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var e = (int)(entries + i * 12);
            var tag = U16(tiff, e, little);
            var type = U16(tiff, e + 2, little);
            if (!isExifIfd && tag == 274 && type == 3) W16(tiff, e + 8, 1, little);
            else if (!isExifIfd && tag == 34665 && type is 4 or 13)
                PatchIfd(tiff, U32(tiff, e + 8, little), little, width, height, isExifIfd: true, out _);
            else if (isExifIfd && tag is 40962 or 40963)
            {
                var value = tag == 40962 ? width : height;
                if (type == 3 && value <= ushort.MaxValue) { W16(tiff, e + 8, value, little); W16(tiff, e + 10, 0, little); }
                else if (type == 4) W32(tiff, e + 8, (uint)value, little);
            }
        }
        nextPointer = entries + count * 12L;
        return true;
    }

    private static readonly Regex XmpOrientationAttribute = new(@"tiff:Orientation\s*=\s*([""'])\d+\1", RegexOptions.CultureInvariant);
    private static readonly Regex XmpOrientationElement = new(@"<tiff:Orientation>\s*\d+\s*</tiff:Orientation>", RegexOptions.CultureInvariant);

    public static byte[] NormalizeXmp(byte[] xmp)
    {
        var text = Encoding.UTF8.GetString(xmp);
        var updated = XmpOrientationAttribute.Replace(text, m => "tiff:Orientation=" + m.Groups[1].Value + "1" + m.Groups[1].Value);
        updated = XmpOrientationElement.Replace(updated, "<tiff:Orientation>1</tiff:Orientation>");
        return ReferenceEquals(updated, text) || updated == text ? xmp : Encoding.UTF8.GetBytes(updated);
    }

    private static int U16(byte[] b, int o, bool little) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    private static long U32(byte[] b, int o, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static void W16(byte[] b, int o, int v, bool little)
    { if (little) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v); }
    private static void W32(byte[] b, long o, uint v, bool little)
    { if (little) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)o), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan((int)o), v); }
}

/// <summary>Adds metadata chunks to an encoded PNG (eXIf, iTXt XMP and, when none is present, iCCP) before the first IDAT.</summary>
public static class PngMetadataWriter
{
    public static byte[] Insert(byte[] png, MetadataBlobs metadata)
    {
        if (metadata.IsEmpty || png.Length < 8) return png;
        var chunks = new List<(string Type, byte[] Data)>();
        var existing = ListChunkTypes(png);
        if (metadata.Icc is not null && !existing.Contains("iCCP") && !existing.Contains("sRGB"))
        {
            var payload = new MemoryStream();
            payload.Write("ICC Profile\0\0"u8);
            using (var z = new ZLibStream(payload, CompressionLevel.Optimal, leaveOpen: true)) z.Write(metadata.Icc);
            chunks.Add(("iCCP", payload.ToArray()));
        }
        if (metadata.Exif is not null && !existing.Contains("eXIf")) chunks.Add(("eXIf", metadata.Exif));
        if (metadata.Xmp is not null)
        {
            var payload = new MemoryStream();
            payload.Write("XML:com.adobe.xmp\0"u8);
            payload.Write([0, 0, 0, 0]); // uncompressed, method 0, empty language, empty translated keyword
            payload.Write(metadata.Xmp);
            chunks.Add(("iTXt", payload.ToArray()));
        }
        if (chunks.Count == 0) return png;
        var insertAt = FindChunk(png, "IDAT");
        if (insertAt < 0) return png;
        var output = new MemoryStream(png.Length + chunks.Sum(c => c.Data.Length + 12));
        output.Write(png, 0, insertAt);
        foreach (var (type, data) in chunks) WriteChunk(output, type, data);
        output.Write(png, insertAt, png.Length - insertAt);
        return output.ToArray();
    }

    public static HashSet<string> ListChunkTypes(byte[] png)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var at = 8;
        while (at + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            types.Add(Encoding.ASCII.GetString(png, at + 4, 4));
            if (length > int.MaxValue - at - 12) break;
            at += 12 + (int)length;
        }
        return types;
    }

    private static int FindChunk(byte[] png, string wanted)
    {
        var at = 8;
        while (at + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (Encoding.ASCII.GetString(png, at + 4, 4) == wanted) return at;
            if (length > int.MaxValue - at - 12) break;
            at += 12 + (int)length;
        }
        return -1;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        output.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}

/// <summary>WebP RIFF helpers: lossless detection and the extended (VP8X) container with ICC/EXIF/XMP chunks.</summary>
public static class WebPContainer
{
    /// <summary>True when <paramref name="path"/> is a still WebP whose image is VP8L (lossless).</summary>
    public static bool IsLossless(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8))
                return false;
            Span<byte> chunk = stackalloc byte[8];
            for (var i = 0; i < 64 && stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8; i++)
            {
                var fourcc = Encoding.ASCII.GetString(chunk[..4]);
                if (fourcc == "VP8L") return true;
                if (fourcc is "VP8 " or "ANIM") return false;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                stream.Position += size + (size & 1);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// Rewrites a simple or extended WebP so it carries <paramref name="metadata"/>: VP8X (with the ICC/EXIF/XMP
    /// and alpha flags), ICCP, the image chunks, EXIF and "XMP " (the order the WebP container spec requires).
    /// </summary>
    public static byte[] AddMetadata(byte[] webp, MetadataBlobs metadata, int width, int height)
    {
        if (metadata.IsEmpty || webp.Length < 20 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return webp;
        var imageChunks = new List<(string FourCC, byte[] Data)>();
        var alpha = false;
        var at = 12;
        while (at + 8 <= webp.Length)
        {
            var fourcc = Encoding.ASCII.GetString(webp, at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            if (size < 0 || at + 8 + size > webp.Length) return webp;
            var data = webp.AsSpan(at + 8, size).ToArray();
            switch (fourcc)
            {
                case "VP8X": alpha |= (data[0] & 0x10) != 0; break;
                case "ALPH": alpha = true; imageChunks.Add((fourcc, data)); break;
                case "VP8L":
                    // VP8L header: signature 0x2f, then 14+14 bits of size and one alpha_is_used bit.
                    if (data.Length >= 5) alpha |= ((data[4] >> 4) & 1) != 0;
                    imageChunks.Add((fourcc, data));
                    break;
                case "VP8 ": imageChunks.Add((fourcc, data)); break;
                case "ICCP" or "EXIF" or "XMP ": break; // replaced below
                default: return webp; // animation or unknown chunks: leave the encoder's output untouched
            }
            at += 8 + size + (size & 1);
        }
        if (imageChunks.Count == 0) return webp;
        var flags = (byte)((metadata.Icc is not null ? 0x20 : 0) | (alpha ? 0x10 : 0) | (metadata.Exif is not null ? 0x08 : 0) | (metadata.Xmp is not null ? 0x04 : 0));
        var vp8x = new byte[10];
        vp8x[0] = flags;
        WriteU24(vp8x, 4, width - 1);
        WriteU24(vp8x, 7, height - 1);
        var output = new MemoryStream(webp.Length + 64 + (metadata.Icc?.Length ?? 0) + (metadata.Exif?.Length ?? 0) + (metadata.Xmp?.Length ?? 0));
        output.Write("RIFF\0\0\0\0WEBP"u8);
        Chunk(output, "VP8X", vp8x);
        if (metadata.Icc is not null) Chunk(output, "ICCP", metadata.Icc);
        foreach (var (fourcc, data) in imageChunks) Chunk(output, fourcc, data);
        if (metadata.Exif is not null) Chunk(output, "EXIF", metadata.Exif);
        if (metadata.Xmp is not null) Chunk(output, "XMP ", metadata.Xmp);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteU24(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); }

    private static void Chunk(Stream output, string fourcc, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(fourcc));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        output.Write(size);
        output.Write(data);
        if ((data.Length & 1) != 0) output.WriteByte(0);
    }
}
