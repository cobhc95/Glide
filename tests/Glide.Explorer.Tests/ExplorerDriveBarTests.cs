using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Glide.App.Explorer;

namespace Glide.Explorer.Tests;

/// <summary>Round 3 #12: the drive bar is resized by dragging the divider, and the width is remembered.</summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerDriveBarTests
{
    [Fact]
    public void Nav_pane_width_is_clamped_and_unset_gives_the_default()
    {
        Assert.Equal(208, ExplorerLayout.ClampNavPaneWidth(0));
        Assert.Equal(208, ExplorerLayout.ClampNavPaneWidth(double.NaN));
        Assert.Equal(120, ExplorerLayout.ClampNavPaneWidth(30));
        Assert.Equal(600, ExplorerLayout.ClampNavPaneWidth(5000));
        Assert.Equal(301, ExplorerLayout.ClampNavPaneWidth(300.6));
    }

    [Fact]
    public async Task Dragging_the_divider_resizes_the_drive_bar_and_saves_the_width()
    {
        await ExplorerHarness.Run(h =>
        {
            Assert.Equal(208, h.Surface.NavPaneWidth);
            var grip = h.Surface.NavSplitter;
            var start = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), h.Window)!.Value;
            var end = start + new Point(90, 0);
            h.Window.MouseMove(start);
            h.Window.MouseDown(start, MouseButton.Left);
            h.Window.MouseMove(start + new Point(40, 0), RawInputModifiers.LeftMouseButton);
            h.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            h.Window.MouseUp(end, MouseButton.Left);
            h.Pump();
            Assert.Equal(298, h.Surface.NavPaneWidth);
            Assert.Equal(298, h.Settings.ExplorerNavPaneWidth);

            // A remembered width is applied when the surface is shown again (settings change re-applies it).
            h.Settings.ExplorerNavPaneWidth = 150;
            h.Call("ApplyExplorerNavPaneWidth");
            Assert.Equal(150, h.Surface.NavPaneWidth);
            h.Settings.ExplorerNavPaneWidth = 208; // the window is shared by the whole test run
            h.Call("ApplyExplorerNavPaneWidth");
            return Task.CompletedTask;
        });
    }
}
