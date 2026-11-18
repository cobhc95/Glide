using Avalonia.Media.Imaging;
using Glide.Core;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// Small preview bitmaps for the tab hover tooltip (5.0.6, "Show tab previews on hover").
/// A preview comes from the Windows thumbnail cache when it already holds one; otherwise from a bounded,
/// scaled decode of a modest file. Both run on a background thread, one at a time, so hovering tabs never decodes
/// on the UI thread and never competes with the viewer for more than one core. Results are kept in a small LRU
/// (UI thread only) so hovering the same tab again is instant.
/// </summary>
internal sealed class TabPreviewCache : IDisposable
{
    // The hover card shows a picture about 260 DIP wide (taller for portraits, up to 220 DIP: TabHoverCard), and the
    // folder thumbnails as square cells across it; these bounds keep both sharp without decoding more than needed.
    public const int PictureWidth = 300;
    public const int PictureHeight = 300;
    public const int FolderThumbSize = 96;
    public const int FolderThumbCount = 4;

    private const int Capacity = 40;
    /// <summary>5.1.1 RAM: byte ceiling next to the item count (shell thumbnails can come back larger than asked).</summary>
    internal const long MaxBytes = 16L * 1024 * 1024;
    /// <summary>Files larger than this get no decoded preview (only a cached Windows thumbnail).</summary>
    private const long MaxDecodedFileBytes = 48L * 1024 * 1024;

    /// <summary>Test seam: replaces the background preview producer (path, max width, max height).</summary>
    internal static Func<string, int, int, Bitmap?>? ProducerOverrideForTests { get; set; }

    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, Bitmap Bitmap)> _lru = new();
    private readonly Dictionary<string, Task<Bitmap?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Bitmap> _shown = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _lane = new(1, 1);
    private bool _disposed;
    private long _bytes;

    private static long BytesOf(Bitmap bitmap) => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    private static string Key(string path, int width, int height) => $"{width}x{height}|{path}";

    /// <summary>A cached preview, or null. UI thread; never touches the disk.</summary>
    public Bitmap? TryGet(string path, int maxWidth, int maxHeight)
    {
        if (!_entries.TryGetValue(Key(path, maxWidth, maxHeight), out var node)) return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Bitmap;
    }

    /// <summary>The preview of <paramref name="path"/>, made in the background when it is not cached. Call on the UI thread.</summary>
    public Task<Bitmap?> GetAsync(string path, int maxWidth, int maxHeight)
    {
        if (_disposed) return Task.FromResult<Bitmap?>(null);
        if (TryGet(path, maxWidth, maxHeight) is { } cached) return Task.FromResult<Bitmap?>(cached);
        var key = Key(path, maxWidth, maxHeight);
        if (_inflight.TryGetValue(key, out var running)) return running;
        var task = ProduceAndStoreAsync(key, path, maxWidth, maxHeight);
        _inflight[key] = task;
        return task;
    }

    private async Task<Bitmap?> ProduceAndStoreAsync(string key, string path, int maxWidth, int maxHeight)
    {
        try
        {
            // No ConfigureAwait(false) here: the rest of this method (the _disposed check, Store, _inflight) must run
            // back on the UI thread, which owns the LRU. Only Produce runs on the pool, one at a time through _lane.
            var bitmap = await Task.Run(async () =>
            {
                await _lane.WaitAsync().ConfigureAwait(false);
                try { return Produce(path, maxWidth, maxHeight); }
                finally { _lane.Release(); }
            });
            if (bitmap is null) return null;
            if (_disposed) { bitmap.Dispose(); return null; }
            Store(key, bitmap);
            return bitmap;
        }
        catch { return null; /* an unreadable file simply has no preview */ }
        finally { _inflight.Remove(key); }
    }

    /// <summary>
    /// The first few supported pictures of <paramref name="folder"/>, listed in the background on every hover so the
    /// preview follows the folder (only the first 64 pictures are looked at, so a huge folder costs no more).
    /// </summary>
    public static Task<IReadOnlyList<string>> FirstPicturesAsync(string folder) => Task.Run<IReadOnlyList<string>>(() =>
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(p => ImageNavigator.IsSupported(p) && (File.GetAttributes(p) & FileAttributes.Hidden) == 0)
                .Take(64)
                .OrderBy(p => p, ImageSortOrder.CreateComparer())
                .Take(FolderThumbCount)
                .ToArray();
        }
        catch { return Array.Empty<string>(); /* unreadable folder: no thumbnails */ }
    });

    /// <summary>The bitmaps an open tooltip shows: they are never disposed while shown.</summary>
    public void SetShown(IEnumerable<Bitmap> bitmaps)
    {
        _shown.Clear();
        foreach (var bitmap in bitmaps) _shown.Add(bitmap);
    }

    private void Store(string key, Bitmap bitmap)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _lru.Remove(existing);
            _entries.Remove(key);
            _bytes -= BytesOf(existing.Value.Bitmap);
            if (!_shown.Contains(existing.Value.Bitmap) && !ReferenceEquals(existing.Value.Bitmap, bitmap)) existing.Value.Bitmap.Dispose();
        }
        _entries[key] = _lru.AddFirst((key, bitmap));
        _bytes += BytesOf(bitmap);
        for (var node = _lru.Last; (_entries.Count > Capacity || _bytes > MaxBytes) && node is not null;)
        {
            var previous = node.Previous;
            if (!_shown.Contains(node.Value.Bitmap))
            {
                _lru.Remove(node);
                _entries.Remove(node.Value.Key);
                _bytes -= BytesOf(node.Value.Bitmap);
                node.Value.Bitmap.Dispose();
            }
            node = previous;
        }
    }

    /// <summary>Background thread: a cached Windows thumbnail, else a scaled decode of a modest file. Null when neither is cheap.</summary>
    private static Bitmap? Produce(string path, int maxWidth, int maxHeight)
    {
        if (ProducerOverrideForTests is { } fake) return fake(path, maxWidth, maxHeight);
        if (NativeImageDecoder.TryDecodeShellCachedBounded(path, maxWidth, maxHeight, out var cached)) return cached;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxDecodedFileBytes || !CanDecodeScaled(path)) return null;
        return ThumbnailDecode.DecodeToWidth(path, maxWidth);
    }

    /// <summary>Formats Avalonia's own decoder handles (JPEG decodes directly at the reduced size).</summary>
    private static bool CanDecodeScaled(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".gif" or ".webp";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _lru) node.Bitmap.Dispose();
        _lru.Clear();
        _entries.Clear();
        _shown.Clear();
        _bytes = 0;
    }
}
