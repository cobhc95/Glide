namespace Glide.Core.Commands;

/// <summary>
/// One-time moves of changed default shortcuts in saved settings. A settings file stores the RESOLVED shortcut of
/// every action (defaults are written out), so "still at the old default" and "the user chose the same key" look the
/// same on disk. The rule is therefore deliberately narrow and only runs for files older than the schema that
/// introduced the new default.
/// </summary>
public static class HotkeyDefaultMigrations
{
    /// <summary>Settings schema that moved "Show / hide status bar" from Shift+Alt+S (Alt+Shift+S) to Ctrl+Shift+B.</summary>
    public const int StatusToggleMovedSchema = 8;

    public const string StatusToggleId = "status.toggle";
    public const string StatusToggleOldDefault = "Shift+Alt+S";
    public const string StatusToggleNewDefault = "Ctrl+Shift+B";

    /// <summary>
    /// Moves <c>status.toggle</c> to the new default when the saved file is older than
    /// <see cref="StatusToggleMovedSchema"/> and the action is bound to exactly the old default (either spelling:
    /// the 5.0.0-and-earlier files wrote "Alt+Shift+S", the registry now writes "Shift+Alt+S"). Not changed:
    /// any other binding (rebound, unassigned, several keys), a file already at the new schema, and a file in which
    /// another action already uses the new key (the old binding stays, so nothing ends up conflicting).
    /// Known limit: a user who deliberately kept or chose exactly Alt+Shift+S before this schema looks the same as
    /// an untouched default and also moves (they can rebind in one step).
    /// </summary>
    /// <returns>True when the map was changed.</returns>
    public static bool MigrateStatusToggle(IDictionary<string, List<string>> hotkeys, int sourceSchema)
    {
        if (sourceSchema >= StatusToggleMovedSchema) return false;
        if (!hotkeys.TryGetValue(StatusToggleId, out var current) || current is null) return false;
        if (current.Count != 1 || !ShortcutText.AreEqual(current[0], StatusToggleOldDefault)) return false;
        foreach (var (id, shortcuts) in hotkeys)
        {
            if (string.Equals(id, StatusToggleId, StringComparison.OrdinalIgnoreCase)) continue;
            if (shortcuts.Any(s => ShortcutText.AreEqual(s, StatusToggleNewDefault))) return false;
        }
        hotkeys[StatusToggleId] = [StatusToggleNewDefault];
        return true;
    }

    /// <summary>Settings schema that gave "Explore/Browse containing folder" the default Ctrl+E.</summary>
    public const int ExploreParentDefaultSchema = 10;

    public const string ExploreParentId = "folder.exploreParent";
    public const string ExploreParentNewDefault = "Ctrl+E";

    /// <summary>
    /// Gives <c>folder.exploreParent</c> its new default Ctrl+E once, for a file older than
    /// <see cref="ExploreParentDefaultSchema"/>: only when the action is unassigned (the old default, indistinguishable
    /// from "the user removed it") and no other action already uses Ctrl+E. A binding the user set is never touched,
    /// and a file already at the new schema is kept (someone who removes Ctrl+E later keeps it removed).
    /// </summary>
    /// <returns>True when the map was changed.</returns>
    public static bool MigrateExploreParent(IDictionary<string, List<string>> hotkeys, int sourceSchema)
    {
        if (sourceSchema >= ExploreParentDefaultSchema) return false;
        if (hotkeys.TryGetValue(ExploreParentId, out var current) && current is not null && current.Count > 0) return false;
        foreach (var (id, shortcuts) in hotkeys)
        {
            if (string.Equals(id, ExploreParentId, StringComparison.OrdinalIgnoreCase)) continue;
            if (shortcuts is not null && shortcuts.Any(s => ShortcutText.AreEqual(s, ExploreParentNewDefault))) return false;
        }
        hotkeys[ExploreParentId] = [ExploreParentNewDefault];
        return true;
    }

