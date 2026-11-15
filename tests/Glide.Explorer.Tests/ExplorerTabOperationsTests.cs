using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Glide.App.Explorer;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// Context menus, clipboard, rename, delete, new folder, properties and drag and drop of the Explorer tab, run on
/// generated temp folders. Nothing touches the real Recycle Bin, clipboard, dialogs or explorer.exe (seams).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
public sealed class ExplorerTabOperationsTests
{
    public ExplorerTabOperationsTests() { }

    private static void Raise(MenuItem item) => item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    private static List<string> Headers(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToList();

    private static MenuItem Item(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().First(i => string.Equals(i.Header?.ToString(), header, StringComparison.Ordinal));

    // ---- context menu structure -------------------------------------------------------------------

    [Fact]
    public async Task Right_click_on_an_image_selects_it_and_offers_the_Explorer_item_menu()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("b.png", MouseButton.Right);
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);
            var menu = h.ActiveMenu;
            Assert.NotNull(menu);
            Assert.True(menu!.IsOpen);
            Assert.Equal(new[]
            {
                "Open", "Open in new tab", "Open with…", "Show in Windows File Explorer", "-",
                "Cut", "Copy", "Copy path", "Paste", "-", "Rename", "Delete", "Print…", "-", "Properties"
            }, Headers(menu));
            Assert.False(Item(menu, "Paste").IsEnabled, "nothing on the clipboard yet");
            Assert.True(Item(menu, "Cut").IsEnabled);
            // Shortcuts are displayed.
            Assert.Equal("F2", Item(menu, "Rename").InputGesture?.ToString());
            Assert.Equal("Delete", Item(menu, "Delete").InputGesture?.ToString());
            // The generic Window-in-Window menu is not what appears here.
            Assert.DoesNotContain(Headers(menu), x => x.Contains("Overlay", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Right_click_on_a_folder_offers_Open_folder_new_Explorer_tab_and_paste_into_folder()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("Alpha", MouseButton.Right);
            var menu = h.ActiveMenu!;
            Assert.Equal(new[]
            {
                "Open folder", "Open in new Explorer tab", "Show in Windows File Explorer", "-",
                "Cut", "Copy", "Copy path", "Paste into folder", "-", "Rename", "Delete", "-", "Properties"
            }, Headers(menu));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Right_click_on_a_multi_selection_offers_actions_that_apply_to_all_items()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("Alpha");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            h.Click("b.png", MouseButton.Right); // right-click inside the selection keeps it
            Assert.Equal(new[] { "Alpha", "b.png" }, h.SelectedNames);
            var menu = h.ActiveMenu!;
            Assert.Equal(new[]
            {
                "Open all 2 in new tabs", "-", "Cut", "Copy", "Copy path", "-", "Delete 2 items", "-", "Properties"
            }, Headers(menu));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Right_click_on_empty_space_offers_View_Sort_Refresh_Paste_New_folder_Select_all_Properties()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("a.png");
            h.Click(h.EmptySpotInList(), MouseButton.Right);
            Assert.Empty(h.SelectedNames); // right-clicking the background deselects
            var menu = h.ActiveMenu!;
            Assert.Equal(new[]
            {
                "View", "Sort by", "Refresh", "-", "Paste", "-", "New folder", "Select all", "-",
                "Show in Windows File Explorer", "Properties"
            }, Headers(menu));
            var view = Item(menu, "View").Items.OfType<MenuItem>().Select(i => i.Header!.ToString()).ToArray();
            Assert.Equal(new[] { "Extra large thumbnails", "Large thumbnails  ✓", "Medium thumbnails", "Small thumbnails", "List", "Details" }, view);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Menu_key_and_Shift_F10_open_the_context_menu_for_the_current_item()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("Beta");
            h.Key(Key.F10, RawInputModifiers.Shift);
            Assert.NotNull(h.ActiveMenu);
            Assert.Equal("Open folder", Headers(h.ActiveMenu!)[0]);
            h.Key(Key.Escape);
            return Task.CompletedTask;
        });
    }

