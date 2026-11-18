using Avalonia.VisualTree;

namespace Glide.Explorer.Tests;

/// <summary>
/// Round 3 #15: a reload of the shown folder (Glide's own paste / delete, or an outside change seen by the folder
/// watcher) updates the list in place: unchanged tiles keep their element (and thumbnail), the list is never emptied
/// or swapped, deleted tiles disappear and new items are inserted at their sorted position.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerInPlaceUpdateTests
{
    // The decoded thumbnail shown by a tile: an in-place update keeps the very same bitmap (no reload, no blank tile).
    private static object? Thumb(ExplorerHarness h, string name) =>
        h.Tile(name)?.GetVisualDescendants().OfType<Avalonia.Controls.Image>().FirstOrDefault()?.Source;

    private static async Task<object> LoadedThumb(ExplorerHarness h, string name)
    {
        await h.WaitUntil(() => Thumb(h, name) is not null, $"thumbnail of {name}");
        return Thumb(h, name)!;
    }

    [Fact]
    public async Task Reload_after_add_and_delete_keeps_the_list_and_the_unchanged_tiles()
    {
        await ExplorerHarness.Run(async h =>
        {
            var list = h.Surface.Repeater.ItemsSource;
            var thumbA = await LoadedThumb(h, ExplorerHarness.ImageA);
            var thumbC = await LoadedThumb(h, ExplorerHarness.ImageC);
            h.Call("SelectBrowserIndex", h.Names.ToList().IndexOf(ExplorerHarness.ImageC), false, false, false);

            File.Delete(Path.Combine(h.Root, ExplorerHarness.ImageB));
            File.WriteAllBytes(Path.Combine(h.Root, "bb.png"), ExplorerHarness.Png(8, 8, 9, 9, 9));
            h.Call("RefreshManagedBrowser", h.Root);
            await h.SettleAsync();

            Assert.Same(list, h.Surface.Repeater.ItemsSource);
            Assert.DoesNotContain(ExplorerHarness.ImageB, h.Names);
            var names = h.Names.ToList();
            Assert.Equal(names.IndexOf(ExplorerHarness.ImageA) + 1, names.IndexOf("bb.png"));
            Assert.Same(thumbA, Thumb(h, ExplorerHarness.ImageA));
            Assert.Same(thumbC, Thumb(h, ExplorerHarness.ImageC));
            Assert.Equal(new[] { ExplorerHarness.ImageC }, h.SelectedNames);
            Assert.Equal(names.IndexOf(ExplorerHarness.ImageC), h.CurrentIndex);
            Assert.False(h.Surface.EmptyText.IsVisible);
        });
    }

    [Fact]
    public async Task An_outside_change_reported_by_the_watcher_is_applied_in_place()
    {
        // The real FileSystemWatcher + debounce is covered by Glide.Core.Tests (ExplorerFolderWatchTests); here the
        // watcher's UI-thread callback is invoked directly so the test does not depend on watcher timing.
        await ExplorerHarness.Run(async h =>
        {
            Assert.NotNull(h.Get<object?>("_explorerFolderWatch"));
            var list = h.Surface.Repeater.ItemsSource;
            var thumbA = await LoadedThumb(h, ExplorerHarness.ImageA);
            var entryA = h.Entries.First(e => e.Name == ExplorerHarness.ImageA);
            File.WriteAllBytes(Path.Combine(h.Root, "zz-outside.png"), ExplorerHarness.Png(8, 8, 1, 1, 1));
            Directory.CreateDirectory(Path.Combine(h.Root, "Outside folder"));
            File.Move(Path.Combine(h.Root, ExplorerHarness.ImageC), Path.Combine(h.Root, "renamed.png"));
            h.Call("OnExplorerFolderChanged", h.Root);
            await h.SettleAsync();
            Assert.Same(list, h.Surface.Repeater.ItemsSource);
            Assert.Contains("zz-outside.png", h.Names);
            Assert.Contains("renamed.png", h.Names);
            Assert.DoesNotContain(ExplorerHarness.ImageC, h.Names);
            Assert.Same(entryA, h.Entries.First(e => e.Name == ExplorerHarness.ImageA));
            Assert.Same(thumbA, Thumb(h, ExplorerHarness.ImageA));
            var names = h.Names.ToList();
            Assert.True(names.IndexOf("Outside folder") < names.IndexOf(ExplorerHarness.ImageA), "folders first");

            File.Delete(Path.Combine(h.Root, "zz-outside.png"));
            h.Call("OnExplorerFolderChanged", h.Root);
            await h.SettleAsync();
            Assert.DoesNotContain("zz-outside.png", h.Names);
            Assert.Same(list, h.Surface.Repeater.ItemsSource);
            Assert.Same(thumbA, Thumb(h, ExplorerHarness.ImageA));
        });
    }
}
