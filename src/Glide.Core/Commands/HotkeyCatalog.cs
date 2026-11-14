namespace Glide.Core.Commands;

/// <summary>
/// Canonical keyboard-action catalogue. It is intentionally Avalonia-free so Settings, diagnostics
/// and tests can reason about shortcuts without constructing UI. Shortcut strings are normalized by
/// the UI/input adapter (for example Ctrl+Shift+T, F11, Right, Space).
/// </summary>
public sealed record HotkeyActionDefinition(
    string Id,
    string Label,
    string Category,
    GlideCommand Command,
    string[] DefaultShortcuts,
    string[]? SearchTerms = null,
    string? ActionId = null);

/// <summary>A read-only shortcut owned by one surface (Explorer list keys, slideshow Space...). Shown in the
/// hotkey list and found by Settings search, but never rebindable and never part of the saved hotkey map.</summary>
public sealed record FixedShortcutDefinition(string Id, string Shortcut, string Label, string Category, string Description, string[] SearchTerms);

public static class HotkeyCatalog
{
    /// <summary>
    /// Every editable hotkey row, read from the master <see cref="AppActionRegistry"/>. The first 75 rows are the
    /// pre-registry list (same ids, labels, categories, defaults and order, so saved maps, presets and the input
    /// router's priority are unchanged); actions added later follow, unassigned by default.
    /// </summary>
    public static IReadOnlyList<HotkeyActionDefinition> All { get; } = AppActionRegistry.All
        .SelectMany(action => action.HotkeySlots.Select(slot => new HotkeyActionDefinition(
            slot.Id, slot.Label, action.Category, action.Command, slot.DefaultShortcuts,
            action.SearchTerms.Length == 0 ? null : action.SearchTerms, action.Id)))
        .ToArray();

    /// <summary>Read-only shortcuts listed next to the editable rows.</summary>
    public static IReadOnlyList<FixedShortcutDefinition> FixedShortcuts { get; } = AppActionRegistry.All
        .Where(action => action.Kind == AppActionKind.FixedShortcut)
        .Select(action => new FixedShortcutDefinition(action.Id, action.FixedShortcut!, action.Label, action.Category, action.Description, action.SearchTerms))
        .ToArray();

    /// <summary>The ids of the 75 rows that existed before the registry (saved settings / presets use them).</summary>
    public static readonly string[] LegacyIds =
    [
        "file.open", "file.openFolder", "app.settings", "folder.refresh", "workspace.home", "folder.previous", "folder.next",
        "folder.exploreParent", "slideshow.startPause", "slideshow.stop", "view.info", "view.rotateLeft", "view.rotateRight",
        "view.flipHorizontal", "view.flipVertical", "nav.nextSpace", "nav.nextRight", "nav.nextPage", "nav.previousBack",
        "nav.previousLeft", "nav.previousPage", "nav.first", "nav.last", "view.fullscreen", "view.fit", "view.fitWidth",
        "view.fitHeight", "view.fitWindow", "view.actual", "view.toggleFitActual", "view.zoomIn", "view.zoomOut", "gif.playPause",
        "gif.previousFrame", "gif.nextFrame", "selection.clear", "tab.new", "tab.close", "tab.restore", "tab.duplicate",
        "window.newSameImage", "window.duplicateSession", "tab.next", "tab.previous", "tab.select1", "tab.select2", "tab.select3",
        "tab.select4", "tab.select5", "tab.select6", "tab.select7", "tab.select8", "tab.selectLast", "tab.detach", "window.close",
        "window.alwaysTop", "status.toggle", "window.transparency", "view.contextMenu", "overlay.add", "overlay.saveLayout",
        "overlay.loadLayout", "file.copyImage", "file.copyFile", "file.copyName", "file.copyFolder", "file.copyPath", "file.rename",
        "file.delete", "file.print", "file.save", "file.saveAs", "external.1", "external.2", "external.3"
    ];

