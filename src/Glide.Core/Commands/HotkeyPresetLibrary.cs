namespace Glide.Core.Commands;

/// <summary>
/// A keyboard mapping the user saved under a name. Plain mutable class so the settings JSON source
/// generator (no reflection) can persist it as part of the settings document and profile exports.
/// </summary>
public sealed class CustomHotkeyPreset
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, List<string>> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CustomHotkeyPreset Clone() => new()
    {
        Name = Name,
        Hotkeys = (Hotkeys ?? new()).ToDictionary(kv => kv.Key, kv => (kv.Value ?? new List<string>()).ToList(), StringComparer.OrdinalIgnoreCase)
    };
}

public enum HotkeyPresetResult
{
    Ok,
    /// <summary>A custom preset with that name already existed and was overwritten.</summary>
    Overwritten,
    InvalidName,
    /// <summary>The name belongs to a built-in preset; built-ins can be neither replaced nor deleted.</summary>
    BuiltInName,
    NotFound,
    /// <summary>Renaming to the name of a different custom preset.</summary>
    NameInUse,
    /// <summary>The list already holds <see cref="HotkeyPresetLibrary.MaxCustomPresets"/> custom presets.</summary>
    LimitReached
}

/// <summary>
/// Save / apply / rename / delete for user-defined hotkey presets, next to the stock presets in
/// <see cref="HotkeyCatalog.PresetNames"/>. Pure list manipulation (the caller owns persistence).
/// </summary>
public static class HotkeyPresetLibrary
{
    public const int MaxNameLength = 40;
    public const int MaxCustomPresets = 50;

    public static bool IsBuiltIn(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        HotkeyCatalog.PresetNames.Any(p => string.Equals(p, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeName(string? name)
    {
        var clean = string.Join(" ", (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }

    public static CustomHotkeyPreset? Find(IReadOnlyList<CustomHotkeyPreset> presets, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
            : presets.FirstOrDefault(p => string.Equals(p.Name, NormalizeName(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>All preset names for a picker: the built-ins first, then the custom ones.</summary>
    public static IReadOnlyList<string> AllNames(IReadOnlyList<CustomHotkeyPreset> presets) =>
        HotkeyCatalog.PresetNames.Concat(presets.Select(p => p.Name)).ToArray();

    /// <summary>Saves the mapping as a custom preset (overwriting a custom preset of the same name).</summary>
    public static HotkeyPresetResult Save(List<CustomHotkeyPreset> presets, string? name, IReadOnlyDictionary<string, List<string>> map, out string savedName)
    {
        savedName = NormalizeName(name);
        if (savedName.Length == 0) return HotkeyPresetResult.InvalidName;
        if (IsBuiltIn(savedName)) return HotkeyPresetResult.BuiltInName;
        var snapshot = new CustomHotkeyPreset
        {
            Name = savedName,
            Hotkeys = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase)
        };
        var existing = Find(presets, savedName);
        if (existing is not null)
        {
            savedName = existing.Name; // keep the original casing the user already sees
            existing.Hotkeys = snapshot.Hotkeys;
            return HotkeyPresetResult.Overwritten;
        }
        if (presets.Count >= MaxCustomPresets) return HotkeyPresetResult.LimitReached;
        presets.Add(snapshot);
        return HotkeyPresetResult.Ok;
    }

    public static HotkeyPresetResult Rename(List<CustomHotkeyPreset> presets, string oldName, string? newName, out string finalName)
    {
        finalName = NormalizeName(newName);
        if (IsBuiltIn(oldName)) return HotkeyPresetResult.BuiltInName;
        var preset = Find(presets, oldName);
        if (preset is null) return HotkeyPresetResult.NotFound;
        if (finalName.Length == 0) return HotkeyPresetResult.InvalidName;
        if (IsBuiltIn(finalName)) return HotkeyPresetResult.BuiltInName;
        var clash = Find(presets, finalName);
        if (clash is not null && !ReferenceEquals(clash, preset)) return HotkeyPresetResult.NameInUse;
        preset.Name = finalName;
        return HotkeyPresetResult.Ok;
    }

    /// <summary>Deletes a custom preset. Built-in presets are never deletable.</summary>
    public static HotkeyPresetResult Delete(List<CustomHotkeyPreset> presets, string? name)
    {
        if (IsBuiltIn(name)) return HotkeyPresetResult.BuiltInName;
        var preset = Find(presets, name);
        if (preset is null) return HotkeyPresetResult.NotFound;
        presets.Remove(preset);
        return HotkeyPresetResult.Ok;
    }

    /// <summary>
    /// The complete editable map for a preset name: a built-in (via <see cref="HotkeyCatalog.CreatePresetMap"/>)
    /// or a saved custom one. Actions added to Glide after the preset was saved keep their default shortcuts
    /// unless that key is already used by the preset, so applying an old preset never creates a duplicate.
    /// </summary>
    public static Dictionary<string, List<string>> Resolve(IReadOnlyList<CustomHotkeyPreset> presets, string? name)
    {
        if (IsBuiltIn(name) || Find(presets, name) is null) return HotkeyCatalog.CreatePresetMap(name);
        var preset = Find(presets, name)!;
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in HotkeyCatalog.All)
        {
            if (preset.Hotkeys is not null && preset.Hotkeys.TryGetValue(action.Id, out var saved))
            {
                var clean = saved.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                map[action.Id] = clean;
                foreach (var shortcut in clean) used.Add(shortcut);
            }
        }
        foreach (var action in HotkeyCatalog.All)
            if (!map.ContainsKey(action.Id))
                map[action.Id] = action.DefaultShortcuts.Where(x => used.Add(x)).ToList();
        return map;
    }

    /// <summary>Drops unusable entries from a loaded list (blank/duplicate/built-in names, null maps).</summary>
    public static List<CustomHotkeyPreset> Normalize(IEnumerable<CustomHotkeyPreset>? loaded)
    {
        var result = new List<CustomHotkeyPreset>();
        foreach (var preset in loaded ?? Array.Empty<CustomHotkeyPreset>())
        {
            if (preset is null) continue;
            var name = NormalizeName(preset.Name);
            if (name.Length == 0 || IsBuiltIn(name) || result.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var copy = preset.Clone();
            copy.Name = name;
            var known = HotkeyCatalog.All.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in copy.Hotkeys.Keys.ToList())
            {
                if (!known.Contains(key)) { copy.Hotkeys.Remove(key); continue; }
                copy.Hotkeys[key] = copy.Hotkeys[key].Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            result.Add(copy);
            if (result.Count >= MaxCustomPresets) break;
        }
        return result;
    }
}
