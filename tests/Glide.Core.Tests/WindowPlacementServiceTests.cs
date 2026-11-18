using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App.Printing;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Windows;

namespace Glide.Core.Tests;

/// <summary>
/// The shared window placement service: per-window save on close (also Cancel), restore before showing, validation
/// against the monitors, position-only dialogs, the settings-file store (clone / equality / merge / JSON / older
/// files) and the migration of the Settings window's older keys.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Window")]
public sealed class WindowPlacementServiceTests : IDisposable
{
    private static readonly ScreenWorkArea Full = new(0, 0, 1920, 1040, 1.0);
    private readonly List<IReadOnlyDictionary<string, WindowPlacementState>> _persisted = new();
    private readonly string _printDir = Path.Combine(Path.GetTempPath(), "Glide.PlacementTests", Guid.NewGuid().ToString("N"));

    public WindowPlacementServiceTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        Directory.CreateDirectory(_printDir);
        PrintSettingsStore.DirectoryOverrideForTests = _printDir;
        WindowPlacementService.ResetForTests();
        WindowPlacementService.PersistOverrideForTests = snapshot => _persisted.Add(snapshot);
        WindowPlacementService.ScreensOverrideForTests = _ => (new[] { Full }, Full);
    }

    public void Dispose()
    {
        WindowPlacementService.PersistOverrideForTests = null;
        WindowPlacementService.ScreensOverrideForTests = null;
        WindowPlacementService.ResetForTests();
        PrintSettingsStore.DirectoryOverrideForTests = null;
        TempDir.TryDelete(_printDir);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static PrintWindow NewPrint() =>
        PrintWindowResponsivenessTests.Open(new PrintQueryCache(FakePrintQueries.Standard()), show: false).Window;

    [Fact]
    public async Task Print_window_opens_at_its_default_first_then_remembers_size_position_and_maximized()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var first = NewPrint();
            Assert.Equal(1020, first.Width);
            Assert.Equal(680, first.Height);
            Assert.Equal(WindowStartupLocation.CenterOwner, first.WindowStartupLocation);
            Assert.Equal("print", WindowPlacementService.KeyOf(first));
            first.Show();
            first.Width = 1180; first.Height = 760; first.Position = new PixelPoint(140, 90);
            Settle(first);
            first.Close();

            var saved = WindowPlacementService.Get("print")!;
            Assert.Equal((140, 90, 1180d, 760d, false), (saved.X, saved.Y, saved.Width, saved.Height, saved.Maximized));
            Assert.NotEmpty(_persisted);
            Assert.True(_persisted[^1].ContainsKey("print"));

            var again = NewPrint();
            Assert.Equal(1180, again.Width);
            Assert.Equal(760, again.Height);
            Assert.Equal(WindowStartupLocation.Manual, again.WindowStartupLocation);
            again.Show();
            Assert.Equal(new PixelPoint(140, 90), again.Position);
            again.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            again.Close();

            var max = WindowPlacementService.Get("print")!;
            Assert.True(max.Maximized);
            Assert.Equal(1180, max.Width, 1.0); // the normal size is kept for un-maximizing
            var third = NewPrint();
            Assert.Equal(WindowState.Maximized, third.WindowState);
            third.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Cancel_saves_the_placement_too()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = NewPrint();
            window.Show();
            window.Position = new PixelPoint(33, 44);
            Settle(window);
            var cancel = (Button)typeof(PrintWindow).GetField("_cancelButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
            Assert.Equal((33, 44), (WindowPlacementService.Get("print")!.X, WindowPlacementService.Get("print")!.Y));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Saved_placements_are_validated_off_screen_centres_oversized_clamps_tiny_respects_minimum()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            WindowPlacementService.ResetForTests(new Dictionary<string, WindowPlacementState>
            {
                ["print"] = new() { X = 9000, Y = 9000, Width = 20000, Height = 20000 },
            });
            var huge = NewPrint();
            Assert.Equal(WindowStartupLocation.CenterOwner, huge.WindowStartupLocation); // off-screen: centred on owner
            Assert.True(huge.Width <= 1920 && huge.Height <= 1040, $"{huge.Width}x{huge.Height}");
            Assert.True(huge.MinWidth <= huge.Width && huge.MinHeight <= huge.Height);
            huge.Close();

            WindowPlacementService.ResetForTests(new Dictionary<string, WindowPlacementState>
            {
                ["print"] = new() { X = 10, Y = 10, Width = 100, Height = 80 },
            });
            var tiny = NewPrint();
            Assert.Equal(880, tiny.Width);
            Assert.Equal(560, tiny.Height);
            Assert.Equal(880, tiny.MinWidth);
            tiny.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Fixed_size_dialogs_remember_nothing_and_always_open_centred_on_their_owner()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            Window Dialog()
            {
                var d = new Window { Width = 470, SizeToContent = SizeToContent.Height, CanResize = false, Content = new TextBlock { Text = "Sure?" } };
                WindowPlacementService.Track(d, "confirm");
                return d;
            }
            var first = Dialog();
            Assert.Equal(WindowPlacementMode.PositionOnly, WindowPlacementService.ModeOf(first));
            Assert.Equal(WindowStartupLocation.CenterOwner, first.WindowStartupLocation);
            first.Show();
            first.Position = new PixelPoint(500, 300);
            Settle(first);
            first.Close();
            Assert.Null(WindowPlacementService.Get("confirm")); // a dialog's position is never saved

            var again = Dialog();
            Assert.Equal(470, again.Width);                  // size still comes from the dialog itself
            Assert.Equal(SizeToContent.Height, again.SizeToContent);
            Assert.Equal(WindowStartupLocation.CenterOwner, again.WindowStartupLocation);
            again.Close();

            // A resizable dialog defaults to size and position.
            var resizable = new Window { Width = 620, Height = 360 };
            WindowPlacementService.Track(resizable, "diagnostics-results");
            Assert.Equal(WindowPlacementMode.SizeAndPosition, WindowPlacementService.ModeOf(resizable));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Old_settings_window_keys_are_migrated_into_the_shared_store_and_still_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try
        {
            // A settings file written before the shared store: only the SettingsWindow* keys.
            File.WriteAllText(Path.Combine(root, "glide.settings.json"), """
                { "SettingsSchemaVersion": 7, "ThemeChoice": "Light",
                  "SettingsWindowX": 210, "SettingsWindowY": 130, "SettingsWindowWidth": 1010, "SettingsWindowHeight": 690, "SettingsWindowWasMaximized": true }
                """);
            var loaded = SettingsStore.Load();
            var entry = loaded.WindowPlacements["settings"];
            Assert.Equal((210, 130, 1010d, 690d, true), (entry.X, entry.Y, entry.Width, entry.Height, entry.Maximized));
            Assert.Equal(new SettingsWindowPlacement(210, 130, 1010, 690, true), SettingsWindowPlacement.From(loaded));
            // The light-weight reader the service seeds from agrees.
            Assert.Equal(1010, SettingsStore.LoadWindowPlacements()["settings"].Width);

            // Once the shared entry exists it wins over the older keys (which are still mirrored for older builds).
            new SettingsWindowPlacement(5, 6, 900, 600, false).WriteTo(loaded);
            Assert.Equal(900, loaded.SettingsWindowWidth);
            loaded.SettingsWindowWidth = 1234; // stale older key
            Assert.Equal(900, SettingsWindowPlacement.From(loaded).Width);

            // A file with neither has nothing saved; the default size applies.
            File.WriteAllText(Path.Combine(root, "glide.settings.json"), """{ "SettingsSchemaVersion": 7 }""");
            Assert.Empty(SettingsStore.Load().WindowPlacements);
            Assert.False(SettingsWindowPlacement.From(SettingsStore.Load()).HasSize);
        }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(root);
        }
    }

    [Fact]
    public void Store_is_part_of_clone_equality_merge_and_json_and_a_dialog_save_patches_only_its_property()
    {
        var state = new GlideSettingsState();
        Assert.Empty(state.WindowPlacements);
        var changed = state.CloneState();
        changed.WindowPlacements["print"] = new WindowPlacementState { X = 1, Y = 2, Width = 1000, Height = 700 };
        Assert.Empty(state.WindowPlacements); // the clone is deep
        Assert.False(state.ContentEquals(changed));
        Assert.True(changed.ContentEquals(changed.CloneState()));
        var restamped = changed.CloneState();
        restamped.WindowPlacements["print"] = restamped.WindowPlacements["print"] with { LastUsedUtcTicks = 99 };
        Assert.True(changed.ContentEquals(restamped)); // the last-used stamp is not a setting change

        var hydrated = state.CloneState();
        hydrated.CacheItems = 33;
        var merged = SettingsStore.MergeHydratedPreservingEdits(state, changed, hydrated);
        Assert.Equal(1000, merged.WindowPlacements["print"].Width);
        Assert.Equal(33, merged.CacheItems);

        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try
        {
            var path = Path.Combine(root, "glide.settings.json");
            Assert.False(SettingsStore.SaveWindowPlacements(changed.WindowPlacements)); // no file yet: nothing written
            Assert.False(File.Exists(path));

            changed.ThemeChoice = "Light";
            SettingsStore.Save(changed);
            Assert.Equal(1000, SettingsStore.Load().WindowPlacements["print"].Width);

            var before = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var update = new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase)
            {
                ["print"] = new() { X = 7, Y = 8, Width = 1111, Height = 777 },
                ["confirm"] = new() { X = 50, Y = 60 },
            };
            Assert.True(SettingsStore.SaveWindowPlacements(update));
            var after = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var property in before)
            {
                if (property.Key == "WindowPlacements") continue;
                Assert.True(JsonNode.DeepEquals(property.Value, after[property.Key]), property.Key);
            }
            var reloaded = SettingsStore.Load();
            Assert.Equal(1111, reloaded.WindowPlacements["print"].Width);
            Assert.Equal(50, reloaded.WindowPlacements["confirm"].X);
            Assert.Equal("Light", reloaded.ThemeChoice);
        }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(root);
        }
    }

    [Fact]
    public void A_full_settings_save_carries_the_live_store_so_a_stale_copy_never_wins()
    {
        WindowPlacementService.ResetForTests(new Dictionary<string, WindowPlacementState> { ["print"] = new() { X = 1, Y = 1, Width = 900, Height = 600 } });
        WindowPlacementService.Remember("confirm", new WindowPlacementState { X = 300, Y = 200 }, persist: false);
        var stale = new GlideSettingsState();
        stale.WindowPlacements["print"] = new WindowPlacementState { X = 5, Y = 5, Width = 1500, Height = 900 };
        WindowPlacementService.ApplyTo(stale);
        Assert.Equal(900, stale.WindowPlacements["print"].Width);
        Assert.Equal(300, stale.WindowPlacements["confirm"].X);

        // Bounded: the least recently used placements are dropped.
        for (var i = 0; i < SettingsStore.MaxWindowPlacements + 10; i++)
            WindowPlacementService.Remember("k" + i, new WindowPlacementState { X = i, Y = i }, persist: false);
        Assert.Equal(SettingsStore.MaxWindowPlacements, WindowPlacementService.Snapshot().Count);
        Assert.NotNull(WindowPlacementService.Get("k" + (SettingsStore.MaxWindowPlacements + 9)));
    }
}
