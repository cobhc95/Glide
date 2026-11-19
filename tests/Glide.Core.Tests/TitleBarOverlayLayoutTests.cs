using Glide.Core.Settings;

namespace Glide.Core.Tests;

[Trait("Area", "Overlay")]
public sealed class TitleBarOverlayLayoutTests
{
    // Title bar 1000 wide, right-hand buttons 200 wide: tabs may use 0..800.
    [Fact]
    public void Left_pushes_tabs_right_by_overlay_width()
    {
        var l = TitleBarOverlayLayout.Compute(TextOverlayPlacements.TitleBarLeft, 60, 1000, 200, 300);
        Assert.Equal(0, l.OverlayX);
        Assert.Equal(60 + TitleBarOverlayLayout.Padding, l.TabsLeftReserve);
        Assert.Equal(0, l.TabsRightReserve);
    }

    [Fact]
    public void Right_sits_before_buttons_and_reserves_tab_space()
    {
        var l = TitleBarOverlayLayout.Compute(TextOverlayPlacements.TitleBarRight, 60, 1000, 200, 300);
        Assert.True(l.OverlayX + 60 <= 800);
        Assert.True(l.OverlayX >= 800 - l.TabsRightReserve);
        Assert.Equal(0, l.TabsLeftReserve);
    }

    [Fact]
    public void Center_uses_free_space_or_reserves_when_tabs_reach_it()
    {
        var free = TitleBarOverlayLayout.Compute(TextOverlayPlacements.TitleBarCenter, 60, 1000, 200, 300);
        Assert.Equal(470, free.OverlayX);
        Assert.Equal(0, free.TabsRightReserve);

        var crowded = TitleBarOverlayLayout.Compute(TextOverlayPlacements.TitleBarCenter, 60, 1000, 200, 700);
        Assert.True(crowded.TabsRightReserve >= 60);
        Assert.True(crowded.OverlayX >= 800 - crowded.TabsRightReserve && crowded.OverlayX + 60 <= 800);
    }
}
