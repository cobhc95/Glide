using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>5.1.0: "Move tab to new window" leaves the source like closing the tab would, and the new window stays on screen.</summary>
public sealed class TabMoveToWindowTests
{
    private static (WorkspaceState W, Guid[] Ids) Four()
    {
        var w = new WorkspaceState(createHome: false);
        var ids = Enumerable.Range(0, 4).Select(_ => w.AddHome().Id).ToArray();
        return (w, ids);
    }

    [Theory]
    [InlineData(TabCloseActivationMode.Right, 2)]
    [InlineData(TabCloseActivationMode.Left, 0)]
    [InlineData(TabCloseActivationMode.PreviouslyUsed, 3)]
    public void Moving_the_active_tab_activates_what_closing_it_would(TabCloseActivationMode mode, int expectedOriginalIndex)
    {
        var (w, ids) = Four();
        w.Select(ids[1]);
        var moved = w.RemoveForTransfer(ids[1], mode, [ids[1], ids[3], ids[0]]);
        Assert.Equal(ids[1], moved!.Id);
        Assert.Equal(3, w.Tabs.Count);
        Assert.DoesNotContain(w.Tabs, t => t.Id == ids[1]);
        Assert.Equal(ids[expectedOriginalIndex], w.Active!.Id);
        var (closeW, closeIds) = Four();
        closeW.Select(closeIds[1]);
        closeW.Close(closeIds[1], false, mode, [closeIds[1], closeIds[3], closeIds[0]]);
        Assert.Equal(Array.IndexOf(closeIds, closeW.Active!.Id), expectedOriginalIndex);
    }

    [Fact]
    public void Moving_a_background_tab_keeps_the_active_tab()
    {
        var (w, ids) = Four();
        w.Select(ids[2]);
        w.RemoveForTransfer(ids[0], TabCloseActivationMode.Right, null);
        Assert.Equal(ids[2], w.Active!.Id);
    }

    [Fact]
    public void Moving_the_only_tab_leaves_no_tab_and_no_closed_history()
    {
        var w = new WorkspaceState(createHome: false);
        var only = w.AddHome().Id;
        Assert.NotNull(w.RemoveForTransfer(only, TabCloseActivationMode.PreviouslyUsed, null));
        Assert.Empty(w.Tabs);
        Assert.Null(w.Active);
        Assert.Null(w.RestoreClosed());
    }

    private static readonly PixelBox Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Menu_move_cascades_from_the_source_when_it_fits()
    {
        var box = DetachedWindowPlacement.ForMenuMove(new PixelBox(100, 100, 1200, 800), Work);
        Assert.Equal(new PixelBox(132, 132, 1200, 800), box);
    }

    [Fact]
    public void Menu_move_centres_when_the_cascade_would_leave_the_work_area()
    {
        var box = DetachedWindowPlacement.ForMenuMove(new PixelBox(700, 200, 1200, 800), Work);
        Assert.Equal(new PixelBox(360, 120, 1200, 800), box);
    }

    [Fact]
    public void Menu_move_of_a_window_as_large_as_the_screen_fits_inside()
    {
        var box = DetachedWindowPlacement.ForMenuMove(new PixelBox(0, 0, 1920, 1080), Work);
        Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= 1920 && box.Bottom <= 1040);
    }

    [Fact]
    public void Drag_out_keeps_the_pointer_grab_but_never_leaves_the_work_area()
    {
        Assert.Equal(new PixelBox(400, 300, 800, 600), DetachedWindowPlacement.ForDragOut(500, 324, 100, 24, 800, 600, Work));
        var low = DetachedWindowPlacement.ForDragOut(1800, 1000, 100, 24, 800, 600, Work);
        Assert.Equal(new PixelBox(1120, 440, 800, 600), low);
        var offLeft = DetachedWindowPlacement.ForDragOut(10, 5, 100, 24, 800, 600, new PixelBox(-1920, 0, 1920, 1040) with { X = 0 });
        Assert.Equal(0, offLeft.X);
        Assert.Equal(0, offLeft.Y);
    }
}
