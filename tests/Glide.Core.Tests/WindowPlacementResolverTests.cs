using Glide.Core.Windows;

namespace Glide.Core.Tests;

/// <summary>The shared, pure clamp/validate rules every remembering window uses (Glide.Core WindowPlacementResolver).</summary>
public sealed class WindowPlacementResolverTests
{
    private static readonly ScreenWorkArea Full = new(0, 0, 1920, 1040, 1.0);
    private static readonly ScreenWorkArea Laptop = new(0, 0, 1366, 728, 1.5);
    private static readonly ScreenWorkArea Second = new(1920, 0, 2560, 1400, 1.0);
    private static readonly ScreenWorkArea LeftHiDpi = new(-3840, 0, 3840, 2100, 2.0);

    private static SavedWindowPlacement Saved(int x, int y, double w = 0, double h = 0, bool max = false) => new(x, y, w, h, max);

    private static ResolvedWindowPlacement? Size(SavedWindowPlacement saved, params ScreenWorkArea[] screens) =>
        WindowPlacementResolver.Resolve(saved, screens, screens.Length > 0 ? screens[0] : default, WindowPlacementMode.SizeAndPosition, 880, 560);

    [Fact]
    public void Nothing_saved_or_garbage_means_keep_the_window_default()
    {
        Assert.Null(Size(SavedWindowPlacement.None, Full));
        Assert.Null(Size(Saved(10, 10, 0, 0), Full));
        Assert.Null(Size(Saved(10, 10, double.NaN, 500), Full));
        Assert.Null(Size(Saved(10, 10, double.PositiveInfinity, 500), Full));
        Assert.Null(WindowPlacementResolver.Resolve(SavedWindowPlacement.None, [Full], Full, WindowPlacementMode.PositionOnly, 0, 0, 470, 200));
        // No monitors at all: never guess.
        Assert.Null(WindowPlacementResolver.Resolve(Saved(10, 10, 1000, 700), [], default, WindowPlacementMode.SizeAndPosition, 0, 0));
    }

    [Fact]
    public void A_valid_placement_is_restored_exactly()
    {
        var r = Size(Saved(200, 120, 1100, 700), Full)!.Value;
        Assert.Equal(1100, r.Width);
        Assert.Equal(700, r.Height);
        Assert.Equal((200, 120), (r.X!.Value, r.Y!.Value));
        Assert.Equal(880, r.MinWidth);
        Assert.False(r.Maximized);
    }

    [Fact]
    public void Oversized_is_clamped_to_the_dpi_scaled_working_area_and_kept_fully_inside()
    {
        var r = Size(Saved(0, 0, 4000, 3000), Laptop)!.Value;
        Assert.True(r.Width <= Laptop.Width / 1.5 && r.Height <= Laptop.Height / 1.5, $"{r.Width}x{r.Height}");
        Assert.True(r.MinWidth <= r.Width && r.MinHeight <= r.Height, "minimum never exceeds the size the screen allows");
        Assert.True(r.X >= Laptop.X && r.Y >= Laptop.Y);
        Assert.True(r.X + (r.Width + WindowPlacementResolver.ChromeAllowanceWidth) * 1.5 <= Laptop.Right + 1);
    }

    [Fact]
    public void Tiny_sizes_become_the_minimum_size()
    {
        var r = Size(Saved(100, 100, 120, 90), Full)!.Value;
        Assert.Equal(880, r.Width);
        Assert.Equal(560, r.Height);
    }

    [Fact]
    public void Partly_off_screen_is_moved_inside_and_off_screen_or_gone_monitor_loses_the_position()
    {
        var r = Size(Saved(1500, 900, 930, 630), Full)!.Value;
        Assert.True(r.X + 930 <= Full.Right && r.Y + 630 <= Full.Bottom);
        Assert.Equal(0, Size(Saved(-300, 20, 930, 630), Full)!.Value.X);
        Assert.False(Size(Saved(100, -400, 930, 630), Full)!.Value.HasPosition); // title bar above the screen
        Assert.False(Size(Saved(6000, 4000, 1000, 700), Full)!.Value.HasPosition); // entirely off-screen
        var onSecond = Saved(2200, 150, 1000, 700);
        Assert.False(Size(onSecond, Full)!.Value.HasPosition); // monitor unplugged -> centre on owner
        var present = Size(onSecond, Full, Second)!.Value;
        Assert.Equal((2200, 150), (present.X!.Value, present.Y!.Value));
    }

    [Fact]
    public void A_negative_coordinate_monitor_at_200_percent_is_respected()
    {
        var r = Size(Saved(-3000, 200, 1200, 800), Full, LeftHiDpi)!.Value;
        Assert.Equal((-3000, 200), (r.X!.Value, r.Y!.Value));
        Assert.Equal(1200, r.Width);
        // 1800 DIPs at 200 % is 3600 device pixels: fits; 2000 DIPs does not and is clamped to the 3840 px area.
        var wide = Size(Saved(-3840, 0, 2000, 900), Full, LeftHiDpi)!.Value;
        Assert.True(wide.Width <= 3840 / 2.0 - WindowPlacementResolver.ChromeAllowanceWidth, $"{wide.Width}");
    }

    [Fact]
    public void Maximized_is_restored_only_for_windows_that_remember_their_size()
    {
        Assert.True(Size(Saved(150, 100, 1000, 680, max: true), Full)!.Value.Maximized);
        var dialog = WindowPlacementResolver.Resolve(Saved(150, 100, 1000, 680, max: true), [Full], Full, WindowPlacementMode.PositionOnly, 0, 0, 470, 230)!.Value;
        Assert.False(dialog.Maximized);
    }

    [Fact]
    public void Position_only_restores_the_position_never_the_size_and_keeps_the_dialog_on_screen()
    {
        var r = WindowPlacementResolver.Resolve(Saved(300, 200), [Full], Full, WindowPlacementMode.PositionOnly, 380, 0, 470, 230)!.Value;
        Assert.Null(r.Width);
        Assert.Null(r.Height);
        Assert.Equal((300, 200), (r.X!.Value, r.Y!.Value));
        var corner = WindowPlacementResolver.Resolve(Saved(1600, 950), [Full], Full, WindowPlacementMode.PositionOnly, 0, 0, 470, 230)!.Value;
        Assert.True(corner.X + 470 <= Full.Right && corner.Y + 230 <= Full.Bottom, $"{corner.X},{corner.Y}");
        // Only a corner of the title bar left on screen (cannot be grabbed): treated as off-screen, centred on the owner.
        Assert.Null(WindowPlacementResolver.Resolve(Saved(1900, 1030), [Full], Full, WindowPlacementMode.PositionOnly, 0, 0, 470, 230));
        // Off-screen: nothing to apply, the dialog centres on its owner.
        Assert.Null(WindowPlacementResolver.Resolve(Saved(9000, 9000), [Full], Full, WindowPlacementMode.PositionOnly, 0, 0, 470, 230));
    }
}
