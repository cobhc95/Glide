using Glide.App.Explorer;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>Issue #4: the batch rename is all or nothing. Temp folders only.</summary>
[Trait("Area", "Explorer")]
[Collection("BatchRenameExecutor")]
public sealed class BatchRenameExecutorTests : IDisposable
{
    private readonly TempDir _dir = new("glide-batchrename");

    public void Dispose()
    {
        BatchRenameExecutor.MoveEntry = (from, to, isDirectory) => { if (isDirectory) Directory.Move(from, to); else File.Move(from, to); };
        _dir.Dispose();
    }

    private string Make(string name, string? content = null)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, content ?? "content of " + name);
        return path;
    }

    private string[] Names() => Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void Renames_every_item_and_keeps_the_content()
    {
        var a = Make("a.jpg"); var b = Make("b.png");
        var result = BatchRenameExecutor.Execute([(a, _dir.Combine("one.jpg")), (b, _dir.Combine("two.png"))]);
        Assert.True(result.Success, result.Error);
        Assert.Equal(["one.jpg", "two.png"], Names());
        Assert.Equal("content of a.jpg", File.ReadAllText(_dir.Combine("one.jpg")));
        Assert.Equal([(a, _dir.Combine("one.jpg")), (b, _dir.Combine("two.png"))], result.Renamed);
    }

    [Fact]
    public void A_swap_and_a_chain_work_through_temporary_names()
    {
        var a = Make("a.jpg"); var b = Make("b.jpg"); var c = Make("c.jpg");
        var result = BatchRenameExecutor.Execute([(a, b), (b, a)]);
        Assert.True(result.Success, result.Error);
        Assert.Equal("content of a.jpg", File.ReadAllText(b));
        Assert.Equal("content of b.jpg", File.ReadAllText(a));

        // a -> b -> c -> d: every target is another item's old name except the last
        var d = _dir.Combine("d.jpg");
        var chain = BatchRenameExecutor.Execute([(a, b), (b, c), (c, d)]);
        Assert.True(chain.Success, chain.Error);
        Assert.Equal(["b.jpg", "c.jpg", "d.jpg"], Names());
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void A_case_only_change_works()
    {
        var a = Make("photo.jpg");
        var result = BatchRenameExecutor.Execute([(a, _dir.Combine("PHOTO.jpg"))]);
        Assert.True(result.Success, result.Error);
        Assert.Equal(["PHOTO.jpg"], Names());
    }

    [Fact]
    public void Folders_are_renamed_too()
    {
        Directory.CreateDirectory(_dir.Combine("old"));
        File.WriteAllText(_dir.Combine("old", "x.txt"), "x");
        var f = Make("f.txt");
        var result = BatchRenameExecutor.Execute([(_dir.Combine("old"), _dir.Combine("new")), (f, _dir.Combine("g.txt"))]);
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(_dir.Combine("new", "x.txt")));
        Assert.Equal(["g.txt", "new"], Names());
    }

    [Fact]
    public void A_failure_halfway_puts_everything_back()
    {
        var a = Make("a.jpg"); var b = Make("b.jpg"); var c = Make("c.jpg");
        var calls = 0;
        // Phase 1 is three moves to temporary names, phase 2 three moves to the final names: fail on the fifth move overall.
        BatchRenameExecutor.MoveEntry = (from, to, isDirectory) =>
        {
            if (++calls == 5) throw new IOException("The file is in use.");
            File.Move(from, to);
        };
        var result = BatchRenameExecutor.Execute([(a, _dir.Combine("x1.jpg")), (b, _dir.Combine("x2.jpg")), (c, _dir.Combine("x3.jpg"))]);
        Assert.False(result.Success);
        Assert.False(result.RollbackIncomplete);
        Assert.StartsWith("Nothing was renamed:", result.Error);
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], Names());
        Assert.Equal("content of b.jpg", File.ReadAllText(b));
    }

    [Fact]
    public void A_failure_while_moving_aside_also_restores_the_originals()
    {
        var a = Make("a.jpg"); var b = Make("b.jpg");
        var calls = 0;
        BatchRenameExecutor.MoveEntry = (from, to, isDirectory) =>
        {
            if (++calls == 2) throw new UnauthorizedAccessException("denied");
            File.Move(from, to);
        };
        var result = BatchRenameExecutor.Execute([(a, b), (b, a)]);
        Assert.False(result.Success);
        Assert.Equal(["a.jpg", "b.jpg"], Names());
        Assert.Equal("content of a.jpg", File.ReadAllText(a));
    }

    [Fact]
    public void Nothing_is_touched_when_a_target_exists_outside_the_batch_or_a_source_is_gone_or_names_repeat()
    {
        var a = Make("a.jpg"); var other = Make("other.jpg");
        var taken = BatchRenameExecutor.Execute([(a, other)]);
        Assert.False(taken.Success);
        Assert.Contains("already a file or folder", taken.Error);

        var missing = BatchRenameExecutor.Execute([(_dir.Combine("ghost.jpg"), _dir.Combine("x.jpg"))]);
        Assert.False(missing.Success);
        Assert.Contains("no longer exists", missing.Error);

        var b = Make("b.jpg");
        var twice = BatchRenameExecutor.Execute([(a, _dir.Combine("same.jpg")), (b, _dir.Combine("SAME.jpg"))]);
        Assert.False(twice.Success);

        var illegal = BatchRenameExecutor.Execute([(a, _dir.Combine("con.jpg"))]);
        Assert.False(illegal.Success);
        Assert.Equal(["a.jpg", "b.jpg", "other.jpg"], Names());
    }

    [Fact]
    public void An_empty_batch_succeeds_without_doing_anything()
    {
        Assert.True(BatchRenameExecutor.Execute([]).Success);
    }
}
