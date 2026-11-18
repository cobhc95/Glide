using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Data;
using Glide.Core.Reset;

namespace Glide.Core.Tests;

/// <summary>
/// The Settings window's Restore defaults button and Reset Glide's "All settings" item reset exactly the same thing: options
/// only (<see cref="AllSettingsReset"/>). Window geometry, remembered folders, hotkeys and gestures, the schema version, the
/// Windows startup switches, saved presets and the update checker's memory are kept, and the schema never goes backwards.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Data")]
public sealed class RestoreDefaultsScopeTests
{
    public RestoreDefaultsScopeTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private const string ChangedSlot = "wheel.windowed";

    /// <summary>A state with non-default options AND non-default history, placements, gestures, hotkeys and schema.</summary>
    internal static GlideSettingsState NonDefaultState()
    {
        var s = new GlideSettingsState { SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema };
        // Real options.
        s.OverlayAlwaysOnTop = true;
        s.StatusBarSize = "Large";
        s.ShowFolderCounter = true;
        s.ThemeChoice = "Light";
        s.LightTheme = true;
        s.HistorySize = 20;
        s.TabSidePanelWidth = 333;
        // Remembered folders (history).
        s.LastNewExplorerTabDirectory = @"C:\Explorer";
        s.LastOpenDirectory = @"C:\Open";
        s.LastOpenFileDirectory = @"C:\OpenFile";
        s.LastOverlayDirectory = @"C:\Overlay";
        s.LastProfileDirectory = @"C:\Profiles";
        // Window sizes and positions.
        s.WindowX = 120; s.WindowY = 80; s.WindowWidth = 1400; s.WindowHeight = 900; s.WindowWasMaximized = true;
        s.SettingsWindowX = 30; s.SettingsWindowY = 40; s.SettingsWindowWidth = 950; s.SettingsWindowHeight = 640;
        foreach (var key in new[] { "settings", "print", "confirm", "rename", "profile-add", "about" })
            s.WindowPlacements[key] = new WindowPlacementState { X = 10, Y = 20, Width = 700, Height = 500, LastUsedUtcTicks = 5 };
        for (var i = 0; i < 4; i++)
            s.OverlayWindowPlacements[$@"c:\pictures\{i}.jpg"] = new OverlayWindowPlacementState { X = i, Y = i, Width = 300, Height = 200 };
        // Hotkeys and mouse gestures.
        s.Gestures[ChangedSlot] = GestureCatalog.ZoomWheel;
        s.Hotkeys[HotkeyCatalog.All[0].Id] = ["Ctrl+Alt+Shift+F12"];
        s.CustomGestures.Add(new CustomGesture { Action = "view.fit" });
        s.GestureRecentActions.Add("view.fit");
        s.CustomHotkeyPresets.Add(new CustomHotkeyPreset { Name = "Mine" });
        // Startup entries, the saved User custom preset and the update checker's memory.
        s.StartWithWindowsInBackground = true;
        s.SpeedBoostEnabled = false;
        s.PerformanceUserCustomSaved = true;
        s.UserCustomPrefetchDepth = 9;
        s.UpdateLatestKnownVersion = "9.9.9";
        s.UpdateLastCheckedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        return s;
    }

