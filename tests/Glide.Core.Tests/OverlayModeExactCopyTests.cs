using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.Imaging;
using Pointer = Avalonia.Input.Pointer;

namespace Glide.Core.Tests;

/// <summary>
/// Overlay mode (the Glide window as a floating overlay) must be an exact copy of a picture-in-picture overlay: the same chrome
/// geometry and drawing, the same hit zones, the same resize/drag behaviour and size rules. Its only additions are the
/// right-click menu and the optional drag strip. Headless: nothing here starts Glide.exe or touches the desktop.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Overlay")]
public sealed class OverlayModeExactCopyTests
{
    public OverlayModeExactCopyTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private static readonly Avalonia.Media.Color Accent = Avalonia.Media.Color.FromRgb(0x36, 0xB8, 0xF4);

    // ---- pure geometry: one definition for both hosts ----------------------------------------------------------

    private sealed class RecordingCanvas : OverlayFrameChromePainter.ICanvas
    {
        public readonly List<string> Ops = new();
        public readonly List<Rect> Rects = new();
        public void Rect(Rect rect, Avalonia.Media.Color? fill, Avalonia.Media.Color? stroke, double strokeThickness, double radius)
        {
            Rects.Add(rect);
            Ops.Add($"rect {rect} {fill} {stroke} {strokeThickness} {radius}");
        }
        public void Line(Point a, Point b, Avalonia.Media.Color color, double thickness) => Ops.Add($"line {a} {b} {color} {thickness}");
        public void Ellipse(Point center, double radius, Avalonia.Media.Color fill) => Ops.Add($"ellipse {center} {radius} {fill}");
    }

    private static OverlayState Item(double x, double y, double w, double h, double opacity = 1) =>
        new(Guid.NewGuid(), @"C:\i\a.png", x, y, w, h, Opacity: opacity, ContentScale: 0.5);

    [Theory]
    [InlineData(400, 300, 1.0)]
    [InlineData(400, 300, 0.45)]
    [InlineData(240, 180, 0.35)]
    [InlineData(1000, 700, 1.0)]
    public void Both_hosts_draw_identical_chrome_operations(double w, double h, double opacity) => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        // The picture-in-picture item at the origin, and Overlay mode at the same size: every drawing operation after the
        // glow and frame (the only host adaptation: the window frame sits inside the glow) is identical.
        var pip = new RecordingCanvas();
        OverlayFrameChromePainter.Draw(pip, WindowInWindowOverlayManager.BuildChromeSpec(Item(0, 0, w, h, opacity), glow: true), Accent);
        var mode = new RecordingCanvas();
        OverlayFrameChromePainter.Draw(mode, new LegacyOverlayChrome { OpacityValue = opacity }.BuildSpec(new Size(w, h)), Accent);

