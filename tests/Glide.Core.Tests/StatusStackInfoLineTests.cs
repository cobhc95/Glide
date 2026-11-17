using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// "Always stack the information on a separate line" (status.stackInfoLine, on by default): the file info keeps its own top
/// line with the window buttons at its right and the button groups sit on the row below, even in a wide window where
/// everything would fit on one line. Off restores the single wide line. Covers the setting itself (default, persistence,
/// search, registry toggle, right-click item) and the layout of the real MainWindow in every state that depends on the
/// bar's box: Left / Center / Right, every Size, narrow windows, collapsed, Auto-hide reveal area, reserved fit space, Home.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class StatusStackInfoLineTests
{
    public StatusStackInfoLineTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- the setting ---------------------------------------------------------------------------------------

    [Fact]
    public void The_setting_is_catalogued_searchable_toggleable_and_on_by_default_in_the_status_bar_card()
    {
        var setting = Assert.Single(SettingsCatalog.All, s => s.Id == "status.stackInfoLine");
        Assert.Equal("Tabs & status bar", setting.Category);
        Assert.Equal(SettingKind.Toggle, setting.Kind);
        Assert.True((bool)setting.DefaultValue);
        Assert.Equal("Always stack the information on a separate line", setting.Label);
        foreach (var term in new[] { "stack", "separate line", "two rows", "wrap", "info line", "layout", "compact", "single line", "one line" })
            Assert.Contains(term, setting.SearchTerms);
        Assert.Contains(SettingEffectRegistry.All, e => e.SettingId == "status.stackInfoLine");
        Assert.True(new GlideSettingsState().StatusStackInfoLine);
        Assert.NotNull(SettingAccessors.Find("status.stackInfoLine"));
        // Every Toggle setting gets an automatic registry action (the bindable flag): hotkey list, gestures, menus.
        Assert.True(SettingActionCatalog.IsBindable(setting));
        var action = AppActionRegistry.ForSetting("status.stackInfoLine");
        Assert.NotNull(action);
        Assert.Equal("setting.status.stackInfoLine", action!.Id);
        Assert.Equal(AppActionKind.SettingToggle, action.Kind);
        Assert.Equal("status.stackInfoLine", action.SettingId);
        Assert.NotNull(HotkeyCatalog.Find(action.Id));
        Assert.NotNull(GestureCatalog.FindAction(action.Id));
        Assert.Contains(setting.Label, action.Label);
    }

    [Fact]
    public void Settings_files_without_the_key_get_the_new_default_and_an_explicit_value_is_kept()
    {
        WithIsolatedStore(() =>
        {
            var file = Path.Combine(SettingsStore.GetSettingsDirectory(), "glide.settings.json");
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusBarAlignment\":\"Right\"}");
            var loaded = SettingsStore.Load();
            Assert.True(loaded.StatusStackInfoLine, "an existing file without the key gets the new default (on)");
            Assert.Equal("Right", loaded.StatusBarAlignment);

            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusStackInfoLine\":false}");
            Assert.False(SettingsStore.Load().StatusStackInfoLine, "an explicit saved off stays off");
            File.WriteAllText(file, "{\"SettingsSchemaVersion\":7,\"StatusStackInfoLine\":true}");
            Assert.True(SettingsStore.Load().StatusStackInfoLine);

            // Save / load round trip, clone and content equality all carry the new field.
            var state = new GlideSettingsState { StatusStackInfoLine = false };
            SettingsStore.Save(state);
            Assert.False(SettingsStore.Load().StatusStackInfoLine);
            Assert.False(state.CloneState().StatusStackInfoLine);
            var other = state.CloneState();
            Assert.True(state.ContentEquals(other));
            other.StatusStackInfoLine = true;
            Assert.False(state.ContentEquals(other));
        });
    }

    [Fact]
    public async Task The_status_bar_menu_item_sits_in_the_Auto_resize_group_with_a_live_tick_and_toggles_the_setting_and_the_layout()
    {
        await OnWindow(window =>
        {
            Resize(window, 1800, 900);                                // wide enough for the single-line form
            var menu = (ContextMenu)Invoke(window, "BuildStatusSurfaceContextMenu")!;
            var entries = menu.ItemsSource!.Cast<object>().ToList();
            var items = entries.OfType<MenuItem>().ToList();
            var item = Assert.Single(items, i => (i.Header?.ToString() ?? "").StartsWith("Always stack the information on a separate line", StringComparison.Ordinal));
            Assert.Equal("Always stack the information on a separate line  ✓", item.Header);       // on by default
            Assert.Equal("setting.status.stackInfoLine", MenuActions.GetActionId(item));
            Assert.True(MenuActions.IsValidTag(MenuActions.GetActionId(item)));
            var icon = Assert.IsType<GlideIconView>(item.Icon);
            Assert.Equal("StackLines", icon.Kind);

            // Same group as Auto-hide / Reserve / Auto-resize: right after Auto-resize and before the separator and Size.
            var index = entries.IndexOf(item);
            Assert.StartsWith("Auto-resize", ((MenuItem)entries[index - 1]).Header!.ToString());
            Assert.Equal("-", ((MenuItem)entries[index + 1]).Header);
            Assert.StartsWith("Size", ((MenuItem)entries[index + 2]).Header!.ToString());

            var stacked = Surface(window).Bounds.Height;
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(Settings(window).StatusStackInfoLine);
            Assert.True(Surface(window).Bounds.Height < stacked - 20, $"switching it off re-lays the bar out as one line at once (setting={Settings(window).StatusStackInfoLine}, was {stacked}, now {Surface(window).Bounds.Height}, wc row {Grid.GetRow(WindowControls(window))}, panel row {Grid.GetRow(Controls(window))}, w={window.Bounds.Width})");
            var off = ((ContextMenu)Invoke(window, "BuildStatusSurfaceContextMenu")!).ItemsSource!.Cast<object>().OfType<MenuItem>()
                .Single(i => (i.Header?.ToString() ?? "").StartsWith("Always stack", StringComparison.Ordinal));
            Assert.Equal("Always stack the information on a separate line", off.Header);               // no tick while off

            off.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(Settings(window).StatusStackInfoLine);
            Assert.Equal(stacked, Surface(window).Bounds.Height, 0.5);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_registry_toggle_action_flips_the_setting_and_relayouts_the_open_window()
    {
        await OnWindow(async window =>
        {
            Assert.True(Settings(window).StatusStackInfoLine);
            Resize(window, 1800, 900);
            var stacked = Surface(window).Bounds.Height;
            await (Task)Invoke(window, "ExecuteGestureActionAsync", "setting.status.stackInfoLine", 0, null)!;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(Settings(window).StatusStackInfoLine);
            Assert.True(Surface(window).Bounds.Height < stacked - 20);
            Assert.Equal(0, Grid.GetRow(Controls(window)));
            await (Task)Invoke(window, "ExecuteGestureActionAsync", "setting.status.stackInfoLine", 0, null)!;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(Settings(window).StatusStackInfoLine);
            Assert.Equal(1, Grid.GetRow(Controls(window)));
            Assert.Equal(stacked, Surface(window).Bounds.Height, 0.5);
        });
    }

    // ---- row structure ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1800, 900)]
    [InlineData(1000, 700)]
    [InlineData(430, 700)]
    public async Task Stacked_puts_the_info_text_on_its_own_top_line_with_the_window_buttons_at_its_right_and_the_buttons_below(double width, double height)
    {
        await OnWindow(window =>
        {
            Resize(window, width, height);
            var surface = Rect(window, Surface(window));
            var stats = Rect(window, Stats(window));
            var panel = Rect(window, Controls(window));
            var wc = Rect(window, WindowControls(window));

            Assert.Equal(0, Grid.GetRow(StatsHost(window)));
            Assert.Equal(0, Grid.GetRow(WindowControls(window)));
            Assert.Equal(1, Grid.GetRow(Controls(window)));
            Assert.True(stats.Bottom <= panel.Top + 0.5, $"info row {stats} must be above the buttons {panel}");
            Assert.True(wc.Bottom <= panel.Top + 0.5, $"min/close row {wc} must be above the buttons {panel}");
            Assert.True(wc.Top - surface.Top <= 8, "min/close sit at the top of the bar");
            Assert.True(surface.Right - wc.Right <= 8 && surface.Right - wc.Right >= 0, $"min/close sit at the top-right: bar {surface}, buttons {wc}");
            Assert.True(stats.Right <= wc.Left + 0.5, "the info text ends before the window buttons");
            Assert.True(panel.Left - surface.Left <= 16, "the button groups start at the bar's left edge");
            Assert.True(surface.Left >= 0 && surface.Right <= width + 0.5, "the bar stays inside the window");
            Assert.True(surface.Height > 70, $"a taller two-row bar, got {surface.Height}");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Off_keeps_everything_on_one_line_in_a_wide_window_and_only_wraps_when_it_is_narrow()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusStackInfoLine = false;
            Resize(window, 1800, 900);
            Assert.Equal(0, Grid.GetRow(Controls(window)));
            Assert.Equal(0, Grid.GetRow(WindowControls(window)));
            Assert.False(Row2(window).IsVisible, "wide + off: one row");
            var stats = Rect(window, Stats(window));
            var panel = Rect(window, Controls(window));
            var wc = Rect(window, WindowControls(window));
            Assert.True(Math.Abs((stats.Top + stats.Bottom) / 2 - (panel.Top + panel.Bottom) / 2) < 8, "info text and buttons share one line");
            Assert.True(wc.Left >= panel.Right, "min/close sit after the button groups on that line");
            Assert.True(Surface(window).Bounds.Height < 70, $"one-line height, got {Surface(window).Bounds.Height}");

            Resize(window, 430, 700);                                // narrow: the old responsive logic wraps by itself
            Assert.Equal(1, Grid.GetRow(Controls(window)));
            Assert.Equal(0, Grid.GetRow(WindowControls(window)));
            Assert.True(Surface(window).Bounds.Height > 70);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task In_a_narrow_window_on_and_off_lay_out_identically_because_both_wrap()
    {
        await OnWindow(window =>
        {
            Resize(window, 430, 700);
            var on = Surface(window).Bounds;
            var onWidth = Surface(window).Bounds.Width;
            Settings(window).StatusStackInfoLine = false;
            Resize(window, 430, 700);
            Assert.Equal(on.Height, Surface(window).Bounds.Height, 0.5);
            Assert.Equal(onWidth, Surface(window).Bounds.Width, 0.5);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Toggling_live_relayouts_in_both_directions_in_a_wide_and_a_narrow_window()
    {
        await OnWindow(window =>
        {
            foreach (var width in new[] { 1800d, 1400d })
            {
                Resize(window, width, 800);
                Settings(window).StatusStackInfoLine = true;
                Invoke(window, "ApplyStatusBarSize"); Apply(window);
                var on = Surface(window).Bounds.Height;
                Settings(window).StatusStackInfoLine = false;
                Invoke(window, "ApplyStatusBarSize"); Apply(window);
                var off = Surface(window).Bounds.Height;
                Settings(window).StatusStackInfoLine = true;
                Invoke(window, "ApplyStatusBarSize"); Apply(window);
                Assert.True(on > off + 20, $"{width}: stacked {on} must be taller than single-line {off}");
                Assert.Equal(on, Surface(window).Bounds.Height, 0.5);
            }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Center")]
    [InlineData("Right")]
    public async Task Stacked_follows_Left_Center_and_Right_placement(string alignment)
    {
        await OnWindow(window =>
        {
            Settings(window).StatusBarAlignment = alignment;
            Resize(window, 1800, 900);
            Apply(window);
            var surface = Rect(window, Surface(window));
            switch (alignment)
            {
                case "Left": Assert.Equal(14, surface.Left, 0.6); break;
                case "Right": Assert.Equal(1800 - 14, surface.Right, 0.6); break;
                default: Assert.Equal(900, (surface.Left + surface.Right) / 2, 0.6); break;
            }
            Assert.Equal(900 - 14, surface.Bottom, 0.6);
            Assert.True(Rect(window, Stats(window)).Bottom <= Rect(window, Controls(window)).Top + 0.5);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("Tiny")]
    [InlineData("Very small")]
    [InlineData("Small")]
    [InlineData("Medium")]
    [InlineData("Large")]
    [InlineData("Extra large")]
    public async Task Stacked_works_at_every_size_preset_and_stays_inside_the_window(string size)
    {
        await OnWindow(window =>
        {
            Settings(window).StatusBarSize = size;
            foreach (var (width, height) in new[] { (1800d, 900d), (900d, 700d), (500d, 700d) })
            {
                Resize(window, width, height);
                var surface = Rect(window, Surface(window));
                Assert.True(surface.Left >= -0.5 && surface.Right <= width + 0.5 && surface.Bottom <= height + 0.5, $"{size} @ {width}: bar {surface} leaves the window");
                Assert.Equal(1, Grid.GetRow(Controls(window)));
                Assert.Equal(0, Grid.GetRow(WindowControls(window)));
                Assert.True(Rect(window, Stats(window)).Bottom <= Rect(window, Controls(window)).Top + 0.5, $"{size} @ {width}: info row overlaps the buttons");
                Assert.True(Rect(window, WindowControls(window)).Bottom <= Rect(window, Controls(window)).Top + 0.5);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_very_narrow_window_still_wraps_the_buttons_and_never_overflows_the_bar()
    {
        await OnWindow(window =>
        {
            foreach (var width in new[] { 330d, 300d, 280d, 240d })
            {
                Resize(window, width, 600);
                var surface = Rect(window, Surface(window));
                Assert.True(surface.Left >= -0.5 && surface.Right <= width + 0.5, $"{width}: bar {surface} leaves the window");
                var more = window.FindControl<Border>("MoreGroupBorder")!;
                Assert.True(Row1(window).Children.Count > 0);
                // Whatever does not fit goes into the "..." menu (or a second row), exactly as with stacking off.
                Assert.True(more.IsVisible || Row2(window).IsVisible || Row1(window).Bounds.Right <= surface.Right + 0.5);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_long_file_name_wraps_to_at_most_two_lines_and_ends_in_an_ellipsis_instead_of_leaving_the_bar()
    {
        await OnWindow(window =>
        {
            Resize(window, 900, 700);
            Stats(window).Text = string.Concat(Enumerable.Repeat("a-very-long-file-name-", 40)) + ".png  •  1 / 32  •  3840 × 2160  •  25%";
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            Assert.Equal(2, Stats(window).MaxLines);
            Assert.Equal(TextTrimming.CharacterEllipsis, Stats(window).TextTrimming);
            var surface = Rect(window, Surface(window));
            var stats = Rect(window, Stats(window));
            Assert.True(surface.Right <= 900 + 0.5 && surface.Left >= -0.5, $"bar {surface} must stay in the window");
            Assert.True(stats.Right <= surface.Right + 0.5);
            Assert.True(stats.Height < 80, $"at most two text lines, got {stats.Height}");
            Assert.True(Rect(window, WindowControls(window)).Right <= surface.Right + 0.5, "min/close stay visible at the top-right");

            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            Assert.Equal(0, Stats(window).MaxLines);                  // the previous behaviour is untouched
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task With_no_info_text_to_show_the_single_row_logic_applies_even_when_stacking_is_on()
    {
        await OnWindow(window =>
        {
            Resize(window, 240, 600);                                 // below 260 DIP the info text is hidden
            Assert.False(StatsHost(window).IsVisible);
            var on = Surface(window).Bounds;
            var onRows = (Grid.GetRow(Controls(window)), Grid.GetRow(WindowControls(window)), Row2(window).IsVisible);
            Settings(window).StatusStackInfoLine = false;
            Resize(window, 240, 600);
            // Nothing to stack: identical to the single-row logic, so the option changes nothing here.
            Assert.Equal(on.Height, Surface(window).Bounds.Height, 0.5);
            Assert.Equal(onRows, (Grid.GetRow(Controls(window)), Grid.GetRow(WindowControls(window)), Row2(window).IsVisible));
            return Task.CompletedTask;
        });
    }

    // ---- collapsed bar, fullscreen --------------------------------------------------------------------------

    [Fact]
    public async Task The_collapsed_corner_button_is_in_the_same_place_with_stacking_on_or_off()
    {
        await OnWindow(window =>
        {
            Resize(window, 1400, 800);
            SetField(window, "_statusCollapsed", true);
            Settings(window).StatusCollapsedToCorner = true;
            Settings(window).StatusShowOnHoverWhenClosed = false;
            Apply(window);
            var on = Rect(window, Collapse(window));
            Assert.True(Collapse(window).IsVisible);
            Assert.True(on.Right > 1400 - 40 && on.Bottom > 800 - 80, $"parked lower-right, got {on}");

            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            var off = Rect(window, Collapse(window));
            Assert.Equal(off.Right, on.Right, 0.6);
            Assert.Equal(off.Bottom, on.Bottom, 0.6);
            Assert.Equal(off.Top, on.Top, 0.6);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task With_the_corner_option_off_the_collapse_arrow_stays_at_the_top_right_of_the_stacked_bar()
    {
        await OnWindow(window =>
        {
            Resize(window, 1400, 800);
            Settings(window).StatusCollapsedToCorner = false;
            Settings(window).StatusShowOnHoverWhenClosed = false;
            Apply(window);
            var expandedButton = Rect(window, Collapse(window));
            var bar = Rect(window, Surface(window));
            Assert.True(expandedButton.Top - bar.Top <= 8 && bar.Right - expandedButton.Right <= 8 + Close(window).Bounds.Width + 4, $"collapse button {expandedButton} in bar {bar}");

            SetField(window, "_statusCollapsed", true);
            Apply(window);
            var collapsed = Rect(window, Collapse(window));
            Assert.Equal(expandedButton.Left, collapsed.Left, 0.6);       // the button never moves when collapsing
            Assert.Equal(expandedButton.Top, collapsed.Top, 0.6);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Fullscreen_uses_the_stacked_layout_and_the_boosted_size()
    {
        await OnWindow(window =>
        {
            // Measured on the nine groups only (5.0.7 adds the Loop this folder button by default).
            Settings(window).StatusBarGroups = Glide.Core.StatusBarGroupCatalog.Format(Glide.Core.StatusBarGroupCatalog.DefaultOrder);
            Settings(window).FullscreenStatusAlwaysOn = true;
            window.WindowState = WindowState.FullScreen;
            Dispatcher.UIThread.RunJobs();
            Resize(window, window.Bounds.Width > 0 ? window.Bounds.Width : 1000, window.Bounds.Height > 0 ? window.Bounds.Height : 700);
            Assert.True(Surface(window).IsVisible);
            Assert.Equal(1, Grid.GetRow(Controls(window)));
            Assert.Equal(0, Grid.GetRow(WindowControls(window)));
            var stacked = Surface(window).Bounds.Height;
            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            Assert.True(Surface(window).Bounds.Height < stacked - 20 || window.Bounds.Width < 900, "fullscreen follows the option too");
            return Task.CompletedTask;
        });
    }

    // ---- reveal area ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1800, 900, "Center")]
    [InlineData(1800, 900, "Left")]
    [InlineData(1800, 900, "Right")]
    [InlineData(1000, 700, "Center")]
    [InlineData(430, 700, "Center")]
    public async Task The_reveal_area_is_the_exact_stacked_bar_rectangle_and_the_hidden_measure_matches_the_revealed_bar(double width, double height, string alignment)
    {
        await OnWindow(window =>
        {
            Settings(window).StatusBarAlignment = alignment;
            Settings(window).StatusAutoHide = true;
            Resize(window, width, height);
            Assert.False(Surface(window).IsVisible);
            var hiddenBox = Box(window);                              // measured while hidden

            Move(window, new Point((hiddenBox.Left + hiddenBox.Right) / 2, (hiddenBox.Top + hiddenBox.Bottom) / 2));
            Assert.True(Surface(window).IsVisible);
            var live = Rect(window, Surface(window));
            Assert.Equal(live.Left, hiddenBox.Left, 0.6);
            Assert.Equal(live.Top, hiddenBox.Top, 0.6);
            Assert.Equal(live.Right, hiddenBox.Right, 0.6);
            Assert.Equal(live.Bottom, hiddenBox.Bottom, 0.6);
            Assert.True(hiddenBox.Height > 70, "the area follows the taller stacked bar");

            var shownBox = Box(window);                               // read from the live layout
            Assert.Equal(live.Top, shownBox.Top, 0.6);
            Assert.Equal(live.Bottom, shownBox.Bottom, 0.6);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_reveal_area_re_measures_live_when_the_option_is_toggled_while_the_bar_is_hidden()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusAutoHide = true;
            Resize(window, 1800, 900);
            var stacked = Box(window);
            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            var single = Box(window);
            Assert.True(stacked.Height > single.Height + 20, $"stacked area {stacked.Height} must be taller than the single line area {single.Height}");
            Assert.True(stacked.Width < single.Width, "and narrower: the one-line bar is the widest form");
            Assert.Equal(900 - 14, single.Bottom, 0.6);

            // Reveal in single-line form, then switch the option on through the setting while the bar is shown and hidden again.
            Move(window, new Point((single.Left + single.Right) / 2, (single.Top + single.Bottom) / 2));
            Assert.True(Surface(window).IsVisible);
            Invoke(window, "MainWindowPointerExited", window, null);
            Assert.False(Surface(window).IsVisible);
            Settings(window).StatusStackInfoLine = true;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            var again = Box(window);
            Assert.Equal(stacked.Height, again.Height, 0.6);
            Assert.Equal(stacked.Width, again.Width, 0.6);
            Move(window, new Point(300, 100));
            Move(window, new Point((again.Left + again.Right) / 2, again.Top + 2));       // the upper (info) row of the taller bar reveals
            Assert.True(Surface(window).IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_collapsed_corner_reveal_box_is_the_stacked_bar_and_follows_the_option()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusCollapsedToCorner = true;
            Resize(window, 1800, 900);
            SetField(window, "_statusCollapsed", true);
            Apply(window);
            var stacked = Box(window);
            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            var single = Box(window);
            Assert.True(stacked.Height > single.Height + 20);
            return Task.CompletedTask;
        });
    }

    // ---- reserve space when fitting -------------------------------------------------------------------------

    [Fact]
    public async Task Reserve_space_when_fitting_uses_the_stacked_height_and_re_fits_on_toggle()
    {
        await OnWindow(window =>
        {
            Settings(window).StatusReserveFitSpace = true;
            Resize(window, 1400, 800);
            var viewport = window.FindControl<ImageViewport>("Viewport")!;
            double Expected() => Math.Max(0, viewport.Bounds.Bottom - Surface(window).Bounds.Top);

            Invoke(window, "UpdateViewportStatusInset"); Apply(window);
            var stackedInset = viewport.FitBottomInset;
            Assert.Equal(Expected(), stackedInset, 0.6);
            var stackedHeight = Surface(window).Bounds.Height;
            Assert.True(stackedInset >= stackedHeight + 14 - 0.6, "the reserved area covers the taller bar and its bottom margin");

            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            var singleInset = viewport.FitBottomInset;                // no extra call: the bar's bounds change re-fits by itself
            Assert.Equal(Expected(), singleInset, 0.6);
            Assert.True(stackedInset > singleInset + 20, $"stacked reserves more ({stackedInset}) than the single line ({singleInset})");

            Settings(window).StatusStackInfoLine = true;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            Assert.Equal(stackedInset, viewport.FitBottomInset, 0.6);
            return Task.CompletedTask;
        });
    }

    // ---- Home page ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_Home_page_bar_stacks_too_and_the_option_toggles_it_live()
    {
        await OnWindow(window =>
        {
            var home = (HomeSurface)Invoke(window, "EnsureHomeSurface")!;
            ImageView(window).IsVisible = false;
            window.FindControl<ContentControl>("HomeHost")!.IsVisible = true;
            Resize(window, 1400, 800);
            var grid = home.GetLogicalDescendants().OfType<Grid>().First(g => g.Name == "HomeStatusLayoutGrid");
            Assert.True(home.StatusSurface.IsVisible);
            Assert.True(home.StatusStacked);
            var surface = home.StatusSurface.Bounds;
            Assert.Equal(1, Grid.GetRow(home.StatusControlsPanel));
            Assert.Equal(0, Grid.GetRow(home.StatusWindowControls));
            Assert.Equal(0, Grid.GetRow(home.StatusText));
            var text = home.StatusText.TranslatePoint(default, window)!.Value;
            var controls = home.StatusControlsPanel.TranslatePoint(default, window)!.Value;
            var wc = home.StatusWindowControls.TranslatePoint(default, window)!.Value;
            Assert.True(text.Y + home.StatusText.Bounds.Height <= controls.Y + 0.5);
            Assert.True(wc.Y + home.StatusWindowControls.Bounds.Height <= controls.Y + 0.5);
            Assert.True(surface.Height > 70, $"stacked Home bar is two rows tall, got {surface.Height}");
            Assert.NotNull(grid);

            Settings(window).StatusStackInfoLine = false;
            Invoke(window, "ApplyStatusBarSize"); Apply(window);
            Assert.False(home.StatusStacked);
            Assert.Equal(0, Grid.GetRow(home.StatusControlsPanel));
            Assert.Equal(0, Grid.GetRow(home.StatusWindowControls));
            Assert.Equal(48, home.StatusSurface.Bounds.Height, 0.6);       // the previous fixed one-line height (Small)
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_Home_page_collapsed_corner_button_and_reveal_box_follow_the_stacked_bar()
    {
        await OnWindow(window =>
        {
            var home = (HomeSurface)Invoke(window, "EnsureHomeSurface")!;
            ImageView(window).IsVisible = false;
            window.FindControl<ContentControl>("HomeHost")!.IsVisible = true;
            Settings(window).StatusAutoHide = true;
            Resize(window, 1400, 800);
            Assert.False(home.StatusSurface.IsVisible);
            var box = Box(window);
            Move(window, new Point((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2));
            Assert.True(home.StatusSurface.IsVisible);
            var origin = home.StatusSurface.TranslatePoint(default, window)!.Value;
            Assert.Equal(origin.Y, box.Top, 0.6);
            Assert.Equal(origin.Y + home.StatusSurface.Bounds.Height, box.Bottom, 0.6);
            Assert.True(box.Height > 70);
            return Task.CompletedTask;
        });
    }

    // ---- renders for the visual check ----------------------------------------------------------------------

    private static readonly string? SaveDirectory = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    [Fact]
    public async Task Renders_of_the_status_bar_stacked_and_single_line_for_a_visual_check()
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;      // only produces PNGs when GLIDE_SAVE_RENDERS is set
        await OnWindow(window =>
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.Styles.Insert(0, new FluentTheme());
            window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
            Stats(window).Text = "3m90fxiwwfo61.png  •  1 / 32  •  3840 × 2160  •  25%";

            void Shot(string name, double width, double height, bool stacked, string size = "Small", bool home = false)
            {
                Settings(window).StatusStackInfoLine = stacked;
                Settings(window).StatusBarSize = size;
                if (home)
                {
                    Invoke(window, "EnsureHomeSurface");
                    ImageView(window).IsVisible = false;
                    window.FindControl<ContentControl>("HomeHost")!.IsVisible = true;
                }
                Resize(window, width, height);
                Stats(window).Text = "3m90fxiwwfo61.png  •  1 / 32  •  3840 × 2160  •  25%";
                Invoke(window, "ApplyStatusBarSize");
                Apply(window);
                var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                Directory.CreateDirectory(SaveDirectory!);
                bitmap.Save(Path.Combine(SaveDirectory!, $"{name}.png"));
            }

            Shot("1-wide-1800-stacked-on", 1800, 500, true);
            Shot("2-wide-1800-stacked-off", 1800, 500, false);
            Shot("3-narrow-430-stacked-on", 430, 600, true);
            Shot("4-narrow-430-stacked-off", 430, 600, false);
            Shot("5-size-tiny-stacked-on", 1000, 400, true, "Tiny");
            Shot("6-size-extra-large-stacked-on", 1400, 500, true, "Extra large");
            Shot("7-size-tiny-stacked-off", 1000, 400, false, "Tiny");
            Shot("8-size-extra-large-stacked-off", 1400, 500, false, "Extra large");
            Shot("9-home-wide-stacked-on", 1400, 500, true, home: true);
            Shot("10-home-wide-stacked-off", 1400, 500, false, home: true);
            return Task.CompletedTask;
        });
    }

    // ---- helpers --------------------------------------------------------------------------------------------

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

    private static Task OnWindow(Func<MainWindow, Task> body) => OnWindowAsync(body);

    private static async Task OnWindowAsync(Func<MainWindow, Task> body)
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
                SetField(window, "_statusRevealClock", (Func<long>)(() => _clockMs));
                // A fresh state: every status setting at its default, so stacking is ON exactly as for a new user.
                SetField(window, "_settings", new GlideSettingsState { ShowStatusSurface = true, StatusGifControlsOnlyOnGifs = true });
                // The bar geometry below assumes no GIF group (no animated image open); the GIF controls are always shown by default.
                Invoke(window, "UpdateGifStatusUi");
                window.Width = 1000; window.Height = 700;
                window.Show();
                ImageView(window).IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                window.Measure(new Size(1000, 700));
                window.Arrange(new Avalonia.Rect(0, 0, 1000, 700));
                Invoke(window, "ApplyStatusBarSize");
                Apply(window);
                Stats(window).Text = "3m90fxiwwfo61.png  •  1 / 32  •  3840 × 2160  •  25%";
                Invoke(window, "ApplyStatusBarSize");
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

    private static long _clockMs;

    private static void Apply(MainWindow window)
    {
        Invoke(window, "ApplyStatusVisibility");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Resize(MainWindow window, double width, double height)
    {
        window.Width = width; window.Height = height;
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(width, height));
        window.Arrange(new Avalonia.Rect(0, 0, width, height));
        Invoke(window, "ApplyStatusBarSize");                       // what the SizeChanged handler does
        Apply(window);
    }

    private static void Move(MainWindow window, Point point)
    {
        SetField(window, "_lastWindowPointer", point);
        Invoke(window, "UpdateStatusHoverReveal", point);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Avalonia.Rect Rect(MainWindow window, Control control)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        return new Avalonia.Rect(origin, control.Bounds.Size);
    }

    private static StatusRevealZone Box(MainWindow window) => (StatusRevealZone)Invoke(window, "CurrentStatusBarBox")!;
    private static Grid ImageView(MainWindow window) => window.FindControl<Grid>("ImageView")!;
    private static Border Surface(MainWindow window) => window.FindControl<Border>("ViewerStatusSurface")!;
    private static TextBlock Stats(MainWindow window) => window.FindControl<TextBlock>("StatusStatsText")!;
    private static StackPanel StatsHost(MainWindow window) => window.FindControl<StackPanel>("StatusStatsHost")!;
    private static StackPanel Controls(MainWindow window) => window.FindControl<StackPanel>("StatusControlsPanel")!;
    private static StackPanel WindowControls(MainWindow window) => window.FindControl<StackPanel>("StatusWindowControls")!;
    private static StackPanel Row1(MainWindow window) => window.FindControl<StackPanel>("StatusRow1")!;
    private static StackPanel Row2(MainWindow window) => window.FindControl<StackPanel>("StatusRow2")!;
    private static Button Collapse(MainWindow window) => window.FindControl<Button>("StatusCollapseButton")!;
    private static Button Close(MainWindow window) => window.FindControl<Button>("StatusCloseButton")!;
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
