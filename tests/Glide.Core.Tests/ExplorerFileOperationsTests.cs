using Glide.App.Explorer;

namespace Glide.Core.Tests;

/// <summary>Shared temp sandbox: every test works only inside Path.GetTempPath()/Glide.ExplorerOps.Tests/&lt;guid&gt;.</summary>
internal sealed class ExplorerOpsSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Glide.ExplorerOps.Tests", Guid.NewGuid().ToString("N"));

    public ExplorerOpsSandbox() => Directory.CreateDirectory(Root);

    public string Dir(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                try { System.IO.File.SetAttributes(entry, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(Root, recursive: true);
        }
        catch { /* best effort */ }
    }
}

internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

[Collection("ExplorerFileOperations")]
[Trait("Area", "Explorer")]
public sealed class ExplorerFileOperationsTests
{
    // ------------------------------------------------------------ ValidateName

    [Fact]
    public void ValidateName_accepts_ordinary_names()
    {
        using var box = new ExplorerOpsSandbox();
        Assert.Null(ExplorerFileOperations.ValidateName("photo.jpg", box.Root, null));
        Assert.Null(ExplorerFileOperations.ValidateName("My folder (2)", box.Root, null));
        Assert.Null(ExplorerFileOperations.ValidateName(".gitignore", box.Root, null));
        Assert.Null(ExplorerFileOperations.ValidateName("CONSOLE.txt", box.Root, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void ValidateName_rejects_empty_and_dot_names(string name)
    {
        using var box = new ExplorerOpsSandbox();
        Assert.NotNull(ExplorerFileOperations.ValidateName(name, box.Root, null));
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    public void ValidateName_rejects_invalid_characters_with_explorer_message(string name)
    {
        using var box = new ExplorerOpsSandbox();
        var message = ExplorerFileOperations.ValidateName(name, box.Root, null);
        Assert.Equal("A file name can't contain any of the following characters: \\ / : * ? \" < > |", message);
    }

    [Theory]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("name.txt.")]
    public void ValidateName_rejects_trailing_dot_or_space(string name)
    {
        using var box = new ExplorerOpsSandbox();
        Assert.NotNull(ExplorerFileOperations.ValidateName(name, box.Root, null));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("NUL.txt")]
    [InlineData("com3.tar.gz")]
    public void ValidateName_rejects_reserved_device_names(string name)
    {
        using var box = new ExplorerOpsSandbox();
        var message = ExplorerFileOperations.ValidateName(name, box.Root, null);
        Assert.NotNull(message);
        Assert.Contains("reserved", message);
    }

    [Fact]
    public void ValidateName_rejects_names_longer_than_255()
    {
        using var box = new ExplorerOpsSandbox();
        Assert.Null(ExplorerFileOperations.ValidateName(new string('a', 255), box.Root, null));
        Assert.NotNull(ExplorerFileOperations.ValidateName(new string('a', 256), box.Root, null));
    }

    [Fact]
    public void ValidateName_rejects_clash_case_insensitively_but_allows_the_entry_itself()
    {
        using var box = new ExplorerOpsSandbox();
        var existing = box.File("Photo.JPG");
        box.Dir("Folder");

        Assert.NotNull(ExplorerFileOperations.ValidateName("Photo.JPG", box.Root, null));
        Assert.NotNull(ExplorerFileOperations.ValidateName("photo.jpg", box.Root, null));
        Assert.NotNull(ExplorerFileOperations.ValidateName("folder", box.Root, null));

        // Renaming an entry to its own name, or changing only the case, is allowed.
        Assert.Null(ExplorerFileOperations.ValidateName("Photo.JPG", box.Root, existing));
        Assert.Null(ExplorerFileOperations.ValidateName("PHOTO.jpg", box.Root, existing));

        // ...but not to the name of a different entry.
        var other = box.File("Other.txt");
        Assert.NotNull(ExplorerFileOperations.ValidateName("photo.jpg", box.Root, other));
    }

    // ------------------------------------------------------------ Rename

    [Fact]
    public void Rename_file_returns_new_full_path_and_moves_content()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("a.txt", "hello");
        var result = ExplorerFileOperations.Rename(file, "b.txt");
        Assert.Equal(Path.Combine(box.Root, "b.txt"), result);
        Assert.False(File.Exists(file));
        Assert.Equal("hello", File.ReadAllText(result));
    }

    [Fact]
    public void Rename_folder_keeps_its_content()
    {
        using var box = new ExplorerOpsSandbox();
        box.File(Path.Combine("old", "inner.txt"), "i");
        var result = ExplorerFileOperations.Rename(Path.Combine(box.Root, "old"), "new");
        Assert.Equal(Path.Combine(box.Root, "new"), result);
        Assert.False(Directory.Exists(Path.Combine(box.Root, "old")));
        Assert.True(File.Exists(Path.Combine(result, "inner.txt")));
    }

    [Fact]
    public void Rename_case_only_works_via_temp_name()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("photo.jpg");
        var result = ExplorerFileOperations.Rename(file, "PHOTO.jpg");
        Assert.Equal(Path.Combine(box.Root, "PHOTO.jpg"), result);
        var names = Directory.EnumerateFileSystemEntries(box.Root).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "PHOTO.jpg" }, names);
    }

    [Fact]
    public void Rename_to_same_name_is_a_no_op()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("same.txt");
        Assert.Equal(file, ExplorerFileOperations.Rename(file, "same.txt"));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Rename_rejects_invalid_clashing_and_escaping_names()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("a.txt");
        box.File("b.txt");

        Assert.Throws<ExplorerOperationException>(() => ExplorerFileOperations.Rename(file, "b.txt"));
        Assert.Throws<ExplorerOperationException>(() => ExplorerFileOperations.Rename(file, "..\\escape.txt"));
        Assert.Throws<ExplorerOperationException>(() => ExplorerFileOperations.Rename(file, "sub/escape.txt"));
        var ex = Assert.Throws<ExplorerOperationException>(() => ExplorerFileOperations.Rename(file, "bad?"));
        Assert.Contains("can't contain", ex.Message);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Rename_missing_source_throws_user_facing_message()
    {
        using var box = new ExplorerOpsSandbox();
        var ex = Assert.Throws<ExplorerOperationException>(() => ExplorerFileOperations.Rename(Path.Combine(box.Root, "ghost.txt"), "x.txt"));
        Assert.Contains("no longer exists", ex.Message);
    }

    // ------------------------------------------------------------ CreateFolder / UniqueCopyName

    [Fact]
    public void CreateFolder_numbers_new_folders()
    {
        using var box = new ExplorerOpsSandbox();
        var first = ExplorerFileOperations.CreateFolder(box.Root);
        var second = ExplorerFileOperations.CreateFolder(box.Root);
        var third = ExplorerFileOperations.CreateFolder(box.Root);
        Assert.Equal(Path.Combine(box.Root, "New folder"), first);
        Assert.Equal(Path.Combine(box.Root, "New folder (2)"), second);
        Assert.Equal(Path.Combine(box.Root, "New folder (3)"), third);
        Assert.True(Directory.Exists(first) && Directory.Exists(second) && Directory.Exists(third));
    }

    [Fact]
    public void CreateFolder_skips_names_taken_by_files()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("New folder");
        Assert.Equal(Path.Combine(box.Root, "New folder (2)"), ExplorerFileOperations.CreateFolder(box.Root));
    }

    [Fact]
    public void CreateFolder_in_missing_parent_throws()
    {
        using var box = new ExplorerOpsSandbox();
        Assert.Throws<ExplorerOperationException>(() => ExplorerFileOperations.CreateFolder(Path.Combine(box.Root, "nope")));
    }

    [Fact]
    public void UniqueCopyName_returns_name_when_free()
    {
        using var box = new ExplorerOpsSandbox();
        Assert.Equal("a.txt", ExplorerFileOperations.UniqueCopyName(box.Root, "a.txt", false));
    }

    [Fact]
    public void UniqueCopyName_numbers_files_keeping_the_extension()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("a.txt");
        Assert.Equal("a - Copy.txt", ExplorerFileOperations.UniqueCopyName(box.Root, "a.txt", false));
        box.File("a - Copy.txt");
        Assert.Equal("a - Copy (2).txt", ExplorerFileOperations.UniqueCopyName(box.Root, "a.txt", false));
        box.File("a - Copy (2).txt");
        Assert.Equal("a - Copy (3).txt", ExplorerFileOperations.UniqueCopyName(box.Root, "a.txt", false));
    }

    [Fact]
    public void UniqueCopyName_handles_folders_without_extension_splitting()
    {
        using var box = new ExplorerOpsSandbox();
        box.Dir("my.folder");
        Assert.Equal("my.folder - Copy", ExplorerFileOperations.UniqueCopyName(box.Root, "my.folder", true));
        box.Dir("my.folder - Copy");
        Assert.Equal("my.folder - Copy (2)", ExplorerFileOperations.UniqueCopyName(box.Root, "my.folder", true));
    }

    [Fact]
    public void UniqueCopyName_handles_dotfiles_and_extensionless_files()
    {
        using var box = new ExplorerOpsSandbox();
        box.File(".gitignore");
        box.File("README");
        Assert.Equal(".gitignore - Copy", ExplorerFileOperations.UniqueCopyName(box.Root, ".gitignore", false));
        Assert.Equal("README - Copy", ExplorerFileOperations.UniqueCopyName(box.Root, "README", false));
    }

    // ------------------------------------------------------------ Copy

    [Fact]
    public async Task Copy_file_to_another_folder_keeps_the_original()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("src\\a.txt", "data");
        var dest = box.Dir("dest");
        var result = await ExplorerFileOperations.TransferAsync([src], dest, move: false);
        Assert.Empty(result.Errors);
        Assert.False(result.Cancelled);
        Assert.Equal(new[] { Path.Combine(dest, "a.txt") }, result.Created);
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Copy_into_same_folder_gets_copy_suffix_and_repeats_are_numbered()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("a.txt", "data");
        var first = await ExplorerFileOperations.TransferAsync([src], box.Root, move: false);
        var second = await ExplorerFileOperations.TransferAsync([src], box.Root, move: false);
        Assert.Equal(new[] { Path.Combine(box.Root, "a - Copy.txt") }, first.Created);
        Assert.Equal(new[] { Path.Combine(box.Root, "a - Copy (2).txt") }, second.Created);
        Assert.Equal("data", File.ReadAllText(Path.Combine(box.Root, "a - Copy (2).txt")));
    }

    [Fact]
    public async Task Copy_never_overwrites_a_clashing_destination_file()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("src\\a.txt", "new");
        var dest = box.Dir("dest");
        File.WriteAllText(Path.Combine(dest, "a.txt"), "old");
        var result = await ExplorerFileOperations.TransferAsync([src], dest, move: false);
        Assert.Equal(new[] { Path.Combine(dest, "a - Copy.txt") }, result.Created);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "a - Copy.txt")));
    }

    [Fact]
    public async Task Copy_folder_tree_recursively_preserving_structure_and_empty_folders()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\root.txt", "r");
        box.File("tree\\sub\\deep\\leaf.txt", "l");
        box.Dir("tree\\empty");
        var dest = box.Dir("dest");
        var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "tree")], dest, move: false);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { Path.Combine(dest, "tree") }, result.Created);
        Assert.Equal("r", File.ReadAllText(Path.Combine(dest, "tree", "root.txt")));
        Assert.Equal("l", File.ReadAllText(Path.Combine(dest, "tree", "sub", "deep", "leaf.txt")));
        Assert.True(Directory.Exists(Path.Combine(dest, "tree", "empty")));
        Assert.True(File.Exists(Path.Combine(box.Root, "tree", "sub", "deep", "leaf.txt")));
    }

    [Fact]
    public async Task Copy_folder_into_same_parent_gets_copy_suffix()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt");
        var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "tree")], box.Root, move: false);
        Assert.Equal(new[] { Path.Combine(box.Root, "tree - Copy") }, result.Created);
        Assert.True(File.Exists(Path.Combine(box.Root, "tree - Copy", "a.txt")));
    }

    [Fact]
    public async Task Copy_or_move_folder_into_itself_or_a_descendant_is_refused()
    {
        using var box = new ExplorerOpsSandbox();
        var tree = Path.Combine(box.Root, "tree");
        box.File("tree\\sub\\a.txt");
        foreach (var move in new[] { false, true })
        {
            var intoSelf = await ExplorerFileOperations.TransferAsync([tree], tree, move);
            var intoChild = await ExplorerFileOperations.TransferAsync([tree], Path.Combine(tree, "sub"), move);
            foreach (var result in new[] { intoSelf, intoChild })
            {
                Assert.Empty(result.Created);
                var error = Assert.Single(result.Errors);
                Assert.Contains("into itself", error.Message);
                Assert.Equal(tree, error.Path);
            }
        }
        Assert.Equal(new[] { "a.txt" }, Directory.GetFiles(Path.Combine(tree, "sub")).Select(Path.GetFileName).ToList());
        Assert.False(Directory.Exists(Path.Combine(tree, "sub", "tree")));
    }

    [Fact]
    public async Task Prefix_sibling_is_not_mistaken_for_a_descendant()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\a.txt");
        var sibling = box.Dir("tree2");
        var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "tree")], sibling, move: false);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(sibling, "tree", "a.txt")));
    }

    // ------------------------------------------------------------ Move

    [Fact]
    public async Task Move_file_removes_the_source()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("src\\a.txt", "data");
        var dest = box.Dir("dest");
        var result = await ExplorerFileOperations.TransferAsync([src], dest, move: true);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { Path.Combine(dest, "a.txt") }, result.Created);
        Assert.False(File.Exists(src));
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task Move_folder_tree_on_same_volume()
    {
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\sub\\a.txt", "a");
        var dest = box.Dir("dest");
        var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "tree")], dest, move: true);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { Path.Combine(dest, "tree") }, result.Created);
        Assert.False(Directory.Exists(Path.Combine(box.Root, "tree")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(dest, "tree", "sub", "a.txt")));
    }

    [Fact]
    public async Task Move_into_the_current_parent_is_a_silent_no_op()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("a.txt");
        box.File("tree\\b.txt");
        var result = await ExplorerFileOperations.TransferAsync([file, Path.Combine(box.Root, "tree")], box.Root, move: true);
        Assert.Empty(result.Created);
        Assert.Empty(result.Errors);
        Assert.False(result.Cancelled);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(box.Root, "tree", "b.txt")));
        Assert.False(File.Exists(Path.Combine(box.Root, "a - Copy.txt")));
    }

    [Fact]
    public async Task Move_with_name_clash_uses_copy_name_and_leaves_destination_untouched()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("src\\a.txt", "incoming");
        var dest = box.Dir("dest");
        File.WriteAllText(Path.Combine(dest, "a.txt"), "original");
        var result = await ExplorerFileOperations.TransferAsync([src], dest, move: true);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { Path.Combine(dest, "a - Copy.txt") }, result.Created);
        Assert.Equal("original", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(dest, "a - Copy.txt")));
        Assert.False(File.Exists(src));
    }

    [Fact]
    public async Task Cross_volume_style_move_copies_then_deletes_source_files_and_folders()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("src\\a.txt", "f");
        box.File("tree\\sub\\b.txt", "b");
        var dest = box.Dir("dest");
        ExplorerFileOperations.ForceCrossVolumeMoves = true;
        try
        {
            var result = await ExplorerFileOperations.TransferAsync([file, Path.Combine(box.Root, "tree")], dest, move: true);
            Assert.Empty(result.Errors);
            Assert.Equal(2, result.Created.Count);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(Path.Combine(box.Root, "tree")));
            Assert.Equal("f", File.ReadAllText(Path.Combine(dest, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(dest, "tree", "sub", "b.txt")));
        }
        finally { ExplorerFileOperations.ForceCrossVolumeMoves = false; }
    }

    [Fact]
    public async Task Cross_volume_style_folder_move_keeps_source_when_a_file_cannot_be_copied()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        box.File("tree\\ok.txt", "ok");
        var locked = box.File("tree\\locked.txt", "locked");
        var dest = box.Dir("dest");
        ExplorerFileOperations.ForceCrossVolumeMoves = true;
        try
        {
            using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await ExplorerFileOperations.TransferAsync([Path.Combine(box.Root, "tree")], dest, move: true);
            Assert.Contains(result.Errors, e => e.Path == locked && e.Message.Contains("in use"));
            Assert.True(File.Exists(locked)); // source kept: copy was not fully successful
            Assert.True(File.Exists(Path.Combine(box.Root, "tree", "ok.txt")));
        }
        finally { ExplorerFileOperations.ForceCrossVolumeMoves = false; }
    }

    // ------------------------------------------------------------ errors, batches

    [Fact]
    public async Task Missing_destination_is_an_error_result()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("a.txt");
        var result = await ExplorerFileOperations.TransferAsync([src], Path.Combine(box.Root, "nope"), move: false);
        Assert.Empty(result.Created);
        Assert.Single(result.Errors);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task Nonexistent_and_duplicate_sources_are_errors_and_the_batch_continues()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("src\\a.txt");
        var b = box.File("src\\b.txt");
        var ghost = Path.Combine(box.Root, "src", "ghost.txt");
        var dest = box.Dir("dest");
        var result = await ExplorerFileOperations.TransferAsync([a, ghost, a, b], dest, move: false);
        Assert.Equal(new[] { Path.Combine(dest, "a.txt"), Path.Combine(dest, "b.txt") }, result.Created);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Path == ghost && e.Message.Contains("no longer exists"));
        Assert.Contains(result.Errors, e => e.Path == a && e.Message.Contains("more than once"));
    }

    [Fact]
    public async Task Move_of_a_locked_file_reports_a_clear_error_and_continues()
    {
        if (!OperatingSystem.IsWindows()) return; // sharing violations are a Windows behaviour
        using var box = new ExplorerOpsSandbox();
        var locked = box.File("src\\locked.txt", "l");
        var free = box.File("src\\free.txt", "f");
        var dest = box.Dir("dest");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await ExplorerFileOperations.TransferAsync([locked, free], dest, move: true);

        var error = Assert.Single(result.Errors);
        Assert.Equal(locked, error.Path);
        Assert.Contains("locked.txt", error.Message);
        Assert.Contains("in use", error.Message);
        Assert.Equal(new[] { Path.Combine(dest, "free.txt") }, result.Created);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public async Task Copy_of_a_locked_file_reports_a_clear_error_and_continues()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        var locked = box.File("src\\locked.txt", "l");
        var free = box.File("src\\free.txt", "f");
        var dest = box.Dir("dest");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await ExplorerFileOperations.TransferAsync([locked, free], dest, move: false);

        var error = Assert.Single(result.Errors);
        Assert.Contains("locked.txt", error.Message);
        Assert.Contains("in use", error.Message);
        Assert.Equal(new[] { Path.Combine(dest, "free.txt") }, result.Created);
    }

    [Fact]
    public async Task Cancellation_before_start_does_nothing()
    {
        using var box = new ExplorerOpsSandbox();
        var src = box.File("src\\a.txt");
        var dest = box.Dir("dest");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await ExplorerFileOperations.TransferAsync([src], dest, move: false, cancellationToken: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(result.Created);
        Assert.Empty(Directory.GetFileSystemEntries(dest));
    }

    [Fact]
    public async Task Cancellation_between_items_keeps_finished_items()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("src\\a.txt");
        var b = box.File("src\\b.txt");
        var c = box.File("src\\c.txt");
        var dest = box.Dir("dest");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ExplorerProgress>(p => { if (p.Done == 1 && p.Total == 3) cts.Cancel(); });

        var result = await ExplorerFileOperations.TransferAsync([a, b, c], dest, move: false, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(new[] { Path.Combine(dest, "a.txt") }, result.Created);
        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "b.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "c.txt")));
    }

    [Fact]
    public async Task Progress_reports_top_level_items_with_verb_and_final_completion()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("src\\a.txt");
        var b = box.File("src\\b.txt");
        var c = box.File("src\\tree\\c.txt");
        var dest = box.Dir("dest");
        var reports = new List<ExplorerProgress>();
        var progress = new SyncProgress<ExplorerProgress>(reports.Add);

        await ExplorerFileOperations.TransferAsync([a, b, Path.Combine(box.Root, "src", "tree")], dest, move: false, progress);

        Assert.All(reports, r => { Assert.Equal(3, r.Total); Assert.Equal("Copying", r.Verb); });
        Assert.Equal(reports.Select(r => r.Done).OrderBy(d => d), reports.Select(r => r.Done));
        Assert.Equal(new[] { 0, 1, 2, 3 }, reports.Select(r => r.Done).Distinct());
        Assert.Equal("a.txt", reports[0].CurrentName);
        Assert.Equal(3, reports[^1].Done);

        reports.Clear();
        var moveSrc = box.File("m\\x.txt");
        await ExplorerFileOperations.TransferAsync([moveSrc], dest, move: true, progress);
        Assert.All(reports, r => Assert.Equal("Moving", r.Verb));
    }

    // ------------------------------------------------------------ Delete

    [Fact]
    public async Task Permanent_delete_removes_files_and_folder_trees_including_read_only()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("a.txt");
        var readOnly = box.File("ro.txt");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var inner = box.File("tree\\sub\\inner.txt");
        File.SetAttributes(inner, FileAttributes.ReadOnly);
        var tree = Path.Combine(box.Root, "tree");

        var result = await ExplorerFileOperations.DeleteAsync([file, readOnly, tree], permanent: true);

        Assert.Empty(result.Errors);
        Assert.False(result.Cancelled);
        Assert.Equal(new[] { file, readOnly, tree }, result.Deleted);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(readOnly));
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public async Task Delete_reports_missing_items_and_continues()
    {
        using var box = new ExplorerOpsSandbox();
        var ghost = Path.Combine(box.Root, "ghost.txt");
        var real = box.File("real.txt");
        var result = await ExplorerFileOperations.DeleteAsync([ghost, real], permanent: true);
        Assert.Equal(new[] { real }, result.Deleted);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ghost, error.Path);
        Assert.Contains("no longer exists", error.Message);
    }

    [Fact]
    public async Task Delete_ignores_duplicates_and_items_inside_a_listed_folder()
    {
        using var box = new ExplorerOpsSandbox();
        var inner = box.File("tree\\a.txt");
        var tree = Path.Combine(box.Root, "tree");
        var result = await ExplorerFileOperations.DeleteAsync([inner, tree, tree], permanent: true);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { tree }, result.Deleted);
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public async Task Permanent_delete_of_a_locked_file_reports_in_use_and_continues()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var box = new ExplorerOpsSandbox();
        var locked = box.File("locked.txt");
        var free = box.File("free.txt");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await ExplorerFileOperations.DeleteAsync([locked, free], permanent: true);

        var error = Assert.Single(result.Errors);
        Assert.Equal(locked, error.Path);
        Assert.Contains("locked.txt", error.Message);
        Assert.Contains("in use", error.Message);
        Assert.Equal(new[] { free }, result.Deleted);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task Recycle_delete_goes_through_the_recycler_seam_and_reports_failures()
    {
        using var box = new ExplorerOpsSandbox();
        var file = box.File("a.txt");
        var refused = box.File("refused.txt");
        var tree = Path.Combine(box.Root, "tree");
        box.File("tree\\x.txt");
        var calls = new List<(string Path, bool IsDirectory)>();
        var original = ExplorerFileOperations.Recycler;
        ExplorerFileOperations.Recycler = (path, isDirectory) =>
        {
            lock (calls) calls.Add((path, isDirectory));
            if (path == refused) return false;
            if (isDirectory) Directory.Delete(path, true); else File.Delete(path);
            return true;
        };
        try
        {
            var result = await ExplorerFileOperations.DeleteAsync([file, refused, tree], permanent: false);

            Assert.Equal(new[] { (file, false), (refused, false), (tree, true) }, calls);
            Assert.Equal(new[] { file, tree }, result.Deleted);
            var error = Assert.Single(result.Errors);
            Assert.Equal(refused, error.Path);
            Assert.Contains("Recycle Bin", error.Message);
            Assert.True(File.Exists(refused));
        }
        finally { ExplorerFileOperations.Recycler = original; }
    }

    [Fact]
    public async Task Recycle_delete_turns_recycler_exceptions_into_item_errors()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("a.txt");
        var b = box.File("b.txt");
        var original = ExplorerFileOperations.Recycler;
        ExplorerFileOperations.Recycler = (path, isDirectory) =>
        {
            if (path == a) throw new UnauthorizedAccessException();
            File.Delete(path);
            return true;
        };
        try
        {
            var result = await ExplorerFileOperations.DeleteAsync([a, b], permanent: false);
            Assert.Equal(new[] { b }, result.Deleted);
            var error = Assert.Single(result.Errors);
            Assert.Contains("Access is denied", error.Message);
        }
        finally { ExplorerFileOperations.Recycler = original; }
    }

    [Fact]
    public async Task Delete_progress_and_cancellation()
    {
        using var box = new ExplorerOpsSandbox();
        var a = box.File("a.txt");
        var b = box.File("b.txt");
        var reports = new List<ExplorerProgress>();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ExplorerProgress>(p =>
        {
            reports.Add(p);
            if (p.Done == 1) cts.Cancel();
        });

        var result = await ExplorerFileOperations.DeleteAsync([a, b], permanent: true, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(new[] { a }, result.Deleted);
        Assert.True(File.Exists(b));
        Assert.All(reports, r => { Assert.Equal("Deleting", r.Verb); Assert.Equal(2, r.Total); });
    }

    // ------------------------------------------------------------ DescribeError

    [Fact]
    public void DescribeError_maps_known_exceptions_to_clear_messages()
    {
        const string path = "C:\\x\\file.txt";
        Assert.Equal("'file.txt': Access is denied.", ExplorerFileOperations.DescribeError(new UnauthorizedAccessException(), path));
        Assert.Contains("too long", ExplorerFileOperations.DescribeError(new PathTooLongException(), path));
        Assert.Contains("in use by another program", ExplorerFileOperations.DescribeError(new IOException("x", unchecked((int)0x80070020)), path));
        Assert.Contains("in use by another program", ExplorerFileOperations.DescribeError(new IOException("x", unchecked((int)0x80070021)), path));
        Assert.Contains("can't be found", ExplorerFileOperations.DescribeError(new DirectoryNotFoundException(), path));
        Assert.Contains("can't be found", ExplorerFileOperations.DescribeError(new FileNotFoundException(), path));
        Assert.Contains("enough space", ExplorerFileOperations.DescribeError(new IOException("x", unchecked((int)0x80070070)), path));
        Assert.Equal("'file.txt': boom.", ExplorerFileOperations.DescribeError(new InvalidOperationException("boom"), path));
        Assert.Equal("Already user facing.", ExplorerFileOperations.DescribeError(new ExplorerOperationException("Already user facing."), path));
    }
}
