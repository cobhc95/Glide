using Glide.Core.Reset;

namespace Glide.Core.Tests;

/// <summary>Backup, close-wipe-restart handover and the helper process, all on scratch folders with fake process and registry.</summary>
public sealed class ResetCoordinatorTests
{
    private static void SeedUserData(ResetScratch s)
    {
        s.Write(s.Settings, "glide.settings.json", "{\"SettingsSchemaVersion\":7,\"ThemeChoice\":\"Light\"}");
        s.Write(s.Settings, "glide.profiles.json", "{\"Profiles\":[{\"Name\":\"Work\"}]}");
        s.Write(s.Settings, "glide.workspace-session.json", "{\"Tabs\":[]}");
        s.Write(s.Settings, "glide.history.json", "{\"Items\":[{\"Path\":\"C:\\\\a.jpg\"}]}");
        s.Write(s.Settings, "thumbnail.settings.json", "{\"Quality\":\"High\"}");
    }

    [Fact]
    public void Backup_is_a_complete_timestamped_copy_of_settings_profiles_session_and_history()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var plan = ResetPlanner.Plan(s.Environment, [ResetItemId.AllSettings, ResetItemId.LastSession]);

        var result = ResetBackup.Create(s.Environment, plan.Items.Select(x => x.Item), PhysicalResetFileSystem.Instance, s.Backups, new DateTime(2026, 11, 14, 16, 30, 5), "5.0.0");

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(s.Backups, "Glide backup 2026-11-14 163005"), result.Folder);
        Assert.Equal(
            ["glide.settings.json", "glide.profiles.json", "glide.workspace-session.json", "glide.history.json", "thumbnail.settings.json"],
            result.Files.ToArray());
        foreach (var name in result.Files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(s.Settings, name)), File.ReadAllBytes(Path.Combine(result.Folder!, name)));
        var info = File.ReadAllText(Path.Combine(result.Folder!, "backup-info.txt"));
        Assert.Contains("All settings, Last session", info);
        Assert.Contains("5.0.0", info);
        Assert.Contains(s.Settings, info);
    }

    [Fact]
    public void Backup_never_overwrites_an_earlier_backup_from_the_same_second()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var plan = ResetPlanner.Plan(s.Environment, [ResetItemId.AllSettings]);
        var items = plan.Items.Select(x => x.Item).ToArray();
        var now = new DateTime(2026, 11, 14, 16, 30, 5);
        var first = ResetBackup.Create(s.Environment, items, PhysicalResetFileSystem.Instance, s.Backups, now, "5.0.0");
        var second = ResetBackup.Create(s.Environment, items, PhysicalResetFileSystem.Instance, s.Backups, now, "5.0.0");
        Assert.NotEqual(first.Folder, second.Folder);
        Assert.True(Directory.Exists(first.Folder));
        Assert.True(Directory.Exists(second.Folder));
    }

    private sealed class FailingCopyFileSystem(IResetFileSystem inner) : IResetFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public IReadOnlyList<string> EnumerateFiles(string directory, string pattern) => inner.EnumerateFiles(directory, pattern);
        public IReadOnlyList<string> EnumerateDirectories(string directory, string pattern) => inner.EnumerateDirectories(directory, pattern);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public void WriteAllTextAtomic(string path, string text) => inner.WriteAllTextAtomic(path, text);
        public void CopyFile(string source, string destination) => throw new IOException("disk full (scratch)");
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public long FileLength(string path) => inner.FileLength(path);
    }

    [Fact]
    public void A_failed_backup_stops_the_reset_before_anything_is_changed()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var processes = new FakeProcessHost();
        var services = s.Services(processes: processes, fs: new FailingCopyFileSystem(PhysicalResetFileSystem.Instance));

        var result = ResetCoordinator.Run(new ResetRequest([ResetItemId.AllSettings, ResetItemId.RecentHistory], SaveBackup: true), services);

        Assert.Equal(ResetOutcome.BackupFailed, result.Outcome);
        Assert.Contains("disk full", result.Error);
        Assert.Contains("nothing was reset", result.Error);
        Assert.Empty(processes.Started);
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.settings.json")));
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.history.json")));
    }

    [Fact]
    public void Selection_that_needs_a_restart_starts_the_helper_and_changes_nothing_yet()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var processes = new FakeProcessHost();
        var services = s.Services(processes: processes);

        var result = ResetCoordinator.Run(new ResetRequest([ResetItemId.RecentHistory, ResetItemId.HotkeysAndGestures], SaveBackup: true), services);

        Assert.Equal(ResetOutcome.RestartScheduled, result.Outcome);
        Assert.NotNull(result.BackupFolder);
        Assert.True(File.Exists(Path.Combine(result.BackupFolder!, "glide.history.json")));
        // The wipe itself is the helper's job, after Glide has exited: right now everything is still there.
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.history.json")));
        var started = Assert.Single(processes.Started);
        Assert.Equal("C:\\scratch\\Glide.exe", started.Exe);
        Assert.True(ResetHelperArguments.TryParse(started.Args, out var parsed));
        Assert.Equal([ResetItemId.HotkeysAndGestures, ResetItemId.RecentHistory], parsed!.Items.ToArray());
        Assert.Equal(4242, parsed.WaitProcessId);
        Assert.Equal(s.Settings, parsed.SettingsDirectory);
    }

    [Fact]
    public void Caches_and_registry_only_selections_run_in_place_without_a_restart()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var cache = s.Write(s.Local, @"Startup Diagnostics\startup-timings.jsonl");
        var processes = new FakeProcessHost();
        var registry = new FakeRegistry();
        registry.Set(ResetCatalog.ProgIdKey, "", "x");
        var services = s.Services(registry, processes);

        var result = ResetCoordinator.Run(new ResetRequest([ResetItemId.Caches, ResetItemId.RegistryEntries], SaveBackup: false), services);

        Assert.Equal(ResetOutcome.Completed, result.Outcome);
        Assert.Null(result.BackupFolder);
        Assert.False(Directory.Exists(s.Backups));
        Assert.True(result.Report!.AllSucceeded, result.Report.Describe());
        Assert.False(File.Exists(cache));
        Assert.False(File.Exists(Path.Combine(s.Settings, "thumbnail.settings.json")));
        Assert.False(registry.KeyExists(ResetCatalog.ProgIdKey));
        Assert.Empty(processes.Started);
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.settings.json")));
    }

    [Fact]
    public void If_the_helper_cannot_start_nothing_is_reset_and_the_user_is_told()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var services = s.Services(processes: new FakeProcessHost { StartSucceeds = false });

        var result = ResetCoordinator.Run(new ResetRequest([ResetItemId.AllSettings], SaveBackup: false), services);

        Assert.Equal(ResetOutcome.HelperFailed, result.Outcome);
        Assert.Contains("nothing was reset", result.Error);
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.settings.json")));

        var noExe = ResetCoordinator.Run(new ResetRequest([ResetItemId.AllSettings], false), s.Services(exe: null));
        Assert.Equal(ResetOutcome.HelperFailed, noExe.Outcome);
    }

    [Fact]
    public void Helper_arguments_round_trip_and_reject_anything_that_is_not_an_item_name()
    {
        var args = ResetHelperArguments.Build([ResetItemId.Caches, ResetItemId.StartupEntries], 99, @"C:\a b\settings");
        Assert.True(ResetHelperArguments.TryParse(args, out var parsed));
        Assert.Equal([ResetItemId.Caches, ResetItemId.StartupEntries], parsed!.Items.ToArray());
        Assert.Equal(99, parsed.WaitProcessId);
        Assert.Equal(@"C:\a b\settings", parsed.SettingsDirectory);

        Assert.False(ResetHelperArguments.TryParse(["--reset-glide", "--items", "Caches,DeleteEverything", "--settings-dir", "x"], out _));
        Assert.False(ResetHelperArguments.TryParse(["--reset-glide", "--items", "Caches", "--wait-pid", "-1", "--settings-dir", "x"], out _));
        Assert.False(ResetHelperArguments.TryParse(["--reset-glide", "--items", "Caches"], out _));
        Assert.False(ResetHelperArguments.TryParse(["image.jpg"], out _));
        Assert.False(ResetHelperArguments.IsHelperCommand([]));
    }

    [Fact]
    public void Helper_waits_for_glide_wipes_and_starts_glide_again_without_a_message_when_all_went_well()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var processes = new FakeProcessHost();
        var messages = new FakeMessages();
        var arguments = new ResetHelperArguments([ResetItemId.RecentHistory, ResetItemId.AllSettings], 4242, s.Settings);

        var exit = ResetHelper.Run(arguments, s.Services(processes: processes), messages);

        Assert.Equal(0, exit);
        Assert.Equal([4242], processes.Waited);
        Assert.False(File.Exists(Path.Combine(s.Settings, "glide.history.json")));
        Assert.False(File.Exists(Path.Combine(s.Settings, "glide.settings.json")));
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.profiles.json")));
        var started = Assert.Single(processes.Started);
        Assert.Equal("C:\\scratch\\Glide.exe", started.Exe);
        Assert.Empty(started.Args);
        Assert.Empty(messages.Shown);
    }

    [Fact]
    public void Helper_reports_what_failed_and_still_starts_glide()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var processes = new FakeProcessHost();
        var messages = new FakeMessages();
        using var held = new FileStream(Path.Combine(s.Settings, "glide.history.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var arguments = new ResetHelperArguments([ResetItemId.RecentHistory, ResetItemId.LastSession], 4242, s.Settings);

        var exit = ResetHelper.Run(arguments, s.Services(processes: processes), messages);

        Assert.Equal(1, exit);
        Assert.Single(processes.Started);
        var message = Assert.Single(messages.Shown);
        Assert.Contains("Recent files and Home list: not completed", message.Message);
        Assert.Contains("Last session: 1 removed or changed", message.Message);
        Assert.Contains("Glide is starting now", message.Message);
        Assert.False(File.Exists(Path.Combine(s.Settings, "glide.workspace-session.json")));
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.history.json")));
    }

    [Fact]
    public void Helper_does_nothing_when_glide_does_not_close_in_time()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var processes = new FakeProcessHost { GlideExits = false };
        var messages = new FakeMessages();

        var exit = ResetHelper.Run(new ResetHelperArguments([ResetItemId.AllSettings], 4242, s.Settings), s.Services(processes: processes), messages);

        Assert.Equal(2, exit);
        Assert.True(File.Exists(Path.Combine(s.Settings, "glide.settings.json")));
        Assert.Empty(processes.Started);
        Assert.Contains("nothing was reset", Assert.Single(messages.Shown).Message);
    }

    [Fact]
    public void Helper_tells_the_user_when_glide_cannot_be_started_again()
    {
        using var s = new ResetScratch();
        SeedUserData(s);
        var messages = new FakeMessages();

        var exit = ResetHelper.Run(new ResetHelperArguments([ResetItemId.RecentHistory], 4242, s.Settings),
            s.Services(processes: new FakeProcessHost { StartSucceeds = false }), messages);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(s.Settings, "glide.history.json")));
        Assert.Contains("could not be started again", Assert.Single(messages.Shown).Message);
    }
}
