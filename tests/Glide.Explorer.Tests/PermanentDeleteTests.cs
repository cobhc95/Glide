using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
