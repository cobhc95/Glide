namespace Glide.Core.Commands;

public enum HotkeySortColumn { None, Shortcut, Action, Category }

/// <summary>One row of the Settings hotkey list: an action with one of its shortcuts (empty = unassigned).</summary>
public sealed record HotkeyListRow(string ActionId, string Shortcut, string Label, string Category);

/// <summary>
/// Column-header sorting state for the hotkey list. The default (None) keeps the catalogue order; clicking
/// a header sorts by that column ascending and clicking the same header again flips the direction.
/// </summary>
public readonly record struct HotkeySortState(HotkeySortColumn Column, bool Descending)
{
    public static HotkeySortState Default => new(HotkeySortColumn.None, false);

    public HotkeySortState Toggle(HotkeySortColumn clicked)
    {
        if (clicked == HotkeySortColumn.None) return Default;
        return clicked == Column ? new HotkeySortState(clicked, !Descending) : new HotkeySortState(clicked, false);
    }

    /// <summary>Arrow shown in that column's header; empty for columns that are not the sort key.</summary>
    public string Indicator(HotkeySortColumn column) =>
        column == Column && column != HotkeySortColumn.None ? (Descending ? "▼" : "▲") : string.Empty;
}

public static class HotkeyListSort
{
    /// <summary>
    /// Orders rows by the chosen column using Explorer-style natural order (F2 before F11). Unassigned rows
    /// always sort last for the Shortcut column, in either direction, so assigned keys stay on top. Ties keep
    /// catalogue order (a stable sort), then break by action label.
    /// </summary>
    public static List<HotkeyListRow> Sort(IEnumerable<HotkeyListRow> rows, HotkeySortState state)
    {
        var indexed = rows.Select((row, index) => (row, index)).ToList();
        if (state.Column == HotkeySortColumn.None) return indexed.Select(x => x.row).ToList();

        var comparer = NaturalStringComparer.Instance;
        int Primary(HotkeyListRow a, HotkeyListRow b) => state.Column switch
        {
            HotkeySortColumn.Shortcut => comparer.Compare(a.Shortcut, b.Shortcut),
            HotkeySortColumn.Action => comparer.Compare(a.Label, b.Label),
            HotkeySortColumn.Category => comparer.Compare(a.Category, b.Category),
            _ => 0
        };

        indexed.Sort((x, y) =>
        {
            if (state.Column == HotkeySortColumn.Shortcut)
            {
                var xe = string.IsNullOrWhiteSpace(x.row.Shortcut);
                var ye = string.IsNullOrWhiteSpace(y.row.Shortcut);
                if (xe != ye) return xe ? 1 : -1;
                if (xe) return TieBreak(x, y, comparer);
            }
            var result = Primary(x.row, y.row);
            if (result != 0) return state.Descending ? -result : result;
            return TieBreak(x, y, comparer);
        });
        return indexed.Select(x => x.row).ToList();
    }

    private static int TieBreak((HotkeyListRow row, int index) x, (HotkeyListRow row, int index) y, NaturalStringComparer comparer)
    {
        var byLabel = comparer.Compare(x.row.Label, y.row.Label);
        return byLabel != 0 ? byLabel : x.index.CompareTo(y.index);
    }
}
