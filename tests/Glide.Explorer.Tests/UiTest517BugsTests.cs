using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Glide.App.Controls;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Fixes from the 5.1.7 desktop UI test (docs/feedback/ui-test-5.1.7-desktop.md) that need the real, themed MainWindow:
/// the click that confirms the batch rename dialog (bug 7), middle-click on a thumbnail (12) and the text overlay in the
/// title bar of an Explorer tab (13).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class UiTest517BugsTests
{
    // ------------------------------------------------------------------ bug 7: the Rename click must not reach the list

    [Fact]
    public async Task A_press_on_the_list_right_after_the_batch_rename_dialog_closes_opens_nothing_and_later_presses_still_do()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            h.FocusList();
            h.Key(Key.F2);
            await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "batch rename dialog opened");
            var dialog = h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
            dialog.Editor.Template = "Trip*";
            h.Pump();
            await dialog.RenameForTestsAsync();
            await h.WaitUntil(() => !h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "dialog closed");
            await h.WaitUntil(() => h.Names.Contains("Tripa.png"), "files renamed and listed");

            var tabs = h.Workspace.Tabs.Count;
            var folder = h.CurrentFolder;
            // The leaked second half of the Rename click: a quick double-click at a spot over the list.
            h.DoubleClick("Tripa.png");
            await Task.Delay(250);
            h.Pump();
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Equal(tabs, h.Workspace.Tabs.Count);
            Assert.Equal(folder, h.CurrentFolder);

            // After the short guard the list takes input again.
            h.Set("_explorerIgnorePressesUntilTick", 0L);
            h.DoubleClick("Tripa.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "a later double-click opens the picture");
        });
    }

    [Fact]
    public async Task Back_after_a_batch_rename_and_one_real_open_returns_to_the_folder_not_home()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            h.FocusList();
            h.Key(Key.F2);
            await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "batch rename dialog opened");
            var dialog = h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
            dialog.Editor.Template = "Trip*";
            h.Pump();
            await dialog.RenameForTestsAsync();
            await h.WaitUntil(() => h.Names.Contains("Tripa.png") && h.Names.Contains("Tripb.png"), "files renamed and listed");
            var folder = h.CurrentFolder;
            h.Set("_explorerIgnorePressesUntilTick", 0L);
            h.DoubleClick("Tripa.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "picture opened");
            await (Task)h.Call("NavigateBackAsync")!;
            await h.WaitUntil(() => h.Workspace.Active is BrowserTabState, "back to the Explorer folder");
            Assert.Equal(folder, h.CurrentFolder);
        });
    }

    // ------------------------------------------------------------------ bug 12: middle-click on a thumbnail

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Middle_click_on_a_picture_opens_a_new_tab_in_every_view(int viewMode)
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Call("SetExplorerViewMode", viewMode);
            await h.SettleAsync();
            var explorerId = h.Workspace.Active!.Id;
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "a new picture tab");
            Assert.Contains(h.Workspace.Tabs, t => t.Id == explorerId && t is BrowserTabState);
            Assert.Equal(Path.Combine(h.Root, "b.png"), h.Workspace.Tabs.OfType<ImageTabState>().Single().Path);
        });
    }

    // ------------------------------------------------------------------ 13: the title-bar overlay in an Explorer tab

    [Fact]
    public async Task A_text_overlay_in_the_title_bar_hides_in_an_Explorer_tab_and_shows_again_on_a_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.PictureCounterPositionTopTabs = "Title bar – center";
            h.Call("ApplySettingsVisuals", false);
            await h.SettleAsync();
            var host = h.Window.FindControl<Border>("CornerCounterHost")!;
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.False(host.IsVisible, "no picture is on screen in an Explorer tab, so the title-bar overlay has nothing to show");

            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "picture opened in the tab");
            await h.WaitUntil(() => host.IsVisible, "the overlay shows with a picture");

            await (Task)h.Call("NavigateBackAsync")!;
            await h.WaitUntil(() => h.Workspace.Active is BrowserTabState, "back in the Explorer tab");
            await h.WaitUntil(() => !host.IsVisible, "the overlay hides again with no picture");
        });
    }

    [Fact]
    public async Task A_text_overlay_over_the_picture_is_unaffected_by_the_Explorer_rule()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.PictureCounterPositionTopTabs = "Top right";
            h.Call("ApplySettingsVisuals", false);
            await h.SettleAsync();
            Assert.True(h.Window.FindControl<Border>("CornerCounterHost")!.IsVisible, "over the picture the host stays on (its parent, the picture view, is what hides)");
        });
    }
    // ------------------------------------------------------------------ bug 12: a duplicated Explorer tab

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Middle_click_works_in_a_duplicated_Explorer_tab_made_in_the_background_and_activated_later(bool background)
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.DuplicateTabPosition = "at the end";
            h.Settings.DuplicateTabSide = background ? "background" : "foreground";
            var original = h.Workspace.Active!.Id;
            await h.Window.ExecuteActionAsync(Glide.Core.Commands.AppActionRegistry.Find("tab.duplicate")!, "test");
            await h.SettleAsync();
            var copy = h.Workspace.Tabs.OfType<BrowserTabState>().Single(t => t.Id != original);
            if (h.Workspace.Active!.Id != copy.Id)
            {
                // A real click on the duplicate's tab header, as the user did.
                var strip = h.Get<Avalonia.Controls.Panel>("TabStripPanel");
                var header = strip.Children.OfType<Avalonia.Controls.Border>().Single(b => Equals(b.DataContext, copy.Id));
                h.Click(header.TranslatePoint(new Avalonia.Point(header.Bounds.Width / 3, header.Bounds.Height / 2), h.Window)!.Value);
                await h.WaitUntil(() => h.Workspace.Active?.Id == copy.Id, "the duplicate was activated");
                await h.SettleAsync();
            }
            Assert.Equal(copy.Id, h.Workspace.Active!.Id);
            var imageTabs = h.Workspace.Tabs.OfType<ImageTabState>().Count();
            await h.WaitUntil(() => h.Tile("b.png") is not null, "the duplicate lists its pictures");
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Count() == imageTabs + 1, "middle-click opened a new picture tab");
        });
    }
    [Fact]
    public async Task Middle_click_on_a_picture_that_is_already_open_in_another_tab_still_opens_a_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Workspace.AddImage(Path.Combine(h.Root, "b.png"));
            h.Call("RebuildTabStrip");
            h.Pump();
            var imageTabs = h.Workspace.Tabs.OfType<ImageTabState>().Count();
            var explorer = h.Workspace.Tabs.OfType<BrowserTabState>().Single().Id;
            if (h.Workspace.Active!.Id != explorer)
            {
                Assert.True(h.Workspace.Select(explorer));
                await (Task)h.Call("ActivateWorkspaceAsync")!;
                await h.SettleAsync();
            }
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Count() == imageTabs + 1, "middle-click opened another tab");
        });
    }
    [Fact]
    public async Task Middle_click_works_after_switching_between_an_Explorer_tab_and_its_duplicate_and_an_image_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.DuplicateTabPosition = "at the end";
            h.Settings.DuplicateTabSide = "background";
            var original = h.Workspace.Active!.Id;
            h.Workspace.AddImage(Path.Combine(h.Root, "c.png"));
            Assert.True(h.Workspace.Select(original));
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            await h.Window.ExecuteActionAsync(Glide.Core.Commands.AppActionRegistry.Find("tab.duplicate")!, "test");
            await h.SettleAsync();
            var copy = h.Workspace.Tabs.OfType<BrowserTabState>().Single(t => t.Id != original).Id;
            async Task Click(Guid id)
            {
                var strip = h.Get<Avalonia.Controls.Panel>("TabStripPanel");
                var header = strip.Children.OfType<Avalonia.Controls.Border>().Single(b => Equals(b.DataContext, id));
                h.Click(header.TranslatePoint(new Avalonia.Point(header.Bounds.Width / 3, header.Bounds.Height / 2), h.Window)!.Value);
                await h.WaitUntil(() => h.Workspace.Active?.Id == id, "tab activated");
                await h.SettleAsync();
            }
            var image = h.Workspace.Tabs.OfType<ImageTabState>().Single().Id;
            await Click(image);
            await Click(copy);
            await Click(original);
            await Click(copy);
            var imageTabs = h.Workspace.Tabs.OfType<ImageTabState>().Count();
            await h.WaitUntil(() => h.Tile("b.png") is not null, "tiles");
            h.Click("b.png", MouseButton.Middle);
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Count() == imageTabs + 1, "middle-click opened a new picture tab");
            await h.SettleAsync();
            Assert.Equal(copy, h.Workspace.Tabs.OfType<BrowserTabState>().Last().Id);
        });
    }
}
