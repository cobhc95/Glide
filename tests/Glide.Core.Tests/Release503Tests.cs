using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>5.0.3 features and fixes: pure logic and headless window checks.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class Release503Tests
{
    public Release503Tests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    [Fact]
    public void Tab_tooltip_lines_are_the_name_and_the_containing_folder()
    {
        Assert.Equal(("cat.png", @"C:\Pics"), MainWindow.TabToolTipLines("cat.png", @"C:\Pics\cat.png"));
        Assert.Equal(("Pics", @"C:\"), MainWindow.TabToolTipLines("Pics", @"C:\Pics\"));
        Assert.Equal(("Home", null), MainWindow.TabToolTipLines("Home", null));
    }

    [Theory]
    [InlineData("a<br>b", "a\nb")]
    [InlineData("a<BR/>b<br />c", "a\nb\nc")]
    [InlineData("no breaks", "no breaks")]
    public void Text_overlay_br_starts_a_new_line(string template, string expected) =>
        Assert.Equal(expected, MainWindow.ApplyTextOverlayLineBreaks(template));

    [Fact]
    public void Status_bar_groups_parse_format_and_default()
    {
        Assert.Equal(StatusBarGroupCatalog.DefaultOrder, StatusBarGroupCatalog.Parse(""));
        Assert.Equal(new[] { "nav", "zoom" }, StatusBarGroupCatalog.Parse("nav, zoom, bogus, nav"));
        Assert.Equal(string.Empty, StatusBarGroupCatalog.Format(StatusBarGroupCatalog.DefaultOrder));
        Assert.Equal("nav,zoom", StatusBarGroupCatalog.Format(["nav", "zoom"]));
        Assert.Equal("none", StatusBarGroupCatalog.Format([]));
        Assert.Empty(StatusBarGroupCatalog.Parse("none"));
    }

    [Fact]
    public void Status_bar_offers_registry_actions_as_extra_buttons_none_by_default()
    {
        Assert.True(StatusBarGroupCatalog.AvailableActions.Count >= 20);
        Assert.All(StatusBarGroupCatalog.AvailableActions, a =>
        {
            var action = AppActionRegistry.Find(a.ActionId);
            Assert.NotNull(action);
            Assert.False(action!.Destructive);
            Assert.Equal(action.Icon, a.Icon);
        });
        Assert.DoesNotContain(StatusBarGroupCatalog.AvailableActions, a => a.ActionId == "view.zoomIn");
        Assert.All(StatusBarGroupCatalog.Parse(""), id => Assert.True(StatusBarGroupCatalog.IsGroup(id)));
        var extra = StatusBarGroupCatalog.AvailableActions[0].Id;
        Assert.Equal(new[] { "nav", extra }, StatusBarGroupCatalog.Parse("nav," + extra + ",a:no.such.action"));
    }

    [Fact]
    public async Task Customizer_adds_removes_moves_and_restores_like_the_title_bar_one()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new StatusBarCustomizerWindow(string.Empty);
            Assert.Equal(StatusBarGroupCatalog.DefaultOrder, window.Shown);
            Assert.Equal(string.Empty, window.Result());

            var extra = StatusBarGroupCatalog.AvailableActions[0].Id;
            window.AvailableList.SelectedItem = window.AvailableList.Items.OfType<ListBoxItem>().First(i => (string?)i.Tag == extra);
            window.AddSelected();
            Assert.Equal(extra, window.Shown[^1]);
            window.MoveSelected(-1);
            Assert.Equal(extra, window.Shown[^2]);
            Assert.Contains(extra, window.Result(), StringComparison.Ordinal);

            window.ShownList.SelectedItem = window.ShownList.Items.OfType<ListBoxItem>().First(i => (string?)i.Tag == "zoom");
            window.RemoveSelected();
            Assert.DoesNotContain("zoom", window.Shown);
            Assert.Contains(window.AvailableList.Items.OfType<ListBoxItem>(), i => (string?)i.Tag == "zoom");

            // Every row is one ListBoxItem with a transparent, stretched hit area (whole-row click selects it).
            Assert.All(window.ShownList.Items.OfType<ListBoxItem>(), i =>
            {
                Assert.Equal(Avalonia.Layout.HorizontalAlignment.Stretch, i.HorizontalContentAlignment);
                Assert.NotNull(((Grid)i.Content!).Background);
            });

            window.RestoreDefaults();
            Assert.Equal(string.Empty, window.Result());
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_added_action_appears_as_a_status_icon_button_and_default_bar_is_unchanged()
    {
        await OnWindow(window =>
        {
            window.Width = 1600; window.Height = 800;
            window.Show();
            var live = (GlideSettingsState)Field(window, "_settings").GetValue(window)!;
            window.FindControl<Grid>("ImageView")!.IsVisible = true;
            InvokeArgs(window, "ApplyStatusBarSize");
            Dispatcher.UIThread.RunJobs();
            var row = window.FindControl<StackPanel>("StatusRow1")!;
            var defaultButtons = row.GetLogicalDescendants().OfType<Button>().Count();
            Assert.DoesNotContain(row.Children.OfType<Border>(), b => b.Classes.Contains("extraGroup"));

            var extra = StatusBarGroupCatalog.AvailableActions.First(a => a.ActionId == "view.fullscreen" || a.Icon.Length > 0);
            live.StatusBarGroups = StatusBarGroupCatalog.Format(StatusBarGroupCatalog.DefaultOrder.Append(extra.Id));
            InvokeArgs(window, "ApplyStatusBarSize");
            Dispatcher.UIThread.RunJobs();
            var added = row.Children.OfType<Border>().Single(b => b.Classes.Contains("extraGroup"));
            var button = Assert.IsType<Button>(((StackPanel)added.Child!).Children.Single());
            Assert.Contains("statusIcon", button.Classes);
            Assert.Contains("legacyStatusIcon", button.Classes);
            Assert.Equal(extra.Icon, Assert.IsType<Glide.App.Controls.GlideIconView>(button.Content).Kind);
            Assert.Equal(defaultButtons + 1, row.GetLogicalDescendants().OfType<Button>().Count());
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("Large icons", "Large thumbnails")]
    [InlineData("extra large icons", "Extra large thumbnails")]
    [InlineData("Details", "Details")]
    [InlineData("nonsense", "Large thumbnails")]
    public void Explorer_view_names_say_thumbnails_and_old_values_migrate(string saved, string expected) =>
        Assert.Equal(expected, ExplorerDefaultViews.Normalize(saved));

    [Fact]
    public void Hotkey_conflict_prompt_uses_real_quotes_not_mojibake()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        var shortcut = map["nav.first"].First();
        var text = HotkeyConflicts.Check(map, shortcut, "nav.last").PromptText();
        Assert.DoesNotContain("â€", text, StringComparison.Ordinal);
        Assert.Contains("\u201c", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_text_overlay_style_changes_take_the_fast_preview_path()
    {
        var a = new GlideSettingsState();
        var b = a.CloneState();
        b.PictureCounterFontSize = 22;
        Assert.True(MainWindow.OnlyTextOverlayStyleDiffers(a, b));
        b.StatusAutoHide = !a.StatusAutoHide;
        Assert.False(MainWindow.OnlyTextOverlayStyleDiffers(a, b));
        Assert.False(MainWindow.OnlyTextOverlayStyleDiffers(a, a.CloneState()));
    }

    [Fact]
    public void Unsaved_changes_compare_collections_by_value_and_read_as_words()
    {
        var a = new GlideSettingsState();
        var b = a.CloneState();
        Assert.Empty(SettingsChangeSet.Compute(a, b));
        b.CustomGestures = [new CustomGesture()];
        var change = Assert.Single(SettingsChangeSet.Compute(a, b));
        Assert.Equal("Custom gestures", change.Name);
        Assert.DoesNotContain("System.", change.Before + change.After, StringComparison.Ordinal);
    }

    [Fact]
    public void New_settings_and_actions_are_registered_and_searchable()
    {
        foreach (var id in new[] { "status.gifOnlyOnGifs", "status.fileNameClickActions", "status.customizeButtons", "tabs.position", "tabs.sideAutoHide", "tabs.topAutoHide", "settings.alwaysExpand" })
            Assert.Contains(SettingsCatalog.All, s => s.Id == id);
        Assert.False((bool)SettingsCatalog.All.Single(s => s.Id == "settings.alwaysExpand").DefaultValue!);
        foreach (var id in new[] { "tabs.tooltipLarger", "tabs.tooltipSmaller", "statusbar.customize" })
            Assert.NotNull(AppActionRegistry.Find(id));
    }

    [Fact]
    public void Tab_drag_holds_the_last_slot_and_tears_off_only_well_away_from_the_strip()
    {
        var source = File.ReadAllText(FindRepoFile("src/Glide.App/Services/TabDragController.cs"));
        Assert.Contains("_host.GetInsertIndex(_lastScreen) : _targetIndex", source, StringComparison.Ordinal);
        Assert.Contains("_host.IsBeyondTearOff(_lastScreen) && _host.TabDetachEnabled()", source, StringComparison.Ordinal);
        var startup = File.ReadAllText(FindRepoFile("src/Glide.App/MainWindow.axaml.cs"));
        Assert.Contains("!_isTransferredTabWindow", startup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gif_controls_show_greyed_out_by_default_and_hide_with_the_option()
    {
        await OnWindow(window =>
        {
            try
            {
                Field(window, "_settings").SetValue(window, new GlideSettingsState());
                window.Show();
                Invoke(window, "UpdateGifStatusUi");
                var group = window.FindControl<StackPanel>("GifGroup")!;
                var play = window.FindControl<Button>("GifPlayPauseButton")!;
                Assert.True(group.IsVisible);
                Assert.False(play.IsEnabled);

                ((GlideSettingsState)Field(window, "_settings").GetValue(window)!).StatusGifControlsOnlyOnGifs = true;
                Invoke(window, "UpdateGifStatusUi");
                Assert.False(group.IsVisible);
            }
            finally { }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Side_tab_position_moves_the_strip_into_the_side_panel_and_back()
    {
        await OnWindow(window =>
        {
            try
            {
                var settings = new GlideSettingsState { TabStripPosition = "Side" };
                Field(window, "_settings").SetValue(window, settings);
                window.Width = 1000; window.Height = 700;
                window.Show();
                Invoke(window, "ApplyChromeLayoutForWindowState");
                Dispatcher.UIThread.RunJobs();
                var side = window.FindControl<Border>("SideTabHost")!;
                var host = window.FindControl<Grid>("TabOverflowHost")!;
                var strip = window.FindControl<StackPanel>("TabStripPanel")!;
                Assert.Same(side, host.Parent);
                Assert.True(side.IsVisible);
                Assert.Equal(Avalonia.Layout.Orientation.Vertical, strip.Orientation);
                Assert.Equal(MainWindow.SideTabPanelWidth, window.FindControl<Grid>("WorkspaceLayer")!.Margin.Left);

                // Opening the window may adopt a fresh settings object: always edit the live one.
                settings = (GlideSettingsState)Field(window, "_settings").GetValue(window)!;
                settings.TabSideAutoHide = true;
                Invoke(window, "ApplyChromeLayoutForWindowState");
                Assert.False(side.IsVisible);
                Assert.Equal(0, window.FindControl<Grid>("WorkspaceLayer")!.Margin.Left);

                // Full-width "New tab" row and a resizable pane (clamped, remembered in settings state).
                settings.TabSideAutoHide = false;
                Invoke(window, "ApplyChromeLayoutForWindowState");
                var newTab = window.FindControl<Button>("NewTabButton")!;
                Assert.True(window.FindControl<TextBlock>("NewTabLabel")!.IsVisible);
                Assert.Equal(MainWindow.SideTabPanelWidth - 14, newTab.Width);
                Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, newTab.HorizontalContentAlignment);
                Assert.True(window.FindControl<Border>("SideTabResizeHandle")!.IsVisible);
                window.SetSideTabPaneWidth(300, save: false);
                Assert.Equal(300, settings.TabSidePanelWidth);
                Assert.Equal(300, side.Width);
                Assert.Equal(300 - 14, newTab.Width);
                window.SetSideTabPaneWidth(5000, save: false);
                Assert.Equal((int)MainWindow.SideTabPanelMaxWidth, settings.TabSidePanelWidth);
                window.SetSideTabPaneWidth(10, save: false);
                Assert.Equal((int)MainWindow.SideTabPanelMinWidth, settings.TabSidePanelWidth);
                window.SetSideTabPaneWidth(MainWindow.SideTabPanelWidth, save: false);
                Assert.Null(window.FindControl<Button>("TabPositionButton"));

                settings.TabStripPosition = "Top";
                Invoke(window, "ApplyChromeLayoutForWindowState");
                Assert.Same(window.FindControl<Grid>("ChromeLayout"), host.Parent);
                Assert.Equal(Avalonia.Layout.Orientation.Horizontal, strip.Orientation);
                Assert.False(side.IsVisible);
                Assert.False(window.FindControl<TextBlock>("NewTabLabel")!.IsVisible);
                Assert.Equal(36, window.FindControl<Button>("NewTabButton")!.Width);

                // The placement switch lives in the context menus, tagged with the bindable setting action.
                var item = (MenuItem)window.GetType().GetMethod("BuildTabPlacementMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [])!;
                Assert.StartsWith("Place tabs vertically", item.Header?.ToString());
                Assert.Equal("setting.tabs.position", Glide.App.Services.MenuActions.GetActionId(item));
                window.ToggleTabPlacement();
                item = (MenuItem)window.GetType().GetMethod("BuildTabPlacementMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [])!;
                Assert.StartsWith("Place tabs on top", item.Header?.ToString());
            }
            finally { }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Switching_tab_orientation_relays_the_tabs_out_like_a_fresh_layout()
    {
        await OnWindow(window =>
        {
            window.Width = 1200; window.Height = 700;
            window.Show();
            var settings = (GlideSettingsState)Field(window, "_settings").GetValue(window)!;
            var workspace = Field(window, "_workspace").GetValue(window)!;
            for (var i = 0; i < 3; i++) workspace.GetType().GetMethod("AddHome")!.Invoke(workspace, []);
            void Settle()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            double[] TabWidths() => window.FindControl<StackPanel>("TabStripPanel")!.Children.OfType<Border>().Select(b => b.Width).ToArray();
            // A fresh, settled layout: rebuild twice so the first pass's measurement is not the baseline.
            InvokeArgs(window, "RebuildTabStrip");
            Settle();
            InvokeArgs(window, "RebuildTabStrip");
            Settle();
            var topFresh = TabWidths();
            Assert.True(topFresh.Length >= 3);

            window.ToggleTabPlacement();          // top -> side
            Settle();
            Assert.Equal("Side", settings.TabStripPosition);
            Assert.All(TabWidths(), w => Assert.Equal(window.SideTabPaneWidth - 14, w));

            window.ToggleTabPlacement();          // side -> top
            Settle();
            var afterSwitch = TabWidths();
            InvokeArgs(window, "RebuildTabStrip");
            Settle();
            Assert.Equal(TabWidths(), afterSwitch);
            Assert.Equal(topFresh, afterSwitch);
            return Task.CompletedTask;
        });
    }

    private static async Task OnWindow(Func<MainWindow, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try { await body(window); }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static void InvokeArgs(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(target, []);

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, name == "ApplyChromeLayoutForWindowState" ? [null] : []);

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glide.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }
}
