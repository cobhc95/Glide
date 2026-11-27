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
        Assert.Equal((99999, -99999), Glide.Core.Settings.TextOverlayOffsets.Get("Top left=99999,-99999", "Top left"));   // retest E: kept as typed; limited only when drawn
        Assert.Equal((0, 0), Glide.Core.Settings.TextOverlayOffsets.Get("garbage;=;x=1", "Top left"));
    }

    [Fact]
    public void Text_overlay_placements_are_one_master_list_per_tab_layout()
    {
        Assert.Equal(Glide.Core.Settings.TextOverlayPlacements.TopTabsPositions, Glide.Core.Settings.SettingActionCatalog.Choices["overlay.picturePositionTopTabs"]);
        Assert.Equal(Glide.Core.Settings.TextOverlayPlacements.All, Glide.Core.Settings.SettingActionCatalog.Choices["overlay.picturePositionSideTabs"]);
        Assert.Equal(Glide.Core.Settings.TextOverlayPlacements.Alignments, Glide.Core.Settings.SettingActionCatalog.Choices["overlay.pictureAlignmentTopTabs"]);
        Assert.Equal(Glide.Core.Settings.TextOverlayPlacements.Alignments, Glide.Core.Settings.SettingActionCatalog.Choices["overlay.pictureAlignmentSideTabs"]);
        Assert.DoesNotContain(Glide.Core.Settings.TextOverlayPlacements.TopTabsPositions, Glide.Core.Settings.TextOverlayPlacements.IsTabBar);
        var state = new Glide.Core.Settings.GlideSettingsState();
        Assert.Equal("Top right", state.PictureCounterPositionTopTabs);
        Assert.Equal("Top right", state.PictureCounterPositionSideTabs);
        Assert.Equal("Title bar – left", Glide.Core.Settings.TextOverlayPlacements.Normalize(" title BAR - left "));
        Assert.Equal("Top right", Glide.Core.Settings.TextOverlayPlacements.Normalize("nonsense"));
        Assert.Equal("Top right", Glide.Core.Settings.TextOverlayPlacements.NormalizeTopTabs("Vertical tabs – top"));
    }

    [Fact]
    public void Text_overlay_uses_the_card_matching_the_tab_layout()
    {
        var s = new Glide.Core.Settings.GlideSettingsState
        {
            PictureCounterPositionTopTabs = "Title bar – center", PictureCounterAlignmentTopTabs = "Automatic",
            PictureCounterPositionSideTabs = "Vertical tabs – top", PictureCounterAlignmentSideTabs = "Center"
        };
        Assert.Equal("Title bar – center", Glide.Core.Settings.TextOverlayPlacements.Position(s, sideTabs: false));
        Assert.Equal("Automatic", Glide.Core.Settings.TextOverlayPlacements.Alignment(s, sideTabs: false));
        Assert.Equal("Vertical tabs – top", Glide.Core.Settings.TextOverlayPlacements.Position(s, sideTabs: true));
        Assert.Equal("Center", Glide.Core.Settings.TextOverlayPlacements.Alignment(s, sideTabs: true));
        s.TabStripPosition = "Side";
        Assert.True(Glide.Core.Settings.TextOverlayPlacements.SideTabsLayout(s));
        s.TabsEnabled = false;
        Assert.False(Glide.Core.Settings.TextOverlayPlacements.SideTabsLayout(s));
    }

    [Theory]
    [InlineData("{\"SettingsSchemaVersion\":20,\"PictureCounterPosition\":\"Bottom left\",\"PictureCounterSideTabsPlacement\":\"Top of the tab bar\",\"PictureCounterAlignment\":\"Right\"}", "Bottom left", "Right", "Vertical tabs – top", "Right")]
    [InlineData("{\"SettingsSchemaVersion\":20,\"PictureCounterPosition\":\"Bottom left\",\"PictureCounterSideTabsPlacement\":\"Bottom of the tab bar\"}", "Bottom left", "Automatic", "Vertical tabs – bottom", "Automatic")]
    [InlineData("{\"SettingsSchemaVersion\":20,\"PictureCounterPosition\":\"Bottom left\",\"PictureCounterSideTabsPlacement\":\"Title bar\",\"PictureCounterAlignment\":\"Center\"}", "Bottom left", "Center", "Title bar – center", "Center")]
    [InlineData("{\"SettingsSchemaVersion\":20,\"PictureCounterPosition\":\"Bottom left\",\"PictureCounterSideTabsPlacement\":\"Over the picture\"}", "Bottom left", "Automatic", "Bottom left", "Automatic")]
    [InlineData("{\"SettingsSchemaVersion\":20,\"PictureCounterPosition\":\"Bottom left\",\"PictureCounterAlignment\":\"Left\"}", "Bottom left", "Left", "Bottom left", "Left")]
    [InlineData("{\"SettingsSchemaVersion\":12,\"PictureCounterPosition\":\"Top center\"}", "Top center", "Automatic", "Top center", "Automatic")]
    [InlineData("{\"SettingsSchemaVersion\":21,\"PictureCounterPosition\":\"Bottom center\",\"PictureCounterAlignment\":\"Right\",\"PictureCounterTitleBarAlignment\":\"Left\",\"PictureCounterTabBarAlignment\":\"Center\"}", "Bottom center", "Right", "Bottom center", "Right")]
    [InlineData("{\"SettingsSchemaVersion\":21,\"PictureCounterPosition\":\"Title bar – right\",\"PictureCounterAlignment\":\"Right\",\"PictureCounterTitleBarAlignment\":\"Left\",\"PictureCounterTabBarAlignment\":\"Center\"}", "Title bar – right", "Left", "Title bar – right", "Left")]
    [InlineData("{\"SettingsSchemaVersion\":21,\"PictureCounterPosition\":\"Vertical tabs – bottom\",\"PictureCounterAlignment\":\"Right\",\"PictureCounterTitleBarAlignment\":\"Left\",\"PictureCounterTabBarAlignment\":\"Center\"}", "Top right", "Right", "Vertical tabs – bottom", "Center")]
    public void Text_overlay_placement_migrates_into_both_tab_layout_cards(string json, string topPosition, string topAlignment, string sidePosition, string sideAlignment)
    {
        var loaded = Glide.App.Settings.SettingsStore.MigrateFromJson(json, isSettingsFile: false);
        Assert.Equal(topPosition, loaded.PictureCounterPositionTopTabs);
        Assert.Equal(topAlignment, loaded.PictureCounterAlignmentTopTabs);
        Assert.Equal(sidePosition, loaded.PictureCounterPositionSideTabs);
        Assert.Equal(sideAlignment, loaded.PictureCounterAlignmentSideTabs);
    }

    [Fact]
    public void Text_overlay_tab_layout_cards_survive_a_current_schema_round_trip()
    {
        var loaded = Glide.App.Settings.SettingsStore.MigrateFromJson("{\"SettingsSchemaVersion\":22,\"PictureCounterPositionTopTabs\":\"Vertical tabs – top\",\"PictureCounterPositionSideTabs\":\"Vertical tabs – bottom\",\"PictureCounterAlignmentSideTabs\":\"Right\"}", isSettingsFile: false);
        Assert.Equal("Top right", loaded.PictureCounterPositionTopTabs);
        Assert.Equal("Vertical tabs – bottom", loaded.PictureCounterPositionSideTabs);
        Assert.Equal("Right", loaded.PictureCounterAlignmentSideTabs);
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
    public void Overlay_opacity_track_is_the_picture_in_picture_track_with_a_35_percent_floor()
    {
        var slider = new Rect(8, 50, 22, 130);
        var inset = OverlayFrameGeometry.OpacityTrackInset;
        // Same dot position as a picture-in-picture overlay of the same opacity: opacity x the track length.
        Assert.Equal(slider.Bottom - inset - (slider.Height - 2 * inset) * 0.35, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, 0.35), 6);
        Assert.Equal(OverlayFrameGeometry.OpacityKnobY(slider, 0.6), OverlayChromePolicy.WholeWindowOpacityTrackY(slider, 0.6), 6);
        Assert.Equal(slider.Top + inset, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, 1.0), 6);
        Assert.Equal(0.35, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, slider.Bottom + 40), 6);
        Assert.Equal(1.0, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, slider.Top), 6);
        for (var v = 0.35; v <= 1.0; v += 0.05)
            Assert.Equal(v, OverlayChromePolicy.WholeWindowOpacityFromTrackY(slider, OverlayChromePolicy.WholeWindowOpacityTrackY(slider, v)), 6);
    }
}
