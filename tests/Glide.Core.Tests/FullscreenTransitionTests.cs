using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// Fullscreen enter/exit (5.0.2 "leaving fullscreen feels like the app reloads"). The planner decides the native
/// steps and whether the window is hidden; the headless window tests check that the window no longer walks
/// through extra states, never touches the user's window opacity, and still returns to the remembered bounds.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class FullscreenTransitionTests
{
    public FullscreenTransitionTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- planner ---------------------------------------------------------------------------------------------

    [Fact]
    public void Maximized_origin_restores_straight_to_maximized_without_hiding_the_window()
    {
        var plan = FullscreenTransitionPlanner.PlanExit(WindowState.Maximized, FullscreenTransitionPlanner.RestoreBehavior, forceMaximized: false, nativeRestoreRectIsRememberedNormal: false);
        Assert.Equal(WindowState.Maximized, plan.TargetState);
        Assert.False(plan.StepThroughNormal);
        Assert.Equal(FullscreenTransitionFence.None, plan.Fence);
        Assert.Equal("direct", plan.Path);
    }

    [Fact]
    public void Normal_origin_restores_directly_and_hides_the_large_geometry_change()
    {
        var plan = FullscreenTransitionPlanner.PlanExit(WindowState.Normal, null, forceMaximized: false, nativeRestoreRectIsRememberedNormal: true);
        Assert.Equal(WindowState.Normal, plan.TargetState);
        Assert.False(plan.StepThroughNormal);
        Assert.Equal(FullscreenTransitionFence.Cloak, plan.Fence);
    }

    [Fact]
    public void Exit_maximized_from_a_normal_origin_is_direct_only_when_the_restore_rectangle_is_the_remembered_one()
    {
        var direct = FullscreenTransitionPlanner.PlanExit(WindowState.Normal, FullscreenTransitionPlanner.MaximizedBehavior, forceMaximized: false, nativeRestoreRectIsRememberedNormal: true);
        Assert.Equal(new FullscreenExitPlan(WindowState.Maximized, false, FullscreenTransitionFence.None), direct);

        var mismatch = FullscreenTransitionPlanner.PlanExit(WindowState.Normal, FullscreenTransitionPlanner.RestoreBehavior, forceMaximized: true, nativeRestoreRectIsRememberedNormal: false);
        Assert.Equal(WindowState.Maximized, mismatch.TargetState);
        Assert.True(mismatch.StepThroughNormal);      // legacy path: fix the restore rectangle first
        Assert.Equal(FullscreenTransitionFence.Cloak, mismatch.Fence);

        var maximizedOrigin = FullscreenTransitionPlanner.PlanExit(WindowState.Maximized, null, forceMaximized: true, nativeRestoreRectIsRememberedNormal: false);
        Assert.False(maximizedOrigin.StepThroughNormal);
    }

    [Fact]
    public void Minimized_exit_keeps_the_normal_step_so_un_minimizing_returns_a_normal_window()
    {
        foreach (var origin in new[] { WindowState.Normal, WindowState.Maximized })
        {
            var plan = FullscreenTransitionPlanner.PlanExit(origin, FullscreenTransitionPlanner.MinimizedBehavior, forceMaximized: false, nativeRestoreRectIsRememberedNormal: true);
            Assert.Equal(WindowState.Minimized, plan.TargetState);
            Assert.True(plan.StepThroughNormal);
        }
    }

    [Fact]
    public void Only_entering_from_an_ordinary_window_is_fenced()
    {
        Assert.Equal(FullscreenTransitionFence.Cloak, FullscreenTransitionPlanner.PlanEnterFence(WindowState.Normal));
        Assert.Equal(FullscreenTransitionFence.None, FullscreenTransitionPlanner.PlanEnterFence(WindowState.Maximized));
    }

    [Fact]
    public void Same_geometry_ignores_sub_pixel_dip_noise_but_not_a_real_pixel()
    {
        var p = new PixelPoint(120, 80);
        // 1251 px at 125 % is 1000.8 DIP; float noise must not count as a resize.
        Assert.True(FullscreenTransitionPlanner.SameGeometry(p, new Size(1000.8, 700), p, new Size(1000.8000000000001, 700), 1.25));
        Assert.False(FullscreenTransitionPlanner.SameGeometry(p, new Size(1000.8, 700), p, new Size(1001.6, 700), 1.25));
        Assert.False(FullscreenTransitionPlanner.SameGeometry(p, new Size(1000, 700), new PixelPoint(121, 80), new Size(1000, 700), 1.0));
        Assert.True(FullscreenTransitionPlanner.SameGeometry(p, new Size(1000, 700), p, new Size(1000, 700), double.NaN));
    }

    // ---- headless window -------------------------------------------------------------------------------------

    [Fact]
    public async Task Normal_window_fullscreen_round_trip_has_no_extra_states_keeps_opacity_and_returns_to_its_bounds()
    {
        await OnWindow(window =>
        {
            window.Width = 900; window.Height = 640;
            window.Position = new PixelPoint(140, 90);
            Dispatcher.UIThread.RunJobs();
            Invoke(window, "TrackNormalWindowPlacement");
            var beforePosition = window.Position;
            var beforeSize = window.ClientSize;
            var normalPosition = (PixelPoint)GetField(window, "_lastNormalWindowPosition")!;
            var normalSize = (Size)GetField(window, "_lastNormalWindowSize")!;
            Assert.True((bool)GetField(window, "_normalPlacementKnown")!);
            var states = RecordStates(window);
            var opacities = RecordOpacity(window);

            Invoke(window, "EnterFullscreen");
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Invoke(window, "ExitFullscreen", false);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { WindowState.FullScreen, WindowState.Normal }, states);
            Assert.Empty(opacities);                         // the old exit fence set Opacity 0 and back
            Assert.Equal(1.0, window.Opacity);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(beforePosition, window.Position);
            Assert.Equal(beforeSize, window.ClientSize);
            Assert.Equal(normalPosition, (PixelPoint)GetField(window, "_lastNormalWindowPosition")!);
            Assert.Equal(normalSize, (Size)GetField(window, "_lastNormalWindowSize")!);
            var settings = Settings(window);
            Assert.Equal(normalPosition.X, settings.WindowX);
            Assert.Equal(normalPosition.Y, settings.WindowY);
            Assert.Equal(normalSize.Width, settings.WindowWidth, 3);
            Assert.Equal(normalSize.Height, settings.WindowHeight, 3);
            Assert.False((bool)GetField(window, "_fullscreenTransitionActive")!);
        });
    }

    [Fact]
    public async Task Maximized_window_returns_straight_to_maximized_and_keeps_the_remembered_normal_bounds()
    {
        await OnWindow(window =>
        {
            window.Width = 820; window.Height = 600;
            window.Position = new PixelPoint(200, 120);
            Dispatcher.UIThread.RunJobs();
            Invoke(window, "TrackNormalWindowPlacement");
            var normalPosition = (PixelPoint)GetField(window, "_lastNormalWindowPosition")!;
            var normalSize = (Size)GetField(window, "_lastNormalWindowSize")!;
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            var states = RecordStates(window);
            var opacities = RecordOpacity(window);

            Invoke(window, "EnterFullscreen");
            Invoke(window, "ExitFullscreen", false);
            Dispatcher.UIThread.RunJobs();

            // No intermediate Normal step (it used to be FullScreen -> Normal -> Maximized: three resizes).
            Assert.Equal(new[] { WindowState.FullScreen, WindowState.Maximized }, states);
            Assert.Empty(opacities);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal(normalPosition, (PixelPoint)GetField(window, "_lastNormalWindowPosition")!);
            Assert.Equal(normalSize, (Size)GetField(window, "_lastNormalWindowSize")!);
            var settings = Settings(window);
            Assert.Equal(normalPosition.X, settings.WindowX);
            Assert.Equal(normalSize.Width, settings.WindowWidth, 3);
        });
    }

    [Fact]
    public async Task Exit_maximized_from_a_normal_window_is_a_single_state_change()
    {
        await OnWindow(window =>
        {
            window.Width = 880; window.Height = 620;
            window.Position = new PixelPoint(60, 40);
            Dispatcher.UIThread.RunJobs();
            Invoke(window, "TrackNormalWindowPlacement");
            var normalPosition = (PixelPoint)GetField(window, "_lastNormalWindowPosition")!;
            var normalSize = (Size)GetField(window, "_lastNormalWindowSize")!;
            var states = RecordStates(window);

            Invoke(window, "EnterFullscreen");
            Invoke(window, "ExitFullscreen", true);   // "Exit fullscreen maximized"
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { WindowState.FullScreen, WindowState.Maximized }, states);
            Assert.Equal(normalPosition, (PixelPoint)GetField(window, "_lastNormalWindowPosition")!);
            Assert.Equal(normalSize, (Size)GetField(window, "_lastNormalWindowSize")!);
        });
    }

    [Fact]
    public async Task Status_bar_sizing_does_not_churn_style_classes_when_nothing_changed()
    {
        await OnWindow(window =>
        {
            Invoke(window, "ApplyStatusBarSize");
            var surface = (Control)GetField(window, "ViewerStatusSurface")!;
            var changes = 0;
            surface.Classes.CollectionChanged += (_, _) => changes++;
            Invoke(window, "ApplyStatusBarSize");
            Invoke(window, "ApplyStatusBarSize");
            Assert.Equal(0, changes);
            Assert.Contains("statusSmall", surface.Classes);

            Settings(window).StatusBarSize = "Large";
            Invoke(window, "ApplyStatusBarSize");
            Assert.Contains("statusLarge", surface.Classes);
            Assert.DoesNotContain("statusSmall", surface.Classes);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static List<WindowState> RecordStates(Window window)
    {
        var states = new List<WindowState>();
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) states.Add(window.WindowState); };
        return states;
    }

    private static List<double> RecordOpacity(Window window)
    {
        var values = new List<double>();
        window.PropertyChanged += (_, e) => { if (e.Property == Visual.OpacityProperty) values.Add(window.Opacity); };
        return values;
    }

    private static async Task OnWindow(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Fullscreen.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                SetField(window, "_settings", new GlideSettingsState());
                window.Show();
                for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
                ((WorkspaceState)GetField(window, "_workspace")!).Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
                // Placement tracking only starts once the startup placement has been applied.
                SetField(window, "_startupPlacementApplied", true);
                body(window);
            }
            finally
            {
                window.ActionInterceptorForTests = (_, _) => true;
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        try { Directory.Delete(root, true); } catch (IOException) { /* temp folder cleanup is best effort */ }
    }

    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var candidates = typeof(MainWindow).GetMethods(Flags).Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();
        Assert.True(candidates.Length > 0, method);
        return candidates[0].Invoke(window, args);
    }

    private static object? GetField(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return field!.GetValue(window);
    }

    private static void SetField(MainWindow window, string name, object? value)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }
}