    /// <summary>Settings schema that moved the Backspace default from "Previous image" (nav.previousBack) to "Back, or Previous image" (nav.backOrPrevious).</summary>
    public const int BackspaceToBackOrPreviousSchema = 11;

    public const string BackspaceOldOwnerId = "nav.previousBack";
    public const string BackOrPreviousId = "nav.backOrPrevious";
    public const string BackspaceKey = "Backspace";

    /// <summary>
    /// Moves Backspace from "Previous image" (row <c>nav.previousBack</c>) to "Back, or Previous image" (<c>nav.backOrPrevious</c>) once, for a file older
    /// than <see cref="BackspaceToBackOrPreviousSchema"/>. Only when the saved map still shows the old default exactly: the old row is
    /// bound to just Backspace, no other action uses Backspace. A user who rebound or
    /// removed the key on Previous image, or who gave Backspace to another action, is left unchanged (never steal a
    /// binding); a file at schema 11 or later keeps whatever it has (a Backspace removed from the composite stays removed).
    /// Known limit: a user who deliberately kept Backspace on Previous image looks like an untouched default and moves too
    /// (one rebind restores it, and Settings > Hotkeys offers Replace).
    /// </summary>
    /// <returns>True when the map was changed.</returns>
    public static bool MigrateBackspaceToBackOrPrevious(IDictionary<string, List<string>> hotkeys, int sourceSchema)
    {
        if (sourceSchema >= BackspaceToBackOrPreviousSchema) return false;
        if (!hotkeys.TryGetValue(BackspaceOldOwnerId, out var old) || old is null) return false;
        if (old.Count != 1 || !ShortcutText.AreEqual(old[0], BackspaceKey)) return false;
        foreach (var (id, shortcuts) in hotkeys)
        {
            if (string.Equals(id, BackspaceOldOwnerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (shortcuts is not null && shortcuts.Any(s => ShortcutText.AreEqual(s, BackspaceKey))) return false;
        }
        hotkeys[BackOrPreviousId] = [BackspaceKey];
        hotkeys[BackspaceOldOwnerId] = [];
        return true;
    }

    /// <summary>A default key a new hotkey row did not get because a saved binding of another row already uses it.</summary>
    public sealed record SkippedDefault(string RowId, string Shortcut, string OwnerRowId);

    /// <summary>
    /// Adds every hotkey row the saved map does not have yet (a row added in a later version, for example Back /
    /// Forward in 5.0.0) with its default keys, minus any key another row of the saved map already uses: a user who
    /// had bound Alt+Left to something else keeps that binding, and the new action simply starts without that key
    /// (Settings > Hotkeys then offers Replace / Cancel if they assign it). Rows already in the map are never touched.
    /// </summary>
    /// <returns>The default keys that were held back, for diagnostics and tests.</returns>
    public static IReadOnlyList<SkippedDefault> AddMissingRows(IDictionary<string, List<string>> hotkeys)
    {
        var skipped = new List<SkippedDefault>();
        foreach (var row in HotkeyCatalog.All)
        {
            if (hotkeys.ContainsKey(row.Id)) continue;
            // Backspace for "Back, or Previous image" is decided only by MigrateBackspaceToBackOrPrevious (it moves an untouched old
            // default and never gives the key to anyone else): a file that freed Backspace on purpose must not get it back.
            if (row.Id == BackOrPreviousId) { hotkeys[row.Id] = []; continue; }
            var keys = new List<string>();
            foreach (var shortcut in row.DefaultShortcuts)
            {
                var owner = hotkeys.FirstOrDefault(pair => pair.Value is not null && pair.Value.Any(s => ShortcutText.AreEqual(s, shortcut)));
                if (owner.Key is not null) skipped.Add(new SkippedDefault(row.Id, shortcut, owner.Key));
                else keys.Add(shortcut);
            }
            hotkeys[row.Id] = keys;
        }
        return skipped;
    }
}
