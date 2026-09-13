using Glide.App.Settings;
using Glide.Core.Commands;
using Glide.Core.Settings;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Executable contracts for the nine major Glide 2.3 feature identifiers.  The few
/// Avalonia/Windows-only seams are intentionally marked structural: these checks prove
/// that the declared owner and wiring remain present, but cannot prove pointer hit-testing
/// or Explorer HWND behaviour on this non-Windows test host.
/// </summary>
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
        Assert.False(photos.SelectionRightClickZoomOut);
        Assert.Equal("Pan image", photos.LeftDragMode);
        Assert.Contains("profiles.import", SettingsCatalog.All.Select(x => x.Id));
        Assert.Contains("profiles.export", SettingsCatalog.All.Select(x => x.Id));
    }

    [Fact]
    public void TabTooltipContractIsSimplified()
    {
        var source = Source("src/Glide.App/MainWindow.axaml.cs");
        Assert.Contains("Drag to reorder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("next/previous hotkey", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupAndNewTabContractsHaveIndependentDestinations()
    {
        var state = new GlideSettingsState();
        Assert.Equal("Welcome tab", state.StartupAction);
        Assert.Equal("Explorer tab", state.NewTabAction);
        Assert.NotEqual(state.StartupAction, state.NewTabAction);
        Assert.Contains("general.startupAction", SettingsCatalog.All.Select(x => x.Id));
        Assert.Contains("general.newTabAction", SettingsCatalog.All.Select(x => x.Id));
    }

    [Fact]
    public void ExplorerContractDeclaresLazyNativeHostAndActivationRoute()
    {
        var source = Source("src/Glide.App/Platform/WindowsExplorerHost.cs");
        Assert.Contains("NativeControlHost", source, StringComparison.Ordinal);
        Assert.Contains("ComImport", source, StringComparison.Ordinal);
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
        Assert.Contains("MoveRecentHistoryClicked", source, StringComparison.Ordinal);
        Assert.Contains("ResizeRecentHistoryClicked", source, StringComparison.Ordinal);
        Assert.Contains("ResetWelcomeLayoutClicked", source, StringComparison.Ordinal);
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
    public void TitleUpUsesPresentedImagePathAndSameTabExplorerState()
    {
        var source = Source("src/Glide.App/MainWindow.axaml.cs");
        Assert.Contains("NavigateTitleUpAsync", source, StringComparison.Ordinal);
        Assert.Contains("ImageView.IsVisible && !string.IsNullOrWhiteSpace(_presentedPath)", source, StringComparison.Ordinal);
        Assert.Contains("new BrowserTabState(id, folder)", source, StringComparison.Ordinal);
        Assert.Contains("_tabForwardImageTargets[id] = imagePath", source, StringComparison.Ordinal);
        Assert.Contains("title_up_image_to_folder", source, StringComparison.Ordinal);
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
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var directory = current; directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate repository source file '{relativePath}'.");
    }
}
