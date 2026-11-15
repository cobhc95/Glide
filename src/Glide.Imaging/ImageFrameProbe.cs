using System.Buffers.Binary;

namespace Glide.Imaging;

/// <summary>
/// Cheap page/frame count for a file, used before Save writes over it: Glide's encoder writes a single frame,
/// so saving a multipage TIFF, an animated GIF/PNG/WebP or a multi-size icon in place would silently drop the
/// rest. Combines the native WIC probe (any format WIC can open, including HEIF sequences) with a bounded
/// managed container walk (TIFF IFD chain, GIF image blocks, APNG acTL, WebP ANMF, ICO directory) that also
/// works when the native bridge is missing or older. Reads headers and block lengths only, never pixels.
/// </summary>
public static class ImageFrameProbe
{
    /// <summary>Upper bound on what is counted; hostile files can't make the walk run long.</summary>
    internal const int MaxCount = 100_000;

    /// <summary>Test seam: replaces the native WIC probe (return null for "no native answer").</summary>
    internal static Func<string, int?>? NativeCountOverrideForTests { get; set; }

    /// <summary>Number of pages/frames in <paramref name="path"/>; 1 when the file is single-frame or unknown.</summary>
    public static int CountFrames(string path)
    {
        var managed = 0;
        try { managed = CountFramesManaged(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        var native = 0;
        try { native = NativeCount(path) ?? 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Math.Max(1, Math.Max(managed, native));
    }

    private static int? NativeCount(string path)
    {
        if (NativeCountOverrideForTests is { } seam) return seam(path);
        return NativeImageProbe.TryProbe(path, out var info) ? (int)Math.Min(info.FrameCount, (uint)MaxCount) : null;
    }

    /// <summary>Managed container walk only; 0 when the format isn't one it understands.</summary>
    public static int CountFramesManaged(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        return CountFrames(stream);
    }

    /// <summary>Managed container walk over a seekable stream; 0 when the format isn't one it understands.</summary>
    public static int CountFrames(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead) return 0;
        Span<byte> head = stackalloc byte[16];
        stream.Position = 0;
        if (!ReadExactly(stream, head)) return 0;

        if (head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0) return CountTiffPages(stream, little: true);
        if (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42) return CountTiffPages(stream, little: false);
        if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && head[3] == '8') return CountGifFrames(stream);
        if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G') return CountPngFrames(stream);
        if (head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return CountWebpFrames(stream);
        if (head[0] == 0 && head[1] == 0 && head[2] is 1 or 2 && head[3] == 0 && head[9] == 0) // ICO/CUR: first entry's reserved byte is 0
        {
            var images = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
            return images is > 0 and < 1024 ? images : 0;
        }
        return 0;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    private static int CountTiffPages(Stream stream, bool little)
    {
        Span<byte> buffer = stackalloc byte[4];
        uint U32(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        ushort U16(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

        stream.Position = 4;
        if (!ReadExactly(stream, buffer)) return 0;
        long offset = U32(buffer);
        var seen = new HashSet<long>();
        var pages = 0;
        while (offset >= 8 && pages < MaxCount && seen.Add(offset) && offset + 2 <= stream.Length)
        {
            stream.Position = offset;
            if (!ReadExactly(stream, buffer[..2])) break;
            pages++;
            var next = offset + 2 + 12L * U16(buffer[..2]);
            if (next + 4 > stream.Length) break;
            stream.Position = next;
            if (!ReadExactly(stream, buffer)) break;
            offset = U32(buffer);
        }
        return pages;
    }

    private static int CountGifFrames(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[9];
        stream.Position = 10;
        var packed = stream.ReadByte();
        if (packed < 0) return 0;
        stream.Position = 13 + ((packed & 0x80) != 0 ? 3 * (1 << ((packed & 7) + 1)) : 0);
        var frames = 0;
        while (frames < MaxCount)
        {
            var marker = stream.ReadByte();
            if (marker is < 0 or 0x3B) break;
            if (marker == 0x2C)
            {
                if (!ReadExactly(stream, buffer)) break;
                var imagePacked = buffer[8];
                if ((imagePacked & 0x80) != 0) stream.Seek(3 * (1 << ((imagePacked & 7) + 1)), SeekOrigin.Current);
                if (stream.ReadByte() < 0) break; // LZW minimum code size
                if (!SkipGifSubBlocks(stream)) { frames++; break; }
                frames++;
            }
            else if (marker == 0x21)
            {
                if (stream.ReadByte() < 0 || !SkipGifSubBlocks(stream)) break;
            }
            else break;
        }
        return frames;
    }

    private static bool SkipGifSubBlocks(Stream stream)
    {
        while (true)
        {
            var length = stream.ReadByte();
            if (length < 0) return false;
            if (length == 0) return true;
            if (stream.Position + length > stream.Length) return false;
            stream.Seek(length, SeekOrigin.Current);
        }
    }

    private static int CountPngFrames(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.Position = 8;
        for (var chunks = 0; chunks < 10_000; chunks++)
        {
            if (!ReadExactly(stream, header[..8])) return 1;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header[4..8];
            if (type.SequenceEqual("acTL"u8))
            {
                if (length < 8 || !ReadExactly(stream, header[..4])) return 1;
                var frames = BinaryPrimitives.ReadUInt32BigEndian(header);
                return (int)Math.Clamp(frames, 1u, (uint)MaxCount);
            }
            // acTL must come before the image data: no acTL by then means a still PNG.
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return 1;
            var skip = (long)length + 4;
            if (stream.Position + skip > stream.Length) return 1;
            stream.Seek(skip, SeekOrigin.Current);
        }
        return 1;
    }

    private static int CountWebpFrames(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position = 12;
        var frames = 0;
        for (var chunks = 0; chunks < MaxCount * 2 && frames < MaxCount; chunks++)
        {
            if (!ReadExactly(stream, header)) break;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (header[..4].SequenceEqual("ANMF"u8)) frames++;
            var skip = (long)size + (size & 1);
            if (stream.Position + skip > stream.Length) break;
            stream.Seek(skip, SeekOrigin.Current);
        }
        return Math.Max(1, frames);
    }
}
