namespace Glide.Core.Navigation;

/// <summary>What a tab showed at one point of its Back / Forward history.</summary>
public enum NavigationEntryKind { Home, Explorer, Image }

/// <summary>
/// One step of a tab's browser-style history. <see cref="Path"/> is the folder (Explorer) or the file (Image); Home has
/// none. Explorer entries also remember the selected item, the list scroll offset and the view mode, so Back returns
/// to the same place in the folder.
/// </summary>
public sealed record NavigationEntry(NavigationEntryKind Kind, string? Path)
{
    /// <summary>Explorer: the item to select when the entry is shown again (null: nothing).</summary>
    public string? SelectedPath { get; init; }
    /// <summary>Explorer: vertical scroll offset of the file list, in pixels.</summary>
    public double ScrollY { get; init; }
    /// <summary>Explorer: view mode index (icon size / details) when it was left; null when unknown.</summary>
    public int? ViewMode { get; init; }
    /// <summary>Explorer: the user arrived here from this image tab (Ctrl+E ...); Back returns to it (<see cref="CrossTabHistory"/>).</summary>
    public CrossTabLink? ReturnTo { get; init; }
    /// <summary>Image: Back came here from this Explorer tab; Forward returns to it.</summary>
    public CrossTabLink? ForwardTo { get; init; }

    public static NavigationEntry Home() => new(NavigationEntryKind.Home, null);
    public static NavigationEntry Explorer(string folder, string? selectedPath = null, double scrollY = 0, int? viewMode = null) =>
        new(NavigationEntryKind.Explorer, folder) { SelectedPath = selectedPath, ScrollY = double.IsFinite(scrollY) && scrollY > 0 ? scrollY : 0, ViewMode = viewMode };
    public static NavigationEntry Image(string path) => new(NavigationEntryKind.Image, path);

    /// <summary>Same kind and same folder / file (paths compared like Windows does: case-insensitive, trailing separator ignored).</summary>
    public bool SameLocation(NavigationEntry? other) =>
        other is not null && Kind == other.Kind && PathsEqual(Path, other.Path);

    public static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>An immutable copy of a history (travels with a tab moved to another window).</summary>
public sealed record NavigationHistoryState(IReadOnlyList<NavigationEntry> Entries, int Index);

/// <summary>
/// Per-tab browser-style Back / Forward history (Glide 5.0.0). Framework-free; the main window owns one per tab.
/// <list type="bullet">
/// <item><see cref="Push"/>: going somewhere else (another folder, an image opened from the Explorer, Home ...) adds an
/// entry after the current one and drops everything Forward of it, like a web browser.</item>
/// <item><see cref="Replace"/>: changes the current entry in place (arrow keys from image to image, the Explorer
/// selection / scroll changing). These are not history steps.</item>
/// <item><see cref="Record"/>: the rule the window uses when a tab simply shows something: nothing recorded yet or a
/// different place pushes, the same place is kept, image to image replaces.</item>
/// <item><see cref="Step"/>: Back / Forward with a resolver that skips entries whose target is gone and moves a
/// missing folder to its nearest existing parent.</item>
/// </list>
/// At most <see cref="Capacity"/> entries are kept; the oldest are dropped first.
/// </summary>
public sealed class NavigationHistory
{
    public const int DefaultCapacity = 100;

    private readonly List<NavigationEntry> _entries = new();
    // Round 3 #13: when each entry was last the current one (a counter, parallel to _entries), so the Recent locations
    // dropdown can list places most recently visited first, independent of their Back / Forward position.
    private readonly List<long> _visited = new();
    private long _clock;

    public NavigationHistory(int capacity = DefaultCapacity) => Capacity = Math.Max(2, capacity);

    public int Capacity { get; }
    public IReadOnlyList<NavigationEntry> Entries => _entries;
    /// <summary>Index of the current entry; -1 while empty.</summary>
    public int Index { get; private set; } = -1;
    public int Count => _entries.Count;
    public NavigationEntry? Current => Index >= 0 && Index < _entries.Count ? _entries[Index] : null;
    public bool CanGoBack => Index > 0;
    public bool CanGoForward => Index >= 0 && Index < _entries.Count - 1;

