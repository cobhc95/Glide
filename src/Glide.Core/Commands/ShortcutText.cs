namespace Glide.Core.Commands;

/// <summary>
/// Canonical text form of a keyboard shortcut, shared by the hotkey table, the input router, conflict
/// detection and "Search hotkey". The UI adapter (KeyboardShortcutText) always writes modifiers in the
/// order Ctrl, Shift, Alt; saved settings, presets and hand-written defaults may not (the 5.0.0 default
/// "Alt+Shift+S" never matched the pressed "Shift+Alt+S", so the status-bar hotkey did nothing).
/// Comparing canonical forms makes modifier order and letter case irrelevant everywhere.
/// </summary>
public static class ShortcutText
{
    private static readonly string[] ModifierOrder = ["Ctrl", "Shift", "Alt"];

    /// <summary>Returns the canonical form ("Ctrl+Shift+Alt+Key"), or the trimmed input when it has no key part.</summary>
    public static string Canonicalize(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return string.Empty;
        var text = shortcut.Trim();
        // "+" is itself a key: "+", "Ctrl++" and "Ctrl+Shift++" end with the plus key.
        string key;
        string modifierPart;
        if (text == "+") return "+";
        if (text.EndsWith("++", StringComparison.Ordinal))
        {
            key = "+";
            modifierPart = text[..^2];
        }
        else
        {
            var last = text.LastIndexOf('+');
            if (last < 0) return NormalizeKey(text);
            key = text[(last + 1)..];
            modifierPart = text[..last];
        }

        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in modifierPart.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var modifier = NormalizeModifier(raw);
            if (modifier is null) return text; // not a shortcut we understand: compare it verbatim
            modifiers.Add(modifier);
        }
        var parts = ModifierOrder.Where(modifiers.Contains).ToList();
        parts.Add(NormalizeKey(key));
        return string.Join("+", parts);
    }

    /// <summary>True when both strings name the same key combination.</summary>
    public static bool AreEqual(string? a, string? b) =>
        string.Equals(Canonicalize(a), Canonicalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the text is already in canonical form (used by the registry guard tests).</summary>
    public static bool IsCanonical(string shortcut) => string.Equals(Canonicalize(shortcut), shortcut, StringComparison.Ordinal);

    private static string? NormalizeModifier(string raw) => raw.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => "Ctrl",
        "SHIFT" => "Shift",
        "ALT" or "MENU" => "Alt",
        _ => null
    };

    private static string NormalizeKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 1) return char.IsLetter(trimmed[0]) ? trimmed.ToUpperInvariant() : trimmed;
        return trimmed.ToUpperInvariant() switch
        {
            "ESC" or "ESCAPE" => "Esc",
            "SPACE" => "Space",
            "ENTER" or "RETURN" => "Enter",
            "TAB" => "Tab",
            "BACK" or "BACKSPACE" => "Backspace",
            "DEL" or "DELETE" => "Delete",
            "LEFT" => "Left",
            "RIGHT" => "Right",
            "UP" => "Up",
            "DOWN" => "Down",
            "PAGEUP" or "PRIOR" => "PageUp",
            "PAGEDOWN" or "NEXT" => "PageDown",
            "HOME" => "Home",
            "END" => "End",
            "INSERT" => "Insert",
            var upper when upper.Length is 2 or 3 && upper[0] == 'F' && int.TryParse(upper[1..], out _) => upper,
            _ => trimmed
        };
    }
}
