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
    public void Explorer_tabs_activate_the_managed_folder_browser_and_round_trip_sessions()
    {
        // 4.8.0 restored the Explorer tab as Glide's own managed BrowserSurface. The native
        // IExplorerBrowser COM host stays dormant (NativeAOT), and browser tabs are no longer
        // migrated to Home at activation or at session restore.
        var main = ReadSource("src", "Glide.App", "MainWindow.axaml.cs");
        Assert.DoesNotContain("legacy_browser_tab_migrated_to_home", main, StringComparison.Ordinal);
        Assert.DoesNotContain("_workspace.ReplaceTab(new HomeTabState(browser.Id));", main, StringComparison.Ordinal);
        Assert.Contains("explorer_tab_activated", main, StringComparison.Ordinal);
        Assert.Contains("ShowBrowserSurface();", main, StringComparison.Ordinal);
        Assert.DoesNotContain("tab is BrowserTabState ? new HomeTabState(tab.Id) : tab", main, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNativeExplorerHost(", Body(main, "private async Task ActivateWorkspaceAsync()"), StringComparison.Ordinal);

        // New tab / Home page choices and the open-image setting.
        Assert.Contains("\"Explorer tab\"", Body(main, "private TabState CreateConfiguredNewTabDestination()"), StringComparison.Ordinal);
        Assert.Contains("_workspace.AddBrowser(", Body(main, "private TabState CreateConfiguredHomeDestination()"), StringComparison.Ordinal);
        // 4.8.0: the open-image choice is the Explorer gesture rows (double-click / Enter and middle-click).
        var open = Body(main, "private async Task OpenBrowserEntryAsync(string path, bool alternate, KeyModifiers modifiers = KeyModifiers.None, bool fromMenu = false)");
        Assert.Contains("alternate ? \"explorer.middleClick\" : \"explorer.doubleClick\"", open, StringComparison.Ordinal);
        Assert.Contains("var newTab = action == GestureCatalog.OpenInNewTab;", open, StringComparison.Ordinal);
        Assert.Contains("IsMiddleButtonPressed", Body(main, "private async void BrowserItemPointerPressed("), StringComparison.Ordinal);

        var settings = ReadSource("src", "Glide.Core", "Settings", "SettingsCatalog.cs");
        Assert.DoesNotContain("\"explorer.openImagesWith\"", settings, StringComparison.Ordinal);
        var gestures = ReadSource("src", "Glide.Core", "Commands", "GestureCatalog.cs");
        Assert.Contains("S(\"explorer.doubleClick\"", gestures, StringComparison.Ordinal);
        Assert.Contains("S(\"explorer.middleClick\"", gestures, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_host_is_never_declared_hit_test_invisible()
    {
        // 4.8.0 regression: BrowserView shipped with IsHitTestVisible="False" and nothing ever re-enabled it, so the
        // Explorer tab drew correctly but could not be clicked, scrolled or typed into.
        var xaml = ReadSource("src", "Glide.App", "MainWindow.axaml");
        var line = xaml.Split((char)10).Single(l => l.Contains("x:Name=\"BrowserView\"", StringComparison.Ordinal));
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", line, StringComparison.OrdinalIgnoreCase);
        foreach (var file in new[] { "MainWindow.Browser.cs", "MainWindow.Navigation.cs", "MainWindow.Tabs.cs" })
            Assert.DoesNotContain("BrowserView.IsHitTestVisible = false", ReadSource("src", "Glide.App", file), StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method: {signature}");
        var brace = source.IndexOf('{', start);
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(brace, i - brace + 1);
        }
        throw new InvalidOperationException($"Unterminated body for: {signature}");
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
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }
}
