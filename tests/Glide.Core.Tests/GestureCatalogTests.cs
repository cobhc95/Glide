using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

public sealed class GestureCatalogTests
{
    [Fact]
    public void Catalog_has_54_unique_slots_and_the_56_historical_actions_first()
    {
        // 5.0.x: actions are read from AppActionRegistry; the 56 historical ones come first, then every
        // registry command / setting toggle that may be bound to a pointer input.
        Assert.Equal(54, GestureCatalog.Slots.Count);
        Assert.True(GestureCatalog.Actions.Count > 56);
        Assert.Equal(GestureCatalog.LegacyActionIds, GestureCatalog.Actions.Take(56).Select(x => x.Id));
        Assert.Equal(54, GestureCatalog.Slots.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(GestureCatalog.Actions.Count, GestureCatalog.Actions.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_slot_has_area_input_default_text_and_a_unique_input_per_area()
    {
        var areas = GestureCatalog.Areas.Select(a => a.Id).ToHashSet();
        foreach (var slot in GestureCatalog.Slots)
        {
            Assert.Contains(slot.Area, areas);
            Assert.Contains(slot.Input, GestureInputs.All);
            Assert.Equal(GestureCatalog.Default, slot.DefaultActionId);
            Assert.True(GestureCatalog.DefaultBehavior.TryGetValue(slot.Id, out var text) && !string.IsNullOrWhiteSpace(text), slot.Id);
            Assert.False(string.IsNullOrWhiteSpace(slot.InputLabel));
        }
        // One row per (area, input, held key).
        Assert.Equal(GestureCatalog.Slots.Count, GestureCatalog.Slots.Select(s => (s.Area, s.Input, s.Modifier)).Distinct().Count());
        Assert.Equal(GestureCatalog.Slots.Count, GestureCatalog.DefaultBehavior.Count);
        Assert.Equal(new[] { "Windowed image", "Fullscreen image", "Empty background", "Selection", "Slideshow", "Window in Window", "Tabs", "Title bar", "Explorer" },
            GestureCatalog.Areas.Select(a => a.Label));
    }

    [Fact]
    public void Every_slot_is_a_searchable_settings_catalogue_entry_in_its_area()
    {
        foreach (var slot in GestureCatalog.Slots)
        {
            var setting = Assert.Single(SettingsCatalog.All, x => x.Id == "gesture." + slot.Id);
            Assert.Equal("Mouse & keyboard", setting.Category);
            Assert.StartsWith(GestureCatalog.AreaLabel(slot.Area) + ": " + slot.InputLabel, setting.Label);
            Assert.Contains(GestureCatalog.AreaLabel(slot.Area).ToLowerInvariant(), setting.SearchTerms);
            Assert.Contains("gesture", setting.SearchTerms);
            Assert.Contains("mouse", setting.SearchTerms);
            if (slot.Kind == GestureInputKind.Wheel) { Assert.Contains("scroll", setting.SearchTerms); Assert.Contains("wheel", setting.SearchTerms); }
            if (slot.Modifier != GestureModifiers.None) Assert.Contains(GestureModifiers.Label(slot.Modifier).ToLowerInvariant(), setting.SearchTerms);
            Assert.Contains("Default (" + GestureCatalog.DefaultBehavior[slot.Id] + ")", setting.Description);
        }
        Assert.Contains(SettingsCatalog.All, x => x.Id == "gesture.custom");
        // "scroll window" finds the windowed-image wheel row by its terms/label.
        var windowedWheel = SettingsCatalog.All.Single(x => x.Id == "gesture.wheel.windowed");
        var haystack = string.Join(' ', windowedWheel.SearchTerms.Append(windowedWheel.Label));
        Assert.Contains("scroll", haystack, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window", haystack, StringComparison.OrdinalIgnoreCase);
        // "middle click tab" finds the tab middle-click row.
        var tabMiddle = SettingsCatalog.All.Single(x => x.Id == "gesture.tabs.middleClick");
        Assert.Contains("middle click", tabMiddle.SearchTerms);
        Assert.Contains("tab", tabMiddle.SearchTerms);
    }

    // ---------- Smart per-input actions ----------

    private static readonly string[] DragActions = [GestureCatalog.Default, GestureCatalog.Nothing, GestureCatalog.Pan, GestureCatalog.CreateSelection, GestureCatalog.MoveWindow];

    [Fact]
    public void Wheel_rows_offer_only_direction_aware_wheel_actions()
    {
        foreach (var slot in GestureCatalog.Slots.Where(s => s.Kind == GestureInputKind.Wheel))
        {
            var offered = GestureCatalog.ActionsForSlot(slot.Id);
            Assert.Contains(offered, a => a.Id == GestureCatalog.Default);
            Assert.All(offered.Where(a => a.Id is not (GestureCatalog.Default or GestureCatalog.Nothing)), a =>
            {
                Assert.Equal(GestureInputKinds.Wheel, a.Inputs);
                Assert.True(a.DirectionAware, $"{slot.Id} offers non-directional {a.Id}");
            });
            foreach (var forbidden in new[] { GestureCatalog.CloseTab, GestureCatalog.Settings, GestureCatalog.Fullscreen, GestureCatalog.NewTab, GestureCatalog.ContextMenu, GestureCatalog.Pan })
                Assert.DoesNotContain(offered, a => a.Id == forbidden);
        }
        var windowed = GestureCatalog.ActionsForSlot("wheel.windowed").Select(a => a.Id).ToArray();
        foreach (var expected in new[] { GestureCatalog.ZoomWheel, GestureCatalog.ZoomWheelReversed, GestureCatalog.BrowseWheel, GestureCatalog.BrowseWheelReversed,
                     GestureCatalog.PanWheel, GestureCatalog.PanWheelHorizontal, GestureCatalog.JumpWheel, GestureCatalog.FolderWheel, GestureCatalog.RotateWheel, GestureCatalog.TabWheel })
            Assert.Contains(expected, windowed);
        Assert.DoesNotContain(GestureCatalog.ZoomIn, windowed);
        Assert.DoesNotContain(GestureCatalog.Next, windowed);
        Assert.Equal(new[] { GestureCatalog.Default, GestureCatalog.Nothing, GestureCatalog.TabWheel }, GestureCatalog.ActionsForSlot("tabs.wheel").Select(a => a.Id));
    }

    [Fact]
    public void Drag_rows_offer_only_continuous_actions()
    {
        foreach (var slot in GestureCatalog.Slots.Where(s => s.Kind == GestureInputKind.Drag))
            Assert.All(GestureCatalog.ActionsForSlot(slot.Id), a => Assert.Contains(a.Id, DragActions));
        Assert.DoesNotContain(GestureCatalog.ActionsForSlot("drag.leftImage"), a => a.Id == GestureCatalog.Next);
        Assert.Equal(new[] { GestureCatalog.Default, GestureCatalog.Nothing }, GestureCatalog.ActionsForSlot("overlay.rightDrag").Select(a => a.Id));
    }

    [Fact]
    public void Click_rows_offer_one_shot_commands_only()
    {
        foreach (var slot in GestureCatalog.Slots.Where(s => s.Kind is GestureInputKind.Click or GestureInputKind.DoubleClick))
            Assert.All(GestureCatalog.ActionsForSlot(slot.Id), a =>
                Assert.True(a.Id == GestureCatalog.Default || (a.Inputs & GestureInputKinds.OneShot) != 0, $"{slot.Id} offers {a.Id}"));
        var click = GestureCatalog.ActionsForSlot("windowed.leftClickImage").Select(a => a.Id).ToArray();
        Assert.Contains(GestureCatalog.ZoomIn, click);
        Assert.Contains(GestureCatalog.RotateLeft, click);
        Assert.Contains(GestureCatalog.CopyImage, click);
        Assert.DoesNotContain(GestureCatalog.ZoomWheel, click);
        Assert.DoesNotContain(GestureCatalog.Pan, click);
        Assert.DoesNotContain(GestureCatalog.OpenInNewTab, click);
    }

    [Fact]
    public void Area_specific_sets_are_enforced()
    {
        string[] Ids(string slot) => GestureCatalog.ActionsForSlot(slot).Select(a => a.Id).ToArray();
        // Tabs: tab actions, no image view commands.
        Assert.Contains(GestureCatalog.CloseTab, Ids("tabs.middleClick"));
        Assert.Contains(GestureCatalog.DuplicateTab, Ids("tabs.middleClick"));
        Assert.DoesNotContain(GestureCatalog.ZoomIn, Ids("tabs.middleClick"));
        Assert.DoesNotContain(GestureCatalog.RotateLeft, Ids("tabs.doubleClick"));
        // Title bar: window actions.
        Assert.Contains(GestureCatalog.MaximizeRestore, Ids("titleBar.doubleClick"));
        Assert.Contains(GestureCatalog.MinimizeWindow, Ids("titleBar.middleClick"));
        Assert.Contains(GestureCatalog.ToggleAlwaysOnTop, Ids("titleBar.rightClick"));
        Assert.DoesNotContain(GestureCatalog.Fit, Ids("titleBar.doubleClick"));
        Assert.DoesNotContain(GestureCatalog.MaximizeRestore, Ids("windowed.leftClickImage"));
        // Explorer: open actions only there.
        Assert.Contains(GestureCatalog.OpenInNewTab, Ids("explorer.doubleClick"));
        Assert.Contains(GestureCatalog.OpenInCurrentTab, Ids("explorer.middleClick"));
        Assert.DoesNotContain(GestureCatalog.Next, Ids("explorer.doubleClick"));
        // Overlay: overlay actions only.
        Assert.Equal(new[] { GestureCatalog.Default, GestureCatalog.Nothing, GestureCatalog.ResetOverlayZoom, GestureCatalog.BringOverlayFront }, Ids("overlay.middleClick"));
        Assert.Equal(new[] { GestureCatalog.Default, GestureCatalog.Nothing, GestureCatalog.ZoomWheel, GestureCatalog.ZoomWheelReversed }, Ids("overlay.wheel"));
        // Slideshow: slideshow actions offered.
        Assert.Contains(GestureCatalog.ToggleSlideshow, Ids("slideshow.leftClick"));
        Assert.Contains(GestureCatalog.StopSlideshow, Ids("slideshow.rightClick"));
        // "Ignore here" only where the default claims the press.
        var passThrough = GestureCatalog.Slots.Where(s => GestureCatalog.IsOffered(s.Id, GestureCatalog.PassThrough)).Select(s => s.Id).OrderBy(x => x);
        Assert.Equal(new[] { "fullscreen.leftClickImage", "fullscreen.rightClickImage", "selection.leftClick", "selection.rightClick", "slideshow.leftClick", "slideshow.rightClick" }.OrderBy(x => x), passThrough);
    }

    [Fact]
    public void No_slot_offers_an_invalid_or_destructive_action()
    {
        Assert.DoesNotContain(GestureCatalog.Actions, a => a.Destructive);
        var forbiddenCommands = new[] { GlideCommand.DeleteFile, GlideCommand.CloseWindow, GlideCommand.RenameFile, GlideCommand.SaveImage, GlideCommand.Print };
        Assert.DoesNotContain(GestureCatalog.Actions, a => forbiddenCommands.Contains(a.Command));
        foreach (var slot in GestureCatalog.Slots)
            foreach (var action in GestureCatalog.ActionsForSlot(slot.Id))
                Assert.True(GestureCatalog.IsActionValidFor(action, slot.Area, slot.Kind), $"{slot.Id} offers invalid {action.Id}");
    }

    [Fact]
    public void Suggested_chips_are_valid_short_and_never_default()
    {
        foreach (var slot in GestureCatalog.Slots)
        {
            var suggested = GestureCatalog.SuggestedForSlot(slot.Id);
            Assert.True(suggested.Count <= 4);
            Assert.DoesNotContain(suggested, a => a.Id is GestureCatalog.Default or GestureCatalog.Nothing);
            Assert.All(suggested, a => Assert.True(GestureCatalog.IsOffered(slot.Id, a.Id), $"{slot.Id} suggests {a.Id}"));
        }
        Assert.Contains(GestureCatalog.SuggestedForSlot("wheel.windowed"), a => a.Id == GestureCatalog.ZoomWheel);
        Assert.Contains(GestureCatalog.SuggestedForSlot("wheel.windowed"), a => a.Id == GestureCatalog.PanWheelHorizontal);
    }

    [Fact]
    public void Legacy_wheel_values_still_normalize_and_unknown_values_fall_back_to_default()
    {
        Assert.Equal(GestureCatalog.ZoomWheel, GestureCatalog.NormalizeActionForSlot("wheel.windowed", GestureCatalog.ZoomIn));
        Assert.Equal(GestureCatalog.ZoomWheel, GestureCatalog.NormalizeActionForSlot("wheel.fullscreenCtrl", GestureCatalog.ZoomOut));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureCatalog.NormalizeActionForSlot("wheel.rightHeld", GestureCatalog.Next));
        Assert.Equal(GestureCatalog.BrowseWheel, GestureCatalog.NormalizeActionForSlot("overlay.wheel", GestureCatalog.Previous));
        Assert.Equal(GestureCatalog.ZoomWheelReversed, GestureCatalog.NormalizeActionForSlot("wheel.windowed", GestureCatalog.ZoomWheelReversed));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.NormalizeActionForSlot("windowed.leftClickImage", GestureCatalog.ZoomWheel));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.NormalizeActionForSlot("windowed.leftClickImage", GestureCatalog.PanWheel));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("wheel.windowed", new Dictionary<string, string> { { "wheel.windowed", "bogus" } }));
        Assert.Equal(GlideCommand.ZoomIn, GestureCatalog.CommandForAction(GestureCatalog.ZoomIn));
        Assert.Equal(GlideCommand.JumpForward10, GestureCatalog.CommandForAction(GestureCatalog.JumpForward10));
        Assert.Equal(GlideCommand.ZoomTo200, GestureCatalog.CommandForAction(GestureCatalog.Zoom200));
    }