        Assert.Equal(pip.Ops.Count, mode.Ops.Count);
        Assert.Equal(pip.Ops.Skip(2), mode.Ops.Skip(2));
        // The glow is the same translucent rectangle reaching 2 DIP past the frame, and the frame the same accent outline.
        Assert.Equal(new Rect(-2, -2, w + 4, h + 4), pip.Rects[0]);
        Assert.Equal(new Rect(0, 0, w, h), pip.Rects[1]);
        Assert.Equal(new Rect(0, 0, w, h), mode.Rects[0]);
        Assert.Equal(new Rect(2, 2, w - 4, h - 4), mode.Rects[1]);
        Assert.Equal(pip.Ops[0].Substring(pip.Ops[0].IndexOf(" #", StringComparison.Ordinal)), mode.Ops[0].Substring(mode.Ops[0].IndexOf(" #", StringComparison.Ordinal)));
    });

    [Theory]
    [InlineData(400, 300)]
    [InlineData(80, 60)]
    [InlineData(180, 140)]
    [InlineData(1280, 800)]
    public void Hit_zones_are_the_same_in_picture_in_picture_and_overlay_mode(double w, double h)
    {
        var item = Item(37, 53, w, h);
        var managerRects = typeof(WindowInWindowOverlayManager);
        Rect Pip(string name) => (Rect)managerRects.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { item })!;
        Rect Moved(Rect r) => new(r.X + 37, r.Y + 53, r.Width, r.Height);
        var size = new Size(w, h);

        Assert.Equal(Pip("CloseRect"), Moved(OverlayChromePolicy.CloseRect(size)));
        Assert.Equal(Pip("ResizeRect"), Moved(OverlayChromePolicy.ResizeHitRect(size)));
        Assert.Equal(Pip("ResizeWidthRect"), Moved(OverlayChromePolicy.ResizeWidthRect(size)));
        Assert.Equal(Pip("ResizeHeightRect"), Moved(OverlayChromePolicy.ResizeHeightRect(size)));
        Assert.Equal(Pip("SliderRect"), Moved(OverlayChromePolicy.SliderRect(size)));
        Assert.Equal(Pip("ZoomOutRect"), Moved(OverlayChromePolicy.ZoomOutRect(size)));
        Assert.Equal(Pip("ZoomInRect"), Moved(OverlayChromePolicy.ZoomInRect(size)));

        // Every point of the frame resolves to the same zone whether the frame sits in the viewer or at a window's origin.
        var inViewer = new Rect(37, 53, w, h);
        var atOrigin = new Rect(0, 0, w, h);
        for (var x = -6.0; x <= w + 6; x += 3)
            for (var y = -6.0; y <= h + 6; y += 3)
                Assert.Equal(OverlayFrameGeometry.ChromeZoneAt(inViewer, new Point(x + 37, y + 53)), OverlayFrameGeometry.ChromeZoneAt(atOrigin, new Point(x, y)));
    }

    [Fact]
    public void Zones_resolve_in_the_press_order_close_zoom_corner_handles_slider()
    {
        var f = new Rect(0, 0, 400, 300);
        Assert.Equal(OverlayChromeZone.Close, OverlayFrameGeometry.ChromeZoneAt(f, OverlayFrameGeometry.Close(f).Center));
        Assert.Equal(OverlayChromeZone.ZoomOut, OverlayFrameGeometry.ChromeZoneAt(f, OverlayFrameGeometry.ZoomOut(f).Center));
        Assert.Equal(OverlayChromeZone.ZoomIn, OverlayFrameGeometry.ChromeZoneAt(f, OverlayFrameGeometry.ZoomIn(f).Center));
        Assert.Equal(OverlayChromeZone.Grip, OverlayFrameGeometry.ChromeZoneAt(f, new Point(399, 299)));
        Assert.Equal(OverlayChromeZone.WidthHandle, OverlayFrameGeometry.ChromeZoneAt(f, OverlayFrameGeometry.WidthHandle(f).Center));
        Assert.Equal(OverlayChromeZone.HeightHandle, OverlayFrameGeometry.ChromeZoneAt(f, OverlayFrameGeometry.HeightHandle(f).Center));
        Assert.Equal(OverlayChromeZone.Slider, OverlayFrameGeometry.ChromeZoneAt(f, OverlayFrameGeometry.Slider(f).Center));
        Assert.Equal(OverlayChromeZone.None, OverlayFrameGeometry.ChromeZoneAt(f, new Point(200, 150)));
        Assert.Equal(OverlayResizeMode.Proportional, OverlayFrameGeometry.ResizeModeOf(OverlayChromeZone.Grip));
        Assert.Null(OverlayFrameGeometry.ResizeModeOf(OverlayChromeZone.Close));
    }

    [Theory]
    [InlineData(400, 300)]
    [InlineData(200, 200)]
    [InlineData(1000, 700)]
    public void The_drag_strip_never_overlaps_the_edge_handles_or_other_chrome(double w, double h)
    {
        var frame = new Rect(0, 0, w, h);
        var strip = OverlayChromePolicy.DragStripRect(frame);
        Assert.NotNull(strip);
        foreach (var chrome in new[] { OverlayFrameGeometry.HeightHandle(frame), OverlayFrameGeometry.WidthHandle(frame), OverlayFrameGeometry.Close(frame) })
            Assert.False(strip!.Value.Intersects(chrome), $"strip {strip} overlaps {chrome}");
    }

    [Fact]
    public void Resize_rules_are_the_picture_in_picture_rules()
    {
        // Corner: stronger axis wins, aspect kept, minimum 80 x 60, scale 0.05..20.
        var grow = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, 100, 10, preserveAspect: true);
        Assert.Equal(500, grow.Width, 6);
        Assert.Equal(375, grow.Height, 6);
        var min = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, -5000, -5000, preserveAspect: true);
        Assert.Equal(80, min.Width, 6);
        Assert.Equal(60, min.Height, 6);
        // Edge handles: one axis, within [minimum, max(start, cap)].
        var wide = OverlayResizeMath.Compute(OverlayResizeMode.WidthOnly, 400, 300, 5000, 5000, preserveAspect: true, maxWidth: 1000);
        Assert.Equal((1000d, 300d, 0d), (wide.Width, wide.Height, wide.TopShift));
        var narrow = OverlayResizeMath.Compute(OverlayResizeMode.WidthOnly, 400, 300, -5000, 0, preserveAspect: true);
        Assert.Equal(80, narrow.Width, 6);
        // Top handle: dragging up grows, the bottom edge stays (TopShift is how far the top moved down).
        var up = OverlayResizeMath.Compute(OverlayResizeMode.HeightOnly, 400, 300, 0, -80, preserveAspect: true, maxHeight: 1000);
        Assert.Equal((400d, 380d, -80d), (up.Width, up.Height, up.TopShift));
        var down = OverlayResizeMath.Compute(OverlayResizeMode.HeightOnly, 400, 300, 0, 5000, preserveAspect: true);
        Assert.Equal(60, down.Height, 6);
        Assert.Equal(240, down.TopShift, 6);
        // A cap (the work area) shrinks the corner resize without changing its aspect ratio.
        var capped = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, 4000, 0, preserveAspect: true, maxWidth: 800, maxHeight: 800);
        Assert.Equal(800, capped.Width, 6);
        Assert.Equal(600, capped.Height, 6);
    }

    [Fact]
    public void Opacity_pill_maps_the_pointer_the_same_way_in_both_hosts()
    {
        var slider = OverlayChromePolicy.SliderRect(new Size(400, 300));
        foreach (var opacity in new[] { 0.35, 0.5, 0.8, 1.0 })
        {
            var pip = OverlayFrameGeometry.OpacityKnobY(slider, opacity);
            Assert.Equal(pip, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, opacity), 6);
            Assert.Equal(opacity, OverlayFrameGeometry.OpacityFromY(slider, pip, 0.1), 6);
            Assert.Equal(opacity, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, pip), 6);
        }
        // Only the floor differs: a window cannot fade below 35 % (it would be unfindable), a picture-in-picture overlay below 10 %.
        Assert.Equal(0.1, OverlayFrameGeometry.OpacityFromY(slider, slider.Bottom + 50, OverlayChromePolicy.MinimumOpacity), 6);
        Assert.Equal(0.35, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, slider.Bottom + 50), 6);
    }

    // ---- Overlay mode: the real window ------------------------------------------------------------------------------

    private static (Panel Host, Control Grip, Control Width, Control Height, Button Close, Button ZoomOut, Button ZoomIn, Control Opacity) Hits(MainWindow window)
    {
        var host = (Panel)OverlayRound3Tests.GetField(window, "_wholeAppOverlayControlsHost")!;
        host.IsVisible = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var frame = new Rect(default, host.Bounds.Size);
        Control At(Rect rect) => host.Children.Where(c => c.ZIndex == 2 && c.Bounds.Contains(rect.Center) && c is not Button).First();
        var buttons = host.Children.OfType<Button>().ToArray();
        Button ButtonAt(Rect rect) => buttons.Single(b => b.Bounds.Contains(rect.Center));
        return (host,
            At(OverlayFrameGeometry.GripHit(frame).Intersect(frame)),
            At(OverlayFrameGeometry.WidthHandle(frame)),
            At(OverlayFrameGeometry.HeightHandle(frame)),
            ButtonAt(OverlayFrameGeometry.Close(frame)),
            ButtonAt(OverlayFrameGeometry.ZoomOut(frame)),
            ButtonAt(OverlayFrameGeometry.ZoomIn(frame)),
            At(OverlayFrameGeometry.Slider(frame)));
    }

    [Fact]
    public async Task Overlay_mode_hit_targets_sit_exactly_on_the_picture_in_picture_zones_and_have_no_pin_tooltips_or_cursors()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            window.Width = 400; window.Height = 300;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var (host, grip, widthHandle, heightHandle, close, zoomOut, zoomIn, opacity) = Hits(window);
            var frame = new Rect(default, host.Bounds.Size);
            Assert.Equal(OverlayFrameGeometry.GripHit(frame).Intersect(frame), grip.Bounds);
            Assert.Equal(OverlayFrameGeometry.WidthHandle(frame), widthHandle.Bounds);
            Assert.Equal(OverlayFrameGeometry.HeightHandle(frame), heightHandle.Bounds);
            Assert.Equal(OverlayFrameGeometry.Close(frame), close.Bounds);
            Assert.Equal(OverlayFrameGeometry.ZoomOut(frame), zoomOut.Bounds);
            Assert.Equal(OverlayFrameGeometry.ZoomIn(frame), zoomIn.Bounds);

            // Exactly the picture-in-picture controls: close, zoom out/in, opacity, corner, two handles. No always-on-top pin.
            Assert.Equal(7, host.Children.Count(c => c.ZIndex == 2));
            // A picture-in-picture overlay shows no tooltips and changes no cursor.
            foreach (var hit in host.Children.Where(c => c.ZIndex == 2))
            {
                Assert.Null(ToolTip.GetTip(hit));
                Assert.Null(hit.Cursor);
            }
        });
    }

    private static void RaiseAt(Window window, Control target, Pointer pointer, RoutedEvent routed, Point windowPoint, bool left = true)
    {
        var p = windowPoint;
        RoutedEventArgs args = routed == InputElement.PointerPressedEvent
            ? new PointerPressedEventArgs(target, pointer, window, p, 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None) { RoutedEvent = routed }
            : routed == InputElement.PointerReleasedEvent
                ? new PointerReleasedEventArgs(target, pointer, window, p, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left) { RoutedEvent = routed }
                : new PointerEventArgs(routed, target, pointer, window, p, 0,
                    new PointerPointProperties(left ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None);
        target.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Raise(Window window, Control target, Control relativeTo, Pointer pointer, RoutedEvent routed, Point point) =>
        RaiseAt(window, target, pointer, routed, relativeTo.TranslatePoint(point, window)!.Value);

    /// <summary>
    /// Drags a hit control by (dx, dy) SCREEN pixels in three moves and releases. The cursor is tracked in screen space, so a
    /// window that moves under the pointer (the top handle) gets the same window-relative pointer positions a real one does.
    /// </summary>
    private static void Drag(MainWindow window, Control hit, Control host, double dx, double dy)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var startWindow = hit.TranslatePoint(new Point(hit.Bounds.Width / 2, hit.Bounds.Height / 2), window)!.Value;
        var startScreen = window.PointToScreen(startWindow);
        RaiseAt(window, hit, pointer, InputElement.PointerPressedEvent, startWindow);
        Point At(double fraction) => window.PointToClient(new PixelPoint(startScreen.X + (int)Math.Round(dx * fraction), startScreen.Y + (int)Math.Round(dy * fraction)));
        for (var step = 1; step <= 3; step++)
            RaiseAt(window, hit, pointer, InputElement.PointerMovedEvent, At(step / 3.0));
        RaiseAt(window, hit, pointer, InputElement.PointerReleasedEvent, At(1));
    }

    [Fact]
    public async Task Corner_grip_resizes_the_real_window_proportionally()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            window.Width = 400; window.Height = 300;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var (host, grip, _, _, _, _, _, _) = Hits(window);
            var before = window.Position;
            Drag(window, grip, host, 100, 10);
            Assert.Equal(500, window.Width, 1);
            Assert.Equal(375, window.Height, 1);
            Assert.Equal(before, window.Position);   // the corner grows from the top-left

            // Minimum size rule: 80 x 60.
            Drag(window, grip, host, -5000, -5000);
            Assert.True(window.Width >= 80 && window.Height >= 60);
            Assert.Equal(80, window.Width, 1);
            Assert.Equal(60, window.Height, 1);
        });
    }

    [Fact]
    public async Task Edge_handles_resize_one_axis_and_the_top_handle_keeps_the_bottom_edge()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            OverlayRound3Tests.SetField(window, "_wholeAppOverlayPreserveAspect", false);
            window.Width = 400; window.Height = 300;
            window.Position = new PixelPoint(200, 150);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var (host, _, widthHandle, heightHandle, _, _, _, _) = Hits(window);

            Drag(window, widthHandle, host, 120, 90);
            Assert.Equal(520, window.Width, 1);
            Assert.Equal(300, window.Height, 1);
            Assert.Equal(new PixelPoint(200, 150), window.Position);

            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            (host, _, _, heightHandle, _, _, _, _) = Hits(window);
            var bottom = window.Position.Y + (int)Math.Round(window.Height * window.RenderScaling);
            Drag(window, heightHandle, host, 40, -80);   // up grows the window
            Assert.Equal(520, window.Width, 1);
            Assert.Equal(380, window.Height, 1);
            Assert.Equal(70, window.Position.Y);
            Assert.Equal(bottom, window.Position.Y + (int)Math.Round(window.Height * window.RenderScaling));
        });
    }

    [Fact]
    public async Task Ratio_on_edge_handles_crop_the_picture_and_ratio_off_edge_and_corner_stretch_it()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            window.Width = 400; window.Height = 300;
            window.Position = new PixelPoint(200, 150);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True((bool)OverlayRound3Tests.GetField(window, "_wholeAppOverlayPreserveAspect")!); // on by default
            var (host, grip, widthHandle, _, _, _, _, _) = Hits(window);

            // Ratio on: the width handle crops (window narrower, same height, the picture keeps its scale).
            var scaleBefore = window.Viewport.CurrentScale;
            Drag(window, widthHandle, host, -100, 60);
            Assert.Equal(300, window.Width, 1);
            Assert.Equal(300, window.Height, 1);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(scaleBefore, window.Viewport.CurrentScale, 3);

            // Ratio off: the picture is stretched over the whole window; both axes follow a corner drag freely.
            OverlayRound3Tests.Invoke(window, "ToggleWholeAppOverlayPreserveAspect");
            Assert.False((bool)OverlayRound3Tests.GetField(window, "_wholeAppOverlayPreserveAspect")!);
            Assert.True(window.Viewport.StretchToFill);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var rect = window.Viewport.GetPresentationRectForViewportSize(window.Viewport.Bounds.Size);
            Assert.Equal(new Rect(default, window.Viewport.Bounds.Size), rect);
            (host, grip, _, _, _, _, _, _) = Hits(window);
            Drag(window, grip, host, 100, 10);
            Assert.Equal(400, window.Width, 1);
            Assert.Equal(310, window.Height, 1);

            // Back on: the stretch ends.
            OverlayRound3Tests.Invoke(window, "ToggleWholeAppOverlayPreserveAspect");
            Assert.False(window.Viewport.StretchToFill);
        });
    }

    [Fact]
    public void Picture_in_picture_stretches_the_whole_picture_when_the_ratio_is_off()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        var source = manager[manager.IndexOf("private Rect SourceRect(Item item)", StringComparison.Ordinal)..];
        source = source[..source.IndexOf("private void DrawChrome", StringComparison.Ordinal)];
        Assert.Contains("!s.PreserveAspectRatio ? Math.Max(1.0, pw / z)", source, StringComparison.Ordinal);
        Assert.Contains("!s.PreserveAspectRatio ? Math.Max(1.0, ph / z)", source, StringComparison.Ordinal);
        // Ratio off: edge drags are not limited to the picture's extent (they stretch); ratio on keeps the crop limit.
        Assert.Contains("current.PreserveAspectRatio ? Math.Max(80.0", manager, StringComparison.Ordinal);
        Assert.Contains("double.PositiveInfinity", manager, StringComparison.Ordinal);
        // The corner is free with the ratio off (OverlayResizeMath), and the ratio-off result is a plain per-axis size.
        var free = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, 100, 10, preserveAspect: false);
        Assert.Equal(500, free.Width); Assert.Equal(310, free.Height);
        var kept = OverlayResizeMath.Compute(OverlayResizeMode.Proportional, 400, 300, 100, 10, preserveAspect: true);
        Assert.Equal(kept.Width / kept.Height, 400.0 / 300.0, 3);
    }

    [Fact]
    public async Task Resizing_keeps_the_chrome_up_and_lock_position_stops_the_top_handle_only()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            OverlayRound3Tests.SetField(window, "_wholeAppOverlayPreserveAspect", false);
            window.Width = 400; window.Height = 300;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var (host, grip, widthHandle, heightHandle, _, _, _, _) = Hits(window);

            // Mid-drag the pointer is far outside the picture: the controls stay (like a picture-in-picture resize).
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            Raise(window, grip, host, pointer, InputElement.PointerPressedEvent, grip.Bounds.Center);
            Assert.NotNull(OverlayRound3Tests.GetField(window, "_wholeAppOverlayResize"));
            OverlayRound3Tests.Invoke(window, "UpdateWholeAppOverlayChromeHover", new Point(-500, -500));
            Assert.True(host.IsVisible);
            Raise(window, grip, host, pointer, InputElement.PointerReleasedEvent, grip.Bounds.Center);
            Assert.Null(OverlayRound3Tests.GetField(window, "_wholeAppOverlayResize"));

            OverlayRound3Tests.Settings(window).WholeAppOverlayLockPosition = true;
            var heightBefore = window.Height;
            Drag(window, heightHandle, host, 0, -80);
            Assert.Equal(heightBefore, window.Height, 3);       // locked: the top handle would move the window
            var widthBefore = window.Width;
            Drag(window, widthHandle, host, 60, 0);              // the others still resize
            Assert.Equal(widthBefore + 60, window.Width, 1);
        });
    }

    [Fact]
    public async Task Overlay_mode_window_minimum_is_the_picture_in_picture_minimum()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            Assert.Equal(80, window.MinWidth);
            Assert.Equal(60, window.MinHeight);
        });
    }

    [Fact]
    public async Task Picture_in_picture_and_overlay_mode_resize_to_the_same_size()
    {
        // The same drags through the real picture-in-picture manager (an item injected into a live manager) and through the
        // real Overlay window give the same size, so the two cannot drift apart.
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            window.Width = 400; window.Height = 300;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var (host, grip, widthHandle, heightHandle, _, _, _, _) = Hits(window);

            var pip = new PipHarness(400, 300);
            try
            {
                foreach (var (name, zone, dx, dy) in new[]
                {
                    ("corner", OverlayChromeZone.Grip, 100.0, 10.0),
                    ("corner small", OverlayChromeZone.Grip, -5000.0, -5000.0),
                    ("width", OverlayChromeZone.WidthHandle, -150.0, 0.0),
                    ("height", OverlayChromeZone.HeightHandle, 0.0, 70.0),
                })
                {
                    window.Width = 400; window.Height = 300; window.Position = new PixelPoint(200, 150);
                    window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    (host, grip, widthHandle, heightHandle, _, _, _, _) = Hits(window);
                    var overlayHandle = zone switch { OverlayChromeZone.Grip => grip, OverlayChromeZone.WidthHandle => widthHandle, _ => heightHandle };
                    Drag(window, overlayHandle, host, dx, dy);

                    var state = pip.Drag(zone, dx, dy);
                    Assert.True(Math.Abs(state.Width - window.Width) < 1, $"{name}: width pip {state.Width} overlay {window.Width}");
                    Assert.True(Math.Abs(state.Height - window.Height) < 1, $"{name}: height pip {state.Height} overlay {window.Height}");
                    pip.Reset(400, 300);
                }
            }
            finally { pip.Dispose(); }
        });
    }

    /// <summary>A real picture-in-picture manager over a headless window, with one item injected.</summary>
    private sealed class PipHarness : IDisposable
    {
        private readonly Window _window;
        private readonly Grid _host;
        private readonly LegacyOverlaySurface _surface = new();
        private readonly ImageLoadCoordinator _loader = new();
        private readonly WindowInWindowOverlayManager _manager;
        private readonly WriteableBitmap _bitmap = new(new PixelSize(800, 600), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

        public PipHarness(double width, double height)
        {
            _host = new Grid { Children = { _surface } };
            _window = new Window { Width = 1000, Height = 800, Content = _host };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
            _manager = new WindowInWindowOverlayManager(_surface, _host, _loader, _window);
            Reset(width, height);
        }

        public void Reset(double width, double height)
        {
            var type = typeof(WindowInWindowOverlayManager);
            var items = (System.Collections.IList)type.GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_manager)!;
            items.Clear();
            var itemType = type.GetNestedType("Item", BindingFlags.NonPublic)!;
            var state = new OverlayState(Guid.NewGuid(), @"C:\pictures\pip.png", 100, 120, width, height, Opacity: 1, ContentScale: 0.5);
            items.Add(Activator.CreateInstance(itemType, state, _bitmap, null));
            _window.UpdateLayout();
        }

        public OverlayState State => _manager.States[0];

        public OverlayState Drag(OverlayChromeZone zone, double dx, double dy)
        {
            var s = State;
            var frame = new Rect(s.X, s.Y, s.Width, s.Height);
            var rect = zone switch
            {
                OverlayChromeZone.Grip => OverlayFrameGeometry.GripHit(frame),
                OverlayChromeZone.WidthHandle => OverlayFrameGeometry.WidthHandle(frame),
                _ => OverlayFrameGeometry.HeightHandle(frame)
            };
            var start = rect.Center;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            Raise(_window, _host, _host, pointer, InputElement.PointerPressedEvent, start);
            for (var step = 1; step <= 3; step++)
                Raise(_window, _host, _host, pointer, InputElement.PointerMovedEvent, new Point(start.X + dx * step / 3, start.Y + dy * step / 3));
            Raise(_window, _host, _host, pointer, InputElement.PointerReleasedEvent, new Point(start.X + dx, start.Y + dy));
            return State;
        }

        public void Dispose()
        {
            _manager.Dispose();
            _loader.Dispose();
            _window.Close();
        }
    }

    [Fact]
    public async Task Dragging_the_picture_moves_the_window_like_a_picture_in_picture_drag_moves_its_item()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            window.Width = 400; window.Height = 300;
            window.Position = new PixelPoint(300, 200);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var (host, _, _, _, _, _, _, _) = Hits(window);
            var viewport = window.FindControl<ImageViewport>("Viewport")!;
            typeof(ImageViewport).GetField("_lastImageDest", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(viewport, new Rect(0, 0, viewport.Bounds.Width, viewport.Bounds.Height));

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var middle = new Point(host.Bounds.Width / 2, host.Bounds.Height / 2);
            var p = host.TranslatePoint(middle, window)!.Value;
            viewport.RaiseEvent(new PointerPressedEventArgs(viewport, pointer, window, p, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent });
            Dispatcher.UIThread.RunJobs();
            var moved = new Point(p.X + 40, p.Y + 25);
            window.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, window, pointer, window, moved, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new PixelPoint(340, 225), window.Position);
            window.RaiseEvent(new PointerReleasedEventArgs(window, pointer, window, moved, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left) { RoutedEvent = InputElement.PointerReleasedEvent });
            Dispatcher.UIThread.RunJobs();

            // The picture-in-picture item follows the same drag one-to-one.
            var pip = new PipHarness(400, 300);
            try
            {
                var s = pip.State;
                var start = new Point(s.X + s.Width / 2, s.Y + s.Height / 2);
                var pipPointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var hostField = typeof(PipHarness).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var pipHost = (Grid)hostField.GetValue(pip)!;
                var pipWindow = (Window)typeof(PipHarness).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pip)!;
                Raise(pipWindow, pipHost, pipHost, pipPointer, InputElement.PointerPressedEvent, start);
                Raise(pipWindow, pipHost, pipHost, pipPointer, InputElement.PointerMovedEvent, new Point(start.X + 40, start.Y + 25));
                Raise(pipWindow, pipHost, pipHost, pipPointer, InputElement.PointerReleasedEvent, new Point(start.X + 40, start.Y + 25));
                Assert.Equal((s.X + 40, s.Y + 25), (pip.State.X, pip.State.Y));
            }
            finally { pip.Dispose(); }
        });
    }

    [Fact]
    public async Task The_two_overlay_mode_additions_remain_the_right_click_menu_and_the_drag_strip()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            var menu = (ContextMenu)OverlayRound3Tests.Invoke(window, "BuildWholeAppOverlayOnlyContextMenu")!;
            var ids = OverlayRound3Tests.Flatten(menu.ItemsSource!.Cast<object>()).Select(MenuActions.GetActionId).Where(id => id is not null).ToArray();
            foreach (var expected in new[]
            {
                "setting.overlay.wholeAppWindowedInteractions", "setting.overlay.wholeAppShowDragStrip", "setting.overlay.wholeAppLockPosition",
                "setting.overlay.wholeAppAlwaysStart", "window.alwaysTop", "overlay.toggleMode"
            })
                Assert.Contains(expected, ids);
            Assert.Contains(menu.ItemsSource!.Cast<object>().OfType<MenuItem>(), i => i.Header?.ToString() == "Size");

            OverlayRound3Tests.Settings(window).WholeAppOverlayShowDragStrip = true;
            OverlayRound3Tests.Invoke(window, "UpdateWholeAppOverlayDragStrip", true);
            var strip = (Border)OverlayRound3Tests.GetField(window, "_wholeAppOverlayDragStrip")!;
            Assert.True(strip.IsVisible);
            Assert.Equal(1, strip.Opacity);
            Assert.True(strip.IsHitTestVisible);
            Assert.Equal(22, strip.Height);
        });
    }

    [Fact]
    public async Task Chrome_shows_while_the_pointer_is_over_the_picture_and_hides_when_it_leaves_like_a_picture_in_picture_overlay()
    {
        await OverlayRound3Tests.OnOverlayWithPicture(window =>
        {
            var host = (Panel)OverlayRound3Tests.GetField(window, "_wholeAppOverlayControlsHost")!;
            var chrome = (LegacyOverlayChrome)OverlayRound3Tests.GetField(window, "_wholeAppOverlayChromeVisual")!;
            Assert.True(chrome.ChromeVisible);
            host.IsVisible = false;
            // Over the picture: shown. Over the transparent window outside it: hidden at once (no idle delay).
            var over = new Point(window.Bounds.Width / 2, window.Bounds.Height / 2);
            var viewport = window.FindControl<ImageViewport>("Viewport")!;
            typeof(ImageViewport).GetField("_lastImageDest", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(viewport, new Rect(0, 0, viewport.Bounds.Width, viewport.Bounds.Height));
            window.Activate();
            OverlayRound3Tests.Invoke(window, "UpdateWholeAppOverlayChromeHover", over);
            if (window.IsActive) Assert.True(host.IsVisible);
            OverlayRound3Tests.Invoke(window, "UpdateWholeAppOverlayChromeHover", new Point(-300, -300));
            if (window.IsActive) Assert.False(host.IsVisible);
        });
    }

    // ---- side-by-side render ------------------------------------------------------------------------------------

    private sealed class DrawControl(Action<Avalonia.Media.DrawingContext, Size> draw) : Control
    {
        public override void Render(Avalonia.Media.DrawingContext context) => draw(context, Bounds.Size);
    }

    private static void Picture(Avalonia.Media.DrawingContext context, Rect rect)
    {
        var sky = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromRgb(0x5B, 0x8F, 0xD6), 0), new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromRgb(0xF2, 0xC1, 0x8B), 1) }
        };
        context.DrawRectangle(sky, null, rect);
        context.DrawEllipse(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xFF, 0xE0, 0x8A)), null, new Point(rect.X + rect.Width * .72, rect.Y + rect.Height * .3), rect.Width * .07, rect.Width * .07);
        var hill = new Avalonia.Media.StreamGeometry();
        using (var g = hill.Open())
        {
            g.BeginFigure(new Point(rect.X, rect.Bottom), true);
            g.LineTo(new Point(rect.X, rect.Y + rect.Height * .7));
            g.LineTo(new Point(rect.X + rect.Width * .35, rect.Y + rect.Height * .5));
            g.LineTo(new Point(rect.X + rect.Width * .6, rect.Y + rect.Height * .68));
            g.LineTo(new Point(rect.Right, rect.Y + rect.Height * .55));
            g.LineTo(new Point(rect.Right, rect.Bottom));
            g.EndFigure(true);
        }
        context.DrawGeometry(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x3A, 0x6B, 0x4A)), null, hill);
    }

    /// <summary>Set GLIDE_SAVE_RENDERS to a folder to write overlay-mode-exact.png (synthetic picture only).</summary>
    [Fact]
    public void Render_picture_in_picture_and_overlay_mode_side_by_side_idle_and_hover() => AvaloniaHeadlessFixture.RunOnUIThread(() =>
    {
        var size = new Size(840, 700);
        var label = new Avalonia.Media.Typeface("Segoe UI");
        var control = new DrawControl((context, _) =>
        {
            void Caption(string text, double x, double y, double fontSize = 13) => context.DrawText(new Avalonia.Media.FormattedText(text,
                System.Globalization.CultureInfo.InvariantCulture, Avalonia.Media.FlowDirection.LeftToRight, label, fontSize, Avalonia.Media.Brushes.White), new Point(x, y));

            Caption("Picture-in-picture overlay (inside Glide)", 30, 10);
            Caption("Overlay mode (the Glide window)", 450, 10);
            Caption("idle", 30, 36, 12);
            Caption("idle", 450, 36, 12);
            Caption("hover", 30, 366, 12);
            Caption("hover", 450, 366, 12);

            var panel = new Size(360, 270);
            foreach (var (x, y, hover, overlayMode) in new[] { (30.0, 56.0, false, false), (450.0, 56.0, false, true), (30.0, 386.0, true, false), (450.0, 386.0, true, true) })
            {
                var rect = new Rect(x, y, panel.Width, panel.Height);
                Picture(context, rect);
                if (!hover) continue;
                if (!overlayMode)
                    OverlayFrameChromePainter.Draw(context, WindowInWindowOverlayManager.BuildChromeSpec(Item(rect.X, rect.Y, rect.Width, rect.Height, 0.8), glow: true), Accent);
                else
                {
                    using (context.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
                    {
                        OverlayFrameChromePainter.Draw(context, new LegacyOverlayChrome { OpacityValue = .8 }.BuildSpec(rect.Size), Accent);
                        if (OverlayChromePolicy.DragStripRect(new Rect(rect.Size)) is { } strip)
                        {
                            context.DrawRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xBF, 0x14, 0x14, 0x16)), null, strip, strip.Height / 2, strip.Height / 2);
                            var c = strip.Center;
                            context.DrawRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xC8, 0xE6, 0xE9, 0xEE)), null, new Rect(c.X - 22, c.Y - 2, 44, 4), 2, 2);
                        }
                    }
                }
            }
            Caption("Same frame, glow, close, opacity pill, zoom buttons, corner grip and edge handles. Overlay mode adds only its right-click menu and the optional drag strip (top, shown here).", 30, 664, 11);
        }) { Width = size.Width, Height = size.Height };

        var window = new Window { Width = size.Width, Height = size.Height, Content = control, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x2B, 0x2F, 0x36)) };
        window.Show();
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
        bitmap.Render(window);
        window.Close();
        var folder = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            bitmap.Save(Path.Combine(folder, "overlay-mode-exact.png"));
        }
        Assert.Equal(new PixelSize(840, 700), bitmap.PixelSize);
    });

    [Fact]
    public void Overlay_mode_source_has_no_pin_button_or_pin_state_left()
    {
        var overlays = ReadSource("src", "Glide.App", "MainWindow.Overlays.cs");
        Assert.DoesNotContain("topmostHit", overlays, StringComparison.Ordinal);
        Assert.DoesNotContain("TopmostRect", overlays, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip.SetTip", overlays.Substring(0, overlays.IndexOf("SetWholeAppOverlayOpacity", StringComparison.Ordinal)), StringComparison.Ordinal);
        var chrome = ReadSource("src", "Glide.App", "Controls", "LegacyOverlayChrome.cs");
        Assert.DoesNotContain("Topmost", chrome, StringComparison.Ordinal);
        Assert.Contains("OverlayFrameGeometry.BuildSpec", chrome, StringComparison.Ordinal);
        Assert.Contains("OverlayFrameGeometry.BuildSpec", ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs"), StringComparison.Ordinal);
        Assert.Contains("OverlayFrameGeometry.BuildSpec", ReadSource("src", "Glide.App", "Controls", "CompositorOverlayRenderer.cs"), StringComparison.Ordinal);
        Assert.Contains("OverlayResizeMath.Compute", ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs"), StringComparison.Ordinal);
        Assert.Contains("OverlayResizeMath.Compute", overlays, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
