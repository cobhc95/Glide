
namespace Glide.Core.Data;

/// <summary>What a data category can be used for. A category shows up on every card whose capability it has.</summary>
[Flags]
public enum GlideDataCapabilities
{
    None = 0,
    /// <summary>Settings &gt; Windows &amp; developer &gt; Reset Glide can wipe it.</summary>
    Reset = 1,
    /// <summary>Settings &gt; Windows &amp; developer &gt; Back up and restore can export it to a .glidebackup file and import it again.</summary>
    Backup = 2,
}

/// <summary>
/// One kind of Glide data (settings, hotkeys, history...). The single source of the tick boxes of both the Reset Glide card
/// and the Back up and restore card: same ids, same order, same groups, same titles and descriptions. The App's
/// DataCategorySelector builds both cards' tick-box lists from <see cref="GlideDataCategoryCatalog.All"/>, filtered by
/// capability, so adding a category here updates both cards.
/// </summary>
/// <param name="Id">Stable id. Historically named <see cref="ResetItemId"/> (it is persisted in the reset helper's arguments).</param>
/// <param name="Group">Group heading the tick box sits under.</param>
/// <param name="Title">Short wording next to the tick box (both cards, the confirmation lists and the import dialog).</param>
/// <param name="Description">Second line under the title, shared by every card unless <paramref name="ResetDescription"/> overrides it.</param>
/// <param name="Capabilities">Which cards offer it.</param>
/// <param name="ResetSettingId">Settings-search id of its Reset Glide tick box (SettingsCatalog entry).</param>
/// <param name="ResetNeedsRestart">See <see cref="ResetItemInfo.NeedsRestart"/>.</param>
/// <param name="BackupSettingId">Settings-search id of its Back up and restore tick box; null when it cannot be backed up.</param>
/// <param name="BackupEntryName">The one entry of a .glidebackup archive that holds it; null when it cannot be backed up.
/// Import only ever opens the entry under this fixed name (never a name taken from the archive).</param>
/// <param name="ResetDescription">Reset-only wording of the second line (only where the shared wording would be wrong for a wipe).</param>
public sealed record GlideDataCategory(
    ResetItemId Id,
    ResetGroup Group,
    string Title,
    string Description,
    GlideDataCapabilities Capabilities,
    string ResetSettingId,
    bool ResetNeedsRestart,
    string? BackupSettingId = null,
    string? BackupEntryName = null,
    string? ResetDescription = null)
{
    public bool CanReset => (Capabilities & GlideDataCapabilities.Reset) != 0;

    /// <summary>Back up and restore exports it.</summary>
    public bool CanExport => (Capabilities & GlideDataCapabilities.Backup) != 0;

    /// <summary>Back up and restore imports it (always the same set as <see cref="CanExport"/>).</summary>
    public bool CanImport => CanExport;

    public bool Has(GlideDataCapabilities capability) => (Capabilities & capability) == capability;

    /// <summary>The second line as the card of <paramref name="capability"/> shows it.</summary>
    public string DescriptionFor(GlideDataCapabilities capability) =>
        capability == GlideDataCapabilities.Reset && ResetDescription is { } reset ? reset : Description;

    /// <summary>The Settings-search id of this category's tick box on the card of <paramref name="capability"/>.</summary>
    public string? SettingIdFor(GlideDataCapabilities capability) => capability switch
    {
        GlideDataCapabilities.Reset => ResetSettingId,
        GlideDataCapabilities.Backup => BackupSettingId,
        _ => null,
    };
}

/// <summary>The shared category list (see <see cref="GlideDataCategory"/>). Order = order on screen and order of execution.</summary>
public static class GlideDataCategoryCatalog
{
    private const GlideDataCapabilities ResetAndBackup = GlideDataCapabilities.Reset | GlideDataCapabilities.Backup;

