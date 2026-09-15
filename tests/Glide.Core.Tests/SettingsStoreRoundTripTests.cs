using System.Text.Json;
using Glide.App.Settings;
using Glide.Core.Commands;
using Xunit;

namespace Glide.Core.Tests;

public sealed class SettingsStoreRoundTripTests
{
    [Theory]
    [InlineData("Glide default")]
    [InlineData("Windows Photos")]
    [InlineData("IrfanView")]
    [InlineData("nomacs")]
    [InlineData("FastStone")]
    [InlineData("XnView MP")]
    public void Behavior_and_hotkey_presets_survive_save_load(string preset)
    {
        WithIsolatedStore(() =>
        {
            var state = SettingsPresetCatalog.Apply(preset, new GlideSettingsState());
            var expectedHotkeys = CloneMap(state.Hotkeys);
            var expectedGestures = new Dictionary<string, string>(state.Gestures, StringComparer.OrdinalIgnoreCase);
            var expectedInteraction = InteractionSnapshot(state);

            SettingsStore.Save(state);
            var loaded = SettingsStore.Load();

            AssertMapsEqual(expectedHotkeys, loaded.Hotkeys);
            Assert.Equal(expectedGestures.OrderBy(x => x.Key), loaded.Gestures.OrderBy(x => x.Key));
            Assert.Equal(expectedInteraction, InteractionSnapshot(loaded));
        });
    }

    [Fact]
    public void Explicit_IrfanView_preset_is_not_rewritten_on_repeated_loads()
    {
        WithIsolatedStore(() =>
        {
            var state = SettingsPresetCatalog.Apply("IrfanView", new GlideSettingsState());
            SettingsStore.Save(state);
            for (var i = 0; i < 3; i++)
            {
                state = SettingsStore.Load();
                Assert.Equal(new[] { "F11", "Enter" }, state.Hotkeys["view.fullscreen"]);
                Assert.Equal(new[] { "F", "Shift+W" }, state.Hotkeys["view.fit"]);
                Assert.Equal(new[] { "5", "W" }, state.Hotkeys["view.fitWidth"]);
                Assert.Equal(new[] { "6" }, state.Hotkeys["view.fitHeight"]);
                Assert.Equal(new[] { "Ctrl+H", "1" }, state.Hotkeys["view.actual"]);
                SettingsStore.Save(state);
            }
        });
    }

    [Fact]
    public void Schema_less_Irfan_like_map_is_preserved_conservatively()
    {
        WithIsolatedStore(() =>
        {
            var state = SettingsPresetCatalog.Apply("IrfanView", new GlideSettingsState());
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            using var doc = JsonDocument.Parse(json);
            var properties = doc.RootElement.EnumerateObject()
                .Where(p => !string.Equals(p.Name, nameof(GlideSettingsState.SettingsSchemaVersion), StringComparison.Ordinal))
                .Select(p => $"{JsonSerializer.Serialize(p.Name)}:{p.Value.GetRawText()}");
            File.WriteAllText(SettingsStore.GetSettingsPath(), "{" + string.Join(",", properties) + "}");

            var loaded = SettingsStore.Load();
            Assert.Equal(new[] { "Ctrl+H", "1" }, loaded.Hotkeys["view.actual"]);
            Assert.Equal(3, loaded.SettingsSchemaVersion);
        });
    }

    [Fact]
    public void Launch_policy_cache_is_invalidated_when_main_settings_change()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState { ReuseSingleInstance = false, ExternalOpenBehavior = "Open new window" };
            SettingsStore.Save(state);
            Assert.Equal(new SettingsStore.LaunchPolicy(false, "Open new window"), SettingsStore.LoadLaunchPolicy());

