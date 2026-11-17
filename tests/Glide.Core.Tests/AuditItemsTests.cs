using System.Text.Json;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Input;

namespace Glide.Core.Tests;

/// <summary>5.0.0 audit items O2 (zoom step), O3 (mouse side buttons), O7 (status bar hotkey default): pure logic.</summary>
public sealed class AuditItemsTests
{
    // ---- O2 -----------------------------------------------------------------------------------------------

    [Fact]
    public void Default_zoom_step_reproduces_the_historical_15_and_12_percent_exactly()
    {
        Assert.Equal(15, new GlideSettingsState().ViewportZoomStepPercent);
        Assert.Equal(1.15, ZoomStepPolicy.KeyFactor(ZoomStepPolicy.DefaultStepPercent));
        Assert.Equal(1.12, ZoomStepPolicy.WheelFactor(ZoomStepPolicy.DefaultStepPercent));
        Assert.Equal(15, Glide.Core.Settings.SettingsCatalog.All.First(s => s.Id == "view.zoomStep").DefaultValue);
    }

    [Theory]
    [InlineData(10, 1.10, 1.08)]
    [InlineData(25, 1.25, 1.20)]
    [InlineData(30, 1.30, 1.24)]
    [InlineData(100, 2.00, 1.80)]
    [InlineData(1, 1.01, 1.008)]
    public void A_changed_step_scales_keys_and_the_wheel_proportionally(int step, double key, double wheel)
    {
        Assert.Equal(key, ZoomStepPolicy.KeyFactor(step), 10);
        Assert.Equal(wheel, ZoomStepPolicy.WheelFactor(step), 10);
        // Zoom out is the exact reciprocal of zoom in.
        Assert.Equal(1.0, ZoomStepPolicy.KeyFactor(step) * (1 / ZoomStepPolicy.KeyFactor(step)), 12);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(101, 100)]
    [InlineData(5000, 100)]
    public void The_step_is_validated_to_1_to_100_percent(int stored, int effective)
    {
        Assert.Equal(effective, ZoomStepPolicy.Clamp(stored));
        Assert.Equal(1 + effective / 100.0, ZoomStepPolicy.KeyFactor(stored), 10);
    }

    [Fact]
    public void A_stored_old_default_step_moves_to_15_once_and_other_values_are_kept()
    {
        var old = new GlideSettingsState { SettingsSchemaVersion = 7, ViewportZoomStepPercent = 10 };
        SettingsStore.MigrateDefaultsSchema8(old, old.SettingsSchemaVersion);
        Assert.Equal(15, old.ViewportZoomStepPercent);
        var custom = new GlideSettingsState { SettingsSchemaVersion = 7, ViewportZoomStepPercent = 25 };
        SettingsStore.MigrateDefaultsSchema8(custom, custom.SettingsSchemaVersion);
        Assert.Equal(25, custom.ViewportZoomStepPercent);
        var current = new GlideSettingsState { SettingsSchemaVersion = 8, ViewportZoomStepPercent = 10 };   // chosen after the migration
        SettingsStore.MigrateDefaultsSchema8(current, current.SettingsSchemaVersion);
        Assert.Equal(10, current.ViewportZoomStepPercent);
    }

