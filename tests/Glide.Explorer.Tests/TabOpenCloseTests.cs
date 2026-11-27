using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Glide.App.Explorer;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.0.6 tester items 9 and 10, driven through the real MainWindow:
///  9. where a tab opened "in a new tab" goes and whether Glide switches to it (settings + opt-in Shift / Alt modifiers);
/// 10. which tab becomes active after the active tab is closed (previously used tab when Ctrl+Tab is MRU).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class TabOpenCloseTests
{
    private static string Img(ExplorerHarness h, string name) => Path.Combine(h.Root, name);

    /// <summary>These tests drive the general new-tab rules through a middle-click (round 4 D1: middle-click follows them).</summary>
    private static void MiddleClick(ExplorerHarness h, string name, RawInputModifiers modifiers = RawInputModifiers.None) =>
        h.Click(name, MouseButton.Middle, modifiers);

    /// <summary>Adds a Home tab to the right of the Explorer tab without activating it: [Home, Explorer, Home].</summary>
    private static Guid AddHomeToTheRight(ExplorerHarness h)
    {
        var home = h.Workspace.InsertTransferred(new HomeTabState(Guid.NewGuid()), h.Workspace.Tabs.Count, activate: false);
        h.Call("RebuildTabStrip");
        h.Pump();
        return home.Id;
    }

    private static int IndexOf(ExplorerHarness h, Guid id) => h.Workspace.Tabs.ToList().FindIndex(t => t.Id == id);

    private static ImageTabState ImageTab(ExplorerHarness h, string name) =>
        h.Workspace.Tabs.OfType<ImageTabState>().Single(t => string.Equals(t.Path, Img(h, name), StringComparison.OrdinalIgnoreCase));

    /// <summary>Clicks a tab in the strip with real pointer input (a click on a tab activates it).</summary>
    private static async Task ClickTabAsync(ExplorerHarness h, Guid id)
    {
        var strip = h.Get<Panel>("TabStripPanel");
        var tab = strip.Children.OfType<Border>().Single(b => Equals(b.DataContext, id));
        var point = tab.TranslatePoint(new Point(tab.Bounds.Width / 3, tab.Bounds.Height / 2), h.Window)!.Value;
        h.Click(point);
        await h.WaitUntil(() => h.Workspace.Active?.Id == id, "clicked tab activated");
        await h.SettleAsync();
    }

    private static async Task CtrlWAsync(ExplorerHarness h)
    {
        var count = h.Workspace.Tabs.Count;
        h.Key(Key.W, RawInputModifiers.Control);
        h.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None);
        await h.WaitUntil(() => h.Workspace.Tabs.Count == count - 1, "Ctrl+W closed the tab");
        h.Pump();
    }

    // ---- item 9: placement and switching ------------------------------------------------------------

    [Fact]
    public async Task Background_middle_click_puts_the_new_tab_next_to_the_current_one_and_stays()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            Assert.Equal(explorer, h.Workspace.Active!.Id);
            Assert.Equal(IndexOf(h, explorer) + 1, IndexOf(h, ImageTab(h, "b.png").Id));
        });
    }

    [Fact]
    public async Task By_default_a_middle_click_switches_to_the_new_tab_next_to_the_current_one()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.True(h.Settings.SwitchToNewTabs); // 5.0.6: "Open new tabs in the foreground" is on by default
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "switched to the new tab");
            Assert.Equal(Img(h, "b.png"), ((ImageTabState)h.Workspace.Active!).Path);
            Assert.Equal(IndexOf(h, explorer) + 1, h.Workspace.ActiveIndex);
        });
    }

    [Fact]
    public async Task Shift_middle_click_switches_to_the_new_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png", RawInputModifiers.Shift);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "Shift+middle-click switched to the new tab");
            Assert.Equal(Img(h, "b.png"), ((ImageTabState)h.Workspace.Active!).Path);
            Assert.Equal(IndexOf(h, explorer) + 1, h.Workspace.ActiveIndex); // still next to the Explorer tab
        });
    }

    [Fact]
    public async Task With_no_keys_chosen_Shift_and_Alt_do_not_change_how_a_new_tab_opens()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            Assert.Equal((NewTabKeys.None, NewTabKeys.None), (h.Settings.NewTabPositionKey, h.Settings.NewTabForegroundKey)); // both None by default
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png", RawInputModifiers.Shift | RawInputModifiers.Alt);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            Assert.Equal(explorer, h.Workspace.Active!.Id);                                   // still in the background
            Assert.Equal(IndexOf(h, explorer) + 1, IndexOf(h, ImageTab(h, "b.png").Id));     // still next to the current tab
        });
    }

    [Fact]
    public async Task Ctrl_shift_middle_click_also_switches_to_the_new_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            MiddleClick(h, "c.png", RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "Ctrl+Shift+middle-click switched to the new tab");
            Assert.Equal(Img(h, "c.png"), ((ImageTabState)h.Workspace.Active!).Path);
        });
    }

    [Fact]
    public async Task Round3_18_the_chosen_keys_apply_whichever_keys_they_are()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false;
            h.Settings.NewTabForegroundKey = NewTabKeys.Ctrl; // Ctrl now brings the tab to the front ...
            h.Settings.NewTabPositionKey = NewTabKeys.None;
            var tabs = h.Workspace.Tabs.Count;
            MiddleClick(h, "b.png", RawInputModifiers.Shift); // ... and Shift (not chosen) does nothing
            await h.WaitUntil(() => h.Workspace.Tabs.Count == tabs + 1, "Shift+middle-click opened a tab");
            Assert.IsNotType<ImageTabState>(h.Workspace.Active);
            MiddleClick(h, "c.png", RawInputModifiers.Control);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "Ctrl+middle-click switched to the new tab");
            Assert.Equal(Img(h, "c.png"), ((ImageTabState)h.Workspace.Active!).Path);
        });
    }

    [Fact]
    public async Task Alt_middle_click_puts_the_new_tab_at_the_end()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png", RawInputModifiers.Alt);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            Assert.Equal(explorer, h.Workspace.Active!.Id);
            Assert.Equal(h.Workspace.Tabs.Count - 1, IndexOf(h, ImageTab(h, "b.png").Id));
        });
    }

    [Fact]
    public async Task Alt_middle_click_on_a_folder_puts_the_new_Explorer_tab_at_the_end()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            AddHomeToTheRight(h);
            MiddleClick(h, "Gamma", RawInputModifiers.Alt);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<BrowserTabState>().Count() == 2, "new Explorer tab");
            Assert.Equal(Path.Combine(h.Root, "Gamma"), ((BrowserTabState)h.Workspace.Tabs[^1]).Folder);
        });
    }

    [Fact]
    public async Task Shift_double_click_opens_in_a_new_tab_and_switches_when_double_click_opens_new_tabs()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            h.Settings.Gestures["explorer.doubleClick"] = GestureCatalog.OpenInNewTab;
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            var point = h.TileCenter("a.png");
            h.Window.MouseMove(point, RawInputModifiers.Shift);
            h.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.Shift);
            h.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.Shift);
            h.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.Shift);
            h.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.Shift);
            h.Pump();
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "Shift+double-click switched to the new tab");
            Assert.Contains(h.Workspace.Tabs, t => t.Id == explorer);
            Assert.Equal(IndexOf(h, explorer) + 1, h.Workspace.ActiveIndex);
        });
    }

    [Fact]
    public async Task Opening_several_selected_items_in_new_tabs_keeps_their_order()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            var entries = h.Entries.Where(e => e.Name is "a.png" or "b.png" or "c.png").OrderBy(e => e.Name).ToList();
            await (Task)h.Call("OpenExplorerEntriesInNewTabsAsync", (IReadOnlyList<ExplorerEntry>)entries)!;
            h.Pump();
            var start = IndexOf(h, explorer);
            Assert.Equal(Img(h, "a.png"), ((ImageTabState)h.Workspace.Tabs[start + 1]).Path);
            Assert.Equal(Img(h, "b.png"), ((ImageTabState)h.Workspace.Tabs[start + 2]).Path);
            Assert.Equal(Img(h, "c.png"), ((ImageTabState)h.Workspace.Tabs[start + 3]).Path);
            Assert.Equal(explorer, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task At_the_end_and_switch_settings_open_the_new_tab_at_the_end_in_front()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.NewTabPosition = NewTabPositions.AtEnd;
            h.Settings.SwitchToNewTabs = true;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "switched to the new tab");
            Assert.Equal(h.Workspace.Tabs.Count - 1, h.Workspace.ActiveIndex);
        });
    }

    [Fact]
    public async Task With_switch_on_Shift_keeps_the_new_tab_in_the_background_and_Alt_puts_it_next_to_the_current_one()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            h.Settings.NewTabPosition = NewTabPositions.AtEnd;
            h.Settings.SwitchToNewTabs = true;
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png", RawInputModifiers.Shift | RawInputModifiers.Alt);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            Assert.Equal(explorer, h.Workspace.Active!.Id);
            Assert.Equal(IndexOf(h, explorer) + 1, IndexOf(h, ImageTab(h, "b.png").Id));
        });
    }

    [Fact]
    public async Task Explorer_menu_Open_in_new_tab_reads_the_keys_held_when_it_is_clicked()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            AddHomeToTheRight(h);
            var entry = h.Entries.Single(e => e.Name == "c.png");
            var menu = (ContextMenu)h.Call("BuildExplorerContextMenu", entry)!;
            var item = ((IEnumerable<MenuItem>)menu.ItemsSource!).Single(i => (i.Header as string) == "Open in new tab (Shift: foreground; Alt: at end)");
            Glide.App.Platform.ModifierKeyState.OverrideForTests = () => KeyModifiers.Shift;
            try
            {
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "Shift+click on the menu item switched to the new tab");
            }
            finally { Glide.App.Platform.ModifierKeyState.OverrideForTests = null; }
            Assert.Equal(Img(h, "c.png"), ((ImageTabState)h.Workspace.Active!).Path);
        });
    }

    // ---- item 10: the tab activated after closing ---------------------------------------------------

    [Fact]
    public async Task Previously_used_tab_setting_applies_even_with_tab_order_cycling()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // background, explicitly (the default is foreground since 5.0.6)
            h.Settings.AfterTabCloseActivate = TabCloseActivation.PreviouslyUsed;
            h.Settings.NewTabPositionKey = NewTabKeys.Alt; h.Settings.NewTabForegroundKey = NewTabKeys.Shift; // the 5.0.6 Shift / Alt scheme
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png", RawInputModifiers.Shift);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "switched to the new tab");
            await h.SettleAsync();
            await CtrlWAsync(h);
            Assert.Equal(explorer, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task Tab_to_the_left_setting_activates_the_left_neighbour()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.TabCycleOrder = "Most recently used"; // an explicit choice wins over "Match Ctrl+Tab order"
            h.Settings.AfterTabCloseActivate = TabCloseActivation.Left;
            // [Home, Explorer, Home2]: the previously used tab (Home2) and the right neighbour (Home2) are not the left one.
            var leftHome = h.Workspace.Tabs[0].Id;
            var explorer = h.Workspace.Active!.Id;
            var rightHome = AddHomeToTheRight(h);
            await ClickTabAsync(h, rightHome);
            await ClickTabAsync(h, explorer);
            await CtrlWAsync(h);
            Assert.Equal(leftHome, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task Mru_cycle_closing_a_tab_opened_from_the_Explorer_returns_to_the_Explorer_not_the_neighbour()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // opened in the background, then switched to by a tab click
            // Tester: uitest -> open img05 in a new tab -> switch to it -> Ctrl+W landed on "Home", not uitest.
            h.Settings.TabCycleOrder = "Most recently used";
            var explorer = h.Workspace.Active!.Id;
            var home = AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            await ClickTabAsync(h, ImageTab(h, "b.png").Id);
            await CtrlWAsync(h);
            Assert.NotEqual(home, h.Workspace.Active!.Id);
            Assert.Equal(explorer, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task Match_Ctrl_Tab_order_with_tab_order_cycling_activates_the_tab_to_the_right()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // opened in the background, then switched to by a tab click
            h.Settings.AfterTabCloseActivate = TabCloseActivation.MatchCycleOrder; // no longer the default (5.0.6)
            var home = AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            await ClickTabAsync(h, ImageTab(h, "b.png").Id);
            await CtrlWAsync(h);
            Assert.Equal(home, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task By_default_closing_a_tab_returns_to_the_previously_used_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // opened in the background, then switched to by a tab click
            Assert.Equal(TabCloseActivation.PreviouslyUsed, h.Settings.AfterTabCloseActivate);
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            await ClickTabAsync(h, ImageTab(h, "b.png").Id);
            await CtrlWAsync(h);
            Assert.Equal(explorer, h.Workspace.Active!.Id); // not the Home tab on its right
        });
    }

    [Fact]
    public async Task By_default_closing_a_tab_opened_in_the_foreground_returns_to_the_Explorer()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.True(h.Settings.SwitchToNewTabs);
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "switched to the new tab");
            await h.SettleAsync();
            await CtrlWAsync(h);
            Assert.Equal(explorer, h.Workspace.Active!.Id); // the previously used tab, not the Home tab on its right
        });
    }

    [Fact]
    public async Task Mru_cycle_closing_a_reopened_tab_returns_to_the_previously_used_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = false; // opened in the background, then switched to by a tab click
            h.Settings.TabCycleOrder = "Most recently used";
            var explorer = h.Workspace.Active!.Id;
            AddHomeToTheRight(h);
            MiddleClick(h, "b.png");
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            await ClickTabAsync(h, ImageTab(h, "b.png").Id);
            var closedIndex = h.Workspace.ActiveIndex;
            await CtrlWAsync(h);
            Assert.Equal(explorer, h.Workspace.Active!.Id);
            // Reopen it (Ctrl+Shift+T restores it at its former position and switches to it), then close it again.
            h.Key(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "reopened tab active");
            await h.SettleAsync();
            Assert.Equal(closedIndex, h.Workspace.ActiveIndex);
            await CtrlWAsync(h);
            Assert.Equal(explorer, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task Mru_cycle_closing_a_picture_folder_tab_returns_to_the_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.TabCycleOrder = "Most recently used";
            AddHomeToTheRight(h);
            // Open b.png in the Explorer tab itself, then "open the picture's folder" in a new tab (opened at the end).
            h.DoubleClick("b.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in this tab");
            await h.SettleAsync();
            var picture = h.Workspace.Active!.Id;
            await (Task)h.Call("ShowPathInExplorerTabCoreAsync", Img(h, "b.png"), true)!;
            await h.SettleAsync();
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(h.Workspace.Tabs.Count - 1, h.Workspace.ActiveIndex);
            await CtrlWAsync(h);
            Assert.Equal(picture, h.Workspace.Active!.Id);
        });
    }
}
