using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>5.1.4: "Custom" text overlay position; presets always reset; schema 23 migration of per-preset offsets.</summary>
public sealed class TextOverlayCustomPositionTests
{
    [Fact]
    public void Custom_is_last_in_both_cards_and_matches_hotkey_choices()
    {
        Assert.Equal(TextOverlayPlacements.Custom, TextOverlayPlacements.TopTabsPositions[^1]);
        Assert.Equal(TextOverlayPlacements.Custom, TextOverlayPlacements.All[^1]);
        Assert.Equal(TextOverlayPlacements.TopTabsPositions, SettingActionCatalog.Choices["overlay.picturePositionTopTabs"]);
        Assert.Equal(TextOverlayPlacements.All, SettingActionCatalog.Choices["overlay.picturePositionSideTabs"]);
        Assert.True(TextOverlayPlacements.IsOverPicture(TextOverlayPlacements.Custom));
        Assert.Equal(TextOverlayPlacements.Custom, TextOverlayPlacements.NormalizeTopTabs("custom"));
    }

    [Fact]
    public void Presets_have_no_offset_and_custom_keeps_anchor_and_offset_per_card()
    {
        var s = new GlideSettingsState { PictureCounterPositionTopTabs = "Top right" };
        TextOverlayPlacements.SetCustom(s, sideTabs: false, "Bottom left", 12, -4);
        Assert.Equal(TextOverlayPlacements.Custom, s.PictureCounterPositionTopTabs);
        Assert.Equal("Bottom left", TextOverlayPlacements.PictureAnchor(s, false));
        Assert.Equal((12, -4), TextOverlayPlacements.Offset(s, false));
        Assert.Equal((0, 0), TextOverlayPlacements.Offset(s, true));   // side tabs card untouched

        s.PictureCounterPositionTopTabs = "Bottom left";   // choosing a preset: exact preset
        Assert.Equal((0, 0), TextOverlayPlacements.Offset(s, false));
        s.PictureCounterPositionTopTabs = TextOverlayPlacements.Custom;   // Custom remembered
        Assert.Equal((12, -4), TextOverlayPlacements.Offset(s, false));
    }

    [Fact]
    public void Offset_setting_edits_custom_or_turns_a_picture_preset_into_custom()
    {
        var s = new GlideSettingsState { PictureCounterPositionSideTabs = "Bottom center" };
        TextOverlayPlacements.SetOffset(s, true, 0, null);
        Assert.Equal("Bottom center", s.PictureCounterPositionSideTabs);
        TextOverlayPlacements.SetOffset(s, true, 7, null);
        Assert.Equal(TextOverlayPlacements.Custom, s.PictureCounterPositionSideTabs);
        Assert.Equal("Bottom center", TextOverlayPlacements.PictureAnchor(s, true));
        TextOverlayPlacements.SetOffset(s, true, null, -3);
        Assert.Equal((7, -3), TextOverlayPlacements.Offset(s, true));

        s.PictureCounterPositionSideTabs = TextOverlayPlacements.TabBarTop;   // bar positions ignore offsets
        TextOverlayPlacements.SetOffset(s, true, 5, 5);
        Assert.Equal(TextOverlayPlacements.TabBarTop, s.PictureCounterPositionSideTabs);
    }

    [Fact]
    public void Migration_turns_offset_presets_into_custom_at_the_same_spot()
    {
        var s = new GlideSettingsState
        {
            PictureCounterPositionTopTabs = "Top right",
            PictureCounterPositionSideTabs = "Bottom left",
            PictureCounterOffsets = "Top right=12,-4;Top left=3,3"
        };
        TextOverlayPlacements.MigratePerPresetOffsets(s);
        Assert.Equal(TextOverlayPlacements.Custom, s.PictureCounterPositionTopTabs);
        Assert.Equal("Top right", TextOverlayPlacements.PictureAnchor(s, false));
        Assert.Equal((12, -4), TextOverlayPlacements.Offset(s, false));
        Assert.Equal("Bottom left", s.PictureCounterPositionSideTabs);   // no offset on its preset
        Assert.DoesNotContain("Top left=", s.PictureCounterOffsets);
    }

    [Fact]
    public void Drop_picks_nearest_anchor_and_offset_from_its_preset_place()
    {
        // 1000x800 area, 100x40 overlay; side 15, top 28, bottom inset 50.
        var (a, x, y) = TextOverlayLayout.CustomFromDrop(1000, 800, 100, 40, 880, 30, 15, 28, 50);
        Assert.Equal(("Top right", -5, 2), (a, x, y));
        (a, x, y) = TextOverlayLayout.CustomFromDrop(1000, 800, 100, 40, 450, 700, 15, 28, 50);
        Assert.Equal(("Bottom center", 0, -10), (a, x, y));
        (a, x, y) = TextOverlayLayout.CustomFromDrop(1000, 800, 100, 40, -50, 2000, 15, 28, 50);   // kept inside
        Assert.Equal(("Bottom left", -15, 50), (a, x, y));
    }
}