    private static void AssertKept(GlideSettingsState before, GlideSettingsState after)
    {
        Assert.Equal(before.SettingsSchemaVersion, after.SettingsSchemaVersion);
        Assert.Equal(before.LastNewExplorerTabDirectory, after.LastNewExplorerTabDirectory);
        Assert.Equal(before.LastOpenDirectory, after.LastOpenDirectory);
        Assert.Equal(before.LastOpenFileDirectory, after.LastOpenFileDirectory);
        Assert.Equal(before.LastOverlayDirectory, after.LastOverlayDirectory);
        Assert.Equal(before.LastProfileDirectory, after.LastProfileDirectory);
        Assert.Equal((before.WindowX, before.WindowY, before.WindowWidth, before.WindowHeight, before.WindowWasMaximized),
                     (after.WindowX, after.WindowY, after.WindowWidth, after.WindowHeight, after.WindowWasMaximized));
        Assert.Equal((before.SettingsWindowX, before.SettingsWindowY, before.SettingsWindowWidth, before.SettingsWindowHeight),
                     (after.SettingsWindowX, after.SettingsWindowY, after.SettingsWindowWidth, after.SettingsWindowHeight));
        Assert.True(GlideSettingsState.WindowPlacementsEqual(before.WindowPlacements, after.WindowPlacements));
        Assert.Equal(before.OverlayWindowPlacements.Count, after.OverlayWindowPlacements.Count);
        foreach (var (key, value) in before.OverlayWindowPlacements) Assert.Equal(value, after.OverlayWindowPlacements[key]);
        Assert.Equal(GestureCatalog.ZoomWheel, after.Gestures[ChangedSlot]);
        Assert.Equal(before.Hotkeys[HotkeyCatalog.All[0].Id], after.Hotkeys[HotkeyCatalog.All[0].Id]);
        Assert.True(GlideSettingsState.CustomGesturesEqual(before.CustomGestures, after.CustomGestures));
        Assert.Equal(before.GestureRecentActions, after.GestureRecentActions);
        Assert.True(GlideSettingsState.CustomHotkeyPresetsEqual(before.CustomHotkeyPresets, after.CustomHotkeyPresets));
        Assert.Equal(before.StartWithWindowsInBackground, after.StartWithWindowsInBackground);
        Assert.Equal(before.SpeedBoostEnabled, after.SpeedBoostEnabled);
        Assert.Equal(before.PerformanceUserCustomSaved, after.PerformanceUserCustomSaved);
        Assert.Equal(before.UserCustomPrefetchDepth, after.UserCustomPrefetchDepth);
        Assert.Equal(before.UpdateLatestKnownVersion, after.UpdateLatestKnownVersion);
        Assert.Equal(before.UpdateLastCheckedUtc, after.UpdateLastCheckedUtc);
    }

    private static void AssertOptionsAreDefault(GlideSettingsState after)
    {
        var d = new GlideSettingsState();
        Assert.Equal(d.OverlayAlwaysOnTop, after.OverlayAlwaysOnTop);
        Assert.Equal(d.StatusBarSize, after.StatusBarSize);
        Assert.Equal(d.ShowFolderCounter, after.ShowFolderCounter);
        Assert.Equal(d.ThemeChoice, after.ThemeChoice);
        Assert.Equal(d.HistorySize, after.HistorySize);
        Assert.Equal(d.TabSidePanelWidth, after.TabSidePanelWidth);
    }

    // Pending-change names that are not options (the bug: they showed after Restore defaults).
    private static readonly string[] NonSettingChangeNames =
    [
        "Settings Schema Version", "Last New Explorer Tab Directory", "Last Open Directory", "Last Open File Directory",
        "Last Overlay Directory", "Last Profile Directory", "Overlay Window Placements", "Window X", "Window Y", "Window Width",
        "Window Height", "Window Was Maximized", "Settings Window X", "Settings Window Y", "Settings Window Width",
        "Settings Window Height", "Window placements", "Mouse gesture matrix", "Keyboard shortcuts", "Custom gestures",
        "Custom keyboard shortcut presets", "Start With Windows In Background", "Speed Boost Enabled", "User Custom Prefetch Depth",
        "Performance User Custom Saved",
    ];

    [Fact]
    public void Restore_defaults_resets_only_options_and_keeps_history_placements_gestures_and_schema()
    {
        var before = NonDefaultState();
        var after = SettingsDefaults.RestoreAllSettings(before);

        AssertOptionsAreDefault(after);
        AssertKept(before, after);
        // The input is not modified.
        Assert.True(before.OverlayAlwaysOnTop);

        var changes = SettingsChangeSet.Compute(before, after);
        Assert.Contains(changes, c => c.Name == "Overlay Always On Top");
        Assert.Contains(changes, c => c.Name == "Status Bar Size");
        Assert.Contains(changes, c => c.Name == "Show Folder Counter");
        foreach (var name in NonSettingChangeNames) Assert.DoesNotContain(changes, c => c.Name == name);
    }