    // ---- context menu actions ---------------------------------------------------------------------

    [Fact]
    public async Task Item_menu_actions_open_reveal_open_with_copy_path_and_properties_use_the_seams()
    {
        await ExplorerHarness.Run(async h =>
        {
            var a = Path.Combine(h.Root, "a.png");
            h.Click("a.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Open with…"));
            Assert.Equal(new[] { a }, h.OpenWithCalls);

            h.Click("a.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Show in Windows File Explorer"));
            Assert.Equal(new[] { (a, false) }, h.Revealed.ToArray());

            h.Click("a.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Copy path"));
            await Task.Delay(20);
            Assert.Equal(a, ExplorerHostLastCopied());

            h.Click("a.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Properties"));
            Assert.Equal(new[] { a }, h.Properties);

            h.Click("Gamma", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Show in Windows File Explorer"));
            Assert.Equal((Path.Combine(h.Root, "Gamma"), true), h.Revealed.Last());
        });
    }

    private static string? ExplorerHostLastCopied() => ExplorerHost.LastCopiedText;

    [Fact]
    public async Task Item_menu_Open_and_Open_in_new_Explorer_tab_for_a_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Gamma", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Open in new Explorer tab"));
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<BrowserTabState>().Count() == 2, "new explorer tab");
            Assert.Equal(Path.Combine(h.Root, "Gamma"), h.Workspace.Tabs.OfType<BrowserTabState>().Last().Folder);
            h.Click("Beta", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Open folder"));
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Beta"), h.CurrentFolder);
        });
    }

    [Fact]
    public async Task Item_menu_Open_in_new_tab_and_Open_for_an_image_follow_the_gesture_bindings()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Open in new tab"));
            await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(), "background image tab");
            Assert.IsType<BrowserTabState>(h.Workspace.Active);
            h.Click("c.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Open"));
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "opened in current tab");
        });
    }

    [Fact]
    public async Task Menu_Select_all_Refresh_and_View_choices_work()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click(h.EmptySpotInList(), MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Select all"));
            Assert.Equal(6, h.SelectionSet.Count);

            h.Click(h.EmptySpotInList(), MouseButton.Right);
            var details = Item(h.ActiveMenu!, "View").Items.OfType<MenuItem>().First(i => i.Header!.ToString() == "Details");
            Raise(details);
            await h.SettleAsync();
            Assert.Equal(ExplorerViewModes.Details, h.Surface.ViewModeCombo.SelectedIndex);

            File.WriteAllBytes(Path.Combine(h.Root, "later.png"), ExplorerHarness.Png(8, 8, 1, 2, 3));
            h.Click(h.EmptySpotInList(), MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Refresh"));
            await h.SettleAsync();
            Assert.Contains("later.png", h.Names);
            h.Click(h.EmptySpotInList(), MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Properties"));
            Assert.Equal(new[] { h.Root }, h.Properties);
        });
    }

    // ---- clipboard: copy / cut / paste ------------------------------------------------------------

    [Fact]
    public async Task Ctrl_C_then_Ctrl_V_copies_a_file_into_another_folder_and_a_second_paste_makes_a_Copy_name()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Key(Key.C, RawInputModifiers.Control);
            var content = h.Clipboard.TryGet()!;
            Assert.Equal(new[] { Path.Combine(h.Root, "a.png") }, content.Paths);
            Assert.False(content.Cut);

            await h.NavigateAsync(Path.Combine(h.Root, "Beta"));
            h.FocusList();
            h.Key(Key.V, RawInputModifiers.Control);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Beta", "a.png")), "pasted copy");
            await h.SettleAsync();
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")), "copy leaves the original");
            Assert.Equal(new[] { "a.png" }, h.Names);
            Assert.Equal(new[] { "a.png" }, h.SelectedNames); // the pasted item is selected

            h.Key(Key.V, RawInputModifiers.Control); // same name again: auto "- Copy"
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Beta", "a - Copy.png")), "second paste");
            await h.SettleAsync();
            Assert.Equal(new[] { "a - Copy.png", "a.png" }, h.Names.OrderBy(n => n).ToArray());
            // The copy is byte-identical.
            Assert.Equal(File.ReadAllBytes(Path.Combine(h.Root, "a.png")), File.ReadAllBytes(Path.Combine(h.Root, "Beta", "a - Copy.png")));
        });
    }

    [Fact]
    public async Task Ctrl_X_then_Ctrl_V_moves_files_marks_cut_items_and_empties_the_clipboard()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("b.png");
            h.Click("c.png", modifiers: RawInputModifiers.Control);
            h.Key(Key.X, RawInputModifiers.Control);
            Assert.True(h.Clipboard.TryGet()!.Cut);
            Assert.Contains("cut", h.Tile("b.png")!.Classes);
            Assert.Contains("cut", h.Tile("c.png")!.Classes);
            Assert.DoesNotContain("cut", h.Tile("a.png")!.Classes);

            await h.NavigateAsync(Path.Combine(h.Root, "Gamma"));
            h.FocusList();
            h.Key(Key.V, RawInputModifiers.Control);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Gamma", "c.png")), "moved");
            await h.SettleAsync();
            Assert.False(File.Exists(Path.Combine(h.Root, "b.png")));
            Assert.False(File.Exists(Path.Combine(h.Root, "c.png")));
            Assert.Equal(new[] { "b.png", "c.png" }, h.Names.OrderBy(n => n).ToArray());
            Assert.Null(h.Clipboard.TryGet()); // a cut can only be pasted once
        });
    }

    [Fact]
    public async Task Pasting_into_a_folder_tile_via_the_menu_targets_that_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Key(Key.C, RawInputModifiers.Control);
            h.Click("Gamma", MouseButton.Right);
            var paste = Item(h.ActiveMenu!, "Paste into folder");
            Assert.True(paste.IsEnabled);
            Raise(paste);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Gamma", "a.png")), "pasted into Gamma");
            Assert.Equal(h.Root, h.CurrentFolder); // the view did not navigate
        });
    }

    [Fact]
    public async Task Paste_with_an_empty_clipboard_reports_it_and_a_vanished_source_is_reported_as_an_error()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.FocusList();
            h.Key(Key.V, RawInputModifiers.Control);
            await h.SettleAsync();
            Assert.Equal("There is nothing to paste.", h.Surface.MessageText.Text);

            h.Clipboard.TrySet(new[] { Path.Combine(h.Root, "ghost.png") }, cut: false);
            h.Key(Key.V, RawInputModifiers.Control);
            await h.WaitUntil(() => h.Errors.Count > 0, "error reported");
            Assert.Contains("ghost.png", h.Errors[0].Message);
            Assert.StartsWith("Copied 0", h.Surface.MessageText.Text);
        });
    }

    // ---- rename -----------------------------------------------------------------------------------

    private static TextBox RenameBox(ExplorerHarness h)
    {
        // The inline editor is attached when the tile is prepared (a queued UI job): give it a moment.
        for (var i = 0; i < 60; i++)
        {
            h.Pump();
            var box = h.Surface.Repeater.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "RenameBox");
            if (box is not null) return box;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("no inline rename editor appeared; renamePath=" + h.Get<string?>("_explorerRenamePath") + " pending=" + h.Get<string?>("_explorerPendingRename") + " names=" + string.Join(",", h.Names) + " msg=" + h.Surface.MessageText.Text);
    }

    [Fact]
    public async Task F2_renames_inline_Enter_commits_and_the_file_on_disk_follows()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("b.png");
            h.Key(Key.F2);
            var box = RenameBox(h);
            Assert.Equal("b.png", box.Text);
            Assert.Equal(0, box.SelectionStart);
            Assert.Equal("b".Length, box.SelectionEnd); // the base name is selected, not the extension
            box.Text = "renamed.png";
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.False(File.Exists(Path.Combine(h.Root, "b.png")));
            Assert.True(File.Exists(Path.Combine(h.Root, "renamed.png")));
            Assert.Contains("renamed.png", h.Names);
            Assert.Equal(new[] { "renamed.png" }, h.SelectedNames);
            Assert.Empty(h.Surface.Repeater.GetVisualDescendants().OfType<TextBox>());
        });
    }

    [Fact]
    public async Task Rename_validates_invalid_characters_and_name_clashes_and_Esc_cancels()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("b.png");
            h.Key(Key.F2);
            var box = RenameBox(h);
            box.Text = "bad:name?.png";
            h.Key(Key.Enter);
            await h.SettleAsync();
            // Still editing, error shown, nothing renamed.
            Assert.NotNull(RenameBox(h));
            Assert.Contains("can't contain", h.Surface.MessageText.Text);
            Assert.True(File.Exists(Path.Combine(h.Root, "b.png")));

            box.Text = "c.png"; // clashes with an existing file
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.NotNull(RenameBox(h));
            Assert.Contains("already a file or folder", h.Surface.MessageText.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(h.Root, "b.png")));
            Assert.True(File.Exists(Path.Combine(h.Root, "c.png")));

            h.Key(Key.Escape);
            await h.SettleAsync();
            Assert.Empty(h.Surface.Repeater.GetVisualDescendants().OfType<TextBox>());
            Assert.True(File.Exists(Path.Combine(h.Root, "b.png")));
        });
    }

    [Fact]
    public async Task Rename_works_for_folders_and_through_the_context_menu_and_clicking_away_commits()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Gamma", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Rename"));
            var box = RenameBox(h);
            Assert.Equal(0, box.SelectionStart);
            Assert.Equal("Gamma".Length, box.SelectionEnd);
            box.Text = "Delta";
            // Clicking on empty space commits (Explorer behaviour).
            h.Click(h.EmptySpotInList());
            await h.SettleAsync();
            Assert.True(Directory.Exists(Path.Combine(h.Root, "Delta")));
            Assert.False(Directory.Exists(Path.Combine(h.Root, "Gamma")));
            Assert.Contains("Delta", h.Names);
        });
    }

    // ---- new folder -------------------------------------------------------------------------------

    [Fact]
    public async Task Ctrl_Shift_N_creates_a_folder_and_starts_renaming_it_immediately()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.FocusList();
            h.Key(Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.True(Directory.Exists(Path.Combine(h.Root, "New folder")));
            var box = RenameBox(h);
            Assert.Equal("New folder", box.Text);
            Assert.Equal("New folder".Length, box.SelectionEnd);
            box.Text = "Holiday";
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.True(Directory.Exists(Path.Combine(h.Root, "Holiday")));
            Assert.False(Directory.Exists(Path.Combine(h.Root, "New folder")));
            Assert.Equal(new[] { "Holiday" }, h.SelectedNames);

            // The menu entry does the same; names are numbered.
            h.Click(h.EmptySpotInList(), MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "New folder"));
            await h.SettleAsync();
            Assert.True(Directory.Exists(Path.Combine(h.Root, "New folder")));
            h.Key(Key.Escape); // keep the default name
            h.FocusList();
            h.Key(Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.True(Directory.Exists(Path.Combine(h.Root, "New folder (2)")));
        });
    }

    // ---- delete -----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_key_asks_then_sends_to_the_Recycle_Bin_and_selects_the_next_item()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("b.png");
            h.Key(Key.Delete);
            await h.SettleAsync();
            Assert.Single(h.Confirmations);
            Assert.Equal("Delete", h.Confirmations[0].Title);
            Assert.Contains("'b.png'", h.Confirmations[0].Message);
            Assert.Contains("Recycle Bin", h.Confirmations[0].Message);
            Assert.Equal(new[] { (Path.Combine(h.Root, "b.png"), false) }, h.Recycled.ToArray());
            Assert.False(File.Exists(Path.Combine(h.Root, "b.png")));
            Assert.DoesNotContain("b.png", h.Names);
            Assert.Equal(new[] { "c.png" }, h.SelectedNames); // the item that moved into its place
        });
    }

    [Fact]
    public async Task Declining_the_delete_confirmation_deletes_nothing()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.ConfirmAnswer = false;
            h.Click("a.png");
            h.Key(Key.Delete);
            await h.SettleAsync();
            Assert.Single(h.Confirmations);
            Assert.Empty(h.Recycled);
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.Contains("a.png", h.Names);
        });
    }

    [Fact]
    public async Task Shift_Delete_deletes_permanently_after_a_stronger_confirmation_and_skips_the_Recycle_Bin()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("Alpha");
            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.Single(h.Confirmations);
            Assert.Equal("Delete permanently", h.Confirmations[0].Title);
            Assert.Contains("can't be undone", h.Confirmations[0].Message);
            Assert.Contains("NOT go to the Recycle Bin", h.Confirmations[0].Message);
            Assert.Equal("Delete permanently", h.Confirmations[0].Accept);
            Assert.Empty(h.Recycled);
            Assert.False(Directory.Exists(Path.Combine(h.Root, "Alpha"))); // folder trees go too
            Assert.DoesNotContain("Alpha", h.Names);
        });
    }

    [Fact]
    public async Task Deleting_a_multi_selection_via_the_menu_applies_to_all_items()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click("a.png");
            h.Click("c.png", modifiers: RawInputModifiers.Control);
            h.Click("c.png", MouseButton.Right);
            Raise(Item(h.ActiveMenu!, "Delete 2 items"));
            await h.WaitUntil(() => h.Recycled.Count == 2, "both recycled");
            Assert.Contains("these 2 items", h.Confirmations[0].Message);
            await h.SettleAsync();
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma", "b.png" }, h.Names);
        });
    }

    [Fact]
    public async Task A_failing_delete_is_reported_to_the_user_and_the_item_stays()
    {
        await ExplorerHarness.Run(async h =>
        {
            ExplorerFileOperations.Recycler = (_, _) => false; // the Recycle Bin refuses
            h.Click("a.png");
            h.Key(Key.Delete);
            await h.WaitUntil(() => h.Errors.Count > 0, "error dialog");
            Assert.Contains("a.png", h.Errors[0].Message);
            Assert.True(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.StartsWith("Deleted 0", h.Surface.MessageText.Text);
        });
    }

    // ---- keyboard odds and ends --------------------------------------------------------------------

    [Fact]
    public async Task Alt_Enter_shows_properties_and_Enter_on_a_non_image_file_uses_the_Windows_default_program()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.ExplorerShowOnlyImages = false;
            h.Key(Key.F5);
            await h.SettleAsync();
            Assert.Contains("notes.txt", h.Names);
            // Non-image files are styled as files (file glyph), not as images.
            var glyph = h.Tile("notes.txt")!.GetVisualDescendants().OfType<Glide.App.Controls.GlideIconView>().First();
            Assert.Equal("FileGeneric", glyph.Kind);
            h.Click("notes.txt");
            h.Key(Key.Enter, RawInputModifiers.Alt);
            Assert.Equal(new[] { Path.Combine(h.Root, "notes.txt") }, h.Properties);
            h.Key(Key.Enter);
            await h.SettleAsync();
            Assert.Equal(new[] { Path.Combine(h.Root, "notes.txt") }, h.ShellOpened);
            Assert.IsType<BrowserTabState>(h.Workspace.Active); // the Explorer stays
        });
    }

    // ---- drag and drop ----------------------------------------------------------------------------

    private static DataTransfer DragData(params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            var item = new DataTransferItem();
            item.Set(DataFormat.CreateStringApplicationFormat(ExplorerHost.DragFormatName), path);
            data.Add(item);
        }
        return data;
    }

    private static void Drop(ExplorerHarness h, Point point, DataTransfer data, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        h.Window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Move | DragDropEffects.Copy, modifiers);
        h.Window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Move | DragDropEffects.Copy, modifiers);
        h.Window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Move | DragDropEffects.Copy, modifiers);
        h.Pump();
    }

    [Fact]
    public async Task Dropping_Explorer_items_on_a_folder_tile_moves_them_and_Ctrl_copies()
    {
        await ExplorerHarness.Run(async h =>
        {
            var gamma = h.TileCenter("Gamma");
            Drop(h, gamma, DragData(Path.Combine(h.Root, "a.png")));
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Gamma", "a.png")), "moved by drop");
            await h.SettleAsync();
            Assert.False(File.Exists(Path.Combine(h.Root, "a.png")));
            Assert.DoesNotContain("a.png", h.Names);
            Assert.Equal(h.Root, h.CurrentFolder);

            Drop(h, h.TileCenter("Beta"), DragData(Path.Combine(h.Root, "b.png")), RawInputModifiers.Control);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Beta", "b.png")), "copied by Ctrl drop");
            await h.SettleAsync();
            Assert.True(File.Exists(Path.Combine(h.Root, "b.png")), "Ctrl = copy keeps the original");
        });
    }

    [Fact]
    public async Task Dropping_on_a_sidebar_location_moves_into_that_folder_and_a_folder_cannot_be_dropped_into_itself()
    {
        await ExplorerHarness.Run(async h =>
        {
            var pictures = h.Surface.NavHost.Children.OfType<Button>().First(b => ((TextBlock)((StackPanel)b.Content!).Children[1]).Text == "Pictures"); // -> Beta
            var point = pictures.TranslatePoint(new Point(20, 10), h.Window)!.Value;
            Drop(h, point, DragData(Path.Combine(h.Root, "c.png")));
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Beta", "c.png")), "moved to the sidebar location");

            // Alpha onto itself and onto its own child: refused, nothing changes.
            Drop(h, h.TileCenter("Alpha"), DragData(Path.Combine(h.Root, "Alpha")));
            await Task.Delay(100);
            Assert.True(Directory.Exists(Path.Combine(h.Root, "Alpha")));
            Assert.False(Directory.Exists(Path.Combine(h.Root, "Alpha", "Alpha")));
        });
    }

    [Fact]
    public async Task Dropping_on_the_list_background_moves_items_into_the_current_folder()
    {
        await ExplorerHarness.Run(async h =>
        {
            await h.NavigateAsync(Path.Combine(h.Root, "Alpha"));
            Drop(h, h.EmptySpotInList(), DragData(Path.Combine(h.Root, "a.png")));
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Alpha", "a.png")), "moved into the current folder");
            await h.SettleAsync();
            Assert.Contains("a.png", h.Names);
            Assert.False(File.Exists(Path.Combine(h.Root, "a.png")));
        });
    }

    [Fact]
    public async Task Pressing_on_a_tile_and_dragging_starts_a_drag_without_changing_the_selection()
    {
        await ExplorerHarness.Run(async h =>
        {
            var from = h.TileCenter("b.png");
            h.Window.MouseMove(from);
            h.Window.MouseDown(from, MouseButton.Left);
            h.Window.MouseMove(new Point(from.X + 30, from.Y + 20), RawInputModifiers.LeftMouseButton);
            h.Pump();
            Assert.True(h.Get<bool>("_explorerDragging"), "dragging started past the threshold");
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);
            h.Window.MouseUp(new Point(from.X + 30, from.Y + 20), MouseButton.Left);
            h.Pump();
            Assert.True(File.Exists(Path.Combine(h.Root, "b.png")));
            await Task.CompletedTask;
        });
    }
}
