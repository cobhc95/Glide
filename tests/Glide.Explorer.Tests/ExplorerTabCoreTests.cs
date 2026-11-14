using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// The Explorer tab driven like a user would: real MainWindow, real pointer and keyboard input through
/// the headless platform, over generated temp folders. (Owner report: the tab looked like a still image.)
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
public sealed class ExplorerTabCoreTests
{
    public ExplorerTabCoreTests() { }

    // ---- hit-testing / regression for "looks like a still image" -----------------------------------

    [Fact]
    public async Task Explorer_host_is_hit_testable_and_pointer_input_reaches_the_tiles()
    {
        await ExplorerHarness.Run(h =>
        {
            Assert.True(h.BrowserView.IsVisible);
            // REGRESSION: BrowserView used to be declared IsHitTestVisible=False and never switched back on, so no
            // pointer input could reach the Explorer (the owner's "still image" symptom).
            Assert.True(h.BrowserView.IsHitTestVisible, "BrowserView host must be hit-test visible while an Explorer tab is active");
            Assert.True(h.BrowserView.IsEffectivelyEnabled);
            foreach (var name in h.Names)
            {
                var hit = h.Window.InputHitTest(h.TileCenter(name));
                Assert.NotNull(hit);
                Assert.True(((Visual)hit!).GetSelfAndVisualAncestors().Contains(h.Surface), $"hit test at tile '{name}' landed on {hit}, outside BrowserSurface");
            }
            // The toolbar buttons and the address bar are reachable too.
            foreach (var control in new Control[] { h.Surface.UpButton, h.Surface.RefreshButton, h.Surface.ViewModeCombo, h.Surface.FilterBox })
            {
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), h.Window)!.Value;
                var hit = h.Window.InputHitTest(point);
                Assert.True(hit is Visual v && v.GetSelfAndVisualAncestors().Contains(control), $"{control.GetType().Name} is not reachable (hit {hit})");
            }
            // Nothing sits above the Explorer: the point is owned by the surface, not by an overlay.
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_hit_test_invisible_host_blocks_input_this_is_what_the_regression_guard_catches()
    {
        await ExplorerHarness.Run(h =>
        {
            var point = h.TileCenter("Alpha");
            h.BrowserView.IsHitTestVisible = false;
            h.Pump();
            var hit = h.Window.InputHitTest(point);
            Assert.False(hit is Visual v && v.GetSelfAndVisualAncestors().Contains(h.Surface), "with the host hit-test invisible the tile must be unreachable");
            h.Click("Alpha");
            Assert.Empty(h.SelectedNames); // clicking does nothing: the exact reported symptom
            h.BrowserView.IsHitTestVisible = true;
            h.Pump();
            h.Click("Beta"); // (a different tile: a second click on the same spot would count as a double-click)
            Assert.Equal(new[] { "Beta" }, h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Lists_folders_and_supported_images_only_and_hides_hidden_and_system_files()
    {
        await ExplorerHarness.Run(h =>
        {
            // notes.txt (non-image), secret.png (hidden) and desktop.ini (hidden+system) are not listed.
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "a.png", "b.png", "c.png" }, h.Names);
            Assert.Equal("6 items", h.Surface.ItemCountText.Text);
            Assert.Equal("", h.Surface.SelectionText.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Settings_can_show_all_files_and_hidden_files()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.ExplorerShowOnlyImages = false;
            h.Settings.ExplorerShowHiddenFiles = false;
            h.Key(Key.F5);
            await h.SettleAsync();
            Assert.Contains("notes.txt", h.Names);
            Assert.DoesNotContain("secret.png", h.Names);
            Assert.DoesNotContain("desktop.ini", h.Names);
            h.Settings.ExplorerShowHiddenFiles = true;
            h.Surface.RefreshButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await h.SettleAsync();
            Assert.Contains("secret.png", h.Names);
            Assert.Contains("desktop.ini", h.Names);
        });
    }

    // ---- selection ----------------------------------------------------------------------------------

    [Fact]
    public async Task Click_selects_ctrl_toggles_shift_selects_a_range_and_empty_space_clears()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("Beta");
            Assert.Equal(new[] { "Beta" }, h.SelectedNames);
            Assert.Equal("1 item selected", h.Surface.SelectionText.Text);
            h.Click("a.png", modifiers: RawInputModifiers.Control);
            Assert.Equal(new[] { "a.png", "Beta" }, h.SelectedNames);
            h.Click("Beta", modifiers: RawInputModifiers.Control);
            Assert.Equal(new[] { "a.png" }, h.SelectedNames);
            h.Click("Alpha");
            h.Click("a.png", modifiers: RawInputModifiers.Shift);
            Assert.Equal(new[] { "a.png", "Alpha", "Beta", "Gamma" }, h.SelectedNames);
            Assert.Equal("4 items selected", h.Surface.SelectionText.Text);
            h.Click(h.EmptySpotInList());
            Assert.Empty(h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selection_hover_and_keyboard_focus_are_visible_style_states()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("b.png");
            var tile = h.Tile("b.png")!;
            Assert.Contains("selected", tile.Classes);
            Assert.Contains("focused", tile.Classes); // keyboard focus ring: the list has focus after a click
            Assert.DoesNotContain("selected", h.Tile("a.png")!.Classes);
            h.Window.MouseMove(h.TileCenter("c.png"));
            h.Pump();
            Assert.True(h.Tile("c.png")!.IsPointerOver, "hover state");
            var fill = h.Tile("b.png")!.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("exFill"));
            Assert.True(fill.Opacity > 0.2, $"selected tile must be tinted, opacity={fill.Opacity}");
            var idleFill = h.Tile("a.png")!.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("exFill"));
            Assert.True(idleFill.Opacity < 0.02, $"an idle tile must not be tinted, opacity={idleFill.Opacity}");
            var hoverFill = h.Tile("c.png")!.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("exFill"));
            Assert.True(hoverFill.Opacity > 0.05, $"hovered tile must be tinted, opacity={hoverFill.Opacity}");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Ctrl_A_selects_everything_and_arrow_keys_home_end_move_the_selection()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Key(Key.A, RawInputModifiers.Control);
            Assert.Equal(6, h.SelectionSet.Count);
            h.Click("Alpha");
            h.Key(Key.Right);
            Assert.Equal(new[] { "Beta" }, h.SelectedNames);
            h.Key(Key.Right, RawInputModifiers.Shift);
            Assert.Equal(new[] { "Beta", "Gamma" }, h.SelectedNames);
            h.Key(Key.End);
            Assert.Equal(new[] { "c.png" }, h.SelectedNames);
            h.Key(Key.Home);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);
            h.Key(Key.Left);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames); // clamped at the start
            h.Key(Key.Down); // one row down: five columns at this width -> index 5
            Assert.Equal(new[] { "c.png" }, h.SelectedNames);
            h.Key(Key.Up);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);
            h.Key(Key.PageDown);
            Assert.Equal(new[] { "c.png" }, h.SelectedNames);
            h.Key(Key.PageUp);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Ctrl_arrow_moves_focus_only_and_ctrl_space_toggles()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("Alpha");
            h.Key(Key.Right, RawInputModifiers.Control);
            h.Key(Key.Right, RawInputModifiers.Control);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);
            Assert.Equal(2, h.CurrentIndex);
            h.Key(Key.Space, RawInputModifiers.Control);
            Assert.Equal(new[] { "Alpha", "Gamma" }, h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Typing_letters_jumps_to_the_matching_item()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Type("g");
            Assert.Equal(new[] { "Gamma" }, h.SelectedNames);
            h.Type("b"); // buffer "gb" matches nothing: selection stays
            Assert.Equal(new[] { "Gamma" }, h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Rubber_band_drag_on_empty_space_selects_the_tiles_it_touches()
    {
        await ExplorerHarness.Run(h =>
        {
            // Press in the empty area right of c.png (second row), drag up-left across Beta/Gamma/a.png in row one.
            var scroll = h.Surface.ScrollViewer;
            var start = scroll.TranslatePoint(new Point(scroll.Bounds.Width - 20, 300), h.Window)!.Value;
            var betaCenter = h.TileCenter("Beta");
            var end = new Point(betaCenter.X, betaCenter.Y - 5);
            h.Window.MouseMove(start);
            h.Window.MouseDown(start, MouseButton.Left);
            h.Window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.LeftMouseButton);
            h.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            h.Pump();
            Assert.True(h.Surface.RubberRect.IsVisible, "rubber band rectangle is drawn while dragging");
            h.Window.MouseUp(end, MouseButton.Left);
            h.Pump();
            Assert.False(h.Surface.RubberRect.IsVisible);
            // The row-one tiles right of Beta (Gamma, a.png, b.png) are inside the band: it spans x from Beta's centre to the right edge.
            Assert.Contains("Gamma", h.SelectedNames);
            Assert.Contains("a.png", h.SelectedNames);
            Assert.DoesNotContain("Alpha", h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    // ---- navigation ---------------------------------------------------------------------------------

    [Fact]
    public async Task Double_click_on_a_folder_opens_it_and_updates_address_history_and_buttons()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.False(h.Surface.BackButton.IsEnabled);
            Assert.True(h.Surface.UpButton.IsEnabled);
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.Surface.AddressBar.Path);
            Assert.Equal(new[] { "Deep", "inner.png" }, h.Names);
            Assert.True(h.Surface.BackButton.IsEnabled);
            Assert.False(h.Surface.ForwardButton.IsEnabled);
            // One browser-style history per tab (5.0.0): the folders are its Explorer entries.
            var histories = h.Get<Dictionary<Guid, Glide.Core.Navigation.NavigationHistory>>("_tabHistories");
            var history = histories[h.Workspace.Active!.Id];
            Assert.Equal(new[] { h.Root, Path.Combine(h.Root, "Alpha") }, history.Entries.Select(e => e.Path));
            // The breadcrumb ends in the opened folder.
            Assert.Equal("Alpha", h.Surface.AddressBar.Segments.Last().Content);
        });
    }

    [Fact]
    public async Task Back_Forward_Up_Refresh_buttons_work()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.DoubleClick("Deep");
            await h.SettleAsync();
            Assert.Equal(Path.Combine(alpha, "Deep"), h.CurrentFolder);

            h.ClickButton(h.Surface.BackButton);
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.True(h.Surface.ForwardButton.IsEnabled);
            h.ClickButton(h.Surface.ForwardButton);
            await h.SettleAsync();
            Assert.Equal(Path.Combine(alpha, "Deep"), h.CurrentFolder);

            h.ClickButton(h.Surface.UpButton);
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            // Going up highlights the folder we came from.
            Assert.Equal(new[] { "Deep" }, h.SelectedNames);

            File.WriteAllBytes(Path.Combine(alpha, "new.png"), ExplorerHarness.Png(8, 8, 9, 9, 9));
            h.ClickButton(h.Surface.RefreshButton);
            await h.SettleAsync();
            Assert.Contains("new.png", h.Names);
            // Refresh keeps the selection.
            Assert.Equal(new[] { "Deep" }, h.SelectedNames);
        });
    }

    [Fact]
    public async Task Keyboard_navigation_Backspace_Alt_arrows_and_F5()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            h.Click("Alpha");
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);

            // 5.0.0: Backspace is Back (Windows 11 File Explorer); Up stays on Alt+Up.
            h.Key(Key.Back);
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);          // the folder we came from is selected again

            h.Key(Key.Right, RawInputModifiers.Alt); // forward: to Alpha
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            h.Key(Key.Left, RawInputModifiers.Alt); // back: to root
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            h.Key(Key.Back, RawInputModifiers.Shift); // Shift+Backspace = forward: to Alpha
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            h.Key(Key.Up, RawInputModifiers.Alt); // up from Alpha
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);

            File.WriteAllBytes(Path.Combine(h.Root, "fresh.png"), ExplorerHarness.Png(8, 8, 1, 1, 1));
            h.Key(Key.F5);
            await h.SettleAsync();
            Assert.Contains("fresh.png", h.Names);
        });
    }

    [Fact]
    public async Task History_dropdown_lists_recent_locations_and_jumps()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.DoubleClick("Deep");
            await h.SettleAsync();
            h.ClickButton(h.Surface.HistoryButton);
            var menu = h.ActiveMenu;
            Assert.NotNull(menu);
            Assert.True(menu!.IsOpen);
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal(new[] { "Deep  ✓", "Alpha", Path.GetFileName(h.Root) }, items.Select(i => i.Header!.ToString()).ToArray());
            items[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.True(h.Surface.ForwardButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Breadcrumb_segment_click_navigates_and_clicking_empty_part_edits_the_path()
    {
        await ExplorerHarness.Run(async h =>
        {
            var deep = Path.Combine(h.Root, "Alpha", "Deep");
            await h.NavigateAsync(deep);
            var bar = h.Surface.AddressBar;
            var segments = bar.Segments;
            Assert.Equal("Deep", segments.Last().Content);
            // Click the "Alpha" segment.
            var alpha = segments.First(s => (string?)s.Content == "Alpha");
            h.ClickButton(alpha);
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);
            Assert.False(bar.IsEditing);

            // Click the empty part right of a short breadcrumb: the path becomes editable text.
            bar.Path = @"T:\Photos";
            h.Pump();
            h.Window.UpdateLayout();
            var point = bar.TranslatePoint(new Point(bar.Bounds.Width - 12, bar.Bounds.Height / 2), h.Window)!.Value;
            h.Click(point);
            Assert.True(bar.IsEditing);
            Assert.Equal(@"T:\Photos", bar.EditBox.Text);
        });
    }

    [Fact]
    public async Task Typing_a_path_and_Enter_navigates_invalid_paths_show_an_inline_error_and_Esc_cancels()
    {
        await ExplorerHarness.Run(async h =>
        {
            var bar = h.Surface.AddressBar;
            var beta = Path.Combine(h.Root, "Beta");
            bar.BeginEdit();
            h.Pump();
            bar.EditBox.Text = beta;
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.Equal(beta, h.CurrentFolder);
            Assert.False(bar.IsEditing);

            // Invalid path: error shown inline, edit box stays open, the folder is unchanged.
            bar.BeginEdit();
            h.Pump();
            bar.EditBox.Text = Path.Combine(h.Root, "no such folder");
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.True(bar.IsEditing);
            Assert.Contains("Can't find", bar.ErrorText);
            Assert.Equal(beta, h.CurrentFolder);
            // Editing the text clears the error; Esc cancels back to the breadcrumb with the real path.
            bar.EditBox.Text = "x";
            h.Pump();
            Assert.Null(bar.ErrorText);
            h.Key(Key.Escape);
            Assert.False(bar.IsEditing);
            Assert.Equal(beta, bar.Path);

            // A file path navigates to its folder and selects the file.
            bar.BeginEdit();
            h.Pump();
            bar.EditBox.Text = Path.Combine(h.Root, "b.png");
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);
        });
    }

    [Fact]
    public async Task Ctrl_L_and_F4_focus_the_address_bar()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Key(Key.L, RawInputModifiers.Control);
            Assert.True(h.Surface.AddressBar.IsEditing);
            h.Key(Key.Escape);
            Assert.False(h.Surface.AddressBar.IsEditing);
            h.FocusList();
            h.Key(Key.F4);
            Assert.True(h.Surface.AddressBar.IsEditing);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Navigation_pane_lists_quick_access_and_drives_and_clicking_navigates()
    {
        await ExplorerHarness.Run(async h =>
        {
            var buttons = h.Surface.NavHost.Children.OfType<Button>().ToList();
            Assert.Equal(new[] { "Desktop", "Pictures", "Test drive (T:)" },
                buttons.Select(b => ((TextBlock)((StackPanel)b.Content!).Children[1]).Text).ToArray());
            var headers = h.Surface.NavHost.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Equal(new[] { "Quick access", "This PC" }, headers);
            h.ClickButton(buttons[1]);
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Beta"), h.CurrentFolder);
            Assert.Contains("current", buttons[1].Classes);
            Assert.DoesNotContain("current", buttons[0].Classes);
        });
    }

    [Fact]
    public async Task Status_line_shows_item_count_and_selection()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.Equal("6 items", h.Surface.ItemCountText.Text);
            h.Click("a.png");
            Assert.Equal("1 item selected   135 B".Replace("135", new FileInfo(Path.Combine(h.Root, "a.png")).Length.ToString()), h.Surface.SelectionText.Text);
            await h.NavigateAsync(Path.Combine(h.Root, "Beta"));
            Assert.Equal("0 items", h.Surface.ItemCountText.Text);
            Assert.True(h.Surface.EmptyText.IsVisible);
            await h.NavigateAsync(Path.Combine(h.Root, "Alpha", "Deep"));
            Assert.Equal("1 item", h.Surface.ItemCountText.Text);
        });
    }

    // ---- opening (gesture bindings) -----------------------------------------------------------------

    [Fact]
    public async Task Enter_and_double_click_open_an_image_in_the_current_tab_by_default()
    {
        await ExplorerHarness.Run(async h =>
        {
            var tabId = h.Workspace.Active!.Id;
            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in current tab");
            Assert.Equal(tabId, h.Workspace.Active!.Id); // same tab, now an image
            Assert.Equal(Path.Combine(h.Root, "a.png"), ((ImageTabState)h.Workspace.Active!).Path);
            Assert.Equal(2, h.Workspace.Tabs.Count); // Home + this tab
        });
    }

    [Fact]
    public async Task Enter_opens_the_selected_image_in_the_current_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("c.png");
            h.Key(Key.Enter);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened by Enter");
            Assert.Equal(Path.Combine(h.Root, "c.png"), ((ImageTabState)h.Workspace.Active!).Path);
        });
    }

    [Fact]
    public async Task Middle_click_opens_an_image_in_a_new_background_tab_by_default()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Active!.Id;
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new image tab");
            Assert.Equal(explorerId, h.Workspace.Active!.Id); // the Explorer stays in front
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(Path.Combine(h.Root, "b.png"), h.Workspace.Tabs.OfType<ImageTabState>().Single().Path);
        });
    }

    [Fact]
    public async Task Swapped_Explorer_gesture_bindings_swap_double_click_and_middle_click()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.Gestures["explorer.doubleClick"] = GestureCatalog.OpenInNewTab;
            h.Settings.Gestures["explorer.middleClick"] = GestureCatalog.OpenInCurrentTab;
            var explorerId = h.Workspace.Active!.Id;
            // Double-click now opens in a NEW tab: the new image tab is added (and activated by OpenAsImageTabAsync).
            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "new tab for double-click");
            Assert.Contains(h.Workspace.Tabs, t => t.Id == explorerId && t is BrowserTabState); // the Explorer tab still exists
            Assert.Equal(Path.Combine(h.Root, "a.png"), h.Workspace.Tabs.OfType<ImageTabState>().Single().Path);
        });
    }

    [Fact]
    public async Task Swapped_bindings_middle_click_opens_in_the_current_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.Gestures["explorer.doubleClick"] = GestureCatalog.OpenInNewTab;
            h.Settings.Gestures["explorer.middleClick"] = GestureCatalog.OpenInCurrentTab;
            var explorerId = h.Workspace.Active!.Id;
            h.Click("c.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "middle-click opened in current tab");
            Assert.Equal(explorerId, h.Workspace.Active!.Id);
        });
    }

    [Fact]
    public async Task Middle_click_on_a_folder_opens_a_new_Explorer_tab_and_keeps_the_current_one()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Active!.Id;
            h.Click("Gamma", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<BrowserTabState>().Count() == 2, "new explorer tab");
            Assert.Equal(explorerId, h.Workspace.Active!.Id);
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(Path.Combine(h.Root, "Gamma"), h.Workspace.Tabs.OfType<BrowserTabState>().Last().Folder);
        });
    }

    // ---- tab switching and sessions -----------------------------------------------------------------

    [Fact]
    public async Task Switching_to_another_tab_and_back_keeps_selection_scroll_view_and_sort()
    {
        await ExplorerHarness.Run(async h =>
        {
            // A long folder so there is something to scroll.
            var folder = Path.Combine(h.Root, "Sub");
            Directory.CreateDirectory(folder);
            var png = ExplorerHarness.Png(8, 8, 5, 5, 5);
            for (var i = 0; i < 200; i++) File.WriteAllBytes(Path.Combine(folder, $"p{i:D3}.png"), png);
            await h.NavigateAsync(folder);
            h.Call("SelectBrowserIndex", 40, false, false, true); // selects p040 and scrolls it into view
            h.Pump();
            Assert.True(h.Surface.ScrollViewer.Offset.Y > 0, "selecting an item far down scrolls it into view");
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Medium;
            await h.SettleAsync();
            h.Surface.ScrollViewer.Offset = new Vector(0, 300);
            h.Pump();
            var offsetBefore = h.Surface.ScrollViewer.Offset.Y;
            Assert.True(offsetBefore > 0);
            var explorerIndex = h.Workspace.ActiveIndex;

            // Away to the Home tab and back.
            h.Workspace.Select(h.Workspace.Tabs[0].Id);
            h.Call("CaptureActiveTabRuntimeState");
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.False(h.BrowserView.IsVisible);
            h.Workspace.Select(h.Workspace.Tabs[explorerIndex].Id);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();

            Assert.True(h.BrowserView.IsVisible);
            Assert.Contains("p040.png", h.SelectedNames);
            Assert.Equal(ExplorerViewModes.Medium, h.Surface.ViewModeCombo.SelectedIndex);
            Assert.True(Math.Abs(h.Surface.ScrollViewer.Offset.Y - offsetBefore) < 2, $"scroll {h.Surface.ScrollViewer.Offset.Y} vs {offsetBefore}");
            Assert.Equal(folder, h.CurrentFolder);
        });
    }

    [Fact]
    public async Task Two_explorer_tabs_keep_independent_state()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Beta");
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Details;
            await h.SettleAsync();
            var first = h.Workspace.Active!.Id;
            h.Call("CaptureActiveTabRuntimeState");

            await h.OpenExplorerAsync(Path.Combine(h.Root, "Alpha"));
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);
            Assert.Empty(h.SelectedNames);
            Assert.Equal(ExplorerViewModes.Large, h.Surface.ViewModeCombo.SelectedIndex); // a new tab starts from the default view
            h.Surface.ViewModeCombo.SelectedIndex = ExplorerViewModes.Small;
            await h.SettleAsync();
            h.Call("CaptureActiveTabRuntimeState");

            h.Workspace.Select(first);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(new[] { "Beta" }, h.SelectedNames);
            Assert.Equal(ExplorerViewModes.Details, h.Surface.ViewModeCombo.SelectedIndex);
        });
    }

    [Fact]
    public async Task Explorer_tab_with_its_history_round_trips_through_the_session_file()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.DoubleClick("Deep");
            await h.SettleAsync();
            h.ClickButton(h.Surface.BackButton);
            await h.SettleAsync();
            h.Call("CaptureActiveTabRuntimeState");
            WorkspaceSessionStore.Save(h.Workspace.Tabs, h.Workspace.ActiveIndex);
            var loaded = WorkspaceSessionStore.Load();
            Assert.NotNull(loaded);
            var browser = Assert.IsType<BrowserTabState>(loaded!.Value.Tabs[loaded.Value.ActiveIndex]);
            Assert.Equal(Path.Combine(h.Root, "Alpha"), browser.Folder);
            Assert.NotNull(browser.Navigation);
            Assert.Equal(new[] { h.Root, Path.Combine(h.Root, "Alpha"), Path.Combine(h.Root, "Alpha", "Deep") }, browser.Navigation!.History.ToArray());
            Assert.Equal(1, browser.Navigation.Index);
        });
    }

    [Fact]
    public async Task Restored_session_activates_an_explorer_tab_with_its_history_and_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            var restored = new BrowserTabState(Guid.NewGuid(), alpha)
            {
                Navigation = new BrowserNavigationState(new[] { h.Root, alpha }, 1, new string?[] { Path.Combine(h.Root, "Alpha"), null })
            };
            h.Workspace.Reset(new TabState[] { new HomeTabState(Guid.NewGuid()), restored }, 1);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal(new[] { "Deep", "inner.png" }, h.Names);
            Assert.True(h.Surface.BackButton.IsEnabled, "restored history allows Back");
            h.ClickButton(h.Surface.BackButton);
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
        }, startExplorer: false);
    }

    [Fact]
    public async Task Activating_the_explorer_tab_puts_keyboard_focus_on_the_file_list()
    {
        await ExplorerHarness.Run(async h =>
        {
            await h.WaitUntil(() => h.Surface.ScrollViewer.IsFocused, "list focused after activation");
            h.Key(Key.End);
            Assert.Equal(new[] { "c.png" }, h.SelectedNames); // keys work immediately, without clicking first
        });
    }

    [Fact]
    public async Task Typing_in_the_filter_box_does_not_trigger_viewer_hotkeys()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Surface.FilterBox.Focus();
            h.Pump();
            // F = fullscreen, R = rotate, Delete = delete file, Home/End = first/last image in the viewer.
            foreach (var (key, text) in new[] { (Key.F, "f"), (Key.R, "r"), (Key.H, "h") })
            {
                h.Window.KeyPress(key, RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, text);
                h.Window.KeyTextInput(text);
                h.Window.KeyRelease(key, RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, text);
            }
            h.Pump();
            Assert.Equal(WindowState.Normal, h.Window.WindowState);
            await h.WaitUntil(() => h.Surface.FilterBox.Text == "frh", "text typed");
            Assert.Equal(0, h.Names.Count); // nothing contains "frh": the filter works, nothing else happened
        });
    }

    [Fact]
    public async Task Explorer_shortcuts_do_not_leak_to_viewer_hotkeys()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Alpha");
            // Enter is fullscreen in the viewer; in the Explorer it opens the folder.
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);
            Assert.Equal(WindowState.Normal, h.Window.WindowState);
            // Ctrl+Shift+N is "duplicate window" in the viewer; here it makes a folder.
            h.Key(Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.Contains("New folder", h.Names);
            h.Key(Key.Escape);
        });
    }
}
