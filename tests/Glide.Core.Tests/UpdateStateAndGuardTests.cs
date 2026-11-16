using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glide.App.Settings;
using Glide.App.Updates;
using Glide.Core.Updates;
using static Glide.Core.Tests.UpdateTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// The update checker's memory in the settings file (additive keys, JSON round trip, old files, equality, merge, targeted writes)
/// and the guard rails: no test and no code path outside production start-up can make a real network request.
/// </summary>
// Source-text contract kept (tests reading src/): update-feed hardening and the "only HttpClient users" scan are security tripwires over source that no runtime test can enumerate.
[Collection("UpdateService process-wide state")]
public sealed class UpdateStateAndGuardTests
{
    private static void WithIsolatedStore(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Update.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try { action(root); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static GlideSettingsState WithMemory() => new()
    {
        UpdateCheckDaily = false,
        UpdateDontRemind = true,
        UpdateLastCheckedUtc = new DateTime(2026, 11, 15, 21, 40, 5, DateTimeKind.Utc),
        UpdateLastAttemptUtc = new DateTime(2026, 11, 15, 21, 40, 5, DateTimeKind.Utc),
        UpdateLastAttemptFailed = true,
        UpdateLatestKnownVersion = "5.0.2",
        UpdateLatestKnownUrl = "https://github.com/cobhc95/Glide/releases/tag/v5.0.2",
        UpdateLatestKnownPublishedUtc = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc),
        UpdateLastPromptedVersion = "5.0.2",
        UpdateLastPromptedUtc = new DateTime(2026, 11, 15, 21, 41, 0, DateTimeKind.Utc)
    };

    // ============================================================ settings file ========

    [Fact]
    public void Defaults_match_the_agreed_behaviour()
    {
        var state = new GlideSettingsState();
        Assert.True(state.UpdateCheckDaily);
        Assert.False(state.UpdateDontRemind);
        Assert.Null(state.UpdateLastCheckedUtc);
        Assert.Null(state.UpdateLastAttemptUtc);
        Assert.False(state.UpdateLastAttemptFailed);
        Assert.Equal(string.Empty, state.UpdateLatestKnownVersion);
        Assert.Equal(string.Empty, state.UpdateLatestKnownUrl);
        Assert.Equal(string.Empty, state.UpdateLastPromptedVersion);
        Assert.Null(state.UpdateLastPromptedUtc);
    }

    [Fact]
    public void The_update_memory_survives_save_and_load_as_additive_keys()
    {
        WithIsolatedStore(root =>
        {
            SettingsStore.Save(WithMemory());
            var loaded = SettingsStore.Load();
            Assert.False(loaded.UpdateCheckDaily);
            Assert.True(loaded.UpdateDontRemind);
            Assert.Equal(new DateTime(2026, 11, 15, 21, 40, 5, DateTimeKind.Utc), loaded.UpdateLastCheckedUtc);
            Assert.Equal(DateTimeKind.Utc, loaded.UpdateLastCheckedUtc!.Value.Kind);
            Assert.True(loaded.UpdateLastAttemptFailed);
            Assert.Equal("5.0.2", loaded.UpdateLatestKnownVersion);
            Assert.Equal("https://github.com/cobhc95/Glide/releases/tag/v5.0.2", loaded.UpdateLatestKnownUrl);
            Assert.Equal(new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc), loaded.UpdateLatestKnownPublishedUtc);
            Assert.Equal("5.0.2", loaded.UpdateLastPromptedVersion);
            Assert.Equal(new DateTime(2026, 11, 15, 21, 41, 0, DateTimeKind.Utc), loaded.UpdateLastPromptedUtc);

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "glide.settings.json")));
            foreach (var key in new[] { "UpdateCheckDaily", "UpdateDontRemind", "UpdateLastCheckedUtc", "UpdateLastAttemptUtc", "UpdateLastAttemptFailed",
                         "UpdateLatestKnownVersion", "UpdateLatestKnownUrl", "UpdateLatestKnownPublishedUtc", "UpdateLastPromptedVersion", "UpdateLastPromptedUtc" })
                Assert.True(document.RootElement.TryGetProperty(key, out _), "missing key " + key);
        });
    }

    [Fact]
    public void A_settings_file_from_before_the_update_checker_loads_with_the_defaults()
    {
        WithIsolatedStore(root =>
        {
            SettingsStore.Save(new GlideSettingsState { ThemeChoice = "Light" });
            // Strip every update key, like a file written by 5.0.0.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "glide.settings.json")))!.AsObject();
            foreach (var key in json.Where(p => p.Key.StartsWith("Update", StringComparison.Ordinal)).Select(p => p.Key).ToList()) json.Remove(key);
            File.WriteAllText(Path.Combine(root, "glide.settings.json"), json.ToJsonString());

            var loaded = SettingsStore.Load();
            Assert.Equal("Light", loaded.ThemeChoice);
            Assert.True(loaded.UpdateCheckDaily);
            Assert.False(loaded.UpdateDontRemind);
            Assert.Null(loaded.UpdateLastCheckedUtc);
            Assert.Equal(string.Empty, loaded.UpdateLatestKnownVersion);
        });
    }

    [Fact]
    public void Junk_in_the_update_keys_is_cleaned_on_load()
    {
        WithIsolatedStore(root =>
        {
            SettingsStore.Save(new GlideSettingsState());
            var path = Path.Combine(root, "glide.settings.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["UpdateLatestKnownVersion"] = "definitely not a version";
            json["UpdateLatestKnownUrl"] = "https://github.com/cobhc95/Glide/releases/tag/v9";
            json["UpdateLastCheckedUtc"] = "2026-11-15T21:40:00";       // no zone
            File.WriteAllText(path, json.ToJsonString());
            var loaded = SettingsStore.Load();
            Assert.Equal(string.Empty, loaded.UpdateLatestKnownVersion);
            Assert.Equal(string.Empty, loaded.UpdateLatestKnownUrl);
            Assert.Equal(DateTimeKind.Utc, loaded.UpdateLastCheckedUtc!.Value.Kind);
        });
    }

    [Fact]
    public void A_corrupt_update_key_falls_back_to_the_existing_fail_safe_load()
    {
        WithIsolatedStore(root =>
        {
            SettingsStore.Save(new GlideSettingsState { ThemeChoice = "Neutral" });
            var path = Path.Combine(root, "glide.settings.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["UpdateLastCheckedUtc"] = "yesterday-ish";
            File.WriteAllText(path, json.ToJsonString());
            var loaded = SettingsStore.Load();
            // The whole file falls back to defaults (existing fail-safe behaviour) rather than crashing.
            Assert.NotNull(loaded);
        });
    }

    [Fact]
    public void Clone_and_equality_treat_the_memory_as_bookkeeping()
    {
        var a = WithMemory();
        var clone = a.CloneState();
        Assert.Equal(a.UpdateLastCheckedUtc, clone.UpdateLastCheckedUtc);
        Assert.Equal(a.UpdateLatestKnownVersion, clone.UpdateLatestKnownVersion);
        Assert.True(a.ContentEquals(clone));

        // Different memory only: still "the same settings", and never a listed change.
        var other = a.CloneState();
        other.UpdateLastCheckedUtc = other.UpdateLastCheckedUtc!.Value.AddDays(3);
        other.UpdateLatestKnownVersion = "9.9.9";
        other.UpdateLastPromptedUtc = null;
        Assert.True(a.ContentEquals(other));
        Assert.Empty(SettingsChangeSet.Compute(a, other));

        // A real setting difference is still found.
        other.UpdateCheckDaily = true;
        Assert.False(a.ContentEquals(other));
        var changes = SettingsChangeSet.Compute(a, other);
        Assert.Contains(changes, c => c.Name == "Update Check Daily" && c.Before == "Off" && c.After == "On");
        // ...and the memory is never listed, even next to a real change.
        Assert.DoesNotContain(changes, c => c.Name.Contains("Last Checked") || c.Name.Contains("Latest Known") ||
                                            c.Name.Contains("Last Prompted") || c.Name.Contains("Last Attempt"));
    }

    [Fact]
    public void The_memory_round_trips_through_the_standalone_state_object()
    {
        var a = WithMemory();
        var state = a.ReadUpdateState();
        Assert.Equal(a.UpdateLastCheckedUtc, state.LastCheckedUtc);
        Assert.True(state.LastAttemptFailed);
        Assert.Equal("5.0.2", state.LatestKnownVersion);
        var b = new GlideSettingsState();
        b.WriteUpdateState(state);
        Assert.Equal(a.UpdateLastPromptedUtc, b.UpdateLastPromptedUtc);
        Assert.Equal(a.UpdateLatestKnownUrl, b.UpdateLatestKnownUrl);
        Assert.True(state.SameAs(b.ReadUpdateState()));
        // Every memory property is named in UpdateStateProperties (what the change list and the targeted writer skip / write).
        var memoryProperties = typeof(GlideSettingsState).GetProperties().Where(p => p.Name.StartsWith("Update", StringComparison.Ordinal) &&
            p.Name is not (nameof(GlideSettingsState.UpdateCheckDaily) or nameof(GlideSettingsState.UpdateDontRemind))).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(memoryProperties, GlideSettingsState.UpdateStateProperties.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Merging_start_up_settings_keeps_edits_made_since_the_baseline()
    {
        var baseline = new GlideSettingsState();
        var current = baseline.CloneState();
        current.UpdateCheckDaily = false;                       // the user switched it off during start-up
        var hydrated = baseline.CloneState();
        hydrated.UpdateLastCheckedUtc = T0;                     // the file on disk has a check time
        hydrated.UpdateLatestKnownVersion = "5.0.2";
        var merged = SettingsStore.MergeHydratedPreservingEdits(baseline, current, hydrated);
        Assert.False(merged.UpdateCheckDaily);
        Assert.Equal(T0, merged.UpdateLastCheckedUtc);
        Assert.Equal("5.0.2", merged.UpdateLatestKnownVersion);
    }

    [Fact]
    public void Saving_only_the_update_memory_leaves_every_other_value_on_disk_untouched()
    {
        WithIsolatedStore(root =>
        {
            var path = Path.Combine(root, "glide.settings.json");
            SettingsStore.Save(new GlideSettingsState { ThemeChoice = "Light", UpdateCheckDaily = false, SlideshowIntervalMs = 4321 });

            // Settings is previewing an unapplied change in memory; a check finishes meanwhile and persists only its own memory.
            var memory = new UpdateCheckState { LastCheckedUtc = T0, LatestKnownVersion = "5.0.2", LatestKnownUrl = "https://github.com/cobhc95/Glide/releases/tag/v5.0.2", LastPromptedVersion = "5.0.2", LastPromptedUtc = T0 };
            Assert.True(SettingsStore.SaveUpdateState(memory));

            var loaded = SettingsStore.Load();
            Assert.Equal("Light", loaded.ThemeChoice);
            Assert.False(loaded.UpdateCheckDaily);
            Assert.Equal(4321, loaded.SlideshowIntervalMs);
            Assert.Equal(T0, loaded.UpdateLastCheckedUtc);
            Assert.Equal("5.0.2", loaded.UpdateLatestKnownVersion);
            Assert.Equal(T0, loaded.UpdateLastPromptedUtc);
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void Saving_the_memory_without_a_settings_file_creates_one_so_a_fresh_install_does_not_check_on_every_start()
    {
        WithIsolatedStore(root =>
        {
            Assert.False(File.Exists(Path.Combine(root, "glide.settings.json")));
            Assert.True(SettingsStore.SaveUpdateState(new UpdateCheckState { LastCheckedUtc = T0, LatestKnownVersion = "5.0.1" }));
            var loaded = SettingsStore.Load();
            Assert.Equal(T0, loaded.UpdateLastCheckedUtc);
            Assert.True(loaded.UpdateCheckDaily);
            Assert.False(loaded.UpdateDontRemind);
        });
    }

    [Fact]
    public void A_clear_memory_removes_nothing_but_the_update_keys()
    {
        WithIsolatedStore(root =>
        {
            SettingsStore.Save(WithMemory());
            Assert.True(SettingsStore.SaveUpdateState(new UpdateCheckState()));
            var loaded = SettingsStore.Load();
            Assert.Null(loaded.UpdateLastCheckedUtc);
            Assert.Equal(string.Empty, loaded.UpdateLatestKnownVersion);
            Assert.False(loaded.UpdateCheckDaily);       // the user's settings survive
            Assert.True(loaded.UpdateDontRemind);
        });
    }

    [Fact]
    public void Browser_launch_uses_the_validated_address_as_the_only_thing_handed_to_the_shell()
    {
        var info = UpdateBrowserLauncher.CreateStartInfo("https://github.com/cobhc95/Glide/releases/tag/v5.0.2");
        Assert.Equal("https://github.com/cobhc95/Glide/releases/tag/v5.0.2", info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.True(string.IsNullOrEmpty(info.Arguments));
        Assert.Empty(info.ArgumentList);

        foreach (var hostile in new[] { "https://evil.com/x", "calc.exe", "file:///C:/Windows/System32/cmd.exe", "https://github.com/cobhc95/Glide/releases & calc", null })
        {
            var safe = UpdateBrowserLauncher.CreateStartInfo(hostile);
            Assert.Equal(UpdateUrlPolicy.FallbackReleaseUrl, safe.FileName);
            Assert.True(string.IsNullOrEmpty(safe.Arguments));
        }

        var started = new List<ProcessStartInfo>();
        UpdateBrowserLauncher.StartOverrideForTests = started.Add;
        try
        {
            Assert.True(UpdateBrowserLauncher.Open("https://github.com/cobhc95/Glide/releases/tag/v5.0.2"));
            Assert.Single(started);
        }
        finally { UpdateBrowserLauncher.StartOverrideForTests = null; }
    }

    // ============================================================ no real network from tests ========

    private static IEnumerable<string> TestSources()
    {
        var tests = Path.Combine(SourceText.RepoRoot, "tests");
        return Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            // This file and the feed tests name the forbidden things in order to forbid them.
            .Where(f => !Path.GetFileName(f).Equals("UpdateStateAndGuardTests.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_test_builds_the_real_feed_or_a_real_http_stack()
    {
        var problems = new List<string>();
        foreach (var file in TestSources())
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "GitHubUpdateFeed.CreateProduction", "UpdateProduction.Start", "new SocketsHttpHandler", "new HttpClientHandler", "new HttpClient()", "WebRequest.Create", "new TcpClient", "HttpClient.GetAsync" })
                if (text.Contains(forbidden, StringComparison.Ordinal)) problems.Add($"{Path.GetFileName(file)} uses {forbidden}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Test_code_that_builds_the_feed_only_ever_gives_it_a_fake_handler()
    {
        var feedUses = TestSources().SelectMany(f => Regex.Matches(File.ReadAllText(f), @"new GitHubUpdateFeed\(([^;]*)").Select(m => (File: Path.GetFileName(f), Args: m.Groups[1].Value))).ToList();
        Assert.NotEmpty(feedUses);                                              // the feed is covered by tests...
        foreach (var use in feedUses)
            Assert.True(use.Args.Contains("factory", StringComparison.Ordinal), $"{use.File}: new GitHubUpdateFeed({use.Args}) must be given the fake handler factory");   // ...with fakes only
    }

    [Fact]
    public void Only_production_start_up_creates_the_real_feed_and_the_service()
    {
        var root = Path.Combine(SourceText.RepoRoot, "src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (text.Contains("GitHubUpdateFeed.CreateProduction()", StringComparison.Ordinal) && name != "UpdateProduction.cs") offenders.Add($"{name}: builds the real feed");
            if (text.Contains("UpdateProduction.Start()", StringComparison.Ordinal) && name != "App.axaml.cs") offenders.Add($"{name}: starts the production checker");
            if (text.Contains("UpdateService.Install(", StringComparison.Ordinal) && name is not ("UpdateService.cs" or "UpdateProduction.cs")) offenders.Add($"{name}: installs the update service");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));

        // MainWindow only ever looks the service up; it never creates one.
        var app = File.ReadAllText(Path.Combine(root, "Glide.App", "App.axaml.cs"));
        Assert.Contains("UpdateProduction.Start()", app, StringComparison.Ordinal);
        Assert.Contains("BenchmarkExitAfterFirstFrame", app[app.IndexOf("ScheduleUpdateChecker(MainWindow", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_is_absent_by_default_so_headless_windows_never_start_a_checker()
    {
        Assert.Null(UpdateService.Current);
    }

    [Fact]
    public void The_real_feed_is_hardened_in_source()
    {
        var feed = File.ReadAllText(Path.Combine(SourceText.RepoRoot, "src", "Glide.App", "Updates", "GitHubUpdateFeed.cs"));
        Assert.Contains("AllowAutoRedirect = false", feed, StringComparison.Ordinal);
        Assert.Contains("UseCookies = false", feed, StringComparison.Ordinal);
        Assert.Contains("UseProxy = true", feed, StringComparison.Ordinal);
        Assert.Contains("UpdateUrlPolicy.IsAllowedRedirect", feed, StringComparison.Ordinal);
        Assert.Contains("UpdateReleaseParser.MaxResponseBytes", feed, StringComparison.Ordinal);
        Assert.Contains("\"Glide-UpdateCheck\"", feed, StringComparison.Ordinal);
        Assert.Contains("application/vnd.github+json", feed, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Authorization", "CookieContainer", "Credentials", "Referrer", "Environment.MachineName", "Environment.UserName", "GetEnvironmentVariable" })
            Assert.DoesNotContain(forbidden, feed, StringComparison.Ordinal);
        // The only address the code can request.
        Assert.Contains("UpdateUrlPolicy.LatestReleaseApiUrl", feed, StringComparison.Ordinal);
        var urls = Regex.Matches(feed, @"https?://[^\s""]+").Select(m => m.Value).ToList();
        Assert.Empty(urls);
    }

    [Fact]
    public void The_only_http_client_code_in_the_app_is_the_update_feed()
    {
        var root = Path.Combine(SourceText.RepoRoot, "src");
        var users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bnew HttpClient\b|\bSocketsHttpHandler\b|\bHttpClientHandler\b|\bWebClient\b"))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        // 5.0.4: the optional video decoder download is gone; Glide plays videos only with the codecs installed in Windows.
        Assert.Equal(new[] { "GitHubUpdateFeed.cs" }, users);
    }
}
