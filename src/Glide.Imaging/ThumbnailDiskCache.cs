using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Glide.Imaging;

/// <summary>
/// Persistent Explorer-tab thumbnail cache (5.1.7). One small file per (picture path, requested width): the decoded
/// BGRA thumbnail (premultiplied, stored as 24-bit BGR when it has no transparency), keyed by the source's path, size and
/// last-write time, so a changed or replaced picture is a miss and never shows a stale thumbnail. A hit is one file read
/// instead of decoding the picture (a camera JPEG, or a 70 MB TIFF off a slow disk).
/// <list type="bullet">
/// <item>Layout: <c>&lt;root&gt;\&lt;2 hex&gt;\&lt;pathHash&gt;-&lt;width&gt;-&lt;stampHash&gt;.gtc</c>. Storing a new version removes the older versions of the same path and width.</item>
/// <item>Concurrency: files appear atomically (temp file in the same folder, then move), readers share read/delete, every
/// failure is a miss; two Glide processes can share one folder. Writes run on the thread pool, never on a decode lane.</item>
/// <item>Size: <see cref="MaxBytes"/> with least-recently-used trimming to 90 % (a hit refreshes the file's timestamp at most
/// once per <see cref="TouchInterval"/>). The running total is scanned once, then tracked; a trim rescans and so corrects drift
/// from other processes.</item>
/// </list>
/// </summary>
public sealed class ThumbnailDiskCache
{
    public const string FolderName = "thumbnails";
    public const long DefaultMaxBytes = 512L * 1024 * 1024;
    public const long MinMaxBytes = 16L * 1024 * 1024;
    public const long MaxMaxBytes = 16L * 1024 * 1024 * 1024;
    private const string Extension = ".gtc";
    private const uint Magic = 0x31435447; // "GTC1"
    private const int HeaderSize = 64;
    private const int MaxSide = 2048; // larger thumbnails are not cached (a tile is at most a few hundred pixels)
    private const int MaxPendingWrites = 256;

    private static volatile ThumbnailDiskCache? s_current;

    /// <summary>The cache the Explorer thumbnails use, or null while the setting is off.</summary>
    public static ThumbnailDiskCache? Current => s_current;

    /// <summary>Applies the Settings values: on/off, folder and size limit. A changed limit trims at once.</summary>
    public static void Configure(bool enabled, string? directory, long maxBytes)
    {
        var previous = s_current;
        if (!enabled || string.IsNullOrWhiteSpace(directory))
        {
            s_current = null;
            return;
        }
        var full = Path.GetFullPath(directory);
        if (previous is not null && string.Equals(previous.Directory, full, StringComparison.OrdinalIgnoreCase))
        {
            previous.SetMaxBytes(maxBytes);
            return;
        }
        s_current = new ThumbnailDiskCache(full, maxBytes);
    }

    internal static void ConfigureForBench(string? directory, long maxBytes) => Configure(directory is not null, directory, maxBytes);

    public static long ClampMaxBytes(long bytes) => Math.Clamp(bytes, MinMaxBytes, MaxMaxBytes);

    public string Directory { get; }
    private long _maxBytes;
    private long _totalBytes = -1; // -1 = not scanned yet
    private long _unscannedDelta;   // size changes made by writes while the first scan runs
    private int _scanStarted;
    private int _trimming;
    private int _pendingWrites;
    private readonly object _scanLock = new();
    private long _hitsForTests, _missesForTests;

    /// <summary>How old a cache file's timestamp may get before a hit refreshes it (the LRU clock). Tests set it to zero.</summary>
    internal TimeSpan TouchInterval { get; set; } = TimeSpan.FromMinutes(30);

    private readonly bool _clampLimit = true;

    public ThumbnailDiskCache(string directory, long maxBytes)
    {
        Directory = Path.GetFullPath(directory);
        _maxBytes = ClampMaxBytes(maxBytes);
    }

    /// <summary>Tests: a limit below <see cref="MinMaxBytes"/> (a few small thumbnails fill it).</summary>
    internal ThumbnailDiskCache(string directory, long maxBytes, bool clampLimit)
    {
        Directory = Path.GetFullPath(directory);
        _clampLimit = clampLimit;
        _maxBytes = clampLimit ? ClampMaxBytes(maxBytes) : Math.Max(1, maxBytes);
    }

    public long MaxBytes => Interlocked.Read(ref _maxBytes);
    internal long Hits => Interlocked.Read(ref _hitsForTests);
    internal long Misses => Interlocked.Read(ref _missesForTests);