    /// <summary>
    /// Navigates to <paramref name="entry"/>: the Forward entries are dropped and the entry becomes current. Pushing the
    /// place that is already current replaces it (its remembered selection / scroll is the new entry's) instead of
    /// adding a duplicate.
    /// </summary>
    public void Push(NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Index < _entries.Count - 1)
        {
            _entries.RemoveRange(Index + 1, _entries.Count - Index - 1);
            _visited.RemoveRange(Index + 1, _visited.Count - Index - 1);
        }
        if (Current is { } current && current.SameLocation(entry))
        {
            _entries[Index] = KeepLinks(current, entry);
            Touch();
            return;
        }
        _entries.Add(entry);
        _visited.Add(0);
        Index = _entries.Count - 1;
        while (_entries.Count > Capacity)
        {
            RemoveEntryAt(0);
            Index--;
        }
        Touch();
    }

    /// <summary>Replaces the current entry (or adds the first one) without touching Back / Forward.</summary>
    public void Replace(NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Current is null) { Push(entry); return; }
        _entries[Index] = KeepLinks(Current, entry);
    }

    /// <summary>Changes the current entry in place (for example to remember the Explorer selection).</summary>
    public void UpdateCurrent(Func<NavigationEntry, NavigationEntry> update)
    {
        if (Current is { } current) _entries[Index] = update(current) ?? current;
    }

    /// <summary>
    /// The tab now shows <paramref name="entry"/>. Nothing recorded yet: it becomes the first entry. Same place as the
    /// current entry: kept as it is (its remembered details stay). Image to image: replaced in place (arrow keys,
    /// wheel, slideshow, sibling folders and Open file in the same tab never add a history step). Anything else: pushed.
    /// </summary>
    /// <returns>True when a new history step was added.</returns>
    public bool Record(NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Current is not { } current) { Push(entry); return true; }
        if (current.SameLocation(entry)) return false;
        if (current.Kind == NavigationEntryKind.Image && entry.Kind == NavigationEntryKind.Image)
        {
            _entries[Index] = entry;
            return false;
        }
        Push(entry);
        return true;
    }

    /// <summary>Makes entry <paramref name="index"/> current (history dropdown). Returns it, or null when out of range.</summary>
    public NavigationEntry? GoTo(int index)
    {
        if (index < 0 || index >= _entries.Count) return null;
        Index = index;
        Touch();
        return _entries[index];
    }

    /// <summary>
    /// History dropdown: makes entry <paramref name="index"/> current after <paramref name="resolve"/> checked it. A gone
    /// target is removed from the history (never the current entry) and null is returned.
    /// </summary>
    public NavigationEntry? GoTo(int index, Func<NavigationEntry, NavigationEntry?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (index < 0 || index >= _entries.Count) return null;
        var resolved = resolve(_entries[index]);
        if (resolved is null)
        {
            if (index == Index) return null;
            RemoveEntryAt(index);
            if (index < Index) Index--;
            return null;
        }
        _entries[index] = resolved;
        Index = index;
        Touch();
        RemoveAdjacentDuplicate();
        return Current;
    }

    /// <summary>One Back (-1) or Forward (+1) step without checking the target.</summary>
    public NavigationEntry? Back() => CanGoBack ? GoTo(Index - 1) : null;
    public NavigationEntry? Forward() => CanGoForward ? GoTo(Index + 1) : null;

    /// <summary>
    /// Back (<paramref name="direction"/> &lt; 0) or Forward (&gt; 0). Each candidate goes through
    /// <paramref name="resolve"/>: null means the target is gone, so the entry is removed from the history and the step
    /// continues in the same direction; a different entry (a missing folder resolved to its parent) replaces it.
    /// Returns the entry that is now current, or null when there was nothing to go to (the current entry is kept;
    /// dead entries found on the way are still removed). <paramref name="skipped"/> counts the removed entries.
    /// </summary>
    public NavigationEntry? Step(int direction, Func<NavigationEntry, NavigationEntry?> resolve, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        skipped = 0;
        if (direction == 0 || Index < 0) return null;
        var step = Math.Sign(direction);
        var candidate = Index + step;
        while (candidate >= 0 && candidate < _entries.Count)
        {
            var resolved = resolve(_entries[candidate]);
            if (resolved is not null)
            {
                _entries[candidate] = resolved;
                Index = candidate;
                Touch();
                RemoveAdjacentDuplicate();
                return Current;
            }
            RemoveEntryAt(candidate);
            skipped++;
            if (step < 0)
            {
                Index--;
                candidate--;
            }
            // Forward: the next candidate slid into the same slot.
        }
        return null;
    }

    /// <summary>A copy that can travel with the tab state.</summary>
    public NavigationHistoryState Snapshot() => new(_entries.ToArray(), Index);

    public static NavigationHistory FromState(NavigationHistoryState? state, int capacity = DefaultCapacity)
    {
        var history = new NavigationHistory(capacity);
        if (state is null) return history;
        foreach (var entry in state.Entries)
            if (entry is not null) { history._entries.Add(entry); history._visited.Add(0); }
        while (history._entries.Count > history.Capacity) history.RemoveEntryAt(0);
        var dropped = state.Entries.Count(e => e is not null) - history._entries.Count;
        history.Index = history._entries.Count == 0 ? -1 : Math.Clamp(state.Index - dropped, 0, history._entries.Count - 1);
        // Visit times are not part of the state: assume the nearer an entry is to the current one, the more recent.
        for (var i = 0; i < history._entries.Count; i++) history._visited[i] = -Math.Abs(i - history.Index) * 2 - (i > history.Index ? 1 : 0);
        return history;
    }

    public NavigationHistory Clone() => FromState(Snapshot(), Capacity);

    // The same place shown again keeps its cross-tab Back / Forward links (CrossTabHistory); another place drops them.
    private static NavigationEntry KeepLinks(NavigationEntry current, NavigationEntry entry) =>
        current.SameLocation(entry) && entry.ReturnTo is null && entry.ForwardTo is null
            ? entry with { ReturnTo = current.ReturnTo, ForwardTo = current.ForwardTo }
            : entry;

    /// <summary>
    /// Recent locations dropdown (round 3 #13): indexes of the entries, the most recently visited place first, each place
    /// (same kind and folder / file) once, at most <paramref name="max"/>.
    /// </summary>
    public IReadOnlyList<int> RecentOrder(int max = int.MaxValue)
    {
        var order = Enumerable.Range(0, _entries.Count).OrderByDescending(i => _visited[i]).ThenByDescending(i => i);
        var result = new List<int>();
        foreach (var i in order)
        {
            if (result.Count >= max) break;
            if (result.Any(r => _entries[r].SameLocation(_entries[i]))) continue;
            result.Add(i);
        }
        return result;
    }

    private void Touch()
    {
        if (Index >= 0 && Index < _visited.Count) _visited[Index] = ++_clock;
    }

    private void RemoveEntryAt(int index)
    {
        _entries.RemoveAt(index);
        _visited.RemoveAt(index);
    }

    // A missing folder that resolved to its parent can land on the same place as its neighbour; keep one of them.
    private void RemoveAdjacentDuplicate()
    {
        if (Index > 0 && _entries[Index - 1].SameLocation(_entries[Index]))
        {
            RemoveEntryAt(Index - 1);
            Index--;
        }
        else if (Index < _entries.Count - 1 && _entries[Index + 1].SameLocation(_entries[Index]))
        {
            RemoveEntryAt(Index + 1);
        }
    }
}

