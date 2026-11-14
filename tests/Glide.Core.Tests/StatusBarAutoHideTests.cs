using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Settings;
using Avalonia.Markup.Xaml.Styling;

namespace Glide.Core.Tests;

/// <summary>
/// Status bar Auto-hide (Windows-taskbar style) and the collapsed expand button: pure state rules, the real
/// MainWindow reacting to pointer positions, the right-click menu entry, the catalogued setting and a render
/// of the rest / hover looks of the expand button.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class StatusBarAutoHideTests
{
    public StatusBarAutoHideTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- pure rules -----------------------------------------------------------------------------------------

    // zone / hysteresis helper: (enabled, revealed, inZone, overKeep, menuOpen, buttonsDown, now)
    private static bool Step(StatusRevealTracker t, bool revealed, bool inZone, bool overKeep = false, bool menu = false, bool down = false, long now = 0, bool immediate = false) =>
        t.Update(true, revealed, inZone, overKeep, menu, down, now, immediate);

    // The reveal area is derived from the bar's own box (window space). A one-row bar in a wide window is a thin strip.
    private static readonly StatusRevealZone WideBar = new(560, 1002, 1360, 1066);       // 1920 x 1080 window, 14 DIP above the edge
    // The same bar wrapped into several rows in a narrow window is a much taller box.
    private static readonly StatusRevealZone NarrowBar = new(14, 640, 486, 786);         // 500 x 800 window

    [Fact]
    public void Default_reveal_area_is_exactly_the_bars_box_not_a_fixed_height()
    {
        Assert.Equal(0, StatusBarAutoHide.DefaultRevealZoneExpand);
        var wide = StatusBarAutoHide.RevealZone(WideBar, 1920, 1080, StatusBarAutoHide.DefaultRevealZoneExpand);
        Assert.Equal(WideBar, wide);
        Assert.Equal(64, wide.Height, 3);                    // a thin strip, not a band
        var narrow = StatusBarAutoHide.RevealZone(NarrowBar, 500, 800, 0);
        Assert.Equal(NarrowBar, narrow);
        Assert.Equal(146, narrow.Height, 3);                 // the wrapped bar is taller, and so is its area
        Assert.True(narrow.Height > wide.Height);
        Assert.True(wide.Contains(960, 1030));
        Assert.False(wide.Contains(960, 1001), "one DIP above the bar");
        Assert.False(wide.Contains(960, 1067), "the bottom margin below the bar is not part of it");
        Assert.False(wide.Contains(559, 1030));
        Assert.False(wide.Contains(1361, 1030));
    }

    [Theory]
    [InlineData("Left", 14, 614)]
    [InlineData("Center", 660, 1260)]
    [InlineData("Right", 1306, 1906)]
    public void Reveal_area_follows_the_bars_horizontal_placement(string alignment, double left, double right)
    {
        var bar = new StatusRevealZone(left, 1002, right, 1066);
        var zone = StatusBarAutoHide.RevealZone(bar, 1920, 1080, 0);
        Assert.Equal(left, zone.Left, 3);
        Assert.Equal(right, zone.Right, 3);
        Assert.True(zone.Contains((left + right) / 2, 1030), alignment);
        Assert.False(zone.Contains(left - 1, 1030), alignment);
        Assert.False(zone.Contains(right + 1, 1030), alignment);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(150)]
    public void A_positive_adjustment_grows_the_area_outward_on_every_side_clamped_to_the_window(int expand)
    {
        var zone = StatusBarAutoHide.RevealZone(WideBar, 1920, 1080, expand);
        Assert.Equal(WideBar.Left - expand, zone.Left, 3);
        Assert.Equal(WideBar.Right + expand, zone.Right, 3);
        Assert.Equal(WideBar.Top - expand, zone.Top, 3);
        Assert.Equal(Math.Min(1080, WideBar.Bottom + expand), zone.Bottom, 3);     // the window edge clamps the bottom
        Assert.True(zone.Bottom <= 1080 && zone.Left >= 0 && zone.Right <= 1920);
        Assert.Equal(expand >= 14, zone.Contains(960, 1079));                       // the 14 DIP bottom margin is reached from 14 up
    }

    [Fact]
    public void Growth_never_leaves_the_window()
    {
        var zone = StatusBarAutoHide.RevealZone(NarrowBar, 500, 800, 300);
        Assert.Equal(new StatusRevealZone(0, 340, 500, 800), zone);
        // A bar that fills the window width cannot grow sideways at all.
        var full = StatusBarAutoHide.RevealZone(new StatusRevealZone(0, 700, 500, 786), 500, 800, 50);
        Assert.Equal(0, full.Left);
        Assert.Equal(500, full.Right);
        Assert.Equal(800, full.Bottom);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-20)]
    [InlineData(-30)]
    public void A_negative_adjustment_shrinks_the_area_inward_on_every_side(int expand)
    {
        var zone = StatusBarAutoHide.RevealZone(NarrowBar, 500, 800, expand);
        Assert.Equal(NarrowBar.Left - expand, zone.Left, 3);
        Assert.Equal(NarrowBar.Right + expand, zone.Right, 3);
        Assert.Equal(NarrowBar.Top - expand, zone.Top, 3);
        Assert.Equal(NarrowBar.Bottom + expand, zone.Bottom, 3);
        Assert.False(zone.Contains(NarrowBar.Left + 1, 700) && expand < -1, "the shrunk area no longer reaches the old edge");
    }

    [Fact]
    public void Shrinking_never_goes_below_6_DIP_tall_and_40_DIP_wide_and_stays_centred_on_the_bar()
    {
        // A one-row bar (64 DIP tall) shrunk by the maximum 40 on each side would be negative: it stays 6 DIP tall.
        var thin = StatusBarAutoHide.RevealZone(WideBar, 1920, 1080, -40);
        Assert.Equal(StatusBarAutoHide.MinRevealZoneHeight, thin.Height, 3);
        Assert.Equal(1034, (thin.Top + thin.Bottom) / 2, 3);                      // centred on the bar's own centre line
        Assert.Equal(WideBar.Width - 80, thin.Width, 3);                          // wide enough: width shrinks normally
        // A tiny bar (collapsed corner button sized) keeps the minimum in both directions.
        var tiny = StatusBarAutoHide.RevealZone(new StatusRevealZone(1800, 1020, 1840, 1050), 1920, 1080, -40);
        Assert.Equal(StatusBarAutoHide.MinRevealZoneWidth, tiny.Width, 3);
        Assert.Equal(StatusBarAutoHide.MinRevealZoneHeight, tiny.Height, 3);
        Assert.Equal(1820, (tiny.Left + tiny.Right) / 2, 3);
        Assert.Equal(1035, (tiny.Top + tiny.Bottom) / 2, 3);
        Assert.Equal(6, StatusBarAutoHide.MinRevealZoneHeight);
        Assert.Equal(40, StatusBarAutoHide.MinRevealZoneWidth);
    }

    [Theory]
    [InlineData(-100, -40)]
    [InlineData(-40, -40)]
    [InlineData(0, 0)]
    [InlineData(35, 35)]
    [InlineData(300, 300)]
    [InlineData(5000, 300)]
    public void The_adjustment_is_clamped_to_minus_40_through_300(int configured, int expected)
    {
        Assert.Equal(expected, StatusBarAutoHide.NormalizeRevealZoneExpand(configured));
        Assert.Equal(StatusBarAutoHide.RevealZone(WideBar, 1920, 1080, expected), StatusBarAutoHide.RevealZone(WideBar, 1920, 1080, configured));
    }

    [Fact]
    public void Reveal_area_handles_degenerate_windows_and_has_a_sane_fallback_box()
    {
        var tiny = StatusBarAutoHide.RevealZone(new StatusRevealZone(0, 0, 0, 0), 0, 0, 100);
        Assert.True(tiny.Width >= 0 && tiny.Height >= 0 && tiny.Left >= 0 && tiny.Top >= 0);
        var fallback = StatusBarAutoHide.FallbackBarRect(1000, 700);
        Assert.Equal(700 - 14, fallback.Bottom, 3);
        Assert.Equal(1000 / 2.0, (fallback.Left + fallback.Right) / 2, 3);        // centred like the default bar
        Assert.Equal(48, fallback.Height, 3);
        var small = StatusBarAutoHide.FallbackBarRect(100, 20);
        Assert.True(small.Left >= 0 && small.Top >= 0 && small.Right <= 100 && small.Bottom <= 20);
        var empty = StatusBarAutoHide.FallbackBarRect(0, 0);
        Assert.Equal(0, empty.Width);
    }

    [Fact]
    public void Reveal_area_scales_with_a_fullscreen_sized_window()
    {
        var bar = new StatusRevealZone(1130, 2040, 2710, 2146);   // 3840 x 2160, boosted bar
        Assert.Equal(bar, StatusBarAutoHide.RevealZone(bar, 3840, 2160, 0));
        var grown = StatusBarAutoHide.RevealZone(bar, 3840, 2160, 300);
        Assert.Equal(2160, grown.Bottom, 3);
        Assert.Equal(830, grown.Left, 3);
    }

    [Fact]
    public void Hidden_bar_is_revealed_by_entering_the_zone_and_nothing_else()
    {
        // Feature off: never revealed.
        Assert.False(new StatusRevealTracker().Update(false, false, true, true, true, false, 0));
        var t = new StatusRevealTracker();
        Assert.False(Step(t, false, inZone: false));
        Assert.False(Step(t, false, false, overKeep: true));         // nothing to be over while hidden
        Assert.False(Step(t, false, false, menu: true));             // a menu never reveals a hidden bar
        Assert.True(Step(t, false, inZone: true));
    }

    [Fact]
    public void Dragging_never_reveals_the_bar_and_never_changes_a_revealed_one()
    {
        var t = new StatusRevealTracker();
        Assert.False(Step(t, false, inZone: true, down: true));
        Assert.True(Step(t, false, inZone: true, down: false), "released in the zone: now it reveals");
        // Revealed, pointer far away, button down: frozen, the hide timer does not run.
        Assert.True(Step(t, true, false, down: true, now: 0));
        Assert.True(Step(t, true, false, down: true, now: 10_000));
        Assert.False(t.HidePending);
    }

    [Fact]
    public void Revealed_bar_stays_over_the_bar_and_hides_after_a_short_delay_elsewhere()
    {
        var t = new StatusRevealTracker();
        Assert.True(Step(t, true, inZone: true, overKeep: true, now: 0));
        Assert.False(t.HidePending);

        // Pointer leaves the bar (still in the big zone): it waits, then hides - the zone does NOT keep it open.
        Assert.True(Step(t, true, inZone: true, overKeep: false, now: 100));
        Assert.True(t.HidePending);
        Assert.True(Step(t, true, true, false, now: 100 + StatusBarAutoHide.HideDelayMs - 1));
        Assert.False(Step(t, true, true, false, now: 100 + StatusBarAutoHide.HideDelayMs));
        Assert.False(t.HidePending);
    }

    [Fact]
    public void Returning_to_the_bar_within_the_delay_cancels_the_hide()
    {
        var t = new StatusRevealTracker();
        Assert.True(Step(t, true, true, false, now: 0));
        Assert.True(t.HidePending);
        Assert.True(Step(t, true, true, overKeep: true, now: StatusBarAutoHide.HideDelayMs - 10));
        Assert.False(t.HidePending);
        // The delay restarts from the next time it leaves.
        Assert.True(Step(t, true, true, false, now: StatusBarAutoHide.HideDelayMs + 100));
        Assert.True(Step(t, true, true, false, now: StatusBarAutoHide.HideDelayMs + 100 + StatusBarAutoHide.HideDelayMs - 1));
    }

    [Fact]
    public void After_a_lingering_hide_the_bar_is_not_revealed_again_until_the_pointer_has_left_the_zone()
    {
        var t = new StatusRevealTracker();
        Assert.True(Step(t, true, true, false, now: 0));
        Assert.False(Step(t, true, true, false, now: StatusBarAutoHide.HideDelayMs));          // hid while still in the zone
        Assert.False(Step(t, false, inZone: true, now: 5000), "no flicker: still in the zone, so it stays hidden");
        Assert.False(Step(t, false, inZone: false, now: 5100));                                 // pointer leaves the zone: re-armed
        Assert.True(Step(t, false, inZone: true, now: 5200));
    }

    [Fact]
    public void Leaving_the_window_hides_the_bar_at_once_but_an_open_menu_still_keeps_it()
    {
        var t = new StatusRevealTracker();
        Assert.True(Step(t, true, true, true, now: 0));
        Assert.False(Step(t, true, false, false, now: 1, immediate: true));
        Assert.True(Step(new StatusRevealTracker(), true, false, false, menu: true, now: 1, immediate: true));
    }

    [Fact]
    public void An_open_menu_keeps_the_bar_visible_wherever_the_pointer_is()
    {
        var t = new StatusRevealTracker();
        Assert.True(Step(t, true, false, false, menu: true, now: 0));
        Assert.True(Step(t, true, false, false, menu: true, now: 60_000));
        Assert.False(t.HidePending);
        // When the menu closes the normal delay starts from then.
        Assert.True(Step(t, true, false, false, menu: false, now: 60_100));
        Assert.False(Step(t, true, false, false, menu: false, now: 60_100 + StatusBarAutoHide.HideDelayMs));
    }

    [Fact]
    public void Bar_visibility_follows_the_reveal_state_only_with_auto_hide_on()
    {
        Assert.True(StatusBarAutoHide.BarVisible(false, false));
        Assert.True(StatusBarAutoHide.BarVisible(false, true));
        Assert.False(StatusBarAutoHide.BarVisible(true, false));
        Assert.True(StatusBarAutoHide.BarVisible(true, true));
    }

    [Theory]
    // collapsed, corner, autoHide, hovered -> look
    [InlineData(true, false, false, false, StatusButtonLook.Arrow)]   // corner OFF + collapsed: small arrow at rest
    [InlineData(true, false, false, true, StatusButtonLook.Full)]     // ...the full button/shade only while hovered
    [InlineData(true, true, false, false, StatusButtonLook.Full)]     // corner ON: unchanged
    [InlineData(true, true, false, true, StatusButtonLook.Full)]
    [InlineData(false, false, false, false, StatusButtonLook.Full)]   // expanded bar: normal collapse button
    [InlineData(false, false, false, true, StatusButtonLook.Full)]
    [InlineData(true, false, true, false, StatusButtonLook.Full)]     // auto-hide has no collapsed button at all
    public void Expand_button_look_table(bool collapsed, bool corner, bool autoHide, bool hovered, StatusButtonLook expected) =>
        Assert.Equal(expected, StatusBarAutoHide.ExpandButtonLook(collapsed, corner, autoHide, hovered));

    // ---- catalogue / settings -------------------------------------------------------------------------------

    [Fact]
    public void Auto_hide_is_a_searchable_catalogued_setting_that_defaults_off_in_the_status_bar_card()
    {
        var setting = Assert.Single(SettingsCatalog.All, s => s.Id == "status.autoHide");
        Assert.Equal("Tabs & status bar", setting.Category);
        Assert.Equal(SettingKind.Toggle, setting.Kind);
        Assert.False((bool)setting.DefaultValue);
        Assert.Contains("auto-hide", setting.SearchTerms);
        Assert.Contains("taskbar", setting.SearchTerms);
        Assert.False(new GlideSettingsState().StatusAutoHide);
        Assert.Contains(SettingEffectRegistry.All, e => e.SettingId == "status.autoHide");
    }

    [Fact]
    public void Reveal_area_adjustment_is_a_searchable_catalogued_setting_defaulting_to_0_in_the_status_bar_card()
    {
        var setting = Assert.Single(SettingsCatalog.All, s => s.Id == "status.revealZoneExpand");
        Assert.Equal("Tabs & status bar", setting.Category);
        Assert.Equal(SettingKind.Number, setting.Kind);
        Assert.Equal(0, setting.DefaultValue);
        Assert.Contains("adjustment", setting.Label);
        Assert.Contains("0", setting.Description);
        Assert.Contains("exactly", setting.Description);
        foreach (var term in new[] { "reveal", "hover", "trigger", "zone", "area", "adjust", "expand", "shrink", "bottom edge", "auto-hide", "collapsed" })
            Assert.Contains(term, setting.SearchTerms);
        Assert.Contains(SettingEffectRegistry.All, e => e.SettingId == "status.revealZoneExpand");
        Assert.Equal(0, new GlideSettingsState().StatusRevealZoneExpand);
        Assert.Equal(StatusBarAutoHide.DefaultRevealZoneExpand, new GlideSettingsState().StatusRevealZoneExpand);
        // The earlier fixed-height setting never shipped: it is gone from the catalogue and the registry.
        Assert.DoesNotContain(SettingsCatalog.All, s => s.Id == "status.revealZoneHeight");
        Assert.DoesNotContain(SettingEffectRegistry.All, e => e.SettingId == "status.revealZoneHeight");
    }

    [Fact]
    public void Hover_reveal_is_on_by_default_and_catalogued_next_to_the_reveal_setting()
    {
        Assert.True(new GlideSettingsState().StatusShowOnHoverWhenClosed);
        var setting = Assert.Single(SettingsCatalog.All, s => s.Id == "status.hoverWhenClosed");
        Assert.Equal("Tabs & status bar", setting.Category);
        Assert.Equal(SettingKind.Toggle, setting.Kind);
        Assert.True((bool)setting.DefaultValue);
        Assert.Contains("On by default", setting.Description);
        Assert.DoesNotContain("Disabled by default", setting.Description);
        Assert.Contains("reveal area adjustment", setting.Description);
        foreach (var term in new[] { "hover", "collapsed", "reveal", "bottom edge", "pointer" })
            Assert.Contains(term, setting.SearchTerms);
    }

    [Fact]
    public void Old_settings_files_load_with_hover_on_and_no_adjustment_and_explicit_values_are_respected()
    {
        WithIsolatedStore(() =>
        {
            var file = Path.Combine(SettingsStore.GetSettingsDirectory(), "glide.settings.json");
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusBarAlignment\":\"Right\",\"StatusAutoHide\":true}");
            var loaded = SettingsStore.Load();
            Assert.True(loaded.StatusShowOnHoverWhenClosed, "a file without the key gets the new default (on)");
            Assert.Equal(0, loaded.StatusRevealZoneExpand);
            Assert.Equal("Right", loaded.StatusBarAlignment);
            Assert.True(loaded.StatusAutoHide);

            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusShowOnHoverWhenClosed\":false}");
            Assert.False(SettingsStore.Load().StatusShowOnHoverWhenClosed, "an explicit saved off stays off");
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusShowOnHoverWhenClosed\":true}");
            Assert.True(SettingsStore.Load().StatusShowOnHoverWhenClosed);

            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusRevealZoneExpand\":9999}");
            Assert.Equal(300, SettingsStore.Load().StatusRevealZoneExpand);
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusRevealZoneExpand\":-90}");
            Assert.Equal(-40, SettingsStore.Load().StatusRevealZoneExpand);
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusRevealZoneExpand\":-15}");
            Assert.Equal(-15, SettingsStore.Load().StatusRevealZoneExpand);
        });
    }

    [Fact]
    public void A_file_holding_the_discarded_fixed_height_setting_still_loads_and_ignores_it()
    {
        WithIsolatedStore(() =>
        {
            var file = Path.Combine(SettingsStore.GetSettingsDirectory(), "glide.settings.json");
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusRevealZoneHeight\":220,\"StatusBarAlignment\":\"Left\",\"StatusShowOnHoverWhenClosed\":false}");
            var loaded = SettingsStore.Load();
            Assert.Equal("Left", loaded.StatusBarAlignment);                  // the rest of the file loaded normally
            Assert.False(loaded.StatusShowOnHoverWhenClosed);
            Assert.Equal(0, loaded.StatusRevealZoneExpand);                   // the old key does not leak into the new setting
            Assert.DoesNotContain(typeof(GlideSettingsState).GetProperties(), p => p.Name == "StatusRevealZoneHeight");
        });
    }

    [Fact]
    public void A_custom_reveal_area_adjustment_and_hover_flag_survive_save_and_load()
    {
        WithIsolatedStore(() =>
        {
            SettingsStore.Save(new GlideSettingsState { StatusRevealZoneExpand = -25, StatusShowOnHoverWhenClosed = false });
            var loaded = SettingsStore.Load();
            Assert.Equal(-25, loaded.StatusRevealZoneExpand);
            Assert.False(loaded.StatusShowOnHoverWhenClosed);
            SettingsStore.Save(new GlideSettingsState { StatusRevealZoneExpand = 120 });
            loaded = SettingsStore.Load();
            Assert.Equal(120, loaded.StatusRevealZoneExpand);
            Assert.True(loaded.StatusShowOnHoverWhenClosed);
        });
    }

    [Fact]
    public async Task The_status_bar_context_menu_has_Auto_hide_but_no_hover_reveal_item_anywhere()
    {
        await OnWindow(window =>
        {
            var menu = (ContextMenu)Invoke(window, "BuildStatusSurfaceContextMenu")!;
            var headers = new List<string>();
            void Walk(IEnumerable<object> items)
            {
                foreach (var item in items.OfType<MenuItem>())
                {
                    headers.Add(item.Header?.ToString() ?? "");
                    if (item.ItemsSource is not null) Walk(item.ItemsSource.Cast<object>());
                    else if (item.Items.Count > 0) Walk(item.Items.Cast<object>());
                }
            }
            Walk(menu.ItemsSource!.Cast<object>());
            Assert.Contains(headers, h => h.StartsWith("Auto-hide status bar", StringComparison.Ordinal));
            Assert.Contains(headers, h => h.StartsWith("Reserve status bar space", StringComparison.Ordinal));
            foreach (var header in headers)
            {
                Assert.False(header.Contains("hover", StringComparison.OrdinalIgnoreCase), $"menu item \"{header}\" must not offer the hover reveal");
                Assert.False(header.Contains("Reveal", StringComparison.OrdinalIgnoreCase), $"menu item \"{header}\" must not offer the hover reveal");
                Assert.False(header.Contains("bottom-edge", StringComparison.OrdinalIgnoreCase), $"menu item \"{header}\" must not offer the hover reveal");
            }
            // Same result whichever state the setting is in (a ticked variant would have carried the same words).
            Settings(window).StatusShowOnHoverWhenClosed = false;
            var off = (ContextMenu)Invoke(window, "BuildStatusSurfaceContextMenu")!;
            Assert.Equal(menu.ItemsSource!.Cast<object>().Count(), off.ItemsSource!.Cast<object>().Count());
            return Task.CompletedTask;
        });
    }

    private static void WithIsolatedStore(Action action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
        try { action(); }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---- the real window ------------------------------------------------------------------------------------

    [Fact]
    public async Task Right_click_menu_offers_Auto_hide_status_bar_and_toggling_it_hides_the_bar()
    {
        await OnWindow(window =>
        {
            Assert.True(Surface(window).IsVisible);
            var item = AutoHideItem(window);
            Assert.Equal("Auto-hide status bar", item.Header);       // not ticked while off

            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Settings(window).StatusAutoHide);
            Assert.False(Surface(window).IsVisible, "auto-hide: no bar until the pointer reaches where it appears");
            Assert.False(Restore(window).IsVisible, "auto-hide leaves no arrow and no button behind");
            Assert.Equal("Auto-hide status bar  ✓", AutoHideItem(window).Header);

            AutoHideItem(window).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(Settings(window).StatusAutoHide);
            Assert.True(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_new_settings_state_reveals_on_hover_by_default()
    {
        await OnWindow(window =>
        {
            Assert.True(Settings(window).StatusShowOnHoverWhenClosed);
            Assert.Equal(0, Settings(window).StatusRevealZoneExpand);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Auto_hidden_bar_appears_where_the_bar_would_be_and_hides_when_the_pointer_leaves()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            Assert.False(Surface(window).IsVisible);

            var height = window.Bounds.Height;
            Move(window, new Point(300, height / 2));                       // middle of the window: still hidden
            Assert.False(Surface(window).IsVisible);

            Move(window, InBar(window));                                    // exactly where the bar appears: it appears
            Assert.True(Surface(window).IsVisible);

            // Moving over the bar itself keeps it.
            var bar = SurfaceRect(window);
            Move(window, bar.Center);
            Advance(window, 5000);
            Assert.True(Surface(window).IsVisible, "the bar must not hide while the pointer is over it");

            Move(window, new Point(300, height / 2 - 100));                 // well away: hidden again after the short delay
            Assert.True(Surface(window).IsVisible, "not instantly: the hide waits a moment");
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.False(Surface(window).IsVisible);

            // Leaving the window hides a revealed bar at once.
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);
            Invoke(window, "MainWindowPointerExited", window, null);
            Assert.False(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Auto_hidden_bar_stays_while_its_menu_is_open_and_goes_when_the_menu_closes()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);

            // The menu is a popup: the pointer leaves the bar while it is open.
            SetField(window, "_statusMenuOpen", true);
            Move(window, new Point(300, 40));
            Assert.True(Surface(window).IsVisible, "no ghost menu: the bar must not vanish under its own open menu");

            Advance(window, 10_000);
            Assert.True(Surface(window).IsVisible, "still held by the menu however long it stays open");

            SetField(window, "_statusMenuOpen", false);
            Invoke(window, "UpdateStatusAutoHide", new Point(300, 40));    // what the menu's Closed handler does
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.False(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_default_reveal_area_is_exactly_the_box_the_bar_occupies_when_auto_hidden()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            Assert.False(Surface(window).IsVisible);
            var box = Box(window);                                          // measured while the bar is hidden
            Assert.True(box.Width > 100 && box.Height > 20, $"a real bar-sized box, got {box}");
            Assert.True(box.Bottom < window.Bounds.Height, "the bar sits above the window's bottom margin");

            // Reveal it and compare with the bar's real laid-out rectangle: they must be identical.
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);
            var live = SurfaceRect(window);
            Assert.Equal(live.Left, box.Left, 0.6);
            Assert.Equal(live.Top, box.Top, 0.6);
            Assert.Equal(live.Right, box.Right, 0.6);
            Assert.Equal(live.Bottom, box.Bottom, 0.6);

            // Hidden again: one DIP outside each edge does not reveal, one DIP inside does.
            Invoke(window, "MainWindowPointerExited", window, null);
            Assert.False(Surface(window).IsVisible);
            var y = (box.Top + box.Bottom) / 2;
            var x = (box.Left + box.Right) / 2;
            foreach (var outside in new[] { new Point(x, box.Top - 1), new Point(x, box.Bottom + 1), new Point(box.Left - 1, y), new Point(box.Right + 1, y) })
            {
                Move(window, outside);
                Assert.False(Surface(window).IsVisible, $"{outside} is outside the bar's box");
            }
            foreach (var inside in new[] { new Point(x, box.Top + 1), new Point(x, box.Bottom - 1), new Point(box.Left + 1, y), new Point(box.Right - 1, y) })
            {
                Invoke(window, "MainWindowPointerExited", window, null);
                Move(window, new Point(300, 100));
                Move(window, inside);
                Assert.True(Surface(window).IsVisible, $"{inside} is inside the bar's box");
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Zero_adjustment_does_not_reveal_at_the_very_bottom_edge_but_a_positive_one_does()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusAutoHide = true;
            Apply(window);
            var height = window.Bounds.Height;
            Move(window, new Point(Box(window).Left + 60, height - 1));
            Assert.False(Surface(window).IsVisible, "the margin below the bar is not part of its box");

            settings.StatusRevealZoneExpand = 30;                            // takes effect at once, no restart
            Move(window, new Point(Box(window).Left + 60, height - 1));
            Assert.True(Surface(window).IsVisible, "30 DIP of growth reaches the window's bottom edge");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Positive_and_negative_adjustments_grow_and_shrink_the_area_around_the_bars_box()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusAutoHide = true;
            Apply(window);
            var box = Box(window);
            var x = (box.Left + box.Right) / 2;

            settings.StatusRevealZoneExpand = 100;
            Move(window, new Point(x, box.Top - 99));
            Assert.True(Surface(window).IsVisible, "99 DIP above the bar is inside a +100 area");
            Invoke(window, "MainWindowPointerExited", window, null);
            Move(window, new Point(x, box.Top - 101));
            Assert.False(Surface(window).IsVisible);
            Move(window, new Point(box.Left - 99, box.Bottom - 5));
            Assert.True(Surface(window).IsVisible, "it grows sideways too");
            Invoke(window, "MainWindowPointerExited", window, null);

            settings.StatusRevealZoneExpand = -10;                           // shrinks inward on every side
            Move(window, new Point(x, box.Top + 5));
            Assert.False(Surface(window).IsVisible, "5 DIP inside the bar's top edge is outside a -10 area");
            Move(window, new Point(box.Left + 5, (box.Top + box.Bottom) / 2));
            Assert.False(Surface(window).IsVisible);
            Move(window, new Point(x, box.Top + 11));
            Assert.True(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_reveal_area_follows_a_narrow_window_where_the_bar_wraps_into_taller_rows()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusAutoHide = true;
            Apply(window);
            var wide = Box(window);

            Resize(window, 430, 700);
            var narrow = Box(window);
            Assert.True(narrow.Height > wide.Height + 20, $"wrapped bar is taller: wide {wide.Height}, narrow {narrow.Height}");
            Assert.True(narrow.Right <= 430 && narrow.Left >= 0);
            Assert.Equal(700 - 14, narrow.Bottom, 0.6);

            // The hidden bar's measured box equals what is laid out once it is revealed in the narrow window.
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);
            var live = SurfaceRect(window);
            Assert.Equal(live.Top, narrow.Top, 0.6);
            Assert.Equal(live.Left, narrow.Left, 0.6);
            Assert.Equal(live.Right, narrow.Right, 0.6);

            // A point in the upper rows (which the thin wide strip would not cover) reveals in the narrow window.
            Invoke(window, "MainWindowPointerExited", window, null);
            Move(window, new Point((narrow.Left + narrow.Right) / 2, narrow.Top + 3));
            Assert.True(Surface(window).IsVisible);

            // Back to a wide window: the area is the thin strip again (nothing cached).
            Invoke(window, "MainWindowPointerExited", window, null);
            Resize(window, 1000, 700);
            var wideAgain = Box(window);
            Assert.Equal(wide.Height, wideAgain.Height, 0.6);
            Assert.Equal(wide.Left, wideAgain.Left, 0.6);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Center")]
    [InlineData("Right")]
    public async Task The_reveal_area_follows_the_bars_left_center_or_right_placement(string alignment)
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusAutoHide = true;
            settings.StatusBarAlignment = alignment;
            Apply(window);
            var box = Box(window);
            var width = window.Bounds.Width;
            switch (alignment)
            {
                case "Left": Assert.Equal(14, box.Left, 0.6); Assert.True(box.Right < width - 100); break;
                case "Right": Assert.Equal(width - 14, box.Right, 0.6); Assert.True(box.Left > 100); break;
                default: Assert.Equal(width / 2, (box.Left + box.Right) / 2, 0.6); break;
            }
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);
            var live = SurfaceRect(window);
            Assert.Equal(live.Left, box.Left, 0.6);
            Assert.Equal(live.Right, box.Right, 0.6);
            // The opposite side of the window is not part of the area.
            Invoke(window, "MainWindowPointerExited", window, null);
            var opposite = alignment == "Left" ? new Point(width - 20, (box.Top + box.Bottom) / 2) : new Point(20, (box.Top + box.Bottom) / 2);
            if (alignment != "Center") { Move(window, opposite); Assert.False(Surface(window).IsVisible); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_zone_does_not_reveal_while_a_mouse_button_is_down()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            SetField(window, "_statusPointerButtonsDown", true);             // dragging the image / window
            Move(window, InBar(window));
            Assert.False(Surface(window).IsVisible, "dragging through the area must not pop the bar up");

            SetField(window, "_statusPointerButtonsDown", false);
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);

            // And a revealed bar does not vanish mid-drag.
            SetField(window, "_statusPointerButtonsDown", true);
            Move(window, new Point(500, 100));
            Advance(window, 10_000);
            Assert.True(Surface(window).IsVisible);
            SetField(window, "_statusPointerButtonsDown", false);
            Move(window, new Point(500, 100));
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.False(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_grown_area_hides_the_bar_after_the_delay_when_the_pointer_lingers_away_from_it_and_does_not_flicker_back()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusAutoHide = true;
            settings.StatusRevealZoneExpand = 120;
            Apply(window);
            var box = Box(window);
            var x = (box.Left + box.Right) / 2;
            var top = new Point(x, box.Top - 110);                           // top of the grown area, well above the bar
            Move(window, top);
            Assert.True(Surface(window).IsVisible);

            // Crossing to the bar quickly keeps it.
            Advance(window, 200);
            Move(window, SurfaceRect(window).Center);
            Advance(window, 2000);
            Assert.True(Surface(window).IsVisible);

            // Back up to the area's top and linger: it hides after the delay, and stays hidden although still in the area.
            Move(window, top);
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.False(Surface(window).IsVisible);
            Move(window, new Point(x + 20, box.Top - 100));
            Assert.False(Surface(window).IsVisible, "still in the area: no flicker");
            // Leaving the area and entering again reveals it.
            Move(window, new Point(x + 20, 100));
            Move(window, new Point(x + 20, box.Top - 100));
            Assert.True(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_collapsed_bar_hover_reveal_uses_the_bars_box_even_when_collapsed_to_the_corner_and_returns_to_the_button()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = true;
            Apply(window);
            var expandedRect = SurfaceRect(window);                           // the real expanded bar, for comparison
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            Assert.True(SurfaceRect(window).Width < expandedRect.Width / 2, "collapsed to just the corner button");

            var box = Box(window);                                            // the box the revealed bar would occupy
            Assert.Equal(expandedRect.Left, box.Left, 0.6);
            Assert.Equal(expandedRect.Top, box.Top, 0.6);
            Assert.Equal(expandedRect.Right, box.Right, 0.6);
            Assert.Equal(expandedRect.Bottom, box.Bottom, 0.6);

            var height = window.Bounds.Height;
            Move(window, new Point(box.Left + 30, box.Top - 5));
            Assert.False((bool)GetField(window, "_statusHoverRevealed")!);
            Move(window, new Point(box.Left + 30, box.Top + 5));
            Assert.True((bool)GetField(window, "_statusHoverRevealed")!, "a point inside the box the bar would occupy reveals the collapsed bar");
            Assert.True(SurfaceRect(window).Width > expandedRect.Width / 2);

            Move(window, new Point(300, 100));
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.False((bool)GetField(window, "_statusHoverRevealed")!);
            Assert.True(Surface(window).IsVisible, "never hides the surface outright");
            Assert.True(SurfaceRect(window).Width < expandedRect.Width / 2, "back to the single button");
            _ = height;
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_corner_collapsed_box_follows_resizing_and_wrapping()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = true;
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            var wide = Box(window);

            Resize(window, 430, 700);
            var narrow = Box(window);
            Assert.True(narrow.Height > wide.Height + 20, $"wrapped: wide {wide.Height}, narrow {narrow.Height}");

            // Compare with the real expanded layout in the same narrow window.
            SetField(window, "_statusCollapsed", false);
            Apply(window);
            var live = SurfaceRect(window);
            Assert.Equal(live.Top, narrow.Top, 0.6);
            Assert.Equal(live.Left, narrow.Left, 0.6);
            Assert.Equal(live.Right, narrow.Right, 0.6);
            Assert.Equal(live.Bottom, narrow.Bottom, 0.6);

            // Collapse again, grow the window again: the box shrinks back to the strip.
            SetField(window, "_statusCollapsed", true);
            Resize(window, 1000, 700);
            Assert.Equal(wide.Height, Box(window).Height, 0.6);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_ghosted_collapsed_bar_with_corner_off_uses_its_live_layout_as_the_box()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = false;
            Apply(window);
            var expanded = SurfaceRect(window);
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            var box = Box(window);
            Assert.Equal(expanded.Left, box.Left, 0.6);
            Assert.Equal(expanded.Right, box.Right, 0.6);
            Assert.Equal(expanded.Top, box.Top, 0.6);
            Assert.Equal(expanded.Bottom, box.Bottom, 0.6);

            Move(window, InBar(window));
            Assert.True((bool)GetField(window, "_statusHoverRevealed")!);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Measuring_the_hidden_bar_leaves_the_window_exactly_as_it_was()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            var before = window.GetVisualDescendants().Count();
            var collapseVisible = Collapse(window).IsVisible;
            for (var i = 0; i < 3; i++) { Invoke(window, "ApplyCollapsedStatusChrome"); _ = Box(window); }
            Assert.False(Surface(window).IsVisible);
            Assert.Equal(collapseVisible, Collapse(window).IsVisible);
            Assert.Equal(before, window.GetVisualDescendants().Count());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Fullscreen_auto_hide_uses_the_boosted_fullscreen_bar_box()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusAutoHide = true;
            Apply(window);
            window.WindowState = WindowState.FullScreen;
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(window.Bounds.Width, window.Bounds.Height));
            window.Arrange(new Rect(window.Bounds.Size));
            Invoke(window, "ApplyStatusBarSize");
            Apply(window);
            Assert.False(Surface(window).IsVisible, "auto-hide also covers fullscreen: hidden until reached");
            var box = Box(window);
            Assert.True(box.Width > 100 && box.Bottom <= window.Bounds.Height);
            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);
            var live = SurfaceRect(window);
            Assert.Equal(live.Left, box.Left, 0.6);
            Assert.Equal(live.Top, box.Top, 0.6);
            Assert.Equal(live.Right, box.Right, 0.6);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_Home_page_status_bar_gets_the_same_exact_reveal_area()
    {
        await OnWindow(window =>
        {
            var home = (HomeSurface)Invoke(window, "EnsureHomeSurface")!;
            ImageView(window).IsVisible = false;
            window.FindControl<ContentControl>("HomeHost")!.IsVisible = true;
            Settings(window).StatusAutoHide = true;
            Apply(window);
            Assert.False(home.StatusSurface.IsVisible);
            var box = Box(window);                                           // measured while the Home bar is hidden
            Assert.True(box.Width > 100 && box.Height > 20, $"a real bar-sized box, got {box}");

            Move(window, InBar(window));
            Assert.True(home.StatusSurface.IsVisible);
            var origin = home.StatusSurface.TranslatePoint(default, window)!.Value;
            var live = new Rect(origin, home.StatusSurface.Bounds.Size);
            Assert.Equal(live.Left, box.Left, 0.6);
            Assert.Equal(live.Top, box.Top, 0.6);
            Assert.Equal(live.Right, box.Right, 0.6);
            Assert.Equal(live.Bottom, box.Bottom, 0.6);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_zone_is_not_a_control_and_never_sits_over_the_image()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            var before = window.GetVisualDescendants().Count();
            Move(window, InBar(window));                                     // reveal adds nothing but the (already present) bar
            Move(window, new Point(500, 100));
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.Equal(before, window.GetVisualDescendants().Count());
            // The pointer is only measured, never captured or marked handled, so clicks and gestures reach the image.
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Auto_hide_ignores_the_collapsed_flag_and_never_shows_a_collapsed_button()
    {
        await OnWindow(window =>
        {
            SetField(window, "_statusCollapsed", true);
            Settings(window).StatusAutoHide = true;
            Apply(window);
            Assert.False(Surface(window).IsVisible);

            Move(window, InBar(window));
            Assert.True(Surface(window).IsVisible);
            Assert.False(Settings(window).StatusCollapsedToCorner && Collapse(window).Classes.Contains("collapsedArrow"));
            Assert.False(Collapse(window).Classes.Contains("collapsedArrow"));
            Assert.False(Collapse(window).IsVisible, "a revealed auto-hide bar has no collapse button (auto-hide replaces collapsing)");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Switching_auto_hide_off_shows_the_bar_normally_again()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Apply(window);
            Assert.False(Surface(window).IsVisible);
            Settings(window).StatusAutoHide = false;
            Apply(window);
            Assert.True(Surface(window).IsVisible);
            Assert.True(Collapse(window).IsVisible, "the collapse button comes back with the normal bar");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Collapsed_bar_hover_reveal_never_hides_the_bar_outright()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = true;
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            Assert.False((bool)GetField(window, "_statusHoverRevealed")!);

            Move(window, InBar(window));
            Assert.True((bool)GetField(window, "_statusHoverRevealed")!, "the bar's own spot reveals the collapsed bar");
            Assert.True(Surface(window).IsVisible);

            Move(window, new Point(200, window.Bounds.Height / 2 - 100));
            Advance(window, StatusBarAutoHide.HideDelayMs + 50);
            Assert.False((bool)GetField(window, "_statusHoverRevealed")!);
            Assert.True(Surface(window).IsVisible, "hover reveal returns to the single expand button; it never hides the surface");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Turning_the_hover_setting_off_stops_the_collapsed_bar_revealing()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusShowOnHoverWhenClosed = false;
            settings.StatusCollapsedToCorner = true;
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            Move(window, InBar(window));
            Assert.False((bool)GetField(window, "_statusHoverRevealed")!, "hover reveal is off: the collapsed bar stays collapsed");
            settings.StatusShowOnHoverWhenClosed = true;
            Move(window, InBar(window));
            Assert.True((bool)GetField(window, "_statusHoverRevealed")!);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Collapsed_with_corner_off_rests_as_a_small_arrow_and_every_other_state_keeps_the_full_button()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = false;
            settings.StatusShowOnHoverWhenClosed = false;

            Apply(window);                                                   // expanded bar
            Assert.False(Collapse(window).Classes.Contains("collapsedArrow"));

            SetField(window, "_statusCollapsed", true);
            Apply(window);                                                   // collapsed, corner off
            Assert.True(Collapse(window).Classes.Contains("collapsedArrow"), "rests as the small arrow");
            Assert.True(Surface(window).IsVisible, "the button stays exactly where the collapse button was (the bar remains laid out)");

            settings.StatusCollapsedToCorner = true;                         // corner mode keeps the normal button
            Apply(window);
            Assert.False(Collapse(window).Classes.Contains("collapsedArrow"));

            settings.StatusCollapsedToCorner = false;
            SetField(window, "_statusCollapsed", false);
            Apply(window);
            Assert.False(Collapse(window).Classes.Contains("collapsedArrow"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Hover_revealing_the_collapsed_bar_shows_the_full_button_not_the_arrow()
    {
        await OnWindow(window =>
        {
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = false;
            settings.StatusShowOnHoverWhenClosed = true;
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            Assert.True(Collapse(window).Classes.Contains("collapsedArrow"));

            Move(window, InBar(window));                                    // pointer reaches the bar's spot: bar revealed
            Assert.False(Collapse(window).Classes.Contains("collapsedArrow"));
            return Task.CompletedTask;
        });
    }

    // ---- render: arrow at rest, full button on hover ----------------------------------------------------------

    private static readonly string? SaveDirectory = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    [Theory]
    [InlineData("dark", "#141414")]
    [InlineData("mid", "#808080")]
    [InlineData("bright", "#F4F4F4")]
    public async Task Collapsed_expand_arrow_rests_on_a_subtle_backing_that_reads_on_dark_and_bright_images_and_the_full_button_shows_on_hover(string name, string backgroundColor)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            Button Make(bool arrow, bool hover, bool expandKind = true)
            {
                var button = new Button { Content = new GlideIconView { Kind = expandKind ? "Expand" : "Collapse" } };
                button.Classes.Add("statusIcon"); button.Classes.Add("legacyStatusIcon");
                ((GlideIconView)button.Content).Classes.Add("statusGlyph");
                if (arrow) button.Classes.Add("collapsedArrow");
                if (hover) ((IPseudoClasses)button.Classes).Set(":pointerover", true);
                return button;
            }
            var rest = Make(arrow: true, hover: false);
            var hovered = Make(arrow: true, hover: true);
            var normal = Make(arrow: false, hover: false, expandKind: false);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(20) };
            row.Children.Add(rest); row.Children.Add(hovered); row.Children.Add(normal);
            var background = Color.Parse(backgroundColor);
            var window = new Window { Width = 300, Height = 100, Content = row, Background = new SolidColorBrush(background) };
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                var pixels = Pixels(bitmap, out var w);
                if (!string.IsNullOrWhiteSpace(SaveDirectory)) { Directory.CreateDirectory(SaveDirectory); bitmap.Save(Path.Combine(SaveDirectory, $"status-expand-button-rest-hover-normal-on-{name}.png")); }

                Color At(Visual v, double dx, double dy)
                {
                    var origin = v.TranslatePoint(default, window)!.Value;
                    return PixelAt(pixels, w, (int)(origin.X + dx), (int)(origin.Y + dy));
                }
                ContentPresenter Presenter(Button b) => b.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");

                // The button keeps its normal slot (46 DIP legacy size); only the backing inside it is 26 x 20 at rest, centred.
                var presenter = Presenter(rest);
                Assert.Equal(46, rest.Bounds.Width, 1);
                Assert.Equal(26, presenter.Bounds.Width, 1);
                Assert.Equal(20, presenter.Bounds.Height, 1);
                Assert.Equal((46 - 26) / 2.0, presenter.Bounds.X, 1);
                Assert.Equal((46 - 20) / 2.0, presenter.Bounds.Y, 1);
                Assert.Equal(46, Presenter(hovered).Bounds.Width, 1);          // hover: the full framed button

                // Backing fill (inside the border, left of the glyph) and its thin border are drawn at rest.
                var fill = At(rest, 12, 23);
                var border = At(rest, 10.5, 23);
                var contrastFill = Distance(fill, background);
                var contrastBorder = Distance(border, background);
                Assert.True(Math.Max(contrastFill, contrastBorder) >= 60, $"[{name}] the resting arrow's backing must be visible (fill {fill}, border {border}, window {background})");
                if (name == "bright") Assert.True(contrastFill >= 60, $"[{name}] the dark grey fill carries it on a bright image (fill {fill}, window {background})");
                if (name == "dark") Assert.True(contrastBorder >= 60, $"[{name}] the light border carries it on a dark image (border {border}, window {background})");
                // The glyph itself is visible on the backing: some pixel in the centre differs strongly from the fill.
                var glyphMax = 0;
                for (var dy = 16; dy <= 30; dy++)
                    for (var dx = 16; dx <= 30; dx++)
                        glyphMax = Math.Max(glyphMax, Distance(At(rest, dx, dy), fill));
                Assert.True(glyphMax >= 120, $"[{name}] the arrow glyph must stand out from its backing (max difference {glyphMax})");

                // Hover: full button face; normal (expanded bar) button keeps its face.
                Assert.True(Distance(At(hovered, 6, 23), background) > 20, $"[{name}] hovering shows the full button/shade");
                Assert.True(Distance(At(normal, 6, 23), background) > 20, $"[{name}] normal button keeps its face");

                // Glyph size: 14 DIP at rest (was 12), 20 when hovered.
                var restGlyph = ((GlideIconView)rest.Content!).Bounds;
                var hoverGlyph = ((GlideIconView)hovered.Content!).Bounds;
                Assert.True(restGlyph.Width is > 12 and <= 14.5 && restGlyph.Height is > 12 and <= 14.5, $"resting glyph is {restGlyph.Width}x{restGlyph.Height}");
                Assert.True(hoverGlyph.Width >= 19, $"hovered glyph is {hoverGlyph.Width}x{hoverGlyph.Height}");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Reveal_area_renders_for_a_wide_window_and_a_narrow_wrapped_window_for_a_visual_check()
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;      // only produces PNGs when GLIDE_SAVE_RENDERS is set
        await OnWindow(window =>
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            var settings = Settings(window);

            // The zone is drawn here, in the test only: a translucent red fill with a solid outline over the real bar.
            void Shot(string name, double width, double height, int expand = 0)
            {
                settings.StatusRevealZoneExpand = expand;
                Resize(window, width, height);
                var zone = StatusBarAutoHide.RevealZone(Box(window), window.Bounds.Width, window.Bounds.Height, expand);
                var overlay = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(48, 255, 40, 40)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(255, 60, 60)), BorderThickness = new Thickness(2),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    Margin = new Thickness(zone.Left, zone.Top, 0, 0), Width = Math.Max(1, zone.Width), Height = Math.Max(1, zone.Height),
                    IsHitTestVisible = false, ZIndex = 5000,
                };
                var root = window.FindControl<Grid>("MainRoot")!;
                Grid.SetRowSpan(overlay, 2);
                root.Children.Add(overlay);
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    bitmap.Render(window);
                    Directory.CreateDirectory(SaveDirectory!);
                    bitmap.Save(Path.Combine(SaveDirectory!, $"reveal-area-{name}.png"));
                }
                finally { root.Children.Remove(overlay); }
            }

            Shot("1-wide-window-thin-strip-adjust-0", 1000, 700);
            Shot("2-narrow-window-wrapped-taller-adjust-0", 430, 700);
            Shot("3-wide-window-adjust-plus-40", 1000, 700, 40);
            Shot("4-narrow-window-adjust-minus-15", 430, 700, -15);
            settings.StatusRevealZoneExpand = 0;
            Resize(window, 1000, 700);
            SetField(window, "_statusCollapsed", true);
            settings.StatusCollapsedToCorner = true;
            Shot("5-wide-window-collapsed-to-corner-zone-is-where-the-bar-would-be", 1000, 700);
            Shot("6-narrow-window-collapsed-to-corner", 430, 700);
            SetField(window, "_statusCollapsed", false);
            settings.StatusAutoHide = true;
            Shot("7-wide-window-auto-hide-hidden-zone-is-where-the-bar-appears", 1000, 700);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Draws the default (adjustment 0) reveal area over the bar in every state in which the bar is not simply sitting there and
    /// checks, from the pixels of the real revealed bar, that the area is the bar's own rectangle (within 1 DIP). PNGs (area drawn
    /// over the state as the user sees it, and over the revealed bar) are written when GLIDE_SAVE_RENDERS is set.
    /// </summary>
    [Theory]
    [InlineData("1-wide-expanded", 1000, 700, "expanded")]
    [InlineData("2-narrow-wrapped-expanded", 430, 700, "expanded")]
    [InlineData("3-wide-collapsed-to-corner", 1000, 700, "corner")]
    [InlineData("4-narrow-wrapped-collapsed-to-corner", 430, 700, "corner")]
    [InlineData("5-wide-auto-hide-hidden", 1000, 700, "autohide")]
    [InlineData("6-narrow-wrapped-auto-hide-hidden", 430, 700, "autohide")]
    public async Task The_default_reveal_area_drawn_over_the_bar_matches_the_bars_rectangle_in_every_state(string name, double width, double height, string state)
    {
        await OnWindow(window =>
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            var settings = Settings(window);
            settings.StatusRevealZoneExpand = 0;
            settings.StatusCollapsedToCorner = true;
            Resize(window, width, height);
            switch (state)
            {
                case "corner": SetField(window, "_statusCollapsed", true); break;
                case "autohide": settings.StatusAutoHide = true; break;
            }
            Apply(window);
            Assert.Equal(state == "autohide" ? false : true, Surface(window).IsVisible);
            if (state == "corner") Assert.True(Surface(window).Bounds.Width < 80, "collapsed to just the corner button");

            var zone = StatusBarAutoHide.RevealZone(Box(window), window.Bounds.Width, window.Bounds.Height, 0);
            var overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(48, 255, 40, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 60, 60)), BorderThickness = new Thickness(2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(zone.Left, zone.Top, 0, 0), Width = Math.Max(1, zone.Width), Height = Math.Max(1, zone.Height),
                IsHitTestVisible = false, ZIndex = 5000,
            };
            var root = window.FindControl<Grid>("MainRoot")!;
            Grid.SetRowSpan(overlay, 2);
            root.Children.Add(overlay);
            void Save(string suffix)
            {
                if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                Directory.CreateDirectory(SaveDirectory);
                bitmap.Save(Path.Combine(SaveDirectory, $"reveal-zone-{name}-{suffix}.png"));
            }
            try
            {
                Save("as-the-user-sees-it");

                // The bar as it is when shown: live layout for an expanded bar, the revealed bar for the other two states.
                if (state != "expanded")
                {
                    Move(window, new Point((zone.Left + zone.Right) / 2, (zone.Top + zone.Bottom) / 2));
                    Assert.True(Surface(window).IsVisible);
                    Assert.True(SurfaceRect(window).Width > 80, "the full bar is shown");
                }
                var bar = SurfaceRect(window);
                Assert.Equal(bar.Left, zone.Left, 1.0);
                Assert.Equal(bar.Top, zone.Top, 1.0);
                Assert.Equal(bar.Right, zone.Right, 1.0);
                Assert.Equal(bar.Bottom, zone.Bottom, 1.0);
                if (width < 600) Assert.True(bar.Height > 60 && zone.Height > 60, $"wrapped bar is taller than a strip ({bar.Height})");
                Save("bar-revealed");
            }
            finally { root.Children.Remove(overlay); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Main_window_renders_the_status_bar_in_each_state_for_a_visual_check()
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;      // only produces PNGs when GLIDE_SAVE_RENDERS is set
        await OnWindow(window =>
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            var settings = Settings(window);
            settings.StatusCollapsedToCorner = false;
            settings.StatusShowOnHoverWhenClosed = false;

            void Shot(string name)
            {
                Apply(window);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                Directory.CreateDirectory(SaveDirectory!);
                bitmap.Save(Path.Combine(SaveDirectory!, $"main-window-status-{name}.png"));
            }

            Shot("1-expanded");
            SetField(window, "_statusCollapsed", true);
            Shot("2-collapsed-corner-off-arrow-at-rest");
            settings.StatusCollapsedToCorner = true;
            Shot("3-collapsed-corner-on");
            SetField(window, "_statusCollapsed", false);
            settings.StatusAutoHide = true;
            Shot("4-auto-hide-hidden");
            Move(window, InBar(window));
            Shot("5-auto-hide-revealed-at-bottom-edge");
            return Task.CompletedTask;
        });
    }

    private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static byte[] Pixels(Bitmap bitmap, out int width)
    {
        width = bitmap.PixelSize.Width;
        var buffer = new byte[width * bitmap.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, width, bitmap.PixelSize.Height), handle.AddrOfPinnedObject(), buffer.Length, width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    private static Color PixelAt(byte[] pixels, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static async Task OnWindow(Func<MainWindow, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                _clockMs = 1_000_000;
                SetField(window, "_statusRevealClock", (Func<long>)(() => _clockMs));   // tests drive the hide delay
                SetField(window, "_settings", new GlideSettingsState { ShowStatusSurface = true });
                window.Width = 1000; window.Height = 700;
                window.Show();
                ImageView(window).IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                window.Measure(new Size(1000, 700));
                window.Arrange(new Rect(0, 0, 1000, 700));
                Apply(window);
                await body(window);
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private static void Apply(MainWindow window)
    {
        Invoke(window, "ApplyStatusVisibility");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static long _clockMs;

    private static void Move(MainWindow window, Point point)
    {
        SetField(window, "_lastWindowPointer", point);
        Invoke(window, "UpdateStatusHoverReveal", point);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Lets time pass with the pointer still (what the hide timer does) and re-checks the reveal state.</summary>
    private static void Advance(MainWindow window, long milliseconds)
    {
        _clockMs += milliseconds;
        Invoke(window, "UpdateStatusHoverReveal", (Point)GetField(window, "_lastWindowPointer")!);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Rect SurfaceRect(MainWindow window)
    {
        var surface = Surface(window);
        var origin = surface.TranslatePoint(default, window)!.Value;
        return new Rect(origin, surface.Bounds.Size);
    }

    /// <summary>The box the status bar occupies (or would occupy when revealed) in window space, as the reveal logic reads it.</summary>
    private static StatusRevealZone Box(MainWindow window) => (StatusRevealZone)Invoke(window, "CurrentStatusBarBox")!;

    /// <summary>A pointer position exactly in the middle of the bar's box.</summary>
    private static Point InBar(MainWindow window)
    {
        var box = Box(window);
        return new Point((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
    }

    private static void Resize(MainWindow window, double width, double height)
    {
        window.Width = width; window.Height = height;
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        Invoke(window, "ApplyStatusBarSize");                                // what the SizeChanged handler does
        Apply(window);
    }

    private static MenuItem AutoHideItem(MainWindow window)
    {
        var menu = (ContextMenu)Invoke(window, "BuildStatusSurfaceContextMenu")!;
        return menu.ItemsSource!.Cast<object>().OfType<MenuItem>().First(i => (i.Header?.ToString() ?? "").StartsWith("Auto-hide status bar", StringComparison.Ordinal));
    }

    private static Grid ImageView(MainWindow window) => window.FindControl<Grid>("ImageView")!;
    private static Border Surface(MainWindow window) => window.FindControl<Border>("ViewerStatusSurface")!;
    private static Button Restore(MainWindow window) => window.FindControl<Button>("StatusRestoreButton")!;
    private static Button Collapse(MainWindow window) => window.FindControl<Button>("StatusCollapseButton")!;
    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, Flags);
        Assert.NotNull(info);
        return info!.Invoke(window, args);
    }

    private static object? GetField(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return field!.GetValue(window);
    }

    private static void SetField(MainWindow window, string name, object? value)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }
}
