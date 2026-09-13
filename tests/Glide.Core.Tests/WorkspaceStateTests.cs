using Glide.Core.Workspace;
using Xunit;

namespace Glide.Core.Tests;

public sealed class WorkspaceStateTests
{
    [Fact]
    public void NewWorkspace_StartsWithOneHomeTab()
    {
        var workspace = new WorkspaceState();
        Assert.Single(workspace.Tabs);
        Assert.IsType<HomeTabState>(workspace.Active);
    }

    [Fact]
    public void ReplaceActiveWithImage_ReusesSelectedTabIdentity()
    {
        var workspace = new WorkspaceState();
        var selectedId = workspace.Active!.Id;
        var path = Path.Combine(Path.GetTempPath(), "same-tab-open.jpg");

        Assert.True(workspace.ReplaceActiveWithImage(path));
        var image = Assert.IsType<ImageTabState>(workspace.Active);
        Assert.Equal(selectedId, image.Id);
        Assert.Equal(Path.GetFullPath(path), image.Path);
        Assert.Single(workspace.Tabs);
    }

    [Fact]
    public void CloseAndRestore_ReopensEquivalentImageTab()
    {
        var workspace = new WorkspaceState();
        var path = Path.Combine(Path.GetTempPath(), "sample.jpg");
        workspace.AddImage(path);
        workspace.CloseActive();
        Assert.Equal(1, workspace.ClosedCount);
        var restored = workspace.RestoreClosed();
        var image = Assert.IsType<ImageTabState>(restored);
        Assert.Equal(Path.GetFullPath(path), image.Path);
        Assert.Equal(image.Id, workspace.Active?.Id);
    }

    [Fact]
    public void SelectRelative_WrapsInBothDirections()
    {
        var workspace = new WorkspaceState();
        workspace.AddHome();
        workspace.AddHome();
        var last = workspace.Active?.Id;
        workspace.SelectRelative(1);
        Assert.Equal(workspace.Tabs[0].Id, workspace.Active?.Id);
        workspace.SelectRelative(-1);
        Assert.Equal(last, workspace.Active?.Id);
    }
    [Fact]
    public void Move_ReordersTabAndKeepsMovedTabActive()
    {
        var workspace = new WorkspaceState();
        var first = workspace.Tabs[0].Id;
        var moved = workspace.AddHome().Id;
        workspace.AddHome();

        Assert.True(workspace.Move(moved, 0));
        Assert.Equal(moved, workspace.Tabs[0].Id);
        Assert.Equal(moved, workspace.Active?.Id);
        Assert.Equal(first, workspace.Tabs[1].Id);
    }

    [Fact]
    public void Move_ToEndUsesInsertionSlotSemantics()
    {
        var workspace = new WorkspaceState();
        var first = workspace.Tabs[0].Id;
        var second = workspace.AddHome().Id;
        var third = workspace.AddHome().Id;

        Assert.True(workspace.Move(first, workspace.Tabs.Count));
        Assert.Equal(new[] { second, third, first }, workspace.Tabs.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void Transfer_PreservesStableIdentityAndState()
    {
        var source = new WorkspaceState(createHome: false);
        var path = Path.Combine(Path.GetTempPath(), "transfer.jpg");
        var image = source.AddImage(path);
        var transferred = source.RemoveForTransfer(image.Id);

        var target = new WorkspaceState(createHome: false);
        target.InsertTransferred(Assert.IsType<ImageTabState>(transferred));

        var restored = Assert.IsType<ImageTabState>(target.Active);
        Assert.Equal(image.Id, restored.Id);
        Assert.Equal(Path.GetFullPath(path), restored.Path);
        Assert.Empty(source.Tabs);
    }

    [Fact]
    public void Transfer_PreservesTypedImageViewState()
    {
        var source = new WorkspaceState(createHome: false);
        var original = Assert.IsType<ImageTabState>(source.AddImage(Path.Combine(Path.GetTempPath(), "view-state.jpg")));
        var withView = original with { ViewState = new ImageTabViewState("Manual", 2.75, 84, -31) };
        Assert.True(source.ReplaceTab(withView));

        var transferred = Assert.IsType<ImageTabState>(source.RemoveForTransfer(original.Id));
        var target = new WorkspaceState(createHome: false);
        target.InsertTransferred(transferred);

        var received = Assert.IsType<ImageTabState>(target.Active);
        Assert.Equal(original.Id, received.Id);
        Assert.Equal(new ImageTabViewState("Manual", 2.75, 84, -31), received.ViewState);
    }

    [Fact]
    public void Transfer_PreservesTypedBrowserNavigationState()
    {
        var source = new WorkspaceState(createHome: false);
        var original = Assert.IsType<BrowserTabState>(source.AddBrowser(Path.GetTempPath()));
        var navigation = new BrowserNavigationState(new[] { "C:\\one", "C:\\two" }, 1);
        Assert.True(source.ReplaceTab(original with { Navigation = navigation }));

        var transferred = Assert.IsType<BrowserTabState>(source.RemoveForTransfer(original.Id));
        var target = new WorkspaceState(createHome: false);
        target.InsertTransferred(transferred);

        Assert.Equal(navigation, Assert.IsType<BrowserTabState>(target.Active).Navigation);
    }

    [Fact]
    public void ClosedHistory_RespectsConfiguredLimit()
    {
        var workspace = new WorkspaceState { ClosedHistoryLimit = 2 };
        workspace.AddHome();
        workspace.AddHome();
        workspace.AddHome();

        workspace.CloseActive();
        workspace.CloseActive();
        workspace.CloseActive();

        Assert.Equal(2, workspace.ClosedCount);
    }

}