            var path = SettingsStore.GetSettingsPath();
            var json = File.ReadAllText(path).Replace("Open new window", "Overwrite existing tab");
            File.WriteAllText(path, json);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            var policy = SettingsStore.LoadLaunchPolicy();
            Assert.True(policy.ReuseSingleInstance == false);
            Assert.Equal("Overwrite existing tab", policy.ExternalOpenBehavior);
        });
    }


    [Fact]
    public void Launch_policy_race_never_tags_policy_A_with_identity_B()
    {
        WithIsolatedStore(() =>
        {
            var a = new GlideSettingsState { ReuseSingleInstance = false, ExternalOpenBehavior = "Open in new tab" };
            var b = new GlideSettingsState { ReuseSingleInstance = true, ExternalOpenBehavior = "Open new window" };
            SettingsStore.Save(a);
            var cache = Path.Combine(Path.GetDirectoryName(SettingsStore.GetSettingsPath())!, "glide.launch.policy.json");
            File.Delete(cache);
            var gate = new ManualResetEventSlim(false);
            var replaced = false;
            SettingsStore.AfterFirstFramePolicyBytesReadForTests = () =>
            {
                if (replaced) return;
                replaced = true;
                var path = SettingsStore.GetSettingsPath();
                var tmp = path + ".replacement";
                File.WriteAllText(tmp, JsonSerializer.Serialize(b));
                File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddSeconds(2));
                File.Move(tmp, path, true);
                gate.Set();
            };
            try
            {
                var policy = SettingsStore.LoadFirstFramePolicy();
                Assert.True(gate.IsSet);
                Assert.True(policy.ReuseSingleInstance);
                Assert.Equal("Open new window", policy.ExternalOpenBehavior);
                Assert.False(File.Exists(cache)); // cold miss does not synchronously publish sidecar
            }
            finally { SettingsStore.AfterFirstFramePolicyBytesReadForTests = null; }
        });
    }


    [Fact]
    public void Truncated_launch_policy_cache_is_ignored_without_mutating_settings()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState { ReuseSingleInstance = false, ExternalOpenBehavior = "Open new window" };
            SettingsStore.Save(state);
            var cache = Path.Combine(Path.GetDirectoryName(SettingsStore.GetSettingsPath())!, "glide.launch.policy.json");
            File.WriteAllText(cache, "{\"Schema\":4,\"SettingsLength\":");

            var policy = SettingsStore.LoadFirstFramePolicy();

            Assert.False(policy.ReuseSingleInstance);
            Assert.Equal("Open new window", policy.ExternalOpenBehavior);
            Assert.StartsWith("{\"Schema\":4", File.ReadAllText(cache)); // cold read does not rewrite a corrupt sidecar
        });
    }

    [Fact]
    public void Stale_launch_policy_cache_is_rejected_when_settings_identity_changes()
    {
        WithIsolatedStore(() =>
        {
            var oldState = new GlideSettingsState { ReuseSingleInstance = false, ExternalOpenBehavior = "Open new window" };
            SettingsStore.Save(oldState);
            var cache = Path.Combine(Path.GetDirectoryName(SettingsStore.GetSettingsPath())!, "glide.launch.policy.json");
            var staleCache = File.ReadAllText(cache);

            var current = new GlideSettingsState { ReuseSingleInstance = true, ExternalOpenBehavior = "Overwrite existing tab" };
            var settingsPath = SettingsStore.GetSettingsPath();
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(current));
            File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddSeconds(3));
            File.WriteAllText(cache, staleCache);

            var policy = SettingsStore.LoadFirstFramePolicy();
            Assert.True(policy.ReuseSingleInstance);
            Assert.Equal("Overwrite existing tab", policy.ExternalOpenBehavior);
        });
    }

    [Fact]
    public void Concurrent_atomic_saves_leave_main_file_and_sidecar_policy_consistent()
    {
        WithIsolatedStore(() =>
        {
            var states = Enumerable.Range(0, 24).Select(i => new GlideSettingsState
            {
                ReuseSingleInstance = (i & 1) == 0,
                ExternalOpenBehavior = (i % 3) switch
                {
                    0 => "Open in new tab",
                    1 => "Open new window",
                    _ => "Overwrite existing tab"
                },
                RapidPreviewLongestSide = 900 + i
            }).ToArray();

            Parallel.ForEach(states, SettingsStore.Save);

            var full = SettingsStore.Load();
            var compact = SettingsStore.LoadFirstFramePolicy();
            Assert.Equal(full.ReuseSingleInstance, compact.ReuseSingleInstance);
            Assert.Equal(full.ExternalOpenBehavior, compact.ExternalOpenBehavior);
            Assert.Equal(full.RapidPreviewLongestSide, compact.RapidPreviewLongestSide);
            var leftovers = Directory.EnumerateFiles(Path.GetDirectoryName(SettingsStore.GetSettingsPath())!, "*.tmp").ToArray();
            Assert.Empty(leftovers);
        });
    }

    [Fact]
    public void Test_directory_override_beats_inherited_benchmark_environment()
    {
        var inherited = Environment.GetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY");
        var envRoot = Path.Combine(Path.GetTempPath(), "Glide.Settings.Env", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(envRoot);
        Environment.SetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY", envRoot);
        try
        {
            WithIsolatedStore(() => Assert.False(SettingsStore.GetSettingsPath().StartsWith(Path.GetFullPath(envRoot), StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIDE_SETTINGS_DIRECTORY", inherited);
            try { Directory.Delete(envRoot, true); } catch { }
        }
    }

    [Fact]
    public void Malformed_first_frame_scalar_does_not_erase_unrelated_valid_choices()
    {
        WithIsolatedStore(() =>
        {
            File.WriteAllText(SettingsStore.GetSettingsPath(), "{\"ReuseSingleInstance\":false,\"ExternalOpenBehavior\":\"Open new window\",\"RapidPreviewLongestSide\":\"bad\",\"AdaptiveFastPreview\":false}");
            var policy = SettingsStore.LoadFirstFramePolicy();
            Assert.False(policy.ReuseSingleInstance);
            Assert.Equal("Open new window", policy.ExternalOpenBehavior);
            Assert.False(policy.AdaptiveFastPreview);
            Assert.Equal(3000, policy.RapidPreviewLongestSide);
        });
    }

    [Fact]
    public void First_frame_projection_matches_full_load_for_overrides()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState
            {
                InitialImageQuality = "Maximum speed", AdaptiveFastPreview = false,
                RapidPreviewLongestSide = 1777, BackgroundRefinement = false, AdaptivePreviewDelayMs = 123,
                SequentialForegroundReads = false, PredictivePrefetch = false, PrefetchDepth = 9,
                CacheItems = 23, CompressedCacheMegabytes = 333, DecodedCacheMegabytes = 444,
                ProgressiveColorFirstPreview = false, DefaultViewMode = "100%"
            };
            SettingsStore.Save(state);
            var compact = SettingsStore.LoadFirstFramePolicy();
            var projected = SettingsStore.ApplyFirstFramePolicy(new GlideSettingsState(), compact);
            var loaded = SettingsStore.Load();
            Assert.Equal(loaded.InitialImageQuality, projected.InitialImageQuality);
            Assert.Equal(loaded.AdaptiveFastPreview, projected.AdaptiveFastPreview);
            Assert.Equal(loaded.RapidPreviewLongestSide, projected.RapidPreviewLongestSide);
            Assert.Equal(loaded.BackgroundRefinement, projected.BackgroundRefinement);
            Assert.Equal(loaded.AdaptivePreviewDelayMs, projected.AdaptivePreviewDelayMs);
            Assert.Equal(loaded.SequentialForegroundReads, projected.SequentialForegroundReads);
            Assert.Equal(loaded.PredictivePrefetch, projected.PredictivePrefetch);
            Assert.Equal(loaded.PrefetchDepth, projected.PrefetchDepth);
            Assert.Equal(loaded.CacheItems, projected.CacheItems);
            Assert.Equal(loaded.CompressedCacheMegabytes, projected.CompressedCacheMegabytes);
            Assert.Equal(loaded.DecodedCacheMegabytes, projected.DecodedCacheMegabytes);
            Assert.Equal(loaded.ProgressiveColorFirstPreview, projected.ProgressiveColorFirstPreview);
            Assert.Equal(loaded.DefaultViewMode, projected.DefaultViewMode);
        });
    }

    [Theory]
    [InlineData("Maximum speed", 877, false, 77, false, 4, 19, 211, 233, true)]
    [InlineData("Balanced", 1666, true, 31, false, 8, 27, 299, 355, false)]
    [InlineData("Maximum quality", 3555, false, 9, true, 11, 41, 611, 777, true)]
    public void First_frame_projection_preserves_profile_label_and_individual_overrides(
        string profile, int previewSide, bool refinement, int delay, bool sequential, int depth,
        int items, int compressedMb, int decodedMb, bool progressive)
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState
            {
                InitialImageQuality = profile, AdaptiveFastPreview = true, RapidPreviewLongestSide = previewSide,
                BackgroundRefinement = refinement, AdaptivePreviewDelayMs = delay, SequentialForegroundReads = sequential,
                PredictivePrefetch = false, PrefetchDepth = depth, CacheItems = items,
                CompressedCacheMegabytes = compressedMb, DecodedCacheMegabytes = decodedMb,
                ProgressiveColorFirstPreview = progressive
            };
            SettingsStore.Save(state);
            var projected = SettingsStore.ApplyFirstFramePolicy(new GlideSettingsState(), SettingsStore.LoadFirstFramePolicy());
            var full = SettingsStore.Load();
            Assert.Equal(full.InitialImageQuality, projected.InitialImageQuality);
            Assert.Equal(full.RapidPreviewLongestSide, projected.RapidPreviewLongestSide);
            Assert.Equal(full.BackgroundRefinement, projected.BackgroundRefinement);
            Assert.Equal(full.AdaptivePreviewDelayMs, projected.AdaptivePreviewDelayMs);
            Assert.Equal(full.SequentialForegroundReads, projected.SequentialForegroundReads);
            Assert.Equal(full.PredictivePrefetch, projected.PredictivePrefetch);
            Assert.Equal(full.PrefetchDepth, projected.PrefetchDepth);
            Assert.Equal(full.CacheItems, projected.CacheItems);
            Assert.Equal(full.CompressedCacheMegabytes, projected.CompressedCacheMegabytes);
            Assert.Equal(full.DecodedCacheMegabytes, projected.DecodedCacheMegabytes);
            Assert.Equal(full.ProgressiveColorFirstPreview, projected.ProgressiveColorFirstPreview);
        });
    }

    [Fact]
    public void Historical_schema_does_not_guess_that_explicit_Irfan_like_hotkeys_are_defaults()
    {
        WithIsolatedStore(() =>
        {
            var state = SettingsPresetCatalog.Apply("IrfanView", new GlideSettingsState());
            state.SettingsSchemaVersion = 1;
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(state));
            var loaded = SettingsStore.Load();
            Assert.Equal(new[] { "F11", "Enter" }, loaded.Hotkeys["view.fullscreen"]);
            Assert.Equal(new[] { "F", "Shift+W" }, loaded.Hotkeys["view.fit"]);
            Assert.Equal(new[] { "Ctrl+H", "1" }, loaded.Hotkeys["view.actual"]);
            Assert.Equal(3, loaded.SettingsSchemaVersion);
        });
    }

    [Fact]
    public void Hydration_merge_preserves_only_properties_edited_after_baseline()
    {
        var baseline = new GlideSettingsState();
        var current = baseline.CloneState();
        current.ThemeChoice = "Light";
        current.Hotkeys["view.fit"] = new List<string> { "Z" };
        var hydrated = baseline.CloneState();
        hydrated.CacheItems = 31;
        hydrated.StatusBarSize = "Large";
        var merged = SettingsStore.MergeHydratedPreservingEdits(baseline, current, hydrated);
        Assert.Equal("Light", merged.ThemeChoice);
        Assert.Equal(new[] { "Z" }, merged.Hotkeys["view.fit"]);
        Assert.Equal(31, merged.CacheItems);
        Assert.Equal("Large", merged.StatusBarSize);
    }

    [Fact]
    public void Window_placement_settings_roundtrip_preserves_position_size_and_maximized_state()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState
            {
                RememberWindowPlacement = true,
                WindowX = 250,
                WindowY = 180,
                WindowWidth = 1400,
                WindowHeight = 900,
                WindowWasMaximized = true
            };
            SettingsStore.Save(state);

            var firstFramePolicy = SettingsStore.LoadFirstFramePolicy();
            Assert.True(firstFramePolicy.RememberWindowPlacement);
            Assert.Equal(250, firstFramePolicy.WindowX);
            Assert.Equal(180, firstFramePolicy.WindowY);
            Assert.Equal(1400, firstFramePolicy.WindowWidth);
            Assert.Equal(900, firstFramePolicy.WindowHeight);
            Assert.True(firstFramePolicy.WindowWasMaximized);

            var loaded = SettingsStore.Load();
            Assert.True(loaded.RememberWindowPlacement);
            Assert.Equal(250, loaded.WindowX);
            Assert.Equal(180, loaded.WindowY);
            Assert.Equal(1400, loaded.WindowWidth);
            Assert.Equal(900, loaded.WindowHeight);
            Assert.True(loaded.WindowWasMaximized);
        });
    }

    private static void WithIsolatedStore(Action action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try { action(); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(root, true); } catch { }
        }
    }
    [Fact]
    public void SpeedBoost_settings_roundtrip_properly()
    {
        WithIsolatedStore(() =>
        {
            var initial = new GlideSettingsState
            {
                SpeedBoostEnabled = true,
                StartWithWindowsInBackground = true,
                ShowTrayIcon = true
            };
            SettingsStore.Save(initial);
            var loaded = SettingsStore.Load();
            Assert.True(loaded.SpeedBoostEnabled);
            Assert.True(loaded.StartWithWindowsInBackground);
            Assert.True(loaded.ShowTrayIcon);

            loaded.SpeedBoostEnabled = false;
            loaded.StartWithWindowsInBackground = false;
            SettingsStore.Save(loaded);

            var reloaded = SettingsStore.Load();
            Assert.False(reloaded.SpeedBoostEnabled);
            Assert.False(reloaded.StartWithWindowsInBackground);
        });
    }

    private static Dictionary<string, List<string>> CloneMap(Dictionary<string, List<string>> map) =>
        map.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);

    private static void AssertMapsEqual(Dictionary<string, List<string>> expected, Dictionary<string, List<string>> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(x => x), actual.Keys.OrderBy(x => x));
        foreach (var key in expected.Keys) Assert.Equal(expected[key], actual[key]);
    }

    private static object InteractionSnapshot(GlideSettingsState s) => new
    {
        s.DoubleClickFullscreen,
        s.DoubleClickExitFullscreen,
        s.FullscreenClickNavigation,
        s.WindowedWheelZoom,
        s.InvertWheelDirection,
        s.LeftDragMode,
        s.LeftWindowDragBehavior,
        s.RightDragBehavior,
        s.RightDragPan,
        s.SelectionClickZoom,
        s.SelectionRightClickZoomOut,
        s.BackgroundDragWindow,
        s.CtrlWheelZoom,
        s.MiddleDragPan
    };
}
