using System.Text.Json.Nodes;
using Glide.App.Platform;
using Glide.Core.Reset;
using Microsoft.Win32;

namespace Glide.Core.Tests;

/// <summary>Reset Glide executed against scratch folders and a fake / scratch-hive registry. The real user data is never touched.</summary>
public sealed class ResetExecutorTests
{
    private static ResetReport Run(ResetScratch s, ResetItemId[] items, FakeRegistry? registry = null, IResetFileSystem? fs = null)
    {
        var services = s.Services(registry, fs: fs);
        return ResetExecutor.Execute(ResetPlanner.Plan(s.Environment, items), services.FileSystem, services.Registry, services.ExecutorOptions);
    }

    private const string SettingsJson = """
        {
          "SettingsSchemaVersion": 7,
          "ThemeChoice": "Light",
          "HistorySize": 12,
          "RapidPreviewLongestSide": 2400.0,
          "Hotkeys": { "view.fit": ["F"] },
          "CustomHotkeyPresets": [ { "Name": "mine" } ],
          "Gestures": { "a": "b" },
          "CustomGestures": [ { "Area": "viewer" } ],
          "GestureRecentActions": ["x"],
          "WindowX": 100, "WindowY": 50, "WindowWidth": 1200.5, "WindowHeight": 800, "WindowWasMaximized": true,
          "SettingsWindowX": 5, "SettingsWindowY": 6, "SettingsWindowWidth": 900, "SettingsWindowHeight": 600, "SettingsWindowWasMaximized": false,
          "OverlayWindowPlacements": { "c:\\a.jpg": { "X": 1 } },
          "WindowPlacements": { "print": { "X": 10, "Y": 20, "Width": 1000, "Height": 700 }, "confirm": { "X": 3, "Y": 4 } },
          "LastOpenDirectory": "C:\\Pictures", "LastProfileDirectory": "C:\\Profiles",
          "SpeedBoostEnabled": true, "StartWithWindowsInBackground": true
        }
        """;

    [Fact]
    public void All_settings_resets_the_options_deletes_the_small_settings_files_and_leaves_everything_else_alone()
    {
        using var s = new ResetScratch();
        var settings = s.Write(s.Settings, "glide.settings.json", SettingsJson);
        var policy = s.Write(s.Settings, "glide.launch.policy.json");
        var print = s.Write(s.Settings, "print.settings.json");
        var leftover = s.Write(s.Settings, "glide.settings.json.1234.abcd.tmp");
        var history = s.Write(s.Settings, "glide.history.json");
        var profiles = s.Write(s.Settings, "glide.profiles.json");
        var session = s.Write(s.Settings, "glide.workspace-session.json");
        var exe = s.Write(s.Install, "Glide.exe");
        var other = s.Write(s.Settings, "someone-elses-file.txt");

        var report = Run(s, [ResetItemId.AllSettings]);

        Assert.True(report.AllSucceeded, report.Describe());
        // Options only: without app defaults (this scratch environment has none) the option keys are removed so they load
        // as defaults; everything that is not an option stays exactly as it was.
        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        foreach (var option in new[] { "ThemeChoice", "HistorySize", "RapidPreviewLongestSide" }) Assert.False(root.ContainsKey(option), option);
        var original = JsonNode.Parse(SettingsJson)!.AsObject();
        foreach (var kept in original.Select(x => x.Key).Where(AllSettingsReset.IsKept))
            Assert.True(JsonNode.DeepEquals(original[kept], root[kept]), kept);
        Assert.Equal(original.Count - 3, root.Count);
        Assert.False(File.Exists(policy));
        Assert.False(File.Exists(print));
        Assert.False(File.Exists(leftover));
        foreach (var kept in new[] { history, profiles, session, exe, other }) Assert.True(File.Exists(kept), kept);
    }

    [Fact]
    public void Hotkeys_item_removes_only_binding_properties_and_keeps_every_other_value_exactly()
    {
        using var s = new ResetScratch();
        var file = s.Write(s.Settings, "glide.settings.json", SettingsJson);

        var report = Run(s, [ResetItemId.HotkeysAndGestures]);

        Assert.True(report.AllSucceeded, report.Describe());
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        foreach (var gone in ResetCatalog.HotkeyGestureProperties) Assert.False(root.ContainsKey(gone), gone);
        Assert.Equal("Light", (string?)root["ThemeChoice"]);
        Assert.Equal(12, (int?)root["HistorySize"]);
        Assert.Equal(7, (int?)root["SettingsSchemaVersion"]);
        Assert.Contains("2400.0", File.ReadAllText(file)); // numbers keep their spelling
        Assert.True(root.ContainsKey("WindowX"));
    }

