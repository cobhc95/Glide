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
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true, "Alphabetical", HierarchicalPreviousOpenFirstImage: false);

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
    public async Task PreviousAcrossAncestorBoundaryDefaultsToFirstImageAndCanUseLastImage()
    {
        using var tree = new FolderTree();
        var previousFirst = tree.Image("B/3", "a.jpg");
        var previousLast = tree.Image("B/3", "z.jpg");
        var current = tree.Image("C", "current.jpg");

        var defaultOptions = new SiblingFolderNavigationOptions(false, false, true, true, true);
        var first = await SiblingFolderNavigationService.FindAsync(current, -1, defaultOptions, false, Supported);
        Assert.NotNull(first);
        Assert.True(first!.CrossedAncestorBoundary);
        Assert.True(string.Equals(previousFirst, first.SelectedPath, StringComparison.OrdinalIgnoreCase));

        var lastOptions = defaultOptions with { HierarchicalPreviousOpenFirstImage = false };
        var last = await SiblingFolderNavigationService.FindAsync(current, -1, lastOptions, false, Supported);
        Assert.NotNull(last);
        Assert.True(string.Equals(previousLast, last!.SelectedPath, StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task NextDoesNotSkipEmptySiblingBranchWithNestedPictures()
    {
        using var tree = new FolderTree();
        var current = tree.Image("B/1", "current.jpg");
        var nested = tree.Image("B/2/inner", "nested.jpg");
        _ = tree.Image("B/3", "later.jpg");
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true);

        var next = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);

        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(nested), next!.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.True(string.Equals(nested, next.SelectedPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwentyTwoSiblingFoldersAreVisitedWithoutSkipping()
    {
        using var tree = new FolderTree();
        var paths = new List<string>();
        for (var i = 1; i <= 22; i++)
            paths.Add(tree.Image($"A/{i:D2}", $"{i:D2}.jpg"));
        _ = tree.Image("B/01", "b.jpg");

        var options = new SiblingFolderNavigationOptions(false, false, true, true, true);
        var current = paths[0];

        for (var expected = 1; expected < paths.Count; expected++)
        {
            var next = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);
            Assert.NotNull(next);
            Assert.True(string.Equals(Path.GetDirectoryName(paths[expected]), next!.Folder, StringComparison.OrdinalIgnoreCase),
                $"Expected sibling {expected + 1:D2}, got {next.Folder}");
            current = next.SelectedPath;
        }

        var crossBranch = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);
        Assert.NotNull(crossBranch);
        Assert.True(crossBranch!.Folder.EndsWith(Path.Combine("B", "01"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AlphabeticalFolderOrderMatchesWindowsNaturalNumberOrdering()
    {
        using var tree = new FolderTree();
        var one = tree.Image("A/1", "1.jpg");
        var two = tree.Image("A/2", "2.jpg");
        var ten = tree.Image("A/10", "10.jpg");
        var eleven = tree.Image("A/11", "11.jpg");
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true, "Alphabetical");

        var next = await SiblingFolderNavigationService.FindAsync(one, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(two), next!.Folder, StringComparison.OrdinalIgnoreCase));

        next = await SiblingFolderNavigationService.FindAsync(next.SelectedPath, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(ten), next!.Folder, StringComparison.OrdinalIgnoreCase));

        next = await SiblingFolderNavigationService.FindAsync(next.SelectedPath, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(eleven), next!.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NaturalSiblingTraversalIsSymmetricInBothDirections()
    {
        using var tree = new FolderTree();
        var names = new[] { "1", "2", "3", "10", "11", "20", "21" };
        var paths = names.Select(name => tree.Image($"A/{name}", $"{name}.jpg")).ToArray();
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true, "Alphabetical");

        var current = paths[0];
        var forward = new List<string> { Path.GetDirectoryName(current)! };
        for (var i = 1; i < paths.Length; i++)
        {
            var next = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);
            Assert.NotNull(next);
            forward.Add(next!.Folder);
            current = next.SelectedPath;
        }

        var backward = new List<string> { Path.GetDirectoryName(current)! };
        for (var i = paths.Length - 2; i >= 0; i--)
        {
            var previous = await SiblingFolderNavigationService.FindAsync(current, -1, options, false, Supported);
            Assert.NotNull(previous);
            backward.Add(previous!.Folder);
            current = previous.SelectedPath;
        }

        Assert.Equal(
            forward.Select(Path.GetFullPath),
            backward.AsEnumerable().Reverse().Select(Path.GetFullPath));
    }

    [Fact]
    public async Task WindowsLogicalOrderHandlesThousandsStyleFolderNames()
    {
        using var tree = new FolderTree();
        var nine = tree.Image("A/9,000", "9.jpg");
        var ten = tree.Image("A/10,000", "10.jpg");
        var eleven = tree.Image("A/11,000", "11.jpg");
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true, "Alphabetical");

        var next = await SiblingFolderNavigationService.FindAsync(nine, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(ten), next!.Folder, StringComparison.OrdinalIgnoreCase));

        next = await SiblingFolderNavigationService.FindAsync(next.SelectedPath, 1, options, false, Supported);
        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(eleven), next!.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WindowsLogicalOrderHandlesMixedNumericTextFolderNamesSymmetrically()
    {
        using var tree = new FolderTree();
        var names = new[] { "Folder 2", "Folder 9", "Folder 10", "Folder 11", "Folder 20" };
        var paths = names.Select(name => tree.Image($"A/{name}", $"{name}.jpg")).ToArray();
        var options = new SiblingFolderNavigationOptions(false, false, true, true, true, "Alphabetical");

        var current = paths[0];
        for (var i = 1; i < paths.Length; i++)
        {
            var next = await SiblingFolderNavigationService.FindAsync(current, 1, options, false, Supported);
            Assert.NotNull(next);
            Assert.True(string.Equals(Path.GetDirectoryName(paths[i]), next!.Folder, StringComparison.OrdinalIgnoreCase));
            current = next.SelectedPath;
        }

        for (var i = paths.Length - 2; i >= 0; i--)
        {
            var previous = await SiblingFolderNavigationService.FindAsync(current, -1, options, false, Supported);
            Assert.NotNull(previous);
            Assert.True(string.Equals(Path.GetDirectoryName(paths[i]), previous!.Folder, StringComparison.OrdinalIgnoreCase));
            current = previous.SelectedPath;
        }
    }

    [Fact]
    public async Task FolderOrderDefaultsToAlphabeticalAndSupportsModifiedDate()
    {
        using var tree = new FolderTree();
        var current = tree.Image("B", "current.jpg");
        var alphabeticalTarget = tree.Image("C", "alphabetical.jpg");
        _ = tree.Image("A", "newest.jpg");
        Directory.SetLastWriteTimeUtc(Path.Combine(tree.Root, "C"), DateTime.UnixEpoch.AddDays(1));
        Directory.SetLastWriteTimeUtc(Path.Combine(tree.Root, "B"), DateTime.UnixEpoch.AddDays(2));
        Directory.SetLastWriteTimeUtc(Path.Combine(tree.Root, "A"), DateTime.UnixEpoch.AddDays(3));

        var alphabetical = await SiblingFolderNavigationService.FindAsync(
            current, 1, new SiblingFolderNavigationOptions(false, false, true, true, true), false, Supported);
        var modified = await SiblingFolderNavigationService.FindAsync(
            current, 1, new SiblingFolderNavigationOptions(false, false, true, true, true, "Modified date (oldest first)"), false, Supported);

        Assert.NotNull(alphabetical);
        Assert.True(string.Equals(Path.GetDirectoryName(alphabeticalTarget), alphabetical!.Folder, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(modified);
        Assert.True(string.Equals(Path.Combine(tree.Root, "A"), modified!.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreationDateOrderingIsDeterministic()
    {
        using var tree = new FolderTree();
        var current = tree.Image("B", "current.jpg");
        var target = tree.Image("A", "newest.jpg");
        _ = tree.Image("C", "oldest.jpg");
        Directory.SetCreationTimeUtc(Path.Combine(tree.Root, "C"), DateTime.UnixEpoch.AddDays(1));
        Directory.SetCreationTimeUtc(Path.Combine(tree.Root, "B"), DateTime.UnixEpoch.AddDays(2));
        Directory.SetCreationTimeUtc(Path.Combine(tree.Root, "A"), DateTime.UnixEpoch.AddDays(3));

        var next = await SiblingFolderNavigationService.FindAsync(
            current, 1, new SiblingFolderNavigationOptions(false, false, true, true, true, "Creation date (oldest first)"), false, Supported);

        Assert.NotNull(next);
        Assert.True(string.Equals(Path.GetDirectoryName(target), next!.Folder, StringComparison.OrdinalIgnoreCase));
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
