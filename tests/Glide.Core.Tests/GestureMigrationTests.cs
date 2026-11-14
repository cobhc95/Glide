using System.Text.Json;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>
/// 4.8.0 folds the separate mouse toggles into gesture rows. These tests pin the one-time migration:
/// each folded toggle becomes the equivalent explicit binding, stock toggles change nothing, explicit
/// bindings are never overwritten, and the result survives save / load.
/// </summary>
public sealed class GestureMigrationTests
{
    private static Dictionary<string, string> Migrate(LegacyMouseToggles toggles, Dictionary<string, string>? start = null)
    {
        var map = start ?? GestureCatalog.CreateDefaultMap();
        GestureLegacyMigration.Apply(map, toggles);
        return map;
    }

    private static void AssertOnlyChanged(Dictionary<string, string> map, params (string Slot, string Action)[] expected)
    {
        foreach (var slot in GestureCatalog.Slots)
        {
            var want = expected.FirstOrDefault(e => e.Slot == slot.Id).Action ?? GestureCatalog.Default;
            Assert.True(want == GestureCatalog.ResolveAction(slot.Id, map), $"{slot.Id}: expected {want}, got {GestureCatalog.ResolveAction(slot.Id, map)}");
        }
    }

    [Fact] public void Stock_toggles_produce_no_bindings() => AssertOnlyChanged(Migrate(new LegacyMouseToggles()));

