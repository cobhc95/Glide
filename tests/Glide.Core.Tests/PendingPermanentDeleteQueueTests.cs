using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>5.2.1 G2: the queue and the hide list behind "Delayed permanent delete (Shift+Delete with undo)".</summary>
[Trait("Area", "Navigation")]
public sealed class PendingPermanentDeleteQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "glide-g2-" + Guid.NewGuid().ToString("N"));

    public PendingPermanentDeleteQueueTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Make(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string? Delete(string path) { File.Delete(path); return null; }

    [Fact]
    public void Enqueue_hides_the_file_from_listings_but_leaves_it_on_disk()
    {
        var a = Make("a.png"); var b = Make("b.png");
        var queue = new PendingPermanentDeleteQueue();
        Assert.NotNull(queue.Enqueue(a));
        Assert.True(File.Exists(a));                       // nothing moved, nothing deleted yet
        Assert.True(PendingDeleteHideList.IsHidden(a));
        Assert.False(PendingDeleteHideList.IsHidden(b));
        var navigator = new ImageNavigator();
        navigator.OpenSingle(b);
        Assert.Equal([b], navigator.Paths);                // the pending file is not in the folder list
        Assert.Equal([b], ImageNavigator.EnumerateSupportedFolder(b));
        Assert.Null(queue.Enqueue(a));                     // already pending
        queue.Undo(queue.Entries[0].Id);
    }

    [Fact]
    public void Undo_lists_the_file_again_and_nothing_is_deleted()
    {
        var a = Make("a.png"); var b = Make("b.png");
        var queue = new PendingPermanentDeleteQueue();
        var entry = queue.Enqueue(a)!;
        var undone = queue.Undo(entry.Id);
        Assert.Equal(entry.Id, undone?.Id);
        Assert.False(PendingDeleteHideList.IsHidden(a));
        Assert.True(File.Exists(a));
        var navigator = new ImageNavigator();
        navigator.OpenSingle(b);
        Assert.Equal([a, b], navigator.Paths);
        // The timer that fires after an Undo finds nothing to do.
        var late = queue.Complete(entry.Id, Delete);
        Assert.Equal(PendingDeleteOutcome.NotPending, late.Outcome);
        Assert.True(File.Exists(a));
        Assert.Null(queue.Undo(entry.Id));
    }

    [Fact]
    public void Complete_deletes_the_file_and_not_before()
    {
        var a = Make("a.png");
        var queue = new PendingPermanentDeleteQueue();
        var entry = queue.Enqueue(a)!;
        Assert.True(File.Exists(a));
        var result = queue.Complete(entry.Id, Delete);
        Assert.Equal(PendingDeleteOutcome.Deleted, result.Outcome);
        Assert.False(File.Exists(a));
        Assert.False(PendingDeleteHideList.IsHidden(a));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Several_pending_deletes_are_independent()
    {
        var a = Make("a.png"); var b = Make("b.png"); var c = Make("c.png");
        var queue = new PendingPermanentDeleteQueue();
        var ea = queue.Enqueue(a)!; var eb = queue.Enqueue(b)!; var ec = queue.Enqueue(c)!;
        queue.Undo(eb.Id);
        Assert.Equal(PendingDeleteOutcome.Deleted, queue.Complete(ea.Id, Delete).Outcome);
        Assert.True(File.Exists(b)); Assert.True(File.Exists(c));
        Assert.True(PendingDeleteHideList.IsHidden(c) && !PendingDeleteHideList.IsHidden(b));
        queue.Undo(ec.Id);
        Assert.True(File.Exists(c));
    }

    [Fact]
    public void A_file_that_changed_meanwhile_is_not_deleted_and_is_listed_again()
    {
        var a = Make("a.png", "one");
        var queue = new PendingPermanentDeleteQueue();
        var entry = queue.Enqueue(a)!;
        File.WriteAllText(a, "a much longer replacement");
        var result = queue.Complete(entry.Id, p => { Assert.Fail("must not delete a changed file"); return null; });
        Assert.Equal(PendingDeleteOutcome.Changed, result.Outcome);
        Assert.True(File.Exists(a));
        Assert.False(PendingDeleteHideList.IsHidden(a));
    }

    [Fact]
    public void A_file_that_is_already_gone_is_handled_quietly()
    {
        var a = Make("a.png");
        var queue = new PendingPermanentDeleteQueue();
        var entry = queue.Enqueue(a)!;
        File.Delete(a);
        var result = queue.Complete(entry.Id, p => { Assert.Fail("nothing to delete"); return null; });
        Assert.Equal(PendingDeleteOutcome.Gone, result.Outcome);
        Assert.False(PendingDeleteHideList.IsHidden(a));
    }

    [Fact]
    public void A_failed_delete_keeps_the_file_and_lists_it_again()
    {
        var a = Make("a.png");
        var queue = new PendingPermanentDeleteQueue();
        var entry = queue.Enqueue(a)!;
        var result = queue.Complete(entry.Id, _ => "locked");
        Assert.Equal(PendingDeleteOutcome.Failed, result.Outcome);
        Assert.Equal("locked", result.Error);
        Assert.True(File.Exists(a));
        Assert.False(PendingDeleteHideList.IsHidden(a));
    }

    [Fact]
    public void CompleteAll_flushes_every_pending_delete()
    {
        var a = Make("a.png"); var b = Make("b.png"); var c = Make("c.png", "keep");
        var queue = new PendingPermanentDeleteQueue();
        queue.Enqueue(a); queue.Enqueue(b); queue.Enqueue(c);
        File.WriteAllText(c, "changed after confirming");
        var results = queue.CompleteAll(Delete);
        Assert.Equal(3, results.Count);
        Assert.False(File.Exists(a)); Assert.False(File.Exists(b));
        Assert.True(File.Exists(c));
        Assert.Equal(0, queue.Count);
        Assert.False(PendingDeleteHideList.IsHidden(c));
    }

    [Fact]
    public void A_deletion_already_running_is_waited_for_before_exit()
    {
        var a = Make("a.png");
        var queue = new PendingPermanentDeleteQueue();
        var entry = queue.Enqueue(a)!;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = Task.Run(() => queue.Complete(entry.Id, p => { started.Set(); release.Wait(); File.Delete(p); return null; }));
        Assert.True(started.Wait(5000));
        Assert.Equal(0, queue.Count);                                  // left the queue, still deleting
        Assert.False(queue.WaitForInFlight(TimeSpan.FromMilliseconds(50)));
        release.Set();
        Assert.True(queue.WaitForInFlight(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(a));
        Assert.Equal(PendingDeleteOutcome.Deleted, worker.Result.Outcome);
    }

    [Fact]
    public void Folder_watcher_additions_of_a_pending_file_are_ignored()
    {
        var a = Make("a.png"); var b = Make("b.png");
        var queue = new PendingPermanentDeleteQueue();
        var navigator = new ImageNavigator();
        queue.Enqueue(a);
        navigator.OpenSingle(b);
        navigator.ApplyChanges([a], []);
        Assert.Equal([b], navigator.Paths);
        queue.CompleteAll(_ => null);
    }
}
