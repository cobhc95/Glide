using System.Text.Json;
using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.App.Settings;

/// <summary>
/// Minimal durable settings store. Portable builds keep an inspectable JSON file beside Glide.exe;
/// installed builds (identified by installed.flag) use %LOCALAPPDATA%\Glide. Corrupt files fail safe.
/// </summary>
public static class SettingsStore
{
    internal static readonly AsyncLocal<string?> SettingsDirectoryOverrideForTests = new();
    internal static Action? AfterFirstFramePolicyBytesReadForTests;
    private const string FileName = "glide.settings.json";
    private const string LaunchPolicyFileName = "glide.launch.policy.json";

    /// <summary>
    /// Tiny cold-launch policy read used before Avalonia starts. It intentionally avoids constructing
    /// the full settings graph (hotkeys/gestures/title-bar collections plus migration work). The
    /// complete settings load runs in parallel with Avalonia startup and is adopted non-blockingly.
    /// </summary>
    private const int LaunchPolicySchema = 4;
    // 9 (5.0.0 Back / Forward): the one-time title-bar default migration below is gated on it.
    internal const int CurrentSettingsSchema = 25;
    /// <summary>
    /// Settings schema (round 4 D1) that retired the 5.1.0 "Middle-click opens in a new tab in the" / "Middle-click new tab
    /// position" settings (Explorer middle-click now follows the general new-tab settings and keys) and added "Duplicated
    /// tabs open [position] in the [side]". Older files: the legacy MiddleClickNewTab* keys are dropped (not read; they are
    /// not written again) and the duplicate settings start at their defaults (next to the current tab, foreground), the
    /// placement Duplicate tab already had before.
    /// </summary>
    internal const int DuplicateTabSettingsSchema = 24;
    /// <summary>
    /// Settings schema (5.1.4) that added the "Custom" text overlay position: per-preset offsets are retired; a card whose
    /// preset had an offset becomes Custom at the same spot (TextOverlayPlacements.MigratePerPresetOffsets).
    /// </summary>
    internal const int TextOverlayCustomPositionSchema = 23;
    /// <summary>
    /// Settings schema (5.1.3) that split "Text overlay position" and "Text overlay alignment" by tab layout (tabs along the
    /// top / at the side). Schema 21 (one position with an alignment per area) and older files (a picture position plus
    /// "Text overlay with vertical tabs") are mapped by TextOverlayPlacements.MigrateLegacy from the retired JSON names below.
    /// </summary>
    internal const int TextOverlayTabLayoutSchema = 22;
    internal const string LegacyTextOverlaySideTabsPlacementProperty = "PictureCounterSideTabsPlacement";
    internal const string LegacyTextOverlayPositionProperty = "PictureCounterPosition";
    internal const string LegacyTextOverlayAlignmentProperty = "PictureCounterAlignment";
    internal const string LegacyTextOverlayTitleBarAlignmentProperty = "PictureCounterTitleBarAlignment";
    internal const string LegacyTextOverlayTabBarAlignmentProperty = "PictureCounterTabBarAlignment";
    /// <summary>
    /// Settings schema (5.1.2) that made "Hide cursor in fullscreen" default to off. The feature never worked before, so a
    /// stored "on" from an older schema is the untouched old default and moves to off once.
    /// </summary>
    internal const int HideCursorOffSchema = 20;
    /// <summary>
    /// Settings schema that made "Previously used tab" the default of "After closing a tab, activate" (5.0.6). Files
    /// from an older schema that store the old default "Match Ctrl+Tab order" move to the new default once.
    /// </summary>
    internal const int PreviouslyUsedTabCloseSchema = 12;
    /// <summary>
    /// Settings schema that made "Open new tabs in the foreground" default to on (5.0.6). Files from an older schema that
    /// store the old default (off) move to the new default once.
    /// </summary>
    internal const int ForegroundNewTabsSchema = 13;
    /// <summary>
    /// Settings schema that raised the "Navigation minimum interval" default from 80 to 120 ms (5.0.6). Files from an older
    /// schema that store exactly the old default (80) move to 120 once.
    /// </summary>
    internal const int NavigationInterval120Schema = 14;
    /// <summary>
    /// Settings schema that replaced the "Automatically continue into sibling folders" checkbox (ContinueSiblingFolders)
    /// with "When the last image is reached" (EndOfFolderAction, 5.0.7). Older files: on = next folder, off = do nothing.
    /// </summary>
    internal const int EndOfFolderActionSchema = 15;
    /// <summary>
    /// Settings schema that shows the "Loop this folder (this tab)" status-bar button by default (5.0.7). A customised
    /// status-bar list from an older schema gets it inserted once (after "nav"); users can remove it again.
    /// </summary>
    internal const int LoopFolderStatusButtonSchema = 16;
    /// <summary>
    /// Settings schema (5.0.8) that made "Ask me each time" the default of "When the last image is reached" (was "Move to
    /// the next folder") and made the mouse side buttons pass-through by default. Older files on the old end-of-folder
    /// default move to Ask once; side-button rows stored as "default" become pass-through by themselves.
    /// </summary>
    internal const int AskEndOfFolderSchema = 17;
    /// <summary>
    /// Settings schema (round 3 #18) that replaced the "Shift and Alt change how a new tab opens" checkbox (NewTabModifiers)
    /// with a chosen key per new-tab option (NewTabPositionKey / NewTabForegroundKey). Older files: on = Shift (position) / Alt (foreground), off = None.
    /// </summary>
    internal const int NewTabKeysSchema = 18;
    /// <summary>The JSON name of the retired checkbox, read only by the schema 18 migration.</summary>
    internal const string LegacyNewTabModifiersProperty = "NewTabModifiers";
    /// <summary>The JSON name of the retired checkbox, read only by the schema 15 migration.</summary>
    internal const string LegacyContinueSiblingFoldersProperty = "ContinueSiblingFolders";
    /// <summary>The navigation minimum interval default before 5.0.6.</summary>
    internal const int PreviousNavigationRateLimitDefaultMs = 80;
    /// <summary>Settings schema that added Back / Forward to the default title-bar buttons.</summary>
    internal const int TitleBarNavigationSchema = 9;

    public readonly record struct LaunchPolicy(bool ReuseSingleInstance, string ExternalOpenBehavior);

    public readonly record struct FirstFramePolicy(
        bool ReuseSingleInstance,
        string ExternalOpenBehavior,
        string DefaultViewMode,
        string InitialImageQuality,
        bool AdaptiveFastPreview,
        int RapidPreviewLongestSide,
        bool BackgroundRefinement,
        int AdaptivePreviewDelayMs,
        bool SequentialForegroundReads,
        bool PredictivePrefetch,
        int PrefetchDepth,
        int CacheItems,
        int CompressedCacheMegabytes,
        int DecodedCacheMegabytes,
        bool ProgressiveColorFirstPreview,
        bool AlwaysStartWholeAppOverlayMode,
        bool RememberWindowPlacement,
        int WindowX, int WindowY, double WindowWidth, double WindowHeight, bool WindowWasMaximized,
        string ThemeChoice);

