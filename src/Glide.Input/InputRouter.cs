using Glide.Core.Commands;

namespace Glide.Input;

/// <summary>
/// Converts canonical physical shortcut strings into semantic Glide commands. The UI adapter owns
/// platform key naming; this type owns conflict-free lookup only. This keeps hotkey remapping testable.
/// </summary>
public sealed class InputRouter
{
    public GlideCommand ResolveShortcut(string shortcut, IReadOnlyDictionary<string, List<string>> bindings)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return GlideCommand.None;
        foreach (var definition in HotkeyCatalog.All)
        {
            if (!bindings.TryGetValue(definition.Id, out var assigned)) continue;
            if (assigned.Any(x => string.Equals(x, shortcut, StringComparison.OrdinalIgnoreCase)))
                return definition.Command;
        }
        return GlideCommand.None;
    }

    public GlideCommand ResolveShortcut(string shortcut)
    {
        foreach (var definition in HotkeyCatalog.All)
            if (definition.DefaultShortcuts.Any(x => string.Equals(x, shortcut, StringComparison.OrdinalIgnoreCase)))
                return definition.Command;
        return GlideCommand.None;
    }


    /// <summary>Resolve one of Glide's 24 gesture slots to a stable action ID.</summary>
    public string ResolveGesture(string slotId, IReadOnlyDictionary<string, string>? bindings) =>
        GestureCatalog.ResolveAction(slotId, bindings);

    public GlideCommand ResolveGestureCommand(string slotId, IReadOnlyDictionary<string, string>? bindings) =>
        GestureCatalog.CommandForAction(ResolveGesture(slotId, bindings));

    // Compatibility helper retained for older tests/callers. New UI code should use ResolveShortcut.
    public GlideCommand ResolveKey(string key, bool control = false, bool shift = false, bool alt = false)
    {
        var normalized = CanonicalizeLegacy(key, control, shift, alt);
        return ResolveShortcut(normalized);
    }

    private static string CanonicalizeLegacy(string key, bool control, bool shift, bool alt)
    {
        var normalized = key.Trim();
        normalized = normalized.ToUpperInvariant() switch
        {
            "LEFT" => "Left", "RIGHT" => "Right", "HOME" => "Home", "END" => "End",
            "PAGEDOWN" => "PageDown", "PAGEUP" => "PageUp", "BACKSPACE" => "Backspace",
            "ESCAPE" => "Esc", "SPACE" => "Space", "ENTER" => "Enter", "TAB" => "Tab",
            "F11" => "F11", "F4" => "F4", "DELETE" => "Delete", _ => normalized.ToUpperInvariant()
        };
        var parts = new List<string>();
        if (control) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(normalized);
        return string.Join("+", parts);
    }
}