/// <summary>
/// What Back / Forward does with an entry whose target changed on disk, using the same fallback as the workspace
/// session restore: an image that is gone is skipped; a folder that is gone opens its nearest existing parent (with
/// nothing selected and no remembered scroll); Home always works.
/// </summary>
public static class NavigationTargetResolver
{
    public static NavigationEntry? Resolve(NavigationEntry entry, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(entry);
        switch (entry.Kind)
        {
            case NavigationEntryKind.Home:
                return entry;
            case NavigationEntryKind.Image:
                return !string.IsNullOrWhiteSpace(entry.Path) && SafeExists(fileExists, entry.Path) ? entry : null;
            case NavigationEntryKind.Explorer:
                if (string.IsNullOrWhiteSpace(entry.Path)) return null;
                if (SafeExists(directoryExists, entry.Path))
                {
                    // The remembered item may be gone; the folder still opens, just without a selection.
                    if (entry.SelectedPath is { } selected && !SafeExists(fileExists, selected) && !SafeExists(directoryExists, selected))
                        return entry with { SelectedPath = null };
                    return entry;
                }
                var parent = NearestExistingParent(entry.Path, directoryExists);
                return parent is null ? null : NavigationEntry.Explorer(parent, viewMode: entry.ViewMode);
            default:
                return null;
        }
    }

    public static string? NearestExistingParent(string folder, Func<string, bool> directoryExists)
    {
        try
        {
            for (var current = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(folder));
                 !string.IsNullOrEmpty(current);
                 current = System.IO.Path.GetDirectoryName(current))
                if (SafeExists(directoryExists, current)) return current;
        }
        catch { /* an unusable path has no parent */ }
        return null;
    }

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try { return exists(path); }
        catch { return false; }
    }
}
