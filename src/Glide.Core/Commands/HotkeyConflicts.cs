namespace Glide.Core.Commands;

/// <summary>
/// Where a keyboard action is live. The hotkey map is one flat table that the main window resolves for
/// every key press, so a shortcut can have exactly one owner. The scope only describes how widely the
/// existing owner is felt, so the replace prompt can say "this works everywhere" or "this works while
/// viewing images".
/// </summary>
public enum HotkeyScope
{
    /// <summary>Window, tab, workspace, settings, external-program actions: live in every tab.</summary>
    Global,
    /// <summary>Viewing, navigation, slideshow, selection, file and overlay actions: live while an image is shown.</summary>
    Viewer
}

public enum HotkeyConflictKind
{
    /// <summary>Nobody owns the shortcut; it can simply be assigned.</summary>
    None,
    /// <summary>The target action already owns the shortcut (and nobody else does): assigning again is a no-op.</summary>
    SameAction,
    /// <summary>Another action owns the shortcut: the user must choose Replace or Cancel.</summary>
    OtherAction
}

public sealed record HotkeyOwner(string ActionId, string Label, string Category, HotkeyScope Scope);

public sealed record HotkeyConflict(
    HotkeyConflictKind Kind,
    string Shortcut,
    string TargetActionId,
    string TargetLabel,
    HotkeyScope TargetScope,
    IReadOnlyList<HotkeyOwner> Owners)
{
    public bool NeedsConfirmation => Kind == HotkeyConflictKind.OtherAction;

    /// <summary>True when the existing owner and the new action are live in different places.</summary>
    public bool CrossesScopes => Owners.Any(o => o.Scope != TargetScope);

    public string OwnerNames => string.Join(", ", Owners.Select(o => o.Label));

    /// <summary>The question shown in the Replace / Cancel prompt.</summary>
    public string PromptText()
    {
        var owners = OwnerNames;
        var text = $"{Shortcut} is already assigned to â€œ{owners}â€.\n\nReplace it? {Shortcut} will then run â€œ{TargetLabel}â€, and â€œ{owners}â€ will have that shortcut removed.";
        if (CrossesScopes)
        {
            var global = Owners.Any(o => o.Scope == HotkeyScope.Global);
            text += global
                ? $"\n\nâ€œ{owners}â€ works in every tab, so replacing it takes this shortcut away everywhere."
                : $"\n\nâ€œ{owners}â€ only works while viewing an image; â€œ{TargetLabel}â€ works in every tab, so after replacing it {Shortcut} will also act in Explorer and Home tabs.";
        }
        return text;
    }
}

/// <summary>What <see cref="HotkeyConflicts.Assign"/> did.</summary>
public enum HotkeyAssignOutcome
{
    /// <summary>The shortcut was added (and the old one removed when changing).</summary>
    Assigned,
    /// <summary>The action already had exactly this shortcut and nothing changed.</summary>
    AlreadyAssigned,
    /// <summary>Another action owns it and replacement was not confirmed: nothing was changed.</summary>
    Declined
}

/// <summary>
/// Conflict detection and exclusive assignment for the keyboard shortcut map. Pure and Avalonia-free so
/// the Settings window, tests and diagnostics reason about the same rules.
/// </summary>
public static class HotkeyConflicts
{
    public static HotkeyScope ScopeOf(HotkeyActionDefinition action) => action.Category switch
    {
        "Application" or "Tabs" or "Workspace" or "External" or AppActionRegistry.SettingsCategory or "Explorer" => HotkeyScope.Global,
        _ => HotkeyScope.Viewer
    };

    public static HotkeyScope ScopeOf(string actionId) =>
        HotkeyCatalog.Find(actionId) is { } action ? ScopeOf(action) : HotkeyScope.Viewer;

    /// <summary>Every action that has <paramref name="shortcut"/> bound (case-insensitive), in catalogue order.</summary>
    public static IReadOnlyList<HotkeyOwner> OwnersOf(IReadOnlyDictionary<string, List<string>> map, string shortcut)
    {
        var owners = new List<HotkeyOwner>();
        if (string.IsNullOrWhiteSpace(shortcut)) return owners;
        foreach (var action in HotkeyCatalog.All)
        {
            if (!map.TryGetValue(action.Id, out var assigned)) continue;
            if (assigned.Any(x => ShortcutText.AreEqual(x, shortcut)))
                owners.Add(new HotkeyOwner(action.Id, action.Label, action.Category, ScopeOf(action)));
        }
        return owners;
    }

