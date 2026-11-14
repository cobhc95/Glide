using System.Runtime.InteropServices;

namespace Glide.Imaging;

/// <summary>
/// Decoded-pixel safety limits shared by every full-resolution decode path (header probe, foreground
/// loader, neighbour preparation, startup preloader, Skia fallback, bundled raster decoders).
///
/// Two independent checks:
/// <list type="bullet">
/// <item><b>Pixel cap</b>: a frame larger than <see cref="MaxTotalPixels"/> (512 MP, about 2 GB as 32-bit
/// BGRA) or wider/taller than <see cref="MaxDimension"/> is never decoded at full resolution; it can still be
/// shown through a decoder-scaled preview.</item>
/// <item><b>Memory guard</b>: a frame larger than <see cref="AlwaysAllowedBytes"/> is decoded at full
/// resolution only when it needs at most <see cref="MaxShareOfAvailableMemory"/> of the physical memory
/// (and commit) available right now. Otherwise the preview is kept, so opening a 512 MP image on a PC
/// with little free memory cannot push Windows into heavy paging or fail with an out-of-memory crash.</item>
/// </list>
/// </summary>
public static partial class DecodeMemoryGuard
{
    /// <summary>Largest frame decoded at full resolution: 512 megapixels (~2.05 GB as 32-bit BGRA).</summary>
    public const long MaxTotalPixels = 512_000_000L;

    /// <summary>Largest width or height decoded at full resolution.</summary>
    public const int MaxDimension = 65_535;

    /// <summary>Frames up to this size (256 MiB, ~67 MP) are never refused by the memory guard.</summary>
    public const long AlwaysAllowedBytes = 256L * 1024 * 1024;

    /// <summary>A full decode may use at most this share of the memory that is available right now.</summary>
    public const double MaxShareOfAvailableMemory = 0.5;

    /// <summary>Free physical memory and the commit the process can still allocate, in bytes.</summary>
    public readonly record struct MemorySnapshot(ulong AvailablePhysicalBytes, ulong AvailableCommitBytes);

    // Flows with the async context (into Task.Run), so a test's synthetic snapshot never leaks into
    // tests running in parallel.
    private static readonly AsyncLocal<Func<MemorySnapshot?>?> SnapshotOverride = new();

    /// <summary>Tests: replaces the live memory query for the current async flow (null result = "unknown").</summary>
    internal static Func<MemorySnapshot?>? SnapshotOverrideForTests
    {
        get => SnapshotOverride.Value;
        set => SnapshotOverride.Value = value;
    }

    /// <summary>Bytes of a full-resolution 32-bit frame of <paramref name="dimensions"/> (0 when unknown).</summary>
    public static long EstimatedFrameBytes(ImageDimensions dimensions)
        => dimensions.IsValid ? (long)dimensions.Width * dimensions.Height * 4L : 0;

    /// <summary>True when the frame is beyond the fixed pixel cap (65,535 px per side or 512 MP).</summary>
    public static bool ExceedsPixelCap(ImageDimensions dimensions)
        => dimensions.IsValid &&
           (dimensions.Width > MaxDimension || dimensions.Height > MaxDimension ||
            (long)dimensions.Width * dimensions.Height > MaxTotalPixels);

    /// <summary>True when a full-resolution frame of this size fits the memory available right now.</summary>
    public static bool FitsAvailableMemory(ImageDimensions dimensions)
        => FitsAvailableMemory(EstimatedFrameBytes(dimensions), GetSnapshot());

    /// <summary>Pure form of <see cref="FitsAvailableMemory(ImageDimensions)"/> (tests use synthetic snapshots).</summary>
    public static bool FitsAvailableMemory(long frameBytes, MemorySnapshot? snapshot)
    {
        if (frameBytes <= AlwaysAllowedBytes) return true;
        // Unknown (query failed): the pixel cap and the allocation-failure fallback still bound the decode.
        if (snapshot is not { } s || s.AvailablePhysicalBytes == 0) return true;
        var available = s.AvailableCommitBytes == 0 ? s.AvailablePhysicalBytes : Math.Min(s.AvailablePhysicalBytes, s.AvailableCommitBytes);
        return frameBytes <= available * MaxShareOfAvailableMemory;
    }

