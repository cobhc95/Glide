namespace Glide.Core.Tests;

[Trait("Area", "Tabs")]
public sealed class TabStripControllerTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void Touch_puts_the_active_tab_first_and_drops_closed_tabs()
    {
        var strip = new TabStripController();
        strip.Touch([A, B, C], A);
        strip.Touch([A, B, C], C);
        Assert.Equal([C, A, B], strip.Mru);
        strip.Touch([A, C], C);
        Assert.Equal([C, A], strip.Mru);
    }

    [Fact]
    public void Held_ctrl_walks_the_snapshot_and_release_commits_the_landing_tab()
    {
        var strip = new TabStripController();
        strip.Touch([A, B, C], A);
        strip.Touch([A, B, C], B);
        strip.Touch([A, B, C], C);              // MRU: C, B, A
        Assert.Equal(B, strip.NextInCycle([A, B, C], C, 1, holdingCtrl: true));
        Assert.True(strip.Cycling);
        strip.TouchUnlessCycling([A, B, C], B); // activation during the cycle does not reorder
        Assert.Equal(A, strip.NextInCycle([A, B, C], B, 1, holdingCtrl: true));
        Assert.Equal(C, strip.NextInCycle([A, B, C], A, 1, holdingCtrl: true)); // wraps
        Assert.Equal(A, strip.NextInCycle([A, B, C], C, -1, holdingCtrl: true));
        strip.EndCycle([A, B, C], A);
        Assert.False(strip.Cycling);
        Assert.Equal([A, C, B], strip.Mru);
    }

    [Fact]
    public void Without_ctrl_each_press_is_one_complete_switch()
    {
        var strip = new TabStripController();
        strip.Touch([A, B, C], A);
        strip.Touch([A, B, C], B);              // MRU: B, A, C
        Assert.Equal(A, strip.NextInCycle([A, B, C], B, 1, holdingCtrl: false));
        Assert.False(strip.Cycling);
        strip.Touch([A, B, C], A);              // MRU: A, B, C
        Assert.Equal(B, strip.NextInCycle([A, B, C], A, 1, holdingCtrl: false)); // toggles back
    }

    [Theory]
    [InlineData(true, 500, 400, 0, false, false)]
    [InlineData(true, 500, 1000, 0, false, true)]
    [InlineData(true, 500, 1000, 250, true, true)]
    [InlineData(true, 500, 1000, 500, true, false)]
    [InlineData(true, 500, 501, 0, false, false)]
    [InlineData(false, 500, 1000, 250, false, false)]
    public void Overflow_arrows_follow_the_scroll_position(bool enabled, double viewport, double extent, double offset, bool left, bool right)
        => Assert.Equal((left, right), TabStripController.OverflowArrows(enabled, viewport, extent, offset));

    [Fact]
    public void Overflow_arrows_are_hidden_for_side_tabs_and_narrow_windows()
    {
        Assert.True(TabStripController.OverflowArrowsAllowed(false, true, true, 330));
        Assert.False(TabStripController.OverflowArrowsAllowed(true, true, true, 800));
        Assert.False(TabStripController.OverflowArrowsAllowed(false, true, true, 329));
        Assert.False(TabStripController.OverflowArrowsAllowed(false, false, true, 800));
        Assert.False(TabStripController.OverflowArrowsAllowed(false, true, false, 800));
    }

    [Fact]
    public void Scroll_step_is_about_one_tab_and_clamped()
    {
        Assert.Equal(223, TabStripController.ScrollOffset(1000, 3000, 0, 1, 220));
        Assert.Equal(90, TabStripController.ScrollOffset(100, 3000, 0, 1, 220));       // minimum step
        Assert.Equal(0, TabStripController.ScrollOffset(1000, 3000, 50, -1, 220));
        Assert.Equal(2000, TabStripController.ScrollOffset(1000, 3000, 1950, 1, 220));  // max offset
    }

    [Fact]
    public void Neighbours_slide_with_smoothstep_between_a_quarter_and_three_quarters()
    {
        var none = TabStripController.NeighbourOffsets(1, 4, 20, 100);
        Assert.All(none, o => Assert.Equal(0, o));
        var half = TabStripController.NeighbourOffsets(1, 4, 50, 100);
        Assert.Equal(-50, half[2], 6);
        Assert.Equal(0, half[3]);
        var full = TabStripController.NeighbourOffsets(1, 4, 80, 100);
        Assert.Equal(-100, full[2], 6);
        var left = TabStripController.NeighbourOffsets(2, 4, -150, 100);
        Assert.Equal(100, left[1], 6);
        Assert.Equal(50, left[0], 6);
        Assert.Equal(0, left[2]);
    }

    [Fact]
    public void Tear_off_triggers_about_36_dip_beyond_the_strip_edge()
    {
        Assert.Equal(36, TabStripController.TearOffDistanceDip);
        // horizontal strip at y 100..140, scale 1 => tolerance 36 beyond either edge
        Assert.False(TabStripController.BeyondTearOffThreshold(true, false, 500, 176, 0, 100, 1000, 40, 1.0));
        Assert.True(TabStripController.BeyondTearOffThreshold(true, false, 500, 177, 0, 100, 1000, 40, 1.0));
        Assert.False(TabStripController.BeyondTearOffThreshold(true, false, 500, 64, 0, 100, 1000, 40, 1.0));
        Assert.True(TabStripController.BeyondTearOffThreshold(true, false, 500, 63, 0, 100, 1000, 40, 1.0));
        // reordering along the strip never detaches, however far
        Assert.False(TabStripController.BeyondTearOffThreshold(true, false, 99999, 120, 0, 100, 1000, 40, 1.0));
        Assert.True(TabStripController.BeyondTearOffThreshold(false, false, 500, 120, 0, 100, 1000, 40, 1.0));
        // side tabs tear off sideways (strip x 0..200); 2x scale => 72 physical px
        Assert.False(TabStripController.BeyondTearOffThreshold(true, true, 272, 9999, 0, 0, 200, 800, 2.0));
        Assert.True(TabStripController.BeyondTearOffThreshold(true, true, 273, 0, 0, 0, 200, 800, 2.0));
        Assert.True(TabStripController.BeyondTearOffThreshold(true, true, -73, 0, 0, 0, 200, 800, 2.0));
        // invalid scale falls back to 1
        Assert.False(TabStripController.BeyondTearOffThreshold(true, false, 0, 176, 0, 100, 1000, 40, 0));
        Assert.True(TabStripController.BeyondTearOffThreshold(true, false, 0, 177, 0, 100, 1000, 40, 0));
    }
}
