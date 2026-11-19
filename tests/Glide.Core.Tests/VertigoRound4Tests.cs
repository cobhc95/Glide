using Avalonia;
using Glide.App.Controls;

namespace Glide.Core.Tests;

[Trait("Area", "Overlay")]
public sealed class VertigoRound4Tests
{
    [Fact]
    public void Text_overlay_offsets_are_remembered_per_anchor()
    {
        var map = Glide.Core.Settings.TextOverlayOffsets.Set("", "Top right", 12, -4);
        map = Glide.Core.Settings.TextOverlayOffsets.Set(map, "Bottom left", 0, 30);
        Assert.Equal((12, -4), Glide.Core.Settings.TextOverlayOffsets.Get(map, "Top right"));
        Assert.Equal((0, 30), Glide.Core.Settings.TextOverlayOffsets.Get(map, "bottom left"));
        Assert.Equal((0, 0), Glide.Core.Settings.TextOverlayOffsets.Get(map, "Top center"));
        map = Glide.Core.Settings.TextOverlayOffsets.SetX(map, "Top right", 5);
        Assert.Equal((5, -4), Glide.Core.Settings.TextOverlayOffsets.Get(map, "Top right"));
        map = Glide.Core.Settings.TextOverlayOffsets.Set(map, "Top right", 0, 0);
        Assert.Equal("Bottom left=0,30", map);
        Assert.Equal((4000, -4000), Glide.Core.Settings.TextOverlayOffsets.Get("Top left=99999,-99999", "Top left"));
        Assert.Equal((0, 0), Glide.Core.Settings.TextOverlayOffsets.Get("garbage;=;x=1", "Top left"));
    }

    [Fact]
    public void Text_overlay_side_tab_placements_are_one_master_list()
    {
        Assert.Equal(Glide.Core.Settings.TextOverlayPlacements.All, Glide.Core.Settings.SettingActionCatalog.Choices["overlay.pictureSideTabsPlacement"]);
        Assert.Equal("Over the picture", new Glide.Core.Settings.GlideSettingsState().PictureCounterSideTabsPlacement);
        Assert.Equal("Title bar", Glide.Core.Settings.TextOverlayPlacements.Normalize(" title BAR "));
        Assert.Equal("Over the picture", Glide.Core.Settings.TextOverlayPlacements.Normalize("nonsense"));
    }

    [Fact]
    public void Top_centre_grip_zone_is_the_middle_quarter_of_the_top_edge()
    {
        var picture = new Rect(100, 50, 400, 300);   // zone x: 250..350
        Assert.True(OverlayChromePolicy.IsInTopCentreGripZone(picture, new Point(300, 55)));
        Assert.True(OverlayChromePolicy.IsInTopCentreGripZone(picture, new Point(251, 80)));
        Assert.False(OverlayChromePolicy.IsInTopCentreGripZone(picture, new Point(240, 55)));
        Assert.False(OverlayChromePolicy.IsInTopCentreGripZone(picture, new Point(360, 55)));
        Assert.False(OverlayChromePolicy.IsInTopCentreGripZone(picture, new Point(300, 150)));
        Assert.True(new Glide.Core.Settings.GlideSettingsState().WholeAppOverlayTopCentreGrip);
    }

    [Fact]
    public void Overlay_opacity_track_spans_the_bar_and_maps_35_to_100_percent()
    {
        var slider = new Rect(8, 50, 22, 130);
        var inset = OverlayChromePolicy.WholeWindowOpacityTrackInsetDip;
        Assert.Equal(slider.Bottom - inset, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, 0.35), 6);
        Assert.Equal(slider.Top + inset, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, 1.0), 6);
        Assert.Equal(0.35, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, slider.Bottom + 40), 6);
        Assert.Equal(1.0, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, slider.Top), 6);
        for (var v = 0.35; v <= 1.0; v += 0.05)
            Assert.Equal(v, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, v)), 6);
    }
}
