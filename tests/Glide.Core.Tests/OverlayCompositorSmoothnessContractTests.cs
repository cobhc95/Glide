namespace Glide.Core.Tests;

public sealed class OverlayCompositorSmoothnessContractTests
{
    [Fact]
    public void Windows_overlay_motion_uses_independent_native_presenter_with_compositor_fallback()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs").Replace("\r\n", "\n");
        var renderer = ReadSource("src", "Glide.App", "Controls", "CompositorOverlayRenderer.cs").Replace("\r\n", "\n");

        Assert.Contains("_surface.EnableNativeWindowRenderer(owner);", manager, StringComparison.Ordinal);
        Assert.Contains("private readonly List<Item> _orderedItems = new();", renderer, StringComparison.Ordinal);
        Assert.Contains("foreach (var item in _orderedItems)", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_items.Values.OrderBy", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("ToDictionary(item => item.Id, item => new Point", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("AdvanceAxis(", renderer, StringComparison.Ordinal);
        Assert.Contains("AddBoundarySteer", renderer, StringComparison.Ordinal);
        Assert.Contains("RotateTowards", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_navigation_does_not_gate_on_unconditional_startup_timing_fence()
    {
        var mainWindow = ReadSource("src", "Glide.App", "MainWindow.axaml.cs").Replace("\r\n", "\n");
        var nativeRenderer = ReadSource("src", "Glide.App", "Controls", "NativeOverlayWindowRenderer.cs").Replace("\r\n", "\n");
        var diagnostics = ReadSource("src", "Glide.Diagnostics", "Runtime", "LiveDiagnosticTrace.cs").Replace("\r\n", "\n");

        Assert.Contains("_startupFirstImageTimingWritten = true;", mainWindow, StringComparison.Ordinal);
        Assert.Contains("flags |= SWP_NOSIZE;", nativeRenderer, StringComparison.Ordinal);
        Assert.Contains("ConcurrentQueue<string>", diagnostics, StringComparison.Ordinal);
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
