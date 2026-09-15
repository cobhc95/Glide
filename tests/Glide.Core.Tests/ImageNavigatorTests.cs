using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public sealed class ImageNavigatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GlideTests_" + Guid.NewGuid().ToString("N"));

    public ImageNavigatorTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void OpenSingle_BuildsDeterministicSupportedFolderSession()
    {
        File.WriteAllBytes(Path.Combine(_dir, "b.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "a.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "skip.txt"), new byte[] { 1 });

        var nav = new ImageNavigator();
        nav.OpenSingle(Path.Combine(_dir, "b.png"));

        Assert.Equal(2, nav.Count);
        Assert.EndsWith("b.png", nav.Current, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "a.jpg", "b.png" }, nav.Paths.Select(Path.GetFileName));
    }

    [Fact]
    public void Move_ClampsAtFolderBoundaries()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "b.jpg"), new byte[] { 1 });
        var nav = new ImageNavigator();
        nav.OpenSingle(Path.Combine(_dir, "a.jpg"));
        nav.Move(-5);
        Assert.Equal(0, nav.Index);
        nav.Move(50);
        Assert.Equal(1, nav.Index);
    }

    [Fact]
    public void BoundedFolderEnumerationRetainsRequestedImageAndExactNeighbour()
    {
        for (var i = 0; i < 6; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"{i:00}.jpg"), new byte[] { 1 });

        var requested = Path.Combine(_dir, "05.jpg");
        var provisional = ImageNavigator.EnumerateSupportedFolder(requested, maxCount: 2);

        Assert.Equal(2, provisional.Length);
        Assert.Contains(provisional, path => string.Equals(path, requested, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "04.jpg", "05.jpg" }, provisional.Select(Path.GetFileName));
    }

    [Fact]
    public void BoundedFolderEnumerationIsCanonicalSubsequenceAroundLateRequestedImage()
    {
        for (var i = 0; i < 200; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"{i:000}.jpg"), new byte[] { 1 });

        var requested = Path.Combine(_dir, "150.jpg");
        var provisional = ImageNavigator.EnumerateSupportedFolder(requested, maxCount: 64);
        var canonical = ImageNavigator.EnumerateSupportedFolder(requested);
        var canonicalNames = canonical.Select(Path.GetFileName).ToArray();
        var provisionalNames = provisional.Select(Path.GetFileName).ToArray();

        Assert.Equal(64, provisional.Length);
        Assert.Contains("149.jpg", provisionalNames);
        Assert.Contains("150.jpg", provisionalNames);
        Assert.Contains("151.jpg", provisionalNames);
        var positions = provisionalNames.Select(name => Array.IndexOf(canonicalNames, name)).ToArray();
        Assert.True(positions.Zip(positions.Skip(1), (a, b) => b == a + 1).All(x => x));
    }

    [Fact]
    public void ReconcileKeepsProvisionalCurrentPath()
    {
        var first = Path.Combine(_dir, "a.jpg");
        var second = Path.Combine(_dir, "b.jpg");
        File.WriteAllBytes(first, new byte[] { 1 });
        File.WriteAllBytes(second, new byte[] { 1 });

        var nav = new ImageNavigator();
        nav.ReplaceFolderIndex(new[] { first }, first);
        nav.ReplaceFolderIndex(new[] { first, second }, second);

        Assert.True(string.Equals(second, nav.Current, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, nav.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
