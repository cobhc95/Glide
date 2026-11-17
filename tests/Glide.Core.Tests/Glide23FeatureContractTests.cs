using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Executable contracts for the nine major Glide 2.3 feature identifiers.  The few
/// Avalonia/Windows-only seams are intentionally marked structural: these checks prove
/// that the declared owner and wiring remain present, but cannot prove pointer hit-testing
/// or Explorer HWND behaviour on this non-Windows test host.
/// </summary>
// Source-text contract kept: this asserts on production source (UI code-behind, native/installer files or a repo-wide scan) that cannot be run headlessly; behavioural coverage lives in the other test files.
public sealed class Glide23FeatureContractTests
{
    [Fact]
    public void SearchCardSettingsAreAuthoritativeToggleDefinitions()
    {
        AssertToggle("general.recentHistory");
        AssertToggle("general.showRecentOnHome");
        AssertToggle("folderNav.group");

        // The actual pointer route is Avalonia-only; keep a structural guard that the
        // card uses the staged checkbox as its authoritative target.
        var settingsWindow = Source("src/Glide.App/Settings/SettingsWindow.axaml.cs");
        Assert.Contains("Boolean search cards write the authoritative staged CheckBox", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Bubble", settingsWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHotkeyContractUsesStableActionIdsAndEditableLists()
    {
        var definition = HotkeyCatalog.Find("folder.next");
        Assert.NotNull(definition);
        Assert.Equal(GlideCommand.NextFolder, definition!.Command);

        var bindings = HotkeyCatalog.CreateDefaultMap();
        bindings[definition.Id].Add("Ctrl+Shift+N");
        Assert.Contains("Ctrl+Shift+N", HotkeyCatalog.ShortcutsForCommand(GlideCommand.NextFolder, bindings));
        bindings[definition.Id].Clear();
        Assert.Empty(HotkeyCatalog.ShortcutsForCommand(GlideCommand.NextFolder, bindings));
    }

    [Fact]
    public void SelectionZoomOutContractHasSafeDefaultAndIndependentScaleOption()
    {
        var state = new GlideSettingsState();
        Assert.True(state.SelectionRightClickZoomOut);
        Assert.True(state.ReverseSelectionZoomOutScale);

        var viewport = Source("src/Glide.App/Controls/ImageViewport.cs");
        Assert.Contains("SelectionRightClickZoomOutEnabled", viewport, StringComparison.Ordinal);
        Assert.Contains("ZoomOutFromSelection", viewport, StringComparison.Ordinal);
        Assert.Contains("ReverseSelectionZoomOutScale", viewport, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesContractSupportsNamedRowsAndPresetSemantics()
    {
        Assert.Equal(new[] { "Glide default", "Windows Photos", "IrfanView", "nomacs", "FastStone", "XnView MP" }, SettingsPresetCatalog.Names);
        var current = new GlideSettingsState { SelectionRightClickZoomOut = true };
        var photos = SettingsPresetCatalog.Apply("Windows Photos", current);
        // 4.8.0: presets still describe themselves through the folded toggles, which become explicit
        // gesture rows (selection right-click "Ignore here" = zoom-out off; left-drag = pan).
        Assert.Equal(GestureCatalog.PassThrough, photos.Gestures["selection.rightClick"]);
        Assert.Equal(GestureCatalog.Pan, photos.Gestures["drag.leftImage"]);
        Assert.Equal(GestureCatalog.ZoomWheel, photos.Gestures["wheel.windowed"]);
        Assert.Contains("profiles.import", SettingsCatalog.All.Select(x => x.Id));
        Assert.Contains("profiles.export", SettingsCatalog.All.Select(x => x.Id));
    }

    [Fact]
    public void TabTooltipContractIsSimplified()
    {
        var source = Source("src/Glide.App/MainWindow.axaml.cs");
        // 5.0.6 hover card: one short hint line built from the live bindings (BindingHints.TabMouseHint).
        Assert.Contains("BindingHints.TabMouseHint(", source, StringComparison.Ordinal);
        Assert.StartsWith("Drag to move", Glide.Core.Commands.BindingHints.TabMouseHint(Glide.Core.Commands.GestureCatalog.CreateDefaultMap(), null), StringComparison.Ordinal);
        Assert.DoesNotContain("next/previous hotkey", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupAndNewTabContractsHaveIndependentDestinations()
    {
        var state = new GlideSettingsState();
        Assert.Equal("Welcome tab", state.StartupAction);
        Assert.Equal("Welcome tab", state.NewTabAction);
        state.StartupAction = "Open file dialog";
        Assert.NotEqual(state.StartupAction, state.NewTabAction);
        Assert.Contains("general.startupAction", SettingsCatalog.All.Select(x => x.Id));
        Assert.Contains("general.newTabAction", SettingsCatalog.All.Select(x => x.Id));
    }

    [Fact]
    public void ExplorerContractDeclaresLazyNativeHostAndActivationRoute()
    {
        var source = Source("src/Glide.App/Platform/WindowsExplorerHost.cs");
        Assert.Contains("NativeControlHost", source, StringComparison.Ordinal);
        Assert.Contains("GeneratedComInterface", source, StringComparison.Ordinal);
        Assert.Contains("FileActivated", source, StringComparison.Ordinal);
        Assert.Contains("CreateNativeControlCore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SiblingNavigationDefaultsExposeShiftArrowCommands()
    {
        var state = new GlideSettingsState();
        Assert.True(state.FolderNavShowPrevious);
        Assert.True(state.FolderNavShowNext);
        Assert.True(state.HierarchicalFolderTraversal);
        Assert.True(state.ConfirmHierarchicalFolderTraversal);
        Assert.True(state.NewExplorerTabsUseLastLocation);
        Assert.Equal(new[] { "Shift+Left" }, HotkeyCatalog.Find("folder.previous")!.DefaultShortcuts);
        Assert.Equal(new[] { "Shift+Right" }, HotkeyCatalog.Find("folder.next")!.DefaultShortcuts);
    }

    [Fact]
    public void WelcomeRecentContractHasDurableLayoutAndVisibilityState()
    {
        var state = new GlideSettingsState();
        Assert.True(state.ShowRecentOnHome);
        Assert.Equal("Below tips", state.WelcomeRecentPosition);
        Assert.Equal("Normal", state.WelcomeRecentSize);
        var source = Source("src/Glide.App/MainWindow.axaml.cs");
        Assert.Contains("ResizeRecentHistoryClicked", source, StringComparison.Ordinal);
        Assert.Contains("ClearRecentHistoryClicked", source, StringComparison.Ordinal);
        // 4.6.4: Home no longer carries feature/tip cards; it is the Recent list plus an empty state.
        var home = Source("src/Glide.App/Controls/HomeSurface.axaml");
        Assert.DoesNotContain("HomeTipsGrid", home, StringComparison.Ordinal);
        Assert.DoesNotContain("homeCard", home, StringComparison.Ordinal);
        Assert.Contains("RecentHistoryPanel", home, StringComparison.Ordinal);
        Assert.Contains("HomeEmptyState", home, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeAppOverlayKeepsTopLevelTransparentForEntireWindowLifetime()
    {
        var xaml = Source("src/Glide.App/MainWindow.axaml");
        var code = Source("src/Glide.App/MainWindow.axaml.cs");

        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TransparencyLevelHint=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MainRoot\" RowDefinitions=\"44,*\" Background=\"{DynamicResource BrushWindow}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background = Brushes.Transparent;", code, StringComparison.Ordinal);
        Assert.Contains("MainRoot.Background = Brushes.Transparent;", code, StringComparison.Ordinal);
        Assert.Contains("MainRoot.Background = normalWindowBrush;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Background = normalWindowBrush;", code.Replace("MainRoot.Background = normalWindowBrush;", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayInputIgnoresViewerControlsBeforeStartingGestures()
    {
        var source = Source("src/Glide.App/Controls/WindowInWindowOverlayManager.cs");

        Assert.Contains("private static bool IsViewerControlSource", source, StringComparison.Ordinal);
        Assert.Contains("if (IsViewerControlSource(e.Source)) return;", source, StringComparison.Ordinal);
        Assert.Contains("Button or ToggleButton", source, StringComparison.Ordinal);
        Assert.Contains("GetVisualParent() as Control", source, StringComparison.Ordinal);

        // All new overlay gesture entry points must use the same exclusion.  The
        // release path intentionally remains independent so active interactions clean up.
        Assert.Equal(3, source.Split("if (IsViewerControlSource(e.Source)) return;", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ThemeContractUsesDarkNeutralLightAndAuthoritativeThemeChoice()
    {
        var state = new GlideSettingsState();
        Assert.Equal("Dark", state.ThemeChoice);
        var theme = SettingsCatalog.All.Single(x => x.Id == "appearance.theme");
        Assert.Equal(SettingKind.Choice, theme.Kind);
        Assert.Contains("Dark", theme.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Neutral", theme.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Light", theme.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Title_navigation_uses_the_one_per_tab_history_and_keeps_the_same_tab()
    {
        // 5.0.0: the dormant title "Up to containing folder / Forward to the image" state (NavigateTitleUpAsync,
        // _tabForwardImageTargets) was replaced by one browser-style history per tab (MainWindow.History.cs).
        var source = Source("src/Glide.App/MainWindow.axaml.cs");
        Assert.DoesNotContain("NavigateTitleUpAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_tabForwardImageTargets", source, StringComparison.Ordinal);
        Assert.Contains("await NavigateBackAsync();", source, StringComparison.Ordinal);
        Assert.Contains("new BrowserTabState(active.Id, folder)", source, StringComparison.Ordinal);   // Back to the Explorer keeps the tab
        Assert.Contains("new ImageTabState(active.Id, target.Path!)", source, StringComparison.Ordinal);
        Assert.Contains("RecordActiveTabLocation();", source, StringComparison.Ordinal);
    }

    private static void AssertToggle(string id)
    {
        var setting = SettingsCatalog.All.Single(x => x.Id == id);
        Assert.Equal(SettingKind.Toggle, setting.Kind);
    }

    private static string Source(string relativePath)
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return SourceText.Read(candidate);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var directory = current; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return SourceText.Read(candidate);
        }

        throw new FileNotFoundException($"Could not locate repository source file '{relativePath}'.");
    }
}
