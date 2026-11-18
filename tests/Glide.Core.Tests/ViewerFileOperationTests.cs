using Avalonia.Threading;
using Glide.App.Explorer;
using Glide.App.Services;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.1 audit item 4: the viewer's rename/recycle run their file system work on a worker thread (the UI thread keeps
/// pumping while a slow disk or network share answers) and rename shares the Explorer implementation, including
/// case-only renames.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ViewerFileOperationTests
{
    public ViewerFileOperationTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    [Fact]
    public async Task Case_only_rename_works_in_the_viewer_like_in_the_explorer()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("photo.jpg", "pixels");
        var operations = new FileOperationController();

        Assert.Equal(Path.Combine(box.Root, "PHOTO.jpg"), operations.ValidateRename(file, "PHOTO.jpg"));
        var renamed = await operations.RenameAsync(file, "PHOTO.jpg");

        Assert.Equal(Path.Combine(box.Root, "PHOTO.jpg"), renamed);
        Assert.Equal(new[] { "PHOTO.jpg" }, Directory.EnumerateFileSystemEntries(box.Root).Select(Path.GetFileName));
        Assert.Equal("pixels", File.ReadAllText(renamed!));

        // The Explorer path (same implementation) does the same for folders.
        box.Dir("album");
        Assert.Equal(Path.Combine(box.Root, "Album"), ExplorerFileOperations.Rename(Path.Combine(box.Root, "album"), "Album"));
        Assert.Contains("Album", Directory.EnumerateDirectories(box.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Rename_problems_come_back_as_the_shared_user_facing_messages()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("a.png");
        box.File("b.png");
        var operations = new FileOperationController();

        Assert.Contains("already a file or folder named", (await operations.TryRenameAsync(file, "B.PNG")).Error);
        Assert.Contains("can't contain", (await operations.TryRenameAsync(file, "bad?.png")).Error);
        Assert.Contains("can't contain", (await operations.TryRenameAsync(file, "..\\escape.png")).Error);
        Assert.Contains("reserved", (await operations.TryRenameAsync(file, "CON.png")).Error);
        Assert.Contains("too long", (await operations.TryRenameAsync(file, new string('x', 300) + ".png")).Error);
        Assert.Contains("unchanged", (await operations.TryRenameAsync(file, "a.png")).Error);
        Assert.Contains("no longer exists", (await operations.TryRenameAsync(Path.Combine(box.Root, "ghost.png"), "x.png")).Error);
        Assert.Null(await operations.RenameAsync(file, "b.png"));
        Assert.True(File.Exists(file));

        // A file system failure (here: the file is open in another program) surfaces as a message, nothing renamed.
        operations.Renamer = (_, _) => throw new IOException("sharing violation", unchecked((int)0x80070020));
        var failed = await operations.TryRenameAsync(file, "c.png");
        Assert.False(failed.Renamed);
        Assert.Contains("in use by another program", failed.Error);
        await Assert.ThrowsAnyAsync<IOException>(() => operations.RenameAsync(file, "c.png"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Read_only_and_long_path_files_can_be_renamed()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("ro.png", "r");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var operations = new FileOperationController();
        var renamed = await operations.TryRenameAsync(file, "ro2.png");
        Assert.True(renamed.Renamed, renamed.Error);
        Assert.True(File.Exists(renamed.NewPath));

        // A folder chain longer than MAX_PATH (long paths are enabled for .NET on Windows 10+).
        var deep = box.Root;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        var longFile = Path.Combine(deep, "long.png");
        File.WriteAllText(longFile, "l");
        var longRenamed = await operations.TryRenameAsync(longFile, "LONG.png");
        Assert.True(longRenamed.Renamed, longRenamed.Error);
        Assert.Equal("LONG.png", Path.GetFileName(Directory.EnumerateFiles(deep).Single()));
    }

    [Fact]
    public async Task Rename_and_recycle_never_block_the_ui_thread()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("slow.png", "s");
        var other = box.File("other.png", "o");

        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            var uiThread = Environment.CurrentManagedThreadId;
            using var gate = new ManualResetEventSlim();
            using var entered = new ManualResetEventSlim();
            var workerThread = -1;
            var operations = new FileOperationController
            {
                // A "network share" that takes as long as the test wants.
                Renamer = (path, name) =>
                {
                    workerThread = Environment.CurrentManagedThreadId;
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                    return ExplorerFileOperations.Rename(path, name);
                },
                Recycler = path =>
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                    File.Delete(path);
                    return true;
                },
            };

            var rename = operations.RenameAsync(file, "renamed.png");
            Assert.False(rename.IsCompleted);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the rename never started");
            // The dispatcher still runs work while the rename is stuck in the file system.
            var pumped = 0;
            for (var i = 0; i < 3; i++) await Dispatcher.UIThread.InvokeAsync(() => pumped++, DispatcherPriority.Background);
            Assert.Equal(3, pumped);
            Assert.False(rename.IsCompleted);
            Assert.NotEqual(uiThread, workerThread);
            gate.Set();
            Assert.Equal(Path.Combine(box.Root, "renamed.png"), await rename);
            Assert.True(Dispatcher.UIThread.CheckAccess()); // results come back on the UI thread

            gate.Reset();
            entered.Reset();
            var recycle = operations.RecycleAsync(other);
            Assert.False(recycle.IsCompleted);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Dispatcher.UIThread.InvokeAsync(() => pumped++, DispatcherPriority.Background);
            Assert.Equal(4, pumped);
            gate.Set();
            Assert.True(await recycle);
            Assert.True(Dispatcher.UIThread.CheckAccess());
            Assert.False(File.Exists(other));
            Assert.False(await operations.RecycleAsync(other)); // already gone
        });
    }

    [Fact]
    public async Task Concurrent_renames_to_one_name_never_overwrite_and_sequential_renames_chain()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("a.png", "a");
        var b = box.File("b.png", "b");
        var operations = new FileOperationController();

        var results = await Task.WhenAll(operations.TryRenameAsync(a, "target.png"), operations.TryRenameAsync(b, "target.png"));

        Assert.Equal(1, results.Count(r => r.Renamed));
        Assert.Equal(1, results.Count(r => !r.Renamed && r.Error is not null));
        Assert.Equal(2, Directory.EnumerateFiles(box.Root).Count());
        var winner = results.Single(r => r.Renamed).NewPath!;
        Assert.Contains(File.ReadAllText(winner), new[] { "a", "b" });

        var first = await operations.RenameAsync(winner, "step1.png");
        var second = await operations.RenameAsync(first!, "Step1.png");
        var third = await operations.RenameAsync(second!, "step2.png");
        Assert.Equal(Path.Combine(box.Root, "step2.png"), third);
        Assert.True(File.Exists(third));
    }

    [Fact]
    public async Task Cancelled_operations_do_nothing()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("keep.png");
        var operations = new FileOperationController();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.TryRenameAsync(file, "x.png", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.RecycleAsync(file, cts.Token));
        Assert.True(File.Exists(file));
    }
}
