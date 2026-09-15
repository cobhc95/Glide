using System.Text.Json;
using Glide.Core.Commands;

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
    private const int CurrentSettingsSchema = 4;

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

    private sealed record LaunchPolicyCache(
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
        InitialImageQuality: "Balanced",
        AdaptiveFastPreview: true,
        RapidPreviewLongestSide: 3000,
        BackgroundRefinement: true,
        AdaptivePreviewDelayMs: 20,
        SequentialForegroundReads: true,
        PredictivePrefetch: true,
        PrefetchDepth: 5,
        CacheItems: 16,
        CompressedCacheMegabytes: 256,
        DecodedCacheMegabytes: 256,
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
                var cached = JsonSerializer.Deserialize<LaunchPolicyCache>(File.ReadAllText(cachePath));
                if (cached is not null && cached.Schema == LaunchPolicySchema &&
                    cached.SettingsLength == identity.Length &&
                    cached.SettingsLastWriteUtcTicks == identity.LastWriteUtcTicks &&
                    !string.IsNullOrWhiteSpace(cached.Policy.ExternalOpenBehavior))
                    return cached.Policy;
            }
        }
        catch { }

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
            theme = B(root, nameof(GlideSettingsState.LightTheme), false) ? "Light" : d.ThemeChoice;
        theme = theme is "Dark" or "Neutral" or "Light" ? theme : d.ThemeChoice;

        var quality = S(root, nameof(GlideSettingsState.InitialImageQuality), d.InitialImageQuality);
        if (quality is not ("Maximum speed" or "Balanced" or "Maximum quality")) quality = d.InitialImageQuality;
        var viewMode = S(root, nameof(GlideSettingsState.DefaultViewMode), d.DefaultViewMode);
        if (viewMode is not ("Fit image" or "Fit width" or "Fit height" or "100%")) viewMode = d.DefaultViewMode;
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

    private static FirstFramePolicy FirstFramePolicyFrom(GlideSettingsState settings) => new(
        settings.ReuseSingleInstance, settings.ExternalOpenBehavior, settings.DefaultViewMode,
        settings.InitialImageQuality, settings.AdaptiveFastPreview, settings.RapidPreviewLongestSide,
        settings.BackgroundRefinement, settings.AdaptivePreviewDelayMs, settings.SequentialForegroundReads,
        settings.PredictivePrefetch, settings.PrefetchDepth, settings.CacheItems, settings.CompressedCacheMegabytes,
        settings.DecodedCacheMegabytes, settings.ProgressiveColorFirstPreview, settings.AlwaysStartWholeAppOverlayMode,
        settings.RememberWindowPlacement, settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight,
        settings.WindowWasMaximized, settings.ThemeChoice);

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
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void SaveLaunchPolicyCache(SettingsFileIdentity settingsFile, FirstFramePolicy policy)
    {
        try
        {
            var cache = new LaunchPolicyCache(LaunchPolicySchema, settingsFile.Length, settingsFile.LastWriteUtcTicks, policy);
            var path = GetLaunchPolicyPath();
            var json = JsonSerializer.Serialize(cache);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal)) return;
            AtomicWriteAllText(path, json);
        }
        catch { }
    }

    private static void SaveLaunchPolicyCache(GlideSettingsState settings)
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
        catch { }
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

    public static GlideSettingsState Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return new GlideSettingsState();
            var json = File.ReadAllText(path);
            // Parse the settings payload once. Glide 2.1 deserialised the string and then parsed the
            // same JSON again for migration probes; that duplicated cold-launch JSON work.
            using var document = JsonDocument.Parse(json);
            var sourceSchema = 0;
            if (document.RootElement.TryGetProperty(nameof(GlideSettingsState.SettingsSchemaVersion), out var schemaNode) &&
                schemaNode.ValueKind == JsonValueKind.Number && schemaNode.TryGetInt32(out var persistedSchema))
                sourceSchema = persistedSchema;
            var loaded = document.RootElement.Deserialize<GlideSettingsState>(JsonOptions()) ?? new GlideSettingsState();
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

            // 2.9-9 changes the fresh/default selection zoom-out mapping to reverse proportional scale.
            // If the property was absent in an older settings payload, adopt the new default. If an
            // existing file explicitly contains true/false, preserve that user-visible choice.
            if (!document.RootElement.TryGetProperty(nameof(GlideSettingsState.ReverseSelectionZoomOutScale), out _))
                loaded.ReverseSelectionZoomOutScale = true;

            // 3.3-1 changes status hover reveal from an implicit-on close behavior to an explicit
            // opt-in collapse behavior. Migrate older schemas to the new safe default once.
            if (sourceSchema < 3)
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
            if (string.Equals(loaded.LastTabCloseBehavior, "Open home page", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(loaded.LastTabCloseBehavior))
                loaded.LastTabCloseBehavior = "Keep last tab open";
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
            loaded.Hotkeys ??= HotkeyCatalog.CreateDefaultMap();
            foreach (var action in HotkeyCatalog.All)
                if (!loaded.Hotkeys.ContainsKey(action.Id)) loaded.Hotkeys[action.Id] = action.DefaultShortcuts.ToList();
            loaded.Gestures ??= GestureCatalog.CreateDefaultMap();
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
            SaveLaunchPolicyCache(loaded);
            return loaded;
        }
        catch
        {
            return new GlideSettingsState();
        }
    }


    public static void Save(GlideSettingsState settings)
    {
        try
        {
            settings.SettingsSchemaVersion = CurrentSettingsSchema;
            var path = GetSettingsPath();
            AtomicWriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions()));
            SaveLaunchPolicyCache(settings);
        }
        catch
        {
            // Settings persistence must never crash the viewer. Diagnostics can surface a failed save later.
        }
    }

    public static GlideSettingsState Reset()
    {
        var state = new GlideSettingsState();
        Save(state);
        return state;
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
        return value;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
