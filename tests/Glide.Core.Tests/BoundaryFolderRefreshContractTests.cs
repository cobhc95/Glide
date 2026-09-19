namespace Glide.Core.Tests;

public sealed class BoundaryFolderRefreshContractTests
{
    [Fact]
    public void Navigation_refreshes_current_folder_before_showing_branch_boundary()
    {
        var source = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");

        Assert.Contains("RefreshCurrentFolderIndexAtBoundaryAsync", source, StringComparison.Ordinal);
        Assert.Contains("ImageNavigator.EnumerateSupportedFolder(expectedPath)", source, StringComparison.Ordinal);
        Assert.Contains("_navigator.ReplaceFolderIndex(paths, expectedPath)", source, StringComparison.Ordinal);
        Assert.Contains("boundary_folder_refreshed", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
