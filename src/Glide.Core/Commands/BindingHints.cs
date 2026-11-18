namespace Glide.Core.Commands;

/// <summary>One part of a hint line drawn as a key chip and a word: [Middle] close (5.1.0 tab hover card).</summary>
public readonly record struct HintChip(string Input, string Action)
{
    public override string ToString() => $"[{Input}] {Action}";
}

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

    /// <summary>The short chip label of a slot's input: "Middle", "Double-click", "Right".</summary>
    public static string InputChip(string slotId) => GestureCatalog.FindSlot(slotId)?.Input switch
    {
        GestureInputs.LeftClick => "Click",
        GestureInputs.RightClick => "Right",
        GestureInputs.MiddleClick => "Middle",
        GestureInputs.DoubleLeft => "Double-click",
        GestureInputs.DoubleRight => "Double-right",
        { } other => GestureInputs.BaseLabel(other),
        null => slotId
    };

    /// <summary>
    /// Mouse hint chips of a tab's hover card (5.1.0, design B): [Drag] move · [Middle] close · [Right] more ·
    /// [Ctrl+Wheel] size. The close chip names the first input that really closes the tab (middle-click, else
    /// double-click; a custom gesture on the same input counts) and is left out when neither does; the right-click chip
    /// only while that slot still opens the menu; the size chip shows the "Resize preview with wheel and" key
    /// (<paramref name="resizeModifier"/>) and is left out when it is None.
    /// </summary>
    public static IReadOnlyList<HintChip> TabMouseHintChips(IReadOnlyDictionary<string, string>? bindings, IReadOnlyList<CustomGesture>? customGestures,
        string? resizeModifier)
    {
        var chips = new List<HintChip> { new("Drag", "move") };
        var closer = new[] { "tabs.middleClick", "tabs.doubleClick" }
            .FirstOrDefault(slot => GestureRuns(slot, GestureCatalog.CloseTab, bindings, customGestures, defaultRunsAction: true));
        if (closer is not null) chips.Add(new(InputChip(closer), "close"));
        if (GestureRuns("tabs.rightClick", GestureCatalog.ContextMenu, bindings, customGestures, defaultRunsAction: true))
            chips.Add(new("Right", "more"));
        var modifier = Glide.Core.Workspace.TabPreviewSize.NormalizeModifier(resizeModifier);
        if (!string.Equals(modifier, "None", StringComparison.OrdinalIgnoreCase)) chips.Add(new($"{modifier}+Wheel", "size"));
        return chips;
    }

    /// <summary>The chips as one plain line ("[Drag] move · [Middle] close · …"), e.g. for diagnostics and tests.</summary>
    public static string TabMouseHint(IReadOnlyDictionary<string, string>? bindings, IReadOnlyList<CustomGesture>? customGestures,
        string? resizeModifier = "None") =>
        string.Join(" · ", TabMouseHintChips(bindings, customGestures, resizeModifier));
}
