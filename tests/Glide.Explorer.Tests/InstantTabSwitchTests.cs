using Glide.App.Controls;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.1.0 instant tab switching: returning to an Explorer tab from a picture tab shows the very same list (no folder
/// re-read, no new ItemsSource, no settings save), and returning to a picture tab shows its kept frame synchronously.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class InstantTabSwitchTests
{
    private static async Task ActivateAsync(ExplorerHarness h, Guid id)
    {
        h.Call("CaptureActiveTabRuntimeState");
        Assert.True(h.Workspace.Select(id));
        await (Task)h.Call("ActivateWorkspaceAsync")!;
    }

    [Fact]
    public async Task Back_from_a_picture_tab_the_Explorer_tab_keeps_its_list_and_is_not_reloaded()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Active!.Id;
            var source = h.Surface.Repeater.ItemsSource;
            var tileBefore = h.Tile(ExplorerHarness.ImageB);
            Assert.NotNull(tileBefore);

            h.Workspace.AddImage(Path.Combine(h.Root, ExplorerHarness.ImageA));
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.WaitUntil(() => h.Get<ImageViewport>("Viewport").Bitmap is not null, "picture shown");
            // Hidden, not retired: the surface still belongs to the Explorer tab.
            Assert.Equal(explorerId, h.Get<Guid>("_explorerDisplayedTabId"));
            Assert.NotNull(h.Get<string?>("_explorerLoadedFolder"));

            var settingsFile = Directory.GetFiles(h.SettingsRoot, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
            await ActivateAsync(h, explorerId);
            Assert.True(h.BrowserView.IsVisible);
            Assert.Same(source, h.Surface.Repeater.ItemsSource);
            Assert.False(h.Get<bool>("_explorerListing"), "the folder was read again from scratch");
            await h.SettleAsync();
            Assert.Same(tileBefore, h.Tile(ExplorerHarness.ImageB));
            var settingsAfter = Directory.GetFiles(h.SettingsRoot, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
            Assert.Equal(settingsFile, settingsAfter);
        });
    }

    [Fact]
    public async Task A_picture_tab_shows_its_kept_frame_at_once_when_switched_back_to()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Active!.Id;
            h.Workspace.AddImage(Path.Combine(h.Root, ExplorerHarness.ImageA));
            var imageId = h.Workspace.Active!.Id;
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            var viewport = h.Get<ImageViewport>("Viewport");
            await h.WaitUntil(() => viewport.Bitmap is not null, "picture shown");
            var frame = viewport.Bitmap;

            await ActivateAsync(h, explorerId);
            await h.SettleAsync();

            // The activation completes synchronously from the kept frame (no decode, no await on the loader).
            h.Call("CaptureActiveTabRuntimeState");
            Assert.True(h.Workspace.Select(imageId));
            var task = (Task)h.Call("ActivateWorkspaceAsync")!;
            Assert.True(task.IsCompleted, "a kept picture tab must be shown without waiting for a decode");
            await task;
            Assert.Same(frame, viewport.Bitmap);
            Assert.True(h.Get<Avalonia.Controls.Control>("ImageView").IsVisible);
        });
    }

    [Fact]
    public async Task Switching_between_two_Explorer_tabs_reuses_the_kept_listing()
    {
        await ExplorerHarness.Run(async h =>
        {
            var first = h.Workspace.Active!.Id;
            await h.OpenExplorerAsync(Path.Combine(h.Root, "Alpha"));
            var second = h.Workspace.Active!.Id;
            Assert.Contains("inner.png", h.Names);

            await ActivateAsync(h, first);
            // The kept listing is shown without waiting for the folder to be read again.
            await h.WaitUntil(() => h.Names.Contains(ExplorerHarness.ImageA), "first tab listing");
            await h.SettleAsync();
            await ActivateAsync(h, second);
            await h.SettleAsync();
            Assert.Contains("inner.png", h.Names);
        });
    }
}
