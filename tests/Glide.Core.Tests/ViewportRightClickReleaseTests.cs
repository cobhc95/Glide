using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Glide.App.Controls;

namespace Glide.Core.Tests;

/// <summary>
/// The viewer's right-click context menu opens on button RELEASE everywhere in the viewport: on the image and on the
/// empty background around it. A wheel turn or a drag while the right button is held cancels it (right+wheel zoom,
/// right-drag), instead of popping the menu on the press.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class ViewportRightClickReleaseTests
{
    public ViewportRightClickReleaseTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    private static readonly Point Outside = new(10, 200);   // letterbox: the 4:3 image is centred in 800x400
    private static readonly Point OnImage = new(400, 200);

    private sealed class Rig
    {
        public required Window Window;
        public required ImageViewport Viewport;
        public int Menus;
        public Avalonia.Input.Pointer Pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
    }

    private static Task WithViewport(Func<Rig, Task> body) => AvaloniaHeadlessFixture.RunOnUIThreadAsync(() => WithViewportCore(body));

    private static async Task WithViewportCore(Func<Rig, Task> body)
    {
        var viewport = new ImageViewport
        {
            Bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(400, 300), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul),
            WindowedWheelZoom = true
        };
        var window = new Window { Width = 800, Height = 400, Content = viewport };
        var rig = new Rig { Window = window, Viewport = viewport };
        viewport.ContextMenuRequested += _ => rig.Menus++;
        window.Show();
        try
        {
            await Settle();
            window.UpdateLayout();
            RenderTick(window);
            viewport.Fit();
            await Settle();
            RenderTick(window);
            // The headless render loop does not run the presentation pipeline that records the drawn image rectangle,
            // so record the fitted 4:3 image centred in the 800x400 viewport the way Render would.
            typeof(ImageViewport).GetField("_lastImageDest", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(viewport, new Rect(133.3, 0, 533.3, 400));
            Assert.False(viewport.IsPointOverImage(Outside), "test setup: the probe point must be outside the image");
            Assert.True(viewport.IsPointOverImage(OnImage), "test setup: the centre must be on the image");
            await body(rig);
        }
        finally { window.Close(); }
    }

    private static void RenderTick(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
            using (window.CaptureRenderedFrame()) { }
        }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
    }

    private static void Press(Rig r, Point p)
    {
        var props = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        r.Viewport.RaiseEvent(new PointerPressedEventArgs(r.Viewport, r.Pointer, r.Window, p, 0, props, KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent });
    }

    private static void Move(Rig r, Point p)
    {
        var props = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.Other);
        r.Viewport.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, r.Viewport, r.Pointer, r.Window, p, 0, props, KeyModifiers.None));
    }

    private static void Wheel(Rig r, Point p, double deltaY)
    {
        var props = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.Other);
        r.Viewport.RaiseEvent(new PointerWheelEventArgs(r.Viewport, r.Pointer, r.Window, p, 0, props, KeyModifiers.None, new Vector(0, deltaY)) { RoutedEvent = InputElement.PointerWheelChangedEvent });
    }

    private static void Release(Rig r, Point p)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased);
        r.Viewport.RaiseEvent(new PointerReleasedEventArgs(r.Viewport, r.Pointer, r.Window, p, 0, props, KeyModifiers.None, MouseButton.Right) { RoutedEvent = InputElement.PointerReleasedEvent });
    }

    [Fact]
    public async Task Right_press_on_the_empty_background_opens_no_menu_until_release()
    {
        await WithViewport(async r =>
        {
            Press(r, Outside);
            await Settle();
            Assert.Equal(0, r.Menus);
            Release(r, Outside);
            Assert.Equal(1, r.Menus);
        });
    }

    [Fact]
    public async Task Right_plus_wheel_over_the_empty_background_zooms_and_opens_no_menu()
    {
        await WithViewport(async r =>
        {
            var before = r.Viewport.ZoomPercent;
            Press(r, Outside);
            Wheel(r, Outside, 1);
            await Settle();
            Assert.NotEqual(before, r.Viewport.ZoomPercent);
            Assert.Equal(0, r.Menus);
            Release(r, Outside);
            Assert.Equal(0, r.Menus);
        });
    }

    [Fact]
    public async Task Right_drag_beyond_the_threshold_from_the_empty_background_opens_no_menu()
    {
        await WithViewport(async r =>
        {
            Press(r, Outside);
            Move(r, new Point(Outside.X + 30, Outside.Y + 30));
            await Settle();
            Release(r, new Point(Outside.X + 30, Outside.Y + 30));
            Assert.Equal(0, r.Menus);
        });
    }

    [Fact]
    public async Task A_background_right_press_released_far_away_without_movement_events_opens_no_menu()
    {
        await WithViewport(async r =>
        {
            Press(r, Outside);
            await Settle();
            Release(r, new Point(Outside.X + 40, Outside.Y));
            Assert.Equal(0, r.Menus);
        });
    }

    [Fact]
    public async Task The_next_plain_background_click_works_after_a_wheel_gesture()
    {
        await WithViewport(async r =>
        {
            Press(r, Outside); Wheel(r, Outside, 1); Release(r, Outside);
            Assert.Equal(0, r.Menus);
            Press(r, Outside); Release(r, Outside);
            Assert.Equal(1, r.Menus);
        });
    }

    [Fact]
    public async Task On_the_image_a_right_click_still_opens_the_menu_on_release_and_wheel_or_drag_cancels_it()
    {
        await WithViewport(async r =>
        {
            Press(r, OnImage);
            Assert.Equal(0, r.Menus);
            Release(r, OnImage);
            Assert.Equal(1, r.Menus);

            Press(r, OnImage); Wheel(r, OnImage, 1); Release(r, OnImage);
            Assert.Equal(1, r.Menus);

            Press(r, OnImage); Move(r, new Point(OnImage.X + 30, OnImage.Y)); Release(r, new Point(OnImage.X + 30, OnImage.Y));
            Assert.Equal(1, r.Menus);
            await Settle();
        });
    }

    [Fact]
    public async Task In_fullscreen_a_right_wheel_gesture_over_the_empty_area_opens_no_menu()
    {
        await WithViewport(async r =>
        {
            var browses = 0;
            r.Viewport.BrowseRequested += (_, _) => browses++;
            r.Viewport.IsFullscreen = true;
            await Settle();
            Press(r, Outside); Wheel(r, Outside, 1); Release(r, Outside);
            Assert.Equal(0, r.Menus);
            Assert.Equal(0, browses);
        });
    }
}