    private readonly record struct SettingsFileIdentity(long Length, long LastWriteUtcTicks)
    {
        public static SettingsFileIdentity Capture(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();
            return new SettingsFileIdentity(info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }

    internal sealed record LaunchPolicyCache(
        int Schema,
        long SettingsLength,
        long SettingsLastWriteUtcTicks,
        FirstFramePolicy Policy);

    public static LaunchPolicy LoadLaunchPolicy()
    {
        var policy = LoadFirstFramePolicy();
        return new LaunchPolicy(policy.ReuseSingleInstance, policy.ExternalOpenBehavior);
    }

    private static readonly FirstFramePolicy DefaultFirstFramePolicy = new(
        ReuseSingleInstance: true,
        ExternalOpenBehavior: "Open in new tab",
        DefaultViewMode: "Fit image",
        InitialImageQuality: "Maximum quality",
        AdaptiveFastPreview: true,
        RapidPreviewLongestSide: 2400,
        BackgroundRefinement: true,
        AdaptivePreviewDelayMs: 5,
        SequentialForegroundReads: true,
        PredictivePrefetch: true,
        PrefetchDepth: 15,
        CacheItems: 20,
        CompressedCacheMegabytes: 999,
        DecodedCacheMegabytes: 999,
        ProgressiveColorFirstPreview: true,
        AlwaysStartWholeAppOverlayMode: false,
        RememberWindowPlacement: true,
        WindowX: int.MinValue, WindowY: int.MinValue, WindowWidth: 0, WindowHeight: 0, WindowWasMaximized: false,
        ThemeChoice: "Dark");

    public static FirstFramePolicy LoadFirstFramePolicy()
    {
        var defaults = DefaultFirstFramePolicy;
        var settingsPath = GetSettingsPath();
        if (!File.Exists(settingsPath)) return defaults;

        try
        {
            var cachePath = GetLaunchPolicyPath();
            if (File.Exists(cachePath))
            {
                var identity = SettingsFileIdentity.Capture(settingsPath);
                var cached = JsonSerializer.Deserialize(File.ReadAllText(cachePath), AppJsonContext.Default.LaunchPolicyCache);
                if (cached is not null && cached.Schema == LaunchPolicySchema &&
                    cached.SettingsLength == identity.Length &&
                    cached.SettingsLastWriteUtcTicks == identity.LastWriteUtcTicks &&
                    !string.IsNullOrWhiteSpace(cached.Policy.ExternalOpenBehavior))
                    return cached.Policy;
            }
        }
        catch { /* unreadable or stale launch-policy cache: fall through and recompute from the settings file */ }

        // Cache misses are deliberately scalar-only and side-effect free. In particular, this path
        // never constructs GlideSettingsState and never writes the sidecar before Avalonia starts.
        // The background full load/save path refreshes the cache after startup.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var before = SettingsFileIdentity.Capture(settingsPath);
                var bytes = File.ReadAllBytes(settingsPath);
                AfterFirstFramePolicyBytesReadForTests?.Invoke();
                using var document = JsonDocument.Parse(bytes);
                var projected = ProjectFirstFramePolicy(document.RootElement, defaults);
                var after = SettingsFileIdentity.Capture(settingsPath);
                if (before == after) return projected;
            }
            catch
            {
                return defaults;
            }
        }
        return defaults;
    }

