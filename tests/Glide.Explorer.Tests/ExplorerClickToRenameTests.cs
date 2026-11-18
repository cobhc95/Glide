using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.0.6: click a selected item's name to rename it (Windows File Explorer's "slow double-click", setting
/// explorer.clickToRename, on by default). Driven with real pointer input through the MainWindow.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerClickToRenameTests
{
    private static TimeSpan DoubleClickTime(ExplorerHarness h) => (TimeSpan)h.Call("ExplorerDoubleClickTime")!;

    /// <summary>Longer than the double-click time, so the next click is a separate click.</summary>
    private static Task WaitPastDoubleClickTimeAsync(ExplorerHarness h) => PumpForAsync(h, DoubleClickTime(h) + TimeSpan.FromMilliseconds(120));

    private static async Task PumpForAsync(ExplorerHarness h, TimeSpan time)
    {
        var end = Environment.TickCount64 + (long)time.TotalMilliseconds;
        while (Environment.TickCount64 < end) { h.Pump(); await Task.Delay(15); }
        h.Pump();
    }

    private static TextBlock NameText(ExplorerHarness h, string name) =>
        h.Tile(name)!.GetVisualDescendants().OfType<TextBlock>().First(t => Equals(t.Tag, "name"));

    /// <summary>A point on the item's name text (the caption is centred under the thumbnail).</summary>
    private static Point NamePoint(ExplorerHarness h, string name, double dx = 0)
    {
        var text = NameText(h, name);
        return text.TranslatePoint(new Point(text.Bounds.Width / 2 + dx, Math.Min(8, text.Bounds.Height / 2)), h.Window)!.Value;
    }

    /// <summary>A point on the item's thumbnail / icon (top part of the tile).</summary>
    private static Point ThumbnailPoint(ExplorerHarness h, string name)
    {
        var tile = h.Tile(name)!;
        return tile.TranslatePoint(new Point(tile.Bounds.Width / 2, 24), h.Window)!.Value;
    }

    private static bool Renaming(ExplorerHarness h) =>
        h.Get<string?>("_explorerRenamePath") is not null || h.Surface.Repeater.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "RenameBox");

    private static async Task AssertNoRenameAsync(ExplorerHarness h, string what)
    {
        await PumpForAsync(h, DoubleClickTime(h) + TimeSpan.FromMilliseconds(150));
        Assert.False(Renaming(h), what);
        Assert.False((bool)h.Window.GetType().GetProperty("ExplorerClickRenamePendingForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(h.Window)!, what + " (still pending)");
    }

    [Fact]
    public async Task A_slow_second_click_on_the_selected_items_name_starts_the_inline_rename()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.True(h.Settings.ExplorerClickToRename); // on by default
            h.Click(NamePoint(h, "b.png", -3));
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);
            Assert.False(Renaming(h)); // the click that selected it never renames
            await WaitPastDoubleClickTimeAsync(h);
            h.Click(NamePoint(h, "b.png", 3));
            Assert.False(Renaming(h)); // it waits one double-click time first
            await h.WaitUntil(() => Renaming(h), "click-to-rename started the inline rename");
            var box = h.Surface.Repeater.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "RenameBox");
            Assert.Equal("b.png", box.Text);
            // The same editor as F2: Escape cancels.
            h.Key(Key.Escape);
            await h.WaitUntil(() => !Renaming(h), "Escape cancelled the rename");
            Assert.Contains("b.png", h.Names);
        });
    }

    [Fact]
    public async Task A_fast_double_click_on_the_selected_items_name_opens_it_and_never_renames()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click(NamePoint(h, "b.png"));
            await WaitPastDoubleClickTimeAsync(h);
            var point = NamePoint(h, "b.png");
            h.Window.MouseMove(point);
            h.Window.MouseDown(point, MouseButton.Left);
            h.Window.MouseUp(point, MouseButton.Left);
            h.Window.MouseDown(point, MouseButton.Left);
            h.Window.MouseUp(point, MouseButton.Left);
            h.Pump();
            await h.WaitUntil(() => h.Workspace.Active is ImageTabState, "the double-click opened the picture");
            await AssertNoRenameAsync(h, "a double-click must not rename");
        });
    }

    [Fact]
    public async Task No_rename_for_an_unselected_item_the_thumbnail_a_modifier_or_a_right_click()
    {
        await ExplorerHarness.Run(async h =>
        {
            // An item that was not selected: the click only selects it.
            h.Click(NamePoint(h, "a.png"));
            await WaitPastDoubleClickTimeAsync(h);
            h.Click(NamePoint(h, "b.png"));
            await AssertNoRenameAsync(h, "clicking an unselected item's name");
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);

            // The thumbnail of the selected item.
            h.Click(ThumbnailPoint(h, "b.png"));
            await AssertNoRenameAsync(h, "clicking the thumbnail");

            // Shift / Alt clicks on the name of the one selected item, well after the previous click (the waits above).
            foreach (var modifier in new[] { RawInputModifiers.Shift, RawInputModifiers.Alt })
            {
                Assert.Equal(new[] { "b.png" }, h.SelectedNames);
                h.Click(NamePoint(h, "b.png", -2), MouseButton.Left, modifier);
                await AssertNoRenameAsync(h, $"{modifier}+click on the name");
            }

            // A right-click on the selected item's name opens the menu, never a rename.
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);
            h.Click(NamePoint(h, "b.png", 2), MouseButton.Right);
            await AssertNoRenameAsync(h, "right-click on the name");
        });
    }

    [Fact]
    public async Task The_setting_off_disables_click_to_rename_and_F2_still_works()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.ExplorerClickToRename = false;
            h.Click(NamePoint(h, "b.png", -3));
            await WaitPastDoubleClickTimeAsync(h);
            h.Click(NamePoint(h, "b.png", 3));
            await AssertNoRenameAsync(h, "setting off");
            h.Key(Key.F2);
            await h.WaitUntil(() => Renaming(h), "F2 still renames");
        });
    }

    [Fact]
    public async Task A_selection_change_cancels_the_pending_rename()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click(NamePoint(h, "b.png", -3));
            await WaitPastDoubleClickTimeAsync(h);
            h.Click(NamePoint(h, "b.png", 3));
            Assert.True((bool)h.Window.GetType().GetProperty("ExplorerClickRenamePendingForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(h.Window)!);
            h.Call("SelectBrowserIndex", h.Names.ToList().IndexOf("a.png"), false, false, false);
            await AssertNoRenameAsync(h, "selection changed while pending");
            Assert.Equal(new[] { "a.png" }, h.SelectedNames);
        });
    }

    [Fact]
    public async Task A_key_press_cancels_the_pending_rename()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Click(NamePoint(h, "b.png", -3));
            await WaitPastDoubleClickTimeAsync(h);
            h.Click(NamePoint(h, "b.png", 3));
            h.Key(Key.LeftShift);
            await AssertNoRenameAsync(h, "a key was pressed while pending");
        });
    }

    [Fact]
    public async Task A_multi_selection_click_selects_only_the_item_and_does_not_rename()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.FocusList();
            h.Key(Key.A, RawInputModifiers.Control);
            Assert.Equal(6, h.SelectionSet.Count);
            await WaitPastDoubleClickTimeAsync(h);
            var point = NamePoint(h, "b.png");
            h.Window.MouseMove(point);
            h.Window.MouseDown(point, MouseButton.Left);
            h.Pump();
            Assert.Equal(6, h.SelectionSet.Count); // the 5.0.6 fix: the press keeps the selection draggable
            h.Window.MouseUp(point, MouseButton.Left);
            h.Pump();
            Assert.Equal(new[] { "b.png" }, h.SelectedNames);
            await AssertNoRenameAsync(h, "a click inside a multi-selection");
            // Now it is the one selected item: a slow click renames.
            h.Click(NamePoint(h, "b.png", 3));
            await h.WaitUntil(() => Renaming(h), "the next slow click renames");
        });
    }
}
