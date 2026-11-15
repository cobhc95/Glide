using System.Reflection;
using System.Text.RegularExpressions;
using Glide.App.Printing;
using Glide.App.Settings;
using Glide.Core.Reset;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>Reset Glide: which concrete files, folders and registry values each item removes (nothing is deleted here).</summary>
[Collection("PrintSettingsStoreStatics")]
public sealed class ResetPlannerTests
{
    private static IReadOnlyList<ResetTarget> Targets(ResetScratch s, ResetItemId id) => ResetPlanner.TargetsFor(s.Environment, id);

    [Fact]
    public void Catalogue_has_the_ten_items_of_the_approved_mockup_in_three_groups()
    {
        Assert.Equal(10, ResetCatalog.Items.Count);
        Assert.Equal(
            ["All settings", "Hotkeys and mouse gestures", "Saved profiles and presets", "Window sizes and positions",
             "Recent files and Home list", "Last session", "Image and thumbnail caches", "Downloaded video codec pack",
             "Registry entries", "Startup and background entries"],
            ResetCatalog.Items.Select(x => x.Title).ToArray());
        Assert.Equal(4, ResetCatalog.Items.Count(x => x.Group == ResetGroup.Settings));
        Assert.Equal(4, ResetCatalog.Items.Count(x => x.Group == ResetGroup.HistoryAndData));
        Assert.Equal(2, ResetCatalog.Items.Count(x => x.Group == ResetGroup.WindowsIntegration));
        Assert.Equal("Settings", ResetCatalog.GroupTitle(ResetGroup.Settings));
        Assert.Equal("History and data", ResetCatalog.GroupTitle(ResetGroup.HistoryAndData));
        Assert.Equal("Windows integration", ResetCatalog.GroupTitle(ResetGroup.WindowsIntegration));
        Assert.All(ResetCatalog.Items, x => Assert.False(string.IsNullOrWhiteSpace(x.Description)));
    }

    [Fact]
    public void Every_item_has_a_searchable_catalogue_entry_in_Windows_and_developer()
    {
        foreach (var item in ResetCatalog.Items.Select(x => x.SettingId).Prepend(ResetCatalog.CardSettingId))
        {
            var entry = Assert.Single(SettingsCatalog.All, x => x.Id == item);
            Assert.Equal("Windows & developer", entry.Category);
            Assert.Equal(SettingKind.Action, entry.Kind);
            Assert.Contains("reset", entry.SearchTerms);
            // Destructive: never bindable to a hotkey or gesture, with the reason written down.
            Assert.True(SettingActionCatalog.Excluded.TryGetValue(item, out var reason) && reason.Length > 20, item);
        }
        Assert.Contains("factory reset", SettingsCatalog.All.Single(x => x.Id == "reset.card").SearchTerms);
        Assert.Contains("restore defaults", SettingsCatalog.All.Single(x => x.Id == "reset.card").SearchTerms);
        // The Settings footer's Restore defaults button is a different feature: the descriptions say so.
        Assert.Contains("Restore defaults", SettingsCatalog.All.Single(x => x.Id == "reset.card").Description);
        Assert.Contains("Restore defaults", SettingsCatalog.All.Single(x => x.Id == "reset.allSettings").Description);
    }

