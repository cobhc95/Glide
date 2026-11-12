using Glide.Core;
using Glide.Core.Commands;
using Xunit;

namespace Glide.Core.Tests;

public sealed class GestureCatalogTests
{
    [Fact]
    public void Mature_contract_has_exactly_25_slots_and_23_actions()
    {
        Assert.Equal(25, GestureCatalog.Slots.Count);
        Assert.Equal(23, GestureCatalog.Actions.Count);
        Assert.Equal(25, GestureCatalog.Slots.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(23, GestureCatalog.Actions.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Wheel_slots_offer_merged_direction_pairs_with_reversed_order_only()
    {
        var wheel = GestureCatalog.ActionsForSlot("wheel.windowed").Select(a => a.Id).ToArray();
        Assert.Contains(GestureCatalog.ZoomWheel, wheel);
        Assert.Contains(GestureCatalog.ZoomWheelReversed, wheel);
        Assert.Contains(GestureCatalog.BrowseWheel, wheel);
        Assert.Contains(GestureCatalog.BrowseWheelReversed, wheel);
        Assert.DoesNotContain(GestureCatalog.ZoomIn, wheel);
        Assert.DoesNotContain(GestureCatalog.ZoomOut, wheel);
        Assert.DoesNotContain(GestureCatalog.Next, wheel);
        Assert.DoesNotContain(GestureCatalog.Previous, wheel);
        Assert.Equal("Zoom in/out", GestureCatalog.Actions.Single(a => a.Id == GestureCatalog.ZoomWheel).Label);
        Assert.Equal("Zoom in/out (reversed)", GestureCatalog.Actions.Single(a => a.Id == GestureCatalog.ZoomWheelReversed).Label);
        Assert.Equal("Previous/next image (reversed)", GestureCatalog.Actions.Single(a => a.Id == GestureCatalog.BrowseWheelReversed).Label);

        var click = GestureCatalog.ActionsForSlot("windowed.leftClickImage").Select(a => a.Id).ToArray();
        Assert.Contains(GestureCatalog.ZoomIn, click);
        Assert.Contains(GestureCatalog.ZoomOut, click);
        Assert.DoesNotContain(GestureCatalog.ZoomWheel, click);
        Assert.DoesNotContain(GestureCatalog.BrowseWheelReversed, click);
    }

    [Fact]
    public void Legacy_single_direction_wheel_bindings_migrate_to_merged_items()
    {
        Assert.Equal(GestureCatalog.ZoomWheel, GestureCatalog.NormalizeActionForSlot("wheel.windowed", GestureCatalog.ZoomIn));
        Assert.Equal(GestureCatalog.ZoomWheel, GestureCatalog.NormalizeActionForSlot("wheel.fullscreenCtrl", GestureCatalog.ZoomOut));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureCatalog.NormalizeActionForSlot("wheel.rightHeld", GestureCatalog.Next));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureCatalog.NormalizeActionForSlot("overlay.wheel", GestureCatalog.Previous));
        Assert.Equal(GestureCatalog.ZoomWheelReversed, GestureCatalog.NormalizeActionForSlot("wheel.windowed", GestureCatalog.ZoomWheelReversed));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.NormalizeActionForSlot("windowed.leftClickImage", GestureCatalog.ZoomWheel));
        var map = GestureCatalog.CreateDefaultMap();
        map["wheel.windowed"] = GestureCatalog.ZoomOut;
        Assert.Equal(GestureCatalog.ZoomWheel, GestureCatalog.ResolveAction("wheel.windowed", map));
    }

    [Fact]
    public void Every_gesture_slot_is_searchable_in_the_settings_catalogue()
    {
        foreach (var slot in GestureCatalog.Slots)
        {
            var setting = Assert.Single(Glide.Core.Settings.SettingsCatalog.All, x => x.Id == "gesture." + slot.Id);
            Assert.Equal("Mouse & Fullscreen", setting.Category);
            Assert.Contains(slot.Label, setting.Label);
        }
    }

    [Fact]
    public void Reversed_wheel_items_invert_the_direction()
    {
        Assert.Equal(OverlayGestureAction.ZoomOut, OverlayGesturePolicy.Wheel(GestureCatalog.ZoomWheelReversed, true, 1));
        Assert.Equal(OverlayGestureAction.ZoomIn, OverlayGesturePolicy.Wheel(GestureCatalog.ZoomWheelReversed, true, -1));
        Assert.Equal(OverlayGestureAction.ZoomIn, OverlayGesturePolicy.Wheel(GestureCatalog.ZoomWheel, true, 1));
    }

    [Fact]
    public void Every_default_and_custom_action_resolves_to_a_known_action()
    {
        var known = GestureCatalog.Actions.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(GestureCatalog.Slots, slot => Assert.Contains(slot.DefaultActionId, known));

        var map = GestureCatalog.CreateDefaultMap();
        map["wheel.windowed"] = GestureCatalog.ZoomIn;
        // Legacy single-direction wheel bindings resolve to the merged direction-aware item.
        Assert.Equal(GestureCatalog.ZoomWheel, GestureCatalog.ResolveAction("wheel.windowed", map));
        Assert.Equal(GlideCommand.ZoomIn, GestureCatalog.CommandForAction(GestureCatalog.ZoomIn));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("wheel.windowed", new Dictionary<string,string>{{"wheel.windowed","bogus"}}));
    }
}
