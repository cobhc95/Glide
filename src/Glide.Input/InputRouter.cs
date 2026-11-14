using Glide.Core.Commands;

namespace Glide.Input;

/// <summary>
/// Converts canonical physical shortcut strings into registry actions / semantic Glide commands. The UI adapter
/// owns platform key naming; this type owns conflict-free lookup only. Shortcuts are compared in canonical form
/// (<see cref="ShortcutText"/>), so modifier order ("Alt+Shift+S" vs the pressed "Shift+Alt+S") and letter case
/// never decide whether a binding fires.
/// </summary>
public sealed class InputRouter
{
    /// <summary>The registry action bound to <paramref name="shortcut"/>, or null. First hotkey row in list order wins.</summary>
    public AppAction? ResolveShortcutAction(string shortcut, IReadOnlyDictionary<string, List<string>> bindings)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        var canonical = ShortcutText.Canonicalize(shortcut);
        foreach (var definition in HotkeyCatalog.All)
        {
            if (!bindings.TryGetValue(definition.Id, out var assigned)) continue;
            if (assigned.Any(x => string.Equals(ShortcutText.Canonicalize(x), canonical, StringComparison.OrdinalIgnoreCase)))
                return AppActionRegistry.FindByHotkeySlot(definition.Id);
        }
        return null;
    }

    public GlideCommand ResolveShortcut(string shortcut, IReadOnlyDictionary<string, List<string>> bindings) =>
        ResolveShortcutAction(shortcut, bindings)?.Command ?? GlideCommand.None;

    public GlideCommand ResolveShortcut(string shortcut)
    {
        var canonical = ShortcutText.Canonicalize(shortcut);
        foreach (var definition in HotkeyCatalog.All)
            if (definition.DefaultShortcuts.Any(x => string.Equals(ShortcutText.Canonicalize(x), canonical, StringComparison.OrdinalIgnoreCase)))
                return definition.Command;
        return GlideCommand.None;
    }


    /// <summary>Resolve one of Glide's gesture slots (see GestureCatalog.Slots) to a stable action ID.</summary>
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