    [Fact]
    public void Schema_version_never_decreases()
    {
        var newer = NonDefaultState();
        newer.SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema + 5;
        Assert.Equal(SettingsStore.CurrentSettingsSchema + 5, SettingsDefaults.RestoreAllSettings(newer).SettingsSchemaVersion);

        // The shared JSON reset: kept as it is, added from the defaults only when the file has none, never lowered.
        var defaults = new JsonObject { ["SettingsSchemaVersion"] = 11, ["ThemeChoice"] = "Dark" };
        var file = new JsonObject { ["SettingsSchemaVersion"] = 14, ["ThemeChoice"] = "Light" };
        Assert.True(AllSettingsReset.Apply(file, defaults));
        Assert.Equal(14, (int?)file["SettingsSchemaVersion"]);
        Assert.Equal("Dark", (string?)file["ThemeChoice"]);
        var noSchema = new JsonObject { ["ThemeChoice"] = "Light" };
        AllSettingsReset.Apply(noSchema, defaults);
        Assert.Equal(11, (int?)noSchema["SettingsSchemaVersion"]);
        var withoutDefaults = new JsonObject { ["SettingsSchemaVersion"] = 9, ["ThemeChoice"] = "Light" };
        AllSettingsReset.Apply(withoutDefaults, (JsonObject?)null);
        Assert.Equal(9, (int?)withoutDefaults["SettingsSchemaVersion"]);
        Assert.False(withoutDefaults.ContainsKey("ThemeChoice"));

        // Settings applied from the dialog can never lower the live schema either.
        var incoming = new GlideSettingsState { SettingsSchemaVersion = 1 };
        SettingsDefaults.KeepRuntimeOwnedValues(incoming, new GlideSettingsState { SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema });
        Assert.Equal(SettingsStore.CurrentSettingsSchema, incoming.SettingsSchemaVersion);
    }

    [Fact]
    public void Shared_reset_writes_defaults_for_options_drops_stale_keys_and_leaves_every_kept_value()
    {
        var defaults = JsonSerializer.SerializeToNode(new GlideSettingsState(), AppJsonContext.Default.GlideSettingsState)!.AsObject();
        var file = JsonSerializer.SerializeToNode(NonDefaultState(), AppJsonContext.Default.GlideSettingsState)!.AsObject();
        file["StatusRevealZoneHeight"] = 40; // a key an older build wrote
        var original = file.DeepClone().AsObject();

        Assert.True(AllSettingsReset.Apply(file, defaults));
        Assert.False(file.ContainsKey("StatusRevealZoneHeight"));
        foreach (var (name, value) in original)
        {
            if (AllSettingsReset.IsKept(name)) Assert.True(JsonNode.DeepEquals(value, file[name]), $"{name} must be kept");
            else if (defaults.ContainsKey(name)) Assert.True(JsonNode.DeepEquals(defaults[name], file[name]), $"{name} must be its default");
        }
        // Every option is written (not removed), so the loader's "key missing = older file" migrations never fire.
        foreach (var (name, _) in defaults) Assert.True(file.ContainsKey(name), name);
        // A second run changes nothing.
        Assert.False(AllSettingsReset.Apply(file, defaults));
    }

    [Fact]
    public void Kept_property_names_exist_in_the_settings_state()
    {
        var names = typeof(GlideSettingsState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var kept in ResetCatalog.AllSettingsKeptProperties) Assert.Contains(kept, names);
        Assert.Equal(GlideSettingsState.UpdateStateProperties.OrderBy(x => x), ResetCatalog.UpdateCheckerMemoryProperties.OrderBy(x => x));
        // Real options are never kept.
        foreach (var option in new[] { "OverlayAlwaysOnTop", "StatusBarSize", "ShowFolderCounter", "ThemeChoice", "TitleBarButtons", "StatusBarGroups" })
            Assert.False(AllSettingsReset.IsKept(option), option);
    }

