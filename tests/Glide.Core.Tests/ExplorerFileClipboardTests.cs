using Glide.App.Explorer;

namespace Glide.Core.Tests;

[Collection("ExplorerFileOperations")]
[Trait("Area", "Explorer")]
public sealed class ExplorerFileClipboardTests
{
    [Fact]
    public void Memory_clipboard_is_empty_until_set()
    {
        var clipboard = new MemoryExplorerFileClipboard();
        Assert.Null(clipboard.TryGet());
    }

    [Fact]
    public void Memory_clipboard_round_trips_paths_and_cut_flag()
    {
        var clipboard = new MemoryExplorerFileClipboard();
        var paths = new List<string> { "C:\\a\\one.txt", "C:\\a\\two" };

        Assert.True(clipboard.TrySet(paths, cut: false));
        var copied = clipboard.TryGet();
        Assert.NotNull(copied);
        Assert.False(copied!.Cut);
        Assert.Equal(new[] { "C:\\a\\one.txt", "C:\\a\\two" }, copied.Paths);

        Assert.True(clipboard.TrySet(paths, cut: true));
        Assert.True(clipboard.TryGet()!.Cut);
    }

    [Fact]
    public void Memory_clipboard_snapshots_the_input_list()
    {
        var clipboard = new MemoryExplorerFileClipboard();
        var paths = new List<string> { "C:\\a.txt" };
        clipboard.TrySet(paths, cut: false);
        paths.Add("C:\\b.txt");
        Assert.Equal(new[] { "C:\\a.txt" }, clipboard.TryGet()!.Paths);
    }

    [Fact]
    public void Memory_clipboard_rejects_empty_sets_and_keeps_previous_content()
    {
        var clipboard = new MemoryExplorerFileClipboard();
        clipboard.TrySet(new[] { "C:\\a.txt" }, cut: true);
        Assert.False(clipboard.TrySet(Array.Empty<string>(), cut: false));
        Assert.False(clipboard.TrySet(new[] { "  " }, cut: false));
        Assert.True(clipboard.TryGet()!.Cut);
    }

    [Fact]
    public void Memory_clipboard_clear_empties_it()
    {
        var clipboard = new MemoryExplorerFileClipboard();
        clipboard.TrySet(new[] { "C:\\a.txt" }, cut: false);
        clipboard.Clear();
        Assert.Null(clipboard.TryGet());
        clipboard.Clear(); // idempotent
    }

    [Fact]
    public void Current_clipboard_can_be_swapped_for_tests()
    {
        var original = ExplorerFileClipboard.Current;
        Assert.NotNull(original);
        try
        {
            var memory = new MemoryExplorerFileClipboard();
            ExplorerFileClipboard.Current = memory;
            Assert.Same(memory, ExplorerFileClipboard.Current);
        }
        finally { ExplorerFileClipboard.Current = original; }
    }

    [Fact]
    public void Drop_files_buffer_round_trips_without_touching_the_clipboard()
    {
        var paths = new[] { "C:\\a\\one.txt", "D:\\folder with spaces\\\u00E9\u4E2D.png" };
        var buffer = WindowsExplorerFileClipboard.BuildDropFiles(paths);

        Assert.Equal(20, BitConverter.ToInt32(buffer, 0));  // pFiles
        Assert.Equal(1, BitConverter.ToInt32(buffer, 16));  // fWide
        Assert.Equal(0, buffer[^1]);
        Assert.Equal(0, buffer[^2]);                         // double NUL terminator
        Assert.Equal(paths, WindowsExplorerFileClipboard.ParseDropFiles(buffer));
    }

    [Fact]
    public void Drop_files_parser_tolerates_garbage()
    {
        Assert.Empty(WindowsExplorerFileClipboard.ParseDropFiles(new byte[5]));
        var bad = new byte[40];
        BitConverter.GetBytes(999).CopyTo(bad, 0); // pFiles past the end
        Assert.Empty(WindowsExplorerFileClipboard.ParseDropFiles(bad));
    }

    /// <summary>
    /// Overwrites the real clipboard, so it only runs when GLIDE_TEST_REAL_CLIPBOARD=1 (manual check).
    /// </summary>
    [Fact]
    public void Windows_clipboard_round_trips_paths_and_cut_flag()
    {
        if (Environment.GetEnvironmentVariable("GLIDE_TEST_REAL_CLIPBOARD") != "1") return;
        if (!OperatingSystem.IsWindows()) return;

        using var box = new ExplorerOpsSandbox();
        var file = box.File("clip\\a.txt");
        var folder = box.Dir("clip\\folder");
        var clipboard = new WindowsExplorerFileClipboard();

        Assert.True(clipboard.TrySet(new[] { file, folder }, cut: false));
        var copied = clipboard.TryGet();
        Assert.NotNull(copied);
        Assert.False(copied!.Cut);
        Assert.Equal(new[] { file, folder }, copied.Paths);

        Assert.True(clipboard.TrySet(new[] { file, folder }, cut: true));
        Assert.True(clipboard.TryGet()!.Cut);

        // Vanished paths are filtered out on read.
        File.Delete(file);
        Assert.Equal(new[] { folder }, clipboard.TryGet()!.Paths);

        clipboard.Clear();
        Assert.Null(clipboard.TryGet());
    }
}
