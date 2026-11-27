using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pointer = Avalonia.Input.Pointer;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>Whole-app Overlay mode fixes from Vertigo220 round 3 (#20-#28).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Overlay")]
public sealed class OverlayRound3Tests
{
    public OverlayRound3Tests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- #27 interaction toggle ------------------------------------------------------------------------------

    [Fact]
    public async Task Interaction_menu_item_is_a_real_toggle_with_a_tick_only_when_on()
    {
        await OnWindow(window =>
        {
            Invoke(window, "EnterWholeAppOverlayMode", "test");
            var item = FindTagged(window, "setting.overlay.wholeAppWindowedInteractions");
            Assert.False(Settings(window).WholeAppOverlayUseWindowedInteractions);
            Assert.DoesNotContain("✓", item.Header?.ToString());

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Settings(window).WholeAppOverlayUseWindowedInteractions);
            var after = FindTagged(window, "setting.overlay.wholeAppWindowedInteractions");
            Assert.Contains("✓", after.Header?.ToString());

            after.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(Settings(window).WholeAppOverlayUseWindowedInteractions);
            Assert.DoesNotContain("✓", FindTagged(window, "setting.overlay.wholeAppWindowedInteractions").Header?.ToString());
        });
    }

    // ---- #26 window kept inside the work area ---------------------------------------------------------------

    private static readonly PixelBox WorkArea = new(0, 0, 1920, 1032); // 1080p with a 48 px taskbar

    [Theory]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void Large_sizes_never_push_the_window_below_the_work_area(int percent)
    {
        // A window near the bottom of the screen, picture 1200 x 900 at 100 % display scaling.
        var current = new PixelBox(600, 600, 400, 300);
        var box = OverlayWindowSizing.ForPercent(percent, 1200, 900, WorkArea, 1.0, current);
        Assert.True(box.Bottom <= WorkArea.Bottom, $"bottom {box.Bottom}");
        Assert.True(box.Right <= WorkArea.Right && box.X >= 0 && box.Y >= 0);
    }

    [Fact]
    public void Keep_inside_moves_and_shrinks_an_off_screen_window()
    {
        var moved = OverlayWindowSizing.KeepInside(new PixelBox(100, 900, 800, 600), WorkArea);
        Assert.Equal(new PixelBox(100, 432, 800, 600), moved);
        var shrunk = OverlayWindowSizing.KeepInside(new PixelBox(0, 0, 2400, 1800), WorkArea);
        Assert.True(shrunk.Width <= 1920 && shrunk.Height <= 1032);
        Assert.Equal(1376, shrunk.Width); // aspect 4:3 kept
    }

    [Fact]
    public async Task Overlay_opening_at_a_remembered_spot_below_the_taskbar_is_pulled_back_on_screen()
    {
        await OnWindow(window =>
        {
            var area = window.Screens.Primary!.WorkingArea;
            SetField(window, "_currentPath", @"C:\pictures\tall.png");
            SetField(window, "_currentPixelWidth", 800);
            SetField(window, "_currentPixelHeight", 600);
            var settings = Settings(window);
            settings.OverlayWindowPlacements = new Dictionary<string, OverlayWindowPlacementState>(StringComparer.OrdinalIgnoreCase)
            {
                // Saved after the window was dragged (or sized) so its lower half sat under the taskbar.
                [Path.GetFullPath(@"C:\pictures\tall.png")] = new() { X = area.X + 100, Y = area.Bottom - 150, Width = 400, Height = 300 }
            };
            Invoke(window, "EnterWholeAppOverlayMode", "test");
            var scale = window.RenderScaling;
            var bottom = window.Position.Y + (int)Math.Round(window.Height * scale);
            Assert.True(bottom <= area.Bottom, $"window bottom {bottom} > work area bottom {area.Bottom}");
        });
    }

    // ---- #25 Size menu tick ----------------------------------------------------------------------------------

    [Fact]
    public async Task Size_menu_ticks_the_current_size_only()
    {
        await OnWindow(window =>
        {
            SetField(window, "_currentPath", @"C:\pictures\size.png");
            SetField(window, "_currentPixelWidth", 800);
            SetField(window, "_currentPixelHeight", 600);
            Invoke(window, "EnterWholeAppOverlayMode", "test");
            Invoke(window, "SetWholeAppOverlaySize", 50);
            var ticked = SizeMenu(window).Items.OfType<MenuItem>().Where(i => i.Header?.ToString()?.Contains('\u2713') == true).Select(i => i.Header!.ToString()).ToArray();
            Assert.Equal(new[] { "50%  \u2713" }, ticked);

            // A size the menu does not offer (window resized by its border) ticks nothing.
            window.Width = 333;
            Assert.DoesNotContain(SizeMenu(window).Items.OfType<MenuItem>(), i => i.Header?.ToString()?.Contains('\u2713') == true);
        });
    }

    [Fact]
    public void Current_percent_matches_capped_choices_and_prefers_the_last_pick()
    {
        // 1200 x 900 on a 1920 x 1032 work area: 125 %, 150 % and 200 % all cap to 1376 x 1032.
        var capped = OverlayWindowSizing.ForPercent(200, 1200, 900, WorkArea, 1.0, new PixelBox(0, 0, 600, 450));
        Assert.Equal(125, OverlayWindowSizing.CurrentPercent(capped, 1200, 900, WorkArea, 1.0));
        Assert.Equal(200, OverlayWindowSizing.CurrentPercent(capped, 1200, 900, WorkArea, 1.0, lastChosen: 200));
        Assert.Equal(50, OverlayWindowSizing.CurrentPercent(new PixelBox(0, 0, 600, 450), 1200, 900, WorkArea, 1.0));
        Assert.Null(OverlayWindowSizing.CurrentPercent(new PixelBox(0, 0, 700, 525), 1200, 900, WorkArea, 1.0));
    }

    // ---- #21 / #22 opacity slider ------------------------------------------------------------------------------

    [Fact]
    public async Task Opacity_slider_click_keeps_the_value_and_drag_tracks_the_dot_down_to_the_minimum()
    {
        await OnOverlayWithPicture(window =>
        {
            var (host, hit) = ChromeHost(window);
            SetOpacity(window, 0.6);
            var slider = Glide.App.Controls.OverlayChromePolicy.SliderRect(host.Bounds.Size);
            var x = slider.Center.X;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);

            // Clicking on the dot (where it is drawn) must not make it jump.
            Raise(window, hit, host, pointer, InputElement.PointerPressedEvent, new Point(x, DotY(window, 0.6)));
            Assert.InRange(Opacity(window), 0.57, 0.63);

            // Dragging follows the pointer: halfway between 60 % and the bottom of the line, then below the pill.
            var target = (DotY(window, 0.6) + DotY(window, 0.35)) / 2;
            Raise(window, hit, host, pointer, InputElement.PointerMovedEvent, new Point(x, target));
            Assert.InRange(DotY(window, Opacity(window)), target - 3, target + 3);
            Raise(window, hit, host, pointer, InputElement.PointerMovedEvent, new Point(x, slider.Bottom + 40));
            Assert.Equal(0.35, Opacity(window), 2);
            Raise(window, hit, host, pointer, InputElement.PointerReleasedEvent, new Point(x, slider.Bottom + 40));
        });
    }

    [Fact]
    public async Task Mouse_wheel_over_the_opacity_slider_zooms_the_picture_like_a_picture_in_picture_overlay()
    {
        await OnOverlayWithPicture(window =>
        {
            var (host, hit) = ChromeHost(window);
            SetOpacity(window, 0.6);
            var viewport = window.FindControl<Glide.App.Controls.ImageViewport>("Viewport")!;
            var center = Glide.App.Controls.OverlayFrameGeometry.Slider(new Rect(default, host.Bounds.Size)).Center;
            Assert.Equal(Glide.App.Controls.OverlayFrameGeometry.Slider(new Rect(default, host.Bounds.Size)), hit.Bounds);   // grab area = drawn pill
            var zoomBefore = viewport.ZoomPercent;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            Raise(window, hit, host, pointer, InputElement.PointerWheelChangedEvent, center, wheel: 1);
            Assert.Equal(0.6, Opacity(window), 2);   // the wheel no longer steps the opacity
            Assert.NotEqual(zoomBefore, viewport.ZoomPercent);
        });
    }

    /// <summary>Headless hit testing does not see the transparent Overlay scene, so raise the input on the hit target.</summary>
    private static void Raise(MainWindow window, Control target, Control host, Pointer pointer, RoutedEvent routed, Point hostPoint, double wheel = 0)
    {
        var p = host.TranslatePoint(hostPoint, window)!.Value;
        var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton,
            routed == InputElement.PointerPressedEvent ? PointerUpdateKind.LeftButtonPressed : PointerUpdateKind.Other);
        RoutedEventArgs args = routed == InputElement.PointerPressedEvent
            ? new PointerPressedEventArgs(target, pointer, window, p, 0, pressed, KeyModifiers.None) { RoutedEvent = routed }
            : routed == InputElement.PointerReleasedEvent
                ? new PointerReleasedEventArgs(target, pointer, window, p, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left) { RoutedEvent = routed }
                : routed == InputElement.PointerWheelChangedEvent
                    ? new PointerWheelEventArgs(target, pointer, window, p, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, new Vector(0, wheel)) { RoutedEvent = routed }
                    : new PointerEventArgs(routed, target, pointer, window, p, 0, pressed, KeyModifiers.None);
        target.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
    }
    private static double DotY(MainWindow window, double opacity)
    {
        var host = (Control)GetField(window, "_wholeAppOverlayControlsHost")!;
        var slider = Glide.App.Controls.OverlayChromePolicy.SliderRect(host.Bounds.Size);
        return Glide.App.Controls.OverlayChromePolicy.WholeWindowOpacityTrackY(slider, opacity);
    }

    private static double Opacity(MainWindow window) => (double)GetField(window, "_sessionOpacity")!;

    private static void SetOpacity(MainWindow window, double value)
    {
        window.FindControl<Slider>("TransparencySlider")!.Value = value * 100;
        Dispatcher.UIThread.RunJobs();
    }

    private static (Control Host, Control OpacityHit) ChromeHost(MainWindow window)
    {
        var host = (Panel)GetField(window, "_wholeAppOverlayControlsHost")!;
        host.IsVisible = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var slider = Glide.App.Controls.OverlayChromePolicy.SliderRect(host.Bounds.Size);
        // The control under the slider's centre (an Avalonia Slider before round 3 #22, a hit Border after).
        var hit = host.Children.Where(c => c.ZIndex == 2 && c.Bounds.Contains(slider.Center)).Single();
        return (host, hit);
    }
    internal static async Task OnOverlayWithPicture(Action<MainWindow> body)
    {
        await OnWindow(window =>
        {
            window.Width = 900;
            window.Height = 700;
            var viewport = window.FindControl<Glide.App.Controls.ImageViewport>("Viewport")!;
            viewport.Bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(800, 600), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            SetField(window, "_currentPath", @"C:\pictures\overlay.png");
            SetField(window, "_currentPixelWidth", 800);
            SetField(window, "_currentPixelHeight", 600);
            Invoke(window, "EnterWholeAppOverlayMode", "test");
            window.FindControl<Control>("ImageView")!.IsVisible = true;   // the Home tab hides the viewer
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            body(window);
        });
    }

    // ---- #24 sub-menu opened under a still pointer --------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Size_submenu_highlights_the_item_under_a_still_pointer_when_it_opens(bool synced)
    {
        var previous = SubmenuHoverSync.CursorPosition;
        try
        {
            await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
            {
                var window = new Window { Width = 500, Height = 400 };
                window.Styles.Insert(0, new Avalonia.Themes.Fluent.FluentTheme());
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    var size = new MenuItem { Header = "Size" };
                    foreach (var percent in OverlayWindowSizing.SizePercents) size.Items.Add(new MenuItem { Header = $"{percent}%" });
                    var menu = new ContextMenu { ItemsSource = new object[] { new MenuItem { Header = "Always on top" }, size } };
                    if (synced) SubmenuHoverSync.Attach(menu);
                    var target = size.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "75%");
                    // The pointer rests where the 75 % item appears; no pointer move reaches the new popup.
                    SubmenuHoverSync.CursorPosition = () => target.Bounds.Width > 0 && TopLevel.GetTopLevel(target) is not null
                        ? target.PointToScreen(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2))
                        : null;
                    menu.Open(window);
                    Dispatcher.UIThread.RunJobs();
                    size.IsSubMenuOpen = true;
                    for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
                    Assert.True(target.Bounds.Width > 0, "test setup: the sub-menu must be laid out");
                    // Without the sync nothing is highlighted (the bug); with it, exactly the item under the pointer.
                    Assert.Equal(synced, target.IsSelected);
                    Assert.Equal(synced ? 1 : 0, size.Items.OfType<MenuItem>().Count(m => m.IsSelected));
                    menu.Close();
                }
                finally { window.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { SubmenuHoverSync.CursorPosition = previous; }
    }
    // ---- #20 moving the Overlay window ----------------------------------------------------------------------------

    [Theory]
    // zoomed, alt, handle, locked -> expected
    [InlineData(false, false, false, false, OverlayLeftDrag.MoveWindow)]  // picture fits: drag anywhere moves
    [InlineData(true, false, false, false, OverlayLeftDrag.Pan)]          // zoomed: drag pans
    [InlineData(true, true, false, false, OverlayLeftDrag.MoveWindow)]    // zoomed + Alt moves
    [InlineData(true, false, true, false, OverlayLeftDrag.MoveWindow)]    // zoomed, on bottom bar / drag strip moves
    [InlineData(false, false, false, true, OverlayLeftDrag.Nothing)]      // locked: no move
    [InlineData(false, true, true, true, OverlayLeftDrag.Nothing)]        // locked beats Alt and handles
    [InlineData(true, true, true, true, OverlayLeftDrag.Pan)]             // locked + zoomed still pans
    public void Drag_policy_follows_the_approved_decision(bool zoomed, bool alt, bool handle, bool locked, OverlayLeftDrag expected) =>
        Assert.Equal(expected, OverlayWindowDragPolicy.Resolve(zoomed, alt, handle, locked));

    [Fact]
    public void Window_drag_follows_the_cursor_in_screen_space_on_both_axes()
    {
        // Below the threshold nothing moves.
        Assert.Null(OverlayWindowDragPolicy.DragTarget(500, 300, 900, 700, 902, 701, alreadyMoving: false));
        // A (-100,-50) drag moves both axes (the reported bug moved only X, then stalled).
        Assert.Equal((400, 250), OverlayWindowDragPolicy.DragTarget(500, 300, 900, 700, 800, 650, alreadyMoving: false));
        // Once moving, the window keeps following; deltas come from the start, so moving the window never cancels them.
        Assert.Equal((501, 300), OverlayWindowDragPolicy.DragTarget(500, 300, 900, 700, 901, 700, alreadyMoving: true));
        Assert.Equal((1100, 700), OverlayWindowDragPolicy.DragTarget(500, 300, 900, 700, 1500, 1100, alreadyMoving: true));
    }

    [Fact]
    public void Overlay_window_drag_does_not_use_the_native_move_loop()
    {
        // WM_ENTERSIZEMOVE hides the Overlay controls host and releases capture, which ended the native loop after a few pixels.
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Glide.App", "MainWindow.Overlays.cs"));
        Assert.DoesNotContain("BeginNativeWindowDrag(", source);
        Assert.Contains("BeginOverlayWindowDrag(e)", source);
    }

    [Fact]
    public void Drag_pill_is_centred_inset_from_the_top_and_shrinks()
    {
        var pill = Glide.App.Controls.OverlayChromePolicy.DragStripRect(new Rect(10, 20, 600, 400))!.Value;
        Assert.Equal(new Rect(10 + 220, 20 + 28, 160, 22), pill);
        var small = Glide.App.Controls.OverlayChromePolicy.DragStripRect(new Rect(0, 0, 180, 200))!.Value;
        Assert.Equal(88, small.Width);
        Assert.Equal(46, small.X);
        Assert.Null(Glide.App.Controls.OverlayChromePolicy.DragStripRect(new Rect(0, 0, 120, 200)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Glide.App"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public async Task Left_drag_moves_the_window_when_the_picture_fits_and_pans_when_zoomed()
    {
        await OnOverlayWithPicture(window =>
        {
            var (host, _) = ChromeHost(window);
            var viewport = window.FindControl<Glide.App.Controls.ImageViewport>("Viewport")!;
            var moves = 0;
            window.NativeWindowDragInterceptorForTests = _ => { moves++; return true; };
            var middle = new Point(host.Bounds.Width / 2, host.Bounds.Height / 2);
            var bottomBar = new Point(40, host.Bounds.Height - 20);
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);

            _dest = new Rect(0, 0, viewport.Bounds.Width, viewport.Bounds.Height);   // fits
            Assert.True(PressOn(window, viewport, host, pointer, middle), "fits: the press moves the window");
            Assert.Equal(1, moves);

            _dest = new Rect(-400, -300, viewport.Bounds.Width * 2, viewport.Bounds.Height * 2);   // zoomed
            Assert.False(PressOn(window, viewport, host, pointer, middle), "zoomed: the press is left to the viewport to pan");
            Assert.Equal(1, moves);
            Assert.True(PressOn(window, viewport, host, pointer, middle, KeyModifiers.Alt), "zoomed + Alt moves");
            Assert.True(PressOn(window, viewport, host, pointer, bottomBar), "zoomed: the bottom bar moves");
            Assert.Equal(3, moves);

            // Lock position: nothing moves the window.
            Settings(window).WholeAppOverlayLockPosition = true;
            Assert.False(PressOn(window, viewport, host, pointer, bottomBar));
            Assert.False(PressOn(window, viewport, host, pointer, middle, KeyModifiers.Alt));
            _dest = new Rect(0, 0, viewport.Bounds.Width, viewport.Bounds.Height);
            PressOn(window, viewport, host, pointer, middle);
            Assert.Equal(3, moves);
        });
    }

    [Theory]
    // over, dragging, ms since left -> opacity
    [InlineData(true, false, 99999.0, 1.0)]     // pointer over the Overlay: shown
    [InlineData(false, true, 99999.0, 1.0)]     // dragging: never hides
    [InlineData(false, false, 0.0, 1.0)]        // just left: lingers
    [InlineData(false, false, 2500.0, 1.0)]     // still lingering at the delay
    [InlineData(false, false, 2650.0, 0.5)]     // half-way through the 300 ms fade
    [InlineData(false, false, 2800.0, 0.0)]     // gone
    public void Drag_handle_lingers_then_fades(bool over, bool dragging, double ms, double expected) =>
        Assert.Equal(expected, Glide.App.Controls.OverlayChromePolicy.DragStripOpacity(true, true, over, dragging, ms), 3);

    [Fact]
    public void Drag_handle_is_hidden_when_off_or_too_small() 
    {
        Assert.Equal(0, Glide.App.Controls.OverlayChromePolicy.DragStripOpacity(false, true, true, false, 0));
        Assert.Equal(0, Glide.App.Controls.OverlayChromePolicy.DragStripOpacity(true, false, true, false, 0));
    }

    [Fact]
    public async Task Pressing_the_drag_handle_moves_the_window_even_when_zoomed_and_it_stays_after_leave()
    {
        await OnOverlayWithPicture(window =>
        {
            var (host, _) = ChromeHost(window);
            var viewport = window.FindControl<Glide.App.Controls.ImageViewport>("Viewport")!;
            Settings(window).WholeAppOverlayShowDragStrip = true;
            long now = 10_000;
            window.DragStripClockForTests = () => now;
            Invoke(window, "UpdateWholeAppOverlayDragStrip", true);
            var pill = (Border)GetField(window, "_wholeAppOverlayDragStrip")!;
            Assert.True(pill.IsHitTestVisible);

            _dest = new Rect(-400, -300, viewport.Bounds.Width * 2, viewport.Bounds.Height * 2);   // zoomed: body would pan
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            Assert.True(PressOn(window, pill, pill, pointer, new Point(pill.Bounds.Width / 2 + 1, 11)), "the handle moves the window");

            // Leaving: still there (and hit-testable) through the delay, then fades out.
            Invoke(window, "UpdateWholeAppOverlayDragStrip", false);
            now += 2000; Invoke(window, "RefreshWholeAppOverlayDragStrip");
            Assert.Equal(1, pill.Opacity);
            Assert.True(pill.IsHitTestVisible);
            now += 1000; Invoke(window, "RefreshWholeAppOverlayDragStrip");
            Assert.Equal(0, pill.Opacity);
            Assert.False(pill.IsHitTestVisible);
            Invoke(window, "UpdateWholeAppOverlayDragStrip", true);
            Assert.Equal(1, pill.Opacity);

            Settings(window).WholeAppOverlayLockPosition = true;
            Assert.False(PressOn(window, pill, pill, pointer, new Point(pill.Bounds.Width / 2 + 1, 11)), "Lock position");
        });
    }

    [Fact]
    public async Task Drag_strip_and_lock_position_are_remembered_menu_toggles_and_the_strip_follows_the_pointer()
    {
        await OnOverlayWithPicture(window =>
        {
            var settings = Settings(window);
            Assert.False(settings.WholeAppOverlayShowDragStrip);
            Assert.False(settings.WholeAppOverlayLockPosition);

            var strip = FindTagged(window, "setting.overlay.wholeAppShowDragStrip");
            Assert.Equal("Show drag strip", strip.Header?.ToString());
            strip.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(settings.WholeAppOverlayShowDragStrip);
            Assert.Contains("✓", FindTagged(window, "setting.overlay.wholeAppShowDragStrip").Header?.ToString());

            var lockItem = FindTagged(window, "setting.overlay.wholeAppLockPosition");
            lockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(settings.WholeAppOverlayLockPosition);
            Assert.Contains("✓", FindTagged(window, "setting.overlay.wholeAppLockPosition").Header?.ToString());

            // The handle: a 22 px pill inset below the top edge, shown while the pointer is over the Overlay, faded out when it leaves.
            Invoke(window, "UpdateWholeAppOverlayDragStrip", true);
            var bar = (Border)GetField(window, "_wholeAppOverlayDragStrip")!;
            Assert.Equal(22, bar.Height);
            Assert.True(bar.Width <= 160);
            Assert.Equal(1, bar.Opacity);
            Assert.True(bar.IsHitTestVisible);
            Invoke(window, "UpdateWholeAppOverlayDragStrip", false);
            Assert.True(bar.Opacity > 0.5, "lingers and fades instead of vanishing at once");
            Assert.True(((DispatcherTimer)GetField(window, "_dragStripFadeTimer")!).IsEnabled);

            // Off again (and the 5.1.2 top-centre discovery handle off): the strip never shows.
            settings.WholeAppOverlayTopCentreGrip = false;
            FindTagged(window, "setting.overlay.wholeAppShowDragStrip").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Invoke(window, "UpdateWholeAppOverlayDragStrip", true);
            Assert.False(bar.IsVisible);
        });
    }

    [ThreadStatic] private static Rect? _dest;

    private static void SetImageDest(Glide.App.Controls.ImageViewport viewport, Rect dest) =>
        typeof(Glide.App.Controls.ImageViewport).GetField("_lastImageDest", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(viewport, dest);

    /// <summary>Raises a left press on the viewport at a host point; true when the window-move path handled it.</summary>
    private static bool PressOn(MainWindow window, Control target, Control host, Pointer pointer, Point hostPoint, KeyModifiers modifiers = KeyModifiers.None)
    {
        if (target is Glide.App.Controls.ImageViewport v && _dest is { } d) SetImageDest(v, d);   // render resets it
        var moved = false;
        var previous = window.NativeWindowDragInterceptorForTests;
        window.NativeWindowDragInterceptorForTests = e => { moved = true; return previous?.Invoke(e) ?? true; };
        try
        {
            var p = host.TranslatePoint(hostPoint, window)!.Value;
            var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, p, 0, props, modifiers) { RoutedEvent = InputElement.PointerPressedEvent });
            target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, p, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left) { RoutedEvent = InputElement.PointerReleasedEvent });
            Dispatcher.UIThread.RunJobs();
            return moved;
        }
        finally { window.NativeWindowDragInterceptorForTests = previous; }
    }

    // ---- #28 normal window size after Overlay ------------------------------------------------------------------

    [Theory]
    [InlineData("Normal")]
    [InlineData("Maximized")]
    [InlineData("FullScreen")]
    public async Task Leaving_overlay_restores_the_normal_window_size(string origin)
    {
        await OnOverlayReady(window =>
        {
            window.Width = 1000; window.Height = 700;
            window.Position = new PixelPoint(120, 90);
            Dispatcher.UIThread.RunJobs();
            Invoke(window, "TrackNormalWindowPlacement");
            // Headless windows keep their client size; make the remembered placement what a real window would track.
            SetField(window, "_lastNormalWindowSize", new Size(1000, 700));
            SetField(window, "_lastNormalWindowPosition", new PixelPoint(120, 90));
            SetField(window, "_normalPlacementKnown", true);
            Settings(window).WindowWidth = 1000; Settings(window).WindowHeight = 700;
            Settings(window).WindowX = 120; Settings(window).WindowY = 90;
            var before = new Size(window.Width, window.Height);
            var lastNormal = (Size)GetField(window, "_lastNormalWindowSize")!;
            var saved = (Settings(window).WindowWidth, Settings(window).WindowHeight, Settings(window).WindowX, Settings(window).WindowY);
            if (origin == "Maximized") window.WindowState = WindowState.Maximized;
            if (origin == "FullScreen") Invoke(window, "EnterFullscreen");
            Dispatcher.UIThread.RunJobs();

            Invoke(window, "EnterWholeAppOverlayMode", "test");
            Dispatcher.UIThread.RunJobs();
            Invoke(window, "SetWholeAppOverlaySize", 50);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new Size(400, 300), new Size(window.Width, window.Height));
            Invoke(window, "ExitWholeAppOverlayMode");
            // A late native resize (WM_SIZE / DPI change) that lands after the restore must not win.
            if (origin == "Normal") window.Width = 400;
            Dispatcher.UIThread.RunJobs();

            if (origin == "Maximized") { Assert.Equal(WindowState.Maximized, window.WindowState); window.WindowState = WindowState.Normal; }
            if (origin == "FullScreen") { Assert.Equal(WindowState.FullScreen, window.WindowState); Invoke(window, "ExitFullscreen", false); }
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(lastNormal, (Size)GetField(window, "_lastNormalWindowSize")!);
            Assert.Equal(saved, (Settings(window).WindowWidth, Settings(window).WindowHeight, Settings(window).WindowX, Settings(window).WindowY));
            if (origin != "Maximized")
            {
                Assert.Equal(before, new Size(window.Width, window.Height));
                Assert.Equal(new PixelPoint(120, 90), window.Position);
            }
        });
    }
    private static Task OnOverlayReady(Action<MainWindow> body) => OnWindow(window =>
    {
        var viewport = window.FindControl<Glide.App.Controls.ImageViewport>("Viewport")!;
        viewport.Bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(800, 600), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        SetField(window, "_currentPath", @"C:\pictures\overlay.png");
        SetField(window, "_currentPixelWidth", 800);
        SetField(window, "_currentPixelHeight", 600);
        body(window);
    });

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static MenuItem FindTagged(MainWindow window, string tag)
    {
        var menu = (ContextMenu)Invoke(window, "BuildWholeAppOverlayOnlyContextMenu")!;
        foreach (var item in Flatten(menu.ItemsSource!.Cast<object>()))
            if (MenuActions.GetActionId(item) == tag) return item;
        throw new Xunit.Sdk.XunitException("No menu item tagged " + tag);
    }

    internal static IEnumerable<MenuItem> Flatten(IEnumerable<object> items)
    {
        foreach (var o in items)
        {
            if (o is not MenuItem item) continue;
            yield return item;
            foreach (var child in Flatten(item.Items.Cast<object>())) yield return child;
        }
    }

    internal static MenuItem SizeMenu(MainWindow window)
    {
        var menu = (ContextMenu)Invoke(window, "BuildWholeAppOverlayOnlyContextMenu")!;
        return menu.ItemsSource!.Cast<object>().OfType<MenuItem>().First(m => m.Header?.ToString() == "Size");
    }

    internal static async Task OnWindow(Action<MainWindow> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Overlay3.Tests", Guid.NewGuid().ToString("N"));
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

    internal static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    internal static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var candidates = typeof(MainWindow).GetMethods(Flags).Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();
        Assert.True(candidates.Length > 0, method);
        return candidates[0].Invoke(window, args);
    }

    internal static object? GetField(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return field!.GetValue(window);
    }

    internal static void SetField(MainWindow window, string name, object? value)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }
}
