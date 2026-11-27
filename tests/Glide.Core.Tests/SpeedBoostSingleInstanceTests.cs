using System.Reflection;
using Glide.App;
using Glide.App.Services;
using Glide.Core.Settings;
using GlideApp = Glide.App.App;

namespace Glide.Core.Tests;

/// <summary>
/// GitHub issue #5: a Glide launched by another program while Glide was running started its own Speed Boost process,
/// which stayed resident (with its own tray icon) after Glide closed, and the tray icon ignored "Never".
/// </summary>
[Trait("Area", "Window")]
[Collection("ExternalLaunchBroker")]
public sealed class SpeedBoostSingleInstanceTests
{
    private const string User = "S-1-5-21-1-2-3-1001";

    private static string Source(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(segments).ToArray())).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void Installed_copy_keeps_the_plain_user_family_and_everything_else_is_isolated()
    {
        // Installed: wire compatible with an already running installed Glide.
        Assert.Equal(User, ProtocolNames.ScopedInstanceKey(User, null, @"C:\Program Files\Glide\", installedCopy: true, foreignHost: false, processId: 1));
        // GLIDE_SETTINGS_DIRECTORY wins and keeps its 5.0.8 key.
        Assert.Equal(ProtocolNames.InstanceKey(User, @"C:\p"), ProtocolNames.ScopedInstanceKey(User, @"C:\p", @"C:\Program Files\Glide\", true, true, 1));
        // A test host: one family per process.
        var hostA = ProtocolNames.ScopedInstanceKey(User, null, @"C:\Program Files\Glide\", true, foreignHost: true, processId: 10);
        var hostB = ProtocolNames.ScopedInstanceKey(User, null, @"C:\Program Files\Glide\", true, foreignHost: true, processId: 11);
        Assert.NotEqual(User, hostA);
        Assert.NotEqual(hostA, hostB);
        // A portable copy / dev build: one family per folder, never the installed one.
        var dev = ProtocolNames.ScopedInstanceKey(User, null, @"C:\src\Glide\bin\Debug\", false, false, 1);
        Assert.NotEqual(User, dev);
        Assert.Equal(dev, ProtocolNames.ScopedInstanceKey(User, null, @"c:\SRC\glide\bin\debug", false, false, 2));
        Assert.NotEqual(dev, ProtocolNames.ScopedInstanceKey(User, null, @"C:\Portable\Glide\", false, false, 1));
    }

    [Fact]
    public void A_test_host_never_uses_the_users_real_pipe_or_presence_names()
    {
        Assert.True(ExternalLaunchBroker.IsForeignHost());
        var user = ExternalLaunchBroker.CurrentUserKey();
        Assert.NotEqual(ProtocolNames.UserScoped(ProtocolNames.PipeName, user), ExternalLaunchBroker.PipeName);
        Assert.StartsWith(ProtocolNames.PipeName + ".", ExternalLaunchBroker.PipeName, StringComparison.Ordinal);
        var presence = (string)typeof(ExternalLaunchBroker).GetProperty("EffectivePresenceName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.False(presence.StartsWith(ProtocolNames.UserScoped(ProtocolNames.PresenceName, user), StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_single_instance_owner_may_stay_resident_in_speed_boost()
    {
        Assert.True(MainWindow.SpeedBoostStandbyOwnerPolicy(ownsLaunchBroker: true, openWindows: 1));
        Assert.False(MainWindow.SpeedBoostStandbyOwnerPolicy(ownsLaunchBroker: false, openWindows: 1));
        Assert.False(MainWindow.SpeedBoostStandbyOwnerPolicy(ownsLaunchBroker: true, openWindows: 2));

        var sessions = Source("src", "Glide.App", "MainWindow.Sessions.cs");
        var gate = sessions[sessions.IndexOf("private bool ShouldEnterSpeedBoostStandby()", StringComparison.Ordinal)..];
        gate = gate[..gate.IndexOf(';')];
        Assert.Contains("SpeedBoostStandbyOwnerPolicy(ExternalLaunchBroker.IsPresenceOwner", gate, StringComparison.Ordinal);
        // The --background start enters standby only as the owner.
        var app = Source("src", "Glide.App", "App.axaml.cs");
        Assert.Contains("if (ExternalLaunchBroker.IsPresenceOwner)\n                        window.EnterSpeedBoostStandby(persistSession: false);", app, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_that_lost_the_election_is_not_the_owner_until_it_takes_over()
    {
        if (!OperatingSystem.IsWindows()) return;
        ExternalLaunchBroker.Stop();
        var presence = (string)typeof(ExternalLaunchBroker).GetProperty("EffectivePresenceName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        try
        {
            using (var runningGlide = new EventWaitHandle(false, EventResetMode.ManualReset, presence, out var created))
            {
                Assert.True(created);
                Assert.False(ExternalLaunchBroker.ClaimProcessPresence());
                Assert.False(ExternalLaunchBroker.IsPresenceOwner);
                Assert.False(MainWindow.SpeedBoostStandbyOwnerPolicy(ExternalLaunchBroker.IsPresenceOwner, 1));
                Assert.False(GlideApp.ShouldShowTrayIconPolicy(new GlideSettingsState(), ExternalLaunchBroker.IsPresenceOwner, () => false));
            }
            ExternalLaunchBroker.Stop();
            Assert.True(ExternalLaunchBroker.ClaimProcessPresence());
            Assert.True(ExternalLaunchBroker.IsPresenceOwner);
            Assert.True(MainWindow.SpeedBoostStandbyOwnerPolicy(ExternalLaunchBroker.IsPresenceOwner, 1));
        }
        finally { ExternalLaunchBroker.Stop(); }
        Assert.False(ExternalLaunchBroker.IsPresenceOwner);
    }

    [Fact]
    public void Tray_icon_respects_never_and_shows_only_in_the_owner()
    {
        var always = new GlideSettingsState();
        var never = new GlideSettingsState { SpeedBoostTrayIcon = "Never" };
        var legacyOff = new GlideSettingsState { ShowTrayIcon = false };
        var whileClosed = new GlideSettingsState { SpeedBoostTrayIcon = "Only while Glide is closed" };
        var off = new GlideSettingsState { SpeedBoostEnabled = false };

        Assert.True(GlideApp.ShouldShowTrayIconPolicy(always, true, () => true));
        Assert.False(GlideApp.ShouldShowTrayIconPolicy(always, false, () => false));
        Assert.False(GlideApp.ShouldShowTrayIconPolicy(never, true, () => false));
        Assert.False(GlideApp.ShouldShowTrayIconPolicy(never, true, () => true));
        Assert.False(GlideApp.ShouldShowTrayIconPolicy(legacyOff, true, () => false));
        Assert.False(GlideApp.ShouldShowTrayIconPolicy(off, true, () => false));
        Assert.True(GlideApp.ShouldShowTrayIconPolicy(whileClosed, true, () => false));
        Assert.False(GlideApp.ShouldShowTrayIconPolicy(whileClosed, true, () => true));
    }

    [Fact]
    public void Tray_icon_starts_hidden_until_settings_are_known_and_follows_every_publish_and_other_processes()
    {
        var app = Source("src", "Glide.App", "App.axaml.cs");
        Assert.DoesNotContain("TryGetStartupSettings() ?? new GlideSettingsState()", app, StringComparison.Ordinal);
        Assert.Contains("IsVisible = settings is not null && ShouldShowTrayIcon(settings)", app, StringComparison.Ordinal);
        Assert.Contains("SettingsService.Changed += (_, e) => UpdateTrayIconState(e.Current)", app, StringComparison.Ordinal);
        // Another process' saved change reaches a resident process; a half-written file never makes the defaults live.
        Assert.Contains("StartSettingsFileWatcher();", app, StringComparison.Ordinal);
        var reload = app[app.IndexOf("private static void ReloadSettingsFromDisk()", StringComparison.Ordinal)..];
        Assert.DoesNotContain("SettingsStore.Load()", reload, StringComparison.Ordinal);
        Assert.Contains("MigrateFromJson", reload, StringComparison.Ordinal);
        // A woken standby window re-evaluates the icon from the live settings, not its own stale copy.
        var sessions = Source("src", "Glide.App", "MainWindow.Sessions.cs");
        var exitStandby = sessions[sessions.IndexOf("internal void ExitStandby()", StringComparison.Ordinal)..];
        exitStandby = exitStandby[..exitStandby.IndexOf("internal async Task<ExternalLaunchItemResult> HandleExternalActivateAsync", StringComparison.Ordinal)];
        Assert.Contains("App.UpdateTrayIconState();", exitStandby, StringComparison.Ordinal);
        Assert.DoesNotContain("App.UpdateTrayIconState(_settings);", exitStandby, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_exits_when_its_last_window_closes_without_standby()
    {
        Assert.True(GlideApp.ShouldExitWithoutWindows(false, false, 0, 0, false));
        Assert.False(GlideApp.ShouldExitWithoutWindows(true, false, 0, 0, false));   // already exiting
        Assert.False(GlideApp.ShouldExitWithoutWindows(false, true, 0, 0, false));   // owner resident in standby
        Assert.False(GlideApp.ShouldExitWithoutWindows(false, false, 1, 1, false));  // another Glide window
        Assert.False(GlideApp.ShouldExitWithoutWindows(false, false, 0, 1, false));  // another top-level window
        Assert.False(GlideApp.ShouldExitWithoutWindows(false, false, 0, 0, true));   // a window is being created

        var main = Source("src", "Glide.App", "MainWindow.axaml.cs");
        var closed = main[main.IndexOf("if (_closeForSpeedBoostStandby)\n            {\n                ExternalLaunchBroker.DetachForStandby(this);", StringComparison.Ordinal)..];
        closed = closed[..closed.IndexOf("};", StringComparison.Ordinal)];
        var unregister = closed.IndexOf("ExternalLaunchBroker.Unregister(this);", StringComparison.Ordinal);
        Assert.True(unregister > 0);
        Assert.True(closed.IndexOf("App.ExitIfNoWindowsRemain();", unregister, StringComparison.Ordinal) > unregister);
        var sessions = Source("src", "Glide.App", "MainWindow.Sessions.cs");
        Assert.Contains("App.NoteSpeedBoostStandbyEntered();", sessions, StringComparison.Ordinal);
    }
}
