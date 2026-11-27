using System.Globalization;

namespace Glide.Core.Settings;

/// <summary>
/// Text overlay offsets. 5.1.2-5.1.3 remembered one per preset ("Top right=12,-4"); since 5.1.4 (schema 23) only the
/// Custom position of each card is stored ("Custom top tabs@Bottom right=12,-4", see <see cref="GetCustom"/>): X positive
/// moves right, Y positive moves down, in DIPs from the anchor's preset place.
/// </summary>
public static class TextOverlayOffsets
{
    // No limit is stored: any int is kept as typed (5.1.7 desktop retest, E: 5000 became 500). The overlay is limited to the
    // picture area when it is drawn (TextOverlayLayout.ClampOffset), so a huge value is harmless and stays what the user typed.

    public static (int X, int Y) Get(string? map, string? anchor)
    {
        if (string.IsNullOrWhiteSpace(map) || string.IsNullOrWhiteSpace(anchor)) return (0, 0);
        foreach (var entry in map.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0 || !string.Equals(entry[..eq].Trim(), anchor.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var parts = entry[(eq + 1)..].Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                return (x, y);
        }
        return (0, 0);
    }

    public static string Set(string? map, string? anchor, int x, int y)
    {
        var key = string.IsNullOrWhiteSpace(anchor) ? "Top right" : anchor.Trim();
        var kept = (map ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => { var eq = e.IndexOf('='); return eq > 0 && !string.Equals(e[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase); })
            .ToList();
        if (x != 0 || y != 0) kept.Add(string.Create(CultureInfo.InvariantCulture, $"{key}={x},{y}"));
        return string.Join(";", kept);
    }

    private static string CustomPrefix(bool sideTabs) => sideTabs ? "Custom side tabs@" : "Custom top tabs@";

    /// <summary>
    /// 5.1.4: the Custom position of a card, stored as "Custom top tabs@Bottom right=12,-4" (anchor = a picture position;
    /// offset from that anchor's preset place). Missing: Top right with no offset.
    /// </summary>
    public static (string Anchor, int X, int Y) GetCustom(string? map, bool sideTabs)
    {
        var prefix = CustomPrefix(sideTabs);
        foreach (var entry in (map ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var key = entry[..eq].Trim();
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var (x, y) = Get(entry, key);
            return (PictureAnchor(key[prefix.Length..]), x, y);
        }
        return (TextOverlayPlacements.Default, 0, 0);
    }

    public static string SetCustom(string? map, bool sideTabs, string? anchor, int x, int y)
    {
        var prefix = CustomPrefix(sideTabs);
        var kept = (map ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => { var eq = e.IndexOf('='); return eq > 0 && !e[..eq].Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase); })
            .ToList();
        kept.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}{PictureAnchor(anchor)}={x},{y}"));
        return string.Join(";", kept);
    }

    private static string PictureAnchor(string? anchor)
    {
        var a = TextOverlayPlacements.Normalize(anchor);
        return TextOverlayPlacements.PicturePositions.Contains(a) ? a : TextOverlayPlacements.Default;
    }

    /// <summary>The map without the retired per-preset entries (schema 23 migration).</summary>
    public static string OnlyCustom(string? map) =>
        string.Join(";", (map ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.TrimStart().StartsWith("Custom ", StringComparison.OrdinalIgnoreCase) && e.Contains('@')));

    public static string SetX(string? map, string? anchor, int x) => Set(map, anchor, x, Get(map, anchor).Y);
    public static string SetY(string? map, string? anchor, int y) => Set(map, anchor, Get(map, anchor).X, y);
}
