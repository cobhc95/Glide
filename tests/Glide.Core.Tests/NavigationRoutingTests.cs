using Glide.App.Services;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>Characterisation of the previous / next routing and the per-tab history store (Stage D step 6).</summary>
[Trait("Area", "Navigation")]
public sealed class NavigationRoutingTests
{
    private static NavigationCoordinator Folder(int count, int index)
    {
        var dir = Path.Combine(Path.GetTempPath(), "glide-route");
        var paths = Enumerable.Range(0, count).Select(i => Path.Combine(dir, $"img{i:D3}.jpg")).ToArray();
        var navigator = new NavigationCoordinator();
        navigator.ReplaceFolderIndex(paths, paths[index]);
        return navigator;
    }

    [Fact]
    public void Boundary_is_first_or_last_image_in_the_direction_of_travel()
    {
        var nav = Folder(3, 0);
        Assert.True(nav.AtBoundary(-1));
        Assert.False(nav.AtBoundary(1));
        nav.Move(2);
        Assert.True(nav.AtBoundary(1));
        Assert.False(nav.AtBoundary(-1));
    }

    [Fact]
    public void Route_ignores_empty_zero_and_open_prompt()
    {
        Assert.Equal(NavigationRoute.Ignore, new NavigationCoordinator().Route(1, true, true, false, () => true));
        Assert.Equal(NavigationRoute.Ignore, Folder(3, 1).Route(0, true, true, false, () => true));
        Assert.Equal(NavigationRoute.Ignore, Folder(3, 1).Route(1, true, true, true, () => true));
    }

    [Fact]
    public void Route_buffers_until_the_index_is_ready_and_canonical_at_a_boundary()
    {
        Assert.Equal(NavigationRoute.Buffer, Folder(3, 1).Route(1, false, false, false, () => true));
        Assert.Equal(NavigationRoute.Move, Folder(3, 1).Route(1, true, false, false, () => true));
        Assert.Equal(NavigationRoute.Buffer, Folder(3, 2).Route(1, true, false, false, () => true));
    }

    [Fact]
    public void Route_at_the_end_uses_the_end_of_folder_step_unless_it_is_stay()
    {
        var asked = 0;
        Assert.Equal(NavigationRoute.Move, Folder(3, 1).Route(1, true, true, false, () => { asked++; return false; }));
        Assert.Equal(0, asked); // the end-of-folder policy is only consulted at a boundary
        Assert.Equal(NavigationRoute.EndOfFolder, Folder(3, 2).Route(1, true, true, false, () => false));
        Assert.Equal(NavigationRoute.Move, Folder(3, 2).Route(1, true, true, false, () => true)); // Stay: notice under the gate
        Assert.Equal(NavigationRoute.EndOfFolder, Folder(3, 0).Route(-1, true, true, false, () => false));
    }

    [Fact]
    public void Buffer_and_rapid_timing()
    {
        Assert.Equal(3, NavigationCoordinator.BufferDelta(2, 1, 5));
        Assert.Equal(5, NavigationCoordinator.BufferDelta(5, 1, 5));
        Assert.Equal(-5, NavigationCoordinator.BufferDelta(-5, -3, 5));
        Assert.False(NavigationCoordinator.IsRapid(false, 0));
        Assert.True(NavigationCoordinator.IsRapid(true, 150));
        Assert.False(NavigationCoordinator.IsRapid(true, 151));
    }

    [Fact]
    public void History_store_creates_on_first_use_and_travels_with_a_tab()
    {
        var store = new TabHistoryStore();
        var tab = new HomeTabState(Guid.NewGuid());
        var history = store.For(tab);
        Assert.Same(history, store.For(tab));
        history.Record(NavigationEntry.Home());
        var image = Path.Combine(Path.GetTempPath(), "a.jpg");
        history.Record(NavigationEntry.Image(image));
        var travelled = store.Take(tab.Id);
        Assert.NotNull(travelled);
        Assert.False(store.Contains(tab.Id));
        var moved = tab with { History = travelled };
        Assert.Equal(2, store.For(moved).Count);
        Assert.Null(store.Take(Guid.NewGuid()));
    }

    [Fact]
    public void History_store_prunes_closed_tabs_and_resets()
    {
        var store = new TabHistoryStore();
        var a = new HomeTabState(Guid.NewGuid());
        var b = new HomeTabState(Guid.NewGuid());
        store.For(a).Record(NavigationEntry.Home());
        store.For(b);
        store.Prune([a]);
        Assert.True(store.Contains(a.Id));
        Assert.False(store.Contains(b.Id));
        store.Reset(a.Id);
        Assert.Equal(0, store.Find(a.Id)!.Count);
    }

    [Fact]
    public void Restored_explorer_folders_become_entries_and_round_trip_to_the_session()
    {
        var root = Directory.CreateTempSubdirectory("glide-hist").FullName;
        try
        {
            var one = Directory.CreateDirectory(Path.Combine(root, "one")).FullName;
            var two = Directory.CreateDirectory(Path.Combine(root, "two")).FullName;
            var gone = Path.Combine(root, "gone");
            var nav = new BrowserNavigationState([one, gone, two], 2, [Path.Combine(one, "x.jpg"), null, null]);
            var browser = new BrowserTabState(Guid.NewGuid(), two) { Navigation = nav };
            var store = new TabHistoryStore();
            var history = store.For(browser);
            Assert.Equal([one, two], history.Entries.Select(e => e.Path));
            Assert.Equal(1, history.Index);
            Assert.Equal(Path.Combine(one, "x.jpg"), history.Entries[0].SelectedPath);
            var back = store.BrowserNavigationFor(browser.Id)!;
            Assert.Equal([one, two], back.History);
            Assert.Equal(1, back.Index);
        }
        finally { Directory.Delete(root, true); }
    }
}
