using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Glide.App;
using Glide.App.Controls;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Browser-style Back / Forward through an Explorer tab (5.0.0, docs/NAVIGATION-HISTORY.md): an image opened from the
/// Explorer in the same tab is a history step, Back returns to that folder with the image selected and the list
/// scrolled as it was, Forward returns to the image. Real key presses go through the window's key handlers.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerNavigationHistoryTests
{
    private static async Task OpenImageFromExplorerAsync(ExplorerHarness h, string name, string? folder = null)
    {
        h.DoubleClick(name);
        var path = Path.Combine(folder ?? h.Root, name);
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
        await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), path, StringComparison.OrdinalIgnoreCase), "image loaded");
    }

    private static async Task WaitForExplorerAsync(ExplorerHarness h, string folder)
    {
        await h.WaitUntil(() => h.Workspace.Active is BrowserTabState b && string.Equals(b.Folder, folder, StringComparison.OrdinalIgnoreCase), "back in the Explorer at " + folder);
        await h.SettleAsync();
    }

    private static NavigationHistory History(ExplorerHarness h) =>
        h.Window.TabHistories.Find(h.Workspace.Active!.Id)!;

    private static Control ImageViewport(ExplorerHarness h) => h.Get<Control>("Viewport");

    private static bool RaiseSideButton(ExplorerHarness h, Visual target, bool back)
    {
        var props = new PointerPointProperties(back ? RawInputModifiers.XButton1MouseButton : RawInputModifiers.XButton2MouseButton,
            back ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed);
        var args = new PointerPressedEventArgs(target, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), h.Window,
            new Point(100, 100), 0, props, KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent };
        ((Interactive)target).RaiseEvent(args);
        return args.Handled;
    }

    [Fact]
    public async Task Back_from_an_image_opened_here_selects_it_with_the_same_folder_and_scroll_and_Forward_returns_to_it()
    {
        await ExplorerHarness.Run(async h =>
        {
            var tabId = h.Workspace.Active!.Id;
            h.Surface.ScrollViewer.Offset = new Vector(0, 400);
            await h.SettleAsync();
            var name = h.Names.First(n => n.StartsWith("img", StringComparison.Ordinal) && h.Tile(n) is { } t && t.TranslatePoint(new Point(0, 0), h.Surface.ScrollViewer)?.Y > 150);
            h.Click(name);
            Assert.Equal(new[] { name }, h.SelectedNames);
            var scrolled = h.Surface.ScrollViewer.Offset.Y;
            Assert.True(scrolled > 100);

            h.Key(Key.Enter);                                                  // open in the same tab
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
            Assert.Equal(tabId, h.Workspace.Active!.Id);
            Assert.True(History(h).CanGoBack);

            h.Key(Key.Left, RawInputModifiers.Alt);                            // Back
            await WaitForExplorerAsync(h, h.Root);
            Assert.Equal(tabId, h.Workspace.Active!.Id);
            Assert.Equal(new[] { name }, h.SelectedNames);
            await h.WaitUntil(() => Math.Abs(h.Surface.ScrollViewer.Offset.Y - scrolled) < 3, "scroll restored");
            Assert.True(h.Surface.ForwardButton.IsEnabled);

            h.Key(Key.Right, RawInputModifiers.Alt);                           // Forward
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState i && string.Equals(Path.GetFileName(i.Path), name, StringComparison.OrdinalIgnoreCase), "Forward returned to the image");
            Assert.Equal(tabId, h.Workspace.Active!.Id);
        }, populate: ExplorerHarness.ManyFilesTree);
    }

    [Fact]
    public async Task Back_after_arrow_navigation_selects_the_image_shown_now_and_bound_side_buttons_go_Back_and_Forward()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageFromExplorerAsync(h, ExplorerHarness.ImageA);
            await h.WaitUntil(() => h.Get<bool>("_folderIndexReady"), "folder indexed");
            h.Key(Key.Right);                                                  // Next image: not a history step
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), ExplorerHarness.ImageB, StringComparison.OrdinalIgnoreCase), "next image");
            Assert.Equal(2, History(h).Count);

            // Round 4 (A1/A2): on Default the mouse back button on a picture opened from the Explorer goes Back to it,
            // and the Explorer's forward button goes Forward.
            Assert.True(RaiseSideButton(h, ImageViewport(h), back: true));   // default mouse back button: Back
            await WaitForExplorerAsync(h, h.Root);
            Assert.Equal(new[] { ExplorerHarness.ImageB }, h.SelectedNames);  // the image on screen when Back was pressed

            Assert.True(RaiseSideButton(h, h.BrowserView, back: false));     // bound mouse forward button: Forward
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState i && Path.GetFileName(i.Path) == ExplorerHarness.ImageB, "Forward restored the image shown when Back was pressed");
        });
    }

    [Fact]
    public async Task Explorer_Back_Forward_buttons_history_dropdown_and_keys_step_across_the_image()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            await OpenImageFromExplorerAsync(h, "inner.png", alpha);
            Assert.Equal(new[] { NavigationEntryKind.Explorer, NavigationEntryKind.Explorer, NavigationEntryKind.Image }, History(h).Entries.Select(e => e.Kind));


            h.Key(Key.Left, RawInputModifiers.Alt);
            await WaitForExplorerAsync(h, alpha);
            Assert.Equal(new[] { "inner.png" }, h.SelectedNames);
            h.ClickButton(h.Surface.BackButton);                               // the Explorer toolbar Back: the previous folder
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);
            h.ClickButton(h.Surface.ForwardButton);
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);

            // The history dropdown lists the image too (full paths, most recently visited first: round 3 #13) and jumps there.
            var menu = h.Window.BuildExplorerHistoryMenu();
            var items = menu.ItemsSource!.Cast<MenuItem>().ToList();
            Assert.Equal(new[] { alpha + "  ✓", h.Root, Path.Combine(alpha, "inner.png") }, items.Select(i => i.Header?.ToString()));   // ✓ = current
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState i && Path.GetFileName(i.Path) == "inner.png", "dropdown opened the image");

            h.Key(Key.Left, RawInputModifiers.Alt);
            await WaitForExplorerAsync(h, alpha);
            h.FocusList();
            h.Key(Key.Back);                                                   // Explorer list: Backspace = Back
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            h.Key(Key.Back, RawInputModifiers.Shift);                          // Shift+Backspace = Forward
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            h.Key(Key.Up, RawInputModifiers.Alt);                              // Up stays Alt+Up (a new step: Forward is gone)
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.False(h.Surface.ForwardButton.IsEnabled);
        });
    }

    [Fact]
    public async Task Typing_in_the_filter_box_or_the_address_bar_never_goes_Back_or_Forward()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.ClickButton(h.Surface.BackButton);
            await h.SettleAsync();
            h.ClickButton(h.Surface.ForwardButton);
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.True(History(h).CanGoBack);

            h.Key(Key.F, RawInputModifiers.Control);                           // filter box
            Assert.True(h.Surface.FilterBox.IsFocused);
            foreach (var (key, mods) in new (Key, RawInputModifiers)[]
            {
                (Key.Back, RawInputModifiers.None), (Key.Back, RawInputModifiers.Shift),
                (Key.Left, RawInputModifiers.Alt), (Key.Right, RawInputModifiers.Alt)
            })
            {
                h.Key(key, mods);
                await h.SettleAsync();
                Assert.Equal(alpha, h.CurrentFolder);
            }

            h.Key(Key.L, RawInputModifiers.Control);                           // address bar edit
            Assert.True(h.Surface.AddressBar.IsEditing);
            h.Key(Key.Back);
            h.Key(Key.Left, RawInputModifiers.Alt);
            h.Key(Key.Back, RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            h.Key(Key.Escape);
            h.FocusList();
            h.Key(Key.Back);
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
        });
    }

    [Fact]
    public async Task Backspace_bound_by_the_user_to_an_Explorer_action_takes_over_from_the_composite()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.DoubleClick("Deep");
            await h.SettleAsync();
            h.Settings.Hotkeys["nav.backOrPrevious"] = new List<string>();      // the user frees Backspace...
            h.Settings.Hotkeys["explorer.up"] = new List<string> { "Backspace" };  // ...and gives it to Up
            h.FocusList();
            h.Key(Key.Back);                                                   // now Up (a new step), not Back
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.False(h.Surface.ForwardButton.IsEnabled);
            Assert.Equal(new[] { h.Root, alpha, Path.Combine(alpha, "Deep"), alpha }, History(h).Entries.Select(e => e.Path));
        });
    }

    [Fact]
    public async Task Real_Backspace_in_an_image_tab_goes_Back_to_the_Explorer_with_the_image_selected_then_Forward_returns_and_Left_still_goes_to_the_previous_image()
    {
        await ExplorerHarness.Run(async h =>
        {
            var tabId = h.Workspace.Active!.Id;
            await OpenImageFromExplorerAsync(h, ExplorerHarness.ImageA);
            await h.WaitUntil(() => h.Get<bool>("_folderIndexReady"), "folder indexed");
            h.Key(Key.Right);                                                  // Next image: not a history step
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), ExplorerHarness.ImageB, StringComparison.OrdinalIgnoreCase), "next image");
            await Task.Delay(400);                                             // past the keyboard navigation rate limit
            await h.SettleAsync();
            h.Key(Key.Left);                                                   // Left arrow is still Previous image
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), ExplorerHarness.ImageA, StringComparison.OrdinalIgnoreCase), "previous image");
            Assert.IsType<ImageTabState>(h.Workspace.Active);

            h.Key(Key.Back);                                                   // Backspace = Back (there is a Back step), not Previous image
            await WaitForExplorerAsync(h, h.Root);
            Assert.Equal(tabId, h.Workspace.Active!.Id);
            Assert.Equal(new[] { ExplorerHarness.ImageA }, h.SelectedNames);

            h.Key(Key.Right, RawInputModifiers.Alt);                           // Forward: back to the image
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState i && Path.GetFileName(i.Path) == ExplorerHarness.ImageA, "Forward returned to the image");

            // Explorer list with no Back step: Backspace does nothing (and never reaches Previous image / Up).
            h.Key(Key.Left, RawInputModifiers.Alt);
            await WaitForExplorerAsync(h, h.Root);
            Assert.False(History(h).CanGoBack);
            h.FocusList();
            h.Key(Key.Back);
            await h.SettleAsync();
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(h.Root, h.CurrentFolder);
            Assert.Equal(1, History(h).Index + 1);
        });
    }

    [Fact]
    public async Task An_image_opened_in_a_new_tab_from_the_explorer_has_nothing_to_go_back_to_so_Backspace_is_Previous_image_and_Alt_Left_does_nothing()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerTab = h.Workspace.Active!.Id;
            h.Settings.Gestures["explorer.doubleClick"] = "openInNewTab";
            h.Settings.SwitchToNewTabs = true; // 5.0.6: new tabs follow "Open new tabs in the foreground" (on by default) on every route
            h.DoubleClick(ExplorerHarness.ImageA);
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in a new tab");
            Assert.NotEqual(explorerTab, h.Workspace.Active!.Id);
            Assert.False(History(h).CanGoBack);
            var back = h.Get<Dictionary<string, Button>>("_titleBarButtons")["nav.back"];
            Assert.True(back.Classes.Contains("inactive"));
            h.Key(Key.Left, RawInputModifiers.Alt);                            // strict Back: no fallback to Previous image
            await h.SettleAsync();
            Assert.IsType<ImageTabState>(h.Workspace.Active);
            Assert.Equal(ExplorerHarness.ImageA, Path.GetFileName(h.Get<string?>("_currentPath")));

            await h.WaitUntil(() => h.Get<bool>("_folderIndexReady"), "folder indexed");
            h.Key(Key.Right);
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), ExplorerHarness.ImageB, StringComparison.OrdinalIgnoreCase), "next image");
            await Task.Delay(400);                                             // past the keyboard navigation rate limit
            await h.SettleAsync();
            h.Key(Key.Back);                                                   // no Back step: Backspace is Previous image
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), ExplorerHarness.ImageA, StringComparison.OrdinalIgnoreCase), "Backspace showed the previous image");
            Assert.IsType<ImageTabState>(h.Workspace.Active);
            Assert.False(History(h).CanGoBack);
        });
    }

    /// <summary>
    /// The test above failed now and then in full runs (KeyNotFoundException on _tabHistories): a new image tab became
    /// the active tab at once, but its history was only registered after the image had loaded (ActivateWorkspaceAsync
    /// awaits LoadPathAsync first). Under load the test looked in between. The history must exist as soon as the tab is
    /// active, before the image load is awaited.
    /// </summary>
    [Fact]
    public async Task A_new_image_tab_has_its_history_as_soon_as_it_is_active_before_the_image_has_loaded()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.SwitchToNewTabs = true;
            var path = Path.Combine(h.Root, ExplorerHarness.ImageA);
            // Only the synchronous part of opening runs here: the image load is still pending.
            var opening = (Task)h.Call("OpenImageInNewTabAsync", path, KeyModifiers.None, "test")!;
            Assert.IsType<ImageTabState>(h.Workspace.Active);
            Assert.True(h.Window.TabHistories.Contains(h.Workspace.Active!.Id),
                $"the new active tab has no history yet (open finished: {opening.IsCompleted})");
            Assert.False(History(h).CanGoBack);
            await opening;
            await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), path, StringComparison.OrdinalIgnoreCase), "image loaded");
            Assert.Equal(1, History(h).Count);
            Assert.False(History(h).CanGoBack);
        });
    }

    [Fact]
    public async Task A_folder_that_is_gone_falls_back_to_its_nearest_parent_with_a_message()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            var deep = Path.Combine(alpha, "Deep");
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            h.DoubleClick("Deep");
            await h.SettleAsync();
            h.ClickButton(h.Surface.BackButton);
            await h.SettleAsync();
            h.ClickButton(h.Surface.BackButton);
            await h.SettleAsync();
            Assert.Equal(h.Root, h.CurrentFolder);
            Directory.Delete(deep, true);

            await h.Window.NavigateForwardAsync();
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            await h.Window.NavigateForwardAsync();                             // Deep is gone: its parent Alpha, merged into one entry
            await h.SettleAsync();
            Assert.Equal(alpha, h.CurrentFolder);
            Assert.Equal("That folder no longer exists; showing the nearest folder that does.", h.Window.LastHistoryNotice);
            Assert.Equal(new[] { h.Root, alpha }, History(h).Entries.Select(e => e.Path));
            Assert.False(h.Surface.ForwardButton.IsEnabled);
        });
    }
}
