namespace Glide.Core.Tests;

public sealed class FullscreenMouseNavigationDirectionContractTests
{
    [Fact]
    public void Fullscreen_left_click_advances_and_overlay_left_click_uses_same_direction()
    {
        var viewport = ReadSource("src", "Glide.App", "Controls", "ImageViewport.cs").Replace("\r\n", "\n");
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs").Replace("\r\n", "\n");
        var window = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");

        Assert.Contains("BrowseRequested?.Invoke(this, 1);", viewport, StringComparison.Ordinal);
        Assert.Contains("if (IsFullscreen && FullscreenClickNavigationEnabled) BrowseRequested?.Invoke(this, -1);", viewport, StringComparison.Ordinal);
        Assert.Contains("(IsFullscreen || _lastImageDest.Contains(point.Position))", viewport, StringComparison.Ordinal);
        Assert.Contains("FullscreenNavigationRequested?.Invoke(1);", manager, StringComparison.Ordinal);
        Assert.Contains("FullscreenNavigationRequested = direction => _ = NavigateMouseAsync(direction)", window, StringComparison.Ordinal);
        Assert.Contains("if (rightClickWithoutDrag)", manager, StringComparison.Ordinal);
        Assert.Contains("OpenContextMenu();", manager, StringComparison.Ordinal);
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
