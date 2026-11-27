using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Glide.App;
using Glide.Core;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.2.0 desktop retest: a torn-off tab window was not under the pointer and overflowed the screen (the source window was
/// maximized; the new one was copied as maximized, restored by Windows at the saved size, and nothing kept it inside the work area),
/// and the window title read "[1/1]" after a tab was dragged back into a window (the folder index finished after the title was set).
/// The monitor is faked at 200 % (2778 x 1736 physical, work area above the taskbar) with a work area smaller than the source window.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class TearOffAndAttachRetestTests
{
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static IReadOnlyList<MainWindow> Windows() => Glide.App.Platform.GlideWindowRegistry.Snapshot();

    /// <summary>Opens b.png in the harness tab and waits until its folder is indexed; returns the picture tab id.</summary>
    private static async Task<Guid> OpenPictureAsync(ExplorerHarness h)
    {
        h.DoubleClick("b.png");
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
        await h.WaitUntil(() => h.Get<bool>("_folderIndexCanonicalReady"), "the folder is indexed");
        await h.SettleAsync();
        if (h.Workspace.Tabs.Count < 2) { h.Workspace.AddHome(); h.Call("RebuildTabStrip"); }   // a tab to leave behind: tearing off the only tab moves the window itself
        return h.Workspace.Active!.Id;
    }

    private static MainWindow NewWindowAfter(IReadOnlyList<MainWindow> before) => Windows().Except(before).Single();

    /// <summary>
    /// The pointer at the end of a held tear-off. The real desktop cursor must not take part: where it rests (over a window's tab
    /// strip, say) decided whether the torn-off tab attached straight back and closed its window, so these tests failed on a
    /// machine whose mouse was in the top left corner of the screen. This point is outside every window.
    /// </summary>
    private static readonly PixelPoint ReleasedOnTheDesktop = new(6000, 4000);

    private static async Task ShowMonitorAsync(Func<Task> body, PixelBox work, double scale)
    {
        MainWindow.WorkAreaForTests = _ => (work, scale);
        MainWindow.PhysicalCursorForTests = () => ReleasedOnTheDesktop;
        try { await body(); }
        finally { MainWindow.WorkAreaForTests = null; MainWindow.PhysicalCursorForTests = null; }
    }

    // ---------------------------------------------------------------- 2: placement

    [Fact]
    public async Task A_tab_torn_off_a_maximized_window_opens_as_a_normal_window_under_the_pointer_inside_a_work_area_smaller_than_the_source()
    {
        await ExplorerHarness.Run(async h =>
        {
            var id = await OpenPictureAsync(h);
            // The source is a maximized window whose restored box (1200 x 800 DIP = 2400 x 1600 physical at 200 %) is bigger than the work area.
            var work = new PixelBox(0, 0, 2200, 1600);
            h.Set("_normalPlacementKnown", true);
            h.Set("_lastNormalWindowSize", new Size(1200, 800));
            h.Set("_lastNormalWindowPosition", new PixelPoint(100, 100));
            h.Window.WindowState = WindowState.Maximized;
            h.Pump();
            var before = Windows();
            MainWindow? detached = null;
            try
            {
                await ShowMonitorAsync(async () =>
                {
                    var cursor = new PixelPoint(400, 22);   // where the tab left the strip
                    Assert.True(await (Task<bool>)h.Call("BeginHeldTabTearOffAsync", id, cursor)!);
                    detached = NewWindowAfter(before);
                    for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(15); }
                    detached.UpdateLayout();

                    Assert.Equal(WindowState.Normal, detached.WindowState);   // a maximized copy was restored by Windows at the saved box
                    var scale = 2.0;
                    var box = new PixelBox(detached.Position.X, detached.Position.Y, (int)Math.Round(detached.Width * scale), (int)Math.Round(detached.Height * scale));
                    Assert.True(box.X >= work.X && box.Y >= work.Y && box.Right <= work.Right && box.Bottom <= work.Bottom, $"{box} is not inside the work area {work}");

                    // The pointer is on the tab header of the new window (offsets in DIPs at 200 %).
                    var grab = new Point((cursor.X - detached.Position.X) / scale, (cursor.Y - detached.Position.Y) / scale);
                    var header = detached.FindControl<StackPanel>("TabStripPanel")!.Children.OfType<Border>().First(b => b.Classes.Contains("glideTab"));
                    var headerBox = new Rect(header.TranslatePoint(new Point(0, 0), detached)!.Value, header.Bounds.Size);
                    Assert.True(headerBox.Contains(grab), $"the pointer {grab} (DIP in the new window) is not on its tab header {headerBox}");

                    // Dropped low and to the right on the desktop: the native move loop leaves the window there, then it is fitted.
                    detached.Position = new PixelPoint(700 - (int)(grab.X * scale), 500 - (int)(grab.Y * scale));
                    var dropped = detached.Position;
                    detached.FitWindowToWorkAreaAfterDrop(new PixelPoint(700, 500));
                    h.Pump();
                    detached.UpdateLayout();
                    var fitted = new PixelBox(detached.Position.X, detached.Position.Y, (int)Math.Round(detached.Width * scale), (int)Math.Round(detached.Height * scale));
                    Assert.True(fitted.Right <= work.Right && fitted.Bottom <= work.Bottom, $"{fitted} still reaches past the work area {work} (the taskbar)");
                    Assert.Equal(dropped, detached.Position);   // shrunk, not moved: the tab header stays under the pointer
                    Assert.True(headerBox.Contains(new Point((700 - fitted.X) / scale, (500 - fitted.Y) / scale)));
                }, work, 2.0);
            }
            finally
            {
                h.Window.WindowState = WindowState.Normal;
                detached?.Close();
            }
        });
    }

    [Fact]
    public async Task A_window_dropped_inside_the_work_area_is_left_alone()
    {
        await ExplorerHarness.Run(async h =>
        {
            var id = await OpenPictureAsync(h);
            var work = new PixelBox(0, 0, 2778, 1656);
            var before = Windows();
            MainWindow? detached = null;
            try
            {
                await ShowMonitorAsync(async () =>
                {
                    Assert.True(await (Task<bool>)h.Call("BeginHeldTabTearOffAsync", id, new PixelPoint(400, 22))!);
                    detached = NewWindowAfter(before);
                    detached.UpdateLayout();
                    var position = detached.Position; var width = detached.Width; var height = detached.Height;
                    detached.FitWindowToWorkAreaAfterDrop(new PixelPoint(position.X + 200, position.Y + 40));
                    Assert.Equal(position, detached.Position);
                    Assert.Equal(width, detached.Width);
                    Assert.Equal(height, detached.Height);
                }, work, 2.0);
            }
            finally { detached?.Close(); }
        });
    }

    // ---------------------------------------------------------------- 3: the title after an attach

    [Fact]
    public async Task The_window_title_counter_is_right_after_a_torn_off_tab_is_dragged_back()
    {
        await ExplorerHarness.Run(async h =>
        {
            var id = await OpenPictureAsync(h);
            var navigator = h.Get<Glide.App.Services.NavigationCoordinator>("_navigator");
            Assert.True(navigator.Count >= 3, $"the folder has {navigator.Count} pictures");
            var counter = $"[{navigator.Index + 1}/{navigator.Count}]";
            Assert.Contains(counter, h.Window.Title ?? "");

            var before = Windows();
            MainWindow? detached = null;
            MainWindow.PhysicalCursorForTests = () => ReleasedOnTheDesktop;
            try
            {
                Assert.True(await (Task<bool>)h.Call("BeginHeldTabTearOffAsync", id, new PixelPoint(400, 22))!);
                var torn = detached = NewWindowAfter(before);
                await h.WaitUntil(() => (torn.Title ?? "").Contains(counter), $"the torn-off window titled {counter} (it reads '{torn.Title}')");

                // Dragged back into the first window (as TabDragController's AttachAsync / the native drag's completion do).
                var tab = (TabState)typeof(MainWindow).GetMethod("RemoveTabForTransfer", Any)!.Invoke(torn, new object[] { id })!;
                await h.Window.AcceptTransferredTabAsync(tab, h.Workspace.Tabs.Count);
                await h.WaitUntil(() => (h.Window.Title ?? "").Contains(counter) && !(h.Window.Title ?? "").Contains("[1/1]"),
                    $"the window title carries {counter} again after the attach (it reads '{h.Window.Title}')");
            }
            finally { MainWindow.PhysicalCursorForTests = null; detached?.Close(); }
        });
    }

    [Fact]
    public async Task The_window_title_follows_the_folder_index_when_it_completes_after_the_first_frame()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenPictureAsync(h);
            var navigator = h.Get<Glide.App.Services.NavigationCoordinator>("_navigator");
            var counter = $"[{navigator.Index + 1}/{navigator.Count}]";
            // The state of a tab that was just attached: a one-entry list under the title the first frame set.
            var current = navigator.Current!;
            var folderList = navigator.Paths.ToArray();
            navigator.OpenSingleOnly(current);
            h.Call("UpdateWindowTitle", new object?[] { null });
            Assert.Contains("[1/1]", h.Window.Title ?? "");
            // The folder index arrives and the status bar / overlay refresh: the title is part of that refresh.
            navigator.ReplaceFolderIndex(folderList, current);
            h.Call("NotifyViewerStatusChanged");
            Assert.Contains(counter, h.Window.Title ?? "");
            Assert.DoesNotContain("[1/1]", h.Window.Title ?? "");
        });
    }
}
