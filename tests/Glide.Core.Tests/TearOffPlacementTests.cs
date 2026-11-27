namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 desktop retest: a torn-off tab window was not under the pointer and ran past the bottom of the work area (2778 x 1736 physical
/// at 200 %, taskbar below). The pure placement maths at 200 % with a work area smaller than the source window.
/// </summary>
[Trait("Area", "Window")]
public sealed class TearOffPlacementTests
{
    // Work area of a 2778 x 1736 primary display at 200 % with the taskbar (80 px) at the bottom.
    private static readonly PixelBox Work = new(0, 0, 2778, 1656);
    private const double Scale = 2.0;

    [Fact]
    public void At_200_percent_the_tab_header_is_under_the_pointer_in_physical_pixels()
    {
        // The tab left the strip at 400,22. The new tab header's centre is 128 x 22 DIPs from the new window's top-left.
        var box = DetachedWindowPlacement.ForTearOff(700, 500, Scale, 128, 22, 1096, 500, Work);
        Assert.Equal(700 - 256, box.X);   // 128 DIP = 256 physical pixels, not 128
        Assert.Equal(500 - 44, box.Y);
        Assert.Equal(2192, box.Width);
        Assert.Equal(1000, box.Height);
        Assert.True(box.Bottom <= Work.Bottom);
    }

    [Fact]
    public void A_window_taller_than_the_work_area_below_the_pointer_is_moved_up_not_left_past_the_taskbar()
    {
        var box = DetachedWindowPlacement.ForTearOff(700, 500, Scale, 128, 22, 1096, 817, Work);   // 1634 physical tall, 456 + 1634 > 1656
        Assert.True(box.Right <= Work.Right && box.Bottom <= Work.Bottom && box.X >= Work.X && box.Y >= Work.Y, $"{box} is outside {Work}");
    }

    [Fact]
    public void A_work_area_smaller_than_the_source_window_shrinks_the_new_window_to_fit()
    {
        var small = new PixelBox(0, 0, 2200, 1500);
        var box = DetachedWindowPlacement.ForTearOff(400, 22, Scale, 128, 22, 1200, 800, small);   // 2400 x 1600 physical
        Assert.True(box.Right <= small.Right && box.Bottom <= small.Bottom && box.X >= 0 && box.Y >= 0, $"{box} is outside {small}");
        Assert.True(box.Width < 2400 && box.Height < 1600);
    }

    [Fact]
    public void The_same_tear_off_at_100_percent_uses_half_the_pixels()
    {
        var at100 = DetachedWindowPlacement.ForTearOff(700, 500, 1.0, 128, 22, 1096, 300, new PixelBox(0, 0, 2000, 1000));
        Assert.Equal(700 - 128, at100.X);
        Assert.Equal(500 - 22, at100.Y);
        Assert.Equal(1096, at100.Width);
    }

    [Fact]
    public void A_window_dropped_low_on_the_screen_gives_up_height_and_keeps_the_header_under_the_pointer()
    {
        // The held tear-off's native move loop dropped a 2192 x 1634 window with its top-left at 444,456 (pointer 700,500).
        var dropped = new PixelBox(444, 456, 2192, 1634);
        var fit = DetachedWindowPlacement.FitAfterDrop(dropped, Work, 360, 520);
        Assert.Equal(new PixelBox(444, 456, 2192, 1200), fit);
        Assert.Equal(Work.Bottom, fit.Bottom);
        // The pointer is still where the header is: same top-left, same grab offset (256, 44 physical).
        Assert.Equal((256, 44), (700 - fit.X, 500 - fit.Y));
    }

    [Fact]
    public void A_window_dropped_far_right_gives_up_width()
    {
        var fit = DetachedWindowPlacement.FitAfterDrop(new PixelBox(1900, 100, 2192, 800), Work, 360, 520);
        Assert.Equal(new PixelBox(1900, 100, 878, 800), fit);
    }

    [Fact]
    public void A_drop_so_low_that_the_minimum_size_does_not_fit_shifts_the_window_up_after_shrinking_to_the_minimum()
    {
        var fit = DetachedWindowPlacement.FitAfterDrop(new PixelBox(300, 1600, 2000, 1500), Work, 360, 520);
        Assert.Equal(520, fit.Height);
        Assert.Equal(Work.Bottom, fit.Bottom);
        Assert.Equal(300, fit.X);
    }

    [Fact]
    public void A_window_partly_off_the_left_or_top_edge_moves_in_and_one_inside_is_unchanged()
    {
        Assert.Equal(new PixelBox(0, 0, 1000, 800), DetachedWindowPlacement.FitAfterDrop(new PixelBox(-300, -40, 1000, 800), Work, 360, 520));
        var inside = new PixelBox(200, 150, 1500, 900);
        Assert.Equal(inside, DetachedWindowPlacement.FitAfterDrop(inside, Work, 360, 520));
    }

    [Fact]
    public void A_window_bigger_than_the_work_area_is_cut_to_it()
    {
        var fit = DetachedWindowPlacement.FitAfterDrop(new PixelBox(50, 50, 4000, 3000), Work, 360, 520);
        Assert.True(fit.X >= Work.X && fit.Y >= Work.Y && fit.Right <= Work.Right && fit.Bottom <= Work.Bottom, $"{fit} is outside {Work}");
    }

    [Theory]
    [InlineData(-1920, 0)]      // monitor left of the primary
    [InlineData(0, -1080)]      // monitor above it
    public void The_fit_after_the_drop_works_on_monitors_with_negative_coordinates(int workX, int workY)
    {
        var work = new PixelBox(workX, workY, 1920, 1040);
        var fit = DetachedWindowPlacement.FitAfterDrop(new PixelBox(workX + 600, workY + 500, 1800, 1200), work, 360, 520);
        Assert.True(fit.X >= work.X && fit.Y >= work.Y && fit.Right <= work.Right && fit.Bottom <= work.Bottom, $"{fit} is outside {work}");
        Assert.Equal((workX + 600, workY + 500), (fit.X, fit.Y));
    }

    [Fact]
    public void The_window_made_for_a_moved_tab_is_always_a_normal_window_and_is_fitted_when_dropped()
    {
        var tabs = SourceText.Read("src/Glide.App/MainWindow.Tabs.cs");
        var place = tabs[tabs.IndexOf("private void PlaceDetachedWindow(", StringComparison.Ordinal)..];
        place = place[..place.IndexOf("internal static Func<PixelPoint", StringComparison.Ordinal)];
        Assert.Contains("detached.WindowState = WindowState.Normal;", place);   // copied as maximized, Windows restored it at its saved box
        var run = tabs[tabs.IndexOf("private async Task RunNativeDetachedTabDragAsync", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("private void NativeDetachedPositionChanged", StringComparison.Ordinal)];
        Assert.Contains("FitWindowToWorkAreaAfterDrop(releaseCursor)", run);
        Assert.True(run.IndexOf("FitWindowToWorkAreaAfterDrop(", StringComparison.Ordinal) < run.IndexOf("CompleteNativeDetachedTabDragAsync(tabId", StringComparison.Ordinal));
    }
}