    [Fact]
    public void All_settings_removes_the_settings_file_its_cache_and_the_small_settings_files()
    {
        using var s = new ResetScratch();
        var targets = Targets(s, ResetItemId.AllSettings);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "glide.settings.json")), targets);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "glide.launch.policy.json")), targets);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "print.settings.json")), targets);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "thumbnail.settings.json")), targets);
        Assert.Contains(new FilesMatchingTarget(s.Settings, "glide.settings.json.*.tmp"), targets);
        // History, session and profiles are their own choices.
        Assert.DoesNotContain(targets, t => t.Describe().Contains("history") || t.Describe().Contains("profiles") || t.Describe().Contains("workspace"));
    }

    [Fact]
    public void Hotkeys_window_and_profile_items_edit_properties_of_the_settings_file()
    {
        using var s = new ResetScratch();
        var file = Path.Combine(s.Settings, "glide.settings.json");

        var hotkeys = Assert.IsType<JsonPropertiesTarget>(Assert.Single(Targets(s, ResetItemId.HotkeysAndGestures)));
        Assert.Equal(file, hotkeys.Path);
        Assert.Equal(["Hotkeys", "CustomHotkeyPresets", "Gestures", "CustomGestures", "GestureRecentActions"], hotkeys.RemoveProperties);

        var window = Assert.IsType<JsonPropertiesTarget>(Assert.Single(Targets(s, ResetItemId.WindowPlacements)));
        Assert.Contains("WindowX", window.RemoveProperties);
        Assert.Contains("SettingsWindowWidth", window.RemoveProperties);
        Assert.Contains("OverlayWindowPlacements", window.RemoveProperties);
        // The shared per-window store (Print window, dialogs, Settings) is wiped too.
        Assert.Contains("WindowPlacements", window.RemoveProperties);

        var profiles = Targets(s, ResetItemId.ProfilesAndPresets);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "glide.profiles.json")), profiles);
    }

    [Fact]
    public void History_session_codec_and_cache_items_name_exactly_their_files()
    {
        using var s = new ResetScratch();
        Assert.Equal([new FileTarget(Path.Combine(s.Settings, "glide.history.json"))], Targets(s, ResetItemId.RecentHistory));

        var session = Targets(s, ResetItemId.LastSession);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "glide.workspace-session.json")), session);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "overlay-layout.json")), session);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "overlay-layout.json.bak")), session);
        Assert.Contains(session, t => t is JsonPropertiesTarget j && j.RemoveProperties.Contains("LastOpenDirectory"));

        Assert.Equal([new DirectoriesMatchingTarget(s.Local, "ffmpeg-*")], Targets(s, ResetItemId.CodecPack));

        var caches = Targets(s, ResetItemId.Caches);
        Assert.Contains(new DirectoryTarget(Path.Combine(s.Local, "Startup Diagnostics")), caches);
        Assert.Contains(new FileTarget(Path.Combine(s.Settings, "thumbnail.settings.json")), caches);
        Assert.Contains(new FileTarget(Path.Combine(s.Temp, "glide-diagnostic-transfer.jpg")), caches);
        // crash logs are evidence, and Windows' own thumbnail database is shared by every app.
        Assert.DoesNotContain(caches, t => t.Describe().Contains("crash.log") || t.Describe().Contains("thumbcache"));
    }

    [Fact]
    public void Registry_item_covers_open_with_file_types_thumbnails_and_the_opt_out_but_only_in_HKCU()
    {
        using var s = new ResetScratch();
        var targets = Targets(s, ResetItemId.RegistryEntries);
        Assert.Contains(new RegistryTreeTarget(@"Software\Classes\Applications\Glide.exe"), targets);
        Assert.Contains(new RegistryTreeTarget(@"Software\Classes\Glide.Image"), targets);
        Assert.Contains(new RegistryTreeTarget(@"Software\Glide\Capabilities"), targets);
        Assert.Contains(new RegistryTreeTarget(@"Software\Glide\ExplorerThumbnails"), targets);
        Assert.Contains(new RegistryTreeTarget(@"Software\Classes\CLSID\{6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}"), targets);
        Assert.Contains(new RegistryValueTarget(@"Software\RegisteredApplications", "Glide", OnlyIfEquals: @"Software\Glide\Capabilities"), targets);
        foreach (var extension in s.Extensions)
        {
            Assert.Contains(new RegistryValueTarget($@"Software\Classes\{extension}\OpenWithProgids", "Glide.Image"), targets);
            Assert.Contains(new RegistryValueTarget($@"Software\Classes\{extension}\ShellEx\{{E357FCCD-A995-4576-B01F-234630154E96}}", "", OnlyIfEquals: "{6E3C1B2A-9F41-4E7C-9B1E-2C7A5D8F0A31}"), targets);
        }
        // Same as "Remove Glide Open With entries": the opt-out keeps Glide from silently registering itself again.
        Assert.Contains(new RegistryDwordTarget(@"Software\Glide", "OpenWithOptOut", 1), targets);
        Assert.All(targets.OfType<RegistryTreeTarget>(), t => Assert.StartsWith("Software\\", t.SubKey));
        Assert.DoesNotContain(targets, t => t.Describe().Contains("HKLM", StringComparison.OrdinalIgnoreCase) || t.Describe().Contains("LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Startup_item_removes_the_run_value_and_switches_off_the_background_settings()
    {
        using var s = new ResetScratch();
        var targets = Targets(s, ResetItemId.StartupEntries);
        Assert.Contains(new RegistryValueTarget(@"Software\Microsoft\Windows\CurrentVersion\Run", "Glide"), targets);
        var json = Assert.IsType<JsonPropertiesTarget>(Assert.Single(targets, t => t is JsonPropertiesTarget));
        Assert.False(json.SetBooleans["StartWithWindowsInBackground"]);
        Assert.False(json.SetBooleans["SpeedBoostEnabled"]);
        Assert.Equal(7, json.CreateWithSchemaVersion);
    }

    [Fact]
    public void Plan_runs_items_in_catalogue_order_and_knows_when_a_restart_is_needed()
    {
        using var s = new ResetScratch();
        var plan = ResetPlanner.Plan(s.Environment, [ResetItemId.StartupEntries, ResetItemId.AllSettings, ResetItemId.Caches]);
        Assert.Equal([ResetItemId.AllSettings, ResetItemId.Caches, ResetItemId.StartupEntries], plan.Items.Select(x => x.Item.Id).ToArray());
        Assert.True(plan.NeedsRestart);
        Assert.True(plan.TouchesRegistry);

        // In-place items: nothing holds their data in memory, so no close-and-restart.
        Assert.False(ResetPlanner.Plan(s.Environment, [ResetItemId.Caches]).NeedsRestart);
        Assert.False(ResetPlanner.Plan(s.Environment, [ResetItemId.RegistryEntries]).NeedsRestart);
        Assert.False(ResetPlanner.Plan(s.Environment, [ResetItemId.Caches, ResetItemId.RegistryEntries]).NeedsRestart);
        foreach (var id in new[] { ResetItemId.AllSettings, ResetItemId.HotkeysAndGestures, ResetItemId.ProfilesAndPresets, ResetItemId.WindowPlacements,
                     ResetItemId.RecentHistory, ResetItemId.LastSession, ResetItemId.CodecPack, ResetItemId.StartupEntries })
            Assert.True(ResetPlanner.Plan(s.Environment, [id]).NeedsRestart, id.ToString());
        Assert.Empty(ResetPlanner.Plan(s.Environment, []).Items);
    }

    [Fact]
    public void Every_planned_target_is_inside_Glides_own_folders_and_never_the_install_folder()
    {
        using var s = new ResetScratch();
        var plan = ResetPlanner.Plan(s.Environment, ResetCatalog.Items.Select(x => x.Id));
        foreach (var target in plan.AllTargets)
        {
            Assert.Null(ResetPathGuard.Refusal(target, s.Environment));
            if (target is FileTarget file) Assert.False(file.Path.StartsWith(s.Install, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Portable_layout_where_settings_sit_beside_the_program_never_deletes_program_files()
    {
        using var s = new ResetScratch();
        // Portable build: settings folder == install folder.
        var env = s.Environment with { SettingsDirectory = s.Install };
        var plan = ResetPlanner.Plan(env, ResetCatalog.Items.Select(x => x.Id));
        foreach (var target in plan.AllTargets) Assert.Null(ResetPathGuard.Refusal(target, env));
        Assert.NotNull(ResetPathGuard.Refusal(new FileTarget(Path.Combine(s.Install, "Glide.exe")), env));
        Assert.NotNull(ResetPathGuard.Refusal(new FileTarget(Path.Combine(s.Install, "Glide.Core.dll")), env));
        Assert.NotNull(ResetPathGuard.Refusal(new DirectoryTarget(s.Install), env));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Users\someone\Documents\photo.jpg")]
    public void Guard_refuses_files_outside_the_known_folders(string path)
    {
        using var s = new ResetScratch();
        Assert.NotNull(ResetPathGuard.Refusal(new FileTarget(path), s.Environment));
    }

    [Fact]
    public void Guard_refuses_roots_the_temp_folder_itself_and_overbroad_patterns()
    {
        using var s = new ResetScratch();
        Assert.NotNull(ResetPathGuard.Refusal(new DirectoryTarget(@"C:\"), s.Environment));
        Assert.NotNull(ResetPathGuard.Refusal(new DirectoryTarget(s.Temp), s.Environment));
        Assert.NotNull(ResetPathGuard.Refusal(new DirectoryTarget(s.Local), s.Environment));
        Assert.NotNull(ResetPathGuard.Refusal(new DirectoryTarget(s.Settings), s.Environment));
        Assert.NotNull(ResetPathGuard.Refusal(new FilesMatchingTarget(s.Temp, "*.*"), s.Environment));
        Assert.NotNull(ResetPathGuard.Refusal(new FilesMatchingTarget(s.Temp, @"..\*.json"), s.Environment));
        Assert.NotNull(ResetPathGuard.Refusal(new FileTarget("relative.json"), s.Environment));
        Assert.Null(ResetPathGuard.Refusal(new DirectoryTarget(Path.Combine(s.Local, "Startup Diagnostics")), s.Environment));
    }

    // ---- the lists in ResetCatalog must stay in step with the real stores and settings state ----

    [Fact]
    public void Catalogue_file_names_match_what_the_stores_actually_write()
    {
        // Behavioural: run the real stores against an isolated directory and check the files they create.
        var dir = Path.Combine(Path.GetTempPath(), "glide-reset-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = dir;
        PrintSettingsStore.DirectoryOverrideForTests = dir;
        try
        {
            SettingsStore.Save(new GlideSettingsState());
            ProfileStore.Add("Reset names", new GlideSettingsState());
            WorkspaceSessionStore.Save([new Glide.Core.Workspace.HomeTabState(Guid.NewGuid())], 0);
            PrintSettingsStore.Save(new PrintDocumentSettings());
            foreach (var name in new[] { ResetCatalog.SettingsFileName, ResetCatalog.ProfilesFileName, ResetCatalog.SessionFileName, ResetCatalog.PrintSettingsFileName })
                Assert.True(File.Exists(Path.Combine(dir, name)), name + " was not written by its store");
        }
        finally
        {
            PrintSettingsStore.DirectoryOverrideForTests = null;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(dir, true); } catch { /* temp cleanup only */ }
        }
    }

    [Fact]
    public void Catalogue_file_names_match_the_stores_that_write_them_in_source()
    {
        // Source-text on purpose: these names are private constants of stores that need the registry, the app window or
        // a real profile folder to run; the stores covered behaviourally above are checked in the test before this one.
        string Source(string path) => SourceText.Read(path);
        Assert.Contains($"\"{ResetCatalog.LaunchPolicyFileName}\"", Source("src/Glide.App/Settings/SettingsStore.cs"));
        Assert.Contains($"\"{ResetCatalog.HistoryFileName}\"", Source("src/Glide.App/Settings/RecentHistoryStore.cs"));
        Assert.Contains($"\"{ResetCatalog.ThumbnailSettingsFileName}\"", Source("src/Glide.App/Platform/ThumbnailSettingsStore.cs"));
        Assert.Contains($"\"{ResetCatalog.OverlayLayoutFileName}\"", Source("src/Glide.App/MainWindow.Pickers.cs"));
        Assert.Contains($"\"{ResetCatalog.StartupDiagnosticsFolderName}\"", Source("src/Glide.App/MainWindow.Sessions.cs"));
        Assert.Contains("\"ffmpeg-\"", Source("src/Glide.Imaging/VideoDecoderPack.cs"));
        Assert.Contains("\"ffmpeg-download-\"", Source("src/Glide.Imaging/VideoDecoderPack.cs"));
        // The overlay layout is also kept as ".bak" by OverlayState.
        Assert.Contains(".bak", Source("src/Glide.Core/OverlayState.cs"));
    }

    [Fact]
    public void Catalogue_registry_names_match_the_registration_code()
    {
        var association = SourceText.Read("src/Glide.App/Platform/WindowsFileAssociationRegistration.cs");
        Assert.Contains("ApplicationExe = \"Glide.exe\"", association);
        Assert.Contains("ProgId = \"Glide.Image\"", association);
        Assert.Contains($"StateKeyPath = @\"{ResetCatalog.StateKey}\"", association);
        Assert.Contains($"\"{ResetCatalog.OpenWithOptOutValue}\"", association);
        Assert.Contains($"\"{ResetCatalog.OpenWithVersionValue}\"", association);
        Assert.Contains($"@\"{ResetCatalog.CapabilitiesKey}\"", association);
        Assert.Contains("Software\\Classes\\Applications\\{ApplicationExe}", association);
        Assert.Contains("Software\\Classes\\{ProgId}", association);
        Assert.Contains("Software\\Classes\\{extension}\\OpenWithProgids", association);
        Assert.Contains("Software\\RegisteredApplications", association);

        var thumbnails = SourceText.Read("src/Glide.App/Platform/WindowsThumbnailRegistration.cs");
        Assert.Contains($"ProviderClsid = \"{ResetCatalog.ThumbnailProviderClsid}\"", thumbnails);
        Assert.Contains($"ThumbnailHandlerGuid = \"{ResetCatalog.ThumbnailHandlerGuid}\"", thumbnails);
        Assert.Contains("Software\\Classes\\{extension}\\ShellEx\\{ThumbnailHandlerGuid}", thumbnails);
        Assert.Contains($"KeyPath = @\"{ResetCatalog.ThumbnailConfigKey}\"", SourceText.Read("src/Glide.App/Platform/ThumbnailSettingsStore.cs"));

        var startup = SourceText.Read("src/Glide.App/Platform/WindowsStartupRegistration.cs");
        Assert.Contains($"RunKeyPath = @\"{ResetCatalog.RunKey}\"", startup);
        Assert.Contains($"ValueName = \"{ResetCatalog.RunValueName}\"", startup);
    }

    private static readonly HashSet<string> StateProperties = typeof(GlideSettingsState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();

    [Fact]
    public void Every_property_named_by_the_catalogue_exists_on_the_settings_state()
    {
        foreach (var name in ResetCatalog.HotkeyGestureProperties.Concat(ResetCatalog.ProfileProperties).Concat(ResetCatalog.WindowPlacementProperties)
                     .Concat(ResetCatalog.SessionLocationProperties).Concat(ResetCatalog.StartupSwitchProperties))
            Assert.True(StateProperties.Contains(name), $"GlideSettingsState has no property '{name}' (renamed? update ResetCatalog)");
    }

    [Fact]
    public void Window_placement_list_covers_every_remembered_window_geometry_property()
    {
        // Guard for future windows (print preview, dialogs, ...): any state property that remembers a window's
        // position, size or maximised flag must be listed, or "Window sizes and positions" would leave it behind.
        var geometry = new Regex(@"^(\w*Window)(X|Y|Width|Height|WasMaximized|Placements?|Bounds|State|Left|Top)$|^(\w+)(WindowPlacement|WindowBounds|Placements)$");
        var missing = StateProperties.Where(n => !n.StartsWith("Remember", StringComparison.Ordinal) && geometry.IsMatch(n) && !ResetCatalog.WindowPlacementProperties.Contains(n)).ToArray();
        Assert.True(missing.Length == 0, "Not covered by Reset > Window sizes and positions: " + string.Join(", ", missing));
    }

    [Fact]
    public void Hotkey_and_gesture_list_covers_every_state_property_that_stores_bindings()
    {
        var bindings = StateProperties.Where(n => n.Contains("Hotkey") || n.Contains("Gesture")).ToArray();
        var missing = bindings.Where(n => !ResetCatalog.HotkeyGestureProperties.Contains(n)).ToArray();
        Assert.True(missing.Length == 0, "Binding storage not covered by Reset > Hotkeys and mouse gestures: " + string.Join(", ", missing));
    }

    [Fact]
    public void Startup_switches_match_the_settings_the_Windows_startup_card_controls()
    {
        Assert.Contains(SettingsCatalog.All, x => x.Id == "performance.startWithWindowsInBackground");
        Assert.Contains(SettingsCatalog.All, x => x.Id == "performance.speedBoost");
        Assert.True(typeof(GlideSettingsState).GetProperty("SpeedBoostEnabled")!.PropertyType == typeof(bool));
        Assert.True(typeof(GlideSettingsState).GetProperty("StartWithWindowsInBackground")!.PropertyType == typeof(bool));
    }
}
