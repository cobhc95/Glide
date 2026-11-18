using Glide.Core.Workspace;

namespace Glide.Core.Tests;

// Source-text contract kept: two facts assert on UI code-behind (activation paths) that cannot run without the window; the LRU itself is tested behaviourally.
[Trait("Area", "Tabs")]
public sealed class TabWarmCacheTests
{
    [Fact]
    public void Keeps_the_most_recent_tabs_and_evicts_the_least_recently_used_once()
    {
        var evicted = new List<string>();
        var cache = new TabWarmCache<string>(2, evicted.Add);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        cache.Put(a, "a");
        cache.Put(b, "b");
        Assert.True(cache.TryGet(a, out _)); // a is now the most recent
        cache.Put(c, "c");
        Assert.Equal(new[] { "b" }, evicted);
        Assert.False(cache.Contains(b));
        Assert.Equal(new[] { c, a }, cache.Ids);
    }

    [Fact]
    public void Replacing_a_value_evicts_the_old_one_unless_it_is_the_same_resource()
    {
        var evicted = new List<string>();
        var cache = new TabWarmCache<string>(3, evicted.Add);
        var id = Guid.NewGuid();
        cache.Put(id, "frame1");
        cache.Put(id, "frame1", (x, y) => x == y);
        Assert.Empty(evicted);
        cache.Put(id, "frame2", (x, y) => x == y);
        Assert.Equal(new[] { "frame1" }, evicted);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Closed_tabs_are_released_and_clear_releases_everything()
    {
        var evicted = new List<string>();
        var cache = new TabWarmCache<string>(5, evicted.Add);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        cache.Put(a, "a"); cache.Put(b, "b"); cache.Put(c, "c");
        Assert.Equal(1, cache.RetainOnly(new[] { a, c }));
        Assert.Equal(new[] { "b" }, evicted);
        Assert.True(cache.Remove(a));
        Assert.False(cache.Remove(a));
        cache.Clear();
        Assert.Equal(new[] { "b", "a", "c" }, evicted);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Image_and_Explorer_capacities_match_the_plan()
    {
        var source = File.ReadAllText(FindSource("src", "Glide.App", "MainWindow.TabWarmCache.cs"));
        Assert.Contains("WarmImageTabCapacity = 5", source, StringComparison.Ordinal);
        Assert.Contains("WarmExplorerTabCapacity = 3", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_activation_does_not_save_settings_or_retire_the_Explorer_surface_for_a_picture()
    {
        var navigation = File.ReadAllText(FindSource("src", "Glide.App", "MainWindow.Navigation.cs")).Replace("\r\n", "\n");
        var showImage = Body(navigation, "private void ShowImageSurface()");
        Assert.DoesNotContain("RetireExplorerSurface", showImage, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelBrowserThumbnailWork", showImage, StringComparison.Ordinal);
        var activate = Body(navigation, "private async Task ActivateWorkspaceAsync()");
        Assert.Contains("TryShowWarmImageTab(image)", activate, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveSettings", activate, StringComparison.Ordinal);

        var pickers = File.ReadAllText(FindSource("src", "Glide.App", "MainWindow.Pickers.cs")).Replace("\r\n", "\n");
        var navigate = Body(pickers, "private void NavigateBrowserTo(");
        var save = navigate.IndexOf("SaveSettingsAndPublish()", StringComparison.Ordinal);
        var guard = navigate.IndexOf("if (!string.Equals(_settings.LastNewExplorerTabDirectory, folder", StringComparison.Ordinal);
        Assert.True(guard >= 0 && save > guard, "the remembered folder is saved only when it changes");
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string FindSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
