namespace Glide.Core.Settings;

/// <summary>Typed effect ownership for the user-facing settings schema.</summary>
public enum SettingApplySemantics { LivePreview, CommitOnly, Action, FutureDisabled }
public enum SettingEditorKind { FromCatalog, DynamicAction, DisabledFuture }
public enum SettingEvidenceStatus { Unknown, StaticContract, LiveVerified }
public enum SettingEffectOwner
{
    None, General, Appearance, Navigation, Viewing, WindowChrome, Fullscreen, Input,
    Performance, StatusBar, Slideshow, TabsWorkspace, FolderNavigation, Overlay,
    Profiles, WindowIntegration, Developer, FutureDisabled
}

public sealed record SettingEffectBinding(
    string SettingId,
    SettingEffectOwner EffectOwner,
    SettingEditorKind EditorKind,
    SettingApplySemantics ApplySemantics,
    string EvidenceId,
    SettingEvidenceStatus EvidenceStatus = SettingEvidenceStatus.Unknown);

/// <summary>
/// The single typed map from SettingsCatalog IDs to editor/action and runtime-effect ownership.
/// This is a contract and audit spine; migrating existing UI switchboards to consume it is incremental.
/// </summary>
public static class SettingEffectRegistry
{
    static SettingEffectRegistry()
    {
        var issues = Validate();
        if (issues.Count != 0)
            throw new InvalidOperationException("Settings effect registry contract failed: " + string.Join("; ", issues));
    }

    public static IReadOnlyList<SettingEffectBinding> All { get; } = StaticRows()
        .Concat(SettingsCatalog.All.Where(x => x.Id.StartsWith("gesture.", StringComparison.Ordinal))
            .Select(x => new SettingEffectBinding(x.Id, SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.LivePreview, "settings.effect." + x.Id, SettingEvidenceStatus.Unknown)))
        .ToArray();

