namespace Glide.Core.Renaming;

/// <summary>One remembered batch rename expression and whether the "Regular expression" box was ticked for it.</summary>
public sealed record RenameHistoryEntry(string Text, bool Regex)
{
    /// <summary>How the entry is listed: regular expressions carry a marker so they can be told apart.</summary>
    public string Display => Regex ? Text + "   (regex)" : Text;
}

/// <summary>
/// The remembered inputs of the batch rename dialog: newest first, no duplicates, at most <see cref="Capacity"/>.
/// Stored as one string in the settings (one entry per line: "T" or "R", a tab, the text), so Reset, backup and profiles
/// carry it without any extra file.
/// </summary>
public static class RenameInputHistory
{
    public const int Capacity = 30;

    public static IReadOnlyList<RenameHistoryEntry> Parse(string? stored)
    {
        var list = new List<RenameHistoryEntry>();
        if (string.IsNullOrEmpty(stored)) return list;
        foreach (var line in stored.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.Length < 3 || row[1] != '\t' || row[0] is not ('T' or 'R')) continue;
            var text = row[2..];
            if (text.Length == 0 || list.Any(e => e.Text == text && e.Regex == (row[0] == 'R'))) continue;
            list.Add(new RenameHistoryEntry(text, row[0] == 'R'));
            if (list.Count == Capacity) break;
        }
        return list;
    }

    public static string Serialize(IEnumerable<RenameHistoryEntry> entries) =>
        string.Join("\n", entries.Take(Capacity).Select(e => (e.Regex ? "R\t" : "T\t") + e.Text));

    /// <summary>The list with <paramref name="entry"/> moved (or added) to the top and the oldest dropped. Blank text is ignored.</summary>
    public static IReadOnlyList<RenameHistoryEntry> Add(IReadOnlyList<RenameHistoryEntry> history, string? text, bool regex)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\n') || text.Contains('\r')) return history;
        var result = new List<RenameHistoryEntry> { new(text, regex) };
        foreach (var existing in history)
        {
            if (existing.Text == text && existing.Regex == regex) continue;
            result.Add(existing);
            if (result.Count == Capacity) break;
        }
        return result;
    }

    /// <summary>
    /// Remembered entries that match what is being typed, for the list under the box: those that start with it first, then
    /// those that contain it (ignoring case). The entry that equals the typed text is left out. Nothing typed gives none.
    /// </summary>
    public static IReadOnlyList<RenameHistoryEntry> Suggest(IReadOnlyList<RenameHistoryEntry> history, string? typed, int max = 8)
    {
        if (string.IsNullOrEmpty(typed)) return [];
        var starts = history.Where(e => e.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        var contains = history.Where(e => !e.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && e.Text.Contains(typed, StringComparison.OrdinalIgnoreCase));
        return starts.Concat(contains).Where(e => !string.Equals(e.Text, typed, StringComparison.Ordinal)).Take(max).ToArray();
    }
}
