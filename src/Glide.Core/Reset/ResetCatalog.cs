using Glide.Core.Data;

namespace Glide.Core.Reset;


/// <summary>
/// One tick box of the Reset card.
/// </summary>
/// <param name="SettingId">Settings-search id (SettingsCatalog entry, also the card's editor map key).</param>
/// <param name="Title">Short wording shown next to the tick box and in the confirmation list.</param>
/// <param name="Description">Second line under the title (mockup wording).</param>
/// <param name="NeedsRestart">
/// True when Glide holds the data in memory or has the files open, so the wipe must run after Glide has closed
/// (a helper process waits for Glide to exit, wipes, then starts Glide again). Caches and registry entries are
/// removed in place.
/// </param>
public sealed record ResetItemInfo(
    ResetItemId Id,
    string SettingId,
    ResetGroup Group,
    string Title,
    string Description,
    bool NeedsRestart);

/// <summary>Fixed wording, ids and name lists of the Reset feature (the planner turns them into concrete targets).</summary>
public static class ResetCatalog
{
    public const string CardSettingId = "reset.card";

    /// <summary>
    /// The Reset Glide tick boxes: every category of the shared <see cref="GlideDataCategoryCatalog"/> that can
    /// be reset, in its order and with its wording (the Back up and restore card reads the same list).
    /// </summary>
    public static IReadOnlyList<ResetItemInfo> Items { get; } =
        GlideDataCategoryCatalog.With(GlideDataCapabilities.Reset)
            .Select(x => new ResetItemInfo(x.Id, x.ResetSettingId, x.Group, x.Title,
                x.DescriptionFor(GlideDataCapabilities.Reset), x.ResetNeedsRestart))
            .ToArray();

    public static string GroupTitle(ResetGroup group) => GlideDataCategoryCatalog.GroupTitle(group);

    public static ResetItemInfo Info(ResetItemId id) => Items.First(x => x.Id == id);


    // ---- File and property names (kept next to the planner so a test can check them against the stores) ----

    public const string SettingsFileName = "glide.settings.json";
    public const string LaunchPolicyFileName = "glide.launch.policy.json";
    public const string ProfilesFileName = "glide.profiles.json";
    public const string HistoryFileName = "glide.history.json";
    public const string SessionFileName = "glide.workspace-session.json";
    public const string OverlayLayoutFileName = "overlay-layout.json";
    public const string PrintSettingsFileName = "print.settings.json";
    public const string ThumbnailSettingsFileName = "thumbnail.settings.json";
    public const string StartupDiagnosticsFolderName = "Startup Diagnostics";
    /// <summary>Folder of the Explorer thumbnail disk cache (must equal Glide.Imaging.ThumbnailDiskCache.FolderName; a test checks).</summary>
    public const string ThumbnailCacheFolderName = "thumbnails";

    // ---- glide.settings.json property groups: defined in Glide.Core.Data.GlideSettingsPropertyGroups (Stage B: one-way
    //      Reset -> Data dependency, no type-initialiser cycle). Kept here as forwarding members for existing callers. ----

    public const string SettingsSchemaVersionProperty = GlideSettingsPropertyGroups.SettingsSchemaVersionProperty;
    public static IReadOnlyList<string> HotkeyGestureProperties => GlideSettingsPropertyGroups.HotkeyGestureProperties;
    public static IReadOnlyList<string> ProfileProperties => GlideSettingsPropertyGroups.ProfileProperties;
    public static IReadOnlyList<string> WindowPlacementProperties => GlideSettingsPropertyGroups.WindowPlacementProperties;
    public static IReadOnlyList<string> SessionLocationProperties => GlideSettingsPropertyGroups.SessionLocationProperties;
    public static IReadOnlyList<string> StartupSwitchProperties => GlideSettingsPropertyGroups.StartupSwitchProperties;
    public static IReadOnlyList<string> UpdateCheckerMemoryProperties => GlideSettingsPropertyGroups.UpdateCheckerMemoryProperties;
    public static IReadOnlyList<string> SavedPerformancePresetProperties => GlideSettingsPropertyGroups.SavedPerformancePresetProperties;
    public static IReadOnlyList<string> AllSettingsKeptProperties => GlideSettingsPropertyGroups.AllSettingsKeptProperties;

    // ---- HKCU names (relative to HKEY_CURRENT_USER) ----

    public const string ApplicationExe = "Glide.exe";
    public const string ProgId = "Glide.Image";
    public const string ApplicationKey = @"Software\Classes\Applications\Glide.exe";
    public const string ProgIdKey = @"Software\Classes\Glide.Image";
    public const string CapabilitiesKey = @"Software\Glide\Capabilities";
    public const string StateKey = @"Software\Glide";
    public const string ThumbnailConfigKey = @"Software\Glide\ExplorerThumbnails";
    public const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    public const string RegisteredApplicationName = "Glide";
    public const string OpenWithOptOutValue = "OpenWithOptOut";
    public const string OpenWithVersionValue = "OpenWithRegistrationVersion";
    public const string ThumbnailHandlerGuid = "{E357FCCD-A995-4576-B01F-234630154E96}";
    public const string ThumbnailProviderClsid = "{6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "Glide";

    /// <summary>What the planner will NOT remove, with the reason (shown in the result and documented in the release notes).</summary>
    public static IReadOnlyList<(string What, string Why)> NotRemoved { get; } =
    [
        ("HKEY_LOCAL_MACHINE registrations (installer Open With, machine-wide thumbnail provider)", "They belong to every user of the PC and need administrator rights; the installer and uninstaller own them."),
        ("Windows default-app choices (UserChoice)", "Windows owns them and Glide never writes them."),
        ("Windows' own thumbnail database (thumbcache_*.db)", "It is shared by every app; use Explorer thumbnails > Refresh thumbnails to rebuild it."),
        ("The Glide program folder, the installer and any other app's data", "Reset only touches Glide's settings folder, %LOCALAPPDATA%\\Glide, Glide's temp files and HKCU\\Software\\Glide."),
        ("crash.log and startup-crash.log", "They are evidence for bug reports, not caches."),
        ("Backups in Documents\\Glide backups, exported settings and profile files", "Files you saved yourself are never deleted."),
    ];
}
