using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Issue #4 end to end in the real, themed MainWindow with simulated input and a temp folder: F2 on several items opens the
/// batch rename dialog, and the tag sidebar adds and removes tags in the names of the selected files (Explorer) or the picture
/// on screen (viewer). Nothing outside the harness's temp folder is touched.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerBatchRenameAndTagsTests
{
    private static void SelectAandB(ExplorerHarness h)
    {
        h.Click("a.png");
        h.Click("b.png", modifiers: RawInputModifiers.Control);
        Assert.Equal(new[] { "a.png", "b.png" }, h.SelectedNames);
    }

    private static void EnableTags(ExplorerHarness h, string groups = "+Family: mom, dad\nFriends: anna")
    {
        h.Settings.TagsEnabled = true;
        h.Settings.TagGroups = groups;
    }

    private static Border Host(ExplorerHarness h) => h.Window.FindControl<Border>("TagSidebarHost")!;

    private static TagSidebar Sidebar(ExplorerHarness h) => (TagSidebar)Host(h).Child!;

    private static CheckBox Tag(ExplorerHarness h, string tag) => Sidebar(h).TagBoxes.Single(b => (string?)b.Tag == tag);

    private static void ClickTag(ExplorerHarness h, string tag)
    {
        Tag(h, tag).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.Pump();
    }

    // ------------------------------------------------------------------ batch rename

    [Fact]
    public async Task F2_with_several_items_selected_opens_the_batch_rename_dialog_and_renames_all_of_them()
    {
        await ExplorerHarness.Run(async h =>
        {
            SelectAandB(h);
            h.FocusList();
            h.Key(Key.F2);
            await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "batch rename dialog opened");
            Assert.False(h.Get<string?>("_explorerRenamePath") is not null);   // no inline editor
            var dialog = h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
            Assert.Equal("Rename 2 pictures", dialog.Title);

            dialog.Editor.Template = "Trip*";                 // the hidden old pattern fallback still reads this
            h.Pump();
            Assert.Equal(["Tripa.png", "Tripb.png"], dialog.CurrentPlan.Items.Select(i => i.NewName));
            Assert.True(dialog.RenameButton.IsEnabled);
            await dialog.RenameForTestsAsync();

            await h.WaitUntil(() => h.Names.Contains("Tripa.png") && h.Names.Contains("Tripb.png"), "files renamed and listed");
            Assert.True(File.Exists(Path.Combine(h.Root, "Tripa.png")));
            Assert.False(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.True(File.Exists(Path.Combine(h.Root, "c.png")));                 // untouched
            await h.WaitUntil(() => h.SelectedNames.SequenceEqual(new[] { "Tripa.png", "Tripb.png" }), "the renamed items stay selected");
        });
    }

    [Fact]
    public async Task Undo_last_rename_puts_the_names_back_selects_them_and_is_only_one_level_deep()
    {
        await ExplorerHarness.Run(async h =>
        {
            async Task<BatchRenameDialog> OpenDialogAsync()
            {
                h.FocusList();
                h.Key(Key.F2);
                await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "batch rename dialog opened");
                return h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
            }

            SelectAandB(h);
            var first = await OpenDialogAsync();
            Assert.False(first.UndoButton.IsEnabled);                                 // nothing renamed yet this session
            first.Editor.Template = "Trip{name}";
            h.Pump();
            await first.RenameForTestsAsync();
            await h.WaitUntil(() => h.Names.Contains("Tripa.png") && h.Names.Contains("Tripb.png"), "renamed");
            await h.WaitUntil(() => !h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "dialog closed");
            Assert.IsType<BrowserTabState>(h.Workspace.Active);                       // the tab does not turn into the viewer

            await h.WaitUntil(() => h.SelectedNames.SequenceEqual(new[] { "Tripa.png", "Tripb.png" }), "the renamed items stay selected");
            var second = await OpenDialogAsync();
            Assert.True(second.UndoButton.IsEnabled);                                 // the last batch can be turned around
            await second.UndoForTestsAsync();
            await h.WaitUntil(() => h.Names.Contains("a.png") && h.Names.Contains("b.png") && !h.Names.Contains("Tripa.png"), "names are back");
            await h.WaitUntil(() => !h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "dialog closed after the undo");
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.False(File.Exists(Path.Combine(h.Root, "Tripa.png")));
            Assert.IsType<BrowserTabState>(h.Workspace.Active);

            await h.WaitUntil(() => h.SelectedNames.SequenceEqual(new[] { "a.png", "b.png" }), "the restored items are selected");
            var third = await OpenDialogAsync();
            Assert.False(third.UndoButton.IsEnabled);                                 // one level: nothing left to undo
            third.Close(null);
            h.Pump();
        });
    }

    [Fact]
    public async Task A_conflict_in_the_dialog_disables_Rename_and_nothing_changes_on_disk()
    {
        await ExplorerHarness.Run(async h =>
        {
            SelectAandB(h);
            h.FocusList();
            h.Key(Key.F2);
            await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "dialog opened");
            var dialog = h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
            dialog.Editor.Template = "a/c";    // a.png would become c.png, which already exists
            h.Pump();
            Assert.False(dialog.RenameButton.IsEnabled);
            Assert.Contains("1 problem", dialog.StatusText.Text);
            Assert.Contains("already a file or folder", dialog.CurrentPlan.Items.Single(i => i.HasProblem).Message);
            dialog.Close(null);
            h.Pump();
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.Equal(new[] { "a.png", "b.png" }, h.SelectedNames);
        });
    }

    [Fact]
    public async Task F2_on_one_item_is_still_the_inline_rename()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.FocusList();
            h.Key(Key.F2);
            await h.WaitUntil(() => h.Get<string?>("_explorerRenamePath") is not null, "inline rename started");
            Assert.Empty(h.Window.OwnedWindows.OfType<BatchRenameDialog>());
        });
    }

    // ------------------------------------------------------------------ tags

    [Fact]
    public async Task With_tags_off_the_action_says_where_to_turn_them_on_and_shows_no_sidebar()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.False(h.Settings.TagsEnabled);   // off by default
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            h.Pump();
            Assert.False(Host(h).IsVisible);
            Assert.Contains("Enable tags", h.Surface.MessageText.Text);
        });
    }

    [Fact]
    public async Task The_sidebar_tags_the_selected_files_re_sorts_and_removes_and_the_filter_finds_them()
    {
        await ExplorerHarness.Run(async h =>
        {
            EnableTags(h);
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            h.Pump();
            Assert.True(Host(h).IsVisible);
            Assert.Equal(TagSidebar.PanelWidth, h.BrowserView.Margin.Right);   // the list makes room beside it

            SelectAandB(h);
            Assert.Equal("2 files selected", Sidebar(h).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TagSidebarCaption").Text);
            Assert.False(Tag(h, "mom").IsChecked);

            ClickTag(h, "dad");
            await h.WaitUntil(() => h.Names.Contains("a {Family;dad}.png") && h.Names.Contains("b {Family;dad}.png"), "dad added (with the group tag)");
            await h.WaitUntil(() => h.SelectedNames.Count == 2, "selection kept");
            await h.WaitUntil(() => Tag(h, "dad").IsChecked == true, "the box follows the new names");

            ClickTag(h, "mom");   // earlier in the list than dad: re-sorted in front of it
            await h.WaitUntil(() => h.Names.Contains("a {Family;mom;dad}.png"), "mom inserted before dad");

            // The filter box already finds tags: they are part of the name.
            h.Surface.FilterBox.Text = "mom";
            await h.WaitUntil(() => h.Names.SequenceEqual(new[] { "a {Family;mom;dad}.png", "b {Family;mom;dad}.png" }), "filter by tag");
            h.Surface.FilterBox.Text = "";
            await h.WaitUntil(() => h.Names.Contains("c.png"), "filter cleared");
            SelectAandB2(h);

            ClickTag(h, "mom");   // on every selected file: clicking removes it
            await h.WaitUntil(() => h.Names.Contains("a {Family;dad}.png"), "mom removed");
            ClickTag(h, "dad");
            await h.WaitUntil(() => h.Names.Contains("a.png") && h.Names.Contains("b.png"), "last tag took the group tag with it");
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")));

            Sidebar(h).CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            h.Pump();
            Assert.False(Host(h).IsVisible);
            Assert.Equal(0, h.BrowserView.Margin.Right);
        });
    }

    /// <summary>Selects the two tagged files again by their current names.</summary>
    private static void SelectAandB2(ExplorerHarness h)
    {
        var first = h.Names.First(n => n.StartsWith("a ", StringComparison.Ordinal) || n == "a.png");
        var second = h.Names.First(n => n.StartsWith("b ", StringComparison.Ordinal) || n == "b.png");
        h.Click(first);
        h.Click(second, modifiers: RawInputModifiers.Control);
    }

    [Fact]
    public async Task A_clash_with_an_existing_file_is_reported_and_nothing_is_renamed()
    {
        await ExplorerHarness.Run(async h =>
        {
            File.WriteAllBytes(Path.Combine(h.Root, "a {mom}.png"), ExplorerHarness.Png(8, 8, 1, 2, 3));
            EnableTags(h, "Family: mom");
            h.Window.GetType().GetMethod("RefreshManagedBrowser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(h.Window, [h.Root]);
            await h.RefreshTask;
            await h.SettleAsync();
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            h.Click("a.png");
            Assert.Equal(new[] { "a.png" }, h.SelectedNames);
            ClickTag(h, "mom");
            await h.WaitUntil(() => !string.IsNullOrEmpty(h.Surface.MessageText.Text), "a message");
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.Contains("already a file or folder", h.Surface.MessageText.Text);
        });
    }

    [Fact]
    public async Task In_a_picture_tab_the_sidebar_tags_the_picture_on_screen()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), "a.png", StringComparison.OrdinalIgnoreCase), "image loaded");
            await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
            EnableTags(h);   // after the tab opened, so the toggle reads the live settings
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            h.Pump();
            Assert.True(Host(h).IsVisible, $"host hidden; tags={h.Settings.TagsEnabled} active={h.Workspace.Active?.Kind} imageView={h.Window.FindControl<Grid>("ImageView")!.IsVisible} wanted={h.Get<bool>("_tagSidebarDismissed")} msg={h.Window.FindControl<TextBlock>("StatusStatsText")?.Text}");
            Assert.Equal(TagSidebar.PanelWidth, h.Window.FindControl<Grid>("ImageView")!.Margin.Right);

            ClickTag(h, "anna");
            await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), "a {anna}.png", StringComparison.OrdinalIgnoreCase), "the picture was renamed and reopened");
            Assert.True(File.Exists(Path.Combine(h.Root, "a {anna}.png")));
            Assert.False(File.Exists(Path.Combine(h.Root, "a.png")));
            await h.WaitUntil(() => Tag(h, "anna").IsChecked == true, "the box shows the picture's tag");
        });
    }
    // ------------------------------------------------------------------ 5.1.7 UI test bug 4: the sidebar is reachable

    private static IEnumerable<string> MenuHeaders(ContextMenu menu) =>
        ((System.Collections.IEnumerable)menu.ItemsSource!).OfType<MenuItem>().SelectMany(Headers);

    private static IEnumerable<string> Headers(MenuItem item) =>
        new[] { item.Header?.ToString() ?? "" }.Concat(item.ItemsSource is System.Collections.IEnumerable children ? children.OfType<MenuItem>().SelectMany(Headers) : []);

    [Fact]
    public async Task With_tags_on_the_sidebar_appears_by_itself_in_an_Explorer_tab_and_the_toggle_hides_and_shows_it()
    {
        await ExplorerHarness.Run(async h =>
        {
            EnableTags(h);
            h.Call("UpdateTagSidebar");   // what applying the Enable tags setting does
            h.Pump();
            Assert.True(Host(h).IsVisible, "tags on -> the sidebar shows without any action");
            Assert.Equal(TagSidebar.PanelWidth, h.BrowserView.Margin.Right);

            await (Task)h.Call("ToggleTagSidebarAsync")!;
            Assert.False(Host(h).IsVisible);
            Assert.Equal(0, h.BrowserView.Margin.Right);
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            Assert.True(Host(h).IsVisible);

            // Closed (as the sidebar's own close button does): it stays closed while settings are applied, until tags go off and on.
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            h.Call("UpdateTagSidebar");
            Assert.False(Host(h).IsVisible, "a closed sidebar stays closed when settings are applied");
            h.Settings.TagsEnabled = false;
            h.Call("UpdateTagSidebar");
            h.Settings.TagsEnabled = true;
            h.Call("UpdateTagSidebar");
            Assert.True(Host(h).IsVisible, "switching tags off and on brings it back");
        });
    }

    [Fact]
    public async Task With_tags_on_the_sidebar_appears_by_itself_in_a_picture_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
            await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
            Assert.False(Host(h).IsVisible, "tags are off by default");
            EnableTags(h);
            h.Call("UpdateTagSidebar");
            h.Pump();
            Assert.True(Host(h).IsVisible);
            Assert.Equal(TagSidebar.PanelWidth, h.Window.FindControl<Grid>("ImageView")!.Margin.Right);
        });
    }

    [Fact]
    public async Task The_Explorer_and_viewer_context_menus_offer_the_sidebar_only_while_tags_are_on()
    {
        await ExplorerHarness.Run(async h =>
        {
            var entry = h.Entries.First(e => e.Name == "a.png");
            static bool Offers(object? menu) => MenuHeaders((ContextMenu)menu!).Any(x => x.Contains("tag sidebar", StringComparison.OrdinalIgnoreCase));
            Assert.False(Offers(h.Call("BuildExplorerContextMenu", entry)), "tags off: no entry");
            Assert.False(Offers(h.Call("BuildExplorerContextMenu", new object?[] { null })), "tags off: no entry on the background");

            EnableTags(h);
            h.Call("UpdateTagSidebar");
            Assert.True(Offers(h.Call("BuildExplorerContextMenu", entry)), "one item");
            Assert.True(Offers(h.Call("BuildExplorerContextMenu", new object?[] { null })), "empty background");
            Assert.Contains(MenuHeaders((ContextMenu)h.Call("BuildExplorerContextMenu", entry)!), x => x.StartsWith("Hide the tag sidebar", StringComparison.Ordinal));
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            Assert.Contains(MenuHeaders((ContextMenu)h.Call("BuildExplorerContextMenu", entry)!), x => x.StartsWith("Show the tag sidebar", StringComparison.Ordinal));

            h.DoubleClick("a.png");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "image opened in the tab");
            await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
            Assert.True(Offers(h.Call("BuildViewerContextMenu")), "the picture's menu");
            h.Settings.TagsEnabled = false;
            Assert.False(Offers(h.Call("BuildViewerContextMenu")), "tags off: not in the picture's menu");
        });
    }
}
