namespace Glide.App.Settings;

/// <summary>One human-readable net change between the committed and the staged settings.</summary>
internal sealed record SettingChange(string Name, string Before, string After);

/// <summary>
/// Computes the net, human-readable differences between two <see cref="GlideSettingsState"/> snapshots
/// for the Settings window's dirty indicator and its confirm dialogs. UI-free so it can be reasoned about
/// (and tested) without a window; the Settings window owns only when it is asked.
/// </summary>
internal static class SettingsChangeSet
{
    public static List<SettingChange> Compute(GlideSettingsState committed, GlideSettingsState current)
    {
        // Canonical semantic guard: opening Settings, cloning staged state, or changing a value and
        // changing it back must never make the window dirty. ContentEquals compares the mutable
        // settings collections by value, not by List/Dictionary object identity.
        if (committed.ContentEquals(current)) return new List<SettingChange>();

        var changes = new List<SettingChange>();
        foreach (var property in typeof(GlideSettingsState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.Name is nameof(GlideSettingsState.Hotkeys) or nameof(GlideSettingsState.Gestures) or nameof(GlideSettingsState.CustomHotkeyPresets)
                or nameof(GlideSettingsState.CustomGestures) or nameof(GlideSettingsState.WindowPlacements) or nameof(GlideSettingsState.GestureRecentActions)) continue;
            // The update checker's memory (last check time...) is bookkeeping, never a pending Settings change.
            if (Array.IndexOf(GlideSettingsState.UpdateStateProperties, property.Name) >= 0) continue;
            if (!IsListedSetting(property.Name)) continue;
            var before = property.GetValue(committed);
            var after = property.GetValue(current);
            if (SettingValuesEqual(before, after)) continue;
            changes.Add(new SettingChange(HumanizeSettingName(property.Name), FormatSettingValue(before), FormatSettingValue(after)));
        }

        if (!DictionaryOfListsEqual(committed.Hotkeys, current.Hotkeys))
            changes.Add(new SettingChange("Keyboard shortcuts", "Previous assignments", "Modified assignments"));
        if (!GlideSettingsState.CustomHotkeyPresetsEqual(committed.CustomHotkeyPresets ?? new(), current.CustomHotkeyPresets ?? new()))
            changes.Add(new SettingChange("Custom keyboard shortcut presets", "Previous presets", "Modified presets"));
        if (!DictionaryEqual(committed.Gestures, current.Gestures))
            changes.Add(new SettingChange("Mouse gesture matrix", "Previous assignments", "Modified assignments"));
        // collections are compared by value and described in words, never as a .NET type name.
        var beforeCustom = committed.CustomGestures ?? new();
        var afterCustom = current.CustomGestures ?? new();
        if (!GlideSettingsState.CustomGesturesEqual(beforeCustom, afterCustom))
            changes.Add(new SettingChange("Custom gestures", CountText(beforeCustom.Count, "custom gesture"), CountText(afterCustom.Count, "custom gesture")));
        // Window placements are remembered geometry, not settings: never a pending change (see IsListedSetting).
        return changes;
    }

    /// <summary>
    /// False for values that are not options and never belong in the pending-change list: the schema version, window sizes
    /// and positions, remembered folders, the saved User custom performance preset (Glide's bookkeeping; the same list Reset
    /// Glide's "All settings" and Restore defaults keep, <see cref="Glide.Core.Reset.ResetCatalog.AllSettingsKeptProperties"/>).
    /// The Windows startup switches are kept by that reset but are real options with controls, so they are listed; hotkeys
    /// and gestures are described separately above.
    /// </summary>
    internal static bool IsListedSetting(string propertyName) =>
        !Glide.Core.Reset.AllSettingsReset.IsKept(propertyName) ||
        Glide.Core.Reset.ResetCatalog.StartupSwitchProperties.Contains(propertyName, StringComparer.OrdinalIgnoreCase);

    internal static string CountText(int count, string noun) => count switch
    {
        0 => "none",
        1 => "1 " + noun,
        _ => $"{count} {noun}s"
    };

    private static bool DictionaryOfListsEqual(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var pair in a)
            if (!b.TryGetValue(pair.Key, out var values) || !pair.Value.SequenceEqual(values, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool DictionaryEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var pair in a)
            if (!b.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string HumanizeSettingName(string name)
    {
        var chars = new List<char>(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) chars.Add(' ');
            chars.Add(name[i]);
        }
        return new string(chars.ToArray()).Replace("Ms", "ms", StringComparison.Ordinal);
    }

    private static bool SettingValuesEqual(object? before, object? after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before is null || after is null) return false;

        // SettingsState intentionally contains mutable collection properties (currently TitleBarButtons).
        // CloneState creates a new List instance, so object.Equals would report a false change every time
        // Settings opens even when the ordered contents are identical. Compare string sequences by value.
        if (before is IEnumerable<string> beforeStrings && after is IEnumerable<string> afterStrings)
            return beforeStrings.SequenceEqual(afterStrings, StringComparer.OrdinalIgnoreCase);

        return Equals(before, after);
    }

    private static string FormatSettingValue(object? value) => value switch
    {
        null => "(none)",
        bool flag => flag ? "On" : "Off",
        string text when string.IsNullOrWhiteSpace(text) => "(empty)",
        IEnumerable<string> values => FormatStringSequence(values),
        System.Collections.ICollection collection => CountText(collection.Count, "item"),
        _ => value.ToString() ?? "(none)"
    };

    private static string FormatStringSequence(IEnumerable<string> values)
    {
        var materialized = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return materialized.Length == 0 ? "(none)" : string.Join(", ", materialized);
    }
}
