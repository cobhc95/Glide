namespace Glide.Core.Reset;

/// <summary>The kinds of Glide data (Reset Glide and Back up and restore, see Glide.Core.Data.GlideDataCategoryCatalog). Order = order on screen and order of execution.</summary>
public enum ResetItemId
{
    AllSettings,
    HotkeysAndGestures,
    ProfilesAndPresets,
    WindowPlacements,
    RecentHistory,
    LastSession,
    Caches,
    RegistryEntries,
    StartupEntries,
}

public enum ResetGroup { Settings, HistoryAndData, WindowsIntegration }

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
    /// The Reset Glide tick boxes: every category of the shared <see cref="Glide.Core.Data.GlideDataCategoryCatalog"/> that can
    /// be reset, in its order and with its wording (the Back up and restore card reads the same list).
    /// </summary>
    public static IReadOnlyList<ResetItemInfo> Items { get; } =
        Glide.Core.Data.GlideDataCategoryCatalog.With(Glide.Core.Data.GlideDataCapabilities.Reset)
            .Select(x => new ResetItemInfo(x.Id, x.ResetSettingId, x.Group, x.Title,
                x.DescriptionFor(Glide.Core.Data.GlideDataCapabilities.Reset), x.ResetNeedsRestart))
            .ToArray();

    public static string GroupTitle(ResetGroup group) => Glide.Core.Data.GlideDataCategoryCatalog.GroupTitle(group);

    public static ResetItemInfo Info(ResetItemId id) => Items.First(x => x.Id == id);

    public static ResetItemInfo? FromSettingId(string settingId) =>
        Items.FirstOrDefault(x => string.Equals(x.SettingId, settingId, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Properties of glide.settings.json removed by "Hotkeys and mouse gestures" (they fall back to the built-in maps).</summary>
    public static IReadOnlyList<string> HotkeyGestureProperties { get; } =
        ["Hotkeys", "CustomHotkeyPresets", "Gestures", "CustomGestures", "GestureRecentActions"];

    /// <summary>Properties removed by "Saved profiles and presets" besides glide.profiles.json.</summary>
    public static IReadOnlyList<string> ProfileProperties { get; } = ["LastProfileDirectory"];

    /// <summary>
    /// Properties removed by "Window sizes and positions": the main window, the Settings window (older keys), the shared
    /// per-window store of every other window (Print, dialogs, Settings) and per-image overlay placements.
    /// </summary>
    public static IReadOnlyList<string> WindowPlacementProperties { get; } =
    [
        "WindowX", "WindowY", "WindowWidth", "WindowHeight", "WindowWasMaximized",
        "SettingsWindowX", "SettingsWindowY", "SettingsWindowWidth", "SettingsWindowHeight", "SettingsWindowWasMaximized",
        "WindowPlacements",
        "OverlayWindowPlacements",
    ];

    /// <summary>Remembered locations removed by "Last session" besides the session file and the overlay layout.</summary>
    public static IReadOnlyList<string> SessionLocationProperties { get; } =
        ["LastNewExplorerTabDirectory", "LastOpenDirectory", "LastOpenFileDirectory", "LastOverlayDirectory"];

    /// <summary>Settings switched off (not deleted: their default is on) by "Startup and background entries".</summary>
    public static IReadOnlyList<string> StartupSwitchProperties { get; } = ["StartWithWindowsInBackground", "SpeedBoostEnabled"];

    /// <summary>Format version of glide.settings.json. Never reset and never lowered (a lower value would re-run old migrations).</summary>
    public const string SettingsSchemaVersionProperty = "SettingsSchemaVersion";

    /// <summary>The update checker's memory (last check, latest known version, last prompt): bookkeeping, not settings.</summary>
    public static IReadOnlyList<string> UpdateCheckerMemoryProperties { get; } =
    [
        "UpdateLastCheckedUtc", "UpdateLastAttemptUtc", "UpdateLastAttemptFailed", "UpdateLatestKnownVersion",
        "UpdateLatestKnownUrl", "UpdateLatestKnownPublishedUtc", "UpdateLastPromptedVersion", "UpdateLastPromptedUtc",
    ];

    /// <summary>The saved "User custom" performance preset (written by Save as User custom): a saved preset, not an option.</summary>
    public static IReadOnlyList<string> SavedPerformancePresetProperties { get; } =
    [
        "PerformanceUserCustomSaved", "UserCustomPanQuality", "UserCustomAdaptivePreview", "UserCustomRefinementDelayMs",
        "UserCustomSequentialReads", "UserCustomPrefetch", "UserCustomPrefetchDepth", "UserCustomBackgroundRefinement",
        "UserCustomCacheItems", "UserCustomCompressedCacheMb", "UserCustomDecodedCacheMb", "UserCustomPreviewLongestSide",
        "UserCustomRapidBrowsePreviewSize", "UserCustomProgressiveColor",
    ];

    /// <summary>
    /// Values of glide.settings.json that "All settings" (and the Settings window's Restore defaults button, which uses the
    /// same <see cref="AllSettingsReset"/>) leave exactly as they are: they are not options, or another reset item owns them.
    /// The schema version; hotkeys and mouse gestures ("Hotkeys and mouse gestures"); the last profile folder ("Saved
    /// profiles and presets"); every window size and position ("Window sizes and positions"); remembered folders ("Last
    /// session"); the Windows startup switches ("Startup and background entries": resetting them would add or remove the
    /// Windows Run entry); the update checker's memory and the saved User custom performance preset.
    /// </summary>
    public static IReadOnlyList<string> AllSettingsKeptProperties { get; } =
        new[] { SettingsSchemaVersionProperty }
            .Concat(HotkeyGestureProperties)
            .Concat(ProfileProperties)
            .Concat(WindowPlacementProperties)
            .Concat(SessionLocationProperties)
            .Concat(StartupSwitchProperties)
            .Concat(UpdateCheckerMemoryProperties)
            .Concat(SavedPerformancePresetProperties)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
