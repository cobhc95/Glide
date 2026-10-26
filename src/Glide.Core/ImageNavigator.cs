namespace Glide.Core;

/// <summary>
/// Owns the deterministic ordered set of image paths for one folder session.
/// It intentionally contains no UI code, so navigation bugs can be tested headlessly.
/// </summary>
public sealed class ImageNavigator
{
    private readonly List<string> _paths = new();
    public IReadOnlyList<string> Paths => _paths;
    public int Index { get; private set; } = -1;
    public string? Current => Index >= 0 && Index < _paths.Count ? _paths[Index] : null;
    public int Count => _paths.Count;

    public void OpenSingle(string path)
    {
        var full = Path.GetFullPath(path);
        _paths.Clear();

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            _paths.AddRange(Directory.EnumerateFiles(directory)
                .Where(IsSupported)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
        }

        Index = _paths.FindIndex(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        if (Index < 0)
        {
            _paths.Clear();
            _paths.Add(full);
            Index = 0;
        }
    }


    public void OpenSingleOnly(string path)
    {
        var full = Path.GetFullPath(path);
        _paths.Clear();
        _paths.Add(full);
        Index = 0;
    }

    public void ReplaceFolderIndex(IEnumerable<string> orderedPaths, string currentPath)
    {
        var currentFull = Path.GetFullPath(currentPath);
        _paths.Clear();
        _paths.AddRange(orderedPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase));
        Index = _paths.FindIndex(p => string.Equals(p, currentFull, StringComparison.OrdinalIgnoreCase));
        if (Index < 0)
        {
            _paths.Add(currentFull);
            _paths.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            Index = _paths.FindIndex(p => string.Equals(p, currentFull, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static int CompareCanonical(string a, string b)
    {
        var c = StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
        return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
    }

    /// <summary>
    /// Incrementally merges watcher-detected additions/removals without a rescan. Keeps the
    /// canonical name order and keeps the index on the current file. If the current file was
    /// removed, the index moves to the item that now occupies its slot (clamped).
    /// Returns true when the list changed.
    /// </summary>
    public bool ApplyChanges(IEnumerable<string> added, IEnumerable<string> removed)
    {
        var current = Current;
        var changed = false;
        var removedSet = new HashSet<string>(removed.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var oldIndex = Index;
        if (removedSet.Count > 0)
        {
            var n = _paths.RemoveAll(p => removedSet.Contains(p));
            changed |= n > 0;
        }
        foreach (var raw in added)
        {
            var full = Path.GetFullPath(raw);
            if (!IsSupported(full)) continue;
            var pos = BinarySearch(full);
            if (pos >= 0) continue;
            // Fall back to a linear dedupe for case-variant paths.
            if (_paths.Exists(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) continue;
            _paths.Insert(~pos, full);
            changed = true;
        }
        if (!changed) return false;

        var keep = current is null ? -1 : _paths.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        if (keep >= 0) Index = keep;
        else if (_paths.Count == 0) Index = -1;
        else Index = Math.Clamp(oldIndex < 0 ? 0 : oldIndex, 0, _paths.Count - 1);
        return true;
    }

    private int BinarySearch(string full)
    {
        int lo = 0, hi = _paths.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var c = CompareCanonical(_paths[mid], full);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }

    public static string[] EnumerateSupportedFolder(string path)
        => EnumerateSupportedFolder(path, null);

    /// <summary>
    /// Returns a bounded canonical-order window around the requested image. Unlike
    /// OrderBy(...).Take(n), this never materializes/sorts the entire directory merely to obtain a
    /// provisional window. It scans names with O(maxCount) retained memory and keeps the exact
    /// nearest canonical neighbours on both sides, so later full-index reconciliation cannot
    /// silently reorder next/previous within the provisional window. maxCount includes the
    /// requested image.
    /// </summary>
    public static string[] EnumerateSupportedFolder(string path, int? maxCount)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return [full];
        if (maxCount is not > 0)
            return Directory.EnumerateFiles(directory)
                .Where(IsSupported)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var limit = Math.Max(1, maxCount.Value);
        if (limit == 1) return [full];

        var neighbourCapacity = limit - 1;
        var requestedName = Path.GetFileName(full);
        var nameComparer = StringComparer.OrdinalIgnoreCase;
        // lower is a normal min-heap: once over capacity, discard the lexically smallest
        // (farthest preceding) name so the greatest/nearest preceding names survive.
        var lower = new PriorityQueue<string, string>(nameComparer);
        // upper reverses priority: once over capacity, dequeue the lexically greatest
        // (farthest following) name so the smallest/nearest following names survive.
        var reverseNameComparer = Comparer<string>.Create((a, b) => nameComparer.Compare(b, a));
        var upper = new PriorityQueue<string, string>(reverseNameComparer);

        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (!IsSupported(candidate) || string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase)) continue;
            var candidateName = Path.GetFileName(candidate);
            var comparison = nameComparer.Compare(candidateName, requestedName);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(candidate, full);
            if (comparison < 0)
            {
                lower.Enqueue(candidate, candidateName);
                if (lower.Count > neighbourCapacity) lower.Dequeue();
            }
            else
            {
                upper.Enqueue(candidate, candidateName);
                if (upper.Count > neighbourCapacity) upper.Dequeue();
            }
        }

        var lowerOrdered = lower.UnorderedItems.Select(x => x.Element)
            .OrderBy(p => Path.GetFileName(p), nameComparer).ToArray();
        var upperOrdered = upper.UnorderedItems.Select(x => x.Element)
            .OrderBy(p => Path.GetFileName(p), nameComparer).ToArray();
        var beforeWanted = neighbourCapacity / 2;
        var afterWanted = neighbourCapacity - beforeWanted;
        var beforeCount = Math.Min(beforeWanted, lowerOrdered.Length);
        var afterCount = Math.Min(afterWanted, upperOrdered.Length);
        var spare = neighbourCapacity - beforeCount - afterCount;
        if (spare > 0)
        {
            var lowerExtra = Math.Min(spare, lowerOrdered.Length - beforeCount);
            beforeCount += lowerExtra;
            spare -= lowerExtra;
            afterCount += Math.Min(spare, upperOrdered.Length - afterCount);
        }

        return lowerOrdered.Skip(lowerOrdered.Length - beforeCount)
            .Concat(new[] { full })
            .Concat(upperOrdered.Take(afterCount))
            .ToArray();
    }

    public string? Move(int delta)
    {
        if (_paths.Count == 0 || delta == 0) return Current;
        var next = Math.Clamp(Index + delta, 0, _paths.Count - 1);
        Index = next;
        return Current;
    }


    public string? SelectIndex(int index)
    {
        if (_paths.Count == 0) return null;
        Index = Math.Clamp(index, 0, _paths.Count - 1);
        return Current;
    }

    public string? First()
    {
        if (_paths.Count == 0) return null;
        Index = 0;
        return Current;
    }

    public string? Last()
    {
        if (_paths.Count == 0) return null;
        Index = _paths.Count - 1;
        return Current;
    }

    public static bool IsSupported(string path) => ImageFormatRegistry.IsSupported(path);
}
