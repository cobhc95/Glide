using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Glide.App.Services;

/// <summary>
/// 5.2.0: decoded Explorer thumbnails kept in memory (pixels, not bitmaps: a tile owns and disposes its own bitmap), least recently
/// used first out, bounded in bytes. The whole-folder background preparation fills it, so a tile scrolled into view is shown at
/// once; anything evicted is still in the disk cache when that is on. Thread-safe.
/// </summary>
internal sealed class ThumbnailMemoryCache
{
    private sealed record Entry(string Key, byte[] Pixels, int Width, int Height, AlphaFormat Alpha);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public ThumbnailMemoryCache(long maxBytes) => MaxBytes = Math.Max(0, maxBytes);

    public long MaxBytes { get; private set; }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>The cache key of a request: this file version at this tile width.</summary>
    public static string KeyFor(in ThumbnailRequest request) => request.Key + "|" + Math.Max(16, request.Width);

    public void SetMaxBytes(long maxBytes)
    {
        lock (_gate)
        {
            MaxBytes = Math.Max(0, maxBytes);
            Trim();
        }
    }

    public bool Contains(string key) { lock (_gate) return _map.ContainsKey(key); }

    /// <summary>A new bitmap (owned by the caller) of the cached picture, or null.</summary>
    public unsafe Bitmap? TryCreate(string key)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            entry = node.Value;
        }
        try
        {
            fixed (byte* pixels = entry.Pixels)
                return new Bitmap(PixelFormat.Bgra8888, entry.Alpha, (IntPtr)pixels, new PixelSize(entry.Width, entry.Height), new Vector(96, 96), entry.Width * 4);
        }
        catch { return null; }
    }

    /// <summary>Keeps a copy of <paramref name="bitmap"/> (BGRA only; other formats and pictures over 2048 px are not kept).</summary>
    public unsafe void Put(string key, Bitmap bitmap)
    {
        if (MaxBytes <= 0) return;
        try
        {
            if (bitmap.Format != PixelFormat.Bgra8888) return;
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0 || size.Width > 2048 || size.Height > 2048) return;
            var length = size.Width * size.Height * 4;
            if (length > MaxBytes) return;
            var pixels = GC.AllocateUninitializedArray<byte>(length);
            fixed (byte* p = pixels) bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, length, size.Width * 4);
            var entry = new Entry(key, pixels, size.Width, size.Height, bitmap.AlphaFormat ?? AlphaFormat.Premul);
            lock (_gate)
            {
                if (_map.Remove(key, out var old)) { _lru.Remove(old); _bytes -= old.Value.Pixels.Length; }
                _map[key] = _lru.AddFirst(entry);
                _bytes += length;
                Trim();
            }
        }
        catch { /* a picture that cannot be copied is decoded again when needed */ }
    }

    /// <summary>A copy of a BGRA bitmap (the caller owns it); null when it cannot be copied.</summary>
    public static unsafe Bitmap? Copy(Bitmap bitmap)
    {
        try
        {
            var size = bitmap.PixelSize;
            var stride = size.Width * 4;
            var pixels = GC.AllocateUninitializedArray<byte>(stride * size.Height);
            fixed (byte* p = pixels)
            {
                bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pixels.Length, stride);
                return new Bitmap(bitmap.Format ?? PixelFormat.Bgra8888, bitmap.AlphaFormat ?? AlphaFormat.Premul, (IntPtr)p, size, new Vector(96, 96), stride);
            }
        }
        catch { return null; }
    }

    public void Clear()
    {
        lock (_gate) { _map.Clear(); _lru.Clear(); _bytes = 0; }
    }

    private void Trim()
    {
        while (_bytes > MaxBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Pixels.Length;
        }
    }
}
