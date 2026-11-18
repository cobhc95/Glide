using Avalonia;
using Glide.App;
using Glide.App.Services;
using GlideApp = Glide.App.App;

namespace Glide.Core.Tests;

/// <summary>
/// Regression tests for the 5.0.7 "Glide started and vanished" reports (docs/audits/silent-exit-investigation.md).
/// </summary>
[Trait("Area", "Window")]
[Collection("ExternalLaunchBroker")]
public sealed class SilentExitRegressionTests
{
    private static LaunchElectionOutcome Run(bool hasRequest, bool hidden, Func<TimeSpan, bool> forward, Func<bool> claim, TimeSpan budget, out List<string> trace)
    {
        var clock = TimeSpan.Zero;
        var lines = new List<string>();
        var outcome = LaunchElection.Resolve(hasRequest, hidden,
            timeout => { clock += timeout; return forward(timeout); },
            claim, budget,
            sleep: d => clock += d,
            elapsed: () => clock,
            trace: lines.Add);
        trace = lines;
        return outcome;
    }

    [Fact]
    public void Unresponsive_owner_never_makes_a_launch_exit_silently()
    {
        // Old behaviour: presence held + 2 s handshake failed => return 0 (no window). Now: start a window.
        var outcome = Run(hasRequest: true, hidden: false, forward: _ => false, claim: () => false, LaunchElection.DefaultBudget, out var trace);
        Assert.Equal(LaunchElectionOutcome.StartFollowerUi, outcome);
        Assert.Contains(trace, l => l.StartsWith("owner_unresponsive", StringComparison.Ordinal));
    }

    [Fact]
    public void Lingering_presence_handle_after_owner_exit_is_retried_until_this_launch_wins()
    {
        // Old behaviour: one final ClaimProcessPresence lost to a transient handle => return 0.
        var claims = 0;
        var outcome = Run(hasRequest: true, hidden: false, forward: _ => false, claim: () => ++claims >= 3, TimeSpan.FromSeconds(30), out _);
        Assert.Equal(LaunchElectionOutcome.Elected, outcome);
        Assert.Equal(3, claims);
    }

    [Fact]
    public void Slow_cold_owner_is_still_forwarded_to_after_the_first_handshake()
    {
        var attempts = 0;
        var outcome = Run(hasRequest: true, hidden: false, forward: _ => ++attempts == 3, claim: () => false, LaunchElection.DefaultBudget, out _);
        Assert.Equal(LaunchElectionOutcome.Forwarded, outcome);
    }

    [Fact]
    public void Reuse_disabled_starts_its_own_window_instead_of_exiting()
    {
        // ReuseSingleInstance=false has no forward batch; it used to lose the election and return 0.
        Assert.Equal(LaunchElectionOutcome.StartFollowerUi,
            Run(hasRequest: false, hidden: false, forward: _ => throw new InvalidOperationException(), claim: () => false, LaunchElection.DefaultBudget, out _));
        Assert.Equal(LaunchElectionOutcome.ExitHiddenDuplicate,
            Run(hasRequest: false, hidden: true, forward: _ => throw new InvalidOperationException(), claim: () => false, LaunchElection.DefaultBudget, out _));
    }

    [Fact]
    public void Stopping_broker_refuses_launch_requests_instead_of_acknowledging_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        ExternalLaunchBroker.Stop();
        var requests = new[] { new ExternalLaunchRequest('A', string.Empty, Guid.NewGuid()) };
        Assert.Null(ExternalLaunchBroker.AcceptOwnership(requests));

