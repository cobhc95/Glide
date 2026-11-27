namespace Glide.Core.Workspace;

/// <summary>
/// 5.2.1 G2 ("Delayed permanent delete"): paths that are waiting for their permanent deletion. Every folder listing that feeds the
/// viewer, the Explorer tab and the tab previews skips these, so a pending file disappears from the lists at once while it is still
/// on disk (nothing is moved or renamed). Process-wide because the lists are built in several places (and windows).
/// </summary>
public static class PendingDeleteHideList
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase);
    private static int _count;

    private static string Key(string path) => Path.GetFullPath(path);

    public static bool Any => Volatile.Read(ref _count) > 0;

    public static void Hide(string path)
    {
        lock (Gate) { Hidden.Add(Key(path)); Volatile.Write(ref _count, Hidden.Count); }
    }

    public static void Unhide(string path)
    {
        lock (Gate) { Hidden.Remove(Key(path)); Volatile.Write(ref _count, Hidden.Count); }
    }

    public static bool IsHidden(string path)
    {
        if (!Any) return false;
        string key;
        try { key = Key(path); } catch { return false; }
        lock (Gate) return Hidden.Contains(key);
    }

    /// <summary>The same sequence without the pending paths (returned untouched, no copy, when nothing is pending).</summary>
    public static IEnumerable<string> Filter(IEnumerable<string> paths) => Any ? paths.Where(p => !IsHidden(p)) : paths;
}

public enum PendingDeleteOutcome
{
    /// <summary>Nothing was waiting under that id (undone or already completed).</summary>
    NotPending,
    /// <summary>The file was deleted permanently.</summary>
    Deleted,
    /// <summary>The file was already gone; nothing to do.</summary>
    Gone,
    /// <summary>The file was modified or replaced after the user confirmed: it was NOT deleted.</summary>
    Changed,
    /// <summary>The deletion was attempted and failed (locked, read-only share ...): the file stays.</summary>
    Failed
}

public sealed record PendingDeleteEntry(Guid Id, string Path, long Length, DateTime LastWriteUtc);

public sealed record PendingDeleteResult(PendingDeleteEntry? Entry, PendingDeleteOutcome Outcome, string? Error);

/// <summary>
/// The queue behind the delayed permanent delete. UI-free: the window owns the timers and calls <see cref="Complete"/> when the
/// delay ends, <see cref="Undo"/> when the user clicks Undo and <see cref="CompleteAll"/> when it closes. An entry leaves the queue
/// exactly once (under a lock), so Undo and the timer can race safely: whichever comes first wins and the other sees NotPending.
/// </summary>
public sealed class PendingPermanentDeleteQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PendingDeleteEntry> _entries = new();
    // Entries that already left the queue but whose Complete() (check + deletion, usually on a worker thread) is still running.
    private int _inFlight;

    public int Count { get { lock (_gate) return _entries.Count; } }

    public IReadOnlyList<PendingDeleteEntry> Entries { get { lock (_gate) return _entries.Values.ToList(); } }

    /// <summary>
    /// Hides <paramref name="path"/> from the lists and remembers its size and time stamp. Null when the file does not exist or
    /// is already pending.
    /// </summary>
    public PendingDeleteEntry? Enqueue(string path)
    {
        var full = Path.GetFullPath(path);
        FileInfo info;
        try { info = new FileInfo(full); if (!info.Exists) return null; } catch { return null; }
        lock (_gate)
        {
            if (_entries.Values.Any(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase))) return null;
            var entry = new PendingDeleteEntry(Guid.NewGuid(), full, info.Length, info.LastWriteTimeUtc);
            _entries[entry.Id] = entry;
            PendingDeleteHideList.Hide(full);
            return entry;
        }
    }

    /// <summary>Cancels the deletion: the file is listed again. Returns the entry, or null when nothing was waiting.</summary>
    public PendingDeleteEntry? Undo(Guid id)
    {
        lock (_gate)
        {
            if (!_entries.Remove(id, out var entry)) return null;
            PendingDeleteHideList.Unhide(entry.Path);
            return entry;
        }
    }

    /// <summary>
    /// Ends the delay for one entry. The file is deleted only if it is still the file the user confirmed (same size and time stamp);
    /// a file that changed or went away is left alone. The path is listed again afterwards in every case but Deleted.
    /// <paramref name="deleter"/> returns null on success or an error message.
    /// </summary>
    public PendingDeleteResult Complete(Guid id, Func<string, string?> deleter)
    {
        PendingDeleteEntry? entry;
        lock (_gate) { if (!_entries.Remove(id, out entry)) return new(null, PendingDeleteOutcome.NotPending, null); _inFlight++; }
        try
        {
            FileInfo info;
            try { info = new FileInfo(entry.Path); } catch (Exception ex) { return new(entry, PendingDeleteOutcome.Failed, ex.Message); }
            if (!info.Exists) return new(entry, PendingDeleteOutcome.Gone, null);
            if (info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteUtc)
                return new(entry, PendingDeleteOutcome.Changed, null);
            string? error;
            try { error = deleter(entry.Path); } catch (Exception ex) { error = ex.Message; }
            if (error is null) return new(entry, PendingDeleteOutcome.Deleted, null);
            return File.Exists(entry.Path) ? new(entry, PendingDeleteOutcome.Failed, error) : new(entry, PendingDeleteOutcome.Deleted, null);
        }
        finally
        {
            PendingDeleteHideList.Unhide(entry.Path);
            lock (_gate) { _inFlight--; Monitor.PulseAll(_gate); }
        }
    }

    /// <summary>Waits until every Complete() that is already running has finished (so a closing window never abandons a deletion half-way).</summary>
    public bool WaitForInFlight(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_inFlight > 0)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(_gate, left);
            }
            return true;
        }
    }

    /// <summary>Completes every waiting entry now (window closing / app exit).</summary>
    public IReadOnlyList<PendingDeleteResult> CompleteAll(Func<string, string?> deleter)
    {
        var results = new List<PendingDeleteResult>();
        foreach (var entry in Entries) results.Add(Complete(entry.Id, deleter));
        WaitForInFlight(TimeSpan.FromSeconds(10));
        return results;
    }
}
