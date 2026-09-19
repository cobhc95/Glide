namespace Glide.Core.Tests;

public sealed class ExplorerRemovalAndOverlayChromeContractTests
{
    [Fact]
    public void Built_in_explorer_is_not_exposed_by_hotkeys_or_settings_catalog()
    {
        var hotkeys = ReadSource("src", "Glide.Core", "Commands", "HotkeyCatalog.cs");
        var settings = ReadSource("src", "Glide.Core", "Settings", "SettingsCatalog.cs");
        var titleButtons = ReadSource("src", "Glide.App", "Settings", "TitleBarButtonCatalog.cs");

        Assert.DoesNotContain("\"browser.back\"", hotkeys, StringComparison.Ordinal);
        Assert.DoesNotContain("\"browser.forward\"", hotkeys, StringComparison.Ordinal);
        Assert.DoesNotContain("\"browser.up\"", hotkeys, StringComparison.Ordinal);
        Assert.DoesNotContain("\"file.containing\"", hotkeys, StringComparison.Ordinal);
        Assert.DoesNotContain("\"appearance.explorerTheme\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tabs.navigateToFolderBehavior\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"file.locate\"", titleButtons, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_browser_tabs_are_migrated_to_home_at_activation()
    {
        var main = ReadSource("src", "Glide.App", "MainWindow.axaml.cs");
        Assert.Contains("legacy_browser_tab_migrated_to_home", main, StringComparison.Ordinal);
        Assert.Contains("_workspace.ReplaceTab(new HomeTabState(browser.Id));", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_controls_are_hover_visible_corner_resizes_and_center_handles_crop_without_changing_zoom()
    {
        var overlay = ReadSource("src", "Glide.App", "Controls", "WindowInWindowOverlayManager.cs");

        Assert.Contains("DrawChrome(context, s,", overlay, StringComparison.Ordinal);
        Assert.Contains("private static Rect ResizeRect(OverlayState s) => new(s.X + s.Width - 36", overlay, StringComparison.Ordinal);
        Assert.Contains("private static Rect ResizeWidthRect(OverlayState s) => new(s.X + s.Width - 22", overlay, StringComparison.Ordinal);
        Assert.Contains("private static Rect ResizeHeightRect(OverlayState s) => new(s.X + s.Width / 2 - 22, s.Y, 44, 22)", overlay, StringComparison.Ordinal);
        Assert.Contains("current.PreserveAspectRatio", overlay, StringComparison.Ordinal);
        Assert.Contains("resize.Mode is ResizeMode.WidthOnly or ResizeMode.HeightOnly", overlay, StringComparison.Ordinal);
        Assert.Contains("ContentScale = resize.ContentScale", overlay, StringComparison.Ordinal);
        Assert.Contains("ContentScale = contentScale", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("_chromeVisibleId == item.State.Id && ResizeRect", overlay, StringComparison.Ordinal);
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
