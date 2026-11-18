using System.Collections;

namespace Glide.Explorer.Tests;

/// <summary>
/// Round 3 #15 / #17: a reload of the shown folder (after copy / paste / delete / rename) keeps the tiles until the new
/// listing is ready (it used to empty the pane, a dark flash), and while another folder is being read the
/// "No folders or images in this folder." text is not shown (it flashed before the contents appeared).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerRefreshFlickerTests
{
    private static int ShownCount(ExplorerHarness h) => (h.Surface.Repeater.ItemsSource as IEnumerable)?.Cast<object>().Count() ?? 0;

    [Fact]
    public async Task Opening_a_folder_never_shows_the_empty_folder_text_before_its_contents()
    {
        await ExplorerHarness.Run(async h =>
        {
            var alpha = Path.Combine(h.Root, "Alpha");
            h.Call("NavigateBrowserTo", alpha, true, null);
            Assert.False(h.Surface.EmptyText.IsVisible, "empty-folder text shown while the folder is still being read");
            h.Pump();
            Assert.False(h.Surface.EmptyText.IsVisible, "empty-folder text shown while the folder is still being read");
            await h.SettleAsync();
            Assert.Contains("inner.png", h.Names);
            Assert.False(h.Surface.EmptyText.IsVisible);

            // A really empty folder still says so once it has been read.
            h.Call("NavigateBrowserTo", Path.Combine(h.Root, "Beta"), true, null);
            Assert.False(h.Surface.EmptyText.IsVisible, "empty-folder text shown before the folder was read");
            await h.SettleAsync();
            Assert.True(h.Surface.EmptyText.IsVisible);
            Assert.Equal("No folders or images in this folder.", h.Surface.EmptyText.Text);
        });
    }

    [Fact]
    public async Task Reloading_the_shown_folder_keeps_the_tiles_until_the_new_listing_is_ready()
    {
        await ExplorerHarness.Run(async h =>
        {
            var before = h.Names.ToList();
            Assert.NotEmpty(before);
            File.WriteAllBytes(Path.Combine(h.Root, "d.png"), ExplorerHarness.Png(8, 8, 9, 9, 9));
            h.Call("RefreshManagedBrowser", h.Root);
            Assert.Equal(before, h.Names);
            Assert.Equal(before.Count, ShownCount(h));
            h.Pump();
            Assert.True(ShownCount(h) >= before.Count, "the list was emptied during the reload");
            Assert.False(h.Surface.EmptyText.IsVisible);
            await h.SettleAsync();
            Assert.Contains("d.png", h.Names);
            Assert.Equal(h.Names.Count, ShownCount(h));
        });
    }
}
