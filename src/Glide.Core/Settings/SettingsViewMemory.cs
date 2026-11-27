using System.Globalization;
using System.Text;

namespace Glide.Core.Settings;

/// <summary>
/// Where the user left the Settings window: the page, each page's scroll offset and the sections (cards) whose open or
/// closed state differs from the page's built-in default. Stored as one short text value in
/// <see cref="GlideSettingsState.SettingsViewMemory"/> (so it is saved with the settings, compared by value and needs no
/// extra serialisation code), restored the next time Settings opens. It is UI memory, not an option: a "Restore
/// defaults" and "All settings" reset keep it, "Window sizes and positions" clears it with the placement.
/// Format: <c>v1;page=Interface;scroll=Interface:412.5,Mouse:80;cards=+tags-card,-general-profiles</c>.
/// Anything unreadable parses to <see cref="Empty"/> (the default page), never an error.
/// </summary>
public sealed class SettingsViewMemory
{
    public const string FormatTag = "v1";

    /// <summary>Most pages / cards remembered (a corrupt or hand-edited value cannot grow without bound).</summary>
    public const int MaxEntries = 128;

    public static SettingsViewMemory Empty { get; } = new(string.Empty, new Dictionary<string, double>(), new Dictionary<string, bool>());

    public SettingsViewMemory(string page, IReadOnlyDictionary<string, double> scroll, IReadOnlyDictionary<string, bool> cards)
    {
        Page = IsSafeToken(page) ? page : string.Empty;
        Scroll = Clean(scroll, (k, v) => v > 0.5 && v < 1_000_000 && !double.IsNaN(v), v => Math.Round(v, 1));
        Cards = Clean(cards, (_, _) => true, v => v);
    }

    /// <summary>The page key (Settings navigation key such as "Interface"), or empty when none is remembered.</summary>
    public string Page { get; }

    /// <summary>Vertical scroll offset per page key (only pages that were scrolled).</summary>
    public IReadOnlyDictionary<string, double> Scroll { get; }

    /// <summary>Card id to expanded, for the cards that differ from their built-in default.</summary>
    public IReadOnlyDictionary<string, bool> Cards { get; }

    public bool IsEmpty => Page.Length == 0 && Scroll.Count == 0 && Cards.Count == 0;

    public SettingsViewMemory WithPage(string page) => new(page, Scroll, Cards);

    public string Serialize()
    {
        if (IsEmpty) return string.Empty;
        var sb = new StringBuilder(FormatTag);
        if (Page.Length > 0) sb.Append(";page=").Append(Page);
        if (Scroll.Count > 0)
            sb.Append(";scroll=").Append(string.Join(',', Scroll.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Key + ":" + x.Value.ToString("0.#", CultureInfo.InvariantCulture))));
        if (Cards.Count > 0)
            sb.Append(";cards=").Append(string.Join(',', Cards.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Value ? "+" : "-") + x.Key)));
        return sb.ToString();
    }

    public static SettingsViewMemory Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        try
        {
            var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], FormatTag, StringComparison.Ordinal)) return Empty;
            var page = string.Empty;
            var scroll = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var cards = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Skip(1))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq];
                var value = part[(eq + 1)..];
                switch (name)
                {
                    case "page":
                        page = value;
                        break;
                    case "scroll":
                        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var colon = item.LastIndexOf(':');
                            if (colon <= 0 || !double.TryParse(item[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
                            scroll[item[..colon]] = y;
                        }
                        break;
                    case "cards":
                        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (item.Length > 1 && item[0] is '+' or '-') cards[item[1..]] = item[0] == '+';
                        break;
                }
            }
            return new SettingsViewMemory(page, scroll, cards);
        }
        catch
        {
            return Empty;
        }
    }

    private static Dictionary<string, T> Clean<T>(IReadOnlyDictionary<string, T>? source, Func<string, T, bool> keep, Func<T, T> normalise)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var (key, value) in source)
        {
            if (result.Count >= MaxEntries) break;
            if (IsSafeToken(key) && keep(key, value)) result[key] = normalise(value);
        }
        return result;
    }

    /// <summary>A key that survives the text format: non-empty, no separators or control characters.</summary>
    private static bool IsSafeToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= 64 && token.All(c => !char.IsControl(c) && c is not (';' or ',' or ':' or '=' or '+' ));

    public override bool Equals(object? obj) => obj is SettingsViewMemory other && Serialize() == other.Serialize();

    public override int GetHashCode() => Serialize().GetHashCode(StringComparison.Ordinal);
}
