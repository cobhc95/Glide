using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Characterisation of the per-surface gesture precedence (docs/audits/stage-d-gesture-precedence.md).</summary>
[Trait("Area", "Gestures")]
public sealed class GestureResolverTests
{
    private static Func<string, string> Map(params (string Slot, string Action)[] rows)
    {
        var map = rows.ToDictionary(r => r.Slot, r => r.Action);
        return slot => map.TryGetValue(slot, out var a) ? a : GestureCatalog.Default;
    }

    [Theory]
    [InlineData(false, false, false, false, "wheel.windowed")]
    [InlineData(true, false, false, false, "wheel.fullscreen")]
    [InlineData(false, true, true, true, "wheel.windowedCtrl")]
    [InlineData(false, false, true, true, "wheel.windowedShift")]
    [InlineData(true, false, false, true, "wheel.fullscreenAlt")]
    public void Wheel_slot_follows_ctrl_then_shift_then_alt(bool fs, bool ctrl, bool shift, bool alt, string expected)
        => Assert.Equal(expected, GestureResolver.WheelSlot(fs, ctrl, shift, alt));

    [Fact]
    public void Shift_and_alt_wheel_on_default_act_as_the_plain_wheel_but_ctrl_does_not()
    {
        var resolve = Map(("wheel.windowed", GestureCatalog.BrowseWheel));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureResolver.WheelAction(resolve, false, false, true, false, false, false));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureResolver.WheelAction(resolve, false, false, false, true, false, false));
        Assert.Equal(GestureCatalog.Default, GestureResolver.WheelAction(resolve, false, true, false, false, false, false));
    }

    [Fact]
    public void Right_held_beats_slideshow_which_beats_the_held_key_row()
    {
        var resolve = Map(("wheel.windowed", GestureCatalog.ZoomWheel), ("slideshow.wheel", GestureCatalog.BrowseWheel), ("wheel.rightHeld", GestureCatalog.TabWheel));
        Assert.Equal(GestureCatalog.ZoomWheel, GestureResolver.WheelAction(resolve, false, false, false, false, false, false));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureResolver.WheelAction(resolve, false, false, false, false, true, false));
        Assert.Equal(GestureCatalog.TabWheel, GestureResolver.WheelAction(resolve, false, false, false, false, true, true));
        // Default overrides never replace a configured row
        var plain = Map(("wheel.windowed", GestureCatalog.ZoomWheel));
        Assert.Equal(GestureCatalog.ZoomWheel, GestureResolver.WheelAction(plain, false, false, false, false, true, true));
    }

    [Fact]
    public void Built_in_rows_keep_the_surface_behaviour_only_on_default()
    {
        Assert.Equal(GestureOutcome.BuiltIn, GestureResolver.ForBuiltInRow(GestureCatalog.Default).Outcome);
        Assert.Equal(GestureOutcome.BuiltIn, GestureResolver.ForBuiltInRow("default").Outcome);
        Assert.Equal(GestureOutcome.Run, GestureResolver.ForBuiltInRow(GestureCatalog.Nothing).Outcome);
        Assert.Equal(new GestureDecision(GestureOutcome.Run, GestureCatalog.Next), GestureResolver.ForBuiltInRow(GestureCatalog.Next));
    }

    [Fact]
    public void Side_buttons_pass_through_on_default_and_claim_any_explicit_choice()
    {
        Assert.Equal(GestureOutcome.PassThrough, GestureResolver.ForSideButton("windowed.backButton", GestureCatalog.Default).Outcome);
        Assert.Equal(GestureOutcome.Run, GestureResolver.ForSideButton("windowed.backButton", GestureCatalog.Nothing).Outcome);
        Assert.Equal(GestureOutcome.Run, GestureResolver.ForSideButton("windowed.backButton", GestureCatalog.Previous).Outcome);
    }

    [Theory]
    [InlineData(true, false, false, false, false, GestureAreas.Tabs)]
    [InlineData(true, true, true, true, true, GestureAreas.Tabs)]
    [InlineData(false, true, true, false, false, GestureAreas.Explorer)]
    [InlineData(false, false, true, true, true, GestureAreas.Fullscreen)]
    [InlineData(false, false, true, false, false, GestureAreas.Windowed)]
    [InlineData(false, false, false, true, true, GestureAreas.Windowed)]
    [InlineData(false, false, false, false, false, null)]
    public void Side_button_area_is_tab_then_explorer_then_image_then_home(bool tab, bool explorer, bool image, bool home, bool fs, string? expected)
        => Assert.Equal(expected, GestureResolver.SideButtonArea(tab, explorer, image, home, fs));

    [Fact]
    public void Double_left_slot_by_surface()
    {
        Assert.Equal("fullscreen.doubleLeftImage", GestureResolver.DoubleLeftSlot(true, false));
        Assert.Equal("windowed.doubleLeftImage", GestureResolver.DoubleLeftSlot(false, true));
        Assert.Equal("background.doubleLeft", GestureResolver.DoubleLeftSlot(false, false));
    }

    [Fact]
    public void Explorer_open_defaults_and_menu_always_opens()
    {
        Assert.Equal(GestureCatalog.OpenInCurrentTab, GestureResolver.ForExplorerOpen(GestureCatalog.Default, alternate: false, fromMenu: false));
        Assert.Equal(GestureCatalog.OpenInNewTab, GestureResolver.ForExplorerOpen(GestureCatalog.Default, alternate: true, fromMenu: false));
        Assert.Equal(GestureCatalog.OpenInNewTab, GestureResolver.ForExplorerOpen(GestureCatalog.OpenInNewTab, alternate: false, fromMenu: false));
        Assert.Equal(GestureCatalog.Next, GestureResolver.ForExplorerOpen(GestureCatalog.Next, alternate: false, fromMenu: false));
        Assert.Equal(GestureCatalog.OpenInCurrentTab, GestureResolver.ForExplorerOpen(GestureCatalog.Next, alternate: false, fromMenu: true));
    }
}
