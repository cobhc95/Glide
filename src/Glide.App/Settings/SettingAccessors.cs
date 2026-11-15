using Glide.Core.Settings;

namespace Glide.App.Settings;

/// <summary>
/// Typed, AOT-safe read/write access to the catalogued Toggle and Choice settings, keyed by SettingsCatalog id.
/// This is what the generated "Toggle: ..." / "Cycle: ..." registry actions use to change a setting through the
/// normal settings pipeline (MainWindow.ChangeSettingAsync clones the state, writes the value here, normalises the
/// derived fields exactly like SettingsWindow.ReadControls, then applies + persists it like Settings > Apply).
/// The table mirrors SettingsWindow.ReadControls; tools/audit-action-registry.py and the registry guard tests
/// fail when a bindable setting has no accessor here.
/// </summary>
internal static class SettingAccessors
{
    internal sealed record Accessor(Func<GlideSettingsState, bool>? GetBool, Action<GlideSettingsState, bool>? SetBool,
        Func<GlideSettingsState, string>? GetText, Action<GlideSettingsState, string>? SetText);

    private static Accessor Bool(Func<GlideSettingsState, bool> get, Action<GlideSettingsState, bool> set) => new(get, set, null, null);
    private static Accessor Text(Func<GlideSettingsState, string> get, Action<GlideSettingsState, string> set) => new(null, null, get, set);

