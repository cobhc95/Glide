namespace Glide.Core.Tests;

[Trait("Area", "Explorer")]
public sealed class ExplorerFolderWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlideExWatch_" + Guid.NewGuid().ToString("N"));
    public ExplorerFolderWatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { TempDir.TryDelete(_dir); }
    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Explorer_batcher_reports_folders_and_non_image_files()
    {
        var b = new FolderChangeBatcher(FolderChangeBatcher.IsRelevantForExplorer);
        b.OnCreatedOrChanged(P("New folder"));
        b.OnCreatedOrChanged(P("notes.txt"));
        b.OnDeleted(P("old.docx"));
        var set = b.Flush(_ => true, _ => true);
        Assert.Equal(new[] { P("New folder"), P("notes.txt") }, set.Added.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { P("old.docx") }, set.Removed);
    }

    [Fact]
    public void Default_batcher_still_ignores_folders_and_other_files()
    {
        var b = new FolderChangeBatcher();
        b.OnCreatedOrChanged(P("New folder"));
        b.OnCreatedOrChanged(P("notes.txt"));
        Assert.False(b.HasPending);
    }

    [Fact]
    public void Explorer_batcher_turns_a_rename_chain_into_removals_plus_the_final_add()
    {
        var b = new FolderChangeBatcher(FolderChangeBatcher.IsRelevantForExplorer);
        b.OnRenamed(P("a"), P("b"));
        b.OnRenamed(P("b"), P("c"));
        var set = b.Flush(p => p == P("c"), _ => true);
        Assert.Equal(new[] { P("c") }, set.Added);
        Assert.Equal(new[] { P("a"), P("b") }, set.Removed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Entry_helpers_see_folders_and_empty_files()
    {
        Directory.CreateDirectory(P("sub"));
        File.WriteAllBytes(P("empty.txt"), []);
        Assert.True(FolderChangeBatcher.EntryExists(P("sub")));
        Assert.True(FolderChangeBatcher.EntryExists(P("empty.txt")));
        Assert.False(FolderChangeBatcher.EntryExists(P("missing")));
        Assert.True(FolderChangeBatcher.IsEntryReady(P("sub")));
        Assert.True(FolderChangeBatcher.IsEntryReady(P("empty.txt")));
        using (new FileStream(P("locked.bin"), FileMode.Create, FileAccess.Write, FileShare.None))
            Assert.False(FolderChangeBatcher.IsEntryReady(P("locked.bin")));
    }

    [Fact]
    public async Task Explorer_watch_reports_a_new_folder_a_rename_and_a_deletion()
    {
        var sets = new System.Collections.Concurrent.ConcurrentQueue<FolderChangeSet>();
        using var watch = new FolderWatchService((_, s) => sets.Enqueue(s), TimeSpan.FromMilliseconds(50), explorer: true);
        File.WriteAllText(P("keep.txt"), "x");
        watch.Watch(_dir);
        Directory.CreateDirectory(P("Folder1"));
        await WaitFor(() => sets.Any(s => s.Added.Contains(P("Folder1"))));
        Directory.Move(P("Folder1"), P("Folder2"));
        await WaitFor(() => sets.Any(s => s.Added.Contains(P("Folder2"))) && sets.Any(s => s.Removed.Contains(P("Folder1"))));
        File.Delete(P("keep.txt"));
        await WaitFor(() => sets.Any(s => s.Removed.Contains(P("keep.txt"))));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
