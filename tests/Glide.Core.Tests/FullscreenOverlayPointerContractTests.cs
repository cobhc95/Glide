namespace Glide.Core.Tests;

public sealed class FullscreenOverlayPointerContractTests
{
    [Fact]
    public void Overlay_right_click_keeps_context_menu_and_pending_pan_is_not_interaction()
    {
        var source = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs").Replace("\r\n", "\n");

        Assert.Contains("_pan is { Started: true }", source, StringComparison.Ordinal);
        Assert.Contains("if (rightClickWithoutDrag)\n            // An overlay owns its right-click in every presentation mode. The canvas receives\n            // right-click navigation only when hit testing finds no overlay underneath it.\n            OpenContextMenu();",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (IsFullscreen?.Invoke() == true)", source, StringComparison.Ordinal);
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
