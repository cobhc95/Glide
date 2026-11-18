using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// The Settings window remembers the size, position and maximized state the user leaves it with (also when
/// closed with OK, Cancel or the close button), restores them on the next opening, validates them against the
/// current monitors, and uses the compact default only when nothing is saved.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Settings")]
public sealed class SettingsWindowPlacementTests
{
    public SettingsWindowPlacementTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly SettingsScreenArea Full = new(new PixelRect(0, 0, 1920, 1040), 1.0);
    private static readonly SettingsScreenArea Laptop = new(new PixelRect(0, 0, 1366, 728), 1.5);
    private static readonly SettingsScreenArea Second = new(new PixelRect(1920, 0, 2560, 1400), 1.0);

    private static SettingsWindowPlacement Saved(int x, int y, double w, double h, bool max = false) => new(x, y, w, h, max);

    // ---- resolver (pure) ------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_saved_means_no_restore()
    {
        Assert.Null(SettingsWindow.ResolveSavedPlacement(SettingsWindowPlacement.None, new[] { Full }, Full));
        Assert.Null(SettingsWindow.ResolveSavedPlacement(Saved(100, 100, 0, 0), new[] { Full }, Full));
        Assert.False(SettingsWindowPlacement.From(new GlideSettingsState()).HasSize);
    }

    [Fact]
    public void A_saved_size_and_position_inside_the_screen_are_restored_exactly()
    {
        var r = SettingsWindow.ResolveSavedPlacement(Saved(200, 120, 1100, 700), new[] { Full }, Full)!.Value;
        Assert.Equal(new Size(1100, 700), r.Size);
        Assert.Equal(new PixelPoint(200, 120), r.Position);
    }

    [Fact]
    public void A_size_saved_with_the_old_1040x760_default_is_a_normal_saved_size()
    {
        var r = SettingsWindow.ResolveSavedPlacement(Saved(300, 100, 1040, 760), new[] { Full }, Full)!.Value;
        Assert.Equal(new Size(1040, 760), r.Size);
        Assert.Equal(new PixelPoint(300, 100), r.Position);
    }

    [Fact]
    public void A_saved_size_larger_than_the_screen_is_clamped_to_the_working_area()
    {
        var r = SettingsWindow.ResolveSavedPlacement(Saved(0, 0, 4000, 3000), new[] { Laptop }, Laptop)!.Value;
        Assert.True(r.Size.Width <= Laptop.Work.Width / 1.5, $"{r.Size.Width}");
        Assert.True(r.Size.Height <= Laptop.Work.Height / 1.5, $"{r.Size.Height}");
        Assert.True(r.Minimum.Width <= r.Size.Width && r.Minimum.Height <= r.Size.Height);
        var outer = new PixelRect(r.Position!.Value, new PixelSize((int)(r.Size.Width * 1.5), (int)(r.Size.Height * 1.5)));
        Assert.True(Laptop.Work.Contains(outer.TopLeft) && outer.Right <= Laptop.Work.Right && outer.Bottom <= Laptop.Work.Bottom);
    }

    [Fact]
    public void A_window_partly_off_the_screen_is_moved_fully_inside()
    {
        var r = SettingsWindow.ResolveSavedPlacement(Saved(1500, 900, 930, 630), new[] { Full }, Full)!.Value;
        Assert.True(r.Position!.Value.X + r.Size.Width <= Full.Work.Right);
        Assert.True(r.Position!.Value.Y + r.Size.Height <= Full.Work.Bottom);
        var negative = SettingsWindow.ResolveSavedPlacement(Saved(-300, 20, 930, 630), new[] { Full }, Full)!.Value;
        Assert.Equal(new PixelPoint(0, 20), negative.Position);
        // Title bar entirely above the screen cannot be grabbed: treated as off-screen.
        Assert.Null(SettingsWindow.ResolveSavedPlacement(Saved(100, -400, 930, 630), new[] { Full }, Full)!.Value.Position);
    }

    [Fact]
    public void A_window_entirely_off_screen_keeps_its_size_but_loses_its_position_so_it_is_centred()
    {
        var r = SettingsWindow.ResolveSavedPlacement(Saved(6000, 4000, 1000, 700), new[] { Full }, Full)!.Value;
        Assert.Equal(new Size(1000, 700), r.Size);
        Assert.Null(r.Position);
    }

