using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Glide.App;

namespace Glide.Explorer.Tests;

/// <summary>Round 3 #14: the Delete (and every other) confirmation opens with the accept button focused, so Enter confirms.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ConfirmDialogTests
{
    [Fact]
    public async Task Delete_confirmation_focuses_the_Delete_button_so_Enter_confirms_and_Escape_cancels()
    {
        await ExplorerHarness.Run(async h =>
        {
            async Task<(bool? Result, Button? Focused)> Show(Key key)
            {
                var dialog = MainWindow.CreateConfirmDialog("Delete", "Move 'a.png' to the Recycle Bin?", "Delete");
                var result = dialog.ShowDialog<bool?>(h.Window);
                for (var i = 0; i < 10 && !dialog.IsActive; i++) { h.Pump(); await Task.Delay(15); }
                h.Pump();
                var focused = dialog.FocusManager?.GetFocusedElement() as Button;
                dialog.KeyPress(key, RawInputModifiers.None);
                if (dialog.IsVisible) dialog.KeyRelease(key, RawInputModifiers.None); // a closed window takes no more input
                h.Pump();
                await h.WaitUntil(() => result.IsCompleted, "dialog closed by " + key, 2000);
                return (await result, focused);
            }

            var (enter, focused) = await Show(Key.Enter);
            Assert.Equal("Delete", focused?.Content);
            Assert.True(enter);
            var (escape, _) = await Show(Key.Escape);
            Assert.False(escape ?? false);
        }, startExplorer: false);
    }
}
