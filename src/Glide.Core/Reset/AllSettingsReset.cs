using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glide.Core.Reset;

/// <summary>
/// The one definition of "every option back to its default". Reset Glide's "All settings" item (on glide.settings.json, in
/// the helper process) and the Settings window's Restore defaults button (on the staged settings, serialised to the same JSON)
/// both run <see cref="Apply"/>, so the two can never reset different things.
/// Every property in <see cref="ResetCatalog.AllSettingsKeptProperties"/> is left exactly as it is; every other property
/// gets its default value.
/// </summary>
public static class AllSettingsReset
{
    private static readonly HashSet<string> Kept = new(ResetCatalog.AllSettingsKeptProperties, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for values "All settings" / Restore defaults never touch (not options, or owned by another reset item).</summary>
    public static bool IsKept(string propertyName) => Kept.Contains(propertyName);

    /// <summary>
    /// Resets <paramref name="root"/> in place and returns whether anything changed.
    /// <paramref name="defaults"/> is a default settings object serialised the same way (the app supplies it). Options are
    /// written with their default value rather than removed, so the loader's "key missing = older file" migrations never
    /// mistake a reset file for an old one. Properties the defaults do not know (stale keys of older builds) are removed.
    /// Without defaults (null) every option is removed instead. The schema version is never lowered: kept as it is, or
    /// taken from the defaults only when the file has none.
    /// </summary>
    public static bool Apply(JsonObject root, JsonObject? defaults)
    {
        ArgumentNullException.ThrowIfNull(root);
        var changed = false;
        foreach (var name in root.Select(x => x.Key).ToArray())
        {
            if (IsKept(name)) continue;
            if (defaults is not null && Contains(defaults, name)) continue;
            root.Remove(name);
            changed = true;
        }
        if (defaults is null) return changed;

        foreach (var (name, value) in defaults)
        {
            if (IsKept(name)) continue;
            var existingName = FindName(root, name);
            if (existingName is not null && JsonNode.DeepEquals(root[existingName], value)) continue;
            if (existingName is not null && existingName != name) root.Remove(existingName);
            root[name] = value?.DeepClone();
            changed = true;
        }

        if (FindName(root, ResetCatalog.SettingsSchemaVersionProperty) is null &&
            FindName(defaults, ResetCatalog.SettingsSchemaVersionProperty) is { } schemaName && defaults[schemaName] is { } schema)
        {
            root[ResetCatalog.SettingsSchemaVersionProperty] = schema.DeepClone();
            changed = true;
        }
        return changed;
    }

    /// <summary>Same as <see cref="Apply(JsonObject, JsonObject?)"/> with the defaults given as JSON text (null or blank = none).</summary>
    public static bool Apply(JsonObject root, string? defaultsJson) =>
        Apply(root, string.IsNullOrWhiteSpace(defaultsJson) ? null : JsonNode.Parse(defaultsJson) as JsonObject
                    ?? throw new JsonException("the default settings are not a JSON object"));

    private static bool Contains(JsonObject obj, string name) => FindName(obj, name) is not null;

    // System.Text.Json reads Glide's settings case-insensitively, so a differently cased key is the same value.
    private static string? FindName(JsonObject obj, string name)
    {
        if (obj.ContainsKey(name)) return name;
        foreach (var pair in obj)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        return null;
    }
}