        // A process that (re)claims ownership accepts again.
        Assert.True(ExternalLaunchBroker.ClaimProcessPresence());
        try
        {
            var accepted = ExternalLaunchBroker.AcceptOwnership(new[] { new ExternalLaunchRequest('A', string.Empty, Guid.NewGuid()) });
            Assert.NotNull(accepted);
            Assert.Equal(ExternalLaunchItemStatus.Accepted, accepted![0].Status);
        }
        finally { ExternalLaunchBroker.Stop(); }
    }

    [Fact]
    public void Application_shutdown_stops_the_broker_and_blocks_speed_boost_standby()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlideApp.ResetShutdownStateForTests();
        var previousTrace = StartupTrace.PathOverrideForTests;
        StartupTrace.PathOverrideForTests = Path.Combine(Path.GetTempPath(), $"glide-trace-{Guid.NewGuid():N}.log");
        try
        {
            Assert.True(ExternalLaunchBroker.ClaimProcessPresence());
            Assert.True(ExternalLaunchBroker.ExistingProcessPresent());
            Assert.True(MainWindow.ShouldEnterSpeedBoostStandbyPolicy(false, false, false, GlideApp.IsShuttingDown));

            GlideApp.BeginApplicationShutdown("test");

            Assert.True(GlideApp.IsShuttingDown);
            Assert.False(ExternalLaunchBroker.ExistingProcessPresent());
            // Whole-app Overlay X (desktop.Shutdown) used to divert the closing window into standby.
            Assert.False(MainWindow.ShouldEnterSpeedBoostStandbyPolicy(false, false, false, GlideApp.IsShuttingDown));
        }
        finally
        {
            ExternalLaunchBroker.Stop();
            GlideApp.ResetShutdownStateForTests();
            try { File.Delete(StartupTrace.PathOverrideForTests!); } catch { }
            StartupTrace.PathOverrideForTests = previousTrace;
        }
    }

    [Fact]
    public void Isolated_settings_profile_uses_its_own_single_instance_names()
    {
        var user = "S-1-5-21-1";
        Assert.Equal(user, ProtocolNames.InstanceKey(user, null));
        Assert.Equal(user, ProtocolNames.InstanceKey(user, "  "));
        var a = ProtocolNames.UserScoped(ProtocolNames.PipeName, ProtocolNames.InstanceKey(user, @"C:\temp\glide-a"));
        var a2 = ProtocolNames.UserScoped(ProtocolNames.PipeName, ProtocolNames.InstanceKey(user, @"c:\TEMP\glide-a\"));
        var b = ProtocolNames.UserScoped(ProtocolNames.PipeName, ProtocolNames.InstanceKey(user, @"C:\temp\glide-b"));
        var normal = ProtocolNames.UserScoped(ProtocolNames.PipeName, user);
        Assert.Equal(a, a2);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, normal);
    }

    [Theory]
    [InlineData(-32000, -32000, true)]
    [InlineData(-32000, 10, true)]
    [InlineData(-1920, 0, false)]
    [InlineData(100, 100, false)]
    public void Minimized_parking_position_is_never_a_placement(int x, int y, bool parking)
        => Assert.Equal(parking, MainWindow.IsMinimizedParkingPosition(new PixelPoint(x, y)));

    [Fact]
    public void Startup_trace_records_lines_and_trims_to_recent_history()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-startup-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = StartupTrace.PathOverrideForTests;
        try
        {
            var path = Path.Combine(dir, StartupTrace.FileName);
            StartupTrace.PathOverrideForTests = path;
            StartupTrace.Write("launch", "args=0\nsecond-line");
            var first = File.ReadAllLines(path);
            Assert.Single(first);
            Assert.Contains($"pid={Environment.ProcessId}", first[0]);
            Assert.Contains("launch args=0 second-line", first[0]);

            var filler = new string('x', 200);
            for (var i = 0; i < 1200; i++) StartupTrace.Write("fill", i + " " + filler);
            StartupTrace.TrimIfLarge();
            var trimmed = File.ReadAllLines(path);
            Assert.True(trimmed.Length <= 600);
            Assert.EndsWith("1199 " + filler, trimmed[^1]);
        }
        finally
        {
            StartupTrace.PathOverrideForTests = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