    public static IReadOnlyList<GlideDataCategory> All { get; } =
    [
        new(ResetItemId.AllSettings, ResetGroup.Settings, "All settings", "Every option in Settings", ResetAndBackup,
            "reset.allSettings", true, "backup.allSettings", "all-settings.json", ResetDescription: "Every option back to its default"),
        new(ResetItemId.HotkeysAndGestures, ResetGroup.Settings, "Hotkeys and mouse gestures", "Including custom presets", ResetAndBackup,
            "reset.hotkeysGestures", true, "backup.hotkeysGestures", "hotkeys-gestures.json"),
        new(ResetItemId.ProfilesAndPresets, ResetGroup.Settings, "Saved profiles and presets", "Named profiles, interaction presets", ResetAndBackup,
            "reset.profiles", true, "backup.profiles", "profiles.json"),
        new(ResetItemId.WindowPlacements, ResetGroup.Settings, "Window sizes and positions", "Main, Settings, Print and dialogs", ResetAndBackup,
            "reset.windowPlacement", true, "backup.windowPlacement", "window-placements.json"),
        new(ResetItemId.RecentHistory, ResetGroup.HistoryAndData, "Recent files and Home list", "Recent images and folders", ResetAndBackup,
            "reset.recentHistory", true, "backup.recentHistory", "recent-history.json"),
        new(ResetItemId.LastSession, ResetGroup.HistoryAndData, "Last session", "Open tabs, Explorer locations", ResetAndBackup,
            "reset.lastSession", true, "backup.lastSession", "last-session.json"),
        // Machine-specific or rebuilt automatically: never in a backup (Back up and restore says so on its card).
        new(ResetItemId.Caches, ResetGroup.HistoryAndData, "Image and thumbnail caches", "Rebuilt automatically", GlideDataCapabilities.Reset,
            "reset.caches", false),
        new(ResetItemId.RegistryEntries, ResetGroup.WindowsIntegration, "Registry entries", "Open With, file types, thumbnails", GlideDataCapabilities.Reset,
            "reset.registry", false),
        new(ResetItemId.StartupEntries, ResetGroup.WindowsIntegration, "Startup and background entries", "Windows startup, Speed Boost task", GlideDataCapabilities.Reset,
            "reset.startup", true),
    ];

    /// <summary>The categories one card offers, in catalogue order.</summary>
    public static IReadOnlyList<GlideDataCategory> With(GlideDataCapabilities capability) => All.Where(x => x.Has(capability)).ToArray();

    public static GlideDataCategory Get(ResetItemId id) => All.First(x => x.Id == id);


    public static string GroupTitle(ResetGroup group) => group switch
    {
        ResetGroup.Settings => "Settings",
        ResetGroup.HistoryAndData => "History and data",
        _ => "Windows integration",
    };

    /// <summary>Settings-search id of the Back up and restore card.</summary>
    public const string BackupCardSettingId = "backup.card";
    public const string BackupExportSettingId = "backup.export";
    public const string BackupImportSettingId = "backup.import";

    // ---- What each backup category carries out of glide.settings.json ----

    /// <summary>
    /// Values of glide.settings.json that "All settings" never exports or imports: everything Reset Glide's "All settings"
    /// keeps (<see cref="GlideSettingsPropertyGroups.AllSettingsKeptProperties"/>: schema version, hotkeys and gestures, the last profile folder,
    /// window geometry, remembered folders, the Windows startup switches, the update checker's memory) EXCEPT the saved
    /// "User custom" performance preset, which is a preset the user saved and travels with the options. The Windows startup
    /// switches are never imported: an import must not add or remove the Windows Run entry.
    /// </summary>
    public static IReadOnlyList<string> AllSettingsBackupExcludedProperties { get; } =
        GlideSettingsPropertyGroups.AllSettingsKeptProperties.Except(GlideSettingsPropertyGroups.SavedPerformancePresetProperties, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The glide.settings.json properties a backup category carries, chosen from <paramref name="allProperties"/> (every
    /// property of the settings object). Empty for categories that are not settings values. The schema version is never part
    /// of a category: each settings entry records it separately so older entries migrate through the normal loader.
    /// </summary>
    public static IReadOnlyList<string> SettingsPropertiesFor(ResetItemId id, IEnumerable<string> allProperties)
    {
        var all = allProperties.ToArray();
        IEnumerable<string> chosen = id switch
        {
            ResetItemId.AllSettings => all.Where(p => !AllSettingsBackupExcludedProperties.Contains(p, StringComparer.OrdinalIgnoreCase)),
            ResetItemId.HotkeysAndGestures => GlideSettingsPropertyGroups.HotkeyGestureProperties,
            ResetItemId.WindowPlacements => GlideSettingsPropertyGroups.WindowPlacementProperties,
            ResetItemId.LastSession => GlideSettingsPropertyGroups.SessionLocationProperties,
            _ => [],
        };
        return chosen.Where(p => !string.Equals(p, GlideSettingsPropertyGroups.SettingsSchemaVersionProperty, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// What Back up and restore never includes, with the reason (shown on the card and in the release notes).
    /// </summary>
    public static IReadOnlyList<(string What, string Why)> NotInBackups => NotInBackupsList;

    private static readonly (string What, string Why)[] NotInBackupsList =
    [
        ("Image and thumbnail caches", "Rebuilt automatically."),
        ("Registry entries and Windows startup entries", "They belong to this PC; an import never changes them."),
        ("External programs (Shift+1 to Shift+3)", "An imported file never decides which programs Glide runs."),
    ];
}