    // ---- O7 -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_status_toggle_default_is_Ctrl_Shift_B_in_the_standard_spelling_and_unique()
    {
        var row = HotkeyCatalog.Find("status.toggle")!;
        Assert.Equal(new[] { "Ctrl+Shift+B" }, row.DefaultShortcuts);
        Assert.Equal(row.DefaultShortcuts[0], ShortcutText.Canonicalize(row.DefaultShortcuts[0]));
        var owners = HotkeyCatalog.All.Where(r => r.DefaultShortcuts.Any(s => ShortcutText.AreEqual(s, "Ctrl+Shift+B"))).Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "status.toggle" }, owners);
        Assert.DoesNotContain(HotkeyCatalog.FixedShortcuts, f => ShortcutText.AreEqual(f.Shortcut, "Ctrl+Shift+B"));
        // Search hotkey and conflict detection use the same spelling.
        Assert.Contains("Show / hide status surface", HotkeyLookup.Describe(HotkeyCatalog.CreateDefaultMap(), "Ctrl+Shift+B"));
        var conflict = HotkeyConflicts.Check(HotkeyCatalog.CreateDefaultMap(), "Shift+Ctrl+B", "view.fit");
        Assert.Equal(HotkeyConflictKind.OtherAction, conflict.Kind);
    }

    [Theory]
    [InlineData("Alt+Shift+S")]   // file spelling of the previous release
    [InlineData("Shift+Alt+S")]   // canonical spelling written by the 5.0.0 development builds
    public void A_file_still_on_the_old_default_gets_the_new_default(string stored)
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        map["status.toggle"] = new List<string> { stored };
        Assert.True(HotkeyDefaultMigrations.MigrateStatusToggle(map, 7));
        Assert.Equal(new[] { "Ctrl+Shift+B" }, map["status.toggle"]);
    }

    [Fact]
    public void Rebound_unassigned_extended_or_newer_files_keep_their_binding()
    {
        foreach (var kept in new[] { new List<string> { "F9" }, new List<string>(), new List<string> { "Alt+Shift+S", "F9" }, new List<string> { "Ctrl+Shift+B" } })
        {
            var map = HotkeyCatalog.CreateDefaultMap();
            map["status.toggle"] = kept.ToList();
            Assert.False(HotkeyDefaultMigrations.MigrateStatusToggle(map, 7));
            Assert.Equal(kept, map["status.toggle"]);
        }
        var current = HotkeyCatalog.CreateDefaultMap();
        current["status.toggle"] = new List<string> { "Alt+Shift+S" };   // chosen on purpose after schema 8
        Assert.False(HotkeyDefaultMigrations.MigrateStatusToggle(current, 8));
        Assert.Equal(new[] { "Alt+Shift+S" }, current["status.toggle"]);
        // Another action already uses Ctrl+Shift+B: nothing is stolen or duplicated.
        var taken = HotkeyCatalog.CreateDefaultMap();
        taken["status.toggle"] = new List<string> { "Alt+Shift+S" };
        taken["view.fit"] = new List<string> { "Ctrl+Shift+B" };
        Assert.False(HotkeyDefaultMigrations.MigrateStatusToggle(taken, 7));
        Assert.Equal(new[] { "Alt+Shift+S" }, taken["status.toggle"]);
    }

    [Fact]
    public void Loading_a_schema_7_settings_file_migrates_both_the_hotkey_and_the_zoom_step_and_a_second_load_is_stable()
    {
        WithIsolatedStore(() =>
        {
            var legacy = new GlideSettingsState { SettingsSchemaVersion = 7, ViewportZoomStepPercent = 10 };
            legacy.Hotkeys["status.toggle"] = new List<string> { "Alt+Shift+S" };
            legacy.Hotkeys["view.fit"] = new List<string> { "Shift+W", "F7" };   // user override survives
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(legacy, Glide.App.AppJsonContext.Default.GlideSettingsState));
            var loaded = SettingsStore.Load();
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
            Assert.Equal(new[] { "Ctrl+Shift+B" }, loaded.Hotkeys["status.toggle"]);
            Assert.Equal(new[] { "Shift+W", "F7" }, loaded.Hotkeys["view.fit"]);
            Assert.Equal(15, loaded.ViewportZoomStepPercent);

            // After the user deliberately rebinds the old key (saved at schema 8) it is kept.
            loaded.Hotkeys["status.toggle"] = new List<string> { "Alt+Shift+S" };
            loaded.ViewportZoomStepPercent = 10;
            SettingsStore.Save(loaded);
            var again = SettingsStore.Load();
            Assert.Equal(new[] { "Alt+Shift+S" }, again.Hotkeys["status.toggle"]);
            Assert.Equal(10, again.ViewportZoomStepPercent);
        });
    }

    // ---- Explore/Browse containing folder: default Ctrl+E (settings schema 10) ----------------------------------

    [Fact]
    public void Explore_containing_folder_gets_ctrl_e_only_when_unassigned_free_and_older_than_schema_10()
    {
        var unassigned = HotkeyCatalog.CreateDefaultMap();
        unassigned["folder.exploreParent"] = new List<string>();
        Assert.True(HotkeyDefaultMigrations.MigrateExploreParent(unassigned, 9));
        Assert.Equal(new[] { "Ctrl+E" }, unassigned["folder.exploreParent"]);

        var missing = HotkeyCatalog.CreateDefaultMap();
        missing.Remove("folder.exploreParent");
        Assert.True(HotkeyDefaultMigrations.MigrateExploreParent(missing, 7));
        Assert.Equal(new[] { "Ctrl+E" }, missing["folder.exploreParent"]);

        // A binding the user set is never replaced or extended.
        var bound = HotkeyCatalog.CreateDefaultMap();
        bound["folder.exploreParent"] = new List<string> { "Ctrl+Alt+E" };
        Assert.False(HotkeyDefaultMigrations.MigrateExploreParent(bound, 9));
        Assert.Equal(new[] { "Ctrl+Alt+E" }, bound["folder.exploreParent"]);

        // A file already at schema 10 keeps "removed".
        var removed = HotkeyCatalog.CreateDefaultMap();
        removed["folder.exploreParent"] = new List<string>();
        Assert.False(HotkeyDefaultMigrations.MigrateExploreParent(removed, 10));
        Assert.Empty(removed["folder.exploreParent"]);

        // Another action already uses Ctrl+E: it is not taken away and nothing is duplicated.
        var taken = HotkeyCatalog.CreateDefaultMap();
        taken["folder.exploreParent"] = new List<string>();
        taken["view.fit"] = new List<string> { "Ctrl+E" };
        Assert.False(HotkeyDefaultMigrations.MigrateExploreParent(taken, 9));
        Assert.Empty(taken["folder.exploreParent"]);
        Assert.Equal(new[] { "Ctrl+E" }, taken["view.fit"]);
    }

    [Fact]
    public void Loading_a_schema_9_file_gives_ctrl_e_once_and_keeps_a_later_removal()
    {
        WithIsolatedStore(() =>
        {
            var legacy = new GlideSettingsState { SettingsSchemaVersion = 9 };
            legacy.Hotkeys["folder.exploreParent"] = new List<string>();
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(legacy, Glide.App.AppJsonContext.Default.GlideSettingsState));
            var loaded = SettingsStore.Load();
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
            Assert.Equal(new[] { "Ctrl+E" }, loaded.Hotkeys["folder.exploreParent"]);

            loaded.Hotkeys["folder.exploreParent"] = new List<string>();   // removed on purpose after the migration
            SettingsStore.Save(loaded);
            Assert.Empty(SettingsStore.Load().Hotkeys["folder.exploreParent"]);

            // A saved Ctrl+E on another action wins over the new default.
            var other = new GlideSettingsState { SettingsSchemaVersion = 9 };
            other.Hotkeys["folder.exploreParent"] = new List<string>();
            other.Hotkeys["view.fit"] = new List<string> { "Ctrl+E" };
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(other, Glide.App.AppJsonContext.Default.GlideSettingsState));
            var kept = SettingsStore.Load();
            Assert.Empty(kept.Hotkeys["folder.exploreParent"]);
            Assert.Equal(new[] { "Ctrl+E" }, kept.Hotkeys["view.fit"]);
        });
    }

    [Fact]
    public void A_settings_file_without_the_status_toggle_entry_gets_the_new_default()
    {
        WithIsolatedStore(() =>
        {
            File.WriteAllText(SettingsStore.GetSettingsPath(), "{\"SettingsSchemaVersion\":7,\"Hotkeys\":{\"view.fit\":[\"Shift+W\"]}}");
            Assert.Equal(new[] { "Ctrl+Shift+B" }, SettingsStore.Load().Hotkeys["status.toggle"]);
        });
    }

    // ---- O3 -----------------------------------------------------------------------------------------------

    private static readonly string[] SideButtonSlots =
    [
        "windowed.backButtonImage", "windowed.forwardButtonImage", "fullscreen.backButtonImage", "fullscreen.forwardButtonImage",
        "tabs.backButton", "tabs.forwardButton", "explorer.backButton", "explorer.forwardButton"
    ];

    [Fact]
    public void Side_buttons_are_gesture_inputs_with_slots_in_the_viewer_tabs_and_explorer_areas_defaulting_to_default()
    {
        Assert.Contains(GestureInputs.BackButton, GestureInputs.All);
        Assert.Contains(GestureInputs.ForwardButton, GestureInputs.All);
        foreach (var id in SideButtonSlots)
        {
            var slot = GestureCatalog.FindSlot(id)!;
            Assert.NotNull(slot);
            Assert.Equal(GestureCatalog.Default, slot.DefaultActionId);          // saved "default" bindings keep meaning Default
            Assert.Equal(GestureInputKind.Click, slot.Kind);
            Assert.True(GestureCatalog.DefaultBehavior.ContainsKey(id));
            // 5.0.0: Default is Back / Forward (the first 5.0 build left the side buttons unbound).
            var back = slot.Input == GestureInputs.BackButton;
            Assert.Equal(back ? "back" : "forward", GestureCatalog.DefaultBehavior[id]);
            Assert.Equal(back ? GestureCatalog.NavigateBack : GestureCatalog.NavigateForward, GestureCatalog.DefaultActionFor(id));
            Assert.Same(slot, GestureCatalog.SlotFor(slot.Area, slot.Input));
            Assert.Equal(GestureCatalog.Default, GestureCatalog.CreateDefaultMap()[id]);
        }
        Assert.Equal("Mouse back button", GestureInputs.BaseLabel(GestureInputs.BackButton));
        Assert.Equal("Mouse forward button", GestureInputs.BaseLabel(GestureInputs.ForwardButton));
        Assert.Null(GestureCatalog.SlotFor(GestureAreas.Overlay, GestureInputs.BackButton));
        Assert.Equal(GestureAreas.Tabs, GestureCatalog.SlotFor(GestureAreas.Tabs, GestureInputs.ForwardButton)!.Area);
    }

    [Fact]
    public void Side_button_rows_offer_click_actions_and_explorer_uses_its_navigation_actions()
    {
        var viewer = GestureCatalog.ActionsForSlot("windowed.backButtonImage").Select(a => a.Id).ToArray();
        Assert.Contains(GestureCatalog.Previous, viewer);
        Assert.Contains(GestureCatalog.Next, viewer);
        Assert.DoesNotContain(GestureCatalog.ZoomWheel, viewer);
        var explorer = GestureCatalog.ActionsForSlot("explorer.backButton").Select(a => a.Id).ToArray();
        Assert.Contains("explorer.back", explorer);
        Assert.Contains("explorer.forward", explorer);
        // 5.0.0 tab history: Back / Forward are offered (and suggested first) on every side-button row.
        foreach (var slot in SideButtonSlots)
        {
            var offered = GestureCatalog.ActionsForSlot(slot).Select(a => a.Id).ToArray();
            Assert.Contains(GestureCatalog.NavigateBack, offered);
            Assert.Contains(GestureCatalog.NavigateForward, offered);
            Assert.Equal(slot.Contains("back", StringComparison.OrdinalIgnoreCase) ? GestureCatalog.NavigateBack : GestureCatalog.NavigateForward,
                GestureCatalog.SuggestedForSlot(slot)[0].Id);
        }
        Assert.DoesNotContain(GestureCatalog.OpenInNewTab, explorer);        // needs an item under the pointer
        Assert.DoesNotContain(GestureCatalog.OpenInCurrentTab, explorer);
        Assert.Contains(GestureCatalog.OpenInNewTab, GestureCatalog.ActionsForSlot("explorer.middleClick").Select(a => a.Id));
        Assert.Contains(GestureCatalog.SuggestedForSlot("explorer.backButton"), a => a.Id == GestureCatalog.NavigateBack);
        Assert.Contains(GestureInputs.BackButton, GestureCatalog.CustomInputsForArea(GestureAreas.Windowed));
        Assert.DoesNotContain(GestureInputs.BackButton, GestureCatalog.CustomInputsForArea(GestureAreas.Overlay));
    }

    [Fact]
    public void Side_button_slots_are_searchable_by_every_common_name_and_listed_in_the_catalog()
    {
        foreach (var id in SideButtonSlots)
        {
            var entry = Glide.Core.Settings.SettingsCatalog.All.Single(s => s.Id == "gesture." + id);
            Assert.Equal("Mouse & keyboard", entry.Category);
            var terms = string.Join(' ', entry.SearchTerms).ToLowerInvariant();
            Assert.Contains("side button", terms);
            Assert.True(terms.Contains("xbutton1") || terms.Contains("xbutton2"));
            Assert.True(terms.Contains("mouse 4") || terms.Contains("mouse 5"));
        }
    }

    [Fact]
    public void Older_files_without_side_button_slots_load_and_new_bindings_round_trip_additively()
    {
        WithIsolatedStore(() =>
        {
            File.WriteAllText(SettingsStore.GetSettingsPath(), "{\"SettingsSchemaVersion\":7,\"Gestures\":{\"windowed.middleClickImage\":\"fit\"}}");
            var loaded = SettingsStore.Load();
            Assert.Equal("fit", loaded.Gestures["windowed.middleClickImage"]);
            foreach (var id in SideButtonSlots) Assert.Equal(GestureCatalog.Default, loaded.Gestures[id]);
            loaded.Gestures["windowed.backButtonImage"] = GestureCatalog.Previous;
            loaded.CustomGestures.Add(new CustomGesture { Area = GestureAreas.Tabs, Input = GestureInputs.ForwardButton, Modifier = GestureModifiers.Ctrl, Action = GestureCatalog.NewTab });
            SettingsStore.Save(loaded);
            var again = SettingsStore.Load();
            Assert.Equal(GestureCatalog.Previous, again.Gestures["windowed.backButtonImage"]);
            var custom = Assert.Single(again.CustomGestures);
            Assert.Equal(GestureInputs.ForwardButton, custom.Input);
        });
    }

    [Fact]
    public void The_input_router_resolves_a_side_button_binding_and_a_custom_gesture_with_a_held_key()
    {
        var router = new InputRouter();
        var bindings = GestureCatalog.CreateDefaultMap();
        Assert.Equal(GestureCatalog.Default, router.ResolveGesture("windowed.backButtonImage", bindings));
        bindings["windowed.backButtonImage"] = GestureCatalog.Previous;
        Assert.Equal(GestureCatalog.Previous, router.ResolveGesture("windowed.backButtonImage", bindings));
        Assert.Equal(GlideCommand.PreviousImage, router.ResolveGestureCommand("windowed.backButtonImage", bindings));
        var custom = new[] { new CustomGesture { Area = GestureAreas.Windowed, Input = GestureInputs.BackButton, Modifier = GestureModifiers.Shift, Action = GestureCatalog.FirstImage } };
        Assert.Equal(GestureCatalog.FirstImage, GestureCatalog.ResolveAction("windowed.backButtonImage", bindings, custom, GestureModifiers.Shift));
        Assert.Equal(GestureCatalog.Previous, GestureCatalog.ResolveAction("windowed.backButtonImage", bindings, custom, GestureModifiers.None));
    }

    [Fact]
    public void Hotkey_bindings_still_never_carry_mouse_buttons()
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState();
            state.Hotkeys["view.fit"] = new List<string> { "MouseX1", "Shift+W" };
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(state, Glide.App.AppJsonContext.Default.GlideSettingsState));
            Assert.Equal(new[] { "Shift+W" }, SettingsStore.Load().Hotkeys["view.fit"]);
        });
    }

    /// <summary>
    /// 5.0.6: "After closing a tab, activate" defaults to Previously used tab. Files saved before schema 12 store the old
    /// default "Match Ctrl+Tab order" explicitly; that value moves to the new default once, other choices are kept, and
    /// Match Ctrl+Tab order picked from schema 12 on stays.
    /// </summary>
    [Theory]
    [InlineData(11, "Match Ctrl+Tab order", "Previously used tab")]
    [InlineData(8, "Match Ctrl+Tab order", "Previously used tab")]
    [InlineData(11, "Tab to the left", "Tab to the left")]
    [InlineData(11, "Tab to the right", "Tab to the right")]
    [InlineData(12, "Match Ctrl+Tab order", "Match Ctrl+Tab order")]
    public void After_closing_a_tab_old_default_moves_to_previously_used_tab_once(int schema, string stored, string expected)
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState { SettingsSchemaVersion = schema, AfterTabCloseActivate = stored };
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(state, Glide.App.AppJsonContext.Default.GlideSettingsState));
            var loaded = SettingsStore.Load();
            Assert.Equal(expected, loaded.AfterTabCloseActivate);
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
        });
        Assert.Equal("Previously used tab", new GlideSettingsState().AfterTabCloseActivate);
    }

    /// <summary>
    /// 5.0.6: "Open new tabs in the foreground" defaults to on. Files saved before schema 13 store the old default (off)
    /// explicitly; it moves to on once. Off chosen from schema 13 on stays off (background opening is still available).
    /// </summary>
    [Theory]
    [InlineData(12, false, true)]
    [InlineData(8, false, true)]
    [InlineData(12, true, true)]
    [InlineData(13, false, false)]
    [InlineData(13, true, true)]
    public void Open_new_tabs_in_the_foreground_old_default_moves_to_on_once(int schema, bool stored, bool expected)
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState { SettingsSchemaVersion = schema, SwitchToNewTabs = stored };
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(state, Glide.App.AppJsonContext.Default.GlideSettingsState));
            var loaded = SettingsStore.Load();
            Assert.Equal(expected, loaded.SwitchToNewTabs);
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
            // Saved again at the current schema, an explicit off is kept from now on.
            loaded.SwitchToNewTabs = false;
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(loaded, Glide.App.AppJsonContext.Default.GlideSettingsState));
            Assert.False(SettingsStore.Load().SwitchToNewTabs);
        });
        Assert.True(new GlideSettingsState().SwitchToNewTabs);
        Assert.True((bool)Glide.Core.Settings.SettingsCatalog.All.Single(s => s.Id == "tabs.switchToNewTabs").DefaultValue);
        Assert.True(SettingsDefaults.RestoreAllSettings(new GlideSettingsState { SwitchToNewTabs = false }).SwitchToNewTabs);
    }

    /// <summary>
    /// 5.0.6: "Navigation minimum interval" defaults to 120 ms (laptop keyboards often bounce). Files saved before schema 14
    /// store the old default 80 explicitly; it moves to 120 once. Every other value is kept, and 80 chosen from schema 14 on
    /// stays 80.
    /// </summary>
    [Theory]
    [InlineData(13, 80, 120)]
    [InlineData(8, 80, 120)]
    [InlineData(13, 0, 0)]
    [InlineData(13, 50, 50)]
    [InlineData(13, 100, 100)]
    [InlineData(13, 250, 250)]
    [InlineData(14, 80, 80)]
    [InlineData(14, 120, 120)]
    public void Navigation_minimum_interval_old_default_moves_to_120_ms_once(int schema, int stored, int expected)
    {
        WithIsolatedStore(() =>
        {
            var state = new GlideSettingsState { SettingsSchemaVersion = schema, NavigationRateLimitMs = stored };
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(state, Glide.App.AppJsonContext.Default.GlideSettingsState));
            var loaded = SettingsStore.Load();
            Assert.Equal(expected, loaded.NavigationRateLimitMs);
            Assert.Equal(SettingsStore.CurrentSettingsSchema, loaded.SettingsSchemaVersion);
            // Saved again at the current schema, an explicit 80 is the user's choice from now on.
            loaded.NavigationRateLimitMs = 80;
            File.WriteAllText(SettingsStore.GetSettingsPath(), JsonSerializer.Serialize(loaded, Glide.App.AppJsonContext.Default.GlideSettingsState));
            Assert.Equal(80, SettingsStore.Load().NavigationRateLimitMs);
        });
        Assert.Equal(120, new GlideSettingsState().NavigationRateLimitMs);
        Assert.Equal(120, Glide.Core.Settings.SettingsCatalog.All.Single(s => s.Id == "navigation.rateLimitMs").DefaultValue);
        Assert.Equal(120, SettingsDefaults.RestoreAllSettings(new GlideSettingsState { NavigationRateLimitMs = 80 }).NavigationRateLimitMs);
    }

    /// <summary>
    /// 5.0.6: "Tell me when a key press seems to skip pictures" (navigation.skipHint) is a real Toggle setting: on by default,
    /// catalogued and searchable, hotkey-bindable, restored by Restore defaults and listed as a pending Settings change.
    /// </summary>
    [Fact]
    public void Navigation_skip_hint_is_a_real_toggle_setting()
    {
        Assert.True(new GlideSettingsState().NavigationSkipHint);
        var entry = Glide.Core.Settings.SettingsCatalog.All.Single(s => s.Id == "navigation.skipHint");
        Assert.Equal("Tell me when a key press seems to skip pictures", entry.Label);
        Assert.Equal("Window & navigation", entry.Category);
        Assert.Equal(Glide.Core.Settings.SettingKind.Toggle, entry.Kind);
        Assert.Equal(true, entry.DefaultValue);
        foreach (var term in new[] { "skip", "hint", "toast", "remind", "bounce", "laptop", "notification" })
            Assert.Contains(term, entry.SearchTerms);
        Assert.Contains(Glide.Core.Settings.SettingEffectRegistry.All, x => x.SettingId == "navigation.skipHint");
        var accessor = SettingAccessors.Find("navigation.skipHint");
        Assert.NotNull(accessor);
        var state = new GlideSettingsState();
        accessor!.SetBool!(state, false);
        Assert.False(state.NavigationSkipHint);
        Assert.Contains(AppActionRegistry.All, a => a.SettingId == "navigation.skipHint" && a.Kind == AppActionKind.SettingToggle);
        Assert.True(SettingsDefaults.RestoreAllSettings(new GlideSettingsState { NavigationSkipHint = false }).NavigationSkipHint);
        Assert.Single(SettingsChangeSet.Compute(new GlideSettingsState(), new GlideSettingsState { NavigationSkipHint = false }));
    }

    private static void WithIsolatedStore(Action action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-audit-items-" + Guid.NewGuid().ToString("N"));
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