    [Fact]
    public void Reversed_wheel_items_invert_the_direction()
    {
        Assert.Equal(OverlayGestureAction.ZoomOut, OverlayGesturePolicy.Wheel(GestureCatalog.ZoomWheelReversed, true, 1));
        Assert.Equal(OverlayGestureAction.ZoomIn, OverlayGesturePolicy.Wheel(GestureCatalog.ZoomWheelReversed, true, -1));
        Assert.Equal(OverlayGestureAction.ZoomIn, OverlayGesturePolicy.Wheel(GestureCatalog.ZoomWheel, true, 1));
        Assert.Equal(OverlayGestureAction.None, OverlayGesturePolicy.Wheel(GestureCatalog.Nothing, true, 1));
    }

    // ---------- Custom gestures ----------

    private static CustomGesture C(string area, string input, string modifier, string action) =>
        new() { Area = area, Input = input, Modifier = modifier, Action = action };

    [Fact]
    public void Custom_gesture_overrides_the_slot_for_the_same_input_and_held_key_only()
    {
        var customs = new List<CustomGesture> { C(GestureAreas.Windowed, GestureInputs.MiddleClick, GestureModifiers.Alt, GestureCatalog.Fullscreen) };
        var bindings = GestureCatalog.CreateDefaultMap();
        Assert.Equal(GestureCatalog.Fullscreen, GestureCatalog.ResolveAction("windowed.middleClickImage", bindings, customs, GestureModifiers.Alt));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("windowed.middleClickImage", bindings, customs, GestureModifiers.None));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("windowed.middleClickImage", bindings, customs, GestureModifiers.Ctrl));
        // Another area is not affected.
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("fullscreen.middleClickImage", bindings, customs, GestureModifiers.Alt));
        // Without a modifier match the built-in binding still applies.
        bindings["windowed.middleClickImage"] = GestureCatalog.Fit;
        Assert.Equal(GestureCatalog.Fit, GestureCatalog.ResolveAction("windowed.middleClickImage", bindings, customs, GestureModifiers.None));
        // A modifier-less custom gesture overrides the built-in row.
        customs.Add(C(GestureAreas.Windowed, GestureInputs.MiddleClick, GestureModifiers.None, GestureCatalog.Actual));
        Assert.Equal(GestureCatalog.Actual, GestureCatalog.ResolveAction("windowed.middleClickImage", bindings, customs, GestureModifiers.None));
    }

    [Fact]
    public void Custom_wheel_gestures_match_the_modifier_wheel_slot()
    {
        var customs = new List<CustomGesture> { C(GestureAreas.Windowed, GestureInputs.Wheel, GestureModifiers.Shift, GestureCatalog.PanWheelHorizontal) };
        Assert.Equal(GestureCatalog.PanWheelHorizontal, GestureCatalog.ResolveAction("wheel.windowedShift", null, customs, GestureModifiers.Shift));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("wheel.windowed", null, customs, GestureModifiers.None));
        Assert.Equal(GestureModifiers.Ctrl, GestureModifiers.From(ctrl: true, shift: true, alt: true));
        Assert.Equal(GestureModifiers.Shift, GestureModifiers.From(ctrl: false, shift: true, alt: true));
        Assert.Equal(GestureModifiers.Alt, GestureModifiers.From(ctrl: false, shift: false, alt: true));
    }

    [Fact]
    public void First_matching_custom_gesture_wins()
    {
        var customs = new List<CustomGesture>
        {
            C(GestureAreas.Tabs, GestureInputs.MiddleClick, GestureModifiers.Ctrl, GestureCatalog.DuplicateTab),
            C(GestureAreas.Tabs, GestureInputs.MiddleClick, GestureModifiers.Ctrl, GestureCatalog.DetachTab)
        };
        Assert.Equal(GestureCatalog.DuplicateTab, GestureCatalog.ResolveCustom(GestureAreas.Tabs, GestureInputs.MiddleClick, GestureModifiers.Ctrl, customs));
    }

    [Fact]
    public void Custom_gestures_are_capped_at_six_per_area()
    {
        var customs = Enumerable.Range(0, 6).Select(_ => C(GestureAreas.Windowed, GestureInputs.LeftClick, GestureModifiers.Ctrl, GestureCatalog.Nothing)).ToList();
        customs.Add(C(GestureAreas.Windowed, GestureInputs.MiddleClick, GestureModifiers.Ctrl, GestureCatalog.Fit)); // 7th: ignored
        customs.Add(C(GestureAreas.Fullscreen, GestureInputs.MiddleClick, GestureModifiers.Ctrl, GestureCatalog.Fit)); // another area: counts separately
        Assert.Null(GestureCatalog.ResolveCustom(GestureAreas.Windowed, GestureInputs.MiddleClick, GestureModifiers.Ctrl, customs));
        Assert.Equal(GestureCatalog.Fit, GestureCatalog.ResolveCustom(GestureAreas.Fullscreen, GestureInputs.MiddleClick, GestureModifiers.Ctrl, customs));
        var normalized = GestureCatalog.NormalizeCustomGestures(customs);
        Assert.Equal(6, normalized.Count(c => c.Area == GestureAreas.Windowed));
        Assert.Single(normalized, c => c.Area == GestureAreas.Fullscreen);
    }

    [Fact]
    public void Custom_gesture_normalization_drops_invalid_entries_and_inputs_follow_the_area()
    {
        var normalized = GestureCatalog.NormalizeCustomGestures(new[]
        {
            C("nowhere", GestureInputs.LeftClick, GestureModifiers.None, GestureCatalog.Fit),
            C(GestureAreas.Tabs, GestureInputs.LeftDrag, GestureModifiers.None, GestureCatalog.Fit),
            C(GestureAreas.Tabs, GestureInputs.MiddleClick, "weird", GestureCatalog.Default)
        });
        var only = Assert.Single(normalized);
        Assert.Equal(GestureModifiers.None, only.Modifier);
        Assert.Equal(GestureCatalog.Nothing, only.Action);
        Assert.DoesNotContain(GestureInputs.LeftDrag, GestureCatalog.CustomInputsForArea(GestureAreas.Tabs));
        Assert.Contains(GestureInputs.LeftDrag, GestureCatalog.CustomInputsForArea(GestureAreas.Windowed));
        Assert.DoesNotContain(GestureInputs.RightWheel, GestureCatalog.CustomInputsForArea(GestureAreas.Windowed));
        Assert.DoesNotContain(GestureCatalog.ActionsForCustom(GestureAreas.Windowed, GestureInputs.Wheel), a => a.Id == GestureCatalog.Default);
        Assert.All(GestureCatalog.ActionsForCustom(GestureAreas.Windowed, GestureInputs.Wheel).Where(a => a.Id != GestureCatalog.Nothing), a => Assert.Equal(GestureInputKinds.Wheel, a.Inputs));
    }
}
