using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Data;
using Glide.Core.Reset;
using Glide.Core.Windows;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// Settings &gt; Back up and restore without the window: the .glidebackup container (format, limits, hostile files) and the
/// per-category export / validated import / apply, always on scratch folders (SettingsStore's test directory override and
/// the stores' test seams), never the real user profile.
/// </summary>
[Collection("RecentHistory")]
[Trait("Area", "Data")]
public sealed class GlideBackupTests
{
    /// <summary>One scratch "PC": its own settings folder, recent-files file, placement store and session flag.</summary>
    private sealed class Pc : IDisposable
    {
        private readonly string? _previousDirectory;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "glide-backup-tests", Guid.NewGuid().ToString("N"));
        public string Settings => Path.Combine(Root, "settings");
        public string History => Path.Combine(Settings, "glide.history.json");
        public List<IReadOnlyDictionary<string, WindowPlacementState>> PlacementWrites { get; } = [];

        public Pc(Dictionary<string, WindowPlacementState>? placements = null)
        {
            Directory.CreateDirectory(Settings);
            _previousDirectory = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            Activate(placements);
        }

        /// <summary>Points every store at this PC (two Pc objects in one test switch back and forth).</summary>
        public void Activate(Dictionary<string, WindowPlacementState>? placements = null)
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = Settings;
            RecentHistoryStore.ResetForTests();
            RecentHistoryStore.TestStoragePathOverride = () => History;
            RecentHistoryStore.TestPathExistsOverride = _ => true;
            WindowPlacementService.ResetForTests(placements);
            WindowPlacementService.PersistOverrideForTests = snapshot => PlacementWrites.Add(snapshot);
            WorkspaceSessionStore.ResetImportedSessionForTests();
        }

        public void Dispose()
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = _previousDirectory;
            RecentHistoryStore.ResetForTests();
            RecentHistoryStore.TestStoragePathOverride = null;
            RecentHistoryStore.TestPathExistsOverride = null;
            WindowPlacementService.PersistOverrideForTests = null;
            WindowPlacementService.ResetForTests();
            WorkspaceSessionStore.ResetImportedSessionForTests();
            TempDir.TryDelete(Root);
        }
    }

    private sealed class FakeLive : IGlideBackupLiveTarget
    {
        public GlideSettingsState? Placements { get; private set; }
        public GlideSettingsState? Locations { get; private set; }
        public IReadOnlyList<string> ApplyImportedWindowPlacements(GlideSettingsState imported) { Placements = imported.CloneState(); return []; }
        public void ApplyImportedSessionLocations(GlideSettingsState imported) => Locations = imported.CloneState();
    }

    private static readonly ScreenWorkArea Screen = new(0, 0, 1920, 1040, 1.0);
    private static readonly IReadOnlyList<ScreenWorkArea> Screens = [Screen];

    private static string BackupPath(Pc pc) => Path.Combine(pc.Root, "test" + GlideBackupFormat.FileExtension);

    private static async Task<string> Export(Pc pc, GlideSettingsState state, params ResetItemId[] categories)
    {
        var path = BackupPath(pc);
        await GlideBackupService.ExportAsync(path, state, categories, new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc));
        return path;
    }

    private static GlideSettingsState Current() => new() { SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema };

    // ================================================================ shared category model

    [Fact]
    public void Reset_and_backup_read_one_category_list_in_the_same_order_with_the_same_wording()
    {
        var reset = GlideDataCategoryCatalog.With(GlideDataCapabilities.Reset);
        var backup = GlideDataCategoryCatalog.With(GlideDataCapabilities.Backup);
        Assert.Equal(ResetCatalog.Items.Select(x => x.Id), reset.Select(x => x.Id));
        Assert.Equal(ResetCatalog.Items.Select(x => x.Title), reset.Select(x => x.Title));
        // Backup offers exactly the user data (not caches, registry or startup entries), in Reset's order.
        Assert.Equal(
            [ResetItemId.AllSettings, ResetItemId.HotkeysAndGestures, ResetItemId.ProfilesAndPresets, ResetItemId.WindowPlacements, ResetItemId.RecentHistory, ResetItemId.LastSession],
            backup.Select(x => x.Id).ToArray());
        Assert.Equal(reset.Where(x => x.CanExport).Select(x => x.Title), backup.Select(x => x.Title));
        Assert.All(backup, x => Assert.True(x.CanImport && x.BackupEntryName is not null && x.BackupSettingId is not null));
        Assert.Equal(backup.Count, backup.Select(x => x.BackupEntryName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Reset keeps its approved wording where a shared description would be wrong for a wipe.
        Assert.Equal("Every option back to its default", ResetCatalog.Info(ResetItemId.AllSettings).Description);
        Assert.Equal("Every option in Settings", GlideDataCategoryCatalog.Get(ResetItemId.AllSettings).DescriptionFor(GlideDataCapabilities.Backup));
        foreach (var id in backup.Select(x => x.BackupSettingId!).Concat([GlideDataCategoryCatalog.BackupCardSettingId, GlideDataCategoryCatalog.BackupExportSettingId, GlideDataCategoryCatalog.BackupImportSettingId]))
        {
            var setting = Glide.Core.Settings.SettingsCatalog.All.Single(x => x.Id == id);
            Assert.Equal("Windows & developer", setting.Category);
            Assert.Equal(Glide.Core.Settings.SettingKind.Action, setting.Kind);
            Assert.True(Glide.Core.Settings.SettingActionCatalog.Excluded.ContainsKey(id), id);
        }
        var card = Glide.Core.Settings.SettingsCatalog.All.Single(x => x.Id == GlideDataCategoryCatalog.BackupCardSettingId);
        foreach (var term in new[] { "backup", "back up", "restore", "export", "import", "transfer", "migrate", "move to new pc", "settings file", "profile" })
            Assert.Contains(term, card.SearchTerms);
    }

    [Fact]
    public void All_settings_never_carries_startup_switches_update_memory_or_other_categories()
    {
        var names = typeof(GlideSettingsState).GetProperties().Select(p => p.Name).ToArray();
        var all = GlideDataCategoryCatalog.SettingsPropertiesFor(ResetItemId.AllSettings, names);
        foreach (var never in ResetCatalog.StartupSwitchProperties.Concat(ResetCatalog.UpdateCheckerMemoryProperties)
                     .Concat(ResetCatalog.HotkeyGestureProperties).Concat(ResetCatalog.WindowPlacementProperties)
                     .Concat(ResetCatalog.SessionLocationProperties).Append(ResetCatalog.SettingsSchemaVersionProperty))
            Assert.DoesNotContain(never, all);
        Assert.Contains("ThemeChoice", all);
        Assert.Contains("PerformanceUserCustomSaved", all); // the saved User custom preset travels with the options
    }

    // ================================================================ round trip per category

    [Fact]
    public async Task All_settings_round_trip_stages_options_but_keeps_this_PCs_programs_startup_and_hotkeys()
    {
        using var source = new Pc();
        var state = Current();
        state.ThemeChoice = "Light";
        state.ViewportZoomStepPercent = 25;
        state.ExternalProgram1 = @"C:\Tools\evil.exe";
        state.StartWithWindowsInBackground = true;
        state.SpeedBoostEnabled = true;
        state.Hotkeys = HotkeyCatalog.CreateDefaultMap();
        state.Hotkeys["status.toggle"] = ["F9"];
        state.StartupCustomPath = @"\\fileserver\photos";
        var path = await Export(source, state, ResetItemId.AllSettings);

        using var target = new Pc();
        var current = Current();
        current.ExternalProgram1 = @"C:\Mine\editor.exe";
        current.StartupCustomPath = @"D:\Pictures";
        current.SpeedBoostEnabled = false;
        var plan = GlideBackupService.ReadImport(path, current);
        Assert.Equal([ResetItemId.AllSettings], plan.Categories.ToArray());
        Assert.True(plan.LaunchTargetsKept);
        Assert.True(plan.NetworkFoldersDropped);

        var staged = GlideBackupService.StageSettings(current, plan, [ResetItemId.AllSettings]);
        Assert.Equal("Light", staged.ThemeChoice);
        Assert.Equal(25, staged.ViewportZoomStepPercent);
        Assert.Equal(@"C:\Mine\editor.exe", staged.ExternalProgram1);
        Assert.Equal(@"D:\Pictures", staged.StartupCustomPath);
        Assert.False(staged.StartWithWindowsInBackground);
        Assert.False(staged.SpeedBoostEnabled);
        Assert.False(staged.Hotkeys.TryGetValue("status.toggle", out var keys) && keys.SequenceEqual(["F9"]));
        Assert.True(staged.SettingsSchemaVersion >= current.SettingsSchemaVersion);
        // Nothing on disk changed: "All settings" is only staged.
        Assert.False(File.Exists(Path.Combine(target.Settings, "glide.settings.json")));
    }

    [Fact]
    public async Task Hotkeys_round_trip_and_an_older_schema_migrates_through_the_normal_loader()
    {
        using var source = new Pc();
        var state = Current();
        state.Hotkeys = HotkeyCatalog.CreateDefaultMap();
        state.Hotkeys["folder.next"] = ["F8"];
        state.ThemeChoice = "Light";
        var path = await Export(source, state, ResetItemId.HotkeysAndGestures);

        using var target = new Pc();
        var plan = GlideBackupService.ReadImport(path, Current());
        var staged = GlideBackupService.StageSettings(Current(), plan, [ResetItemId.HotkeysAndGestures]);
        Assert.Contains("F8", staged.Hotkeys["folder.next"]);
        Assert.Equal("Dark", staged.ThemeChoice); // options were not part of this backup

        // A schema-7 entry with the old Alt+Shift+S status-bar key: migrated exactly like an old settings file.
        var old = HotkeyCatalog.CreateDefaultMap();
        old["status.toggle"] = ["Alt+Shift+S"];
        var holder = Current();
        holder.Hotkeys = old;
        var entry = new JsonObject
        {
            ["SettingsSchemaVersion"] = 7,
            ["Hotkeys"] = JsonSerializer.SerializeToNode(holder, AppJsonContextAccess.State)!["Hotkeys"]!.DeepClone(),
        };
        var crafted = Craft(new() { ["hotkeys-gestures.json"] = Encoding.UTF8.GetBytes(entry.ToJsonString()) },
            new() { ["HotkeysAndGestures"] = "hotkeys-gestures.json" });
        var migrated = GlideBackupService.Plan("old", GlideBackupArchive.Read(new MemoryStream(crafted)), Current());
        Assert.Equal(["Ctrl+Shift+B"], migrated.Settings.Hotkeys["status.toggle"]);
        Assert.Equal(SettingsStore.CurrentSettingsSchema, migrated.Settings.SettingsSchemaVersion);
    }

    [Fact]
    public async Task Profiles_round_trip_replaces_the_profiles_atomically_with_launch_and_network_rules()
    {
        using var source = new Pc();
        var travel = Current();
        travel.ThemeChoice = "Light";
        travel.ExternalProgram2 = @"C:\Downloaded\run-me.exe";
        travel.StartupCustomPath = @"\\host\share\pics";
        ProfileStore.Add("Travel", travel);
        var path = await Export(source, Current(), ResetItemId.ProfilesAndPresets);

        using var target = new Pc();
        ProfileStore.Add("Local only", Current());
        var current = Current();
        current.ExternalProgram2 = @"C:\Mine\viewer.exe";
        var plan = GlideBackupService.ReadImport(path, current);
        Assert.True(plan.LaunchTargetsKept);
        Assert.True(plan.NetworkFoldersDropped);
        var (applied, failed, _) = await GlideBackupService.ApplyStoresAsync(plan, [ResetItemId.ProfilesAndPresets], null, Screens, Screen);
        Assert.Equal([ResetItemId.ProfilesAndPresets], applied.ToArray());
        Assert.Empty(failed);
        var profiles = ProfileStore.ListProfiles();
        Assert.DoesNotContain(profiles, p => p.Name == "Local only");
        var imported = Assert.Single(profiles, p => p.Name == "Travel");
        Assert.Equal("Light", imported.Settings.ThemeChoice);
        Assert.Equal(@"C:\Mine\viewer.exe", imported.Settings.ExternalProgram2);
        Assert.Equal(string.Empty, imported.Settings.StartupCustomPath);
        Assert.Empty(Directory.GetFiles(target.Settings, "*.tmp"));
    }

    [Fact]
    public async Task Window_placements_are_checked_against_this_PCs_screens()
    {
        var store = new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase)
        {
            ["print"] = new() { X = 100, Y = 120, Width = 900, Height = 700 },
            ["profile-add"] = new() { X = 7000, Y = 4000 }, // a monitor this PC does not have
            ["titlebar-customizer"] = new() { X = 7000, Y = 4000, Width = 5000, Height = 3000 },
        };
        using var source = new Pc(store);
        var state = Current();
        state.WindowX = 40; state.WindowY = 50; state.WindowWidth = 1200; state.WindowHeight = 800; state.WindowWasMaximized = true;
        var path = await Export(source, state, ResetItemId.WindowPlacements);

        using var target = new Pc(new(StringComparer.OrdinalIgnoreCase) { ["print"] = new() { X = 1, Y = 1, Width = 500, Height = 500 } });
        var plan = GlideBackupService.ReadImport(path, Current());
        var live = new FakeLive();
        var (applied, failed, notes) = await GlideBackupService.ApplyStoresAsync(plan, [ResetItemId.WindowPlacements], live, Screens, Screen);
        Assert.Equal([ResetItemId.WindowPlacements], applied.ToArray());
        Assert.Empty(failed);
        var written = Assert.Single(target.PlacementWrites);
        Assert.Equal(100, written["print"].X);
        Assert.Equal(900, written["print"].Width);
        Assert.False(written.ContainsKey("profile-add")); // position-only and off every screen: nothing left to remember
        Assert.Equal(int.MinValue, written["titlebar-customizer"].X); // off-screen: opens at its default place...
        Assert.True(written["titlebar-customizer"].Width <= 1920); // ...at most as large as this screen
        Assert.Contains(notes, n => n.Contains("did not fit", StringComparison.Ordinal));
        // The main window's own geometry goes to the running main window, which checks it (IsSafeSavedWindowPosition).
        Assert.Equal(1200, live.Placements!.WindowWidth);
        Assert.True(live.Placements.WindowWasMaximized);
    }

    [Fact]
    public async Task Recent_files_round_trip_drops_network_locations_and_replaces_the_live_list()
    {
        using var source = new Pc();
        await RecentHistoryStore.InitializeAsync();
        RecentHistoryStore.RecordFile(@"C:\Pictures\a.jpg", 16);
        RecentHistoryStore.RecordFolder(@"C:\Pictures", 16);
        RecentHistoryStore.RecordFile(@"\\nas\share\b.jpg", 16);
        await RecentHistoryStore.FlushAsync();
        var path = await Export(source, Current(), ResetItemId.RecentHistory);

        using var target = new Pc();
        await RecentHistoryStore.InitializeAsync();
        RecentHistoryStore.RecordFile(@"D:\Old\z.jpg", 16);
        var plan = GlideBackupService.ReadImport(path, Current());
        Assert.Equal(1, plan.NetworkEntriesDropped);
        var (applied, _, _) = await GlideBackupService.ApplyStoresAsync(plan, [ResetItemId.RecentHistory], null, Screens, Screen);
        Assert.Equal([ResetItemId.RecentHistory], applied.ToArray());
        var paths = RecentHistoryStore.Snapshot(32).Select(x => x.Path).ToArray();
        Assert.Equal(2, paths.Length);
        Assert.Contains(@"C:\Pictures\a.jpg", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith(@"D:\Old", StringComparison.Ordinal));
        var onDisk = await File.ReadAllTextAsync(target.History);
        Assert.Contains("a.jpg", onDisk);
        Assert.DoesNotContain("z.jpg", onDisk);
    }

    [Fact]
    public async Task Last_session_round_trip_drops_network_tabs_and_is_not_overwritten_before_the_next_start()
    {
        using var source = new Pc();
        var tabs = new List<TabState>
        {
            new BrowserTabState(Guid.NewGuid(), @"C:\Photos") { Navigation = new BrowserNavigationState([@"C:\", @"C:\Photos"], 1) },
            new ImageTabState(Guid.NewGuid(), @"\\server\share\x.jpg"),
            new HomeTabState(Guid.NewGuid()),
        };
        WorkspaceSessionStore.Write(WorkspaceSessionStore.Snapshot(tabs, 0)!);
        var state = Current();
        state.LastOpenDirectory = @"C:\Photos";
        state.LastOverlayDirectory = @"\\server\overlays";
        var path = await Export(source, state, ResetItemId.LastSession);

        using var target = new Pc();
        var current = Current();
        current.LastOverlayDirectory = @"E:\Overlays";
        var plan = GlideBackupService.ReadImport(path, current);
        Assert.Equal(1, plan.NetworkEntriesDropped);
        Assert.True(plan.NetworkFoldersDropped);
        Assert.Equal(@"C:\Photos", plan.Settings.LastOpenDirectory);
        Assert.Equal(@"E:\Overlays", plan.Settings.LastOverlayDirectory);
        var live = new FakeLive();
        var (applied, _, notes) = await GlideBackupService.ApplyStoresAsync(plan, [ResetItemId.LastSession], live, Screens, Screen);
        Assert.Equal([ResetItemId.LastSession], applied.ToArray());
        Assert.Equal(@"C:\Photos", live.Locations!.LastOpenDirectory);
        Assert.Contains(notes, n => n.Contains("next time Glide starts", StringComparison.Ordinal));
        Assert.True(WorkspaceSessionStore.ImportedSessionPending);

        var restored = WorkspaceSessionStore.Load();
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Value.Tabs.Count);
        Assert.IsType<BrowserTabState>(restored.Value.Tabs[0]);
        // This process's own autosave / close must not replace the imported session.
        var sessionFile = WorkspaceSessionStore.SessionPathIn(target.Settings);
        var before = await File.ReadAllTextAsync(sessionFile);
        WorkspaceSessionStore.Write(WorkspaceSessionStore.Snapshot([new HomeTabState(Guid.NewGuid())], 0)!);
        Assert.Equal(before, await File.ReadAllTextAsync(sessionFile));
    }

    [Fact]
    public async Task Partial_selection_exports_and_imports_only_what_was_chosen()
    {
        using var source = new Pc();
        ProfileStore.Add("Exported", Current());
        await RecentHistoryStore.InitializeAsync();
        RecentHistoryStore.RecordFile(@"C:\Pictures\one.jpg", 16);
        var all = GlideDataCategoryCatalog.With(GlideDataCapabilities.Backup).Select(x => x.Id).ToArray();
        var path = await Export(source, Current(), all);
        using (var zip = ZipFile.OpenRead(path))
            Assert.Equal(all.Length + 1, zip.Entries.Count); // manifest + one entry per category

        var partial = Path.Combine(source.Root, "partial" + GlideBackupFormat.FileExtension);
        await GlideBackupService.ExportAsync(partial, Current(), [ResetItemId.HotkeysAndGestures, ResetItemId.RecentHistory], DateTime.UtcNow);
        using (var zip = ZipFile.OpenRead(partial))
            Assert.Equal(["hotkeys-gestures.json", "manifest.json", "recent-history.json"], zip.Entries.Select(x => x.FullName).Order().ToArray());

        using var target = new Pc();
        ProfileStore.Add("Kept", Current());
        var plan = GlideBackupService.ReadImport(path, Current());
        Assert.Equal(all, plan.Categories.ToArray());
        var (applied, _, _) = await GlideBackupService.ApplyStoresAsync(plan, [ResetItemId.RecentHistory], null, Screens, Screen);
        Assert.Equal([ResetItemId.RecentHistory], applied.ToArray());
        Assert.Contains(ProfileStore.ListProfiles(), p => p.Name == "Kept"); // profiles were in the file but not chosen
        Assert.False(File.Exists(WorkspaceSessionStore.SessionPathIn(target.Settings)));
        Assert.False(WorkspaceSessionStore.ImportedSessionPending);
        var unchanged = GlideBackupService.StageSettings(Current(), plan, []);
        Assert.True(JsonNode.DeepEquals(GlideBackupService.ToNode(Current()), GlideBackupService.ToNode(unchanged)));
    }

    [Fact]
    public async Task Safety_backup_before_import_holds_the_data_that_is_replaced()
    {
        using var pc = new Pc();
        ProfileStore.Add("Before import", Current());
        var folder = Path.Combine(pc.Root, "Glide backups");
        var saved = await GlideBackupService.SaveSafetyBackupAsync(folder, Current(), [ResetItemId.ProfilesAndPresets], new DateTime(2026, 11, 16, 10, 0, 0));
        Assert.StartsWith(folder, saved);
        Assert.EndsWith(".glidebackup", saved);
        var plan = GlideBackupService.ReadImport(saved, Current());
        Assert.Contains(plan.Profiles!.Profiles, p => p.Name == "Before import");
    }

    // ================================================================ schema never goes down

    [Fact]
    public async Task The_settings_schema_never_decreases()
    {
        using var source = new Pc();
        var path = await Export(source, Current(), ResetItemId.AllSettings, ResetItemId.HotkeysAndGestures);
        var newer = Current();
        newer.SettingsSchemaVersion = SettingsStore.CurrentSettingsSchema + 3; // a future value already on this PC
        var plan = GlideBackupService.ReadImport(path, newer);
        Assert.True(plan.Settings.SettingsSchemaVersion >= newer.SettingsSchemaVersion);
        var staged = GlideBackupService.StageSettings(newer, plan, [ResetItemId.AllSettings, ResetItemId.HotkeysAndGestures]);
        Assert.Equal(newer.SettingsSchemaVersion, staged.SettingsSchemaVersion);

        var older = Current();
        older.SettingsSchemaVersion = 3;
        Assert.True(GlideBackupService.StageSettings(older, plan, [ResetItemId.AllSettings]).SettingsSchemaVersion >= 3);
    }

    // ================================================================ hostile backups

    /// <summary>Builds an archive by hand: entries as given, manifest pointing at them with correct lengths and hashes.</summary>
    private static byte[] Craft(Dictionary<string, byte[]> entries, Dictionary<string, string> manifestEntries, Action<JsonObject>? tweak = null, bool includeManifest = true)
    {
        var manifest = new JsonObject
        {
            ["Format"] = "glide-backup",
            ["FormatVersion"] = 1,
            ["AppVersion"] = "test",
            ["CreatedUtc"] = "2026-11-16T09:00:00Z",
            ["SettingsSchemaVersion"] = SettingsStore.CurrentSettingsSchema,
            ["Entries"] = new JsonArray(manifestEntries.Select(pair => (JsonNode)new JsonObject
            {
                ["Category"] = pair.Key,
                ["File"] = pair.Value,
                ["Bytes"] = entries.TryGetValue(pair.Value, out var b) ? b.Length : 0,
                ["Sha256"] = entries.TryGetValue(pair.Value, out var c) ? Convert.ToHexStringLower(SHA256.HashData(c)) : "",
            }).ToArray()),
        };
        tweak?.Invoke(manifest);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeManifest) Write(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            foreach (var (name, bytes) in entries) Write(zip, name, bytes);
        }
        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(bytes);
    }

    private static string Refusal(byte[] archive)
    {
        var ex = Assert.Throws<GlideBackupException>(() => GlideBackupArchive.Read(new MemoryStream(archive)));
        return ex.Message;
    }

    private static string ImportRefusal(byte[] archive)
    {
        using var pc = new Pc();
        var path = Path.Combine(pc.Root, "hostile.glidebackup");
        File.WriteAllBytes(path, archive);
        var before = Directory.GetFiles(pc.Settings, "*", SearchOption.AllDirectories).Length;
        var ex = Assert.Throws<GlideBackupException>(() => GlideBackupService.ReadImport(path, Current()));
        Assert.Equal(before, Directory.GetFiles(pc.Settings, "*", SearchOption.AllDirectories).Length); // nothing written
        return ex.Message;
    }

    private static byte[] Settings(string json) => Encoding.UTF8.GetBytes(json);

    private static readonly Dictionary<string, string> AllSettingsEntry = new() { ["AllSettings"] = "all-settings.json" };

    [Fact]
    public void Oversized_files_are_refused_before_they_are_opened()
    {
        using var pc = new Pc();
        var path = Path.Combine(pc.Root, "huge.glidebackup");
        using (var stream = File.Create(path)) stream.SetLength(GlideBackupFormat.MaxFileBytes + 1);
        Assert.Contains("too large", Assert.Throws<GlideBackupException>(() => GlideBackupArchive.ReadFile(path)).Message);
    }

    [Fact]
    public void Zip_bombs_are_refused_by_what_actually_decompresses()
    {
        // One entry that inflates past the per-entry cap.
        var bomb = new byte[(int)GlideBackupFormat.MaxEntryBytes + 1024];
        Array.Fill(bomb, (byte)' ');
        Assert.Contains("too large", Refusal(Craft(new() { ["all-settings.json"] = bomb }, AllSettingsEntry)));

        // Several entries under the per-entry cap that together inflate past the total cap.
        var big = new byte[(int)GlideBackupFormat.MaxEntryBytes - 1024];
        Array.Fill(big, (byte)' ');
        var names = new[] { "all-settings.json", "hotkeys-gestures.json", "profiles.json", "window-placements.json", "recent-history.json" };
        var categories = new[] { "AllSettings", "HotkeysAndGestures", "ProfilesAndPresets", "WindowPlacements", "RecentHistory" };
        var archive = Craft(names.ToDictionary(n => n, _ => big), categories.Zip(names).ToDictionary(x => x.First, x => x.Second));
        Assert.True(archive.Length < GlideBackupFormat.MaxFileBytes);
        Assert.Contains("too much data", Refusal(archive));
    }

    [Fact]
    public void Path_traversal_and_unexpected_names_are_refused()
    {
        var ok = Settings("{}");
        Assert.Contains("unexpected file names", Refusal(Craft(new() { ["../evil.json"] = ok }, AllSettingsEntry)));
        Assert.Contains("unexpected file names", Refusal(Craft(new() { ["sub/all-settings.json"] = ok }, AllSettingsEntry)));
        Assert.Contains("unexpected file names", Refusal(Craft(new() { [@"C:\Windows\x.json"] = ok }, AllSettingsEntry)));
        // The manifest may not point a category anywhere but its own fixed entry name.
        var redirected = Craft(new() { ["all-settings.json"] = ok, ["other.json"] = ok }, new() { ["AllSettings"] = "other.json" });
        Assert.Contains("unexpected file", Refusal(redirected));
    }

    [Fact]
    public void Duplicate_entries_and_too_many_entries_are_refused()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.json", Settings("{}"));
            Write(zip, "manifest.json", Settings("{}"));
        }
        Assert.Contains("same file twice", Refusal(buffer.ToArray()));

        var many = Enumerable.Range(0, GlideBackupFormat.MaxEntryCount + 5).ToDictionary(i => $"extra{i}.json", _ => Settings("{}"));
        Assert.Contains("too many files", Refusal(Craft(many, AllSettingsEntry)));
    }

    [Fact]
    public void Missing_manifest_bad_manifest_and_non_archives_are_refused()
    {
        Assert.Contains("manifest.json is missing", Refusal(Craft(new() { ["all-settings.json"] = Settings("{}") }, AllSettingsEntry, includeManifest: false)));
        Assert.Contains("not a Glide backup", Refusal(Encoding.UTF8.GetBytes("this is just a text file, not an archive at all")));
        Assert.Contains("not a Glide backup", Refusal(Craft(new() { ["all-settings.json"] = Settings("{}") }, AllSettingsEntry, m => m["Format"] = "something-else")));
        Assert.Contains("newer version of Glide", Refusal(Craft(new() { ["all-settings.json"] = Settings("{}") }, AllSettingsEntry, m => m["FormatVersion"] = 99)));
        Assert.Contains("checksum", Refusal(Craft(new() { ["all-settings.json"] = Settings("{}") }, AllSettingsEntry,
            m => m["Entries"]![0]!["Sha256"] = new string('0', 64))));
        Assert.Contains("missing", Refusal(Craft(new(), AllSettingsEntry)));
        // Only categories this Glide cannot import: nothing to do.
        Assert.Contains("nothing this version", Refusal(Craft(new() { ["future.json"] = Settings("{}") }, new() { ["FutureThing"] = "future.json" })));
        Assert.Contains("nothing this version", Refusal(Craft(new() { ["registry.json"] = Settings("{}") }, new() { ["RegistryEntries"] = "registry.json" })));
    }

    [Fact]
    public void Malformed_json_wrong_types_and_future_schemas_fail_cleanly_without_changing_anything()
    {
        Assert.Contains("not valid JSON", ImportRefusal(Craft(new() { ["all-settings.json"] = Settings("{ not json") }, AllSettingsEntry)));
        Assert.Contains("not valid", ImportRefusal(Craft(new() { ["all-settings.json"] = Settings("[1,2,3]") }, AllSettingsEntry)));
        Assert.Contains("cannot read", ImportRefusal(Craft(new() { ["all-settings.json"] = Settings($"{{\"SettingsSchemaVersion\":{SettingsStore.CurrentSettingsSchema},\"ViewportZoomStepPercent\":\"lots\"}}") }, AllSettingsEntry)));
        Assert.Contains("newer version of Glide", ImportRefusal(Craft(new() { ["all-settings.json"] = Settings($"{{\"SettingsSchemaVersion\":{SettingsStore.CurrentSettingsSchema + 1}}}") }, AllSettingsEntry)));
        Assert.Contains("settings version", ImportRefusal(Craft(new() { ["all-settings.json"] = Settings("{\"SettingsSchemaVersion\":\"eleven\"}") }, AllSettingsEntry)));
        Assert.Contains("newer version of Glide", ImportRefusal(Craft(new() { ["profiles.json"] = Settings("{\"Schema\":3,\"Profiles\":[]}") }, new() { ["ProfilesAndPresets"] = "profiles.json" })));
        Assert.Contains("not valid", ImportRefusal(Craft(new() { ["recent-history.json"] = Settings("{\"Items\":7}") }, new() { ["RecentHistory"] = "recent-history.json" })));
        var tooManyProfiles = new JsonObject { ["Schema"] = 2, ["Profiles"] = new JsonArray(Enumerable.Range(0, GlideBackupService.MaxProfiles + 1).Select(i => (JsonNode)new JsonObject { ["Name"] = $"P{i}", ["Settings"] = new JsonObject() }).ToArray()) };
        Assert.Contains("too many profiles", ImportRefusal(Craft(new() { ["profiles.json"] = Settings(tooManyProfiles.ToJsonString()) }, new() { ["ProfilesAndPresets"] = "profiles.json" })));
    }

    [Fact]
    public void Values_outside_a_category_are_never_imported_from_its_entry()
    {
        // A hand-edited "hotkeys" entry that also tries to switch on Windows startup, set an external program and a theme.
        var entry = $"{{\"SettingsSchemaVersion\":{SettingsStore.CurrentSettingsSchema},\"StartWithWindowsInBackground\":true,\"ExternalProgram1\":\"C:\\\\x.exe\",\"ThemeChoice\":\"Light\"}}";
        var archive = Craft(new() { ["hotkeys-gestures.json"] = Settings(entry) }, new() { ["HotkeysAndGestures"] = "hotkeys-gestures.json" });
        var current = Current();
        var plan = GlideBackupService.Plan("x", GlideBackupArchive.Read(new MemoryStream(archive)), current);
        var staged = GlideBackupService.StageSettings(current, plan, [ResetItemId.HotkeysAndGestures]);
        Assert.False(staged.StartWithWindowsInBackground);
        Assert.Equal(current.ExternalProgram1, staged.ExternalProgram1);
        Assert.Equal(current.ThemeChoice, staged.ThemeChoice);
    }
}

/// <summary>Source-generated JSON metadata the tests need for hand-made entries.</summary>
internal static class AppJsonContextAccess
{
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<GlideSettingsState> State => Glide.App.AppJsonContext.Default.GlideSettingsState;
}
