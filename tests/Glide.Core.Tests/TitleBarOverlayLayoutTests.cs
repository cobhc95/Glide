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

    // 5.1.4 flicker: DesiredSize includes the margin the layout sets; feeding the result back must give the same result.
    [Theory]
    [InlineData(TextOverlayPlacements.TitleBarLeft)]
    [InlineData(TextOverlayPlacements.TitleBarRight)]
    [InlineData(TextOverlayPlacements.TitleBarCenter)]
    public void Layout_is_stable_when_its_result_is_fed_back(string placement)
    {
        const double content = 87.4, chromeLeft = 4;
        foreach (var tabs in new[] { 0.0, 300, 2000 })
        {
            double marginLeft = chromeLeft;
            TitleBarOverlayLayout? previous = null;
            for (var i = 0; i < 6; i++)
            {
                var desired = content + marginLeft + (i % 2) * 0.3; // measured with the current margin, plus sub-pixel jitter
                var width = TitleBarOverlayLayout.ContentWidth(desired, marginLeft, 0);
                var l = TitleBarOverlayLayout.Compute(placement, width, 1000.3, 200.2, tabs);
                if (previous is { } p) Assert.Equal(p, l);
                previous = l;
                marginLeft = chromeLeft + l.OverlayX;
            }
        }
    }
}
