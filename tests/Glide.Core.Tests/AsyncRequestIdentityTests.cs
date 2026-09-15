using Glide.App.Services;

namespace Glide.Core.Tests;

public sealed class AsyncRequestIdentityTests
{
    [Fact]
    public void Same_path_from_older_A_request_is_rejected_after_A_B_A()
    {
        var window = Guid.NewGuid(); var tab = Guid.NewGuid();
        using var oldCts = new CancellationTokenSource();
        var oldA = new ImageRequestContext(window, tab, 7, 11, @"C:\pics\A.jpg", oldCts.Token);
        Assert.True(oldA.Matches(window, tab, 7, 11, @"C:\pics\A.jpg"));
        Assert.False(oldA.Matches(window, tab, 7, 13, @"C:\pics\A.jpg"));
    }

    [Fact]
    public void Workspace_or_tab_change_rejects_late_result_even_when_path_matches()
    {
        var window = Guid.NewGuid(); var tab = Guid.NewGuid();
        var request = new ImageRequestContext(window, tab, 3, 9, "A.jpg", CancellationToken.None);
        Assert.False(request.Matches(window, Guid.NewGuid(), 3, 9, "A.jpg"));
        Assert.False(request.Matches(window, tab, 4, 9, "A.jpg"));
    }

    [Fact]
    public void Folder_session_survives_neighbour_in_same_folder_but_not_tab_or_session_change()
    {
        var tab=Guid.NewGuid();
        var session=new FolderIndexSession(tab,5,2,"pics",CancellationToken.None);
        Assert.True(session.Matches(tab,5,2,Path.Combine("pics", "next.jpg")));
        Assert.False(session.Matches(tab,5,3,Path.Combine("pics", "next.jpg")));
        Assert.False(session.Matches(Guid.NewGuid(),5,2,Path.Combine("pics", "next.jpg")));
    }
}
