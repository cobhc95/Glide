using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

/// <summary>
/// Shared plumbing for the bundled managed decoders (camera RAW previews, EXR, scientific/medical,
/// layered-editor and GPU texture formats). Nothing here runs at startup: every member is a plain
/// static method or a lazily created table, touched only when one of those formats is opened.
/// Every helper is total — malformed input yields <c>false</c>, never an exception to the caller.
/// </summary>
internal static partial class ManagedCodecSupport
{
    /// <summary>Hard cap for any single decompression (zlib/gzip/RLE) performed by a managed decoder.</summary>
    public const long MaxDecompressedBytes = 1L << 30;

    public static bool IsValidDimensions(long width, long height)
        => width > 0 && height > 0 && width <= DecodeMemoryGuard.MaxDimension && height <= DecodeMemoryGuard.MaxDimension &&
           width * height <= DecodeMemoryGuard.MaxTotalPixels;

    /// <summary>True when a straight BGRA frame of this size (plus the bitmap copy) fits memory now.</summary>
    public static bool FitsMemory(long width, long height)
        => IsValidDimensions(width, height) &&
           DecodeMemoryGuard.FitsAvailableMemory(DecodeMemoryGuard.EstimatedFrameBytes(new ImageDimensions((int)width, (int)height)) * 2,
               DecodeMemoryGuard.GetSnapshot());

