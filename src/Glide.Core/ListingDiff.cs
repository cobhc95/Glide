namespace Glide.Core;

/// <summary>
/// An in-place edit script that turns a displayed, sorted list into a new sorted list: remove the
/// <see cref="RemoveAt"/> indexes (descending, so earlier removals never shift later ones), then insert the
/// <see cref="Inserts"/> (ascending target indexes). Items present in both lists with equal content and the same
/// relative order are kept as they are (same instance), so a view bound to the list keeps their realized
/// elements (Explorer tiles keep their thumbnails, Vertigo round 3 #15).
/// </summary>
public sealed record ListingDiff<T>(IReadOnlyList<int> RemoveAt, IReadOnlyList<(int Index, T Item)> Inserts)
{
    public bool IsEmpty => RemoveAt.Count == 0 && Inserts.Count == 0;
    public int OperationCount => RemoveAt.Count + Inserts.Count;

    /// <summary>Applies the script to <paramref name="list"/> (an observable collection raises one change per step).</summary>
    public void ApplyTo(IList<T> list)
    {
        foreach (var index in RemoveAt) list.RemoveAt(index);
        foreach (var (index, item) in Inserts) list.Insert(index, item);
    }
}

public static class ListingDiff
{
    /// <summary>
    /// Computes the edit script from <paramref name="current"/> to <paramref name="target"/>. Items are matched by
    /// <paramref name="key"/> (case-insensitive path); a matched item whose content differs per <paramref name="same"/>
    /// (size / date changed) is replaced. Matched items whose relative order changed (a re-sort moved them) are moved
    /// with the fewest moves (longest increasing subsequence). The result keeps the <paramref name="current"/> instance
    /// for every kept item; inserted items come from <paramref name="target"/>.
    /// </summary>
    public static ListingDiff<T> Compute<T>(IReadOnlyList<T> current, IReadOnlyList<T> target, Func<T, string> key, Func<T, T, bool>? same = null)
    {
        var targetIndex = new Dictionary<string, int>(target.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < target.Count; i++) targetIndex.TryAdd(key(target[i]), i);

        // Candidates: current positions with their target position (or -1 when gone / changed / duplicate).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapped = new int[current.Count];
        for (var i = 0; i < current.Count; i++)
        {
            var k = key(current[i]);
            mapped[i] = seen.Add(k) && targetIndex.TryGetValue(k, out var t) && (same is null || same(current[i], target[t])) ? t : -1;
        }

        // Keep the longest increasing run of target positions; everything else is removed (and re-inserted if still listed).
        var keep = LongestIncreasing(mapped);
        var removals = new List<int>();
        var keptTargets = new HashSet<int>();
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (keep[i]) keptTargets.Add(mapped[i]);
            else removals.Add(i);
        }
        var inserts = new List<(int, T)>();
        for (var t = 0; t < target.Count; t++)
            if (!keptTargets.Contains(t)) inserts.Add((t, target[t]));
        return new ListingDiff<T>(removals, inserts);
    }

    /// <summary>Marks a longest strictly increasing subsequence of the non-negative values.</summary>
    private static bool[] LongestIncreasing(int[] values)
    {
        var keep = new bool[values.Length];
        var tailValues = new List<int>();
        var tailIndexes = new List<int>();
        var previous = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (v < 0) continue;
            int lo = 0, hi = tailValues.Count;
            while (lo < hi) { var mid = (lo + hi) / 2; if (tailValues[mid] < v) lo = mid + 1; else hi = mid; }
            previous[i] = lo > 0 ? tailIndexes[lo - 1] : -1;
            if (lo == tailValues.Count) { tailValues.Add(v); tailIndexes.Add(i); }
            else { tailValues[lo] = v; tailIndexes[lo] = i; }
        }
        for (var i = tailIndexes.Count > 0 ? tailIndexes[^1] : -1; i >= 0; i = previous[i]) keep[i] = true;
        return keep;
    }
}