    private static SettingEffectBinding[] StaticRows() => new SettingEffectBinding[]
    {
        new("search.showRelatedResults", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.search.showRelatedResults", SettingEvidenceStatus.Unknown),
        new("updates.checkDaily", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.updates.checkDaily", SettingEvidenceStatus.Unknown),
        new("updates.dontRemind", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.updates.dontRemind", SettingEvidenceStatus.Unknown),
        new("general.recentHistory", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.recentHistory", SettingEvidenceStatus.Unknown),
        new("general.historySize", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.historySize", SettingEvidenceStatus.Unknown),
        new("windows.rememberPlacement", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.rememberPlacement", SettingEvidenceStatus.Unknown),
        new("general.singleInstance", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.singleInstance", SettingEvidenceStatus.Unknown),
        new("navigation.sortOrder", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.navigation.sortOrder", SettingEvidenceStatus.Unknown),
        new("navigation.sortDescending", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.navigation.sortDescending", SettingEvidenceStatus.Unknown),
        new("navigation.siblingFolders", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.siblingFolders", SettingEvidenceStatus.Unknown),
        new("navigation.hierarchicalFolders", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.hierarchicalFolders", SettingEvidenceStatus.Unknown),
        new("navigation.confirmHierarchicalBoundary", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.confirmHierarchicalBoundary", SettingEvidenceStatus.Unknown),
        new("navigation.hierarchicalPreviousEntry", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.hierarchicalPreviousEntry", SettingEvidenceStatus.Unknown),
        new("folderNav.showCounter", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.folderNav.showCounter", SettingEvidenceStatus.Unknown),
        new("folderNav.counterGrandparent", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.folderNav.counterGrandparent", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitMs", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitMs", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitInput", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitInput", SettingEvidenceStatus.Unknown),
        new("navigation.skipHint", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.skipHint", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitIndicator", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitIndicator", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitIndicatorPosition", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitIndicatorPosition", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitWindowed", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitWindowed", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitFullscreen", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitFullscreen", SettingEvidenceStatus.Unknown),
        new("navigation.rateLimitSlideshow", SettingEffectOwner.Navigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.navigation.rateLimitSlideshow", SettingEvidenceStatus.Unknown),
        new("general.lastOpenLocation", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.lastOpenLocation", SettingEvidenceStatus.Unknown),
        new("general.openFileDefaultDirectory", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.openFileDefaultDirectory", SettingEvidenceStatus.Unknown),
        new("appearance.theme", SettingEffectOwner.Appearance, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.appearance.theme", SettingEvidenceStatus.Unknown),
        new("appearance.accent", SettingEffectOwner.Appearance, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.appearance.accent", SettingEvidenceStatus.Unknown),
        new("appearance.glowColor", SettingEffectOwner.Appearance, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.appearance.glowColor", SettingEvidenceStatus.Unknown),
        new("appearance.background", SettingEffectOwner.Appearance, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.appearance.background", SettingEvidenceStatus.Unknown),
        new("appearance.glowIntensity", SettingEffectOwner.Appearance, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.appearance.glowIntensity", SettingEvidenceStatus.Unknown),
        new("general.showRecentOnHome", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.showRecentOnHome", SettingEvidenceStatus.Unknown),
        new("general.confirmDiscardSettings", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.confirmDiscardSettings", SettingEvidenceStatus.Unknown),
        new("general.startupAction", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.startupAction", SettingEvidenceStatus.Unknown),
        new("general.startupCustomPath", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.startupCustomPath", SettingEvidenceStatus.Unknown),
        new("general.newTabAction", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.newTabAction", SettingEvidenceStatus.Unknown),
        new("general.newTabCustomPath", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.general.newTabCustomPath", SettingEvidenceStatus.Unknown),
        new("status.visible", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.visible", SettingEvidenceStatus.Unknown),
        new("status.hoverWhenClosed", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.hoverWhenClosed", SettingEvidenceStatus.Unknown),
        new("windows.externalOpenBehavior", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.externalOpenBehavior", SettingEvidenceStatus.Unknown),
        new("view.scrollbars", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.scrollbars", SettingEvidenceStatus.Unknown),
        new("window.fullPathTitle", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.window.fullPathTitle", SettingEvidenceStatus.Unknown),
        new("fullscreen.hideCursor", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.hideCursor", SettingEvidenceStatus.Unknown),
        new("fullscreen.autoHideChrome", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.autoHideChrome", SettingEvidenceStatus.Unknown),
        new("tabs.enabled", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.enabled", SettingEvidenceStatus.Unknown),
        new("titlebar.customize", SettingEffectOwner.WindowChrome, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.titlebar.customize", SettingEvidenceStatus.Unknown),
        new("settings.alwaysExpand", SettingEffectOwner.General, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.settings.alwaysExpand", SettingEvidenceStatus.Unknown),
        new("caption.windowedMinimize", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.caption.windowedMinimize", SettingEvidenceStatus.Unknown),
        new("caption.windowedMaximize", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.caption.windowedMaximize", SettingEvidenceStatus.Unknown),
        new("caption.windowedClose", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.caption.windowedClose", SettingEvidenceStatus.Unknown),
        new("caption.fullscreenMinimize", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.caption.fullscreenMinimize", SettingEvidenceStatus.Unknown),
        new("caption.fullscreenMaximize", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.caption.fullscreenMaximize", SettingEvidenceStatus.Unknown),
        new("caption.fullscreenClose", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.caption.fullscreenClose", SettingEvidenceStatus.Unknown),
        new("fullscreen.statusAlways", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.statusAlways", SettingEvidenceStatus.Unknown),
        new("fullscreen.keepTabBarOpen", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.keepTabBarOpen", SettingEvidenceStatus.Unknown),
        new("fullscreen.keepTitleBarForExplorer", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.keepTitleBarForExplorer", SettingEvidenceStatus.Unknown),
        new("windows.alwaysOnTop", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.windows.alwaysOnTop", SettingEvidenceStatus.Unknown),
        new("window.centerOnDisplay", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.window.centerOnDisplay", SettingEvidenceStatus.Unknown),
        new("view.animateGifs", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.view.animateGifs", SettingEvidenceStatus.Unknown),
        new("view.defaultMode", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.defaultMode", SettingEvidenceStatus.Unknown),
        new("view.pointerZoom", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.pointerZoom", SettingEvidenceStatus.Unknown),
        new("view.keepZoom", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.keepZoom", SettingEvidenceStatus.Unknown),
        new("view.zoomStep", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.zoomStep", SettingEvidenceStatus.Unknown),
        new("view.upscaleSmallImages", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.upscaleSmallImages", SettingEvidenceStatus.Unknown),
        new("view.subpixelRendering", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.subpixelRendering", SettingEvidenceStatus.Unknown),
        new("view.showLoadingIndicator", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.view.showLoadingIndicator", SettingEvidenceStatus.Unknown),
        new("fullscreen.hideCursorDelay", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.hideCursorDelay", SettingEvidenceStatus.Unknown),
        new("keyboard.tabFocusNavigation", SettingEffectOwner.Viewing, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.keyboard.tabFocusNavigation", SettingEvidenceStatus.Unknown),
        new("escape.stopSlideshow", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.escape.stopSlideshow", SettingEvidenceStatus.Unknown),
        new("escape.exitFullscreen", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.escape.exitFullscreen", SettingEvidenceStatus.Unknown),
        new("escape.confirmWindowedClose", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.escape.confirmWindowedClose", SettingEvidenceStatus.Unknown),
        new("escape.resetRememberedClose", SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.escape.resetRememberedClose", SettingEvidenceStatus.Unknown),
        new("mouse.invertWheel", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.mouse.invertWheel", SettingEvidenceStatus.Unknown),
        new("mouse.rightDragPan", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.mouse.rightDragPan", SettingEvidenceStatus.Unknown),
        new("selection.buffer", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.selection.buffer", SettingEvidenceStatus.Unknown),
        new("selection.bufferWindowedPercent", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.selection.bufferWindowedPercent", SettingEvidenceStatus.Unknown),
        new("selection.bufferMaximizedPercent", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.selection.bufferMaximizedPercent", SettingEvidenceStatus.Unknown),
        new("selection.bufferAllModes", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.selection.bufferAllModes", SettingEvidenceStatus.Unknown),
        new("developer.reverseSelectionZoomOut", SettingEffectOwner.Developer, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.developer.reverseSelectionZoomOut", SettingEvidenceStatus.Unknown),
        new("mouse.backgroundWindowDrag", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.mouse.backgroundWindowDrag", SettingEvidenceStatus.Unknown),
        new("mouse.gestureMatrix", SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.mouse.gestureMatrix", SettingEvidenceStatus.Unknown),
        new("performance.initialQuality", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.initialQuality", SettingEvidenceStatus.Unknown),
        new("performance.saveCustomPreset", SettingEffectOwner.Performance, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.performance.saveCustomPreset", SettingEvidenceStatus.Unknown),
        new("performance.interactivePanQuality", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.interactivePanQuality", SettingEvidenceStatus.Unknown),
        new("performance.adaptivePreview", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.adaptivePreview", SettingEvidenceStatus.Unknown),
        new("performance.previewDelay", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.previewDelay", SettingEvidenceStatus.Unknown),
        new("performance.sequentialReads", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.sequentialReads", SettingEvidenceStatus.Unknown),
        new("performance.prefetch", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.prefetch", SettingEvidenceStatus.Unknown),
        new("performance.backgroundRefinement", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.backgroundRefinement", SettingEvidenceStatus.Unknown),
        new("performance.purgeOnMinimize", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.purgeOnMinimize", SettingEvidenceStatus.Unknown),
        new("performance.cacheItems", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.cacheItems", SettingEvidenceStatus.Unknown),
        new("performance.compressedCacheMb", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.compressedCacheMb", SettingEvidenceStatus.Unknown),
        new("performance.decodedCacheMb", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.decodedCacheMb", SettingEvidenceStatus.Unknown),
        new("performance.prefetchDepth", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.prefetchDepth", SettingEvidenceStatus.Unknown),
        new("performance.prefetchSiblingFolders", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.prefetchSiblingFolders", SettingEvidenceStatus.Unknown),
        new("performance.prefetchSiblingFolderCount", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.prefetchSiblingFolderCount", SettingEvidenceStatus.Unknown),
        new("performance.rapidPreviewSize", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.rapidPreviewSize", SettingEvidenceStatus.Unknown),
        new("performance.rapidBrowsePreviewSize", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.rapidBrowsePreviewSize", SettingEvidenceStatus.Unknown),
        new("performance.progressiveColor", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.progressiveColor", SettingEvidenceStatus.Unknown),
        new("developer.startupDiagnostics", SettingEffectOwner.Developer, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.developer.startupDiagnostics", SettingEvidenceStatus.Unknown),
        new("performance.speedBoost", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.speedBoost", SettingEvidenceStatus.Unknown),
        new("performance.speedBoostTrayIcon", SettingEffectOwner.Performance, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.speedBoostTrayIcon", SettingEvidenceStatus.Unknown),
        new("performance.startWithWindowsInBackground", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.performance.startWithWindowsInBackground", SettingEvidenceStatus.Unknown),
        new("status.navigation", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.navigation", SettingEvidenceStatus.Unknown),
        new("status.zoom", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.zoom", SettingEvidenceStatus.Unknown),
        new("status.slideshow", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.slideshow", SettingEvidenceStatus.Unknown),
        new("status.fit", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.fit", SettingEvidenceStatus.Unknown),
        new("status.fitScope", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.fitScope", SettingEvidenceStatus.Unknown),
        new("status.info", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.info", SettingEvidenceStatus.Unknown),
        new("status.options", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.options", SettingEvidenceStatus.Unknown),
        new("status.close", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.close", SettingEvidenceStatus.Unknown),
        new("status.statIndex", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.statIndex", SettingEvidenceStatus.Unknown),
        new("status.statResolution", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.statResolution", SettingEvidenceStatus.Unknown),
        new("status.statZoom", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.statZoom", SettingEvidenceStatus.Unknown),
        new("status.statFileSize", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.statFileSize", SettingEvidenceStatus.Unknown),
        new("status.statFormat", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.statFormat", SettingEvidenceStatus.Unknown),
        new("status.statFileName", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.statFileName", SettingEvidenceStatus.Unknown),
        new("status.size", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.size", SettingEvidenceStatus.Unknown),
        new("status.alignment", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.alignment", SettingEvidenceStatus.Unknown),
        new("status.collapsedCorner", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.collapsedCorner", SettingEvidenceStatus.Unknown),
        new("status.autoHide", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.autoHide", SettingEvidenceStatus.Unknown),
        new("status.stackInfoLine", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.stackInfoLine", SettingEvidenceStatus.Unknown),
        new("status.gifOnlyOnGifs", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.gifOnlyOnGifs", SettingEvidenceStatus.Unknown),
        new("status.fileNameClickActions", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.fileNameClickActions", SettingEvidenceStatus.Unknown),
        new("status.customizeButtons", SettingEffectOwner.StatusBar, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.status.customizeButtons", SettingEvidenceStatus.Unknown),
        new("status.revealZoneExpand", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.revealZoneExpand", SettingEvidenceStatus.Unknown),
        new("status.reserveFitSpace", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.reserveFitSpace", SettingEvidenceStatus.Unknown),
        new("slideshow.interval", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.interval", SettingEvidenceStatus.Unknown),
        new("slideshow.loop", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.loop", SettingEvidenceStatus.Unknown),
        new("slideshow.crossFolders", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.crossFolders", SettingEvidenceStatus.Unknown),
        new("slideshow.shuffle", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.shuffle", SettingEvidenceStatus.Unknown),
        new("slideshow.direction", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.direction", SettingEvidenceStatus.Unknown),
        new("slideshow.startFullscreen", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.startFullscreen", SettingEvidenceStatus.Unknown),
        new("slideshow.pauseInactive", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.pauseInactive", SettingEvidenceStatus.Unknown),
        new("slideshow.showQualityIndicator", SettingEffectOwner.Slideshow, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.slideshow.showQualityIndicator", SettingEvidenceStatus.Unknown),
        new("hotkeys.registry", SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.hotkeys.registry", SettingEvidenceStatus.Unknown),
        new("tabs.minWidth", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.minWidth", SettingEvidenceStatus.Unknown),
        new("tabs.maxWidth", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.maxWidth", SettingEvidenceStatus.Unknown),
        new("tabs.overflowArrows", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.overflowArrows", SettingEvidenceStatus.Unknown),
        new("tabs.detach", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.detach", SettingEvidenceStatus.Unknown),
        new("tabs.attach", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.attach", SettingEvidenceStatus.Unknown),
        new("tabs.closedHistory", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.closedHistory", SettingEvidenceStatus.Unknown),
        new("tabs.closeEmptyAfterDetach", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.closeEmptyAfterDetach", SettingEvidenceStatus.Unknown),
        new("tabs.detachedHome", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.detachedHome", SettingEvidenceStatus.Unknown),
        new("tabs.tooltipTextSize", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.tabs.tooltipTextSize", SettingEvidenceStatus.Unknown),
        new("tabs.position", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.tabs.position", SettingEvidenceStatus.Unknown),
        new("tabs.sideAutoHide", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.tabs.sideAutoHide", SettingEvidenceStatus.Unknown),
        new("tabs.topAutoHide", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.tabs.topAutoHide", SettingEvidenceStatus.Unknown),
        new("tabs.cycleOrder", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.cycleOrder", SettingEvidenceStatus.Unknown),
        new("tabs.lastTabBehavior", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.lastTabBehavior", SettingEvidenceStatus.Unknown),
        new("tabs.confirmCloseMultiple", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.confirmCloseMultiple", SettingEvidenceStatus.Unknown),
        new("tabs.homePage", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.homePage", SettingEvidenceStatus.Unknown),
        new("folderNav.group", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.group", SettingEvidenceStatus.Unknown),
        new("folderNav.previous", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.previous", SettingEvidenceStatus.Unknown),
        new("folderNav.next", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.next", SettingEvidenceStatus.Unknown),
        new("folderNav.skipEmpty", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.skipEmpty", SettingEvidenceStatus.Unknown),
        new("folderNav.includeHidden", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.includeHidden", SettingEvidenceStatus.Unknown),
        new("folderNav.wrap", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.wrap", SettingEvidenceStatus.Unknown),
        new("folderNav.openFirst", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.openFirst", SettingEvidenceStatus.Unknown),
        new("folderNav.order", SettingEffectOwner.FolderNavigation, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.folderNav.order", SettingEvidenceStatus.Unknown),
        new("overlay.pictureCounter", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureCounter", SettingEvidenceStatus.Unknown),
        new("overlay.pictureTemplate", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureTemplate", SettingEvidenceStatus.Unknown),
        new("overlay.pictureFontSize", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureFontSize", SettingEvidenceStatus.Unknown),
        new("overlay.pictureBold", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureBold", SettingEvidenceStatus.Unknown),
        new("overlay.pictureOpacity", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureOpacity", SettingEvidenceStatus.Unknown),
        new("overlay.picturePosition", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.picturePosition", SettingEvidenceStatus.Unknown),
        new("overlay.pictureColor", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureColor", SettingEvidenceStatus.Unknown),
        new("overlay.pictureShadow", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureShadow", SettingEvidenceStatus.Unknown),
        new("overlay.defaultOpacity", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.defaultOpacity", SettingEvidenceStatus.Unknown),
        new("overlay.zoomStep", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.zoomStep", SettingEvidenceStatus.Unknown),
        new("overlay.rememberFolder", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.rememberFolder", SettingEvidenceStatus.Unknown),
        new("overlay.persistSession", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.persistSession", SettingEvidenceStatus.Unknown),
        new("overlay.wholeAppAlwaysStart", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.overlay.wholeAppAlwaysStart", SettingEvidenceStatus.Unknown),
        new("overlay.keyboardZoom", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.keyboardZoom", SettingEvidenceStatus.Unknown),
        new("overlay.highlightSelected", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.highlightSelected", SettingEvidenceStatus.Unknown),
        new("overlay.rememberZoom", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.rememberZoom", SettingEvidenceStatus.Unknown),
        new("overlay.scaleWithWindow", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.scaleWithWindow", SettingEvidenceStatus.Unknown),
        new("overlay.defaultDirectory", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.overlay.defaultDirectory", SettingEvidenceStatus.Unknown),
        new("overlay.animationRegion", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationRegion", SettingEvidenceStatus.Unknown),
        new("overlay.animationSpeedDipsPerSecond", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationSpeedDipsPerSecond", SettingEvidenceStatus.Unknown),
        new("overlay.animationTurnIntervalMs", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationTurnIntervalMs", SettingEvidenceStatus.Unknown),
        new("overlay.animationTurnAngleDegrees", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationTurnAngleDegrees", SettingEvidenceStatus.Unknown),
        new("overlay.animationPauseWhileInteracting", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationPauseWhileInteracting", SettingEvidenceStatus.Unknown),
        new("overlay.animationAvoidOverlap", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationAvoidOverlap", SettingEvidenceStatus.Unknown),
        new("overlay.animationRefreshRateHz", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.animationRefreshRateHz", SettingEvidenceStatus.Unknown),
        new("profiles.defaultDirectory", SettingEffectOwner.Profiles, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.profiles.defaultDirectory", SettingEvidenceStatus.Unknown),
        new("profiles.import", SettingEffectOwner.Profiles, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.profiles.import", SettingEvidenceStatus.Unknown),
        new("profiles.export", SettingEffectOwner.Profiles, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.profiles.export", SettingEvidenceStatus.Unknown),
        new("profiles.presets", SettingEffectOwner.Profiles, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.profiles.presets", SettingEvidenceStatus.Unknown),
        new("windows.external1", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.external1", SettingEvidenceStatus.Unknown),
        new("windows.external2", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.external2", SettingEvidenceStatus.Unknown),
        new("windows.external3", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.external3", SettingEvidenceStatus.Unknown),
        new("windows.fileAssociations", SettingEffectOwner.WindowIntegration, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.windows.fileAssociations", SettingEvidenceStatus.Unknown),
        new("windows.defaultViewer", SettingEffectOwner.WindowIntegration, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.windows.defaultViewer", SettingEvidenceStatus.Unknown),
        new("windows.explorerThumbnails", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.explorerThumbnails", SettingEvidenceStatus.Unknown),
        new("windows.thumbnailMode", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.thumbnailMode", SettingEvidenceStatus.Unknown),
        new("windows.thumbnailMaxSource", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.thumbnailMaxSource", SettingEvidenceStatus.Unknown),
        new("windows.thumbnailQuality", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.thumbnailQuality", SettingEvidenceStatus.Unknown),
        new("windows.thumbnailPreferEmbedded", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.thumbnailPreferEmbedded", SettingEvidenceStatus.Unknown),
        new("developer.statusGlobalScale", SettingEffectOwner.Developer, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.developer.statusGlobalScale", SettingEvidenceStatus.Unknown),
        new("status.autoResize", SettingEffectOwner.StatusBar, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.status.autoResize", SettingEvidenceStatus.Unknown),
        new("developer.statusMaximizedBoost", SettingEffectOwner.Developer, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.developer.statusMaximizedBoost", SettingEvidenceStatus.Unknown),
        new("developer.overlayAbsoluteCoordinates", SettingEffectOwner.Developer, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.developer.overlayAbsoluteCoordinates", SettingEvidenceStatus.Unknown),
        new("developer.diagnostics", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.developer.diagnostics", SettingEvidenceStatus.Unknown),
        new("backup.card", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.card", SettingEvidenceStatus.Unknown),
        new("backup.export", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.export", SettingEvidenceStatus.Unknown),
        new("backup.import", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.import", SettingEvidenceStatus.Unknown),
        new("backup.allSettings", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.allSettings", SettingEvidenceStatus.Unknown),
        new("backup.hotkeysGestures", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.hotkeysGestures", SettingEvidenceStatus.Unknown),
        new("backup.profiles", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.profiles", SettingEvidenceStatus.Unknown),
        new("backup.windowPlacement", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.windowPlacement", SettingEvidenceStatus.Unknown),
        new("backup.recentHistory", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.recentHistory", SettingEvidenceStatus.Unknown),
        new("backup.lastSession", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.backup.lastSession", SettingEvidenceStatus.Unknown),
        new("reset.card", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.card", SettingEvidenceStatus.Unknown),
        new("reset.allSettings", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.allSettings", SettingEvidenceStatus.Unknown),
        new("reset.hotkeysGestures", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.hotkeysGestures", SettingEvidenceStatus.Unknown),
        new("reset.profiles", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.profiles", SettingEvidenceStatus.Unknown),
        new("reset.windowPlacement", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.windowPlacement", SettingEvidenceStatus.Unknown),
        new("reset.recentHistory", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.recentHistory", SettingEvidenceStatus.Unknown),
        new("reset.lastSession", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.lastSession", SettingEvidenceStatus.Unknown),
        new("reset.caches", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.caches", SettingEvidenceStatus.Unknown),
        new("reset.registry", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.registry", SettingEvidenceStatus.Unknown),
        new("reset.startup", SettingEffectOwner.Developer, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.reset.startup", SettingEvidenceStatus.Unknown),
        new("window.showInExplorerTarget", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.window.showInExplorerTarget", SettingEvidenceStatus.Unknown),
        new("explorer.defaultView", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.defaultView", SettingEvidenceStatus.Unknown),
        new("explorer.keepSelectionInView", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.keepSelectionInView", SettingEvidenceStatus.Unknown),
        new("explorer.clickToRename", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.clickToRename", SettingEvidenceStatus.Unknown),
        new("explorer.showHidden", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.showHidden", SettingEvidenceStatus.Unknown),
        new("explorer.onlyImages", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.onlyImages", SettingEvidenceStatus.Unknown),
        new("explorer.scrollSpeed", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.scrollSpeed", SettingEvidenceStatus.Unknown),
        new("explorer.scrollMode", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.scrollMode", SettingEvidenceStatus.Unknown),
        new("explorer.scrollRows", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.scrollRows", SettingEvidenceStatus.Unknown),
        new("explorer.scrollPixels", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.scrollPixels", SettingEvidenceStatus.Unknown),
        new("explorer.locateOpensIn", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.locateOpensIn", SettingEvidenceStatus.Unknown),
        new("explorer.backReturnsToImage", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.backReturnsToImage", SettingEvidenceStatus.Unknown),
        new("explorer.altFileManagerPath", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.altFileManagerPath", SettingEvidenceStatus.Unknown),
        new("explorer.altFileManagerArguments", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.explorer.altFileManagerArguments", SettingEvidenceStatus.Unknown),
        new("windows.alwaysOnTopMode", SettingEffectOwner.WindowChrome, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.windows.alwaysOnTopMode", SettingEvidenceStatus.Unknown),
        new("fullscreen.exitBehavior", SettingEffectOwner.Fullscreen, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.fullscreen.exitBehavior", SettingEvidenceStatus.Unknown),
        new("windows.sameImageBehavior", SettingEffectOwner.WindowIntegration, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.windows.sameImageBehavior", SettingEvidenceStatus.Unknown),
        new("mouse.leftWindowDrag", SettingEffectOwner.Input, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.mouse.leftWindowDrag", SettingEvidenceStatus.Unknown),
        new("overlay.wholeAppWindowedInteractions", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.wholeAppWindowedInteractions", SettingEvidenceStatus.Unknown),
        new("hotkeys.searchHotkey", SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.hotkeys.searchHotkey", SettingEvidenceStatus.Unknown),
        new("hotkeys.customPresets", SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.hotkeys.customPresets", SettingEvidenceStatus.Unknown),
        new("hotkeys.preset", SettingEffectOwner.Input, SettingEditorKind.DynamicAction, SettingApplySemantics.Action, "settings.effect.hotkeys.preset", SettingEvidenceStatus.Unknown),
        new("tabs.newTabPosition", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.newTabPosition", SettingEvidenceStatus.Unknown),
        new("tabs.switchToNewTabs", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.switchToNewTabs", SettingEvidenceStatus.Unknown),
        new("tabs.newTabModifiers", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.newTabModifiers", SettingEvidenceStatus.Unknown),
        new("tabs.afterCloseActivate", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.afterCloseActivate", SettingEvidenceStatus.Unknown),
        new("tabs.hoverPreview", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.hoverPreview", SettingEvidenceStatus.Unknown),
        new("tabs.animateGifPreviews", SettingEffectOwner.TabsWorkspace, SettingEditorKind.FromCatalog, SettingApplySemantics.CommitOnly, "settings.effect.tabs.animateGifPreviews", SettingEvidenceStatus.Unknown),
        new("overlay.pictureBorder", SettingEffectOwner.Overlay, SettingEditorKind.FromCatalog, SettingApplySemantics.LivePreview, "settings.effect.overlay.pictureBorder", SettingEvidenceStatus.Unknown),
    };

    public static IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        var duplicateIds = All.GroupBy(x => x.SettingId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() != 1).Select(g => g.Key);
        issues.AddRange(duplicateIds.Select(id => $"registry duplicate: {id}"));
        var catalogIds = SettingsCatalog.All.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        issues.AddRange(All.Where(x => !catalogIds.Contains(x.SettingId)).Select(x => $"registry unknown: {x.SettingId}"));
        issues.AddRange(catalogIds.Where(id => !All.Any(x => string.Equals(x.SettingId, id, StringComparison.OrdinalIgnoreCase))).Select(id => $"registry missing: {id}"));
        foreach (var row in All)
        {
            var catalog = SettingsCatalog.All.FirstOrDefault(x => string.Equals(x.Id, row.SettingId, StringComparison.OrdinalIgnoreCase));
            if (catalog is null) continue;
            if (catalog.FuturePhase is not null)
            {
                if (row.ApplySemantics != SettingApplySemantics.FutureDisabled || row.EditorKind != SettingEditorKind.DisabledFuture || row.EffectOwner != SettingEffectOwner.FutureDisabled)
                    issues.Add($"future setting is not disabled: {row.SettingId}");
            }
            else
            {
                if (row.EffectOwner is SettingEffectOwner.None or SettingEffectOwner.FutureDisabled)
                    issues.Add($"active setting has no effect owner: {row.SettingId}");
                if (row.EditorKind == SettingEditorKind.DisabledFuture || row.ApplySemantics == SettingApplySemantics.FutureDisabled)
                    issues.Add($"active setting is disabled: {row.SettingId}");
            }
            if (string.IsNullOrWhiteSpace(row.EvidenceId)) issues.Add($"missing evidence id: {row.SettingId}");
        }
        return issues;
    }

    public static SettingEffectBinding For(string settingId) =>
        All.First(x => string.Equals(x.SettingId, settingId, StringComparison.OrdinalIgnoreCase));
}