    public static IReadOnlyDictionary<string, Accessor> All { get; } = new Dictionary<string, Accessor>(StringComparer.OrdinalIgnoreCase)
    {
        ["updates.checkDaily"] = Bool(s => s.UpdateCheckDaily, (s, v) => s.UpdateCheckDaily = v),
        ["updates.dontRemind"] = Bool(s => s.UpdateDontRemind, (s, v) => s.UpdateDontRemind = v),
        ["general.recentHistory"] = Bool(s => s.RecentHistoryEnabled, (s, v) => s.RecentHistoryEnabled = v),
        ["search.showRelatedResults"] = Bool(s => s.SearchShowRelatedResults, (s, v) => s.SearchShowRelatedResults = v),
        ["windows.rememberPlacement"] = Bool(s => s.RememberWindowPlacement, (s, v) => s.RememberWindowPlacement = v),
        ["general.singleInstance"] = Bool(s => s.ReuseSingleInstance, (s, v) => s.ReuseSingleInstance = v),
        ["navigation.siblingFolders"] = Bool(s => s.ContinueSiblingFolders, (s, v) => s.ContinueSiblingFolders = v),
        ["navigation.hierarchicalFolders"] = Bool(s => s.HierarchicalFolderTraversal, (s, v) => s.HierarchicalFolderTraversal = v),
        ["navigation.confirmHierarchicalBoundary"] = Bool(s => s.ConfirmHierarchicalFolderTraversal, (s, v) => s.ConfirmHierarchicalFolderTraversal = v),
        ["navigation.rateLimitIndicator"] = Bool(s => s.NavigationRateLimitShowIndicator, (s, v) => s.NavigationRateLimitShowIndicator = v),
        ["navigation.rateLimitWindowed"] = Bool(s => s.NavigationRateLimitWindowed, (s, v) => s.NavigationRateLimitWindowed = v),
        ["navigation.rateLimitFullscreen"] = Bool(s => s.NavigationRateLimitFullscreen, (s, v) => s.NavigationRateLimitFullscreen = v),
        ["navigation.rateLimitSlideshow"] = Bool(s => s.NavigationRateLimitSlideshow, (s, v) => s.NavigationRateLimitSlideshow = v),
        ["general.lastOpenLocation"] = Bool(s => s.RememberLastOpenLocation, (s, v) => s.RememberLastOpenLocation = v),
        ["general.showRecentOnHome"] = Bool(s => s.ShowRecentOnHome, (s, v) => s.ShowRecentOnHome = v),
        ["general.confirmDiscardSettings"] = Bool(s => s.ConfirmDiscardSettingsChanges, (s, v) => s.ConfirmDiscardSettingsChanges = v),
        ["status.visible"] = Bool(s => s.ShowStatusSurface, (s, v) => s.ShowStatusSurface = v),
        ["status.hoverWhenClosed"] = Bool(s => s.StatusShowOnHoverWhenClosed, (s, v) => s.StatusShowOnHoverWhenClosed = v),
        ["view.scrollbars"] = Bool(s => s.ShowScrollbars, (s, v) => s.ShowScrollbars = v),
        ["window.fullPathTitle"] = Bool(s => s.FullPathInTitle, (s, v) => s.FullPathInTitle = v),
        ["fullscreen.hideCursor"] = Bool(s => s.HideCursorFullscreen, (s, v) => s.HideCursorFullscreen = v),
        ["fullscreen.autoHideChrome"] = Bool(s => s.AutoHideFullscreenChrome, (s, v) => s.AutoHideFullscreenChrome = v),
        ["tabs.enabled"] = Bool(s => s.TabsEnabled, (s, v) => s.TabsEnabled = v),
        ["fullscreen.statusAlways"] = Bool(s => s.FullscreenStatusAlwaysOn, (s, v) => s.FullscreenStatusAlwaysOn = v),
        ["fullscreen.keepTabBarOpen"] = Bool(s => s.FullscreenKeepTabBarOpen, (s, v) => s.FullscreenKeepTabBarOpen = v),
        ["windows.alwaysOnTop"] = Bool(s => s.AlwaysOnTop, (s, v) => s.AlwaysOnTop = v),
        ["window.centerOnDisplay"] = Bool(s => s.AutoCenterWindowOnRestore, (s, v) => s.AutoCenterWindowOnRestore = v),
        ["view.animateGifs"] = Bool(s => s.AnimateGifs, (s, v) => s.AnimateGifs = v),
        ["view.pointerZoom"] = Bool(s => s.PointerZoom, (s, v) => s.PointerZoom = v),
        ["view.keepZoom"] = Bool(s => s.PreserveManualZoomOnNavigate, (s, v) => s.PreserveManualZoomOnNavigate = v),
        ["view.upscaleSmallImages"] = Bool(s => s.UpscaleSmallImages, (s, v) => s.UpscaleSmallImages = v),
        ["view.subpixelRendering"] = Bool(s => s.HighPrecisionSubpixelRendering, (s, v) => s.HighPrecisionSubpixelRendering = v),
        ["view.showLoadingIndicator"] = Bool(s => s.ShowImageLoadingIndicator, (s, v) => s.ShowImageLoadingIndicator = v),
        ["keyboard.tabFocusNavigation"] = Bool(s => s.EnableTabFocusNavigation, (s, v) => s.EnableTabFocusNavigation = v),
        ["escape.stopSlideshow"] = Bool(s => s.EscStopsSlideshow, (s, v) => s.EscStopsSlideshow = v),
        ["escape.exitFullscreen"] = Bool(s => s.EscExitsFullscreen, (s, v) => s.EscExitsFullscreen = v),
        ["escape.confirmWindowedClose"] = Bool(s => s.EscWindowedConfirm, (s, v) => s.EscWindowedConfirm = v),
        ["mouse.invertWheel"] = Bool(s => s.InvertWheelDirection, (s, v) => s.InvertWheelDirection = v),
        ["selection.buffer"] = Bool(s => s.SelectionBufferEnabled, (s, v) => s.SelectionBufferEnabled = v),
        ["selection.bufferAllModes"] = Bool(s => s.SelectionBufferAllModes, (s, v) => s.SelectionBufferAllModes = v),
        ["developer.reverseSelectionZoomOut"] = Bool(s => s.ReverseSelectionZoomOutScale, (s, v) => s.ReverseSelectionZoomOutScale = v),
        ["mouse.backgroundWindowDrag"] = Bool(s => s.BackgroundDragWindow, (s, v) => s.BackgroundDragWindow = v),
        ["performance.speedBoost"] = Bool(s => s.SpeedBoostEnabled, (s, v) => s.SpeedBoostEnabled = v),
        ["performance.startWithWindowsInBackground"] = Bool(s => s.StartWithWindowsInBackground, (s, v) => s.StartWithWindowsInBackground = v),
        ["performance.adaptivePreview"] = Bool(s => s.AdaptiveFastPreview, (s, v) => s.AdaptiveFastPreview = v),
        ["performance.sequentialReads"] = Bool(s => s.SequentialForegroundReads, (s, v) => s.SequentialForegroundReads = v),
        ["performance.prefetch"] = Bool(s => s.PredictivePrefetch, (s, v) => s.PredictivePrefetch = v),
        ["performance.backgroundRefinement"] = Bool(s => s.BackgroundRefinement, (s, v) => s.BackgroundRefinement = v),
        ["performance.purgeOnMinimize"] = Bool(s => s.PurgeCacheOnMinimize, (s, v) => s.PurgeCacheOnMinimize = v),
        ["developer.startupDiagnostics"] = Bool(s => s.StartupDiagnostics, (s, v) => s.StartupDiagnostics = v),
        ["performance.prefetchSiblingFolders"] = Bool(s => s.PrefetchSiblingFolders, (s, v) => s.PrefetchSiblingFolders = v),
        ["performance.progressiveColor"] = Bool(s => s.ProgressiveColorFirstPreview, (s, v) => s.ProgressiveColorFirstPreview = v),
        ["status.navigation"] = Bool(s => s.StatusShowNavigation, (s, v) => s.StatusShowNavigation = v),
        ["status.stackInfoLine"] = Bool(s => s.StatusStackInfoLine, (s, v) => s.StatusStackInfoLine = v),
        ["status.gifOnlyOnGifs"] = Bool(s => s.StatusGifControlsOnlyOnGifs, (s, v) => s.StatusGifControlsOnlyOnGifs = v),
        ["status.fileNameClickActions"] = Bool(s => s.StatusFileNameClickActions, (s, v) => s.StatusFileNameClickActions = v),
        ["settings.alwaysExpand"] = Bool(s => s.SettingsAlwaysExpand, (s, v) => s.SettingsAlwaysExpand = v),
        ["status.autoResize"] = Bool(s => s.StatusBarAutoFit, (s, v) => s.StatusBarAutoFit = v),
        ["developer.overlayAbsoluteCoordinates"] = Bool(s => s.OverlayAbsoluteCoordinatesOnResize, (s, v) => s.OverlayAbsoluteCoordinatesOnResize = v),
        ["status.zoom"] = Bool(s => s.StatusShowZoom, (s, v) => s.StatusShowZoom = v),
        ["status.slideshow"] = Bool(s => s.StatusShowSlideshow, (s, v) => s.StatusShowSlideshow = v),
        ["status.fit"] = Bool(s => s.StatusShowFit, (s, v) => s.StatusShowFit = v),
        ["status.info"] = Bool(s => s.StatusShowInfo, (s, v) => s.StatusShowInfo = v),
        ["status.options"] = Bool(s => s.StatusShowOptions, (s, v) => s.StatusShowOptions = v),
        ["status.close"] = Bool(s => s.StatusShowClose, (s, v) => s.StatusShowClose = v),
        ["status.collapsedCorner"] = Bool(s => s.StatusCollapsedToCorner, (s, v) => s.StatusCollapsedToCorner = v),
        ["status.autoHide"] = Bool(s => s.StatusAutoHide, (s, v) => s.StatusAutoHide = v),
        ["status.reserveFitSpace"] = Bool(s => s.StatusReserveFitSpace, (s, v) => s.StatusReserveFitSpace = v),
        ["status.statIndex"] = Bool(s => s.StatusStatIndex, (s, v) => s.StatusStatIndex = v),
        ["status.statResolution"] = Bool(s => s.StatusStatResolution, (s, v) => s.StatusStatResolution = v),
        ["status.statZoom"] = Bool(s => s.StatusStatZoom, (s, v) => s.StatusStatZoom = v),
        ["status.statFileSize"] = Bool(s => s.StatusStatFileSize, (s, v) => s.StatusStatFileSize = v),
        ["status.statFormat"] = Bool(s => s.StatusStatFormat, (s, v) => s.StatusStatFormat = v),
        ["status.statFileName"] = Bool(s => s.StatusStatFileName, (s, v) => s.StatusStatFileName = v),
        ["slideshow.loop"] = Bool(s => s.SlideshowLoop, (s, v) => s.SlideshowLoop = v),
        ["slideshow.crossFolders"] = Bool(s => s.SlideshowCrossFolders, (s, v) => s.SlideshowCrossFolders = v),
        ["slideshow.shuffle"] = Bool(s => s.SlideshowShuffle, (s, v) => s.SlideshowShuffle = v),
        ["slideshow.startFullscreen"] = Bool(s => s.SlideshowStartFullscreen, (s, v) => s.SlideshowStartFullscreen = v),
        ["slideshow.pauseInactive"] = Bool(s => s.SlideshowPauseWhenInactive, (s, v) => s.SlideshowPauseWhenInactive = v),
        ["navigation.sortDescending"] = Bool(s => s.NavigationSortDescending, (s, v) => s.NavigationSortDescending = v),
        ["slideshow.showQualityIndicator"] = Bool(s => s.SlideshowShowQualityIndicator, (s, v) => s.SlideshowShowQualityIndicator = v),
        ["tabs.overflowArrows"] = Bool(s => s.TabOverflowArrows, (s, v) => s.TabOverflowArrows = v),
        ["tabs.detach"] = Bool(s => s.TabDetachEnabled, (s, v) => s.TabDetachEnabled = v),
        ["tabs.attach"] = Bool(s => s.TabAttachEnabled, (s, v) => s.TabAttachEnabled = v),
        ["tabs.closeEmptyAfterDetach"] = Bool(s => s.CloseEmptyWindowAfterDetach, (s, v) => s.CloseEmptyWindowAfterDetach = v),
        ["tabs.detachedHome"] = Bool(s => s.DetachedWindowHomeTab, (s, v) => s.DetachedWindowHomeTab = v),
        ["explorer.showHidden"] = Bool(s => s.ExplorerShowHiddenFiles, (s, v) => s.ExplorerShowHiddenFiles = v),
        ["explorer.onlyImages"] = Bool(s => s.ExplorerShowOnlyImages, (s, v) => s.ExplorerShowOnlyImages = v),
        ["tabs.confirmCloseMultiple"] = Bool(s => s.ConfirmCloseMultipleTabs, (s, v) => s.ConfirmCloseMultipleTabs = v),
        ["folderNav.showCounter"] = Bool(s => s.ShowFolderCounter, (s, v) => s.ShowFolderCounter = v),
        ["folderNav.counterGrandparent"] = Bool(s => s.FolderCounterIncludeGrandparent, (s, v) => s.FolderCounterIncludeGrandparent = v),
        ["folderNav.group"] = Bool(s => s.FolderNavShowGroup, (s, v) => s.FolderNavShowGroup = v),
        ["folderNav.previous"] = Bool(s => s.FolderNavShowPrevious, (s, v) => s.FolderNavShowPrevious = v),
        ["folderNav.next"] = Bool(s => s.FolderNavShowNext, (s, v) => s.FolderNavShowNext = v),
        ["folderNav.skipEmpty"] = Bool(s => s.FolderNavSkipEmpty, (s, v) => s.FolderNavSkipEmpty = v),
        ["folderNav.includeHidden"] = Bool(s => s.FolderNavIncludeHidden, (s, v) => s.FolderNavIncludeHidden = v),
        ["folderNav.wrap"] = Bool(s => s.FolderNavWrap, (s, v) => s.FolderNavWrap = v),
        ["folderNav.openFirst"] = Bool(s => s.FolderNavOpenFirstImage, (s, v) => s.FolderNavOpenFirstImage = v),
        ["overlay.pictureCounter"] = Bool(s => s.PictureCounterEnabled, (s, v) => s.PictureCounterEnabled = v),
        ["overlay.pictureBold"] = Bool(s => s.PictureCounterBold, (s, v) => s.PictureCounterBold = v),
        ["overlay.pictureShadow"] = Bool(s => s.PictureCounterShadow, (s, v) => s.PictureCounterShadow = v),
        ["overlay.rememberFolder"] = Bool(s => s.OverlayRememberFolder, (s, v) => s.OverlayRememberFolder = v),
        ["overlay.persistSession"] = Bool(s => s.OverlayPersistLayoutBetweenSessions, (s, v) => s.OverlayPersistLayoutBetweenSessions = v),
        ["overlay.wholeAppAlwaysStart"] = Bool(s => s.AlwaysStartWholeAppOverlayMode, (s, v) => s.AlwaysStartWholeAppOverlayMode = v),
        ["overlay.wholeAppWindowedInteractions"] = Bool(s => s.WholeAppOverlayUseWindowedInteractions, (s, v) => s.WholeAppOverlayUseWindowedInteractions = v),
        ["overlay.keyboardZoom"] = Bool(s => s.OverlayKeyboardZoom, (s, v) => s.OverlayKeyboardZoom = v),
        ["overlay.highlightSelected"] = Bool(s => s.OverlaySelectedHighlight, (s, v) => s.OverlaySelectedHighlight = v),
        ["overlay.rememberZoom"] = Bool(s => s.OverlayRememberZoom, (s, v) => s.OverlayRememberZoom = v),
        ["overlay.scaleWithWindow"] = Bool(s => s.OverlayScaleWithWindow, (s, v) => s.OverlayScaleWithWindow = v),
        ["overlay.animationPauseWhileInteracting"] = Bool(s => s.OverlayAnimationPauseWhileInteracting, (s, v) => s.OverlayAnimationPauseWhileInteracting = v),
        ["overlay.animationAvoidOverlap"] = Bool(s => s.OverlayAnimationAvoidOverlap, (s, v) => s.OverlayAnimationAvoidOverlap = v),
        ["navigation.hierarchicalPreviousEntry"] = Text(s => s.HierarchicalPreviousFolderEntry, (s, v) => s.HierarchicalPreviousFolderEntry = v),
        ["navigation.rateLimitInput"] = Text(s => s.NavigationRateLimitInput, (s, v) => s.NavigationRateLimitInput = v),
        ["navigation.rateLimitIndicatorPosition"] = Text(s => s.NavigationRateLimitIndicatorPosition, (s, v) => s.NavigationRateLimitIndicatorPosition = v),
        ["appearance.theme"] = Text(s => s.ThemeChoice, (s, v) => s.ThemeChoice = v),
        ["appearance.accent"] = Text(s => s.AccentChoice, (s, v) => s.AccentChoice = v),
        ["appearance.glowColor"] = Text(s => s.GlowChoice, (s, v) => s.GlowChoice = v),
        ["appearance.background"] = Text(s => s.MainBackgroundChoice, (s, v) => s.MainBackgroundChoice = v),
        ["general.startupAction"] = Text(s => s.StartupAction, (s, v) => s.StartupAction = v),
        ["general.newTabAction"] = Text(s => s.NewTabAction, (s, v) => s.NewTabAction = v),
        ["window.showInExplorerTarget"] = Text(s => s.ShowInExplorerTarget, (s, v) => s.ShowInExplorerTarget = v),
        ["windows.externalOpenBehavior"] = Text(s => s.ExternalOpenBehavior, (s, v) => s.ExternalOpenBehavior = v),
        ["windows.sameImageBehavior"] = Text(s => s.SameImageAlreadyOpenBehavior, (s, v) => s.SameImageAlreadyOpenBehavior = v),
        ["caption.windowedMinimize"] = Text(s => s.CaptionWindowedMinimizeAction, (s, v) => s.CaptionWindowedMinimizeAction = v),
        ["caption.windowedMaximize"] = Text(s => s.CaptionWindowedMaximizeAction, (s, v) => s.CaptionWindowedMaximizeAction = v),
        ["caption.windowedClose"] = Text(s => s.CaptionWindowedCloseAction, (s, v) => s.CaptionWindowedCloseAction = v),
        ["caption.fullscreenMinimize"] = Text(s => s.CaptionFullscreenMinimizeAction, (s, v) => s.CaptionFullscreenMinimizeAction = v),
        ["caption.fullscreenMaximize"] = Text(s => s.CaptionFullscreenMaximizeAction, (s, v) => s.CaptionFullscreenMaximizeAction = v),
        ["caption.fullscreenClose"] = Text(s => s.CaptionFullscreenCloseAction, (s, v) => s.CaptionFullscreenCloseAction = v),
        ["windows.alwaysOnTopMode"] = Text(s => s.AlwaysOnTopMode, (s, v) => s.AlwaysOnTopMode = v),
        ["fullscreen.exitBehavior"] = Text(s => s.FullscreenExitBehavior, (s, v) => s.FullscreenExitBehavior = v),
        ["view.defaultMode"] = Text(s => s.DefaultViewMode, (s, v) => s.DefaultViewMode = v),
        ["mouse.leftWindowDrag"] = Text(s => s.LeftWindowDragBehavior, (s, v) => s.LeftWindowDragBehavior = v),
        ["mouse.rightDragPan"] = Text(s => s.RightDragBehavior, (s, v) => s.RightDragBehavior = v),
        ["performance.initialQuality"] = Text(s => s.InitialImageQuality, (s, v) => s.InitialImageQuality = v),
        ["performance.interactivePanQuality"] = Text(s => s.InteractivePanQuality, (s, v) => s.InteractivePanQuality = v),
        ["performance.speedBoostTrayIcon"] = Text(s => s.SpeedBoostTrayIcon, (s, v) => s.SpeedBoostTrayIcon = v),
        ["status.size"] = Text(s => s.StatusBarSize, (s, v) => s.StatusBarSize = v),
        ["status.alignment"] = Text(s => s.StatusBarAlignment, (s, v) => s.StatusBarAlignment = v),
        ["status.fitScope"] = Text(s => s.StatusFitScope, (s, v) => s.StatusFitScope = v),
        ["slideshow.direction"] = Text(s => s.SlideshowDirection, (s, v) => s.SlideshowDirection = v),
        ["navigation.sortOrder"] = Text(s => s.NavigationSortOrder, (s, v) => s.NavigationSortOrder = v),
        ["tabs.cycleOrder"] = Text(s => s.TabCycleOrder, (s, v) => s.TabCycleOrder = v),
        ["tabs.tooltipTextSize"] = Text(s => s.TabToolTipTextSize, (s, v) => s.TabToolTipTextSize = v),
        ["tabs.position"] = Text(s => s.TabStripPosition, (s, v) => s.TabStripPosition = v),
        ["tabs.sideAutoHide"] = Bool(s => s.TabSideAutoHide, (s, v) => s.TabSideAutoHide = v),
        ["tabs.topAutoHide"] = Bool(s => s.TabTopAutoHide, (s, v) => s.TabTopAutoHide = v),
        ["explorer.defaultView"] = Text(s => s.ExplorerDefaultView, (s, v) => s.ExplorerDefaultView = v),
        ["tabs.lastTabBehavior"] = Text(s => s.LastTabCloseBehavior, (s, v) => s.LastTabCloseBehavior = v),
        ["tabs.homePage"] = Text(s => s.HomePageMode, (s, v) => s.HomePageMode = v),
        ["folderNav.order"] = Text(s => s.FolderNavOrder, (s, v) => s.FolderNavOrder = v),
        ["overlay.picturePosition"] = Text(s => s.PictureCounterPosition, (s, v) => s.PictureCounterPosition = v),
        ["overlay.pictureColor"] = Text(s => s.PictureCounterColor, (s, v) => s.PictureCounterColor = v),
        ["overlay.animationRegion"] = Text(s => s.OverlayAnimationRegion, (s, v) => s.OverlayAnimationRegion = v),
    };

