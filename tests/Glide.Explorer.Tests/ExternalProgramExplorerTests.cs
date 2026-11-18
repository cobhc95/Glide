using Avalonia.Headless;
using Avalonia.Input;
using Glide.App;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// "Open in external program 1-3" in an Explorer tab acts on the image SELECTED in the Explorer list and never on a picture
/// of another tab or an earlier selection (owner decision O6). The process launch is replaced by a recording seam.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExternalProgramExplorerTests
{
    private const string Editor1 = @"C:\Tools\Editor1.exe";
    private const string Editor2 = @"C:\Tools\Editor2.exe";
    private const string Editor3 = @"C:\Tools\Editor3.exe";
    private const string SelectImageFirst = "Select an image first";

    private static List<(string? Program, string? Path)> Record(ExplorerHarness h)
    {
        var launches = new List<(string? Program, string? Path)>();
        h.Settings.ExternalProgram1 = Editor1;
        h.Settings.ExternalProgram2 = Editor2;
        h.Settings.ExternalProgram3 = Editor3;
        h.Window.ExternalLauncherForTests = (program, path) => { launches.Add((program, path)); return true; };
        return launches;
    }

    private static string Msg(ExplorerHarness h) => h.Surface.MessageText.Text ?? "";

    private static void Tree(string root)
    {
        ExplorerHarness.DefaultTree(root);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "data.xyz"), "x");
    }

    private static async Task ShowAllFilesAsync(ExplorerHarness h)
    {
        h.Settings.ExplorerShowOnlyImages = false;
        h.Call("NavigateBrowserTo", h.Root, true, null);
        await h.SettleAsync();
        Assert.Contains("notes.txt", h.Names);
    }

    private static async Task OpenImageFromExplorerAsync(ExplorerHarness h, string name)
    {
        h.DoubleClick(name);
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
        await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), Path.Combine(h.Root, name), StringComparison.OrdinalIgnoreCase), "image loaded");
    }

    private static async Task NewExplorerTabAsync(ExplorerHarness h)
    {
        h.Workspace.AddBrowser(h.Root);
        h.Call("RebuildTabStrip");
        await (Task)h.Call("ActivateWorkspaceAsync")!;
        await h.SettleAsync();
    }

    [Fact]
    public async Task Explorer_tab_with_an_image_selected_launches_that_image_for_every_slot()
    {
        await ExplorerHarness.Run(h =>
        {
            var launches = Record(h);
            h.Click("b.png");
            h.Key(Key.D1, RawInputModifiers.Shift);
            h.Key(Key.D2, RawInputModifiers.Shift);
            h.Key(Key.D3, RawInputModifiers.Shift);
            var b = Path.Combine(h.Root, "b.png");
            Assert.Equal(new[] { (Editor1, b), (Editor2, b), (Editor3, b) }, launches.Select(l => (l.Program!, l.Path!)).ToArray());
            Assert.NotEqual(SelectImageFirst, Msg(h));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Explorer_tab_with_nothing_selected_does_not_launch_and_says_so()
    {
        await ExplorerHarness.Run(h =>
        {
            var launches = Record(h);
            Assert.Empty(h.SelectedNames);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Empty(launches);
            Assert.Equal(SelectImageFirst, Msg(h));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selecting_a_folder_does_not_launch()
    {
        await ExplorerHarness.Run(h =>
        {
            var launches = Record(h);
            h.Click("Alpha");
            Assert.Equal(new[] { "Alpha" }, h.SelectedNames);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Empty(launches);
            Assert.Equal(SelectImageFirst, Msg(h));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selecting_an_unsupported_file_does_not_launch()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            await ShowAllFilesAsync(h);
            h.Click("notes.txt");
            Assert.Equal(new[] { "notes.txt" }, h.SelectedNames);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Empty(launches);
            Assert.Equal(SelectImageFirst, Msg(h));
        }, Tree);
    }

    [Fact]
    public async Task Several_selected_items_use_the_focused_image_else_the_first_selected_image_and_ignore_folders_and_files()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            await ShowAllFilesAsync(h);
            var a = Path.Combine(h.Root, "a.png");
            var c = Path.Combine(h.Root, "c.png");

            // Folder + file + two images, focus on c.png (the last item clicked with Ctrl): the focused image wins.
            h.Click("Alpha");
            h.Click("notes.txt", modifiers: RawInputModifiers.Control);
            h.Click("a.png", modifiers: RawInputModifiers.Control);
            h.Click("c.png", modifiers: RawInputModifiers.Control);
            Assert.Equal(4, h.SelectedNames.Count);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Equal(new[] { c }, launches.Select(l => l.Path!).ToArray());

            // Focus on a folder (not an image): the first selected image in list order is used.
            launches.Clear();
            h.Click(h.EmptySpotInList());
            h.Click("a.png");
            h.Click("c.png", modifiers: RawInputModifiers.Control);
            h.Click("Beta", modifiers: RawInputModifiers.Control);
            Assert.Equal(3, h.SelectedNames.Count);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Equal(new[] { a }, launches.Select(l => l.Path!).ToArray());

            // One image among selected folders / files still counts as THE selected image.
            launches.Clear();
            h.Click(h.EmptySpotInList());
            h.Click("Alpha");
            h.Click("c.png", modifiers: RawInputModifiers.Control);
            h.Click("notes.txt", modifiers: RawInputModifiers.Control);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Equal(new[] { c }, launches.Select(l => l.Path!).ToArray());
        }, Tree);
    }

    [Fact]
    public async Task Image_tab_still_opens_its_current_image()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            await OpenImageFromExplorerAsync(h, "a.png");
            h.Key(Key.D2, RawInputModifiers.Shift);
            Assert.Equal(new[] { (Editor2, Path.Combine(h.Root, "a.png")) }, launches.Select(l => (l.Program!, l.Path!)).ToArray());
        });
    }

    [Fact]
    public async Task Home_tab_does_nothing_even_after_an_image_was_open()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            await OpenImageFromExplorerAsync(h, "a.png");
            h.Workspace.AddHome();
            h.Call("RebuildTabStrip");
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            h.Pump();
            Assert.IsType<HomeTabState>(h.Workspace.Active);
            h.Key(Key.D1, RawInputModifiers.Shift);
            await h.Window.ExecuteActionAsync(AppActionRegistry.Find("external.1")!, "titlebar");
            Assert.Empty(launches);
        });
    }

    [Fact]
    public async Task Switching_from_an_image_tab_to_an_Explorer_tab_with_nothing_selected_does_not_fall_back_to_the_last_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            await OpenImageFromExplorerAsync(h, "a.png");
            await NewExplorerTabAsync(h);
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            Assert.Empty(h.SelectedNames);
            h.Key(Key.D1, RawInputModifiers.Shift);
            h.Key(Key.D2, RawInputModifiers.Shift);
            h.Key(Key.D3, RawInputModifiers.Shift);
            Assert.Empty(launches);
            Assert.Equal(SelectImageFirst, Msg(h));
        });
    }

    [Fact]
    public async Task A_previously_opened_or_selected_image_is_never_remembered_once_the_selection_is_empty()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            // Selected image first launches, then the selection is cleared: no fallback to the earlier selection.
            h.Click("c.png");
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Single(launches);
            launches.Clear();
            h.Click(h.EmptySpotInList());
            Assert.Empty(h.SelectedNames);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Empty(launches);
            Assert.Equal(SelectImageFirst, Msg(h));

            // An image opened from a tab (current image, history and recent list know it) is not a candidate either.
            await OpenImageFromExplorerAsync(h, "a.png");
            await NewExplorerTabAsync(h);
            h.Click(h.EmptySpotInList());
            Assert.Empty(h.SelectedNames);
            h.Key(Key.D3, RawInputModifiers.Shift);
            Assert.Empty(launches);

            // And a second Explorer tab never borrows the selection kept by another Explorer tab's view.
            h.Click("b.png");
            await NewExplorerTabAsync(h);
            Assert.Empty(h.SelectedNames);
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Empty(launches);
        });
    }

    [Fact]
    public async Task Gesture_and_title_bar_routes_follow_the_same_rule()
    {
        await ExplorerHarness.Run(async h =>
        {
            var launches = Record(h);
            var action = AppActionRegistry.Find("external.2")!;

            await h.Window.ExecuteActionAsync(action, "titlebar");
            await (Task)h.Call("ExecuteGestureActionAsync", "external.2", 0, null)!;
            Assert.Empty(launches);

            h.Click("a.png");
            await h.Window.ExecuteActionAsync(action, "titlebar");
            await (Task)h.Call("ExecuteGestureActionAsync", "external.2", 0, null)!;
            var a = Path.Combine(h.Root, "a.png");
            Assert.Equal(new[] { (Editor2, a), (Editor2, a) }, launches.Select(l => (l.Program!, l.Path!)).ToArray());
        });
    }

    [Fact]
    public async Task A_rebound_key_follows_the_same_rule()
    {
        await ExplorerHarness.Run(h =>
        {
            var launches = Record(h);
            h.Settings.Hotkeys["external.1"] = new List<string> { "F9" };
            h.Key(Key.F9);
            Assert.Empty(launches);
            Assert.Equal(SelectImageFirst, Msg(h));

            h.Click("c.png");
            h.Key(Key.F9);
            Assert.Equal(new[] { Path.Combine(h.Root, "c.png") }, launches.Select(l => l.Path!).ToArray());

            // The old default key no longer launches.
            launches.Clear();
            h.Key(Key.D1, RawInputModifiers.Shift);
            Assert.Empty(launches);
            return Task.CompletedTask;
        });
    }
}
