namespace Glide.Core.Tests;

public sealed class OverlayControlVisibilityContractTests
{
    [Fact]
    public void Controls_and_glow_are_hover_or_interaction_only()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        var compositor = ReadSource("src", "Glide.App", "Controls", "CompositorOverlayRenderer.cs");

        Assert.DoesNotContain("OverlayChromeSelection.Controls |\n                OverlayChromeSelection.Controls", manager, StringComparison.Ordinal);
        Assert.Contains("_chromeVisibleId == state.Id || isInteracting ? OverlayChromeSelection.Controls", manager, StringComparison.Ordinal);
        Assert.Contains("_chromeVisibleId == state.Id || isInteracting ? OverlayChromeSelection.Active", manager, StringComparison.Ordinal);
        Assert.Contains("ShowTransientChrome(hover, hold: true)", manager, StringComparison.Ordinal);
        Assert.Contains("Controls = 1", compositor, StringComparison.Ordinal);
        Assert.Contains("Active = 2", compositor, StringComparison.Ordinal);
        Assert.Contains("if (showGlow)", compositor, StringComparison.Ordinal);
    }

    [Fact]
    public void Lower_right_resize_uses_large_zoom_independent_hit_target()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");

        Assert.Contains("ResizeRect(OverlayState s) => new(s.X + s.Width - 36", manager, StringComparison.Ordinal);
        Assert.Contains("ResizeRectVisual(OverlayState s)", manager, StringComparison.Ordinal);
        Assert.Contains("private int HitTestChrome(Point p)", manager, StringComparison.Ordinal);
        Assert.Contains("var chrome = HitTestChrome(p);", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Hover_wheel_zooms_pointer_owned_overlay_corner_resize_is_bounded_and_side_handles_crop()
    {
        var manager = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");

        Assert.Contains("var hit = HitTest(p);", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("if (hit < 0 && _selected is Guid selected)", manager, StringComparison.Ordinal);
        Assert.Contains("current.PreserveAspectRatio", manager, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.05, 20.0)", manager, StringComparison.Ordinal);
        Assert.Contains("resize.Mode is ResizeMode.WidthOnly or ResizeMode.HeightOnly", manager, StringComparison.Ordinal);
        Assert.Contains("ContentScale = resize.ContentScale", manager, StringComparison.Ordinal);
        Assert.Contains("pixelSize.Width) * renderedScale", manager, StringComparison.Ordinal);
        Assert.Contains("pixelSize.Height) * renderedScale", manager, StringComparison.Ordinal);
        Assert.Contains("ContentScale = contentScale", manager, StringComparison.Ordinal);
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
