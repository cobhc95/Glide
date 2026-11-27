using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Glide.App;
using Glide.Core.Workspace;

namespace Glide.Explorer.Tests;

/// <summary>Round 4 G1: Shift+Delete in the image view deletes permanently after a confirmation that can be skipped per folder and tab.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class PermanentDeleteTests
{
    private static async Task OpenImageAsync(ExplorerHarness h, string name)
    {
        h.DoubleClick(name);
        await h.WaitUntil(() => h.Workspace.Active is ImageTabState image && image.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase), "image tab " + name);
        await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), Path.Combine(h.Root, name), StringComparison.OrdinalIgnoreCase), "loaded " + name);
    }

    [Fact]
    public async Task Shift_Delete_asks_then_deletes_permanently_and_dont_ask_again_holds_for_this_folder_in_this_tab()
    {
        await ExplorerHarness.Run(async h =>
        {
            var asked = new List<string>();
            var window = h.Window;
            window.PermanentDeleteConfirmOverride = path => { asked.Add(path); return Task.FromResult((false, false)); };
            await OpenImageAsync(h, "a.png");
            var a = Path.Combine(h.Root, "a.png");

            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => asked.Count == 1, "asked once");
            h.Pump();
            Assert.True(File.Exists(a)); // Cancel keeps the file

            window.PermanentDeleteConfirmOverride = path => { asked.Add(path); return Task.FromResult((true, true)); };
            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => !File.Exists(a), "a.png deleted");
            Assert.Equal(2, asked.Count);
            Assert.Empty(h.Recycled); // not the Recycle Bin

            // Don't ask again: the next Shift+Delete in this folder and tab deletes without asking.
            await h.WaitUntil(() => h.Get<string?>("_currentPath") is { } p && File.Exists(p), "next picture shown");
            var next = h.Get<string?>("_currentPath")!;
            h.Key(Key.Delete, RawInputModifiers.Shift);
            await h.WaitUntil(() => !File.Exists(next), "next deleted without asking");
            Assert.Equal(2, asked.Count);
        });
    }

    [Fact]
    public void Dont_ask_again_is_per_tab_and_per_folder()
    {
        var c = new PermanentDeleteConfirmations();
        var tab = Guid.NewGuid();
        var folder = Path.Combine(Path.GetTempPath(), "x");
        Assert.True(c.ShouldAsk(tab, folder));
        c.DontAskAgain(tab, folder);
        Assert.False(c.ShouldAsk(tab, folder + Path.DirectorySeparatorChar));
        Assert.True(c.ShouldAsk(Guid.NewGuid(), folder));
        Assert.True(c.ShouldAsk(tab, Path.Combine(Path.GetTempPath(), "y")));
    }

    // ---- 5.2.1 G2: "Delayed permanent delete (Shift+Delete with undo)" -----------------------------------------------

    private static void Confirm(ExplorerHarness h) =>
        h.Window.PermanentDeleteConfirmOverride = _ => Task.FromResult((true, false));

    private static bool InNavigator(ExplorerHarness h, string path) =>
        h.Get<Glide.App.Services.NavigationCoordinator>("_navigator").Paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    // The first key press right after a picture opens is sometimes lost in the headless window (focus not settled yet): retry
    // until the expected effect shows (a press only acts once, so a retry after success never happens).
    private static async Task ShiftDeleteUntilAsync(ExplorerHarness h, Func<bool> done)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            h.Key(Key.Delete, RawInputModifiers.Shift);
            if (await Wait.TryUntilAsync(done, 2500, h.Pump, pollMs: 15)) return;
        }
        Assert.True(done(), "Shift+Delete had no effect");
    }

    [Fact]
    public async Task Delayed_delete_is_off_by_default_and_Shift_Delete_then_deletes_at_once()
    {
        await ExplorerHarness.Run(async h =>
        {
            Assert.False(new Glide.Core.Settings.GlideSettingsState().DelayedPermanentDelete);
            Confirm(h);
            await OpenImageAsync(h, "a.png");
            var a = Path.Combine(h.Root, "a.png");
            await ShiftDeleteUntilAsync(h, () => !File.Exists(a));
            Assert.Equal(0, h.Window.PendingPermanentDeletes.Count);
        });
    }

    [Fact]
    public async Task Delayed_delete_on_hides_the_file_shows_the_next_picture_and_Undo_restores_it()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.DelayedPermanentDelete = true;
            Confirm(h);
            await OpenImageAsync(h, "a.png");
            var a = Path.Combine(h.Root, "a.png");
            await ShiftDeleteUntilAsync(h, () => h.Window.PendingPermanentDeletes.Count == 1);
            await h.WaitUntil(() => h.Get<string?>("_currentPath") is { } p && !string.Equals(p, a, StringComparison.OrdinalIgnoreCase), "moved on");
            Assert.True(File.Exists(a));                                   // still on disk during the delay
            Assert.False(InNavigator(h, a));
            var notices = h.Window.FindControl<StackPanel>("PendingDeleteNotices")!;
            Assert.True(notices.IsVisible);
            Assert.Contains("Deleting a.png permanently", string.Join(" ", notices.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)));

            await h.Window.UndoPendingDeleteAsync(h.Window.PendingPermanentDeletes.Entries[0].Id);
            Assert.True(File.Exists(a));
            Assert.Equal(0, h.Window.PendingPermanentDeletes.Count);
            Assert.False(notices.IsVisible);
            Assert.Equal(a, h.Get<string?>("_currentPath"), ignoreCase: true);   // shown again
            Assert.True(InNavigator(h, a));
        });
    }

    [Fact]
    public async Task Delayed_delete_deletes_after_the_delay_and_several_pending_each_work()
    {
        var delay = MainWindow.PendingDeleteDelayMs;
        MainWindow.PendingDeleteDelayMs = 400;
        try
        {
            await ExplorerHarness.Run(async h =>
            {
                h.Settings.DelayedPermanentDelete = true;
                Confirm(h);
                await OpenImageAsync(h, "a.png");
                var a = Path.Combine(h.Root, "a.png"); var b = Path.Combine(h.Root, "b.png");
                await ShiftDeleteUntilAsync(h, () => h.Window.PendingPermanentDeletes.Count == 1 || !File.Exists(a));
                await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), b, StringComparison.OrdinalIgnoreCase), "b shown");
                await ShiftDeleteUntilAsync(h, () => !File.Exists(b) || h.Window.PendingPermanentDeletes.Count == 2 || (!File.Exists(a) && h.Window.PendingPermanentDeletes.Count == 1));
                await h.WaitUntil(() => h.Window.PendingPermanentDeletes.Count == 0 && !File.Exists(a) && !File.Exists(b), "both completed", 6000);
                Assert.False(File.Exists(a)); Assert.False(File.Exists(b));
                Assert.Empty(h.Recycled);
            });
        }
        finally { MainWindow.PendingDeleteDelayMs = delay; }
    }

    [Fact]
    public async Task Closing_the_window_carries_out_pending_deletes_and_leaves_changed_files_alone()
    {
        await ExplorerHarness.Run(async h =>
        {
            h.Settings.DelayedPermanentDelete = true;
            Confirm(h);
            await OpenImageAsync(h, "a.png");
            var a = Path.Combine(h.Root, "a.png"); var b = Path.Combine(h.Root, "b.png");
            await ShiftDeleteUntilAsync(h, () => h.Window.PendingPermanentDeletes.Count == 1);
            await h.WaitUntil(() => string.Equals(h.Get<string?>("_currentPath"), b, StringComparison.OrdinalIgnoreCase), "b shown");
            await ShiftDeleteUntilAsync(h, () => h.Window.PendingPermanentDeletes.Count == 2);
            File.AppendAllText(b, "changed after confirming");
            Assert.True(File.Exists(a));
            h.Window.FlushPendingDeletes();                                // what Closed / the standby close call
            Assert.False(File.Exists(a));
            Assert.True(File.Exists(b));                                   // changed meanwhile: kept
            Assert.Equal(0, h.Window.PendingPermanentDeletes.Count);
        });
    }

    [Fact]
    public async Task Confirmation_has_the_checkbox_and_focuses_Delete()
    {
        await ExplorerHarness.Run(async h =>
        {
            var dialog = MainWindow.CreatePermanentDeleteDialog("a.png", out var dontAsk);
            Assert.Equal("Don't ask again for this folder in this tab", dontAsk.Content);
            var result = dialog.ShowDialog<bool?>(h.Window);
            for (var i = 0; i < 10 && !dialog.IsActive; i++) { h.Pump(); await Task.Delay(15); }
            h.Pump();
            Assert.Equal("Delete", (dialog.FocusManager?.GetFocusedElement() as Button)?.Content);
            dialog.KeyPress(Key.Escape, RawInputModifiers.None);
            h.Pump();
            await h.WaitUntil(() => result.IsCompleted, "closed", 2000);
            Assert.False(await result ?? false);
        }, startExplorer: false);
    }
}