    /// <summary>Changes the limit; a smaller one trims in the background.</summary>
    public void SetMaxBytes(long bytes)
    {
        Interlocked.Exchange(ref _maxBytes, _clampLimit ? ClampMaxBytes(bytes) : Math.Max(1, bytes));
        ScheduleTrim();
    }

    /// <summary>The identity of one cache entry. Take it BEFORE decoding so a picture changed during the decode is a later miss.</summary>
    public readonly struct Stamp
    {
        internal Stamp(string pathHash, byte[] fullPathHash, string stampHash, long length, long ticks, int width)
        {
            PathHash = pathHash; FullPathHash = fullPathHash; StampHash = stampHash; Length = length; Ticks = ticks; Width = width;
        }
        internal string PathHash { get; }
        internal byte[] FullPathHash { get; }
        internal string StampHash { get; }
        internal long Length { get; }
        internal long Ticks { get; }
        internal int Width { get; }
        public bool IsValid => PathHash is not null;
    }

    /// <summary>Reads the picture's size and last-write time. Invalid (never cached) when the file cannot be read.</summary>
    public Stamp StampFor(string sourcePath, int requestedWidth)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists || requestedWidth <= 0) return default;
            var pathKey = info.FullName.ToLowerInvariant();
            var full = SHA256.HashData(Encoding.UTF8.GetBytes(pathKey));
            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            var stampBytes = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{pathKey}|{length}|{ticks}|{requestedWidth}|1")));
            return new Stamp(Convert.ToHexString(full.AsSpan(0, 8)), full.AsSpan(0, 16).ToArray(),
                Convert.ToHexString(stampBytes.AsSpan(0, 8)), length, ticks, requestedWidth);
        }
        catch { return default; }
    }

    internal string FilePathForTests(in Stamp stamp) => FilePath(stamp);

    private string FilePath(in Stamp stamp) =>
        Path.Combine(Directory, stamp.PathHash[..2], $"{stamp.PathHash}-{stamp.Width}-{stamp.StampHash}{Extension}");

    /// <summary>A cached thumbnail: <see cref="Pixels"/> is BGRA, premultiplied, Width * 4 bytes per row. Dispose returns the buffer.</summary>
    public sealed class CachedThumbnail : IDisposable
    {
        private byte[]? _buffer;
        internal CachedThumbnail(byte[] buffer, int width, int height) { _buffer = buffer; Width = width; Height = height; }
        public int Width { get; }
        public int Height { get; }
        public ReadOnlySpan<byte> Pixels => _buffer is null ? default : _buffer.AsSpan(0, checked(Width * Height * 4));
        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The stored thumbnail for this stamp, or null (a miss: no entry, stale, truncated, damaged or unreadable).</summary>
    public CachedThumbnail? TryGet(in Stamp stamp)
    {
        if (!stamp.IsValid) return null;
        var path = FilePath(stamp);
        byte[]? pixels = null;
        byte[]? raw = null;
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                if (stream.Read(header) != HeaderSize) return Miss(path);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 1) return Miss(path);
                var opaque = (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) & 1) != 0;
                var width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
                var height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
                var requested = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
                var payload = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
                var length = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
                if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || requested != stamp.Width ||
                    length != stamp.Length || ticks != stamp.Ticks || !header.Slice(40, 16).SequenceEqual(stamp.FullPathHash)) return Miss(path);
                var expected = checked(width * height * (opaque ? 3 : 4));
                if (payload != expected || stream.Length != HeaderSize + payload) return Miss(path);
                var bgraBytes = width * height * 4;
                pixels = ArrayPool<byte>.Shared.Rent(bgraBytes);
                if (opaque)
                {
                    raw = ArrayPool<byte>.Shared.Rent(payload);
                    stream.ReadExactly(raw.AsSpan(0, payload));
                    ExpandBgrToBgra(raw.AsSpan(0, payload), pixels.AsSpan(0, bgraBytes));
                }
                else stream.ReadExactly(pixels.AsSpan(0, bgraBytes));
                var result = new CachedThumbnail(pixels, width, height);
                pixels = null;
                Interlocked.Increment(ref _hitsForTests);
                TouchIfOld(path);
                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or OverflowException)
        {
            if (ex is not FileNotFoundException and not DirectoryNotFoundException) TryDelete(path);
            Interlocked.Increment(ref _missesForTests);
            return null;
        }
        finally
        {
            if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
            if (raw is not null) ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private CachedThumbnail? Miss(string damagedPath)
    {
        Interlocked.Increment(ref _missesForTests);
        // A header that does not match this stamp under this name means a damaged or foreign file: drop it.
        TryDelete(damagedPath);
        return null;
    }

    private void TouchIfOld(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) >= TouchInterval) File.SetLastWriteTimeUtc(path, now);
        }
        catch { /* the LRU clock is best effort */ }
    }

    /// <summary>
    /// Stores a decoded thumbnail (BGRA premultiplied, <paramref name="stride"/> bytes per row) and returns at once: the file
    /// is written on the thread pool. Over-large, empty or unstamped thumbnails are ignored.
    /// </summary>
    public void Put(in Stamp stamp, int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        if (!stamp.IsValid || width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || stride < width * 4 ||
            bgra.Length < (long)stride * (height - 1) + width * 4) return;
        if (Volatile.Read(ref _pendingWrites) >= MaxPendingWrites) return;
        var copy = ArrayPool<byte>.Shared.Rent(width * height * 4);
        for (var y = 0; y < height; y++) bgra.Slice(y * stride, width * 4).CopyTo(copy.AsSpan(y * width * 4, width * 4));
        Interlocked.Increment(ref _pendingWrites);
        var captured = stamp;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { Write(captured, width, height, copy); }
            catch { /* a failed cache write only means a later miss */ }
            finally
            {
                ArrayPool<byte>.Shared.Return(copy);
                Interlocked.Decrement(ref _pendingWrites);
            }
        }, null);
    }

    /// <summary>Blocks until every queued write has finished (tests and the benchmark; never called on a UI thread).</summary>
    public bool WaitForWrites(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pendingWrites) > 0 || Volatile.Read(ref _scanStarted) != 0)
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(2);
        }
        return true;
    }

    private void Write(Stamp stamp, int width, int height, byte[] bgra)
    {
        EnsureScanned();
        var pixelBytes = width * height * 4;
        var opaque = true;
        for (var i = 3; i < pixelBytes; i += 4)
            if (bgra[i] != 255) { opaque = false; break; }
        var payload = opaque ? width * height * 3 : pixelBytes;
        var finalPath = FilePath(stamp);
        var folder = Path.GetDirectoryName(finalPath)!;
        System.IO.Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)(opaque ? 1 : 0));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), stamp.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), payload);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), stamp.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), stamp.Ticks);
        stamp.FullPathHash.CopyTo(header.AsSpan(40));
        var packed = opaque ? ArrayPool<byte>.Shared.Rent(payload) : null;
        try
        {
            if (opaque) PackBgraToBgr(bgra.AsSpan(0, pixelBytes), packed!.AsSpan(0, payload));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                stream.Write(header);
                stream.Write(opaque ? packed!.AsSpan(0, payload) : bgra.AsSpan(0, pixelBytes));
            }
            try { File.Move(temp, finalPath, overwrite: true); }
            catch { TryDelete(temp); return; }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            if (packed is not null) ArrayPool<byte>.Shared.Return(packed);
        }
        var added = HeaderSize + (long)payload;
        var removed = RemoveOlderVersions(folder, stamp, finalPath);
        AddToTotal(added - removed);
        if (Interlocked.Read(ref _totalBytes) > MaxBytes) ScheduleTrim();
    }

    private static long RemoveOlderVersions(string folder, Stamp stamp, string keep)
    {
        long removed = 0;
        try
        {
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles($"{stamp.PathHash}-{stamp.Width}-*{Extension}"))
            {
                if (string.Equals(old.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                var length = old.Length;
                if (TryDelete(old.FullName)) removed += length;
            }
        }
        catch { /* stale files are trimmed later */ }
        return removed;
    }

    /// <summary>Tests: a pause inside the first scan of the cache folder (a large cache on a slow disk).</summary>
    internal TimeSpan ScanDelayForTests { get; set; }

    /// <summary>
    /// Starts the one-off scan that totals the cache folder, on its own thread-pool item, and returns at once. Writers never wait for
    /// it: until it ends they add their size changes to <see cref="_unscannedDelta"/>, which the scan folds in. (5.2.0: the scan used
    /// to run under a lock on the first writer's thread, so with a large cache every other write of a folder's thumbnails queued
    /// behind it, each holding a thread-pool thread, and the thread pool ran out of threads for the decode lanes.)
    /// </summary>
    private void EnsureScanned()
    {
        if (Interlocked.Read(ref _totalBytes) >= 0) return;
        if (Interlocked.CompareExchange(ref _scanStarted, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                if (ScanDelayForTests > TimeSpan.Zero) Thread.Sleep(ScanDelayForTests);
                long total = 0;
                foreach (var file in EnumerateCacheFiles()) total += file.Length;
                lock (_scanLock)
                {
                    if (Interlocked.Read(ref _totalBytes) < 0) Interlocked.Exchange(ref _totalBytes, total + Interlocked.Exchange(ref _unscannedDelta, 0));
                }
                if (Interlocked.Read(ref _totalBytes) > MaxBytes) ScheduleTrim();
            }
            catch { /* the total stays unknown; the next trim measures it */ }
            finally { Volatile.Write(ref _scanStarted, 0); }
        }, null);
    }

    private void AddToTotal(long delta)
    {
        lock (_scanLock)
        {
            if (Interlocked.Read(ref _totalBytes) >= 0) Interlocked.Add(ref _totalBytes, delta);
            else Interlocked.Add(ref _unscannedDelta, delta);
        }
    }

    private IEnumerable<FileInfo> EnumerateCacheFiles()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory).EnumerateFiles("*" + Extension, SearchOption.AllDirectories);
    }

    private void ScheduleTrim()
    {
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { TrimCore(); }
            catch { /* best effort */ }
            finally { Volatile.Write(ref _trimming, 0); }
        }, null);
    }

    /// <summary>Trims now to 90 % of the limit when over it (oldest timestamps first); returns the bytes left. Tests call it directly.</summary>
    public long Trim()
    {
        while (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0) Thread.Sleep(2);
        try { return TrimCore(); }
        finally { Volatile.Write(ref _trimming, 0); }
    }

    private long TrimCore()
    {
        var files = EnumerateCacheFiles().Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc)).ToList();
        var total = files.Sum(f => f.Length);
        var limit = MaxBytes;
        if (total > limit)
        {
            var target = limit / 10 * 9;
            foreach (var (name, length, _) in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                if (TryDelete(name)) total -= length;
            }
        }
        Interlocked.Exchange(ref _totalBytes, total);
        SweepTemporaryFiles();
        return total;
    }

    private void SweepTemporaryFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var temp in new DirectoryInfo(Directory).EnumerateFiles("*.tmp", SearchOption.AllDirectories))
                if (DateTime.UtcNow - temp.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(temp.FullName);
        }
        catch { /* best effort */ }
    }

    /// <summary>Files and bytes currently stored (scans the folder).</summary>
    public (int Files, long Bytes) Measure()
    {
        var count = 0;
        long bytes = 0;
        try { foreach (var f in EnumerateCacheFiles()) { count++; bytes += f.Length; } }
        catch { /* unreadable folder: report what was seen */ }
        return (count, bytes);
    }

    /// <summary>Deletes every cached thumbnail. Returns how many files were removed.</summary>
    public int Clear()
    {
        WaitForWrites(TimeSpan.FromSeconds(5));
        var removed = ClearDirectory(Directory);
        Interlocked.Exchange(ref _totalBytes, 0);
        return removed;
    }

    /// <summary>Deletes the cache files (and the then-empty shard folders) under <paramref name="directory"/>; other files are never touched.</summary>
    public static int ClearDirectory(string directory)
    {
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(directory)) return 0;
            var root = new DirectoryInfo(directory);
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (!file.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase) && !file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryDelete(file.FullName)) removed++;
            }
            foreach (var shard in root.EnumerateDirectories())
            {
                try { if (!shard.EnumerateFileSystemInfos().Any()) shard.Delete(); } catch { /* in use by a writer */ }
            }
        }
        catch { /* best effort */ }
        return removed;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    private static void PackBgraToBgr(ReadOnlySpan<byte> bgra, Span<byte> bgr)
    {
        for (int s = 0, d = 0; s < bgra.Length; s += 4, d += 3)
        {
            bgr[d] = bgra[s]; bgr[d + 1] = bgra[s + 1]; bgr[d + 2] = bgra[s + 2];
        }
    }

    private static void ExpandBgrToBgra(ReadOnlySpan<byte> bgr, Span<byte> bgra)
    {
        for (int s = 0, d = 0; s < bgr.Length; s += 3, d += 4)
        {
            bgra[d] = bgr[s]; bgra[d + 1] = bgr[s + 1]; bgra[d + 2] = bgr[s + 2]; bgra[d + 3] = 255;
        }
    }
}