    public static Dictionary<string, List<string>> CreateDefaultMap() =>
        All.ToDictionary(x => x.Id, x => x.DefaultShortcuts.ToList(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> PresetNames { get; } =
        ["Glide default", "Windows Photos", "IrfanView", "nomacs", "FastStone", "XnView MP"];

    /// <summary>
    /// Returns a complete editable shortcut map for a named stock familiarity preset. Presets are
    /// opt-in starting points only: Glide default is canonical and remains the fresh-install state.
    /// AssignExclusive removes a borrowed shortcut from any other action before assigning it, so a
    /// compatibility profile never leaves ambiguous duplicate bindings behind.
    /// </summary>
    public static Dictionary<string, List<string>> CreatePresetMap(string? preset)
    {
        var map = CreateDefaultMap();
        if (string.IsNullOrWhiteSpace(preset) || string.Equals(preset, "Glide default", StringComparison.OrdinalIgnoreCase))
            return map;

        void AssignExclusive(string id, params string[] shortcuts)
        {
            var set = shortcuts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in map.Keys.ToArray())
                map[key].RemoveAll(x => set.Contains(x));
            map[id] = shortcuts.ToList();
        }

        void Replace(string id, params string[] shortcuts) => map[id] = shortcuts.ToList();

        if (string.Equals(preset, "Windows Photos", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the Photos-style core deliberately small: arrows browse; F11 owns fullscreen.
            // Other Glide actions remain available and editable rather than inventing Photos keys.
            Replace("nav.nextSpace");
            Replace("nav.nextPage");
            Replace("nav.previousBack");
            Replace("nav.previousPage");
            Replace("nav.nextRight", "Right");
            Replace("nav.previousLeft", "Left");
            Replace("view.fullscreen", "F11");
            return map;
        }

        if (string.Equals(preset, "IrfanView", StringComparison.OrdinalIgnoreCase))
        {
            // IrfanView familiarity: F/5/6/1 are its long-standing fit/width/height/original-size
            // viewing keys; F11/Enter provide fullscreen familiarity.
            AssignExclusive("nav.previousBack", "Backspace"); // 5.0.0: Backspace is Back by default; this preset deliberately keeps it as Previous image (Back keeps Alt+Left).
            AssignExclusive("view.fullscreen", "F11", "Enter");
            AssignExclusive("view.fit", "F", "Shift+W");
            AssignExclusive("view.fitWidth", "5", "W");
            AssignExclusive("view.fitHeight", "6");
            AssignExclusive("view.actual", "Ctrl+H", "1");
            return map;
        }

        if (string.Equals(preset, "nomacs", StringComparison.OrdinalIgnoreCase))
        {
            // nomacs upstream defaults on Windows: arrows browse, Home/End first/last, PageUp/Down
            // skip, F11 fullscreen, Ctrl+0 reset/fit, Ctrl+1 100%, Ctrl+2 fit frame.
            AssignExclusive("nav.previousBack", "Backspace"); // 5.0.0: Backspace is Back by default; this preset deliberately keeps it as Previous image (Back keeps Alt+Left).
            AssignExclusive("view.fullscreen", "F11");
            AssignExclusive("view.fit", "Ctrl+0", "Ctrl+2");
            AssignExclusive("view.actual", "Ctrl+1");
            Replace("nav.nextSpace"); // Space is slideshow in nomacs, not next-image.
            AssignExclusive("slideshow.startPause", "Space");
            return map;
        }

        if (string.Equals(preset, "FastStone", StringComparison.OrdinalIgnoreCase))
        {
            // FastStone's documented viewer navigation already matches Glide's Space/Right/PageDown
            // and Backspace/Left/PageUp conventions; restrict fullscreen to its F11 convention.
            AssignExclusive("nav.previousBack", "Backspace"); // 5.0.0: Backspace is Back by default; this preset deliberately keeps it as Previous image (Back keeps Alt+Left).
            Replace("view.fullscreen", "F11");
            return map;
        }

        if (string.Equals(preset, "XnView MP", StringComparison.OrdinalIgnoreCase))
        {
            // XnView MP keeps arrows/Home/End/Page navigation familiar. Its 100% command is *; the
            // Avalonia key name emitted for the numeric-keypad multiply key is "Multiply".
            AssignExclusive("nav.previousBack", "Backspace"); // 5.0.0: Backspace is Back by default; this preset deliberately keeps it as Previous image (Back keeps Alt+Left).
            AssignExclusive("view.actual", "Multiply");
            Replace("view.fullscreen", "F11");
            return map;
        }

        // Unknown/future preset names are fail-safe: never silently alter Glide defaults.
        return map;
    }

    public static HotkeyActionDefinition? Find(string id) =>
        All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every shortcut currently bound to a registry action (all of its hotkey rows; an alias reports its target's).</summary>
    public static IReadOnlyList<string> ShortcutsForAction(string actionId, IReadOnlyDictionary<string, List<string>> bindings)
    {
        var action = AppActionRegistry.Find(actionId);
        if (action?.AliasOf is { } alias) action = AppActionRegistry.Find(alias);
        if (action is null) return Array.Empty<string>();
        return action.HotkeySlots
            .SelectMany(slot => bindings.TryGetValue(slot.Id, out var assigned) ? (IEnumerable<string>)assigned : Array.Empty<string>())
            .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> ShortcutsForCommand(
        GlideCommand command,
        IReadOnlyDictionary<string, List<string>> bindings)
    {
        return All
            .Where(action => action.Command == command)
            .SelectMany(action => bindings.TryGetValue(action.Id, out var assigned)
                ? (IEnumerable<string>)assigned
                : Array.Empty<string>())
            .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