    [Fact] public void Double_click_fullscreen_off_becomes_nothing_on_image_and_background() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { DoubleClickFullscreen = false }),
            ("windowed.doubleLeftImage", GestureCatalog.Nothing), ("background.doubleLeft", GestureCatalog.Nothing));

    [Fact] public void Double_click_exit_fullscreen_on_becomes_the_fullscreen_double_click_row() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { DoubleClickExitFullscreen = true }), ("fullscreen.doubleLeftImage", GestureCatalog.Fullscreen));

    [Fact] public void Fullscreen_click_navigation_off_becomes_ignore_here() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { FullscreenClickNavigation = false }),
            ("fullscreen.leftClickImage", GestureCatalog.PassThrough), ("fullscreen.rightClickImage", GestureCatalog.PassThrough));

    [Fact] public void Windowed_wheel_zoom_on_zooms_every_plain_and_ctrl_wheel_in_both_modes() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { WindowedWheelZoom = true, CtrlWheelZoom = false }),
            ("wheel.windowed", GestureCatalog.ZoomWheel), ("wheel.fullscreen", GestureCatalog.ZoomWheel),
            ("wheel.windowedCtrl", GestureCatalog.ZoomWheel), ("wheel.fullscreenCtrl", GestureCatalog.ZoomWheel),
            ("overlay.ctrlWheel", GestureCatalog.Nothing));

    [Fact] public void Ctrl_wheel_zoom_off_browses_and_stops_overlay_ctrl_zoom() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { CtrlWheelZoom = false }),
            ("wheel.windowedCtrl", GestureCatalog.BrowseWheel), ("wheel.fullscreenCtrl", GestureCatalog.BrowseWheel), ("overlay.ctrlWheel", GestureCatalog.Nothing));

    [Theory]
    [InlineData("Pan image", GestureCatalog.Pan)]
    [InlineData("Move window", GestureCatalog.MoveWindow)]
    public void Left_drag_choice_becomes_the_left_drag_row(string choice, string action) =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { LeftDragMode = choice }), ("drag.leftImage", action));

    [Fact] public void Selection_toggles_off_become_ignore_here() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { SelectionClickZoom = false, SelectionRightClickZoomOut = false }),
            ("selection.leftClick", GestureCatalog.PassThrough), ("selection.rightClick", GestureCatalog.PassThrough));

    [Fact] public void Middle_drag_pan_on_becomes_pan() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { MiddleDragPan = true }), ("drag.middleImage", GestureCatalog.Pan));

    [Fact] public void Overlay_toggles_off_become_nothing() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { OverlayWheelZoom = false, OverlayRightDragPan = false }),
            ("overlay.wheel", GestureCatalog.Nothing), ("overlay.rightDrag", GestureCatalog.Nothing));

    [Theory]
    [InlineData("Next image", GestureCatalog.Next)]
    [InlineData("Previous image", GestureCatalog.Previous)]
    [InlineData("Stop slideshow", GestureCatalog.StopSlideshow)]
    [InlineData("Do nothing", GestureCatalog.PassThrough)]
    public void Slideshow_left_click_choice_becomes_the_slideshow_row(string choice, string action) =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { SlideshowLeftClickAction = choice }), ("slideshow.leftClick", action));

    [Fact] public void Slideshow_right_click_stops_off_becomes_ignore_here() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { SlideshowRightClickStops = false }), ("slideshow.rightClick", GestureCatalog.PassThrough));

    [Fact] public void Tab_close_toggles_off_become_nothing() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { DoubleClickTabCloses = false, MiddleClickTabCloses = false }),
            ("tabs.doubleClick", GestureCatalog.Nothing), ("tabs.middleClick", GestureCatalog.Nothing));

    [Fact] public void Explorer_new_tab_choice_swaps_the_explorer_rows() =>
        AssertOnlyChanged(Migrate(new LegacyMouseToggles { ExplorerOpenImagesWith = LegacyMouseToggles.ExplorerNewTab }),
            ("explorer.doubleClick", GestureCatalog.OpenInNewTab), ("explorer.middleClick", GestureCatalog.OpenInCurrentTab));

    [Fact]
    public void Explicit_bindings_win_over_folded_toggles_as_they_did_before()
    {
        var start = GestureCatalog.CreateDefaultMap();
        start["wheel.windowed"] = GestureCatalog.BrowseWheelReversed;
        start["drag.leftImage"] = GestureCatalog.CreateSelection;
        var map = Migrate(new LegacyMouseToggles { WindowedWheelZoom = true, LeftDragMode = "Pan image" }, start);
        Assert.Equal(GestureCatalog.BrowseWheelReversed, map["wheel.windowed"]);
        Assert.Equal(GestureCatalog.CreateSelection, map["drag.leftImage"]);
        Assert.Equal(GestureCatalog.ZoomWheel, map["wheel.fullscreen"]);
    }

    [Fact]
    public void Folding_resets_the_toggles_and_is_idempotent()
    {
        var state = new GlideSettingsState { WindowedWheelZoom = true, DoubleClickTabCloses = false, SlideshowLeftClickAction = "Do nothing" };
        state.FoldLegacyMouseToggles();
        var first = new Dictionary<string, string>(state.Gestures, StringComparer.OrdinalIgnoreCase);
        Assert.False(state.WindowedWheelZoom);
        Assert.True(state.DoubleClickTabCloses);
        Assert.Equal("Pause / resume slideshow", state.SlideshowLeftClickAction);
        state.FoldLegacyMouseToggles();
        Assert.Equal(first.OrderBy(x => x.Key), state.Gestures.OrderBy(x => x.Key));
    }

    [Fact]
    public void A_schema_6_settings_file_migrates_on_load_and_round_trips()
    {
        WithIsolatedStore(() =>
        {
            var legacy = new GlideSettingsState
            {
                SettingsSchemaVersion = 6,
                DoubleClickFullscreen = false,
                FullscreenClickNavigation = false,
                CtrlWheelZoom = false,
                LeftDragMode = "Move window",
                MiddleDragPan = true,
                SlideshowRightClickStops = false,
                MiddleClickTabCloses = false,
                ExplorerOpenImagesWith = LegacyMouseToggles.ExplorerNewTab,
                InvertWheelDirection = true
            };
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(legacy, Glide.App.AppJsonContext.Default.GlideSettingsState));

            var loaded = SettingsStore.Load();
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
            Assert.Equal(GestureCatalog.Nothing, loaded.Gestures["windowed.doubleLeftImage"]);
            Assert.Equal(GestureCatalog.PassThrough, loaded.Gestures["fullscreen.leftClickImage"]);
            Assert.Equal(GestureCatalog.BrowseWheel, loaded.Gestures["wheel.windowedCtrl"]);
            Assert.Equal(GestureCatalog.MoveWindow, loaded.Gestures["drag.leftImage"]);
            Assert.Equal(GestureCatalog.Pan, loaded.Gestures["drag.middleImage"]);
            Assert.Equal(GestureCatalog.PassThrough, loaded.Gestures["slideshow.rightClick"]);
            Assert.Equal(GestureCatalog.Nothing, loaded.Gestures["tabs.middleClick"]);
            Assert.Equal(GestureCatalog.OpenInNewTab, loaded.Gestures["explorer.doubleClick"]);
            Assert.True(loaded.InvertWheelDirection); // surviving modifier option
            Assert.True(loaded.DoubleClickFullscreen); // folded toggles are inert afterwards

            // Saving and loading again keeps exactly the same bindings (no second migration).
            loaded.CustomGestures.Add(new CustomGesture { Area = GestureAreas.Tabs, Input = GestureInputs.MiddleClick, Modifier = GestureModifiers.Ctrl, Action = GestureCatalog.DuplicateTab });
            loaded.GestureRecentActions.Add(GestureCatalog.DuplicateTab);
            SettingsStore.Save(loaded);
            var again = SettingsStore.Load();
            Assert.Equal(loaded.Gestures.OrderBy(x => x.Key), again.Gestures.OrderBy(x => x.Key));
            var custom = Assert.Single(again.CustomGestures);
            Assert.Equal((GestureAreas.Tabs, GestureInputs.MiddleClick, GestureModifiers.Ctrl, GestureCatalog.DuplicateTab), (custom.Area, custom.Input, custom.Modifier, custom.Action));
            Assert.Equal(new[] { GestureCatalog.DuplicateTab }, again.GestureRecentActions);
            Assert.True(GlideSettingsState.CustomGesturesEqual(loaded.CustomGestures, again.CustomGestures));
        });
    }

    [Fact]
    public void A_current_schema_file_is_not_migrated_again()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState { WindowedWheelZoom = true };
            SettingsStore.Save(state); // writes schema 7 with a stray toggle value
            var loaded = SettingsStore.Load();
            Assert.Equal(GestureCatalog.Default, loaded.Gestures["wheel.windowed"]);
        });
    }

    [Fact]
    public void Imported_old_profiles_are_migrated()
    {
        var old = new GlideSettingsState { SettingsSchemaVersion = 5, OverlayWheelZoom = false };
        SettingsStore.MigrateLegacyMouseToggles(old, old.SettingsSchemaVersion);
        Assert.Equal(GestureCatalog.Nothing, old.Gestures["overlay.wheel"]);
        var current = new GlideSettingsState { SettingsSchemaVersion = 7, OverlayWheelZoom = false };
        SettingsStore.MigrateLegacyMouseToggles(current, current.SettingsSchemaVersion);
        Assert.Equal(GestureCatalog.Default, current.Gestures["overlay.wheel"]);
    }

    [Fact]
    public void Presets_express_their_mouse_choices_as_gesture_rows()
    {
        var photos = SettingsPresetCatalog.Apply("Windows Photos", new GlideSettingsState());
        Assert.Equal(GestureCatalog.ZoomWheel, photos.Gestures["wheel.windowed"]);
        Assert.Equal(GestureCatalog.Pan, photos.Gestures["drag.leftImage"]);
        Assert.Equal(GestureCatalog.PassThrough, photos.Gestures["selection.leftClick"]);
        Assert.Equal("Do nothing", photos.RightDragBehavior);
        var glide = SettingsPresetCatalog.Apply("Glide default", photos);
        Assert.All(GestureCatalog.Slots, s => Assert.Equal(GestureCatalog.Default, glide.Gestures[s.Id]));
    }

    private static void WithIsolatedStore(Action action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-gesture-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = dir;
        try { action(); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
