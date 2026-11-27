using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Core.Workspace;
using Xunit.Abstractions;

namespace Glide.Core.Tests;

/// <summary>
/// 5.1.7 window move / resize / tab-detach cost and correctness. Headless Avalonia cannot run Windows' modal move loop, so
/// these drive the managed work that loop triggers: what every SizeChanged does (settings publish, status-bar re-layout,
/// tab-strip rebuilds, input-state reset) and what creating, placing and showing a detached window does. Timings go to the
/// test output; the assertions guard the counts (work done per resize event) and the placement, which do not depend on how
/// fast the machine is.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Window")]
public sealed class WindowMoveResizeDetachPerfTests
{
    private readonly ITestOutputHelper _output;
    public WindowMoveResizeDetachPerfTests(AvaloniaHeadlessFixture fixture, ITestOutputHelper output) { _ = fixture; _output = output; }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static object? GetField(MainWindow w, string name) => typeof(MainWindow).GetField(name, Flags)!.GetValue(w);
    private static void SetField(MainWindow w, string name, object? value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(w, value);
    private static object? Invoke(MainWindow w, string name, params object?[] args) =>
        typeof(MainWindow).GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);

    private static int s_tabs;

    private static MainWindow Create(int tabs, out WorkspaceState workspace)
    {
        var window = new MainWindow();
        SetField(window, "_settings", new GlideSettingsState());
        workspace = (WorkspaceState)GetField(window, "_workspace")!;
        s_tabs = tabs;
        Seed(workspace);
        return window;
    }

    private static void Seed(WorkspaceState workspace) =>
        workspace.Reset(Enumerable.Range(0, s_tabs).Select(_ => (TabState)new HomeTabState(Guid.NewGuid())).ToArray(), 0);