    private static FirstFramePolicy ProjectFirstFramePolicy(JsonElement root, FirstFramePolicy d)
    {
        static bool B(JsonElement r, string n, bool d) => r.TryGetProperty(n, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : d;
        static int I(JsonElement r, string n, int d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : d;
        static double D(JsonElement r, string n, double d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : d;
        static string S(JsonElement r, string n, string d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()! : d;

        var theme = S(root, nameof(GlideSettingsState.ThemeChoice), string.Empty);
        if (string.IsNullOrWhiteSpace(theme))
            theme = B(root, nameof(GlideSettingsState.LightTheme), false) ? SettingChoiceNames.Light : d.ThemeChoice;
        theme = theme is SettingChoiceNames.Dark or SettingChoiceNames.Neutral or SettingChoiceNames.Light ? theme : d.ThemeChoice;

        var quality = S(root, nameof(GlideSettingsState.InitialImageQuality), d.InitialImageQuality);
        if (quality is not (SettingChoiceNames.MaximumSpeed or SettingChoiceNames.Balanced or SettingChoiceNames.MaximumQuality)) quality = d.InitialImageQuality;
        var viewMode = S(root, nameof(GlideSettingsState.DefaultViewMode), d.DefaultViewMode);
        if (viewMode is not ("Fit image" or "Fit width" or "Fit height" or "Fit to window" or "100%")) viewMode = d.DefaultViewMode;
        var externalOpen = S(root, nameof(GlideSettingsState.ExternalOpenBehavior), d.ExternalOpenBehavior);
        if (externalOpen is not ("Open in new tab" or "Open new window" or "Overwrite existing tab")) externalOpen = d.ExternalOpenBehavior;

        return new FirstFramePolicy(
            B(root, nameof(GlideSettingsState.ReuseSingleInstance), d.ReuseSingleInstance),
            externalOpen,
            viewMode,
            quality,
            B(root, nameof(GlideSettingsState.AdaptiveFastPreview), d.AdaptiveFastPreview),
            Math.Clamp(I(root, nameof(GlideSettingsState.RapidPreviewLongestSide), d.RapidPreviewLongestSide), 480, 4096),
            B(root, nameof(GlideSettingsState.BackgroundRefinement), d.BackgroundRefinement),
            Math.Clamp(I(root, nameof(GlideSettingsState.AdaptivePreviewDelayMs), d.AdaptivePreviewDelayMs), 5, 500),
            B(root, nameof(GlideSettingsState.SequentialForegroundReads), d.SequentialForegroundReads),
            B(root, nameof(GlideSettingsState.PredictivePrefetch), d.PredictivePrefetch),
            Math.Clamp(I(root, nameof(GlideSettingsState.PrefetchDepth), d.PrefetchDepth), 1, 30),
            Math.Clamp(I(root, nameof(GlideSettingsState.CacheItems), d.CacheItems), 2, 64),
            Math.Clamp(I(root, nameof(GlideSettingsState.CompressedCacheMegabytes), d.CompressedCacheMegabytes), 32, 2048),
            Math.Clamp(I(root, nameof(GlideSettingsState.DecodedCacheMegabytes), d.DecodedCacheMegabytes), 32, 2048),
            B(root, nameof(GlideSettingsState.ProgressiveColorFirstPreview), d.ProgressiveColorFirstPreview),
            B(root, nameof(GlideSettingsState.AlwaysStartWholeAppOverlayMode), d.AlwaysStartWholeAppOverlayMode),
            B(root, nameof(GlideSettingsState.RememberWindowPlacement), d.RememberWindowPlacement),
            I(root, nameof(GlideSettingsState.WindowX), d.WindowX),
            I(root, nameof(GlideSettingsState.WindowY), d.WindowY),
            D(root, nameof(GlideSettingsState.WindowWidth), d.WindowWidth),
            D(root, nameof(GlideSettingsState.WindowHeight), d.WindowHeight),
            B(root, nameof(GlideSettingsState.WindowWasMaximized), d.WindowWasMaximized),
            theme);
    }

    public static GlideSettingsState ApplyFirstFramePolicy(GlideSettingsState state, FirstFramePolicy policy)
    {
        state.ReuseSingleInstance = policy.ReuseSingleInstance;
        state.ExternalOpenBehavior = policy.ExternalOpenBehavior;
        state.DefaultViewMode = policy.DefaultViewMode;
        state.InitialImageQuality = policy.InitialImageQuality;
        state.AdaptiveFastPreview = policy.AdaptiveFastPreview;
        state.RapidPreviewLongestSide = policy.RapidPreviewLongestSide;
        state.BackgroundRefinement = policy.BackgroundRefinement;
        state.AdaptivePreviewDelayMs = policy.AdaptivePreviewDelayMs;
        state.SequentialForegroundReads = policy.SequentialForegroundReads;
        state.PredictivePrefetch = policy.PredictivePrefetch;
        state.PrefetchDepth = policy.PrefetchDepth;
        state.CacheItems = policy.CacheItems;
        state.CompressedCacheMegabytes = policy.CompressedCacheMegabytes;
        state.DecodedCacheMegabytes = policy.DecodedCacheMegabytes;
        state.ProgressiveColorFirstPreview = policy.ProgressiveColorFirstPreview;
        state.AlwaysStartWholeAppOverlayMode = policy.AlwaysStartWholeAppOverlayMode;
        state.RememberWindowPlacement = policy.RememberWindowPlacement;
        state.WindowX = policy.WindowX; state.WindowY = policy.WindowY;
        state.WindowWidth = policy.WindowWidth; state.WindowHeight = policy.WindowHeight;
        state.WindowWasMaximized = policy.WindowWasMaximized;
        state.ThemeChoice = policy.ThemeChoice;
        state.LightTheme = string.Equals(policy.ThemeChoice, "Light", StringComparison.OrdinalIgnoreCase);
        return state;
    }

    private static string GetLaunchPolicyPath() => Path.Combine(GetSettingsDirectory(), LaunchPolicyFileName);

    private static void AtomicWriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup; the write outcome is already decided */ }
        }
    }

    private static void SaveLaunchPolicyCache(SettingsFileIdentity settingsFile, FirstFramePolicy policy)
    {
        try
        {
            var cache = new LaunchPolicyCache(LaunchPolicySchema, settingsFile.Length, settingsFile.LastWriteUtcTicks, policy);
            var path = GetLaunchPolicyPath();
            var json = JsonSerializer.Serialize(cache, AppJsonContext.Default.LaunchPolicyCache);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal)) return;
            AtomicWriteAllText(path, json);
        }
        catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("settings", "launch_policy_cache_write_failed", loggedEx); }
    }

    private static void SaveLaunchPolicyCache()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            if (!File.Exists(settingsPath)) return;
            // Generate from the exact persisted file rather than caller memory: concurrent saves
            // cannot tag an older in-memory policy with newer file metadata.
            var before = SettingsFileIdentity.Capture(settingsPath);
            using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            var policy = ProjectFirstFramePolicy(document.RootElement, DefaultFirstFramePolicy);
            var after = SettingsFileIdentity.Capture(settingsPath);
            if (before != after) return;
            SaveLaunchPolicyCache(after, policy);
        }
        catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("settings", "launch_policy_cache_refresh_failed", loggedEx); }
    }

    public static string GetSettingsPath()
    {
        if (!string.IsNullOrWhiteSpace(SettingsDirectoryOverrideForTests.Value))
        {
            var dir = Path.GetFullPath(SettingsDirectoryOverrideForTests.Value!);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }
        var externalOverride = Environment.GetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(externalOverride))
        {
            var dir = Path.GetFullPath(externalOverride);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }
        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "installed.flag")))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "Glide");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }
        return Path.Combine(baseDir, FileName);
    }

    public static string GetSettingsDirectory() => Path.GetDirectoryName(GetSettingsPath())!;

    /// <summary>
    /// Folder of the Explorer thumbnail disk cache: the settings folder's "thumbnails" when the settings folder is redirected
    /// (an isolated profile or a test), otherwise %LOCALAPPDATA%\Glide\thumbnails (never next to a portable copy's exe).
    /// </summary>
    public static string GetThumbnailCacheDirectory()
    {
        var redirected = !string.IsNullOrWhiteSpace(SettingsDirectoryOverrideForTests.Value) ||
                         !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY"));
        var root = redirected
            ? GetSettingsDirectory()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glide");
        return Path.Combine(root, Glide.Imaging.ThumbnailDiskCache.FolderName);
    }

    internal static bool MigrateWheelDirection(int sourceSchema, bool invert) => sourceSchema >= 6 && invert;

    /// <summary>Schema at which the 4.7 mouse toggles were folded into gesture bindings.</summary>
    /// <summary>Round 4 D1: files older than <see cref="DuplicateTabSettingsSchema"/> get the duplicate-tab defaults; newer ones are normalised.</summary>
    internal static (string Position, string Side) MigrateDuplicateTabSettings(int sourceSchema, string? position, string? side) =>
        sourceSchema < DuplicateTabSettingsSchema
            ? (Glide.Core.Workspace.DuplicateTabPositions.Default, Glide.Core.Workspace.DuplicateTabSides.Default)
            : (Glide.Core.Workspace.DuplicateTabPositions.Normalize(position), Glide.Core.Workspace.DuplicateTabSides.Normalize(side));

    internal const int GestureFoldSchema = 7;

    /// <summary>
    /// Runs the one-time 4.8.0 mouse-toggle fold for a state loaded from an older schema (settings
    /// file, imported profile or settings transfer). Newer states are left untouched.
    /// </summary>
    public static void MigrateLegacyMouseToggles(GlideSettingsState state, int sourceSchema)
    {
        if (sourceSchema >= GestureFoldSchema) return;
        state.SlideshowLeftClickAction = Glide.Core.Settings.SlideshowLeftClickActions.Normalize(state.SlideshowLeftClickAction);
        state.FoldLegacyMouseToggles();
    }

    /// <summary>
    /// Schema 8 (5.0.0 audit items): (1) "Viewport zoom step" now works and defaults to 15% (the zoom keys' historical
    /// hard-coded step). The setting never had an effect before, so a stored 10 (the old default) is an untouched
    /// default and moves to 15; any other value is the user's choice and is kept. (2) "Show / hide status bar" moves from
    /// Alt+Shift+S to Ctrl+Shift+B when the file shows exactly the old default (see HotkeyDefaultMigrations).
    /// Newer states are left untouched.
    /// </summary>
    public static void MigrateDefaultsSchema8(GlideSettingsState state, int sourceSchema)
    {
        state.Hotkeys ??= HotkeyCatalog.CreateDefaultMap();
        // Schema 10: "Explore/Browse containing folder" gains Ctrl+E (only where it is unassigned and the key is free).
        HotkeyDefaultMigrations.MigrateExploreParent(state.Hotkeys, sourceSchema);
        // Schema 11: Backspace moves from "Previous image" to "Back, or Previous image" (only an untouched old default; nobody's binding is taken).
        HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(state.Hotkeys, sourceSchema);
        // Schema 19: "Enter / exit Overlay mode" gains Alt+O (only where unassigned and the key is free).
        HotkeyDefaultMigrations.MigrateOverlayToggle(state.Hotkeys, sourceSchema);
        // Schema 25: "Show / hide the tag sidebar" gains Ctrl+Shift+G (only where unassigned and the key is free).
        HotkeyDefaultMigrations.MigrateTagSidebar(state.Hotkeys, sourceSchema);
        if (sourceSchema >= HotkeyDefaultMigrations.StatusToggleMovedSchema) return;
        if (state.ViewportZoomStepPercent == 10) state.ViewportZoomStepPercent = Glide.Core.ZoomStepPolicy.DefaultStepPercent;
        HotkeyDefaultMigrations.MigrateStatusToggle(state.Hotkeys, sourceSchema);
    }

    /// <summary>
    /// Schema 12: "After closing a tab, activate" defaults to Previously used tab. A file from an older schema that stores
    /// the old default "Match Ctrl+Tab order" (saved explicitly with every settings write) moves to the new default once;
    /// any other value, and any value saved from schema 12 on, is the user's choice and is kept.
    /// </summary>
    internal static string MigrateAfterTabCloseActivate(int sourceSchema, string value) =>
        sourceSchema < PreviouslyUsedTabCloseSchema &&
        string.Equals(value, Glide.Core.Workspace.TabCloseActivation.MatchCycleOrder, StringComparison.OrdinalIgnoreCase)
            ? Glide.Core.Workspace.TabCloseActivation.PreviouslyUsed
            : value;

    /// <summary>
    /// Schema 13: "Open new tabs in the foreground" defaults to on. A file from an older schema that stores the old default
    /// (off, saved explicitly with every settings write) moves to on once; a value saved from schema 13 on is the user's
    /// choice and is kept. Background opening stays available through the setting and through Shift when "Shift and Alt
    /// change how a new tab opens" is on.
    /// </summary>
    internal static bool MigrateSwitchToNewTabs(int sourceSchema, bool value) =>
        sourceSchema < ForegroundNewTabsSchema || value;

    /// <summary>
    /// Schema 18: the "Shift and Alt change how a new tab opens" checkbox became two "Key to open … instead" choices. A file
    /// from an older schema keeps what the checkbox did: on = Shift for the other position and Alt for the other of
    /// foreground / background (the user-approved round 3 mapping), off or missing = None for both. From schema 18 the stored keys are kept.
    /// </summary>
    internal static (string PositionKey, string ForegroundKey) MigrateNewTabKeys(int sourceSchema, bool? legacyModifiersOn,
        string? positionKey, string? foregroundKey) =>
        sourceSchema < NewTabKeysSchema
            ? Glide.Core.Workspace.NewTabPolicy.MigrateLegacyModifiers(legacyModifiersOn == true)
            : (Glide.Core.Workspace.NewTabKeys.Normalize(positionKey), Glide.Core.Workspace.NewTabKeys.Normalize(foregroundKey));

    /// <summary>
    /// Schema 14: "Navigation minimum interval" defaults to 120 ms (laptop keyboards often bounce, so one press skipped
    /// several pictures at 80 ms). A file from an older schema that stores exactly the old default 80 (saved explicitly with
    /// every settings write) moves to 120 once; any other value, and any value saved from schema 14 on, is kept.
    /// </summary>
    internal static int MigrateNavigationRateLimit(int sourceSchema, int value) =>
        sourceSchema < NavigationInterval120Schema && value == PreviousNavigationRateLimitDefaultMs
            ? new GlideSettingsState().NavigationRateLimitMs
            : value;

    /// <summary>
    /// 5.0.7: "When the last image is reached". A file older than schema 15 has no EndOfFolderAction; its old checkbox
    /// decides (missing = its default, on = next folder). From schema 15 on the stored choice is kept (normalised).
    /// </summary>
    internal static string MigrateEndOfFolderAction(int sourceSchema, bool? legacyContinueSiblingFolders, string? stored)
    {
        var value = sourceSchema < EndOfFolderActionSchema
            ? Glide.Core.Navigation.EndOfFolderActions.FromLegacyContinueSiblingFolders(legacyContinueSiblingFolders ?? true)
            : Glide.Core.Navigation.EndOfFolderActions.Normalize(stored);
        // Schema 17: the old default "Move to the next folder" becomes the new default "Ask me each time" once.
        return sourceSchema < AskEndOfFolderSchema && value == Glide.Core.Navigation.EndOfFolderActions.NextFolder
            ? Glide.Core.Navigation.EndOfFolderActions.Ask
            : value;
    }

    /// <summary>
    /// Schema 16: the Loop this folder button joins the default status bar. An empty list (the default layout) picks it up
    /// by itself; "none" (everything deliberately hidden) is kept; any other list from an older schema gets it once.
    /// </summary>
    internal static string MigrateStatusBarGroups(int sourceSchema, string? stored)
    {
        if (sourceSchema >= LoopFolderStatusButtonSchema || string.IsNullOrWhiteSpace(stored)) return stored ?? string.Empty;
        var shown = Glide.Core.StatusBarGroupCatalog.Parse(stored);
        if (shown.Count == 0) return stored;
        return Glide.Core.StatusBarGroupCatalog.Format(Glide.Core.StatusBarGroupCatalog.InsertLoopFolderButton(shown));
    }

    internal static List<string> NormalizeRecentGestureActions(IEnumerable<string>? recent) =>
        (recent ?? [])
            .Select(GestureCatalog.NormalizeAction)
            .Where(id => id is not GestureCatalog.Default and not GestureCatalog.Nothing)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

    /// <summary>Default keys of newly added hotkey rows that the last <see cref="Load"/> held back because a saved binding
    /// of another action already uses them (diagnostics and tests).</summary>
    internal static IReadOnlyList<HotkeyDefaultMigrations.SkippedDefault> LastSkippedHotkeyDefaults { get; private set; } = [];

    public static GlideSettingsState Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return new GlideSettingsState();
            var loaded = MigrateFromJson(File.ReadAllText(path), isSettingsFile: true);
            SaveLaunchPolicyCache();
            return loaded;
        }
        catch
        {
            return new GlideSettingsState();
        }
    }

    /// <summary>
    /// The settings loader proper: parses glide.settings.json text and runs every migration for the schema it records, then
    /// stamps <see cref="CurrentSettingsSchema"/>. <see cref="Load"/> uses it for the settings file; a Glide backup import
    /// runs its settings entries through it too, so an older backup migrates exactly like an older settings file.
    /// <paramref name="isSettingsFile"/> false skips the process-wide side effect of loading the real file (the diagnostics'
    /// skipped-defaults list). Throws on text that is not a settings object.
    /// </summary>
    internal static GlideSettingsState MigrateFromJson(string json, bool isSettingsFile)
    {
        {
            // Parse the settings payload once. Glide 2.1 deserialised the string and then parsed the
            // same JSON again for migration probes; that duplicated cold-launch JSON work.
            using var document = JsonDocument.Parse(json);
            var sourceSchema = 0;
            if (document.RootElement.TryGetProperty(nameof(GlideSettingsState.SettingsSchemaVersion), out var schemaNode) &&
                schemaNode.ValueKind == JsonValueKind.Number && schemaNode.TryGetInt32(out var persistedSchema))
                sourceSchema = persistedSchema;
            var loaded = document.RootElement.Deserialize(AppJsonContext.Default.GlideSettingsState) ?? new GlideSettingsState();
            // Before schema 6 the wheel mapping was backwards, so "reverse" on meant the natural direction.
            // Clearing it keeps that direction now that the mapping is fixed.
            loaded.InvertWheelDirection = MigrateWheelDirection(sourceSchema, loaded.InvertWheelDirection);
            // ThemeChoice supersedes the historical LightTheme Boolean. When loading an older file
            // that only recorded LightTheme=true, preserve that choice. New Neutral/Dark/Light files
            // keep ThemeChoice authoritative while LightTheme remains a compatibility mirror.
            if (!document.RootElement.TryGetProperty(nameof(GlideSettingsState.ThemeChoice), out _))
                loaded.ThemeChoice = loaded.LightTheme ? "Light" : "Dark";

            // A prior release expanded the status-size ladder. Preserve the *visual* size of an existing
            // pre-2.2 setting while adopting the new labels: old Small -> Very small, old Medium
            // -> Small, old Large -> Medium. New installs use Medium as the new default.
            if (!document.RootElement.TryGetProperty(nameof(GlideSettingsState.StatusBarGlobalScalePercent), out _))
            {
                loaded.StatusBarSize = loaded.StatusBarSize switch
                {
                    "Small" => "Very small",
                    "Medium" => "Small",
                    "Large" => "Medium",
                    _ => loaded.StatusBarSize
                };
            }
            loaded.ThemeChoice = loaded.ThemeChoice is "Dark" or "Neutral" or "Light" ? loaded.ThemeChoice : "Dark";
            loaded.ExplorerThemeChoice = loaded.ExplorerThemeChoice is "Follow Glide theme" or "Dark" or "Neutral" or "Light" ? loaded.ExplorerThemeChoice : "Follow Glide theme";
            loaded.LightTheme = string.Equals(loaded.ThemeChoice, "Light", StringComparison.OrdinalIgnoreCase);
            loaded.ExplorerOpenImagesWith = string.Equals(loaded.ExplorerOpenImagesWith, LegacyMouseToggles.ExplorerNewTab, StringComparison.OrdinalIgnoreCase)
                ? LegacyMouseToggles.ExplorerNewTab : LegacyMouseToggles.ExplorerCurrentTab;
            loaded.TabPreviewSizePercent = Glide.Core.Workspace.TabPreviewSize.Clamp(loaded.TabPreviewSizePercent);
            loaded.ExplorerNavPaneWidth = Glide.App.Explorer.ExplorerLayout.ClampNavPaneWidth(loaded.ExplorerNavPaneWidth);
            loaded.ExplorerThumbnailCacheMaxMb = Glide.App.Services.ThumbnailCacheSettings.ClampMegabytes(loaded.ExplorerThumbnailCacheMaxMb);
        loaded.TabPreviewResizeModifier = Glide.Core.Workspace.TabPreviewSize.NormalizeModifier(loaded.TabPreviewResizeModifier);
        loaded.TabToolTipTextSize = loaded.TabToolTipTextSize is "Small" or "Large" ? loaded.TabToolTipTextSize : "Medium";
            loaded.NewTabPosition = Glide.Core.Workspace.NewTabPositions.Normalize(loaded.NewTabPosition);
            loaded.AfterTabCloseCustomTarget = Glide.Core.Workspace.TabCloseCustomTargets.Normalize(loaded.AfterTabCloseCustomTarget);
            foreach (var area in Glide.Core.Settings.TooltipAreas.All)
                Glide.Core.Settings.TooltipAreas.SetDelayMs(loaded, area.Id, Glide.Core.Settings.TooltipAreas.DelayMs(loaded, area.Id));
            (loaded.DuplicateTabPosition, loaded.DuplicateTabSide) =
                MigrateDuplicateTabSettings(sourceSchema, loaded.DuplicateTabPosition, loaded.DuplicateTabSide);
            loaded.AfterTabCloseActivate = MigrateAfterTabCloseActivate(sourceSchema,
                Glide.Core.Workspace.TabCloseActivation.Normalize(loaded.AfterTabCloseActivate));
            loaded.SwitchToNewTabs = MigrateSwitchToNewTabs(sourceSchema, loaded.SwitchToNewTabs);
            if (sourceSchema < HideCursorOffSchema) loaded.HideCursorFullscreen = false;
            loaded.FullscreenHideCursorDelaySeconds = Glide.Core.FullscreenCursorAutoHide.ClampDelaySeconds(loaded.FullscreenHideCursorDelaySeconds);
            loaded.HideCursorMode = loaded.HideCursorMode == "Always while over the picture" ? loaded.HideCursorMode : "After a delay";
            bool? legacyNewTabModifiers =
                document.RootElement.TryGetProperty(LegacyNewTabModifiersProperty, out var legacyModifiers) &&
                legacyModifiers.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? legacyModifiers.GetBoolean() : null;
            (loaded.NewTabPositionKey, loaded.NewTabForegroundKey) =
                MigrateNewTabKeys(sourceSchema, legacyNewTabModifiers, loaded.NewTabPositionKey, loaded.NewTabForegroundKey);
            bool? legacyContinueSiblingFolders =
                document.RootElement.TryGetProperty(LegacyContinueSiblingFoldersProperty, out var legacyContinue) &&
                legacyContinue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? legacyContinue.GetBoolean() : null;
            loaded.EndOfFolderAction = MigrateEndOfFolderAction(sourceSchema, legacyContinueSiblingFolders, loaded.EndOfFolderAction);
            loaded.StatusBarGroups = MigrateStatusBarGroups(sourceSchema, loaded.StatusBarGroups);
            if (sourceSchema < TextOverlayTabLayoutSchema)
            {
                string? Legacy(string name) =>
                    document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                Glide.Core.Settings.TextOverlayPlacements.MigrateLegacy(loaded, sourceSchema,
                    Legacy(LegacyTextOverlayPositionProperty), Legacy(LegacyTextOverlayAlignmentProperty),
                    Legacy(LegacyTextOverlaySideTabsPlacementProperty), Legacy(LegacyTextOverlayTitleBarAlignmentProperty),
                    Legacy(LegacyTextOverlayTabBarAlignmentProperty));
            }
            loaded.PictureCounterPositionTopTabs = Glide.Core.Settings.TextOverlayPlacements.NormalizeTopTabs(loaded.PictureCounterPositionTopTabs);
            loaded.PictureCounterPositionSideTabs = Glide.Core.Settings.TextOverlayPlacements.Normalize(loaded.PictureCounterPositionSideTabs);
            loaded.PictureCounterAlignmentTopTabs = Glide.Core.Settings.TextOverlayPlacements.NormalizeAlignment(loaded.PictureCounterAlignmentTopTabs);
            loaded.PictureCounterAlignmentSideTabs = Glide.Core.Settings.TextOverlayPlacements.NormalizeAlignment(loaded.PictureCounterAlignmentSideTabs);
            // Round 4 B5/B6: unknown choice values fall back to their defaults.
            loaded.TextOverlayTitleBarColor = Glide.Core.Settings.TextOverlayStyle.NormalizeChoice(loaded.TextOverlayTitleBarColor, Glide.Core.Settings.TextOverlayStyle.ColorChoices);
            loaded.TextOverlayTabBarColor = Glide.Core.Settings.TextOverlayStyle.NormalizeChoice(loaded.TextOverlayTabBarColor, Glide.Core.Settings.TextOverlayStyle.ColorChoices);
            loaded.TextOverlayTitleBarBorder = Glide.Core.Settings.TextOverlayStyle.NormalizeChoice(loaded.TextOverlayTitleBarBorder, Glide.Core.Settings.TextOverlayStyle.BorderChoices);
            loaded.TextOverlayTabBarBorder = Glide.Core.Settings.TextOverlayStyle.NormalizeChoice(loaded.TextOverlayTabBarBorder, Glide.Core.Settings.TextOverlayStyle.BorderChoices);
            loaded.TextOverlaySeparator = Glide.Core.Settings.TextOverlayStyle.NormalizeChoice(loaded.TextOverlaySeparator, Glide.Core.Status.TextTemplate.Separators);
            loaded.TextOverlaySeparatorColor = Glide.Core.Settings.TextOverlayStyle.NormalizeChoice(loaded.TextOverlaySeparatorColor, Glide.Core.Settings.TextOverlayStyle.SeparatorColorChoices);
            if (sourceSchema < TextOverlayCustomPositionSchema)
                Glide.Core.Settings.TextOverlayPlacements.MigratePerPresetOffsets(loaded);
            // 4.8.0: the title-bar Show in Explorer button opens Glide's own Explorer tab by default. Files saved
            // before the setting existed omit it (so the new default applies; their hidden legacy
            // NavigateToFolderBehavior value never reflected a user choice); only an explicit
            // "Windows File Explorer" is kept.
            loaded.ShowInExplorerTarget = ShowInExplorerTargets.Normalize(loaded.ShowInExplorerTarget);
            loaded.ExplorerDefaultView = ExplorerDefaultViews.Normalize(loaded.ExplorerDefaultView);
            loaded.ExplorerLocateOpensIn = Glide.Core.Navigation.ExplorerLocateTargets.Normalize(loaded.ExplorerLocateOpensIn);
        loaded.ExplorerBackReturnsToImage = Glide.Core.Navigation.ExplorerBackReturnModes.Normalize(loaded.ExplorerBackReturnsToImage);

            // 2.9-9 changes the fresh/default selection zoom-out mapping to reverse proportional scale.
            // If the property was absent in an older settings payload, adopt the new default. If an
            // existing file explicitly contains true/false, preserve that user-visible choice.
            if (!document.RootElement.TryGetProperty(nameof(GlideSettingsState.ReverseSelectionZoomOutScale), out _))
                loaded.ReverseSelectionZoomOutScale = true;

            // 3.3-1 changes status hover reveal from an implicit-on close behavior to an explicit
            // opt-in collapse behavior. Migrate older schemas that stored a value to the opt-in default once.
            // 5.0.0 turns the reveal on by default: a file that never stored the key simply gets true.
            if (sourceSchema < 3 && document.RootElement.TryGetProperty(nameof(GlideSettingsState.StatusShowOnHoverWhenClosed), out _))
                loaded.StatusShowOnHoverWhenClosed = false;

            // Schema 4 retunes the stock refinement delays. Migrate only untouched stock values;
            // User custom remains authoritative. This also upgrades existing Balanced installations
            // that persisted the historical 40/90 ms defaults instead of adopting the new 20 ms default.
            if (sourceSchema < 4 && !loaded.PerformanceUserCustom)
            {
                if (string.Equals(loaded.InitialImageQuality, "Balanced", StringComparison.OrdinalIgnoreCase) &&
                    loaded.AdaptivePreviewDelayMs is 40 or 90)
                    loaded.AdaptivePreviewDelayMs = 20;
                else if (string.Equals(loaded.InitialImageQuality, "Maximum speed", StringComparison.OrdinalIgnoreCase) &&
                    loaded.AdaptivePreviewDelayMs == 180)
                    loaded.AdaptivePreviewDelayMs = 60;
                else if (string.Equals(loaded.InitialImageQuality, "Maximum quality", StringComparison.OrdinalIgnoreCase) &&
                    loaded.AdaptivePreviewDelayMs != 5)
                    loaded.AdaptivePreviewDelayMs = 5;
            }

            // Schema 5 replaces a hard-coded keyboard bounce guard with the user-facing navigation
            // interval and ships an 80 ms stock default, so a duplicated key event cannot skip an
            // image. 0 was the only historical stock value for this field, so migrate exactly that
            // stock value; users who want unlimited navigation can set 0 again explicitly.
            if (sourceSchema < 5 && loaded.NavigationRateLimitMs == 0)
                loaded.NavigationRateLimitMs = PreviousNavigationRateLimitDefaultMs;
            // Schema 14 (5.0.6) raises the stock default to 120 ms; only the untouched old default 80 moves.
            loaded.NavigationRateLimitMs = MigrateNavigationRateLimit(sourceSchema, loaded.NavigationRateLimitMs);
            loaded.NavigationRateLimitMs = Math.Clamp(loaded.NavigationRateLimitMs, 0, 600000);

            // 2.9-5 gives new Explorer tabs an independent navigation-memory scope. The historical
            // Boolean reused the main Open location and defaulted off; when the new scope first appears,
            // adopt the new product default without copying another picker's history into it.
            if (!document.RootElement.TryGetProperty(nameof(GlideSettingsState.LastNewExplorerTabDirectory), out _))
                loaded.NewExplorerTabsUseLastLocation = true;

            // Glide 2.9-5 retains the 2.9-4 stock Balanced performance preset so dragging/panning keeps
            // the full-quality frame. Migrate only the exact old stock pairing. A user who had
            // independently changed interaction quality would have been classified as User custom,
            // so PerformanceUserCustom protects that explicit override.
            if (!loaded.PerformanceUserCustom &&
                string.Equals(loaded.InitialImageQuality, "Balanced", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(loaded.InteractivePanQuality, "Balanced", StringComparison.OrdinalIgnoreCase))
            {
                loaded.InteractivePanQuality = "Full quality";
            }
            if (string.IsNullOrWhiteSpace(loaded.LastTabCloseBehavior) ||
                (loaded.LastTabCloseBehavior is not ("Open home page" or "Keep last tab open" or "Close program")))
                loaded.LastTabCloseBehavior = "Open home page";
            loaded.FolderNavOrder = loaded.FolderNavOrder switch
            {
                "Modified date (oldest first)" => "Modified date (oldest first)",
                "Creation date (oldest first)" => "Creation date (oldest first)",
                _ => "Alphabetical"
            };
            loaded.GlowIntensityPercent = Math.Clamp(loaded.GlowIntensityPercent, 0, 100);
            // 2.9-5 makes neighbour-prefetch depth part of the stock performance profile. Older builds
            // shipped 2 as the untouched persisted default and deliberately left it profile-independent.
            // Migrate only that exact old stock value; non-default depths remain authoritative user choices.
            if (!loaded.PerformanceUserCustom && loaded.PrefetchDepth == 2)
            {
                loaded.PrefetchDepth = string.Equals(loaded.InitialImageQuality, "Maximum speed", StringComparison.OrdinalIgnoreCase) ? 3 : 5;
            }
            loaded.PrefetchDepth = Math.Clamp(loaded.PrefetchDepth, 1, 30);
            loaded.PrefetchSiblingFolderImageCount = Math.Clamp(loaded.PrefetchSiblingFolderImageCount, 1, 30);
            loaded.StatusBarGlobalScalePercent = Math.Clamp(loaded.StatusBarGlobalScalePercent, 60, 160);
            loaded.StatusBarMaximizedBoostPercent = Math.Clamp(loaded.StatusBarMaximizedBoostPercent, 0, 30);
            // Files written before the reveal-area adjustment existed load 0 (exactly the bar's box); a bad value is clamped.
            // (An earlier unreleased 5.0.0 build stored StatusRevealZoneHeight; that key is gone and is ignored when present.)
            loaded.StatusRevealZoneExpand = Glide.Core.StatusBarAutoHide.NormalizeRevealZoneExpand(loaded.StatusRevealZoneExpand);
            loaded.SlideshowLeftClickAction = Glide.Core.Settings.SlideshowLeftClickActions.Normalize(loaded.SlideshowLeftClickAction);
            loaded.NavigationSortOrder = Glide.Core.ImageSortOrder.NormalizeDisplayName(loaded.NavigationSortOrder);
            // Stage B: loading no longer activates the sort order (a persistence side effect); Program does it explicitly.
            loaded.SelectionBufferWindowedPercent = Math.Clamp(loaded.SelectionBufferWindowedPercent, 0, 100);
            loaded.SelectionBufferMaximizedPercent = Math.Clamp(loaded.SelectionBufferMaximizedPercent, 0, 100);
            loaded.Hotkeys ??= HotkeyCatalog.CreateDefaultMap();
            // Rows added by a later version (5.0.0: Back / Forward) get their defaults, except a key the saved map already
            // gives to another action: the user's binding is respected, never silently taken away.
            var skippedDefaults = HotkeyDefaultMigrations.AddMissingRows(loaded.Hotkeys);
            if (isSettingsFile) LastSkippedHotkeyDefaults = skippedDefaults;
            loaded.Gestures ??= GestureCatalog.CreateDefaultMap();
            // Schema 7 (4.8.0) folds the separate mouse toggles into explicit gesture bindings once,
            // so every user keeps exactly the behaviour they had (see GestureLegacyMigration).
            MigrateLegacyMouseToggles(loaded, sourceSchema);
            MigrateDefaultsSchema8(loaded, sourceSchema);
            loaded.CustomGestures = GestureCatalog.NormalizeCustomGestures(loaded.CustomGestures);
            // User-saved hotkey presets. Absent in older files (empty list), no schema bump needed.
            loaded.CustomHotkeyPresets = HotkeyPresetLibrary.Normalize(loaded.CustomHotkeyPresets);
            loaded.GestureRecentActions = NormalizeRecentGestureActions(loaded.GestureRecentActions);
            // Per-window placements (absent in older files). The Settings window's own older properties are folded into
            // the shared store once, so a placement saved before the store existed is kept.
            loaded.WindowPlacements = NormalizeWindowPlacements(loaded.WindowPlacements, loaded);
            // Update checker memory (5.0.1; absent in older files): junk values dropped, times kept as UTC.
            loaded.NormalizeUpdateState();
            loaded.TitleBarButtons = loaded.TitleBarButtons is null
                ? TitleBarButtonCatalog.DefaultButtonIds.ToList()
                : TitleBarButtonCatalog.Normalize(loaded.TitleBarButtons).ToList();
            // Customizable-chrome migration: Settings was accidentally omitted from the first
            // four-button default. Upgrade only that exact untouched historical default so existing
            // custom title-bar layouts (including users who intentionally remove Settings) remain authoritative.
            string[] previousTitleBarDefault = ["folder.previous", "folder.next", "file.open", "window.alwaysOnTop"];
            if (loaded.TitleBarButtons.SequenceEqual(previousTitleBarDefault, StringComparer.OrdinalIgnoreCase))
                loaded.TitleBarButtons = TitleBarButtonCatalog.DefaultButtonIds.ToList();
            string[] previous28TitleBarDefault = ["folder.previous", "folder.next", "file.open", "window.alwaysOnTop", "settings"];
            if (loaded.TitleBarButtons.SequenceEqual(previous28TitleBarDefault, StringComparer.OrdinalIgnoreCase))
                loaded.TitleBarButtons = TitleBarButtonCatalog.DefaultButtonIds.ToList();
            // Schema 9 (5.0.0 Back / Forward): the untouched previous default gains the Back / Forward buttons once. Any
            // other (customised) layout is kept exactly as it is; a file already at schema 9 is never migrated again, so
            // a user who removes Back / Forward later does not get them back on the next start.
            if (sourceSchema < TitleBarNavigationSchema &&
                loaded.TitleBarButtons.SequenceEqual(TitleBarButtonCatalog.PreviousDefaultButtonIds, StringComparer.OrdinalIgnoreCase))
                loaded.TitleBarButtons = TitleBarButtonCatalog.DefaultButtonIds.ToList();
            // Legacy profiles used one Boolean for the fullscreen X. Preserve an explicit historical
            // "X closes Glide" preference when the new per-button action fields are first introduced.
            if (loaded.FullscreenXClosesApp && string.Equals(loaded.CaptionFullscreenCloseAction, "Exit fullscreen maximized", StringComparison.OrdinalIgnoreCase))
                loaded.CaptionFullscreenCloseAction = "Close Glide";
            // Historical Edge-style chrome migration: only convert the exact old untouched tab-size defaults.
            // Explicit user-customized widths remain unchanged.
            // Migrate only known untouched historical defaults. User-customised tab widths remain
            // authoritative. The current defaults intentionally use a broader Edge-like hit target.
            if ((loaded.TabMinWidth == 125 && loaded.TabMaxWidth == 240) ||
                (loaded.TabMinWidth == 90 && loaded.TabMaxWidth == 200))
            {
                loaded.TabMinWidth = 120;
                loaded.TabMaxWidth = 240;
            }
            // Keyboard hotkeys are deliberately keyboard-only. Older builds briefly
            // allowed mouse buttons into this map; strip those stale entries on load.
            foreach (var key in loaded.Hotkeys.Keys.ToList())
                loaded.Hotkeys[key] = loaded.Hotkeys[key]
                    .Where(x => !IsMouseShortcut(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            // The historical five-key Irfan-like default is ambiguous: it may be an explicit modern
            // IrfanView preset. Do not reactivate destructive guessing on future schema bumps.
            // Schema 2 records this conservative migration decision explicitly.
            loaded.SettingsSchemaVersion = CurrentSettingsSchema;
            foreach (var action in HotkeyCatalog.All)
                if (!loaded.Hotkeys.ContainsKey(action.Id)) loaded.Hotkeys[action.Id] = action.DefaultShortcuts.ToList();
            foreach (var slot in GestureCatalog.Slots)
                if (!loaded.Gestures.ContainsKey(slot.Id)) loaded.Gestures[slot.Id] = slot.DefaultActionId;
            return loaded;
        }
    }


    public static void Save(GlideSettingsState settings)
    {
        try
        {
            settings.SettingsSchemaVersion = CurrentSettingsSchema;
            var path = GetSettingsPath();
            AtomicWriteAllText(path, JsonSerializer.Serialize(settings, AppJsonContext.Default.GlideSettingsState));
            SaveLaunchPolicyCache();
        }
        catch
        {
            // Settings persistence must never crash the viewer. Diagnostics can surface a failed save later.
        }
    }

    /// <summary>
    /// Writes only the update checker's memory (last check, latest known version, last prompt...) into glide.settings.json and
    /// leaves every other value exactly as it is on disk, so a check that finishes while Settings is previewing an unapplied
    /// change can never persist that preview. Without a settings file a file with default settings plus this memory is
    /// created (otherwise a fresh install would check again on every start).
    /// </summary>
    public static bool SaveUpdateState(Glide.Core.Updates.UpdateCheckState state)
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                var fresh = new GlideSettingsState();
                fresh.WriteUpdateState(state);
                Save(fresh);
                return true;
            }
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject root) return false;
            var values = new GlideSettingsState();
            values.WriteUpdateState(state);
            values.NormalizeUpdateState();
            // Serialised through the source-generated context (NativeAOT safe); only the update keys are copied over.
            var node = JsonSerializer.SerializeToNode(values, AppJsonContext.Default.GlideSettingsState) as System.Text.Json.Nodes.JsonObject;
            if (node is null) return false;
            foreach (var name in GlideSettingsState.UpdateStateProperties)
                root[name] = node[name]?.DeepClone();
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                root.WriteTo(writer);
            AtomicWriteAllText(path, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            SaveLaunchPolicyCache();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static GlideSettingsState Reset()
    {
        var state = new GlideSettingsState();
        Save(state);
        return state;
    }


    /// <summary>
    /// Reads only the per-window placement store (plus the older Settings window properties) from glide.settings.json,
    /// without building the whole settings graph. Missing or unreadable file: empty store.
    /// </summary>
    public static Dictionary<string, WindowPlacementState> LoadWindowPlacements()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            Dictionary<string, WindowPlacementState>? store = null;
            if (root.TryGetProperty(nameof(GlideSettingsState.WindowPlacements), out var node) && node.ValueKind == JsonValueKind.Object)
                store = node.Deserialize(AppJsonContext.Default.DictionaryStringWindowPlacementState);
            static int I(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var x) ? x : int.MinValue;
            static double D(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : 0;
            var legacy = new GlideSettingsState
            {
                SettingsWindowX = I(root, nameof(GlideSettingsState.SettingsWindowX)),
                SettingsWindowY = I(root, nameof(GlideSettingsState.SettingsWindowY)),
                SettingsWindowWidth = D(root, nameof(GlideSettingsState.SettingsWindowWidth)),
                SettingsWindowHeight = D(root, nameof(GlideSettingsState.SettingsWindowHeight)),
                SettingsWindowWasMaximized = root.TryGetProperty(nameof(GlideSettingsState.SettingsWindowWasMaximized), out var m) && m.ValueKind == JsonValueKind.True
            };
            return NormalizeWindowPlacements(store, legacy);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Writes only the "WindowPlacements" property of glide.settings.json, leaving every other value exactly as it is on
    /// disk. Used when a dialog closes: saving the main window's whole in-memory state there could persist Settings
    /// changes that are still only being previewed. Without a settings file nothing is written here; the next full save
    /// (which always includes the live store) creates it.
    /// </summary>
    public static bool SaveWindowPlacements(Dictionary<string, WindowPlacementState> placements)
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return false;
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject root) return false;
            root[nameof(GlideSettingsState.WindowPlacements)] = JsonSerializer.SerializeToNode(
                GlideSettingsState.ClonePlacements(placements), AppJsonContext.Default.DictionaryStringWindowPlacementState);
            // Written with a plain Utf8JsonWriter (no reflection-based serializer options: NativeAOT safe).
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                root.WriteTo(writer);
            AtomicWriteAllText(path, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            SaveLaunchPolicyCache();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Most remembered window placements kept; the least recently used are dropped beyond this.</summary>
    internal const int MaxWindowPlacements = 64;

    /// <summary>
    /// Drops null/blank entries, keeps the newest <see cref="MaxWindowPlacements"/>, and migrates the older
    /// SettingsWindow* properties into the "settings" entry when the store has none yet.
    /// </summary>
    internal static Dictionary<string, WindowPlacementState> NormalizeWindowPlacements(Dictionary<string, WindowPlacementState>? source, GlideSettingsState state)
    {
        var store = GlideSettingsState.ClonePlacements(source);
        if (!store.ContainsKey(SettingsWindowPlacement.StoreKey) && state.SettingsWindowWidth > 0 && state.SettingsWindowHeight > 0 &&
            !double.IsNaN(state.SettingsWindowWidth) && !double.IsNaN(state.SettingsWindowHeight))
        {
            store[SettingsWindowPlacement.StoreKey] = new WindowPlacementState
            {
                X = state.SettingsWindowX, Y = state.SettingsWindowY,
                Width = state.SettingsWindowWidth, Height = state.SettingsWindowHeight,
                Maximized = state.SettingsWindowWasMaximized
            };
        }
        if (store.Count > MaxWindowPlacements)
            foreach (var stale in store.OrderBy(kv => kv.Value.LastUsedUtcTicks).Take(store.Count - MaxWindowPlacements).Select(kv => kv.Key).ToArray())
                store.Remove(stale);
        return store;
    }

    private static bool IsMouseShortcut(string shortcut) =>
        shortcut.Contains("Mouse", StringComparison.OrdinalIgnoreCase);

    public static GlideSettingsState MergeHydratedPreservingEdits(
        GlideSettingsState baseline,
        GlideSettingsState current,
        GlideSettingsState hydrated)
    {
        var merged = hydrated.CloneState();
        foreach (var property in typeof(GlideSettingsState).GetProperties()
                     .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(GlideSettingsState.SettingsSchemaVersion)))
        {
            var before = property.GetValue(baseline);
            var now = property.GetValue(current);
            if (SettingValueEquals(before, now, property.PropertyType)) continue;
            property.SetValue(merged, CloneSettingValue(now, property.PropertyType));
        }
        merged.SettingsSchemaVersion = CurrentSettingsSchema;
        return merged;
    }

    private static bool SettingValueEquals(object? left, object? right, Type type)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (type == typeof(Dictionary<string, List<string>>))
        {
            var a = (Dictionary<string, List<string>>)left;
            var b = (Dictionary<string, List<string>>)right;
            return a.Count == b.Count && a.All(kv =>
                b.TryGetValue(kv.Key, out var values) && kv.Value.SequenceEqual(values, StringComparer.OrdinalIgnoreCase));
        }
        if (type == typeof(Dictionary<string, string>))
        {
            var a = (Dictionary<string, string>)left;
            var b = (Dictionary<string, string>)right;
            return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var value) &&
                                                     string.Equals(kv.Value, value, StringComparison.OrdinalIgnoreCase));
        }
        if (type == typeof(List<string>))
            return ((List<string>)left).SequenceEqual((List<string>)right, StringComparer.OrdinalIgnoreCase);
        if (type == typeof(List<CustomGesture>))
            return GlideSettingsState.CustomGesturesEqual((List<CustomGesture>)left, (List<CustomGesture>)right);
        if (type == typeof(List<CustomHotkeyPreset>))
            return GlideSettingsState.CustomHotkeyPresetsEqual((List<CustomHotkeyPreset>)left, (List<CustomHotkeyPreset>)right);
        if (type == typeof(Dictionary<string, WindowPlacementState>))
            return GlideSettingsState.WindowPlacementsEqual((Dictionary<string, WindowPlacementState>)left, (Dictionary<string, WindowPlacementState>)right);
        return Equals(left, right);
    }

    private static object? CloneSettingValue(object? value, Type type)
    {
        if (value is null) return null;
        if (type == typeof(Dictionary<string, List<string>>))
            return ((Dictionary<string, List<string>>)value)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        if (type == typeof(Dictionary<string, string>))
            return new Dictionary<string, string>((Dictionary<string, string>)value, StringComparer.OrdinalIgnoreCase);
        if (type == typeof(List<string>))
            return ((List<string>)value).ToList();
        if (type == typeof(List<CustomGesture>))
            return ((List<CustomGesture>)value).Select(x => x.Clone()).ToList();
        if (type == typeof(List<CustomHotkeyPreset>))
            return ((List<CustomHotkeyPreset>)value).Select(x => x.Clone()).ToList();
        if (type == typeof(Dictionary<string, WindowPlacementState>))
            return GlideSettingsState.ClonePlacements((Dictionary<string, WindowPlacementState>)value);
        return value;
    }

}
