namespace Glide.Core.Tests;

public sealed class OverlayAndTitleNavigationRegressionTests
{
    [Fact]
    public void Overlay_corner_resize_is_non_compounding_while_center_handles_are_crop_only()
    {
        var source = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        Assert.Contains("current.PreserveAspectRatio", source, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.05, 20.0)", source, StringComparison.Ordinal);
        Assert.Contains("ContentScale = contentScale", source, StringComparison.Ordinal);
        Assert.Contains("resize.Mode is ResizeMode.WidthOnly or ResizeMode.HeightOnly", source, StringComparison.Ordinal);
        Assert.Contains("Expansion is capped at the full source-image extent", source, StringComparison.Ordinal);
        Assert.Contains("ContentScale = resize.ContentScale", source, StringComparison.Ordinal);
        Assert.Contains("ZoomSelected", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_controls_autohide_but_remain_visible_during_interaction()
    {
        var source = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        Assert.Contains("var chromeVisible = _chromeVisibleId == s.Id", source, StringComparison.Ordinal);
        Assert.Contains("if (chromeVisible)", source, StringComparison.Ordinal);
        Assert.Contains("ShowTransientChrome(", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleChromeHide(immediate: true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_wheel_routes_to_overlay_zoom()
    {
        var source = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");
        Assert.Contains("OverlayGesturePolicy.Wheel(configured, enabled, e.Delta.Y)", source, StringComparison.Ordinal);
        Assert.Contains("ZoomSelected(action == OverlayGestureAction.ZoomIn ? step : 1.0 / step)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_folder_buttons_use_serialized_pointer_press_navigation()
    {
        var source = ReadSource("src", "Glide.App", "MainWindow.axaml.cs");
        Assert.Contains("else if (id is \"folder.previous\" or \"folder.next\")", source, StringComparison.Ordinal);
        Assert.Contains("await RunSerializedNavigationResultAsync(", source, StringComparison.Ordinal);
        Assert.Contains("TryNavigateSiblingFolderAsync(folderDirection)", source, StringComparison.Ordinal);
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