    /// <summary>True when a full-resolution decode of this frame is allowed (pixel cap and memory guard).</summary>
    public static bool AllowsFullDecode(ImageDimensions dimensions)
        => dimensions.IsValid && !ExceedsPixelCap(dimensions) && FitsAvailableMemory(dimensions);

    /// <summary>True for exceptions that mean an allocation failed (the caller then keeps or decodes a preview).</summary>
    public static bool IsAllocationFailure(Exception exception)
        => exception is OutOfMemoryException ||
           (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1 && IsAllocationFailure(aggregate.InnerExceptions[0]));

    public static MemorySnapshot? GetSnapshot()
    {
        var overrideSnapshot = SnapshotOverrideForTests;
        if (overrideSnapshot is not null) return overrideSnapshot();
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return null;
            return new MemorySnapshot(status.AvailablePhysical, status.AvailablePageFile);
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>
/// Size check for the managed Skia decoder (<c>new Bitmap(stream)</c>, <c>Bitmap.DecodeToWidth</c>), which
/// allocates its output before decoding. When no probe could read an image's size the loader could not
/// apply the pixel cap, so the size is read from Skia's own codec header first (header only, no pixels).
/// </summary>
internal static class SkiaDecodeGuard
{
    private sealed class RefusedException(string message) : IOException(message);

    /// <summary>True for the refusals thrown by <see cref="EnsureAllowed"/> (callers must not retry another full decoder).</summary>
    public static bool IsRefusal(Exception exception) => exception is RefusedException or InsufficientMemoryException;

    /// <summary>
    /// Throws when the frame Skia would allocate (full size, or the codec's scaled size for
    /// <paramref name="longestSide"/> &gt; 0) is beyond the pixel cap or does not fit the free memory.
    /// Leaves the stream position unchanged. Unknown formats pass (Skia then fails on its own).
    /// </summary>
    public static void EnsureAllowed(Stream stream, int longestSide)
    {
        if (!TryReadOutputSize(stream, longestSide, out var size)) return;
        if (DecodeMemoryGuard.ExceedsPixelCap(size))
            throw new RefusedException($"Image dimensions ({size.Width}x{size.Height}) exceed the safe decode limit of {DecodeMemoryGuard.MaxTotalPixels:N0} pixels.");
        if (!DecodeMemoryGuard.FitsAvailableMemory(size))
            throw new InsufficientMemoryException($"There is not enough free memory to decode this {size.Width}x{size.Height} image.");
    }

    internal static bool TryReadOutputSize(Stream stream, int longestSide, out ImageDimensions size)
    {
        size = default;
        if (!stream.CanSeek || !stream.CanRead) return false;
        var start = stream.Position;
        try
        {
            // disposeManagedStream: false - the codec owns the wrapper, never the caller's stream.
            var wrapper = new SkiaSharp.SKManagedStream(stream, false);
            using var codec = SkiaSharp.SKCodec.Create(wrapper);
            if (codec is null) return false;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return false;
            size = new ImageDimensions(info.Width, info.Height);
            var longest = Math.Max(info.Width, info.Height);
            if (longestSide > 0 && longestSide < longest)
            {
                var scaled = codec.GetScaledDimensions((float)longestSide / longest);
                if (scaled.Width > 0 && scaled.Height > 0) size = new ImageDimensions(scaled.Width, scaled.Height);
            }
            GC.KeepAlive(wrapper);
            return true;
        }
        catch { return false; }
        finally
        {
            try { stream.Position = start; } catch { }
        }
    }
}
