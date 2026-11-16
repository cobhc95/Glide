using Avalonia.Controls;
using Glide.App.Services;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// Tab reorder/attach drop index (the drop must match the last live preview / the insertion marker) and the
/// title-bar maximize/restore fence (same flash-free pipeline as fullscreen).
/// </summary>
public sealed class TabDropAndMaximizeTests
{
    [Theory]
    // 4 tabs, slot 100, dragging tab 1.
    [InlineData(1, 0, 1)]      // no move
    [InlineData(1, 49, 1)]     // under half a slot: no swap yet
    [InlineData(1, 50, 3)]     // half way across neighbour 2: swapped -> insert after it
    [InlineData(1, 149, 3)]
    [InlineData(1, 150, 4)]    // past two neighbours -> end
    [InlineData(1, 900, 4)]    // clamped
    [InlineData(1, -50, 0)]    // half way across neighbour 0
    [InlineData(1, -49, 1)]
    [InlineData(1, -900, 0)]
    public void PreviewInsertIndex_matches_half_slot_swap(int original, double offset, int expected)
        => Assert.Equal(expected, TabDragPolicy.PreviewInsertIndex(original, 4, offset, 100));

    [Theory]
    [InlineData(1, 60)]
    [InlineData(1, 160)]
    [InlineData(2, -60)]
    [InlineData(0, 250)]
    [InlineData(3, -260)]
    public void Committing_the_preview_index_produces_the_previewed_order(int original, double offset)
    {
        var state = new WorkspaceState(createHome: false);
        for (var i = 0; i < 4; i++) state.AddHome();
        var ids = state.Tabs.Select(t => t.Id).ToList();
        var dragged = ids[original];

        var insert = TabDragPolicy.PreviewInsertIndex(original, 4, offset, 100);
        state.Move(dragged, insert);

        var previewPosition = Math.Clamp(original + (int)Math.Round(offset / 100, MidpointRounding.AwayFromZero), 0, 3);
        Assert.Equal(previewPosition, state.Tabs.ToList().FindIndex(t => t.Id == dragged));
    }

    [Fact]
    public void InsertIndex_and_marker_agree_between_tabs()
    {
        var tabs = new List<(double, double)> { (0, 100), (103, 100), (206, 100) };
        Assert.Equal(0, TabDragPolicy.InsertIndexFromPointer(tabs, 10));
        Assert.Equal(1, TabDragPolicy.InsertIndexFromPointer(tabs, 60));
        Assert.Equal(1, TabDragPolicy.InsertIndexFromPointer(tabs, 140));
        Assert.Equal(2, TabDragPolicy.InsertIndexFromPointer(tabs, 200));
        Assert.Equal(3, TabDragPolicy.InsertIndexFromPointer(tabs, 400));

        Assert.Equal(0, TabDragPolicy.InsertMarkerPosition(tabs, 0, 3));
        Assert.Equal(101.5, TabDragPolicy.InsertMarkerPosition(tabs, 1, 3));
        Assert.Equal(204.5, TabDragPolicy.InsertMarkerPosition(tabs, 2, 3));
        Assert.Equal(307.5, TabDragPolicy.InsertMarkerPosition(tabs, 3, 3));
    }

    [Theory]
    [InlineData(WindowState.Normal, WindowState.Maximized, true)]
    [InlineData(WindowState.Maximized, WindowState.Normal, true)]
    [InlineData(WindowState.Maximized, WindowState.Maximized, false)]
    [InlineData(WindowState.FullScreen, WindowState.Normal, false)]
    public void Maximize_toggle_is_fenced_like_fullscreen(WindowState from, WindowState to, bool cloaked)
        => Assert.Equal(cloaked, FullscreenTransitionPlanner.PlanMaximizeToggleFence(from, to) == FullscreenTransitionFence.Cloak);
}