    [Fact]
    public void Reset_Glide_all_settings_on_disk_and_Restore_defaults_give_the_same_settings()
    {
        using var s = new ResetScratch();
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = s.Settings;
        try
        {
            SettingsStore.Save(NonDefaultState());
            var before = SettingsStore.Load();
            var expected = SettingsDefaults.RestoreAllSettings(before);

            var env = s.Environment with { SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema, DefaultSettingsJson = SettingsDefaults.DefaultSettingsJson() };
            var services = s.Services();
            var report = ResetExecutor.Execute(ResetPlanner.Plan(env, [ResetItemId.AllSettings]), services.FileSystem, services.Registry, services.ExecutorOptions);
            Assert.True(report.AllSucceeded, report.Describe());

            var after = SettingsStore.Load();
            AssertOptionsAreDefault(after);
            AssertKept(before, after);
            var a = JsonSerializer.SerializeToNode(expected, AppJsonContext.Default.GlideSettingsState);
            var b = JsonSerializer.SerializeToNode(after, AppJsonContext.Default.GlideSettingsState);
            Assert.True(JsonNode.DeepEquals(a, b), "Restore defaults and Reset Glide > All settings disagree:\n" + a + "\n---\n" + b);
        }
        finally { SettingsStore.SettingsDirectoryOverrideForTests.Value = previous; }
    }

    [Fact]
    public void All_settings_deletes_a_settings_file_that_is_not_valid_json()
    {
        using var s = new ResetScratch();
        var file = s.Write(s.Settings, "glide.settings.json", "{ not json");
        var services = s.Services();
        var report = ResetExecutor.Execute(ResetPlanner.Plan(s.Environment, [ResetItemId.AllSettings]), services.FileSystem, services.Registry, services.ExecutorOptions);
        Assert.True(report.AllSucceeded, report.Describe());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Unset_geometry_from_the_Settings_snapshot_never_replaces_the_live_window_placement()
    {
        var live = NonDefaultState();
        var incoming = new GlideSettingsState { SettingsSchemaVersion = 1, ShowFolderCounter = true }; // WindowX = int.MinValue, width 0
        SettingsDefaults.KeepRuntimeOwnedValues(incoming, live);
        Assert.Equal((120, 80, 1400.0, 900.0, true), (incoming.WindowX, incoming.WindowY, incoming.WindowWidth, incoming.WindowHeight, incoming.WindowWasMaximized));
        Assert.Equal(4, incoming.OverlayWindowPlacements.Count);
        Assert.NotSame(live.OverlayWindowPlacements, incoming.OverlayWindowPlacements);
        Assert.True(incoming.ShowFolderCounter); // options still come from Settings
    }

    [Fact]
    public async Task Settings_window_Restore_defaults_stages_only_options_and_Apply_keeps_everything_else()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            GlideSettingsState? staged = null, applied = null;
            var window = new SettingsWindow(NonDefaultState(), (state, commit) => { if (commit) applied = state; else staged = state; });
            window.Show();
            try
            {
                // The window's own baseline (it normalises a few values, e.g. incomplete custom gestures, while loading).
                var committed = ((GlideSettingsState)typeof(SettingsWindow).GetField("_committed", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(window)!).CloneState();
                Assert.Equal(GestureCatalog.ZoomWheel, committed.Gestures[ChangedSlot]);
                Assert.Equal(@"C:\Open", committed.LastOpenDirectory);
                Assert.True(committed.OverlayAlwaysOnTop);
                window.RestoreDefaults();
                Assert.NotNull(staged);
                AssertOptionsAreDefault(staged!);
                AssertKept(committed, staged!);

                var changes = (List<SettingChange>)typeof(SettingsWindow).GetMethod("GetNetChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [null])!;
                Assert.Contains(changes, c => c.Name == "Overlay Always On Top");
                Assert.Contains(changes, c => c.Name == "Status Bar Size");
                foreach (var name in NonSettingChangeNames) Assert.DoesNotContain(changes, c => c.Name == name);

                // A later edit re-reads the controls: the control-less options stay at their defaults, the rest stays kept.
                typeof(SettingsWindow).GetMethod("ApplyClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [null, new Avalonia.Interactivity.RoutedEventArgs()]);
                Assert.NotNull(applied);
                AssertOptionsAreDefault(applied!);
                AssertKept(committed, applied!);
                Assert.Equal(SettingsStore.CurrentSettingsSchema, applied!.SettingsSchemaVersion);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
