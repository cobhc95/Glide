namespace Glide.Core.Data;

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
/// Named groups of glide.settings.json properties shared by Reset Glide and Back up and restore. They live in Core.Data so
/// the dependency is one-way (Reset reads Data; Data never reads Reset) and no type initialiser waits on another.
/// </summary>
public static class GlideSettingsPropertyGroups
{
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
    /// same ResetCatalog.AllSettingsReset) leave exactly as they are: they are not options, or another reset item owns them.
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
}
