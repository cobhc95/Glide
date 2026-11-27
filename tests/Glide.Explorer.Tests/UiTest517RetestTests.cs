using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Glide.App.Controls;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Real-desktop retest of the 5.1.7 build: failures the first round of fixes left. Each test drives the real, themed
/// MainWindow with real input and asserts what the user sees (which surface is showing, real control sizes).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class UiTest517RetestTests
{
    /// <summary>Opens a.png in the Explorer tab, then Ctrl+E (same tab), leaving an Explorer tab that remembers a.png as its hidden picture.</summary>
    private static async Task OpenPictureThenLocateInSameTabAsync(ExplorerHarness h)
    {
        h.Settings.ExplorerLocateOpensIn = ExplorerLocateTargets.SameTab;
        h.DoubleClick("a.png");
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "picture opened");
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
        await h.SettleAsync();
        h.Key(Key.E, RawInputModifiers.Control);
        await h.WaitUntil(() => h.Workspace.Active is BrowserTabState, "Ctrl+E turned the tab into an Explorer tab");
        await h.SettleAsync();
        await h.WaitUntil(() => h.Tile("a.png") is not null, "the folder is listed");
        Assert.True(h.BrowserView.IsVisible);
        Assert.False(h.Window.FindControl<Grid>("ImageView")!.IsVisible);
    }

    private void AssertStillExplorerTab(ExplorerHarness h, Guid tabId, string folder)
    {
        h.Pump();
        Assert.Equal(tabId, h.Workspace.Active!.Id);
        Assert.IsType<BrowserTabState>(h.Workspace.Active);
        Assert.True(h.BrowserView.IsVisible, "the Explorer list is still showing");
        Assert.False(h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture viewer must not appear over an Explorer tab");
        Assert.Equal(folder, h.CurrentFolder);
    }

    // ------------------------------------------------------------------ C: renaming the hidden picture flips the Explorer tab to the viewer

    [Fact]
    public async Task Batch_renaming_the_tabs_hidden_picture_from_its_Explorer_tab_keeps_the_Explorer_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenPictureThenLocateInSameTabAsync(h);
            var tabId = h.Workspace.Active!.Id;
            var folder = h.CurrentFolder;

            h.Click("a.png");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            h.FocusList();
            h.Key(Key.F2);
            await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "batch rename dialog opened");
            var dialog = h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
            dialog.Editor.Template = "Trip*";
            h.Pump();
            await dialog.RenameForTestsAsync();
            await h.WaitUntil(() => h.Names.Contains("Tripa.png"), "files renamed and listed");
            // The folder watcher reports the rename a moment later; the tab must stay an Explorer tab for it.
            for (var i = 0; i < 80; i++) { await Task.Delay(40); h.Pump(); }
            AssertStillExplorerTab(h, tabId, folder);
            // Back (enabled) still reaches the picture, now under its new name.
            Assert.True(h.Window.ActiveHistory!.CanGoBack, "Back is enabled in the Explorer tab");
            await (Task)h.Call("NavigateBackAsync")!;
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "Back returned to the picture");
            Assert.Equal(Path.Combine(h.Root, "Tripa.png"), ((ImageTabState)h.Workspace.Active!).Path, ignoreCase: true);
        });
    }

    [Fact]
    public async Task Ticking_a_tag_on_the_tabs_hidden_picture_from_its_Explorer_tab_keeps_the_Explorer_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.TagsEnabled = true;
            await OpenPictureThenLocateInSameTabAsync(h);
            var tabId = h.Workspace.Active!.Id;
            var folder = h.CurrentFolder;
            h.Click("a.png");
            h.FocusList();
            await (Task)h.Call("ApplyTagChangeAsync", (IReadOnlyList<string>)new[] { "red" }, (IReadOnlyList<string>)Array.Empty<string>())!;
            await h.WaitUntil(() => !h.Names.Contains("a.png"), "a.png renamed with the tag");
            for (var i = 0; i < 80; i++) { await Task.Delay(40); h.Pump(); }
            AssertStillExplorerTab(h, tabId, folder);
        });
    }

    // ------------------------------------------------------------------ D: the tag sidebar ignores the selection an Explorer tab is shown with

    private static string SidebarCaption(ExplorerHarness h) =>
        ((Avalonia.Controls.Border)h.Window.FindControl<Avalonia.Controls.Border>("TagSidebarHost")!).Child is Avalonia.Controls.Border sidebar
            ? sidebar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TagSidebarCaption").Text ?? ""
            : "";

    [Fact]
    public async Task Switching_back_to_an_Explorer_tab_with_a_selected_item_shows_it_in_the_tag_sidebar()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.TagsEnabled = true;
            h.Settings.TagGroups = "+Family: mom, dad\nFriends: anna";
            h.Call("UpdateTagSidebar");
            Assert.True(h.Window.FindControl<Border>("TagSidebarHost")!.IsVisible, "the sidebar is showing");
            await h.SettleAsync();   // the list makes room for the sidebar
            var explorer = h.Workspace.Active!.Id;
            h.Click("b.png");
            h.Pump();
            Assert.True(SidebarCaption(h) == "b.png", $"caption=\"{SidebarCaption(h)}\" selected=[{string.Join(",", h.SelectedNames)}] active={h.Workspace.Active?.Kind} browserVisible={h.BrowserView.IsVisible}");   // the selection made while the sidebar is up is followed (already worked)

            // Another tab in front (a picture), then back to the Explorer tab, which still has b.png selected.
            h.Workspace.AddImage(Path.Combine(h.Root, "a.png"));
            h.Call("RebuildTabStrip");
            h.Workspace.Select(h.Workspace.Tabs.OfType<ImageTabState>().Single().Id);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            h.Workspace.Select(explorer);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            await h.WaitUntil(() => h.Tile("b.png") is not null, "the folder is listed again");
            Assert.Contains("b.png", h.SelectedNames);
            await h.WaitUntil(() => SidebarCaption(h) == "b.png", $"the sidebar names the selected picture (it says \"{SidebarCaption(h)}\")");
        });
    }

    [Fact]
    public async Task An_Explorer_tab_opened_with_a_restored_selection_shows_it_in_the_tag_sidebar()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.TagsEnabled = true;
            h.Settings.TagGroups = "+Family: mom, dad\nFriends: anna";
            // A session reopens an Explorer tab with its saved selection (BrowserTabState.View), as it is first shown.
            var fresh = h.Workspace.AddBrowser(h.Root);
            h.Workspace.ReplaceTab(((BrowserTabState)fresh) with { View = new BrowserTabViewState(Path.Combine(h.Root, "c.png"), 0) });
            h.Call("RebuildTabStrip");
            h.Workspace.Select(fresh.Id);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            await h.WaitUntil(() => h.SelectedNames.Contains("c.png"), "the saved selection was restored");
            await h.WaitUntil(() => SidebarCaption(h) == "c.png", $"the sidebar names the restored selection (it says \"{SidebarCaption(h)}\")");
        });
    }

    // ------------------------------------------------------------------ A: the text overlay's tooltip never showed on the real desktop

    /// <summary>The "Alt+drag to move" hint labels on screen: shown, sized and carrying the text (a label in the overlay layer, not a ToolTip, since 5.2.0).</summary>
    private static IEnumerable<TextBlock> OpenHints(ExplorerHarness h) =>
        h.Window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "TextOverlayHint" && b.IsVisible && b.Bounds.Width > 1 && b.Bounds.Height > 1)
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>());

    [Fact]
    public async Task Resting_the_pointer_on_the_text_overlay_with_the_default_delay_shows_a_real_tooltip()
    {
        await ExplorerHarness.Run(async h =>
        {
            Glide.App.Services.ToolTipGuard.Register();
            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
            await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
            var host = h.Window.FindControl<Border>("CornerCounterHost")!;
            h.Settings.PictureCounterEnabled = true;
            await h.SettleAsync();
            for (var i = 0; i < 5; i++) { h.Pump(); await Task.Delay(20); }
            Assert.True(host.IsVisible && host.Bounds.Width > 10 && host.Bounds.Height > 5, $"the overlay has a real size ({host.Bounds})");
            Assert.Equal(600, Glide.Core.Settings.TooltipAreas.DelayMs(h.Settings, Glide.Core.Settings.TooltipAreas.Main));   // the delay the user has

            // The pointer arrives from the dark area beside the picture and then rests, jittering a pixel now and then like a hand.
            var centre = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
            var start = new Point(centre.X - 120, centre.Y + 120);
            h.Window.MouseMove(start);
            h.Pump();
            for (var i = 1; i <= 12; i++) { h.Window.MouseMove(start + (centre - start) * (i / 12.0)); h.Pump(); await Task.Delay(8); }
            var deadline = Environment.TickCount64 + 1800;
            var jitter = 0;
            while (Environment.TickCount64 < deadline && !OpenHints(h).Any())
            {
                h.Window.MouseMove(new Point(centre.X + (jitter++ % 2), centre.Y));
                h.Pump();
                await Task.Delay(40);
            }
            var tips = OpenHints(h).ToList();
            Assert.True(tips.Count > 0, "no tooltip popup is on screen 1.8 s after the pointer reached the overlay");
            Assert.Contains(tips, t => t.Text == Glide.App.MainWindow.TextOverlayToolTip);
        });
    }

    [Fact]
    public async Task A_pointer_that_rests_on_the_text_overlay_still_gets_its_tooltip_after_a_wheel_tick_blocked_the_first_attempt()
    {
        await ExplorerHarness.Run(async h =>
        {
            Glide.App.Services.ToolTipGuard.Register();
            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
            await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
            var host = h.Window.FindControl<Border>("CornerCounterHost")!;
            h.Settings.PictureCounterEnabled = true;
            Glide.Core.Settings.TooltipAreas.SetDelayMs(h.Settings, Glide.Core.Settings.TooltipAreas.Main, 100);
            await h.SettleAsync();
            var centre = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), h.Window)!.Value;
            h.Window.MouseMove(new Point(centre.X - 100, centre.Y + 100));
            h.Pump();
            h.Window.MouseMove(centre);
            // A wheel tick right as the pointer arrives: the guard lets no tooltip open for 600 ms, and the pointer then rests.
            h.Window.MouseWheel(centre, new Vector(0, 1));
            h.Pump();
            Assert.True(Glide.App.Services.ToolTipGuard.IsInWheelQuietPeriod(Environment.TickCount64), "the wheel tick started the quiet period");
            var deadline = Environment.TickCount64 + 2500;
            while (Environment.TickCount64 < deadline && !OpenHints(h).Any()) { h.Pump(); await Task.Delay(40); }
            Assert.Contains(OpenHints(h), t => t.Text == Glide.App.MainWindow.TextOverlayToolTip);
        });
    }
}
