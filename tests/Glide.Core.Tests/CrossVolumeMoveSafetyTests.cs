using System.Diagnostics;
using Glide.App.Explorer;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.1 audit item 1: a cross-volume move (copy, then remove the original) must never delete anything it did not
/// copy and verify. Files that arrive or change during the move stay in the source; folders are removed only once
/// empty. All tests force the cross-volume path with <see cref="ExplorerFileOperations.ForceCrossVolumeMoves"/> and
/// inject changes through the operation's test seams.
/// </summary>
[Collection("ExplorerFileOperations")]
public sealed class CrossVolumeMoveSafetyTests : IDisposable
{
    private readonly Action<string, string> _defaultCopier = ExplorerFileOperations.FileCopier;

    public CrossVolumeMoveSafetyTests() => ExplorerFileOperations.ForceCrossVolumeMoves = true;

    public void Dispose()
    {
        ExplorerFileOperations.ForceCrossVolumeMoves = false;
        ExplorerFileOperations.AfterFileCopiedForTests = null;
        ExplorerFileOperations.BeforeMoveSourceCleanupForTests = null;
        ExplorerFileOperations.FileCopier = _defaultCopier;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    private static List<string> Tree(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Rel(root, p)).OrderBy(p => p, StringComparer.Ordinal).ToList()
            : new List<string>();

    [Fact]
    public async Task File_added_after_the_snapshot_stays_in_the_source_and_is_reported()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt", "a");
        box.File("tree\\sub\\b.txt", "b");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        // Another program drops a file into the folder after it was copied, before the originals are removed.
        ExplorerFileOperations.BeforeMoveSourceCleanupForTests = _ => File.WriteAllText(Path.Combine(tree, "sub", "late.txt"), "late");

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Equal(new[] { Path.Combine(dest, "tree") }, result.Created);
        Assert.Equal("late", File.ReadAllText(Path.Combine(tree, "sub", "late.txt")));
        Assert.Equal(new[] { "sub", Path.Combine("sub", "late.txt") }, Tree(tree));
        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "tree", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "tree", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "tree", "sub", "late.txt")));
        var error = Assert.Single(result.Errors);
        Assert.Equal(tree, error.Path);
        Assert.Contains("1 item stayed because it changed or arrived during the move", error.Message);
    }

    [Fact]
    public async Task New_files_and_folders_arriving_together_are_counted_once_each()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt", "a");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        ExplorerFileOperations.BeforeMoveSourceCleanupForTests = _ =>
        {
            File.WriteAllText(Path.Combine(tree, "new1.txt"), "1");
            Directory.CreateDirectory(Path.Combine(tree, "newdir", "deeper"));
            File.WriteAllText(Path.Combine(tree, "newdir", "deeper", "x.txt"), "x");
        };

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.False(File.Exists(Path.Combine(tree, "a.txt")));
        Assert.True(File.Exists(Path.Combine(tree, "newdir", "deeper", "x.txt")));
        Assert.Contains(result.Errors, e => e.Message.Contains("2 items stayed because they changed or arrived during the move"));
    }

    [Fact]
    public async Task File_modified_after_it_was_copied_stays_and_its_outdated_copy_is_dropped()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt", "original");
        box.File("tree\\b.txt", "b");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        ExplorerFileOperations.BeforeMoveSourceCleanupForTests = _ =>
        {
            var a = Path.Combine(tree, "a.txt");
            File.WriteAllText(a, "edited by another program");
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(1));
        };

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Equal("edited by another program", File.ReadAllText(Path.Combine(tree, "a.txt")));
        Assert.False(File.Exists(Path.Combine(tree, "b.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "tree", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "tree", "b.txt")));
        Assert.Contains(result.Errors, e => e.Message.Contains("1 item stayed because it changed or arrived during the move"));
    }

    [Fact]
    public async Task File_changed_while_it_was_being_copied_stays()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("tree\\a.txt", "short");
        box.File("tree\\b.txt", "b");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        ExplorerFileOperations.AfterFileCopiedForTests = source =>
        {
            if (source == a) File.AppendAllText(a, " and longer now");
        };

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Equal("short and longer now", File.ReadAllText(a));
        Assert.False(File.Exists(Path.Combine(dest, "tree", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "tree", "b.txt")));
        Assert.False(File.Exists(Path.Combine(tree, "b.txt")));
        Assert.Contains(result.Errors, e => e.Message.Contains("1 item stayed"));
    }

    [Fact]
    public async Task Original_that_cannot_be_deleted_stays_with_a_clear_error_and_the_rest_moves()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        var held = box.File("tree\\held.txt", "held");
        box.File("tree\\free.txt", "free");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        // Readable (so it can be copied) but not deletable while this handle is open.
        using var hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.True(File.Exists(held));
        Assert.False(File.Exists(Path.Combine(tree, "free.txt")));
        Assert.Equal("held", File.ReadAllText(Path.Combine(dest, "tree", "held.txt")));
        Assert.Equal("free", File.ReadAllText(Path.Combine(dest, "tree", "free.txt")));
        var error = Assert.Single(result.Errors);
        Assert.Equal(held, error.Path);
        Assert.Contains("couldn't be removed", error.Message);
        Assert.Contains("in use", error.Message);
    }

    [Fact]
    public async Task Destination_write_failure_keeps_the_whole_source()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt", "a");
        var b = box.File("tree\\sub\\b.txt", "b");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        ExplorerFileOperations.FileCopier = (source, destination) =>
        {
            if (source == b) throw new IOException("disk full", unchecked((int)0x80070070));
            File.Copy(source, destination, overwrite: false);
        };

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Equal(new[] { "a.txt", "sub", Path.Combine("sub", "b.txt") }, Tree(tree));
        Assert.Contains(result.Errors, e => e.Path == b && e.Message.Contains("enough space"));
        Assert.Contains(result.Errors, e => e.Message.Contains("the original was kept because some items couldn't be copied"));
    }

    [Fact]
    public async Task Short_copy_is_detected_and_the_source_kept()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("tree\\a.txt", "complete content");
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        ExplorerFileOperations.FileCopier = (_, destination) => File.WriteAllText(destination, "comp");

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Equal("complete content", File.ReadAllText(a));
        Assert.False(File.Exists(Path.Combine(dest, "tree", "a.txt")));
        Assert.Contains(result.Errors, e => e.Message.Contains("incomplete"));
    }

    [Fact]
    public async Task Cancellation_halfway_keeps_the_source_intact()
    {
        using var box = new ExplorerOpsSandbox();
        for (var i = 0; i < 5; i++) box.File($"tree\\f{i}.txt", "x" + i);
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        var before = Tree(tree);
        using var cts = new CancellationTokenSource();
        var copies = 0;
        ExplorerFileOperations.AfterFileCopiedForTests = _ => { if (++copies == 2) cts.Cancel(); };

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true, cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(before, Tree(tree));
        for (var i = 0; i < 5; i++) Assert.Equal("x" + i, File.ReadAllText(Path.Combine(tree, $"f{i}.txt")));
    }

    [Fact]
    public async Task Empty_subfolders_and_nested_trees_move_completely()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\root.txt", "r");
        box.File("tree\\a\\b\\c\\d\\leaf.txt", "l");
        box.Dir("tree\\empty");
        box.Dir("tree\\a\\also-empty");
        var ro = box.File("tree\\a\\readonly.txt", "ro");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var tree = Path.Combine(box.Root, "tree");
        var dest = box.Dir("dest");
        var before = Tree(tree);

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(tree));
        Assert.Equal(before, Tree(Path.Combine(dest, "tree")));
        Assert.Equal("l", File.ReadAllText(Path.Combine(dest, "tree", "a", "b", "c", "d", "leaf.txt")));
    }

    [Fact]
    public async Task Junction_inside_the_folder_is_never_followed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        var outside = box.File("outside\\precious.txt", "keep me");
        box.File("tree\\a.txt", "a");
        var tree = Path.Combine(box.Root, "tree");
        var link = Path.Combine(tree, "link");
        if (!MakeJunction(link, Path.Combine(box.Root, "outside"))) return; // no mklink on this PC
        var dest = box.Dir("dest");

        var result = await ExplorerFileOperations.TransferAsync([tree], dest, move: true);

        Assert.Equal("keep me", File.ReadAllText(outside));
        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(tree, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "tree", "link")));
        Assert.Contains(result.Errors, e => e.Message.Contains("is a link and was skipped"));
        try { Directory.Delete(link); } catch { }
    }

    [Fact]
    public async Task A_selected_junction_is_not_moved_through()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        var outside = box.File("outside\\precious.txt", "keep me");
        var link = Path.Combine(box.Root, "linked");
        if (!MakeJunction(link, Path.Combine(box.Root, "outside"))) return;
        var dest = box.Dir("dest");

        var result = await ExplorerFileOperations.TransferAsync([link], dest, move: true);

        Assert.Equal("keep me", File.ReadAllText(outside));
        Assert.Empty(result.Created);
        Assert.False(Directory.Exists(Path.Combine(dest, "linked")));
        Assert.Contains(result.Errors, e => e.Message.Contains("is a link and was skipped"));
        try { Directory.Delete(link); } catch { }
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process is null) return false;
            process.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------ single files

    [Fact]
    public async Task Single_file_move_verifies_and_removes_the_original()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("src\\a.txt", "data");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var dest = box.Dir("dest");

        var result = await ExplorerFileOperations.TransferAsync([file], dest, move: true);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { Path.Combine(dest, "a.txt") }, result.Created);
        Assert.False(File.Exists(file));
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Single_file_changed_before_its_original_is_removed_stays_and_the_copy_is_dropped()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("src\\a.txt", "data");
        var dest = box.Dir("dest");
        ExplorerFileOperations.BeforeMoveSourceCleanupForTests = source => File.AppendAllText(source, " plus edits");

        var result = await ExplorerFileOperations.TransferAsync([file], dest, move: true);

        Assert.Equal("data plus edits", File.ReadAllText(file));
        Assert.Empty(result.Created);
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
        var error = Assert.Single(result.Errors);
        Assert.Contains("changed while it was being moved", error.Message);
    }

    [Fact]
    public async Task Single_file_whose_original_is_held_is_copied_and_reported()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        var file = box.File("src\\a.txt", "data");
        var dest = box.Dir("dest");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await ExplorerFileOperations.TransferAsync([file], dest, move: true);

        Assert.True(File.Exists(file));
        Assert.Equal(new[] { Path.Combine(dest, "a.txt") }, result.Created);
        var error = Assert.Single(result.Errors);
        Assert.Contains("couldn't be removed", error.Message);
    }

    [Fact]
    public async Task Same_volume_moves_still_rename_without_copying()
    {
        ExplorerFileOperations.ForceCrossVolumeMoves = false;
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt", "a");
        var file = box.File("b.txt", "b");
        var dest = box.Dir("dest");
        var copied = 0;
        ExplorerFileOperations.AfterFileCopiedForTests = _ => copied++;
        ExplorerFileOperations.BeforeMoveSourceCleanupForTests = _ => copied++;

        var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "tree"), file], dest, move: true);

        Assert.Empty(result.Errors);
        Assert.Equal(0, copied);
        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "tree", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "b.txt")));
    }

    [Fact]
    public async Task Cross_volume_move_with_a_name_clash_still_uses_copy_naming()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("src\\tree\\a.txt", "incoming");
        var dest = box.Dir("dest");
        box.File("dest\\tree\\a.txt", "existing");

        var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "src", "tree")], dest, move: true);

        Assert.Empty(result.Errors);
        Assert.Equal(new[] { Path.Combine(dest, "tree - Copy") }, result.Created);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "tree", "a.txt")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(dest, "tree - Copy", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(box.Root, "src", "tree")));
    }
}