    [Fact]
    public void Window_item_forgets_main_settings_and_overlay_placements_only()
    {
        using var s = new ResetScratch();
        var file = s.Write(s.Settings, "glide.settings.json", SettingsJson);

        var report = Run(s, [ResetItemId.WindowPlacements]);

        Assert.True(report.AllSucceeded, report.Describe());
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        foreach (var gone in new[] { "WindowX", "WindowY", "WindowWidth", "WindowHeight", "WindowWasMaximized", "SettingsWindowX", "SettingsWindowY",
                     "SettingsWindowWidth", "SettingsWindowHeight", "SettingsWindowWasMaximized", "OverlayWindowPlacements", "WindowPlacements" })
            Assert.False(root.ContainsKey(gone), gone);
        Assert.True(root.ContainsKey("Hotkeys"));
        Assert.Equal("Light", (string?)root["ThemeChoice"]);
    }

    [Fact]
    public void Profiles_history_session_and_cache_items_remove_their_data_including_old_video_decoder_leftovers()
    {
        using var s = new ResetScratch();
        var file = s.Write(s.Settings, "glide.settings.json", SettingsJson);
        var profiles = s.Write(s.Settings, "glide.profiles.json");
        var history = s.Write(s.Settings, "glide.history.json");
        var session = s.Write(s.Settings, "glide.workspace-session.json");
        var layout = s.Write(s.Settings, "overlay-layout.json");
        var layoutBak = s.Write(s.Settings, "overlay-layout.json.bak");
        var pack = s.Write(s.Local, @"ffmpeg-7.1\avcodec-61.dll");
        var partial = s.Write(s.Local, @"ffmpeg-download-abc\pack.zip");
        var unrelated = s.Write(s.Local, @"crash.log");

        var report = Run(s, [ResetItemId.ProfilesAndPresets, ResetItemId.RecentHistory, ResetItemId.LastSession, ResetItemId.Caches]);

        Assert.True(report.AllSucceeded, report.Describe());
        foreach (var gone in new[] { profiles, history, session, layout, layoutBak, pack, partial }) Assert.False(File.Exists(gone), gone);
        Assert.False(Directory.Exists(Path.Combine(s.Local, "ffmpeg-7.1")));
        Assert.True(File.Exists(unrelated));
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.False(root.ContainsKey("LastOpenDirectory"));
        Assert.False(root.ContainsKey("LastProfileDirectory"));
        Assert.True(root.ContainsKey("Hotkeys"));
    }

    [Fact]
    public void Caches_item_removes_the_rebuildable_files_but_not_crash_logs()
    {
        using var s = new ResetScratch();
        var thumbs = s.Write(s.Settings, "thumbnail.settings.json");
        var policy = s.Write(s.Settings, "glide.launch.policy.json");
        var timing = s.Write(s.Local, @"Startup Diagnostics\startup-timings.jsonl");
        var transfer = s.Write(s.Temp, "glide-diagnostic-transfer.jpg");
        var fixtures = s.Write(s.Temp, @"glide_diagnostic_fixtures\a.png");
        var bench = s.Write(s.Temp, "glide_bench_0001.tsv");
        var foreign = s.Write(s.Temp, "someone-elses.tsv");
        var crash = s.Write(s.Local, "crash.log");
        var settings = s.Write(s.Settings, "glide.settings.json", SettingsJson);

        var report = Run(s, [ResetItemId.Caches]);

        Assert.True(report.AllSucceeded, report.Describe());
        foreach (var gone in new[] { thumbs, policy, timing, transfer, fixtures, bench }) Assert.False(File.Exists(gone), gone);
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(crash));
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public void Startup_item_clears_the_run_value_and_switches_the_settings_off()
    {
        using var s = new ResetScratch();
        var file = s.Write(s.Settings, "glide.settings.json", SettingsJson);
        var registry = new FakeRegistry();
        registry.Set(ResetCatalog.RunKey, "Glide", "\"C:\\Glide\\Glide.exe\" --background");
        registry.Set(ResetCatalog.RunKey, "SomeOtherApp", "other.exe");

        var report = Run(s, [ResetItemId.StartupEntries], registry);

        Assert.True(report.AllSucceeded, report.Describe());
        Assert.Null(registry.Get(ResetCatalog.RunKey, "Glide"));
        Assert.Equal("other.exe", registry.Get(ResetCatalog.RunKey, "SomeOtherApp"));
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.False((bool)root["SpeedBoostEnabled"]!);
        Assert.False((bool)root["StartWithWindowsInBackground"]!);
        Assert.Equal("Light", (string?)root["ThemeChoice"]);
    }