    /// <summary>Show runs the startup workspace (one Home tab); put the tabs of the scenario back and rebuild the strip.</summary>
    private static void ShowSeeded(MainWindow window, WorkspaceState workspace)
    {
        window.Show();
        Pump();
        Seed(workspace);
        Invoke(window, "RebuildTabStrip");
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    private static async Task WithIsolatedStore(Func<Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            try { await body(); }
            finally
            {
                SettingsService.ResetForTests();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        TempDir.TryDelete(root);
    }

    private static List<MainWindow> RegistryWindows() =>
        Glide.App.Platform.GlideWindowRegistry.Snapshot().OfType<MainWindow>().ToList();

    // ---- resize ------------------------------------------------------------------------------------------------------

    /// <summary>One drag of the border: the box changes on every step, exactly like WM_SIZE inside the modal size/move loop.</summary>
    private static (double Ms, int SizeEvents, int Publishes) DragBorder(MainWindow window, int steps, bool insideModalLoop)
    {
        var publishes = 0;
        var sizeEvents = 0;
        void Count(object? s, SettingsChangedEventArgs e) => publishes++;
        void OnSize(object? s, SizeChangedEventArgs e) => sizeEvents++;
        window.SizeChanged += OnSize;
        SettingsService.Changed += Count;
        try
        {
            if (insideModalLoop) Invoke(window, "BeginNativeResizeInteraction");
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < steps; i++)
            {
                window.Width = 1600 - i * 5; // 1600 -> 1000 in 5 px steps, a slow drag
                window.Height = 900 - (i % 7);
                window.UpdateLayout();
                Pump();
            }
            var ms = sw.Elapsed.TotalMilliseconds;
            if (insideModalLoop) Invoke(window, "ResetPointerStateAfterNativeResize"); // WM_EXITSIZEMOVE
            return (ms, sizeEvents, publishes);
        }
        finally
        {
            SettingsService.Changed -= Count;
            window.SizeChanged -= OnSize;
        }
    }

    [Fact]
    public async Task Resizing_inside_the_native_loop_publishes_the_settings_once_at_its_end_not_per_WM_SIZE()
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(14, out var workspace);
            try
            {
                window.Width = 1600; window.Height = 900;
                ShowSeeded(window, workspace);
                SettingsService.Publish(new GlideSettingsState());

                var (ms, sizeEvents, publishes) = DragBorder(window, 120, insideModalLoop: true);
                _output.WriteLine($"RESIZE in modal loop, 14 tabs, 120 steps: {ms:F1} ms total, {ms / 120:F2} ms/step, size events={sizeEvents}, settings publishes={publishes}");
                Assert.True(sizeEvents >= 100, $"the drag should raise a size event per step, got {sizeEvents}");
                // Before: one Publish (CloneState of the whole settings + every Changed subscriber) per size event.
                Assert.True(publishes <= 2, $"expected the final box to be published once at WM_EXITSIZEMOVE, got {publishes} publishes for {sizeEvents} size events");
                Assert.True(publishes >= 1, "the final box must still be published when the loop ends");
                Assert.Equal(window.Bounds.Width, SettingsService.Current!.WindowWidth);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Resizing_outside_the_native_loop_never_publishes_the_same_box_twice()
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(3, out _);
            try
            {
                window.Width = 1200; window.Height = 800;
                window.Show();
                Pump();
                SettingsService.Publish(new GlideSettingsState());
                var publishes = 0;
                void Count(object? s, SettingsChangedEventArgs e) => publishes++;
                SettingsService.Changed += Count;
                try
                {
                    for (var i = 0; i < 20; i++) { Invoke(window, "TrackNormalWindowPlacement"); }
                }
                finally { SettingsService.Changed -= Count; }
                Assert.True(publishes <= 1, $"an unchanged box was published {publishes} times");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_status_bar_rows_are_left_alone_while_a_resize_does_not_change_the_layout()
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(3, out _);
            try
            {
                window.Width = 1900; window.Height = 900;
                window.Show();
                Pump();
                Invoke(window, "ApplyStatusBarSize");
                var row1 = window.FindControl<StackPanel>("StatusRow1")!;
                var row2 = window.FindControl<StackPanel>("StatusRow2")!;
                var changes = 0;
                NotifyCollectionChangedEventHandler count = (_, _) => changes++;
                ((INotifyCollectionChanged)row1.Children).CollectionChanged += count;
                ((INotifyCollectionChanged)row2.Children).CollectionChanged += count;
                var before = row1.Children.ToList();
                for (var i = 0; i < 40; i++)
                {
                    window.Width = 1900 - i * 3; // far wider than the bar needs: the arrangement does not change
                    window.UpdateLayout();
                    Pump();
                }
                _output.WriteLine($"STATUS BAR rows: {changes} child-list changes over 40 resize steps");
                Assert.Equal(0, changes);
                Assert.Equal(before, row1.Children.ToList());
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static List<(double Width, double? TitleMax)> TabMetrics(MainWindow window)
    {
        var strip = window.FindControl<StackPanel>("TabStripPanel")!;
        return strip.Children.OfType<Border>().Select(b =>
        {
            var title = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            return (b.Width, title is null ? (double?)null : title.MaxWidth);
        }).ToList();
    }

    [Fact]
    public async Task Resizing_the_window_only_rebuilds_the_tab_strip_when_a_tab_changes_what_it_shows()
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(14, out var workspace);
            try
            {
                window.Width = 1900; window.Height = 900;
                ShowSeeded(window, workspace);
                var strip = window.FindControl<StackPanel>("TabStripPanel")!;
                var rebuilds = 0;
                ((INotifyCollectionChanged)strip.Children).CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset || (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?.Count > 1)) rebuilds++; };
                var drag = DragBorder(window, 120, insideModalLoop: true);
                _output.WriteLine($"TAB STRIP rebuilds during a 1600 -> 1000 px drag with 14 tabs: {rebuilds} (a window drag used to rebuild every 24 px, about 25)");
                Assert.True(drag.SizeEvents >= 100);
                Assert.True(rebuilds <= 12, $"{rebuilds} rebuilds");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(14, 1700)]
    [InlineData(14, 1500)]
    [InlineData(6, 1500)]
    [InlineData(3, 1100)]
    public async Task Resizing_the_tab_strip_in_place_gives_exactly_what_a_rebuild_gives(int tabs, int toWidth)
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(tabs, out var workspace);
            try
            {
                window.Width = 1900; window.Height = 900;
                ShowSeeded(window, workspace);
                window.Width = toWidth;
                window.UpdateLayout();
                Pump();
                var inPlace = (bool)Invoke(window, "TryResizeTabStripInPlace")!;
                var afterInPlace = TabMetrics(window);
                Invoke(window, "RebuildTabStrip");
                var afterRebuild = TabMetrics(window);
                _output.WriteLine($"{tabs} tabs, 1900 -> {toWidth}: in place={inPlace}; widths {afterRebuild.First().Width:F1}");
                if (inPlace) Assert.Equal(afterRebuild, afterInPlace);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_in_place_tab_resize_declines_when_a_tab_was_added_or_the_active_tab_changed()
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(4, out var workspace);
            try
            {
                window.Width = 1900; window.Height = 900;
                ShowSeeded(window, workspace);
                Assert.True((bool)Invoke(window, "TryResizeTabStripInPlace")!);
                workspace.Select(workspace.Tabs[2].Id);
                Assert.False((bool)Invoke(window, "TryResizeTabStripInPlace")!);
                Invoke(window, "RebuildTabStrip");
                Assert.True((bool)Invoke(window, "TryResizeTabStripInPlace")!);
                workspace.AddHome();
                Assert.False((bool)Invoke(window, "TryResizeTabStripInPlace")!);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SyncStatusRow_touches_the_panel_only_when_the_children_differ()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var panel = new StackPanel();
            var a = new Border(); var b = new Border(); var c = new Border();
            panel.Children.Add(a); panel.Children.Add(b);
            var changes = 0;
            ((INotifyCollectionChanged)panel.Children).CollectionChanged += (_, _) => changes++;
            MainWindow.SyncStatusRow(panel, new Control[] { a, b });
            Assert.Equal(0, changes);
            MainWindow.SyncStatusRow(panel, new Control[] { b, a, c });
            Assert.True(changes > 0);
            Assert.Equal(new Control[] { b, a, c }, panel.Children.ToArray());
            MainWindow.SyncStatusRow(panel, Array.Empty<Control>());
            Assert.Empty(panel.Children);
        });
    }

    [Fact]
    public void SyncStatusRows_moves_a_group_between_rows_without_a_double_parent()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var row1 = new StackPanel(); var row2 = new StackPanel();
            var a = new Border(); var b = new Border(); var c = new Border();
            row1.Children.Add(a); row1.Children.Add(b); row2.Children.Add(c);
            // c moves up to row 1 while b moves down: each still has its old parent while the other row is filled first
            MainWindow.SyncStatusRows(row1, new Control[] { a, c }, row2, new Control[] { b });
            Assert.Equal(new Control[] { a, c }, row1.Children.ToArray());
            Assert.Equal(new Control[] { b }, row2.Children.ToArray());
            var changes = 0;
            ((INotifyCollectionChanged)row1.Children).CollectionChanged += (_, _) => changes++;
            ((INotifyCollectionChanged)row2.Children).CollectionChanged += (_, _) => changes++;
            MainWindow.SyncStatusRows(row1, new Control[] { a, c }, row2, new Control[] { b });
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public async Task The_resize_input_reset_is_one_coalesced_timer_per_burst()
    {
        await WithIsolatedStore(async () =>
        {
            var window = Create(2, out _);
            try
            {
                window.Width = 1200; window.Height = 800;
                window.Show();
                Pump();
                for (var i = 0; i < 30; i++) { window.Width = 1200 - i; window.UpdateLayout(); }
                Assert.True((bool)GetField(window, "_resizeInputResetArmed")!, "a burst of size changes arms the reset");
                var deadline = Stopwatch.StartNew();
                while ((bool)GetField(window, "_resizeInputResetArmed")! && deadline.ElapsedMilliseconds < 3000)
                {
                    await Task.Delay(20);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.False((bool)GetField(window, "_resizeInputResetArmed")!, "the reset fires once the size stopped changing");
                Assert.True(deadline.ElapsedMilliseconds >= MainWindow.ResizeInputResetDelayMs - 20);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Resize_cost_per_part()
    {
        await WithIsolatedStore(() =>
        {
            var window = Create(14, out var workspace);
            try
            {
                window.Width = 1600; window.Height = 900;
                ShowSeeded(window, workspace);
                SetField(window, "_startupPlacementApplied", true);
                foreach (var name in new[] { "RebuildTabStrip", "ApplyCompactChromeLayout", "ApplyStatusBarSize", "ApplyCollapsedStatusChrome" })
                {
                    var m = typeof(MainWindow).GetMethods(Flags).First(x => x.Name == name);
                    var args = m.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                    for (var i = 0; i < 20; i++) m.Invoke(window, args);
                    var best = double.MaxValue;
                    for (var round = 0; round < 5; round++)
                    {
                        var sw = Stopwatch.StartNew();
                        for (var i = 0; i < 100; i++) m.Invoke(window, args);
                        best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1000 / 100);
                    }
                    _output.WriteLine($"PART {name}: {best:F1} us/call (best of 5 x 100)");
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- detach ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Detached_window_creation_cost()
    {
        await WithIsolatedStore(async () =>
        {
            var source = Create(6, out var workspace);
            var created = new List<MainWindow>();
            try
            {
                source.Width = 1400; source.Height = 900;
                ShowSeeded(source, workspace);
                var times = new List<double>();
                for (var i = 0; i < 5; i++)
                {
                    var id = workspace.Tabs[^1].Id;
                    var before = RegistryWindows();
                    var sw = Stopwatch.StartNew();
                    await (Task)Invoke(source, "DetachTabAsync", id, new PixelPoint(300, 300))!;
                    Pump();
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    created.AddRange(RegistryWindows().Except(before));
                }
                _output.WriteLine($"DETACH x5: {string.Join(", ", times.Select(t => t.ToString("F1")))} ms (first is cold), source tabs left={workspace.Tabs.Count}");
                Assert.Equal(1, workspace.Tabs.Count);
                Assert.Equal(5, created.Count);
            }
            finally
            {
                foreach (var w in created) { try { w.Close(); } catch { } }
                source.Close();
            }
        });
    }

    [Fact]
    public async Task A_detached_window_stays_where_it_was_placed_and_not_where_the_last_saved_placement_is()
    {
        // Repro: every MainWindow remembers the live saved placement at construction and re-applied it on the Loaded
        // dispatcher priority ("RestoreValidatedStartupNormalPosition"), so a detached window jumped to the main window's
        // saved box (usually right on top of its source) a moment after it was shown.
        await WithIsolatedStore(async () =>
        {
            SettingsService.Publish(new GlideSettingsState { RememberWindowPlacement = true, WindowX = 640, WindowY = 480, WindowWidth = 1000, WindowHeight = 700 });
            MainWindow? source = null;
            var created = new List<MainWindow>();
            try
            {
                source = Create(3, out var workspace);
                ShowSeeded(source, workspace);
                var before = RegistryWindows();
                var drop = new PixelPoint(300, 300);
                await (Task)Invoke(source, "DetachTabAsync", workspace.Tabs[^1].Id, drop)!;
                for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
                var detached = RegistryWindows().Except(before).Single();
                created.Add(detached);
                _output.WriteLine($"detached window at {detached.Position}, saved placement is 640,480, dropped at {drop}");
                Assert.NotEqual(new PixelPoint(640, 480), detached.Position);
                Assert.True(detached.Position.X <= drop.X && detached.Position.Y <= drop.Y,
                    $"the window should sit under the drop point, not at the saved placement; it is at {detached.Position}");
            }
            finally
            {
                foreach (var w in created) { try { w.Close(); } catch { } }
                source?.Close();
            }
        });
    }

    [Fact]
    public async Task A_tab_whose_transfer_failed_goes_back_where_it_was_and_is_active()
    {
        await WithIsolatedStore(async () =>
        {
            var window = Create(4, out var workspace);
            try
            {
                ShowSeeded(window, workspace);
                var ids = workspace.Tabs.Select(t => t.Id).ToArray();
                workspace.Select(ids[2]);
                var removed = (TabState)Invoke(window, "RemoveTabForTransfer", ids[1])!;
                Assert.Equal(3, workspace.Tabs.Count);
                await (Task)Invoke(window, "RestoreTransferredTabAsync", removed, 1)!;
                Assert.Equal(ids, workspace.Tabs.Select(t => t.Id).ToArray());
                Assert.Equal(ids[1], workspace.Active!.Id);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void The_held_tear_off_places_the_window_before_showing_it_and_never_re_positions_it_with_raw_pixels()
    {
        var tabs = SourceText.Read("src/Glide.App/MainWindow.Tabs.cs");
        var held = tabs[tabs.IndexOf("private async Task<bool> BeginHeldTabTearOffAsync", StringComparison.Ordinal)..];
        held = held[..held.IndexOf("private async Task BeginHeldSoleTabNativeMoveAsync", StringComparison.Ordinal)];
        Assert.True(held.IndexOf("PlaceDetachedWindow(", StringComparison.Ordinal) < held.IndexOf("ShowSecondaryWindow(", StringComparison.Ordinal),
            "the new window must be placed before its first show, not moved after it appeared at the source's box");
        var run = tabs[tabs.IndexOf("private async Task RunNativeDetachedTabDragAsync", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("private void NativeDetachedPositionChanged", StringComparison.Ordinal)];
        Assert.DoesNotContain("Position =", run); // a raw, unscaled, >= 0 override broke high-DPI and monitors left of / above the primary
    }

    [Fact]
    public void A_transferred_window_does_not_inherit_the_startup_position_restore()
    {
        var startup = SourceText.Read("src/Glide.App/MainWindow.Startup.cs");
        var ctor = startup[startup.IndexOf("internal MainWindow(TabState transferredTab", StringComparison.Ordinal)..];
        ctor = ctor[..ctor.IndexOf("public void QueueOpen(", StringComparison.Ordinal)];
        Assert.Contains("_validatedStartupNormalPosition = null;", ctor);
    }

    [Fact]
    public void The_process_icon_is_extracted_once_and_shared_by_every_window()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sw = Stopwatch.StartNew();
        var first = Glide.App.Platform.WindowsBrowserFrameController.SharedTaskbarIcons();
        var firstMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var second = Glide.App.Platform.WindowsBrowserFrameController.SharedTaskbarIcons();
        _output.WriteLine($"ICONS first call {firstMs:F2} ms (extracts {first.Big}/{first.Small}), later windows {sw.Elapsed.TotalMilliseconds * 1000:F1} us");
        Assert.Equal(first, second); // a second window gets the same handles instead of extracting (and leaking) two more
    }
}

/// <summary>The pure placement maths of a window opened for a moved tab, on desktops whose monitors have negative coordinates.</summary>
[Trait("Area", "Window")]
public sealed class DetachedWindowPlacementMultiMonitorTests
{
    [Theory]
    [InlineData(-1920, 0, -1500, 300)]   // monitor left of the primary
    [InlineData(0, -1080, 400, -700)]    // monitor above the primary
    [InlineData(-3840, -200, -3500, 100)]
    public void A_drag_out_on_a_secondary_monitor_stays_on_that_monitor_under_the_pointer(int workX, int workY, int pointerX, int pointerY)
    {
        var work = new PixelBox(workX, workY, 1920, 1040);
        var box = DetachedWindowPlacement.ForDragOut(pointerX, pointerY, 120, 22, 1200, 800, work);
        Assert.True(box.X >= work.X && box.Y >= work.Y && box.Right <= work.Right && box.Bottom <= work.Bottom, $"{box} is outside {work}");
        // not clamped to the primary monitor's origin (the old tear-off did Math.Max(0, ...)): the window sits where it was grabbed
        Assert.Equal(pointerX - 120, box.X);
    }
}
