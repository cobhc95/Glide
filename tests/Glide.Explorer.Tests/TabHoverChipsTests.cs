using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Glide.Explorer.Tests;

/// <summary>5.1.0 tab hover card: the mouse hint is one line of key chips built from the live bindings (design B).</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Tabs")]
public sealed class TabHoverChipsTests
{
    private static List<string> Chips(TextBlock hint) =>
        hint.Inlines!.OfType<InlineUIContainer>().Select(c => ((TextBlock)((Border)c.Child).Child!).Text!).ToList();

    [Fact]
    public async Task Hint_line_shows_chips_and_follows_the_resize_key()
    {
        await ExplorerHarness.Run(async h =>
        {
            var hint = new TextBlock { FontSize = 12 };
            h.Settings.TabPreviewResizeModifier = "Ctrl";
            h.Call("FillTabMouseHint", hint);
            Assert.Equal(["Drag", "Middle", "Right", "Ctrl+Wheel"], Chips(hint));
            Assert.Contains(hint.Inlines!.OfType<Run>(), r => r.Text == " close");

            h.Settings.TabPreviewResizeModifier = "None";
            h.Call("FillTabMouseHint", hint);
            Assert.Equal(["Drag", "Middle", "Right"], Chips(hint)); // refilled, not appended
            await Task.CompletedTask;
        });
    }
}
