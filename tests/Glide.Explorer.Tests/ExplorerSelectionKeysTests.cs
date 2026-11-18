using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;

namespace Glide.Explorer.Tests;

/// <summary>Round 3 (Vertigo) #8-#10: Ctrl+drag toggles, Escape clears, Shift+Home/End stop at the row.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerSelectionKeysTests
{
    // Default tree at 1200 px: five columns. Row 0 = Alpha, Beta, Gamma, a.png, b.png; row 1 = c.png.

    [Fact]
    public async Task Ctrl_drag_over_selected_items_deselects_them_like_Windows_Explorer()
    {
        await ExplorerHarness.Run(h =>
        {
            h.FocusList();
            h.Key(Key.A, RawInputModifiers.Control);
            Assert.Equal(6, h.SelectionSet.Count);
            var start = h.EmptySpotInList();
            var end = h.TileCenter("Gamma");
            h.Window.MouseMove(start, RawInputModifiers.Control);
            h.Window.MouseDown(start, MouseButton.Left, RawInputModifiers.Control);
            h.Pump();
            h.Window.MouseMove(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2), RawInputModifiers.Control | RawInputModifiers.LeftMouseButton);
            h.Window.MouseMove(end, RawInputModifiers.Control | RawInputModifiers.LeftMouseButton);
            h.Pump();
            h.Window.MouseUp(end, MouseButton.Left, RawInputModifiers.Control);
            h.Pump();
            // The band covered Gamma, a.png and b.png: they toggle off; the rest stays selected.
            Assert.Equal(new[] { "Alpha", "Beta", "c.png" }, h.SelectedNames);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Escape_clears_the_selection()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("a.png");
            h.Click("b.png", modifiers: RawInputModifiers.Control);
            Assert.Equal(2, h.SelectionSet.Count);
            h.Key(Key.Escape);
            Assert.Empty(h.SelectedNames);
            Assert.Equal("", h.Surface.SelectionText.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Shift_Home_and_End_select_to_the_row_start_and_end_and_Ctrl_Shift_to_the_folder_ends()
    {
        await ExplorerHarness.Run(h =>
        {
            h.Click("Beta");
            h.Key(Key.End, RawInputModifiers.Shift);
            Assert.Equal(new[] { "a.png", "b.png", "Beta", "Gamma" }, h.SelectedNames);
            h.Click("Gamma");
            h.Key(Key.Home, RawInputModifiers.Shift);
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, h.SelectedNames);
            h.Click("Beta");
            h.Key(Key.End, RawInputModifiers.Shift | RawInputModifiers.Control);
            Assert.Equal(new[] { "a.png", "b.png", "Beta", "c.png", "Gamma" }, h.SelectedNames);
            h.Click("c.png");
            h.Key(Key.Home, RawInputModifiers.Shift | RawInputModifiers.Control);
            Assert.Equal(6, h.SelectionSet.Count);
            return Task.CompletedTask;
        });
    }
}
