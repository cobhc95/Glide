namespace Glide.Core;

/// <summary>Result of one debounced flush of folder change events.</summary>
public sealed record FolderChangeSet(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool RescanRequired);

/// <summary>
/// Pure (timer-free, filesystem-agnostic) accumulator for watcher events, so the coalescing
/// rules can be unit tested. Thread-safe.
/// </summary>
public sealed class FolderChangeBatcher
{
    private readonly object _sync = new();
    // path -> true (present/candidate add) or false (removed). Last event wins.
    private readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private bool _rescan;
    public int MaxReadAttempts { get; init; } = 40;

    public static bool IsIgnoredName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name.StartsWith('~') || name.StartsWith(".~")) return true;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".crdownload" or ".part" or ".partial" or ".tmp" or ".temp" or ".download" or ".opdownload" or ".!ut";
    }

    public static bool IsRelevant(string path) => !IsIgnoredName(path) && ImageNavigator.IsSupported(path);

    public bool HasPending { get { lock (_sync) return _rescan || _pending.Count > 0; } }

    public void OnCreatedOrChanged(string path)
    {
        if (!IsRelevant(path)) return;
        lock (_sync) _pending[path] = true;
    }

    public void OnDeleted(string path)
    {
        if (!IsRelevant(path)) return;
        lock (_sync) { _pending[path] = false; _attempts.Remove(path); }
    }

    public void OnRenamed(string oldPath, string newPath)
    {
        // e.g. "x.jpg.crdownload" -> "x.jpg": old is ignored, new is a fresh add.
        OnDeleted(oldPath);
        OnCreatedOrChanged(newPath);
    }

    public void OnOverflow() { lock (_sync) { _rescan = true; _pending.Clear(); _attempts.Clear(); } }

    /// <summary>
    /// Drains settled events. Candidate adds that are not yet readable (still being written /
    /// locked) stay pending for the next flush, up to MaxReadAttempts. Files that vanished
    /// before becoming readable are reported as removed.
    /// </summary>
    public FolderChangeSet Flush(Func<string, bool> exists, Func<string, bool> isReadable)
    {
        List<KeyValuePair<string, bool>> snapshot;
        bool rescan;
        lock (_sync)
        {
            rescan = _rescan; _rescan = false;
            snapshot = _pending.ToList();
            _pending.Clear();
        }
        if (rescan) return new FolderChangeSet([], [], true);

        var added = new List<string>();
        var removed = new List<string>();
        var retry = new List<string>();
        foreach (var (path, present) in snapshot)
        {
            if (!present || !exists(path)) { removed.Add(path); lock (_sync) _attempts.Remove(path); continue; }
            if (isReadable(path)) { added.Add(path); lock (_sync) _attempts.Remove(path); continue; }
            retry.Add(path);
        }
        lock (_sync)
        {
            foreach (var path in retry)
            {
                if (_pending.ContainsKey(path)) continue; // newer event arrived meanwhile
                var n = _attempts.TryGetValue(path, out var a) ? a + 1 : 1;
                if (n >= MaxReadAttempts) { _attempts.Remove(path); continue; }
                _attempts[path] = n;
                _pending[path] = true;
            }
        }
        return new FolderChangeSet(added, removed, false);
    }

    public static bool IsFileReadable(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Watches one folder for image additions/removals/renames with a debounce. The callback is
/// invoked on a thread-pool thread; callers marshal to their UI thread.
/// </summary>
public sealed class FolderWatchService : IDisposable
{
    private readonly object _sync = new();
    private readonly Action<string, FolderChangeSet> _onChanges;
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _watcher;
    private FolderChangeBatcher _batcher = new();
    private Timer? _timer;
    private string? _folder;
    private bool _disposed;

    public FolderWatchService(Action<string, FolderChangeSet> onChanges, TimeSpan? debounce = null)
    {
        _onChanges = onChanges;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
    }

    public string? Folder { get { lock (_sync) return _folder; } }

    /// <summary>Starts (or retargets) watching. No-op when already watching the same folder.</summary>
    public void Watch(string? folder)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (folder is not null) folder = Path.GetFullPath(folder);
            if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase) && _watcher is not null) return;
            StopCore();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            _folder = folder;
            _batcher = new FolderChangeBatcher();
            var batcher = _batcher;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => { batcher.OnCreatedOrChanged(e.FullPath); Kick(batcher); };
                w.Changed += (_, e) => { batcher.OnCreatedOrChanged(e.FullPath); Kick(batcher); };
                w.Deleted += (_, e) => { batcher.OnDeleted(e.FullPath); Kick(batcher); };
                w.Renamed += (_, e) => { batcher.OnRenamed(e.OldFullPath, e.FullPath); Kick(batcher); };
                w.Error += (_, _) => { batcher.OnOverflow(); Kick(batcher); };
                w.EnableRaisingEvents = true;
                _watcher = w;
                _timer = new Timer(_ => Tick(batcher), null, Timeout.Infinite, Timeout.Infinite);
            }
            catch (Exception)
            {
                StopCore();
            }
        }
    }

    public void Stop() { lock (_sync) StopCore(); }

    private void StopCore()
    {
        _watcher?.Dispose(); _watcher = null;
        _timer?.Dispose(); _timer = null;
        _folder = null;
    }

    private void Kick(FolderChangeBatcher batcher)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(batcher, _batcher)) return;
            _timer?.Change(_debounce, Timeout.InfiniteTimeSpan); // restart debounce window
        }
    }

    private void Tick(FolderChangeBatcher batcher)
    {
        string? folder;
        lock (_sync)
        {
            if (!ReferenceEquals(batcher, _batcher) || _disposed) return;
            folder = _folder;
        }
        if (folder is null) return;
        FolderChangeSet set;
        try { set = batcher.Flush(File.Exists, FolderChangeBatcher.IsFileReadable); }
        catch { return; }
        if (set.RescanRequired || set.Added.Count > 0 || set.Removed.Count > 0)
        {
            try { _onChanges(folder, set); } catch { }
        }
        if (batcher.HasPending) Kick(batcher); // locked / in-progress files: retry later
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; StopCore(); }
    }
}