    [Fact]
    public void A_window_saved_on_a_monitor_that_is_gone_is_centred_but_kept_when_the_monitor_is_present()
    {
        var saved = Saved(2200, 150, 1000, 700);
        var gone = SettingsWindow.ResolveSavedPlacement(saved, new[] { Full }, Full)!.Value;
        Assert.Null(gone.Position);
        var present = SettingsWindow.ResolveSavedPlacement(saved, new[] { Full, Second }, Full)!.Value;
        Assert.Equal(new PixelPoint(2200, 150), present.Position);
        Assert.Equal(new Size(1000, 700), present.Size);
    }

    [Fact]
    public void The_minimum_size_is_respected_for_tiny_saved_sizes()
    {
        var r = SettingsWindow.ResolveSavedPlacement(Saved(100, 100, 200, 120), new[] { Full }, Full)!.Value;
        Assert.Equal(SettingsWindow.MinimumWidth, r.Size.Width);
        Assert.Equal(SettingsWindow.MinimumHeight, r.Size.Height);
        Assert.Equal(new Size(SettingsWindow.MinimumWidth, SettingsWindow.MinimumHeight), r.Minimum);
        // Garbage never crashes.
        Assert.Null(SettingsWindow.ResolveSavedPlacement(Saved(0, 0, double.NaN, 500), new[] { Full }, Full));
    }

    // ---- state ----------------------------------------------------------------------------------------------

    [Fact]
    public void Placement_is_part_of_the_state_clone_equality_merge_and_json_roundtrip()
    {
        var state = new GlideSettingsState();
        Assert.False(SettingsWindowPlacement.From(state).HasSize);
        Assert.False(SettingsWindowPlacement.From(state).HasPosition);

        var changed = state.CloneState();
        new SettingsWindowPlacement(321, 123, 987.5, 654.5, true).WriteTo(changed);
        Assert.Equal(new SettingsWindowPlacement(321, 123, 987.5, 654.5, true), SettingsWindowPlacement.From(changed.CloneState()));
        Assert.False(state.ContentEquals(changed));
        Assert.True(changed.ContentEquals(changed.CloneState()));

        var hydrated = state.CloneState();
        hydrated.CacheItems = 33;
        var merged = Glide.App.Settings.SettingsStore.MergeHydratedPreservingEdits(state, changed, hydrated);
        Assert.Equal(new SettingsWindowPlacement(321, 123, 987.5, 654.5, true), SettingsWindowPlacement.From(merged));
        Assert.Equal(33, merged.CacheItems);

        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Glide.App.Settings.SettingsStore.SettingsDirectoryOverrideForTests.Value;
        Glide.App.Settings.SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try
        {
            Glide.App.Settings.SettingsStore.Save(changed);
            var loaded = Glide.App.Settings.SettingsStore.Load();
            Assert.Equal(new SettingsWindowPlacement(321, 123, 987.5, 654.5, true), SettingsWindowPlacement.From(loaded));
            // A settings file written before this change has no placement: nothing saved, default size applies.
            Glide.App.Settings.SettingsStore.Save(new GlideSettingsState());
            Assert.False(SettingsWindowPlacement.From(Glide.App.Settings.SettingsStore.Load()).HasSize);
        }
        finally
        {
            Glide.App.Settings.SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            TempDir.TryDelete(root);
        }
    }

    // ---- window ---------------------------------------------------------------------------------------------

    private static GlideSettingsState StateWith(SettingsWindowPlacement placement)
    {
        var state = new GlideSettingsState();
        placement.WriteTo(state);
        return state;
    }

