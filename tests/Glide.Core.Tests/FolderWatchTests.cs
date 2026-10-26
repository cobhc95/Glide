using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public sealed class FolderWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlideWatch_" + Guid.NewGuid().ToString("N"));
    public FolderWatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string P(string name) => Path.Combine(_dir, name);

    private ImageNavigator Nav(string current, params string[] names)
    {
        var nav = new ImageNavigator();
        nav.ReplaceFolderIndex(names.Select(P), P(current));
        return nav;
    }

    [Fact]
    public void ApplyChanges_InsertsInSortedOrder_AndKeepsCurrent()
    {
        var nav = Nav("c.jpg", "a.jpg", "c.jpg", "e.jpg");
        Assert.True(nav.ApplyChanges(new[] { P("D.png"), P("b.jpg"), P("z.webp") }, []));
        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg", "D.png", "e.jpg", "z.webp" }, nav.Paths.Select(Path.GetFileName));
        Assert.Equal(P("c.jpg"), nav.Current);
        Assert.Equal(2, nav.Index);
    }

    [Fact]
    public void ApplyChanges_IgnoresDuplicatesAndUnsupported()
    {
        var nav = Nav("a.jpg", "a.jpg");
        Assert.False(nav.ApplyChanges(new[] { P("A.JPG"), P("notes.txt") }, []));
        Assert.Equal(1, nav.Count);
    }

    [Fact]
    public void ApplyChanges_RemovingOtherFile_KeepsCurrent()
    {
        var nav = Nav("c.jpg", "a.jpg", "b.jpg", "c.jpg");
        Assert.True(nav.ApplyChanges([], new[] { P("a.jpg") }));
        Assert.Equal(P("c.jpg"), nav.Current);
        Assert.Equal(1, nav.Index);
    }

    [Fact]
    public void ApplyChanges_RemovingCurrent_SelectsNeighbourInSlot()
    {
        var nav = Nav("b.jpg", "a.jpg", "b.jpg", "c.jpg");
        Assert.True(nav.ApplyChanges([], new[] { P("b.jpg") }));
        Assert.Equal(P("c.jpg"), nav.Current);
        var last = Nav("c.jpg", "a.jpg", "b.jpg", "c.jpg");
        last.ApplyChanges([], new[] { P("c.jpg") });
        Assert.Equal(P("b.jpg"), last.Current);
    }

    [Fact]
    public void ApplyChanges_RenameOfCurrent_FollowsNeighbourSlot_AndAddsNewName()
    {
        var nav = Nav("b.jpg", "a.jpg", "b.jpg", "c.jpg");
        nav.ApplyChanges(new[] { P("x.jpg") }, new[] { P("b.jpg") });
        Assert.Equal(new[] { "a.jpg", "c.jpg", "x.jpg" }, nav.Paths.Select(Path.GetFileName));
        Assert.Equal(P("c.jpg"), nav.Current);
    }

    [Theory]
    [InlineData("photo.jpg.crdownload", true)]
    [InlineData("photo.part", true)]
    [InlineData("photo.tmp", true)]
    [InlineData("x.download", true)]
    [InlineData("~photo.jpg", true)]
    [InlineData("photo.jpg", false)]
    public void IsIgnoredName_FiltersPartialDownloads(string name, bool ignored)
        => Assert.Equal(ignored, FolderChangeBatcher.IsIgnoredName(name));

    [Fact]
    public void Batcher_CoalescesEvents_AndHandlesDownloadRename()
    {
        var b = new FolderChangeBatcher();
        b.OnCreatedOrChanged(P("a.jpg.crdownload"));
        b.OnCreatedOrChanged(P("a.jpg.crdownload"));
        b.OnRenamed(P("a.jpg.crdownload"), P("a.jpg"));
        b.OnCreatedOrChanged(P("a.jpg"));
        b.OnCreatedOrChanged(P("gone.png"));
        b.OnDeleted(P("gone.png"));
        b.OnCreatedOrChanged(P("readme.txt"));
        var set = b.Flush(_ => true, _ => true);
        Assert.Equal(new[] { P("a.jpg") }, set.Added);
        Assert.Equal(new[] { P("gone.png") }, set.Removed);
        Assert.False(set.RescanRequired);
        Assert.False(b.HasPending);
    }

    [Fact]
    public void Batcher_LockedFile_RetriesUntilReadable()
    {
        var b = new FolderChangeBatcher();
        b.OnCreatedOrChanged(P("big.png"));
        var readable = false;
        var first = b.Flush(_ => true, _ => readable);
        Assert.Empty(first.Added);
        Assert.True(b.HasPending);
        readable = true;
        var second = b.Flush(_ => true, _ => readable);
        Assert.Equal(new[] { P("big.png") }, second.Added);
        Assert.False(b.HasPending);
    }

    [Fact]
    public void Batcher_GivesUpAfterMaxAttempts()
    {
        var b = new FolderChangeBatcher { MaxReadAttempts = 3 };
        b.OnCreatedOrChanged(P("stuck.png"));
        for (var i = 0; i < 3; i++) b.Flush(_ => true, _ => false);
        Assert.False(b.HasPending);
    }

    [Fact]
    public void Batcher_Overflow_RequestsRescan()
    {
        var b = new FolderChangeBatcher();
        b.OnCreatedOrChanged(P("a.jpg"));
        b.OnOverflow();
        var set = b.Flush(_ => true, _ => true);
        Assert.True(set.RescanRequired);
        Assert.Empty(set.Added);
    }

    [Fact]
    public async Task Service_PicksUpCopiedFile_AfterDebounce()
    {
        var tcs = new TaskCompletionSource<FolderChangeSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = new FolderWatchService((_, set) => { if (set.Added.Count > 0) tcs.TrySetResult(set); }, TimeSpan.FromMilliseconds(100));
        svc.Watch(_dir);
        File.WriteAllBytes(P("new.part"), new byte[] { 1, 2, 3 });
        File.Move(P("new.part"), P("new.png"));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, done);
        Assert.Contains(P("new.png"), tcs.Task.Result.Added, StringComparer.OrdinalIgnoreCase);
    }
}
