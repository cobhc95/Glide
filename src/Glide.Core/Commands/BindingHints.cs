namespace Glide.Core.Commands;

/// <summary>
/// Pure text helpers for hints that describe a user-configurable binding ("Double-click to close", "Open an image with
/// Ctrl+O"). Every such string is derived from the live hotkey map / gesture bindings through here, so a hint never
/// claims something the user switched off. Call these lazily (when a tooltip opens or a page is shown), never at startup.
/// </summary>
public static class BindingHints
{
    /// <summary>"Open file (O / Ctrl+O)", or just "Open file" when nothing is bound.</summary>
    public static string WithShortcuts(string text, IReadOnlyList<string> shortcuts) =>
        shortcuts.Count == 0 ? text : $"{text} ({string.Join(" / ", shortcuts)})";

    /// <summary>The live keys of a registry action as "O / Ctrl+O"; empty when it has none.</summary>
    public static string ShortcutList(string actionId, IReadOnlyDictionary<string, List<string>> bindings) =>
        string.Join(" / ", HotkeyCatalog.ShortcutsForAction(actionId, bindings));

    /// <summary>The keys of a non-editable registry shortcut (AppActionRegistry Fixed entry), e.g. "Ctrl+F"; null when unknown.</summary>
    public static string? FixedShortcut(string actionId) =>
        AppActionRegistry.Find(actionId) is { Kind: AppActionKind.FixedShortcut, FixedShortcut: { Length: > 0 } keys } ? keys : null;

    /// <summary>
    /// True when the plain (no modifier key) press of a gesture slot currently runs <paramref name="actionId"/>:
    /// a custom gesture or the slot binding names it, or the slot is on "Default" and
    /// <paramref name="defaultRunsAction"/> says Glide's built-in default is that action.
    /// </summary>
    public static bool GestureRuns(string slotId, string actionId, IReadOnlyDictionary<string, string>? bindings,
        IReadOnlyList<CustomGesture>? customGestures, bool defaultRunsAction)
    {
        var resolved = GestureCatalog.ResolveAction(slotId, bindings, customGestures, GestureModifiers.None);
        return string.Equals(resolved, actionId, StringComparison.OrdinalIgnoreCase)
            || (defaultRunsAction && string.Equals(resolved, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How a user names the physical input of a slot in a sentence ("middle-click", "double-click").</summary>
    public static string InputPhrase(string slotId) => GestureCatalog.FindSlot(slotId)?.Input switch
    {
        GestureInputs.LeftClick => "click",
        GestureInputs.RightClick => "right-click",
        GestureInputs.MiddleClick => "middle-click",
        GestureInputs.DoubleLeft => "double-click",
        GestureInputs.DoubleRight => "double-right-click",
        GestureInputs.BackButton => "mouse back button",
        GestureInputs.ForwardButton => "mouse forward button",
        { } other => GestureInputs.BaseLabel(other).ToLowerInvariant(),
        null => slotId
    };

    /// <summary>
    /// One-line mouse hint of a tab's tooltip (5.0.6 hover card): "Drag to move · Middle-click to close · Right-click for
    /// more". The close part names the first input that really closes the tab (middle-click, else double-click; a custom
    /// gesture on the same input counts) and is left out when neither does; the right-click part only while that slot
    /// still opens the menu.
    /// </summary>
    public static string TabMouseHint(IReadOnlyDictionary<string, string>? bindings, IReadOnlyList<CustomGesture>? customGestures)
    {
        var parts = new List<string> { "Drag to move" };
        var closer = new[] { "tabs.middleClick", "tabs.doubleClick" }
            .FirstOrDefault(slot => GestureRuns(slot, GestureCatalog.CloseTab, bindings, customGestures, defaultRunsAction: true));
        if (closer is not null)
        {
            var phrase = InputPhrase(closer);
            parts.Add($"{char.ToUpperInvariant(phrase[0])}{phrase[1..]} to close");
        }
        if (GestureRuns("tabs.rightClick", GestureCatalog.ContextMenu, bindings, customGestures, defaultRunsAction: true))
            parts.Add("Right-click for more");
        return string.Join(" · ", parts);
    }
}
