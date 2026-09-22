using System.Buffers;
using System.Buffers.Binary;

namespace Glide.Imaging;

/// <summary>Allocation-light common-format dimension probe used before native/WIC fallback.</summary>
public static class ImageHeaderProbe
{
    private const int MaxHeaderBytes = 256 * 1024;
    private const int ProbeChunkBytes = 32 * 1024;

    public static bool TryProbe(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        // Zero-allocation stackalloc probe for common image formats (JPEG, PNG, GIF, BMP, WebP)
        Span<byte> header = stackalloc byte[4096];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.SequentialScan);
            var read = stream.Read(header);
            if (read >= 10 && TryProbe(header[..read], out dimensions)) return true;

            if (read == 4096)
            {
                byte[]? rented = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
                try
                {
                    header.CopyTo(rented);
                    var total = read;
                    while (total < MaxHeaderBytes)
                    {
                        var chunk = stream.Read(rented, total, Math.Min(ProbeChunkBytes, MaxHeaderBytes - total));
                        if (chunk <= 0) break;
                        total += chunk;
                        if (TryProbe(rented.AsSpan(0, total), out dimensions)) return true;
                    }
                    return TryProbe(rented.AsSpan(0, total), out dimensions);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Probes the already-open decode stream and restores its original position. This removes the
    /// common-path second file open that Glide 2.1 performed for dimensions before decoding.
    /// </summary>
    public static async ValueTask<ImageDimensions?> TryProbeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead || !stream.CanSeek) return null;
        var origin = stream.Position;
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(ProbeChunkBytes);
            var read = await stream.ReadAsync(rented.AsMemory(0, ProbeChunkBytes), cancellationToken).ConfigureAwait(false);
            if (read >= 10 && TryProbe(rented.AsSpan(0, read), out var quick))
                return quick;

            if (read == ProbeChunkBytes)
            {
                var larger = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
                try
                {
                    Array.Copy(rented, 0, larger, 0, read);
                    var total = read;
                    while (total < MaxHeaderBytes)
                    {
                        var chunk = await stream.ReadAsync(larger.AsMemory(total, Math.Min(ProbeChunkBytes, MaxHeaderBytes - total)), cancellationToken).ConfigureAwait(false);
                        if (chunk <= 0) break;
                        total += chunk;
                        if (TryProbe(larger.AsSpan(0, total), out var dimensions)) return dimensions;
                    }
                    return TryProbe(larger.AsSpan(0, total), out var final) ? final : null;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(larger);
                }
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            try { stream.Position = origin; } catch { }
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions d)
    {
        d = default;
        if (data.Length < 10) return false;

        // PNG
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }))
            return Set(BinaryPrimitives.ReadInt32BigEndian(data.Slice(16,4)), BinaryPrimitives.ReadInt32BigEndian(data.Slice(20,4)), out d);
        // GIF
        if (data.Length >= 10 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            return Set(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6,2)), BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8,2)), out d);
        // BMP
        if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            var w = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18,4));
            var h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22,4)));
            return Set(w, h, out d);
        }
        // ICO/CUR. The directory count (bytes 4-5) must be >= 1: without it, any header-less format
        // whose first bytes happen to be 00 00 01/02 00 (notably an uncompressed true-colour TGA,
        // which starts 00 00 02 00) would be mis-identified as a 256x256 cursor.
        if (data.Length >= 8 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || data[2] == 2) && data[3] == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)) >= 1)
            return Set(data[6] == 0 ? 256 : data[6], data[7] == 0 ? 256 : data[7], out d);
        // JPEG SOF markers
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 8 < data.Length)
            {
                while (i < data.Length && data[i] != 0xFF) i++;
                while (i < data.Length && data[i] == 0xFF) i++;
                if (i >= data.Length) break;
                var marker = data[i++];
                if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
                if (i + 2 > data.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i,2));
                if (length < 2 || i + length > data.Length) break;
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    if (length >= 7)
                        return Set(BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5,2)), BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 3,2)), out d);
                }
                i += length;
            }
        }
        // TIFF classic IFD, little or big endian.
        if (data.Length >= 16 && ((data[0] == (byte)'I' && data[1] == (byte)'I') || (data[0] == (byte)'M' && data[1] == (byte)'M')))
        {
            var le = data[0] == (byte)'I';
            if (ReadU16(data, 2, le) == 42)
            {
                var off = (int)ReadU32(data, 4, le);
                if (off >= 0 && off + 2 <= data.Length)
                {
                    var count = ReadU16(data, off, le); off += 2;
                    int w = 0, h = 0;
                    for (var n = 0; n < count && off + 12 <= data.Length; n++, off += 12)
                    {
                        var tag = ReadU16(data, off, le); var type = ReadU16(data, off + 2, le); var cnt = ReadU32(data, off + 4, le);
                        if (cnt != 1 || (tag != 256 && tag != 257)) continue;
                        var val = type == 3 ? ReadU16(data, off + 8, le) : type == 4 ? (int)ReadU32(data, off + 8, le) : 0;
                        if (tag == 256) w = val; else h = val;
                        if (w > 0 && h > 0) return Set(w, h, out d);
                    }
                }
            }
        }
        // WebP VP8X / VP8L / lossy VP8.
        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8,4).SequenceEqual("WEBP"u8))
        {
            var kind = data.Slice(12,4);
            if (kind.SequenceEqual("VP8X"u8) && data.Length >= 30)
            {
                var w = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
                var h = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
                return Set(w, h, out d);
            }
            if (kind.SequenceEqual("VP8L"u8) && data.Length >= 25 && data[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21,4));
                return Set((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, out d);
            }
            if (kind.SequenceEqual("VP8 "u8) && data.Length >= 30 && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
                return Set(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26,2)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28,2)) & 0x3FFF, out d);
        }
        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    public const int MaxDimension = 65_535;
    public const long MaxTotalPixels = 256_000_000L; // 256 Megapixels (~1.0 GB uncompressed in 32bpp)

    private static bool Set(int width, int height, out ImageDimensions d)
    {
        d = new(width, height);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            return false;
        var pixels = (long)width * height;
        return pixels <= MaxTotalPixels;
    }
}
