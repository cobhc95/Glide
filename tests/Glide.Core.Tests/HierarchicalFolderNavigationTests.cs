using Glide.App.Services;
using Xunit;

namespace Glide.Core.Tests;

public sealed class HierarchicalFolderNavigationTests
{
    [Fact]
    public async Task NextAcrossAncestorBoundaryAndPreviousAreExactReverse()
    {
        using var tree = new FolderTree();
        var b1 = tree.Image("B/1", "one.jpg");
        var b2 = tree.Image("B/2", "two.jpg");
        var b3 = tree.Image("B/3", "three.jpg");
        var c = tree.Image("C", "c.jpg");
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true);

        var next = await SiblingFolderNavigationService.FindAsync(b3, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(c), next!.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(next.CrossedAncestorBoundary);

        var previous = await SiblingFolderNavigationService.FindAsync(c, -1, options, false, Supported);
        Assert.NotNull(previous);
        Assert.True(string.Equals(Path.GetDirectoryName(b3), previous!.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(string.Equals(b3, previous.SelectedPath, StringComparison.OrdinalIgnoreCase));
        Assert.True(previous.CrossedAncestorBoundary);

        var direct = await SiblingFolderNavigationService.FindAsync(b2, 1, options, false, Supported);
        Assert.NotNull(direct);
        Assert.True(string.Equals(Path.GetDirectoryName(b3), direct!.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.False(direct.CrossedAncestorBoundary);
        _ = b1;
    }

    [Fact]
    public async Task EmptyAdjacentBranchDescendsToFirstPictureFolder()
    {
        using var tree = new FolderTree();
        var current = tree.Image("B/3", "three.jpg");
        var target = tree.Image("C/1", "target.jpg");
        Directory.CreateDirectory(Path.Combine(tree.Root, "C"));
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true);

        var next = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(target), next!.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(next.CrossedAncestorBoundary);
    }

    [Fact]
    public async Task HierarchicalTraversalCanBeDisabledIndependently()
    {
        using var tree = new FolderTree();
        var current = tree.Image("B/3", "three.jpg");
        _ = tree.Image("C", "target.jpg");
        var options = new SiblingFolderNavigationOptions(false, false, true, true, false);

        var next = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);
        Assert.Null(next);
    }

    private static bool Supported(string path) => string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase);

    private sealed class FolderTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "GlideFolderNav_" + Guid.NewGuid().ToString("N"));
        public FolderTree() => Directory.CreateDirectory(Root);
        public string Image(string relativeFolder, string name)
        {
            var folder = Path.Combine(Root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [0]);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