    /// <summary>The headless window applies a new Width/Height over two layout passes.</summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 2; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static T Field<T>(SettingsWindow window, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void Invoke(SettingsWindow window, string name, params object[] args) =>
        typeof(SettingsWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    [Fact]
    public async Task Opens_at_the_compact_default_when_nothing_is_saved()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            Assert.Equal(SettingsWindow.DefaultWidth, window.Width);
            Assert.Equal(SettingsWindow.DefaultHeight, window.Height);
            Assert.Equal(SettingsWindow.MinimumWidth, window.MinWidth);
            Assert.Equal(SettingsWindow.MinimumHeight, window.MinHeight);
            Assert.Equal(WindowState.Normal, window.WindowState);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Restores_the_saved_size_and_position_when_opened()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(StateWith(Saved(210, 130, 1010, 690)), (_, _) => { });
            Assert.Equal(1010, window.Width);
            Assert.Equal(690, window.Height);
            window.Show();
            try
            {
                Assert.Equal(1010, window.Width);
                Assert.Equal(690, window.Height);
                Assert.Equal(new PixelPoint(210, 130), window.Position);
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Clamps_a_saved_off_screen_or_oversized_placement_and_respects_the_minimum_size()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var huge = new SettingsWindow(StateWith(Saved(9000, 9000, 20000, 20000)), (_, _) => { });
            Assert.True(huge.Width < 20000 && huge.Height < 20000, $"{huge.Width}x{huge.Height}");
            Assert.True(huge.MinWidth <= huge.Width && huge.MinHeight <= huge.Height);

            var tiny = new SettingsWindow(StateWith(Saved(10, 10, 100, 100)), (_, _) => { });
            Assert.Equal(SettingsWindow.MinimumWidth, tiny.Width);
            Assert.Equal(SettingsWindow.MinimumHeight, tiny.Height);
            Assert.Equal(SettingsWindow.MinimumWidth, tiny.MinWidth);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Saves_the_size_and_position_when_closed_with_the_close_button_and_restores_them_on_reopen()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            SettingsWindowPlacement? seen = null;
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { PlacementChanged = p => seen = p };
            window.Show();
            window.Width = 1111;
            window.Height = 650;
            window.Position = new PixelPoint(137, 91);
            Settle(window);
            window.Close();

            Assert.NotNull(seen);
            Assert.Equal(1111, seen!.Value.Width, 1.0);
            Assert.Equal(650, seen.Value.Height, 1.0);
            Assert.Equal(137, seen.Value.X);
            Assert.Equal(91, seen.Value.Y);
            Assert.False(seen.Value.Maximized);

            // Reopen with what the owner stored.
            var state = new GlideSettingsState();
            seen.Value.WriteTo(state);
            var again = new SettingsWindow(state, (_, _) => { });
            again.Show();
            try
            {
                Assert.Equal(1111, again.Width, 1.0);
                Assert.Equal(650, again.Height, 1.0);
                Assert.Equal(new PixelPoint(137, 91), again.Position);
            }
            finally { again.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Saves_the_placement_when_closed_with_OK_and_with_Cancel_too()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            foreach (var handler in new[] { "OkClicked", "CancelClicked" })
            {
                SettingsWindowPlacement? seen = null;
                var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { }) { PlacementChanged = p => seen = p };
                window.Show();
                window.Width = 980;
                window.Height = 600;
                window.Position = new PixelPoint(55, 66);
                Settle(window);
                Invoke(window, handler, window, new RoutedEventArgs());
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(seen is not null, $"{handler} did not report the placement");
                Assert.Equal(980, seen!.Value.Width, 1.0);
                Assert.Equal(600, seen.Value.Height, 1.0);
                Assert.Equal(new PixelPoint(55, 66), new PixelPoint(seen.Value.X, seen.Value.Y));
                // The committed state a Cancel rollback re-applies carries the same placement, so it cannot bring back a stale one.
                Assert.Equal(seen.Value, SettingsWindowPlacement.From(Field<GlideSettingsState>(window, "_committed")));
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Maximized_state_is_restored_and_the_normal_size_is_kept_for_later()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            SettingsWindowPlacement? seen = null;
            var window = new SettingsWindow(StateWith(Saved(150, 100, 1000, 680, max: true)), (_, _) => { }) { PlacementChanged = p => seen = p };
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.Close();

            Assert.NotNull(seen);
            Assert.True(seen!.Value.Maximized);
            // The remembered size is the restored (normal) size, not the maximized one.
            Assert.Equal(1000, seen.Value.Width, 1.0);
            Assert.Equal(680, seen.Value.Height, 1.0);
            Assert.Equal(150, seen.Value.X);
            Assert.Equal(100, seen.Value.Y);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_window_that_was_not_maximized_is_not_restored_maximized()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(StateWith(Saved(150, 100, 1000, 680, max: false)), (_, _) => { });
            window.Show();
            try { Assert.Equal(WindowState.Normal, window.WindowState); }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