    public static HotkeyConflict Check(IReadOnlyDictionary<string, List<string>> map, string shortcut, string actionId)
    {
        var target = HotkeyCatalog.Find(actionId);
        var targetLabel = target?.Label ?? actionId;
        var scope = target is null ? HotkeyScope.Viewer : ScopeOf(target);
        var all = OwnersOf(map, shortcut);
        var others = all.Where(o => !string.Equals(o.ActionId, actionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var kind = others.Length > 0 ? HotkeyConflictKind.OtherAction
            : all.Count > 0 ? HotkeyConflictKind.SameAction
            : HotkeyConflictKind.None;
        return new HotkeyConflict(kind, shortcut, actionId, targetLabel, scope, others);
    }

    /// <summary>
    /// Assigns <paramref name="shortcut"/> to <paramref name="actionId"/>. With <paramref name="add"/> the
    /// action keeps its other shortcuts; otherwise <paramref name="oldShortcut"/> (when given) is replaced.
    /// If another action owns the shortcut nothing changes unless <paramref name="replaceOthers"/> is true,
    /// in which case the shortcut is removed from those owners first.
    /// </summary>
    public static HotkeyAssignOutcome Assign(
        Dictionary<string, List<string>> map,
        string shortcut,
        string actionId,
        string? oldShortcut,
        bool add,
        bool replaceOthers)
    {
        var conflict = Check(map, shortcut, actionId);
        if (conflict.NeedsConfirmation && !replaceOthers) return HotkeyAssignOutcome.Declined;

        if (conflict.NeedsConfirmation)
        {
            foreach (var owner in conflict.Owners)
                if (map.TryGetValue(owner.ActionId, out var list))
                    list.RemoveAll(x => ShortcutText.AreEqual(x, shortcut));
        }

        if (!map.TryGetValue(actionId, out var assigned))
            map[actionId] = assigned = new List<string>();

        var replacingDifferent = !add && !string.IsNullOrWhiteSpace(oldShortcut) &&
                                 !ShortcutText.AreEqual(oldShortcut, shortcut);
        var already = assigned.Any(x => ShortcutText.AreEqual(x, shortcut));
        if (already && !replacingDifferent && !conflict.NeedsConfirmation) return HotkeyAssignOutcome.AlreadyAssigned;

        if (replacingDifferent)
            assigned.RemoveAll(x => ShortcutText.AreEqual(x, oldShortcut));
        if (!already) assigned.Add(shortcut);
        return HotkeyAssignOutcome.Assigned;
    }
}

/// <summary>"Search hotkey": which action(s) a pressed key or combination is bound to.</summary>
public static class HotkeyLookup
{
    /// <summary>Read-only shortcuts (Explorer list keys, slideshow Space...) whose key list names this combination.</summary>
    public static IReadOnlyList<FixedShortcutDefinition> FixedOwners(string shortcut) =>
        HotkeyCatalog.FixedShortcuts.Where(f => f.Shortcut.Split(" / ").Any(part => ShortcutText.AreEqual(part, shortcut))).ToArray();

    public static IReadOnlyList<HotkeyOwner> Find(IReadOnlyDictionary<string, List<string>> map, string shortcut) =>
        HotkeyConflicts.OwnersOf(map, shortcut);

    /// <summary>One clear sentence for the status line under the list.</summary>
    public static string Describe(IReadOnlyDictionary<string, List<string>> map, string shortcut)
    {
        var owners = Find(map, shortcut);
        if (owners.Count == 0)
        {
            var fixedOwner = FixedOwners(shortcut);
            return fixedOwner.Count == 0
                ? $"{shortcut} is not assigned to any action."
                : $"{shortcut} is not assigned to any editable action. Fixed: {string.Join("; ", fixedOwner.Select(f => f.Label))}.";
        }
        return owners.Count == 1
            ? $"{shortcut} is assigned to: {owners[0].Label} ({owners[0].Category})."
            : $"{shortcut} is assigned to {owners.Count} actions: {string.Join("; ", owners.Select(o => o.Label))}.";
    }
}
