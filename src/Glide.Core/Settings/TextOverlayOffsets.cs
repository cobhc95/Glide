using System.Globalization;

namespace Glide.Core.Settings;

/// <summary>
/// Text overlay offsets (5.1.2), remembered per corner anchor ("Top right", "Bottom left", ...). Stored in one string,
/// "Top right=12,-4;Bottom left=0,30": X positive moves right, Y positive moves down, in DIPs from the default place.
/// </summary>
public static class TextOverlayOffsets
{
    public const int Limit = 4000;

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
                return (Math.Clamp(x, -Limit, Limit), Math.Clamp(y, -Limit, Limit));
        }
        return (0, 0);
    }

    public static string Set(string? map, string? anchor, int x, int y)
    {
        var key = string.IsNullOrWhiteSpace(anchor) ? "Top right" : anchor.Trim();
        var kept = (map ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => { var eq = e.IndexOf('='); return eq > 0 && !string.Equals(e[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase); })
            .ToList();
        x = Math.Clamp(x, -Limit, Limit); y = Math.Clamp(y, -Limit, Limit);
        if (x != 0 || y != 0) kept.Add(string.Create(CultureInfo.InvariantCulture, $"{key}={x},{y}"));
        return string.Join(";", kept);
    }

    public static string SetX(string? map, string? anchor, int x) => Set(map, anchor, x, Get(map, anchor).Y);
    public static string SetY(string? map, string? anchor, int y) => Set(map, anchor, Get(map, anchor).X, y);
}