    public static Accessor? Find(string settingId) => All.TryGetValue(settingId, out var accessor) ? accessor : null;

    /// <summary>
    /// Applies the derived fields SettingsWindow.ReadControls writes next to some settings, so a hotkey change is
    /// byte-for-byte what pressing Apply after the same edit in Settings would store.
    /// </summary>
    public static void NormalizeDerived(GlideSettingsState state, string settingId)
    {
        switch (settingId)
        {
            case "appearance.theme":
                state.LightTheme = string.Equals(state.ThemeChoice, "Light", StringComparison.OrdinalIgnoreCase);
                break;
            case "mouse.rightDragPan":
                state.RightDragPan = !string.Equals(state.RightDragBehavior, "Do nothing", StringComparison.OrdinalIgnoreCase);
                break;
            case "window.showInExplorerTarget":
                state.ShowInExplorerTarget = ShowInExplorerTargets.Normalize(state.ShowInExplorerTarget);
                break;
            case "explorer.defaultView":
                state.ExplorerDefaultView = ExplorerDefaultViews.Normalize(state.ExplorerDefaultView);
                break;
            case "navigation.sortOrder":
                state.NavigationSortOrder = Glide.Core.ImageSortOrder.NormalizeDisplayName(state.NavigationSortOrder);
                break;
            case "tabs.confirmCloseMultiple":
                if (state.ConfirmCloseMultipleTabs) state.RememberedMultiTabCloseChoice = "Ask";
                break;
        }
    }
}