    /// <summary>
    /// Worker cap for the intra-image parallel loops below. Mirrors the bundled native providers
    /// (OpenJPEG/libjxl/libheif use clamp(cores, 1, 8)) so a decode never claims the whole machine while
    /// prefetch lanes are decoding neighbours.
    /// </summary>
    public static int DecodeWorkers => Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>
    /// Runs <paramref name="body"/>(start, end) over contiguous bands of [0, <paramref name="count"/>).
    /// Small jobs (under ~256K units of work) run inline on the caller's thread. Bands write disjoint
    /// rows/blocks, so results are identical to the serial loop. Exceptions surface as
    /// <see cref="AggregateException"/>, which every managed decoder's catch-all already turns into "false".
    /// </summary>
    public static void ForBands(int count, long workPerItem, Action<int, int> body)
    {
        if (count <= 0) return;
        var workers = DecodeWorkers;
        if (workers <= 1 || count < 2 || count * Math.Max(1, workPerItem) < (1L << 18))
        {
            body(0, count);
            return;
        }
        var bands = Math.Min(count, workers * 4);
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = workers }, band =>
        {
            var start = (int)((long)count * band / bands);
            var end = (int)((long)count * (band + 1) / bands);
            if (start < end) body(start, end);
        });
    }

    public static bool TryAllocatePixels(int width, int height, out byte[] pixels)
    {
        pixels = Array.Empty<byte>();
        if (!IsValidDimensions(width, height)) return false;
        try
        {
            pixels = new byte[checked((long)width * height * 4)];
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Compression
    // ---------------------------------------------------------------------------------------------

    public enum Wrapper { Zlib, Deflate, Gzip }

    /// <summary>Inflates into <paramref name="destination"/>; returns the number of bytes produced.</summary>
    public static unsafe int Inflate(ReadOnlySpan<byte> source, Span<byte> destination, Wrapper wrapper)
    {
        if (source.IsEmpty || destination.IsEmpty) return 0;
        try
        {
            fixed (byte* pointer = source)
            {
                using var input = new UnmanagedMemoryStream(pointer, source.Length);
                using Stream z = wrapper switch
                {
                    Wrapper.Zlib => new ZLibStream(input, CompressionMode.Decompress),
                    Wrapper.Gzip => new GZipStream(input, CompressionMode.Decompress),
                    _ => new DeflateStream(input, CompressionMode.Decompress),
                };
                return ReadFully(z, destination);
            }
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Inflates exactly <paramref name="expected"/> bytes.</summary>
    public static bool TryInflateExact(ReadOnlySpan<byte> source, int expected, Wrapper wrapper, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (expected <= 0 || expected > MaxDecompressedBytes) return false;
        try { result = new byte[expected]; }
        catch { return false; }
        return Inflate(source, result, wrapper) == expected;
    }

    /// <summary>Decompresses a whole gzip member of unknown size, capped at <paramref name="maxBytes"/>.</summary>
    public static unsafe bool TryGunzip(ReadOnlySpan<byte> source, long maxBytes, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (source.Length < 18 || source[0] != 0x1F || source[1] != 0x8B) return false;
        try
        {
            // ISIZE (uncompressed size mod 2^32) is only a capacity hint: it is attacker-controlled.
            var hint = BinaryPrimitives.ReadUInt32LittleEndian(source[^4..]);
            var capacity = (int)Math.Clamp(hint, 4096u, (uint)Math.Min(maxBytes, 256L * 1024 * 1024));
            fixed (byte* pointer = source)
            {
                using var input = new UnmanagedMemoryStream(pointer, source.Length);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(capacity);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes) return false;
                    output.Write(buffer, 0, read);
                }
                if (total == 0) return false;
                result = output.ToArray();
                return true;
            }
        }
        catch
        {
            result = Array.Empty<byte>();
            return false;
        }
    }

    public static int ReadFully(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    public static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

    // Encoded (JPEG/PNG) sub-images: TryProbeEncoded / TryDecodeEncoded live in ManagedCodecSupport.Skia.cs
    // (the viewer, bundled Skia codecs) or ManagedCodecSupport.Host.cs in src/Glide.ThumbnailCodecs (the
    // Explorer thumbnail library, which asks its native host to decode through WIC).

    // ---------------------------------------------------------------------------------------------
    // Orientation
    // ---------------------------------------------------------------------------------------------

    public static bool SwapsAxes(int orientation) => orientation is 5 or 6 or 7 or 8;

    /// <summary>Applies an EXIF orientation (1..8) to a straight BGRA frame.</summary>
    public static DecodedImage ApplyOrientation(DecodedImage source, int orientation)
    {
        if (orientation is < 2 or > 8 || source.Bgra is null) return source;
        int w = source.Width, h = source.Height;
        var swap = SwapsAxes(orientation);
        var dw = swap ? h : w;
        var dh = swap ? w : h;
        var src = source.Bgra;
        var dst = new byte[src.Length];
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = w - 1 - x; sy = y; break;
                    case 3: sx = w - 1 - x; sy = h - 1 - y; break;
                    case 4: sx = x; sy = h - 1 - y; break;
                    case 5: sx = y; sy = x; break;
                    case 6: sx = y; sy = h - 1 - x; break;
                    case 7: sx = w - 1 - y; sy = h - 1 - x; break;
                    default: sx = w - 1 - y; sy = x; break; // 8
                }
                var s = ((long)sy * w + sx) * 4;
                var d = ((long)y * dw + x) * 4;
                dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
            }
        }
        return new DecodedImage(dw, dh, dst);
    }

    public static void FlipVertical(byte[] buffer, int width, int height)
    {
        var rowBytes = width * 4;
        var temp = new byte[rowBytes];
        for (var y = 0; y < height / 2; y++)
        {
            var top = (long)y * rowBytes;
            var bottom = (long)(height - 1 - y) * rowBytes;
            Array.Copy(buffer, top, temp, 0, rowBytes);
            Array.Copy(buffer, bottom, buffer, top, rowBytes);
            Array.Copy(temp, 0, buffer, bottom, rowBytes);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // HDR tone mapping (EXR, PFM, float textures)
    // ---------------------------------------------------------------------------------------------

    private static class SrgbTable
    {
        // 4096-entry linear -> sRGB table over [0, 1]; built on first use only.
        public static readonly byte[] Values = Build();

        private static byte[] Build()
        {
            var table = new byte[4097];
            for (var i = 0; i <= 4096; i++)
            {
                var v = i / 4096.0;
                v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
                table[i] = (byte)Math.Clamp((int)(v * 255.0 + 0.5), 0, 255);
            }
            return table;
        }
    }

    /// <summary>Scene-linear float to display sRGB 8-bit: clamp to [0,1] then apply the sRGB transfer curve.</summary>
    public static byte LinearToSrgb8(float value)
    {
        if (!(value > 0f)) return 0;
        if (value >= 1f) return 255;
        return SrgbTable.Values[(int)(value * 4096f + 0.5f)];
    }

    public static byte UnitToByte(float value)
    {
        if (!(value > 0f)) return 0;
        if (value >= 1f) return 255;
        return (byte)(value * 255f + 0.5f);
    }

    /// <summary>Writes one straight BGRA pixel from scene-linear premultiplied-or-straight RGBA floats.</summary>
    public static void WriteLinearPixel(byte[] output, long pixel, float r, float g, float b, float a, bool premultiplied)
    {
        if (float.IsNaN(a)) a = 1f;
        a = Math.Clamp(a, 0f, 1f);
        if (premultiplied && a > 0f && a < 1f)
        {
            r /= a; g /= a; b /= a;
        }
        var o = pixel * 4;
        output[o] = LinearToSrgb8(b);
        output[o + 1] = LinearToSrgb8(g);
        output[o + 2] = LinearToSrgb8(r);
        output[o + 3] = UnitToByte(a);
    }

    // ---------------------------------------------------------------------------------------------
    // Random-access file reading (RAW containers are parsed without reading the whole file)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Bounded random-access reader over an open file. The first megabyte is cached because nearly all
    /// container directories live there; other reads go straight to <see cref="RandomAccess"/>.
    /// </summary>
    public sealed class FileSource : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly byte[] _head;
        public long Length { get; }

        private FileSource(SafeFileHandle handle, long length, byte[] head)
        {
            _handle = handle;
            Length = length;
            _head = head;
        }

        public static FileSource? Open(string path)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = RandomAccess.GetLength(handle);
                if (length <= 0) { handle.Dispose(); return null; }
                var head = new byte[(int)Math.Min(length, 1 << 20)];
                var read = 0;
                while (read < head.Length)
                {
                    var n = RandomAccess.Read(handle, head.AsSpan(read), read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != head.Length) { handle.Dispose(); return null; }
                return new FileSource(handle, length, head);
            }
            catch
            {
                handle?.Dispose();
                return null;
            }
        }

        public ReadOnlySpan<byte> Head => _head;

        /// <summary>Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="offset"/>.</summary>
        public bool TryRead(long offset, Span<byte> destination)
        {
            if (offset < 0 || destination.Length == 0 || offset + destination.Length > Length) return destination.Length == 0 && offset >= 0;
            if (offset + destination.Length <= _head.Length)
            {
                _head.AsSpan((int)offset, destination.Length).CopyTo(destination);
                return true;
            }
            try
            {
                var total = 0;
                while (total < destination.Length)
                {
                    var n = RandomAccess.Read(_handle, destination[total..], offset + total);
                    if (n <= 0) return false;
                    total += n;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public byte[]? ReadBytes(long offset, long count)
        {
            if (count <= 0 || count > int.MaxValue || offset < 0 || offset + count > Length) return null;
            try
            {
                var buffer = new byte[count];
                return TryRead(offset, buffer) ? buffer : null;
            }
            catch
            {
                return null;
            }
        }

        public bool TryU16(long offset, bool littleEndian, out int value)
        {
            Span<byte> b = stackalloc byte[2];
            value = 0;
            if (!TryRead(offset, b)) return false;
            value = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
            return true;
        }

        public bool TryU32(long offset, bool littleEndian, out uint value)
        {
            Span<byte> b = stackalloc byte[4];
            value = 0;
            if (!TryRead(offset, b)) return false;
            value = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
            return true;
        }

        public void Dispose() => _handle.Dispose();
    }
}