    [Fact]
    public void Startup_item_after_all_settings_switches_both_off_and_keeps_the_schema()
    {
        using var s = new ResetScratch();
        s.Write(s.Settings, "glide.settings.json", SettingsJson);

        var report = Run(s, [ResetItemId.StartupEntries, ResetItemId.AllSettings]);

        Assert.True(report.AllSucceeded, report.Describe());
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(s.Settings, "glide.settings.json")))!.AsObject();
        Assert.False(root.ContainsKey("ThemeChoice"));
        Assert.Equal(7, (int?)root["SettingsSchemaVersion"]);
        Assert.False((bool)root["SpeedBoostEnabled"]!);
        Assert.False((bool)root["StartWithWindowsInBackground"]!);
    }

    [Fact]
    public void Missing_files_are_reported_as_nothing_to_remove_not_as_failures()
    {
        using var s = new ResetScratch();
        var report = Run(s, ResetCatalog.Items.Select(x => x.Id).Where(x => x != ResetItemId.StartupEntries).ToArray());
        Assert.True(report.AllSucceeded, report.Describe());
        Assert.All(report.Items.Where(x => x.Item.Id != ResetItemId.RegistryEntries), item => Assert.Equal(0, item.ChangedCount));
        // The opt-out is the one registry write that always happens.
        Assert.Equal(1, report.Items.Single(x => x.Item.Id == ResetItemId.RegistryEntries).ChangedCount);
    }

    [Fact]
    public void Corrupt_settings_file_fails_that_item_with_advice_and_is_not_touched()
    {
        using var s = new ResetScratch();
        var file = s.Write(s.Settings, "glide.settings.json", "{ this is not json");

        var report = Run(s, [ResetItemId.HotkeysAndGestures, ResetItemId.RecentHistory]);

        Assert.False(report.AllSucceeded);
        var failed = Assert.Single(report.FailedItems);
        Assert.Equal(ResetItemId.HotkeysAndGestures, failed.Item.Id);
        Assert.Contains("All settings", failed.Failures[0].Detail);
        Assert.Equal("{ this is not json", File.ReadAllText(file));
    }

    // ---- registry ----

    private static void SeedGlideRegistry(FakeRegistry r, ResetScratch s)
    {
        r.Set(ResetCatalog.ApplicationKey + @"\shell\open\command", "", "\"C:\\Glide.exe\" \"%1\"");
        r.Set(ResetCatalog.ApplicationKey + @"\SupportedTypes", ".jpg", "");
        r.Set(ResetCatalog.ProgIdKey + @"\DefaultIcon", "", "C:\\Glide.exe,0");
        r.Set(ResetCatalog.CapabilitiesKey + @"\FileAssociations", ".jpg", "Glide.Image");
        r.Set(ResetCatalog.ThumbnailConfigKey, "Quality", 2);
        r.Set(@"Software\Classes\CLSID\" + ResetCatalog.ThumbnailProviderClsid + @"\InprocServer32", "", "C:\\Glide.ShellThumbnail.dll");
        r.Set(ResetCatalog.RegisteredApplicationsKey, "Glide", ResetCatalog.CapabilitiesKey);
        r.Set(ResetCatalog.RegisteredApplicationsKey, "OtherApp", @"Software\Other\Capabilities");
        r.Set(ResetCatalog.StateKey, ResetCatalog.OpenWithVersionValue, "5.0.0.0");
        foreach (var extension in s.Extensions)
        {
            r.Set($@"Software\Classes\{extension}\OpenWithProgids", "Glide.Image", "");
            r.Set($@"Software\Classes\{extension}\OpenWithProgids", "Other.App", "");
            r.Set($@"Software\Classes\{extension}\ShellEx\{ResetCatalog.ThumbnailHandlerGuid}", "", ResetCatalog.ThumbnailProviderClsid);
        }
    }

    [Fact]
    public void Registry_item_removes_glides_entries_keeps_other_apps_and_sets_the_opt_out()
    {
        using var s = new ResetScratch();
        var registry = new FakeRegistry();
        SeedGlideRegistry(registry, s);
        // A thumbnail handler that another program took over must be left alone.
        registry.Set($@"Software\Classes\.png\ShellEx\{ResetCatalog.ThumbnailHandlerGuid}", "", "{SOME-OTHER-HANDLER}");

        var report = Run(s, [ResetItemId.RegistryEntries], registry);

        Assert.True(report.AllSucceeded, report.Describe());
        Assert.False(registry.KeyExists(ResetCatalog.ApplicationKey));
        Assert.False(registry.KeyExists(ResetCatalog.ProgIdKey));
        Assert.False(registry.KeyExists(ResetCatalog.CapabilitiesKey));
        Assert.False(registry.KeyExists(ResetCatalog.ThumbnailConfigKey));
        Assert.False(registry.KeyExists(@"Software\Classes\CLSID\" + ResetCatalog.ThumbnailProviderClsid));
        Assert.Null(registry.Get(ResetCatalog.RegisteredApplicationsKey, "Glide"));
        Assert.Equal(@"Software\Other\Capabilities", registry.Get(ResetCatalog.RegisteredApplicationsKey, "OtherApp"));
        foreach (var extension in s.Extensions)
        {
            Assert.Null(registry.Get($@"Software\Classes\{extension}\OpenWithProgids", "Glide.Image"));
            Assert.NotNull(registry.Get($@"Software\Classes\{extension}\OpenWithProgids", "Other.App"));
        }
        Assert.Null(registry.Get($@"Software\Classes\.jpg\ShellEx\{ResetCatalog.ThumbnailHandlerGuid}", ""));
        Assert.Equal("{SOME-OTHER-HANDLER}", registry.Get($@"Software\Classes\.png\ShellEx\{ResetCatalog.ThumbnailHandlerGuid}", ""));
        Assert.Null(registry.Get(ResetCatalog.StateKey, ResetCatalog.OpenWithVersionValue));
        Assert.Equal(1, registry.Get(ResetCatalog.StateKey, ResetCatalog.OpenWithOptOutValue));
    }

    [Fact]
    public void Registry_interface_cannot_name_any_hive_but_current_user()
    {
        // The only way to reach the registry is IResetRegistry; its members take HKCU-relative paths and no hive.
        var members = typeof(IResetRegistry).GetMethods();
        Assert.All(members, m => Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType.Name.Contains("RegistryKey") || p.Name!.Contains("hive", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Real_registry_code_runs_against_a_scratch_hive_and_leaves_the_rest_alone()
    {
        if (!OperatingSystem.IsWindows()) return;
        var scratch = @"Software\GlideResetTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var s = new ResetScratch();
            var registry = new WindowsResetRegistry(Registry.CurrentUser, scratch);
            using (var key = Registry.CurrentUser.CreateSubKey($@"{scratch}\{ResetCatalog.ProgIdKey}\shell\open\command")) key.SetValue("", "cmd");
            using (var key = Registry.CurrentUser.CreateSubKey($@"{scratch}\Software\Classes\.jpg\OpenWithProgids"))
            {
                key.SetValue("Glide.Image", Array.Empty<byte>(), RegistryValueKind.None);
                key.SetValue("Other.App", Array.Empty<byte>(), RegistryValueKind.None);
            }
            using (var key = Registry.CurrentUser.CreateSubKey($@"{scratch}\{ResetCatalog.RunKey}"))
            {
                key.SetValue("Glide", "\"C:\\Glide.exe\" --background");
                key.SetValue("Keep", "keep.exe");
            }

            var services = s.Services();
            var plan = ResetPlanner.Plan(s.Environment, [ResetItemId.RegistryEntries, ResetItemId.StartupEntries]);
            var report = ResetExecutor.Execute(plan, services.FileSystem, registry, services.ExecutorOptions);

            Assert.True(report.AllSucceeded, report.Describe());
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{scratch}\{ResetCatalog.ProgIdKey}"));
            using (var openWith = Registry.CurrentUser.OpenSubKey($@"{scratch}\Software\Classes\.jpg\OpenWithProgids"))
            {
                Assert.NotNull(openWith);
                Assert.DoesNotContain("Glide.Image", openWith!.GetValueNames());
                Assert.Contains("Other.App", openWith.GetValueNames());
            }
            using (var run = Registry.CurrentUser.OpenSubKey($@"{scratch}\{ResetCatalog.RunKey}"))
            {
                Assert.Null(run!.GetValue("Glide"));
                Assert.Equal("keep.exe", run.GetValue("Keep"));
            }
            using (var state = Registry.CurrentUser.OpenSubKey($@"{scratch}\{ResetCatalog.StateKey}"))
                Assert.Equal(1, state!.GetValue(ResetCatalog.OpenWithOptOutValue));
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlideResetTests", false); } catch { }
        }
    }

    // ---- failures and safety ----

    [Fact]
    public void A_file_in_use_fails_only_its_item_reports_the_reason_and_the_rest_still_runs()
    {
        using var s = new ResetScratch();
        var history = s.Write(s.Settings, "glide.history.json");
        var session = s.Write(s.Settings, "glide.workspace-session.json");
        var profiles = s.Write(s.Settings, "glide.profiles.json");

        // A real sharing violation: another handle holds the file without FileShare.Delete.
        using var held = new FileStream(session, FileMode.Open, FileAccess.Read, FileShare.Read);

        var report = Run(s, [ResetItemId.ProfilesAndPresets, ResetItemId.RecentHistory, ResetItemId.LastSession]);

        Assert.False(report.AllSucceeded);
        var failed = Assert.Single(report.FailedItems);
        Assert.Equal(ResetItemId.LastSession, failed.Item.Id);
        var failure = Assert.Single(failed.Failures);
        Assert.Equal(ResetTargetOutcome.Failed, failure.Outcome);
        Assert.Contains("in use", failure.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(session));
        Assert.False(File.Exists(history));
        Assert.False(File.Exists(profiles));
        Assert.Contains("Last session: not completed", report.Describe());
        Assert.Contains("glide.workspace-session.json", report.Describe());
        Assert.Contains("Recent files and Home list: 1 removed or changed", report.Describe());
    }

    [Fact]
    public void A_briefly_locked_file_is_retried_until_it_can_be_deleted()
    {
        using var s = new ResetScratch();
        var history = s.Write(s.Settings, "glide.history.json");
        var flaky = new FlakyFileSystem(PhysicalResetFileSystem.Instance, p => p.EndsWith("glide.history.json"), failTimes: 2);

        var report = Run(s, [ResetItemId.RecentHistory], fs: flaky);

        Assert.True(report.AllSucceeded, report.Describe());
        Assert.False(File.Exists(history));
        Assert.Equal(3, flaky.DeleteAttempts);
    }

    [Fact]
    public void A_file_that_stays_locked_gives_up_after_the_configured_attempts()
    {
        using var s = new ResetScratch();
        var history = s.Write(s.Settings, "glide.history.json");
        var flaky = new FlakyFileSystem(PhysicalResetFileSystem.Instance, _ => true, failTimes: 99);

        var report = Run(s, [ResetItemId.RecentHistory], fs: flaky);

        Assert.False(report.AllSucceeded);
        Assert.Equal(3, flaky.DeleteAttempts);
        Assert.True(File.Exists(history));
    }

    [Fact]
    public void Targets_outside_glides_folders_are_refused_and_never_deleted()
    {
        using var s = new ResetScratch();
        var photo = s.Write(s.Root, @"pictures\photo.jpg");
        var exe = s.Write(s.Install, "Glide.exe");
        var dll = s.Write(s.Settings, "Glide.dll");
        var info = ResetCatalog.Info(ResetItemId.RecentHistory);
        var plan = new ResetPlan(s.Environment, [new ResetItemPlan(info,
        [
            new FileTarget(photo), new FileTarget(exe), new FileTarget(dll),
            new DirectoryTarget(Path.GetDirectoryName(photo)!), new DirectoryTarget(s.Install), new DirectoryTarget(s.Root),
        ])]);

        var report = ResetExecutor.Execute(plan, PhysicalResetFileSystem.Instance, new FakeRegistry());

        Assert.False(report.AllSucceeded);
        Assert.All(report.Items[0].Targets, t => Assert.Equal(ResetTargetOutcome.Refused, t.Outcome));
        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(dll));
        Assert.True(Directory.Exists(s.Install));
    }

    [Fact]
    public void Wildcard_matches_are_checked_one_by_one()
    {
        using var s = new ResetScratch();
        // A wildcard folder match that happens to contain the program must not be deleted.
        var installInsideLocal = Path.Combine(s.Local, "ffmpeg-portable-install");
        Directory.CreateDirectory(installInsideLocal);
        s.Write(installInsideLocal, "Glide.exe");
        var env = s.Environment with { InstallDirectory = installInsideLocal };
        var plan = ResetPlanner.Plan(env, [ResetItemId.Caches]);

        var report = ResetExecutor.Execute(plan, PhysicalResetFileSystem.Instance, new FakeRegistry());

        Assert.False(report.AllSucceeded);
        Assert.True(File.Exists(Path.Combine(installInsideLocal, "Glide.exe")));
    }
}
