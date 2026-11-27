using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Services;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.1.7 end-to-end coverage (docs/audits/5.1.7-test-coverage.md): each test drives the real, themed MainWindow with routed
/// key / pointer input over a temp folder of real image files, the way a user would. Everything that could touch the machine
/// (Recycle Bin, shell Properties, clipboard, dialogs, registry, the user's single-instance pipe) is a seam or isolated.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class Release517EndToEndTests
{
    // ---------------------------------------------------------------- helpers

    private static async Task OpenImageInTabAsync(ExplorerHarness h, string name)
    {
        h.DoubleClick(name);
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState image && image.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase), "image tab " + name);
        await h.WaitUntil(() => string.Equals(Path.GetFileName(h.Get<string?>("_currentPath")), name, StringComparison.OrdinalIgnoreCase), "loaded " + name);
        await h.WaitUntil(() => h.Window.FindControl<Grid>("ImageView")!.IsVisible, "the picture view is showing");
    }

    private static string CurrentName(ExplorerHarness h) => Path.GetFileName(h.Get<string?>("_currentPath")) ?? "";

    private static void DoubleClickWith(ExplorerHarness h, string name, RawInputModifiers modifiers)
    {
        var point = h.TileCenter(name, 6);
        h.Window.MouseMove(point, modifiers);
        h.Window.MouseDown(point, MouseButton.Left, modifiers);
        h.Window.MouseUp(point, MouseButton.Left, modifiers);
        h.Window.MouseDown(point, MouseButton.Left, modifiers);
        h.Window.MouseUp(point, MouseButton.Left, modifiers);
        h.Pump();
    }

    private static Button VisibleSettingsButton(ExplorerHarness h)
    {
        var candidates = h.Window.GetVisualDescendants().OfType<Button>()
            .Where(b => (ToolTip.GetTip(b)?.ToString() ?? string.Empty).StartsWith("Settings", StringComparison.Ordinal)).ToList();
        return candidates.FirstOrDefault(b => b.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("no visible Settings button: " + string.Join(", ", candidates.Select(b => b.Name + ":" + b.IsEffectivelyVisible)));
    }

    private static byte[] AnimatedGif(int frames)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF });
        for (var i = 0; i < frames; i++)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00 });
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0x02, 0x02, 0x44, 0x01, 0x00 });
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    // ---------------------------------------------------------------- items 1 / 2: side buttons, picture opened from the Explorer

    private static bool RaiseSideButton(ExplorerHarness h, Visual target, bool back)
    {
        var props = new PointerPointProperties(back ? RawInputModifiers.XButton1MouseButton : RawInputModifiers.XButton2MouseButton,
            back ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed);
        var args = new PointerPressedEventArgs(target, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), h.Window,
            new Point(100, 100), 0, props, KeyModifiers.None) { RoutedEvent = InputElement.PointerPressedEvent };
        ((Interactive)target).RaiseEvent(args);
        h.Pump();
        return args.Handled;
    }

    [Fact]
    public async Task Mouse_back_from_a_picture_opened_in_the_Explorer_returns_to_the_folder_and_mouse_forward_returns_to_the_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageB);
            Assert.True(RaiseSideButton(h, h.Get<Control>("Viewport"), back: true), "claimed by default");
            await h.WaitUntil(() => h.Workspace.Active is BrowserTabState b && string.Equals(b.Folder, h.Root, StringComparison.OrdinalIgnoreCase), "back in the folder");
            await h.SettleAsync();
            Assert.Contains(ExplorerHarness.ImageB, h.SelectedNames);                 // the picture is selected again

            Assert.True(RaiseSideButton(h, h.BrowserView, back: false), "forward is claimed in the Explorer by default");
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState image && image.Path.EndsWith(ExplorerHarness.ImageB, StringComparison.OrdinalIgnoreCase), "forward to the picture");
        });
    }

    // ---------------------------------------------------------------- item 6: moved (not only deleted) while the tab was in the background

    [Fact]
    public async Task A_picture_renamed_while_its_tab_was_in_the_background_comes_back_as_its_own_frame_with_the_badge()
    {
        await ExplorerHarness.Run(async h =>
        {
            var pathA = Path.Combine(h.Root, ExplorerHarness.ImageA);
            h.Workspace.AddImage(pathA);
            var tabA = h.Workspace.Active!.Id;
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            var viewport = h.Get<ImageViewport>("Viewport");
            await h.WaitUntil(() => viewport.Bitmap is not null && string.Equals(h.Get<string?>("_presentedPath"), pathA, StringComparison.OrdinalIgnoreCase), "A shown");
            var frameA = viewport.Bitmap;

            h.Workspace.AddImage(Path.Combine(h.Root, ExplorerHarness.ImageB));
            var tabB = h.Workspace.Active!.Id;
            h.Call("CaptureActiveTabRuntimeState");
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.WaitUntil(() => string.Equals(h.Get<string?>("_presentedPath"), Path.Combine(h.Root, ExplorerHarness.ImageB), StringComparison.OrdinalIgnoreCase), "B shown");

            File.Move(pathA, Path.Combine(h.Root, "renamed-away.png"));
            h.Call("CaptureActiveTabRuntimeState");
            Assert.True(h.Workspace.Select(tabA));
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.Same(frameA, viewport.Bitmap);
            Assert.True(h.Get<Control>("MissingFileIndicator").IsVisible, "the 'File no longer on disk' badge is shown");

            h.Call("CaptureActiveTabRuntimeState");
            Assert.True(h.Workspace.Select(tabB));
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.SettleAsync();
            Assert.False(h.Get<Control>("MissingFileIndicator").IsVisible);
        });
    }

    // ---------------------------------------------------------------- item 3: duplicate tab placement

    [Fact]
    public async Task Duplicate_tab_opens_next_to_the_current_tab_in_front_by_default_and_the_settings_send_it_to_the_end_in_the_background()
    {
        await ExplorerHarness.Run(async h =>
        {
            // [Home, Explorer, Home2, Home3]; the Explorer tab (index 1) is active.
            var explorer = h.Workspace.Active!.Id;
            h.Workspace.InsertTransferred(new HomeTabState(Guid.NewGuid()), h.Workspace.Tabs.Count, activate: false);
            h.Workspace.InsertTransferred(new HomeTabState(Guid.NewGuid()), h.Workspace.Tabs.Count, activate: false);
            h.Call("RebuildTabStrip");
            h.Pump();
            var explorerIndex = h.Workspace.Tabs.ToList().FindIndex(t => t.Id == explorer);
            var before = h.Workspace.Tabs.Count;
            Assert.Equal("next to the current tab", h.Settings.DuplicateTabPosition);
            Assert.Equal("foreground", h.Settings.DuplicateTabSide);

            await h.Window.ExecuteActionAsync(AppActionRegistry.Find("tab.duplicate")!, "test");
            await h.SettleAsync();
            Assert.Equal(before + 1, h.Workspace.Tabs.Count);
            Assert.Equal(explorerIndex + 1, h.Workspace.ActiveIndex);       // right of the original, and in front
            Assert.NotEqual(explorer, h.Workspace.Active!.Id);
            Assert.IsType<BrowserTabState>(h.Workspace.Active);

            // Settings > Tabs: at the end, in the background. The original stays in front.
            var current = h.Workspace.Active!.Id;
            h.Settings.DuplicateTabPosition = "at the end";
            h.Settings.DuplicateTabSide = "background";
            await h.Window.ExecuteActionAsync(AppActionRegistry.Find("tab.duplicate")!, "test");
            await h.SettleAsync();
            Assert.Equal(before + 2, h.Workspace.Tabs.Count);
            Assert.Equal(current, h.Workspace.Active!.Id);
            Assert.IsType<BrowserTabState>(h.Workspace.Tabs[^1]);
            Assert.NotEqual(current, h.Workspace.Tabs[^1].Id);
        });
    }

    // ---------------------------------------------------------------- item 5: Space never clicks the Settings button

    [Fact]
    public async Task Space_toggles_the_GIF_when_it_is_bound_to_it_and_never_opens_Settings_from_the_focused_Settings_button()
    {
        await ExplorerHarness.Run(async h =>
        {
            var gifPath = Path.Combine(h.Root, "anim.gif");
            File.WriteAllBytes(gifPath, AnimatedGif(4));
            h.Settings.AnimateGifs = true;
            h.Settings.Hotkeys["nav.nextSpace"] = new List<string>();
            h.Settings.Hotkeys["gif.playPause"] = new List<string> { "Space" };
            h.Workspace.AddImage(gifPath);
            await (Task)h.Call("ActivateWorkspaceAsync")!;
            await h.WaitUntil(() => h.Get<object?>("_gifPlayer") is { } p && (bool)((dynamic)p).HasAnimation, "GIF playing");
            dynamic Player() => h.Get<object>("_gifPlayer");
            Assert.False((bool)Player().IsPaused);

            // The user clicked the Settings button earlier and closed the window: it still has the keyboard.
            var settingsButton = VisibleSettingsButton(h);
            Assert.True(settingsButton.Focus());
            h.Key(Key.Space);
            await h.SettleAsync();
            Assert.Null(h.Get<object?>("_openSettingsWindow"));
            Assert.True((bool)Player().IsPaused, "Space paused the GIF");

            Assert.True(settingsButton.Focus());
            h.Key(Key.Space);
            await h.SettleAsync();
            Assert.Null(h.Get<object?>("_openSettingsWindow"));
            Assert.False((bool)Player().IsPaused, "Space resumed the GIF");
        });
    }

    [Fact]
    public async Task Space_with_the_default_keys_goes_to_the_next_picture_not_to_the_focused_Settings_button()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageA);
            await h.WaitUntil(() => h.Get<bool>("_folderIndexReady"), "folder indexed");
            Assert.True(VisibleSettingsButton(h).Focus());
            h.Key(Key.Space);
            await h.WaitUntil(() => CurrentName(h) == ExplorerHarness.ImageB, "Space showed the next picture");
            Assert.Null(h.Get<object?>("_openSettingsWindow"));
        });
    }

    // ---------------------------------------------------------------- item 8: Alt+double-click = Properties

    [Fact]
    public async Task Alt_double_click_shows_Properties_and_neither_opens_nor_navigates()
    {
        await ExplorerHarness.Run(async h =>
        {
            DoubleClickWith(h, "a.png", RawInputModifiers.Alt);
            await h.SettleAsync();
            Assert.Equal(new[] { Path.Combine(h.Root, "a.png") }, h.Properties);
            Assert.IsType<BrowserTabState>(h.Workspace.Active);               // no image tab

            h.Properties.Clear();
            DoubleClickWith(h, "Alpha", RawInputModifiers.Alt);
            await h.SettleAsync();
            Assert.Equal(new[] { Path.Combine(h.Root, "Alpha") }, h.Properties);
            Assert.Equal(h.Root, h.CurrentFolder);                           // the folder was not entered

            // A custom binding for the Alt row replaces the default (here: do nothing).
            h.Properties.Clear();
            h.Settings.Gestures["explorer.altDoubleClick"] = GestureCatalog.Nothing;
            DoubleClickWith(h, "b.png", RawInputModifiers.Alt);
            await h.SettleAsync();
            Assert.Empty(h.Properties);

            // The plain double-click still opens.
            h.DoubleClick("Alpha");
            await h.SettleAsync();
            Assert.Equal(Path.Combine(h.Root, "Alpha"), h.CurrentFolder);
        });
    }

    // ---------------------------------------------------------------- item 9: sort by Date taken from the Sort menu

    private static string JpegTaken(string folder, string name, string? exifDate, DateTime modified)
    {
        var path = Path.Combine(folder, name);
        using (var s = File.Create(path))
        {
            s.Write([0xFF, 0xD8]);
            if (exifDate is not null)
            {
                var tiff = new List<byte>();
                tiff.AddRange("II"u8.ToArray()); tiff.AddRange(BitConverter.GetBytes((ushort)42)); tiff.AddRange(BitConverter.GetBytes(8u));
                tiff.AddRange(BitConverter.GetBytes((ushort)1));
                tiff.AddRange(BitConverter.GetBytes((ushort)0x0132)); tiff.AddRange(BitConverter.GetBytes((ushort)2)); tiff.AddRange(BitConverter.GetBytes(20u)); tiff.AddRange(BitConverter.GetBytes(26u));
                tiff.AddRange(BitConverter.GetBytes(0u));
                tiff.AddRange(Encoding.ASCII.GetBytes(exifDate)); tiff.Add(0);
                var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
                var len = payload.Length + 2;
                s.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
                s.Write(payload);
            }
            s.Write([0xFF, 0xD9]);
        }
        File.SetLastWriteTime(path, modified);
        return path;
    }

    [Fact]
    public async Task Sort_by_Date_taken_in_the_Sort_menu_orders_by_EXIF_and_falls_back_to_the_modified_date()
    {
        await ExplorerHarness.Run(async h =>
        {
            var jpgs = new[] { "p1.jpg", "p2.jpg", "p3.jpg" };
            List<string> Order() => h.Names.Where(n => n.EndsWith(".jpg", StringComparison.Ordinal)).ToList();
            void Pick(string label)
            {
                var item = ((List<MenuItem>)h.Call("BuildExplorerSortMenuItems")!).First(i => (i.Header as string) == label);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                h.Pump();
            }

            JpegTaken(h.Root, "p1.jpg", "2020:05:01 10:00:00", new DateTime(2024, 1, 1));
            JpegTaken(h.Root, "p2.jpg", "2018:01:01 09:00:00", new DateTime(2025, 6, 1));
            JpegTaken(h.Root, "p3.jpg", null, new DateTime(2019, 6, 1));          // no EXIF: its date modified counts
            h.Call("RefreshManagedBrowser", h.Root);
            await h.RefreshTask;
            await h.SettleAsync();
            Assert.Equal(jpgs.OrderBy(n => n, StringComparer.Ordinal), Order());   // by name first

            Pick("Date modified " + ExplorerSortFields.Triangle(true));
            await h.WaitUntil(() => Order().SequenceEqual(new[] { "p3.jpg", "p1.jpg", "p2.jpg" }), "sorted by date modified");

            Pick("Date taken " + ExplorerSortFields.Triangle(true));
            await h.WaitUntil(() => Order().SequenceEqual(new[] { "p2.jpg", "p3.jpg", "p1.jpg" }), "sorted by date taken");
            Assert.Equal("Date taken", ExplorerSortFields.Label(h.Get<ExplorerSortKey>("_explorerSortKey")));

            Pick("Date taken " + ExplorerSortFields.Triangle(false));
            await h.WaitUntil(() => Order().SequenceEqual(new[] { "p1.jpg", "p3.jpg", "p2.jpg" }), "sorted by date taken, newest first");
        });
    }

    // ---------------------------------------------------------------- item 10: Ctrl+Shift+1..6 pick all six views

    [Theory]
    [InlineData(Key.D1, ExplorerViewModes.ExtraLarge)]
    [InlineData(Key.D2, ExplorerViewModes.Large)]
    [InlineData(Key.D3, ExplorerViewModes.Medium)]
    [InlineData(Key.D4, ExplorerViewModes.Small)]
    [InlineData(Key.D5, ExplorerViewModes.List)]
    [InlineData(Key.D6, ExplorerViewModes.Details)]
    public async Task Ctrl_Shift_digit_chooses_the_matching_view(Key key, int mode)
    {
        await ExplorerHarness.Run(async h =>
        {
            // Start from a different view so a no-op cannot pass.
            h.Surface.ViewModeCombo.SelectedIndex = mode == ExplorerViewModes.List ? ExplorerViewModes.Details : ExplorerViewModes.List;
            await h.SettleAsync();
            h.FocusList();
            h.Key(key, RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.SettleAsync();
            Assert.Equal(mode, h.Get<int>("_browserViewMode"));
            Assert.Equal(mode, h.Surface.ViewModeCombo.SelectedIndex);
        });
    }

    // ---------------------------------------------------------------- item 11: Shift forces a move (the cross-drive default is unit-tested)

    [Fact]
    public async Task Shift_drop_moves_and_Ctrl_Shift_drop_copies_like_Windows()
    {
        await ExplorerHarness.Run(async h =>
        {
            DataTransfer Data(string path)
            {
                var data = new DataTransfer();
                var item = new DataTransferItem();
                item.Set(DataFormat.CreateStringApplicationFormat(ExplorerHost.DragFormatName), path);
                data.Add(item);
                return data;
            }
            void Drop(Point point, DataTransfer data, RawInputModifiers modifiers)
            {
                var both = DragDropEffects.Move | DragDropEffects.Copy;
                h.Window.DragDrop(point, RawDragEventType.DragEnter, data, both, modifiers);
                h.Window.DragDrop(point, RawDragEventType.DragOver, data, both, modifiers);
                h.Window.DragDrop(point, RawDragEventType.Drop, data, both, modifiers);
                h.Pump();
            }

            Drop(h.TileCenter("Gamma"), Data(Path.Combine(h.Root, "a.png")), RawInputModifiers.Shift);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Gamma", "a.png")), "moved by Shift drop");
            Assert.False(File.Exists(Path.Combine(h.Root, "a.png")));
            await h.SettleAsync();

            Drop(h.TileCenter("Beta"), Data(Path.Combine(h.Root, "b.png")), RawInputModifiers.Control | RawInputModifiers.Shift);
            await h.WaitUntil(() => File.Exists(Path.Combine(h.Root, "Beta", "b.png")), "copied by Ctrl+Shift drop");
            Assert.True(File.Exists(Path.Combine(h.Root, "b.png")), "Ctrl wins over Shift: the original stays");
        });
    }

    // ---------------------------------------------------------------- item 18: Shift+Delete through the real confirm dialog

    private static Window? OwnedDialog(ExplorerHarness h, string title) => h.Window.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.Title == title);

    private static void PressOnDialog(ExplorerHarness h, Window dialog, Key key)
    {
        dialog.KeyPress(key, RawInputModifiers.None);
        h.Pump();
    }

    [Fact]
    public async Task Shift_Delete_shows_the_confirm_dialog_Cancel_keeps_the_file_and_Enter_deletes_permanently()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageA);
            var a = Path.Combine(h.Root, ExplorerHarness.ImageA);

            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => OwnedDialog(h, "Delete permanently") is not null, "confirm dialog shown");
            var dialog = OwnedDialog(h, "Delete permanently")!;
            await h.WaitUntil(() => (dialog.FocusManager?.GetFocusedElement() as Button)?.Content as string == "Delete", "Delete is focused");
            var text = string.Join(" ", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Recycle Bin", text);
            Assert.Contains("a.png", text);
            var dontAsk = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
            Assert.Equal("Don't ask again for this folder in this tab", dontAsk.Content);
            Assert.NotEqual(true, dontAsk.IsChecked);

            // Cancel (the Cancel button, as a click) keeps the file.
            dialog.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await h.WaitUntil(() => OwnedDialog(h, "Delete permanently") is null, "dialog closed");
            Assert.True(File.Exists(a));

            // Enter on the focused Delete button deletes it for good (no Recycle Bin), and the next picture is shown.
            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => OwnedDialog(h, "Delete permanently") is not null, "confirm dialog shown again");
            dialog = OwnedDialog(h, "Delete permanently")!;
            await h.WaitUntil(() => (dialog.FocusManager?.GetFocusedElement() as Button)?.Content as string == "Delete", "Delete is focused");
            PressOnDialog(h, dialog, Key.Enter);
            await h.WaitUntil(() => !File.Exists(a), "a.png deleted");
            Assert.Empty(h.Recycled);
            await h.WaitUntil(() => CurrentName(h) != ExplorerHarness.ImageA && File.Exists(h.Get<string?>("_currentPath") ?? ""), "the next picture is shown");
        });
    }

    [Fact]
    public async Task Dont_ask_again_ticked_in_the_real_dialog_skips_the_question_for_this_folder_and_tab_only()
    {
        await ExplorerHarness.Run(async h =>
        {
            await OpenImageInTabAsync(h, ExplorerHarness.ImageA);
            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => OwnedDialog(h, "Delete permanently") is not null, "confirm dialog shown");
            var dialog = OwnedDialog(h, "Delete permanently")!;
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            await h.WaitUntil(() => (dialog.FocusManager?.GetFocusedElement() as Button)?.Content as string == "Delete", "Delete is focused");
            PressOnDialog(h, dialog, Key.Enter);
            await h.WaitUntil(() => !File.Exists(Path.Combine(h.Root, ExplorerHarness.ImageA)), "a.png deleted");
            await h.WaitUntil(() => CurrentName(h) == ExplorerHarness.ImageB, "b.png is shown");

            // Same folder, same tab: no dialog any more.
            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => !File.Exists(Path.Combine(h.Root, ExplorerHarness.ImageB)), "b.png deleted without a question");
            Assert.Null(OwnedDialog(h, "Delete permanently"));
            await h.WaitUntil(() => CurrentName(h) == ExplorerHarness.ImageC, "c.png is shown");
        });
    }

    // ---------------------------------------------------------------- item 22: F2 batch rename, typed like a user

    private static async Task<BatchRenameDialog> OpenBatchDialogAsync(ExplorerHarness h, params string[] names)
    {
        h.Click(names[0]);
        foreach (var name in names.Skip(1)) h.Click(name, modifiers: RawInputModifiers.Control);
        h.FocusList();
        h.Key(Key.F2);
        await h.WaitUntil(() => h.Window.OwnedWindows.OfType<BatchRenameDialog>().Any(), "batch rename dialog opened");
        var dialog = h.Window.OwnedWindows.OfType<BatchRenameDialog>().Single();
        await h.WaitUntil(() => dialog.Editor.TextPieces.Any(t => t.IsFocused), "the New name box is focused");
        return dialog;
    }

    private static void TypeInto(ExplorerHarness h, BatchRenameDialog dialog, string text)
    {
        dialog.Editor.Template = "";
        dialog.Editor.FocusEnd();
        h.Pump();
        dialog.KeyTextInput(text);
        h.Pump();
    }

    [Fact]
    public async Task F2_dialog_still_reads_the_old_patterns_and_saved_presets_reach_the_next_dialog()
    {
        await ExplorerHarness.Run(async h =>
        {
            var dialog = await OpenBatchDialogAsync(h, "a.png", "b.png", "c.png");

            TypeInto(h, dialog, "foo*bar");                                    // wrap the name, extension kept
            Assert.Equal(["fooabar.png", "foobbar.png", "fooc" + "bar.png"], dialog.CurrentPlan.Items.Select(i => i.NewName));
            Assert.True(dialog.RenameButton.IsEnabled);

            TypeInto(h, dialog, "a/b");                                        // replace text: only a changes (a -> b)
            Assert.Contains("b.png", dialog.CurrentPlan.Items.Where(i => i.OldName == "a.png").Select(i => i.NewName));
            Assert.False(dialog.RenameButton.IsEnabled, "a.png -> b.png collides with the existing b.png: Rename is disabled");
            Assert.Contains("More than one item would be named", dialog.CurrentPlan.Items.First(i => i.HasProblem).Message);

            TypeInto(h, dialog, "a/x|b/y");                                    // several replacements in order
            Assert.Equal(["x.png", "y.png", "c.png"], dialog.CurrentPlan.Items.Select(i => i.NewName));
            Assert.True(dialog.RenameButton.IsEnabled);

            dialog.RegexCheck.IsChecked = true;                                 // a .NET regex with $1
            TypeInto(h, dialog, "^([abc])/pic_$1");
            Assert.Equal(["pic_a.png", "pic_b.png", "pic_c.png"], dialog.CurrentPlan.Items.Select(i => i.NewName));
            Assert.True(dialog.RenameButton.IsEnabled);

            await dialog.RenameForTestsAsync();                                 // what the Rename button runs
            await h.WaitUntil(() => h.Names.Contains("pic_a.png") && h.Names.Contains("pic_c.png"), "renamed in the listing");
            Assert.True(File.Exists(Path.Combine(h.Root, "pic_b.png")));
            Assert.False(File.Exists(Path.Combine(h.Root, "a.png")));

            // Saved presets: saving one stores it in the settings at once (even though the dialog is cancelled), and the next dialog lists it.
            var again = await OpenBatchDialogAsync(h, "pic_a.png", "pic_b.png");
            Assert.Empty(again.Presets);
            Assert.True(again.SavePreset("Pic rename"));
            Assert.Contains("Pic rename", h.Settings.BatchRenamePresets);
            again.Close(null);
            h.Pump();
            var third = await OpenBatchDialogAsync(h, "pic_a.png", "pic_b.png");
            Assert.Equal(["Pic rename"], third.Presets.Select(p => p.Name));
            third.Close(null);
            h.Pump();
        });
    }

    [Fact]
    public async Task F2_dialog_rolls_everything_back_when_one_rename_fails_and_keeps_the_dialog_open_with_the_reason()
    {
        await ExplorerHarness.Run(async h =>
        {
            var previous = BatchRenameExecutor.MoveEntry;
            try
            {
                var dialog = await OpenBatchDialogAsync(h, "a.png", "b.png", "c.png");
                TypeInto(h, dialog, "x*");
                Assert.True(dialog.RenameButton.IsEnabled);
                var calls = 0;
                BatchRenameExecutor.MoveEntry = (from, to, isDirectory) =>
                {
                    if (++calls == 5) throw new IOException("The file is in use.");     // 3 aside, then the 2nd final move fails
                    File.Move(from, to);
                };
                await dialog.RenameForTestsAsync();
                h.Pump();
                Assert.True(dialog.IsVisible, "the dialog stays open");
                Assert.Contains("Nothing was renamed", dialog.StatusText.Text);
                foreach (var name in new[] { "a.png", "b.png", "c.png" }) Assert.True(File.Exists(Path.Combine(h.Root, name)), name + " is back");
                Assert.Empty(Directory.GetFiles(h.Root, "x*"));
                dialog.Close(null);
                h.Pump();
            }
            finally { BatchRenameExecutor.MoveEntry = previous; }
        });
    }

    // ---------------------------------------------------------------- item 23: tags in the viewer keep the group order and never repeat

    [Fact]
    public async Task Tags_in_the_viewer_are_written_in_group_order_never_twice_and_toggle_off_again()
    {
        await ExplorerHarness.Run(async h =>
        {
            File.WriteAllBytes(Path.Combine(h.Root, "t {Family;dad}.png"), ExplorerHarness.Png(20, 20, 5, 5, 5));
            h.Call("RefreshManagedBrowser", h.Root);
            await h.RefreshTask;
            await h.SettleAsync();
            await OpenImageInTabAsync(h, "t {Family;dad}.png");
            h.Settings.TagsEnabled = true;
            h.Settings.TagGroups = "+Family: mom, dad\nFriends: anna";
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            var sidebar = (TagSidebar)h.Window.FindControl<Border>("TagSidebarHost")!.Child!;
            CheckBox Tag(string tag) => sidebar.TagBoxes.Single(b => (string?)b.Tag == tag);
            async Task ClickAsync(string tag, string expectedName)
            {
                Tag(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                h.Pump();
                await h.WaitUntil(() => string.Equals(CurrentName(h), expectedName, StringComparison.OrdinalIgnoreCase), $"{tag} clicked -> {expectedName} (now {CurrentName(h)})");
                await h.WaitUntil(() => !h.Get<bool>("_tagChangeBusy"), "the tag change finished (a click during the reload is ignored by design)");
            }

            await h.WaitUntil(() => Tag("dad").IsChecked == true, "dad is ticked from the name");
            Assert.NotEqual(true, Tag("mom").IsChecked);

            await ClickAsync("mom", "t {Family;mom;dad}.png");                    // added in front of dad (list order, not click order)
            await h.WaitUntil(() => Tag("mom").IsChecked == true, "mom ticked");
            await ClickAsync("anna", "t {Family;mom;dad;anna}.png");              // the other group's tag after Family's
            await ClickAsync("mom", "t {Family;dad;anna}.png");                   // clicking a ticked tag removes it, never doubles it
            await ClickAsync("dad", "t {anna}.png");                              // the last Family tag takes the group tag with it
            Assert.True(File.Exists(Path.Combine(h.Root, "t {anna}.png")));
            Assert.Single(Directory.GetFiles(h.Root, "t *"));                    // one file, nothing left behind
        });
    }

    [Fact]
    public async Task Rapid_tag_clicks_in_the_viewer_are_all_applied_in_order_and_the_ticks_show_the_pending_state()
    {
        await ExplorerHarness.Run(async h =>
        {
            File.WriteAllBytes(Path.Combine(h.Root, "t {Family;dad}.png"), ExplorerHarness.Png(20, 20, 5, 5, 5));
            h.Call("RefreshManagedBrowser", h.Root);
            await h.RefreshTask;
            await h.SettleAsync();
            await OpenImageInTabAsync(h, "t {Family;dad}.png");
            h.Settings.TagsEnabled = true;
            h.Settings.TagGroups = "+Family: mom, dad\nFriends: anna";
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            var sidebar = (TagSidebar)h.Window.FindControl<Border>("TagSidebarHost")!.Child!;
            CheckBox Tag(string tag) => sidebar.TagBoxes.Single(b => (string?)b.Tag == tag);
            void Click(string tag) { Tag(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); h.Pump(); }

            // No waiting in between: the second and third click arrive while the first rename is still running.
            Click("mom");
            Click("anna");
            Assert.True(Tag("mom").IsChecked == true && Tag("anna").IsChecked == true && Tag("dad").IsChecked == true,
                "the ticks already show the pending state");
            Click("mom");                                            // toggles the pending mom off again

            await h.WaitUntil(() => !h.Get<bool>("_tagChangeBusy"), "all changes applied", 15000);
            await h.WaitUntil(() => string.Equals(CurrentName(h), "t {Family;dad;anna}.png", StringComparison.OrdinalIgnoreCase),
                "mom added then removed, anna added, in order (now " + CurrentName(h) + ")");
            Assert.Single(Directory.GetFiles(h.Root, "t *"));
            await h.WaitUntil(() => Tag("anna").IsChecked == true && Tag("dad").IsChecked == true && Tag("mom").IsChecked != true, "ticks follow the final name");
        });
    }

    [Fact]
    public async Task Rapid_tag_clicks_in_the_Explorer_are_all_applied_to_every_selected_file()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.TagsEnabled = true;
            h.Settings.TagGroups = "+Family: mom, dad\nFriends: anna";
            await (Task)h.Call("ToggleTagSidebarAsync")!;
            await h.SettleAsync();
            h.Click("a.png");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            var sidebar = (TagSidebar)h.Window.FindControl<Border>("TagSidebarHost")!.Child!;
            CheckBox Tag(string tag) => sidebar.TagBoxes.Single(b => (string?)b.Tag == tag);
            void Click(string tag) { Tag(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); h.Pump(); }

            Click("dad");
            Click("anna");
            Click("mom");
            await h.WaitUntil(() => !h.Get<bool>("_tagChangeBusy"), "all changes applied", 15000);
            await h.WaitUntil(() => h.Names.Contains("a {Family;mom;dad;anna}.png") && h.Names.Contains("b {Family;mom;dad;anna}.png"),
                "all three tags on both files (now " + string.Join(", ", h.Names) + ")");
            await h.WaitUntil(() => h.SelectedNames.Count == 2, "the selection is kept");
        });
    }

    // ---------------------------------------------------------------- item 26: single-instance forwarding round trip (isolated pipe)

    [Fact]
    public async Task A_launch_forwarded_over_the_isolated_single_instance_pipe_opens_in_the_running_window()
    {
        if (!OperatingSystem.IsWindows()) return;
        await ExplorerHarness.Run(async h =>
        {
            // Test host: the pipe and presence names carry this process id, never the user's real Glide names.
            Assert.True(ExternalLaunchBroker.IsForeignHost());
            Assert.NotEqual(Glide.Core.ProtocolNames.UserScoped(Glide.Core.ProtocolNames.PipeName, ExternalLaunchBroker.CurrentUserKey()), ExternalLaunchBroker.PipeName);
            Assert.Contains(Environment.ProcessId.ToString(), Glide.Core.ProtocolNames.ScopedInstanceKey(
                ExternalLaunchBroker.CurrentUserKey(), null, AppContext.BaseDirectory, installedCopy: false, foreignHost: true, processId: Environment.ProcessId), StringComparison.Ordinal);

            h.Settings.ExternalOpenBehavior = "Open in new tab";
            ExternalLaunchBroker.Stop();
            try
            {
                ExternalLaunchBroker.Start(h.Window);                          // this window is the running Glide: it owns the pipe
                await h.WaitUntil(() => ExternalLaunchBroker.IsPresenceOwner, "owner elected");
                Assert.True(ExternalLaunchBroker.ExistingProcessPresent());

                var picture = Path.Combine(h.Root, ExplorerHarness.ImageC);
                var batch = ExternalLaunchBroker.PrepareForwardBatch(new[] { picture, Path.Combine(h.Root, "does-not-exist.png") });
                Assert.NotNull(batch);
                Assert.Single(batch!.Requests);                                // a missing file is not even forwarded
                var forwarded = Task.Run(() => ExternalLaunchBroker.TryForwardToExisting(batch, TimeSpan.FromSeconds(10)));
                await h.WaitUntil(() => forwarded.IsCompleted, "the launcher got its answer", 12000);
                Assert.True(await forwarded, "the owner acknowledged the launch");
                Assert.False(ExternalLaunchBroker.LastOwnerIncompatible);

                await h.WaitUntil(() => h.Workspace.Tabs.OfType<ImageTabState>().Any(t => string.Equals(t.Path, picture, StringComparison.OrdinalIgnoreCase)),
                    "the forwarded picture opened in the running window");
            }
            finally { ExternalLaunchBroker.Stop(); }
            Assert.False(ExternalLaunchBroker.IsPresenceOwner);
        });
    }
}
