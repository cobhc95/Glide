using System.Text.Json;
using System.Text.Json.Nodes;
using Glide.Core.Reset;

namespace Glide.App.Settings;

/// <summary>
/// "Every option back to its default" for the Settings window's Restore defaults button. It runs the very same
/// <see cref="AllSettingsReset"/> as Reset Glide's "All settings" item, on the staged settings serialised exactly as
/// glide.settings.json stores them, so the two can never drift: options go back to their defaults; the schema version,
/// hotkeys and mouse gestures, window sizes and positions, remembered folders, the Windows startup switches, the update
/// checker's memory and the saved User custom performance preset are kept (<see cref="ResetCatalog.AllSettingsKeptProperties"/>).
/// </summary>
internal static class SettingsDefaults
{
    /// <summary>Default settings as glide.settings.json stores them, stamped with the current schema (Reset Glide's helper writes these).</summary>
    public static string DefaultSettingsJson() =>
        JsonSerializer.Serialize(new GlideSettingsState { SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema }, AppJsonContext.Default.GlideSettingsState);

    /// <summary>A copy of <paramref name="current"/> with every option at its default and every non-option value kept.</summary>
    public static GlideSettingsState RestoreAllSettings(GlideSettingsState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var source = current.CloneState();
        var root = JsonSerializer.SerializeToNode(source, AppJsonContext.Default.GlideSettingsState) as JsonObject
                   ?? throw new InvalidOperationException("Settings did not serialise to a JSON object.");
        var defaults = JsonSerializer.SerializeToNode(new GlideSettingsState(), AppJsonContext.Default.GlideSettingsState) as JsonObject;
        AllSettingsReset.Apply(root, defaults);
        var restored = root.Deserialize(AppJsonContext.Default.GlideSettingsState) ?? new GlideSettingsState();
        // Never backwards (a lower schema would re-run old one-time migrations on the next start).
        restored.SettingsSchemaVersion = Math.Max(source.SettingsSchemaVersion, restored.SettingsSchemaVersion);
        restored.OverlayWindowPlacements = new Dictionary<string, OverlayWindowPlacementState>(
            restored.OverlayWindowPlacements ?? new(), StringComparer.OrdinalIgnoreCase);
        return restored.CloneState();
    }

    /// <summary>
    /// Values the main window owns at run time and the Settings window never edits: its own normal-window geometry
    /// (<see cref="GlideSettingsState.WindowX"/>...), the per-image overlay placements and the schema version. Settings works
    /// on a snapshot taken when it opened (or on a loaded profile), so when it applies a state these come from
    /// <paramref name="live"/> instead; an "unset" snapshot value (X = int.MinValue, width 0) can then never replace a real
    /// remembered placement, and the schema version can never go backwards.
    /// </summary>
    public static void KeepRuntimeOwnedValues(GlideSettingsState incoming, GlideSettingsState live)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(live);
        incoming.WindowX = live.WindowX;
        incoming.WindowY = live.WindowY;
        incoming.WindowWidth = live.WindowWidth;
        incoming.WindowHeight = live.WindowHeight;
        incoming.WindowWasMaximized = live.WindowWasMaximized;
        incoming.OverlayWindowPlacements = (live.OverlayWindowPlacements ?? new())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { }, StringComparer.OrdinalIgnoreCase);
        incoming.SettingsSchemaVersion = Math.Max(incoming.SettingsSchemaVersion, live.SettingsSchemaVersion);
    }
}
