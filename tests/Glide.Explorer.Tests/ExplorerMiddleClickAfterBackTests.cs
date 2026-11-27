using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Glide.App.Services;
using Glide.Core.Commands;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Real-desktop failure of the installed 5.2.0 build: after an image opened in place in an Explorer tab and Back (Alt+Left)
/// returned to the folder list, a middle-click on a thumbnail opened nothing ("no tab is added"), while a double-click still
/// opened it. Headless tests never reproduced it (three rounds), so these drive the exact sequence of the desktop test through
/// the real pointer and key routing of the real MainWindow and assert what the user sees: the new tab is in the tab strip and
/// its picture is on screen, after every step. The same steps are traced by GLIDE_TRACE_INPUT=1 on the real desktop.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerMiddleClickAfterBackTests
{
    private static void FiveImages(string root)
    {
        ExplorerHarness.DefaultTree(root);
        File.WriteAllBytes(Path.Combine(root, "d.png"), ExplorerHarness.Png(48, 32, 200, 200, 40));
        File.WriteAllBytes(Path.Combine(root, "e.png"), ExplorerHarness.Png(48, 32, 40, 200, 200));
    }

    private static async Task ClickTabAsync(ExplorerHarness h, Guid id)
    {
        // The activation of the tab opened a moment ago ends with a strip rebuild queued on the dispatcher; a click that lands
        // while it runs would press on a header that is replaced before the release.
        await h.SettleAsync();
        var strip = h.Get<Panel>("TabStripPanel");
        var tab = strip.Children.OfType<Border>().Single(b => Equals(b.DataContext, id));
        var point = tab.TranslatePoint(new Point(tab.Bounds.Width / 3, tab.Bounds.Height / 2), h.Window)!.Value;
        h.Click(point);
        await h.WaitUntil(() => h.Workspace.Active?.Id == id, "clicked tab activated");
        await h.SettleAsync();
        try { await h.WaitUntil(() => h.Tile("c.png") is not null && h.BrowserView.IsVisible, "the folder list is showing"); }
        catch (Xunit.Sdk.XunitException ex)
        {
            throw new Xunit.Sdk.XunitException(ex.Message + " (tabs " + string.Join(", ", h.Workspace.Tabs.Select(t => t.GetType().Name + (t.Id == h.Workspace.Active?.Id ? "*" : "")))
                + $"; tile c.png {(h.Tile("c.png") is null ? "missing" : "present")}; browser view visible {h.BrowserView.IsVisible}; picture view visible {h.Window.FindControl<Grid>("ImageView")!.IsVisible})");
        }
    }

    /// <summary>A real middle press and release on a thumbnail: a new foreground tab appears in the strip and shows that picture.</summary>
    private static async Task MiddleClickOpensNewTabAsync(ExplorerHarness h, string name, string step)
    {
        var explorerId = h.Workspace.Tabs.OfType<BrowserTabState>().First().Id;
        var before = h.Workspace.Tabs.Count;
        h.Click(name, MouseButton.Middle);
        await h.WaitUntil(() => h.Workspace.Tabs.Count == before + 1, $"{step}: middle-click on {name} added a tab");
        Assert.IsType<ImageTabState>(h.Workspace.Active);
        Assert.Equal(Path.Combine(h.Root, name), ((ImageTabState)h.Workspace.Active!).Path, ignoreCase: true);
        Assert.Contains(h.Workspace.Tabs, t => t.Id == explorerId && t is BrowserTabState);
        // What the user sees: the tab is in the strip, and the picture view shows the picture.
        var id = h.Workspace.Active!.Id;
        Assert.Contains(h.Get<Panel>("TabStripPanel").Children.OfType<Border>(), b => Equals(b.DataContext, id));
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, $"{step}: the picture view is showing");
        await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), Path.Combine(h.Root, name), StringComparison.OrdinalIgnoreCase), $"{step}: {name} is the picture on screen");
    }

    /// <summary>An Explorer tab made with Ctrl+E from an image tab, so it has image history (the real test's starting point).</summary>
    private static async Task<Guid> ExplorerTabFromImageWithCtrlEAsync(ExplorerHarness h)
    {
        h.Settings.ExplorerLocateOpensIn = ExplorerLocateTargets.SameTab;
        h.DoubleClick("a.png");
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "picture opened");
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
        await h.SettleAsync();
        h.Key(Key.E, RawInputModifiers.Control);
        await h.WaitUntil(() => h.Workspace.Active is BrowserTabState, "Ctrl+E opened an Explorer tab");
        await h.SettleAsync();
        await h.WaitUntil(() => h.Tile("c.png") is not null, "the folder is listed");
        return h.Workspace.Active!.Id;
    }

    /// <summary>Real keyboard Alt+Left: the Alt key goes down, Left goes down and up with Alt held, Alt goes up.</summary>
    private static void AltLeft(ExplorerHarness h)
    {
        h.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt);
        h.Window.KeyPress(Key.Left, RawInputModifiers.Alt);
        h.Window.KeyRelease(Key.Left, RawInputModifiers.Alt);
        h.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None);
        h.Pump();
    }

    /// <summary>Selects a picture with a click, presses Enter (it opens in the same tab), then Alt+Left (back to the folder list).</summary>
    private static async Task OpenInPlaceWithEnterThenAltLeftAsync(ExplorerHarness h, Guid explorerId, string name)
    {
        h.Click(name);
        h.FocusList();
        h.Key(Key.Enter);
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState && h.Workspace.Active.Id == explorerId, "Enter opened the picture in the same tab");
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
        await h.SettleAsync();
        AltLeft(h);
        await h.WaitUntil(() => h.Workspace.Active is BrowserTabState && h.Workspace.Active.Id == explorerId, "Alt+Left returned to the folder list");
        await h.SettleAsync();
        await h.WaitUntil(() => h.Tile("e.png") is not null && h.BrowserView.IsVisible, "the folder list is showing");
    }

    [Fact]
    public async Task Middle_click_opens_a_tab_again_after_an_image_opened_in_place_and_Alt_Left_came_back()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = await ExplorerTabFromImageWithCtrlEAsync(h);

            // a) middle-click a picture: it opens in a new foreground tab.
            await MiddleClickOpensNewTabAsync(h, "a.png", "a");
            // b) click the Explorer tab header, middle-click another picture.
            await ClickTabAsync(h, explorerId);
            await MiddleClickOpensNewTabAsync(h, "b.png", "b");
            await ClickTabAsync(h, explorerId);
            // c) select a picture and press Enter: it opens in the same tab. Alt+Left: back to the folder list.
            await OpenInPlaceWithEnterThenAltLeftAsync(h, explorerId, "c.png");

            // d) middle-click ANY thumbnail: a new tab opens, again and again.
            await MiddleClickOpensNewTabAsync(h, "d.png", "d");
            await ClickTabAsync(h, explorerId);
            await MiddleClickOpensNewTabAsync(h, "e.png", "d2");
            await ClickTabAsync(h, explorerId);
            // An in-place open and Back once more, then another middle-click.
            await OpenInPlaceWithEnterThenAltLeftAsync(h, explorerId, "b.png");
            await MiddleClickOpensNewTabAsync(h, "c.png", "d3");
        }, FiveImages);
    }

    [Fact]
    public async Task Middle_click_works_in_a_duplicated_Explorer_tab_that_has_image_history()
    {
        await ExplorerHarness.Run(async h =>
        {
            var original = await ExplorerTabFromImageWithCtrlEAsync(h);
            var tabsBefore = h.Workspace.Tabs.Count;
            h.Key(Key.K, RawInputModifiers.Control | RawInputModifiers.Shift);   // Ctrl+Shift+K: duplicate tab
            await h.WaitUntil(() => h.Workspace.Tabs.Count == tabsBefore + 1, "the tab was duplicated");
            await h.SettleAsync();
            var copy = h.Workspace.Active!.Id;
            Assert.NotEqual(original, copy);
            await h.WaitUntil(() => h.Tile("c.png") is not null && h.BrowserView.IsVisible, "the duplicate shows the folder");

            await MiddleClickOpensNewTabAsync(h, "a.png", "duplicate: first");
            await ClickTabAsync(h, copy);
            await OpenInPlaceWithEnterThenAltLeftAsync(h, copy, "c.png");
            await MiddleClickOpensNewTabAsync(h, "d.png", "duplicate: after Back");
        }, FiveImages);
    }

    /// <summary>
    /// A press on a tab header selects the tab; the release shows its content. When the strip is rebuilt between the two (the end of
    /// another tab's activation does that) the press loses its pointer capture and the release never reaches the tab, which used to
    /// leave the Explorer tab highlighted with the picture of the other tab on screen (an intermittent failure of the tests above).
    /// </summary>
    [Fact]
    public async Task A_tab_press_whose_strip_is_rebuilt_before_the_release_still_shows_that_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            var explorerId = h.Workspace.Tabs.OfType<BrowserTabState>().First().Id;
            await MiddleClickOpensNewTabAsync(h, "a.png", "a");
            await h.SettleAsync();
            Assert.NotEqual(explorerId, h.Workspace.Active!.Id);

            var tab = h.Get<Panel>("TabStripPanel").Children.OfType<Border>().Single(b => Equals(b.DataContext, explorerId));
            var point = tab.TranslatePoint(new Point(tab.Bounds.Width / 3, tab.Bounds.Height / 2), h.Window)!.Value;
            h.Window.MouseMove(point);
            h.Window.MouseDown(point, MouseButton.Left);
            h.Call("RebuildTabStrip");   // the header under the pointer is replaced while the button is down
            h.Pump();
            h.Window.MouseUp(point, MouseButton.Left);
            h.Pump();

            await h.WaitUntil(() => h.Workspace.Active?.Id == explorerId, "the pressed tab is the active one");
            await h.WaitUntil(() => h.BrowserView.IsVisible && !h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the Explorer tab's folder list is showing, not the other tab's picture");
        }, FiveImages);
    }

    /// <summary>GLIDE_TRACE_INPUT=1 follows a middle-click from the press to the new tab; a file that vanished says so.</summary>
    [Fact]
    public async Task GLIDE_TRACE_INPUT_logs_the_steps_of_a_middle_click()
    {
        var lines = new List<string>();
        UiTrace.SinkForTests = line => { lock (lines) lines.Add(line); };
        UiTrace.SetForTests(hint: null, input: true);
        try
        {
            await ExplorerHarness.Run(async h =>
            {
                await MiddleClickOpensNewTabAsync(h, "b.png", "traced");
                var explorer = h.Workspace.Tabs.OfType<BrowserTabState>().First().Id;
                await ClickTabAsync(h, explorer);
                // The list still shows c.png although the file is gone (the folder watcher is stopped so the refresh cannot win the race).
                h.Get<Glide.Core.FolderWatchService?>("_explorerFolderWatch")?.Stop();
                var point = h.TileCenter("c.png");
                File.Delete(Path.Combine(h.Root, "c.png"));
                h.Click(point, MouseButton.Middle);
                h.Pump();
            });
        }
        finally { UiTrace.SetForTests(null, null); UiTrace.SinkForTests = null; }

        string[] snapshot; lock (lines) snapshot = lines.ToArray();
        string Find(string prefix, string? contains = null) =>
            snapshot.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal) && (contains is null || l.Contains(contains, StringComparison.Ordinal)))
            ?? throw new Xunit.Sdk.XunitException($"no trace line {prefix} {contains}; got:\n{string.Join("\n", snapshot.Take(40))}");
        var press = Find("input.press", "middle=True");
        Assert.Contains("b.png", press);
        Assert.Contains("kind=MiddleButtonPressed", press);
        Assert.Contains("activeTab=BrowserTabState", press);
        Assert.Contains("action=" + GestureCatalog.OpenInNewTab, Find("input.open_entry", "b.png"));
        Assert.Contains("kind=Image", Find("input.tab_added"));
        Assert.Contains("tabs=", Find("input.open_returned", "b.png"));
        Assert.Contains("reason=file_missing", Find("input.open_stopped"));
    }
}
