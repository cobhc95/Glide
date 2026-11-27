namespace Glide.App.Settings;

/// <summary>
/// One human-readable net change between the committed and the staged settings. <see cref="Name"/> is the stable,
/// property-derived name; <see cref="Label"/> is what the unsaved-changes dialog shows (the SettingsCatalog label in
/// sentence case where the setting is catalogued).
/// </summary>
internal sealed record SettingChange(string Name, string Before, string After)
{
    public string Label { get; init; } = Name;
}

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
            // Compound values stored as one string / list (offsets "Custom top tabs@Top right=500,0", the status bar's group ids) are described the
            // way their Settings controls show them, never as the stored text (5.1.7 desktop retest, F).
            if (CompoundChanges(property.Name, committed, current) is { } compound) { changes.AddRange(compound); continue; }
            // Round 4 D4: a Choice setting shows its own option labels ("foreground → background"), even when it is
            // stored as a bool or number, never "Off → On".
            var (beforeText, afterText) = ChoiceTextAccessor(property.Name) is { } choiceText
                ? (choiceText(committed), choiceText(current))
                : (FormatSettingValue(before), FormatSettingValue(after));
            changes.Add(new SettingChange(HumanizeSettingName(property.Name), beforeText, afterText) { Label = DisplayLabel(property.Name, current) });
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

    /// <summary>The Settings label of the text overlay offset boxes (catalogue "overlay.pictureOffsetX/Y" without the axis).</summary>
    internal const string TextOverlayOffsetLabel = "Text overlay offset (px)";

    /// <summary>
    /// The readable changes of a setting that stores several values in one string or list, or null when <paramref name="propertyName"/>
    /// is not one of them. Text overlay offsets: one row per tab layout that changed ("Tabs along the top: X 500, Y 0"); status bar
    /// groups and title-bar buttons: the labels the customisers show instead of their ids.
    /// </summary>
    internal static List<SettingChange>? CompoundChanges(string propertyName, GlideSettingsState committed, GlideSettingsState current)
    {
        switch (propertyName)
        {
            case nameof(GlideSettingsState.PictureCounterOffsets):
                var rows = new List<SettingChange>();
                foreach (var (side, layout) in new[] { (false, "Tabs along the top"), (true, "Tabs at the side") })
                {
                    var was = Glide.Core.Settings.TextOverlayPlacements.Offset(committed, side);
                    var now = Glide.Core.Settings.TextOverlayPlacements.Offset(current, side);
                    if (was == now) continue;
                    rows.Add(new SettingChange(TextOverlayOffsetLabel, $"{layout}: X {was.X}, Y {was.Y}", $"{layout}: X {now.X}, Y {now.Y}") { Label = TextOverlayOffsetLabel });
                }
                // A change that only moved the anchor (or a retired entry) leaves the visible offsets equal: nothing readable to show.
                return rows;
            case nameof(GlideSettingsState.StatusBarGroups):
                return [new SettingChange("Status bar buttons", StatusBarGroupsText(committed.StatusBarGroups), StatusBarGroupsText(current.StatusBarGroups)) { Label = "Status bar buttons" }];
            case nameof(GlideSettingsState.TitleBarButtons):
                return [new SettingChange("Title bar buttons", TitleBarButtonsText(committed.TitleBarButtons), TitleBarButtonsText(current.TitleBarButtons)) { Label = "Title bar buttons" }];
            default:
                return null;
        }
    }

    private static string StatusBarGroupsText(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "Default layout";
        if (string.Equals(stored.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return "None";
        return string.Join(", ", Glide.Core.StatusBarGroupCatalog.Parse(stored).Select(Glide.Core.StatusBarGroupCatalog.LabelOf));
    }

    private static string TitleBarButtonsText(IEnumerable<string>? ids)
    {
        var labels = (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => Glide.Core.Settings.TitleBarButtonCatalog.Find(id)?.Label ?? id).ToArray();
        return labels.Length == 0 ? "None" : string.Join(", ", labels);
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

    private static readonly Lazy<Dictionary<string, string>> CatalogIdsByProperty = new(BuildCatalogIds);
    private static readonly Lazy<Dictionary<string, string>> CatalogLabelsByProperty = new(() =>
    {
        var labels = Glide.Core.Settings.SettingsCatalog.All.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.OrdinalIgnoreCase);
        return CatalogIdsByProperty.Value.ToDictionary(p => p.Key, p => labels[p.Value], StringComparer.Ordinal);
    });

    /// <summary>The accessor that reads <paramref name="propertyName"/> as its Choice option text, when it backs a catalogued Choice.</summary>
    internal static Func<GlideSettingsState, string>? ChoiceTextAccessor(string propertyName)
    {
        if (!CatalogIdsByProperty.Value.TryGetValue(propertyName, out var id)) return null;
        var entry = Glide.Core.Settings.SettingsCatalog.All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Kind != Glide.Core.Settings.SettingKind.Choice) return null;
        return SettingAccessors.All.TryGetValue(id, out var accessor) ? accessor.GetText : null;
    }

    /// <summary>
    /// The label the unsaved-changes dialog shows for <paramref name="propertyName"/>: the SettingsCatalog label when the
    /// property belongs to a catalogued setting (found through its <see cref="SettingAccessors"/> entry, or a catalogue label
    /// that reads the same as the property name), else the property name split into words, in sentence case either way.
    /// </summary>
    internal static string DisplayLabel(string propertyName) => DisplayLabel(propertyName, null);

    /// <summary>
    /// As <see cref="DisplayLabel(string)"/>, but the two "Key to open ... instead" settings name the other position / side
    /// from the staged state, exactly as the Settings page labels them (<see cref="Glide.Core.Workspace.NewTabPolicy"/>);
    /// the static catalogue label cannot know which one applies.
    /// </summary>
    internal static string DisplayLabel(string propertyName, GlideSettingsState? staged)
    {
        if (staged is not null)
        {
            if (propertyName == nameof(GlideSettingsState.NewTabPositionKey))
                return SentenceCase(Glide.Core.Workspace.NewTabPolicy.PositionKeyLabel(staged.NewTabPosition));
            if (propertyName == nameof(GlideSettingsState.NewTabForegroundKey))
                return SentenceCase(Glide.Core.Workspace.NewTabPolicy.ForegroundKeyLabel(staged.SwitchToNewTabs));
        }
        if (CatalogLabelsByProperty.Value.TryGetValue(propertyName, out var label)) return SentenceCase(label);
        var humanized = HumanizeSettingName(propertyName);
        var match = Glide.Core.Settings.SettingsCatalog.All.FirstOrDefault(s => string.Equals(s.Label, humanized, StringComparison.OrdinalIgnoreCase));
        return SentenceCase(match?.Label ?? humanized);
    }

    /// <summary>
    /// "Status Bar Size" → "Status bar size". The first word keeps its capital; later words are lowercased unless they
    /// are acronyms or names with more than one capital ("HDR", "WebP", "GIF"), which stay as written.
    /// </summary>
    internal static string SentenceCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length == 0) continue;
            if (i == 0) { words[i] = char.ToUpperInvariant(word[0]) + word[1..]; continue; }
            if (word.Count(char.IsUpper) > 1 || word.Any(char.IsDigit) || word == "Windows" || word == "Explorer" || word == "Glide") continue;
            words[i] = word.ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Maps state property names to catalogue labels by probing each accessor once: flip its value on a fresh state and
    /// see which property changed. No hand-kept table to drift.
    /// </summary>
    private static Dictionary<string, string> BuildCatalogIds()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = Glide.Core.Settings.SettingsCatalog.All.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.OrdinalIgnoreCase);
        var properties = typeof(GlideSettingsState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToArray();
        foreach (var (id, accessor) in SettingAccessors.All)
        {
            if (!labels.ContainsKey(id)) continue;
            try
            {
                var baseline = new GlideSettingsState();
                var probe = baseline.CloneState();
                if (accessor.GetBool is { } getBool && accessor.SetBool is { } setBool) setBool(probe, !getBool(probe));
                else if (accessor.GetText is { } getText && accessor.SetText is { } setText)
                {
                    // A choice stored in another form (Open new tabs in the [foreground / background] is a bool) ignores a
                    // made-up value, so fall back to another of its listed options.
                    var current = getText(probe);
                    setText(probe, current + "\u0001probe");
                    if (getText(probe) == current && Glide.Core.Settings.SettingActionCatalog.Choices.TryGetValue(id, out var options))
                        setText(probe, options.FirstOrDefault(o => !string.Equals(o, current, StringComparison.OrdinalIgnoreCase)) ?? current);
                }
                else continue;
                foreach (var property in properties)
                {
                    if (map.ContainsKey(property.Name)) continue;
                    if (!SettingValuesEqual(property.GetValue(baseline), property.GetValue(probe))) { map[property.Name] = id; break; }
                }
            }
            catch
            {
                // An accessor that rejects the probe value just keeps the property-name label.
            }
        }
        return map;
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
