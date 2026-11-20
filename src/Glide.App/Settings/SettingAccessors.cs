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
        ["navigation.hierarchicalFolders"] = Bool(s => s.HierarchicalFolderTraversal, (s, v) => s.HierarchicalFolderTraversal = v),
        ["navigation.confirmHierarchicalBoundary"] = Bool(s => s.ConfirmHierarchicalFolderTraversal, (s, v) => s.ConfirmHierarchicalFolderTraversal = v),
        ["navigation.skipHint"] = Bool(s => s.NavigationSkipHint, (s, v) => s.NavigationSkipHint = v),
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
        ["fullscreen.hideCursorWindowed"] = Bool(s => s.HideCursorNormalWindow, (s, v) => s.HideCursorNormalWindow = v),
        ["fullscreen.autoHideChrome"] = Bool(s => s.AutoHideFullscreenChrome, (s, v) => s.AutoHideFullscreenChrome = v),
        ["tabs.enabled"] = Bool(s => s.TabsEnabled, (s, v) => s.TabsEnabled = v),
        ["fullscreen.statusAlways"] = Bool(s => s.FullscreenStatusAlwaysOn, (s, v) => s.FullscreenStatusAlwaysOn = v),
        ["fullscreen.keepTabBarOpen"] = Bool(s => s.FullscreenKeepTabBarOpen, (s, v) => s.FullscreenKeepTabBarOpen = v),
        ["fullscreen.keepTitleBarForExplorer"] = Bool(s => s.FullscreenKeepTitleBarForExplorer, (s, v) => s.FullscreenKeepTitleBarForExplorer = v),
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
        ["explorer.itemInfoTooltip"] = Bool(s => s.ExplorerItemInfoTooltip, (s, v) => s.ExplorerItemInfoTooltip = v),
        ["explorer.addressBarOverPictures"] = Bool(s => s.ShowAddressBarOverPictures, (s, v) => s.ShowAddressBarOverPictures = v),
        ["explorer.infoDate"] = Bool(s => s.ExplorerInfoDate, (s, v) => s.ExplorerInfoDate = v),
        ["explorer.infoResolution"] = Bool(s => s.ExplorerInfoResolution, (s, v) => s.ExplorerInfoResolution = v),
        ["explorer.infoFileSize"] = Bool(s => s.ExplorerInfoFileSize, (s, v) => s.ExplorerInfoFileSize = v),
        ["explorer.keepSelectionInView"] = Bool(s => s.ExplorerKeepSelectionInView, (s, v) => s.ExplorerKeepSelectionInView = v),
        ["explorer.clickToRename"] = Bool(s => s.ExplorerClickToRename, (s, v) => s.ExplorerClickToRename = v),
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
        ["overlay.wholeAppShowDragStrip"] = Bool(s => s.WholeAppOverlayShowDragStrip, (s, v) => s.WholeAppOverlayShowDragStrip = v),
        ["overlay.wholeAppTopCentreGrip"] = Bool(s => s.WholeAppOverlayTopCentreGrip, (s, v) => s.WholeAppOverlayTopCentreGrip = v),
        ["overlay.wholeAppLockPosition"] = Bool(s => s.WholeAppOverlayLockPosition, (s, v) => s.WholeAppOverlayLockPosition = v),
        ["overlay.keyboardZoom"] = Bool(s => s.OverlayKeyboardZoom, (s, v) => s.OverlayKeyboardZoom = v),
        ["overlay.highlightSelected"] = Bool(s => s.OverlaySelectedHighlight, (s, v) => s.OverlaySelectedHighlight = v),
        ["overlay.rememberZoom"] = Bool(s => s.OverlayRememberZoom, (s, v) => s.OverlayRememberZoom = v),
        ["overlay.scaleWithWindow"] = Bool(s => s.OverlayScaleWithWindow, (s, v) => s.OverlayScaleWithWindow = v),
        ["overlay.animationPauseWhileInteracting"] = Bool(s => s.OverlayAnimationPauseWhileInteracting, (s, v) => s.OverlayAnimationPauseWhileInteracting = v),
        ["overlay.animationAvoidOverlap"] = Bool(s => s.OverlayAnimationAvoidOverlap, (s, v) => s.OverlayAnimationAvoidOverlap = v),
        ["navigation.endOfFolder"] = Text(s => s.EndOfFolderAction, (s, v) => s.EndOfFolderAction = v),
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
        ["fullscreen.hideCursorMode"] = Text(s => s.HideCursorMode, (s, v) => s.HideCursorMode = v),
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
        ["explorer.scrollMode"] = Text(s => s.ExplorerScrollMode, (s, v) => s.ExplorerScrollMode = v),
        ["explorer.locateOpensIn"] = Text(s => s.ExplorerLocateOpensIn, (s, v) => s.ExplorerLocateOpensIn = v),
        ["explorer.backReturnsToImage"] = Text(s => s.ExplorerBackReturnsToImage, (s, v) => s.ExplorerBackReturnsToImage = v),
        ["explorer.defaultView"] = Text(s => s.ExplorerDefaultView, (s, v) => s.ExplorerDefaultView = v),
        ["tabs.lastTabBehavior"] = Text(s => s.LastTabCloseBehavior, (s, v) => s.LastTabCloseBehavior = v),
        ["tabs.homePage"] = Text(s => s.HomePageMode, (s, v) => s.HomePageMode = v),
        ["folderNav.order"] = Text(s => s.FolderNavOrder, (s, v) => s.FolderNavOrder = v),
        ["overlay.picturePositionTopTabs"] = Text(s => s.PictureCounterPositionTopTabs, (s, v) => s.PictureCounterPositionTopTabs = v),
        ["overlay.picturePositionSideTabs"] = Text(s => s.PictureCounterPositionSideTabs, (s, v) => s.PictureCounterPositionSideTabs = v),
        ["overlay.pictureColor"] = Text(s => s.PictureCounterColor, (s, v) => s.PictureCounterColor = v),
        ["overlay.pictureAlignmentTopTabs"] = Text(s => s.PictureCounterAlignmentTopTabs, (s, v) => s.PictureCounterAlignmentTopTabs = v),
        ["overlay.pictureAlignmentSideTabs"] = Text(s => s.PictureCounterAlignmentSideTabs, (s, v) => s.PictureCounterAlignmentSideTabs = v),
        ["overlay.animationRegion"] = Text(s => s.OverlayAnimationRegion, (s, v) => s.OverlayAnimationRegion = v),
        ["tabs.newTabPosition"] = Text(s => s.NewTabPosition, (s, v) => s.NewTabPosition = v),
        ["tabs.switchToNewTabs"] = Text(s => Glide.Core.Workspace.NewTabSides.FromSwitch(s.SwitchToNewTabs), (s, v) => s.SwitchToNewTabs = Glide.Core.Workspace.NewTabSides.ToSwitch(v)),
        ["tabs.newTabPositionKey"] = Text(s => s.NewTabPositionKey, (s, v) => s.NewTabPositionKey = v),
        ["tabs.newTabForegroundKey"] = Text(s => s.NewTabForegroundKey, (s, v) => s.NewTabForegroundKey = v),
        ["tabs.middleClickSide"] = Text(s => s.MiddleClickNewTabSide, (s, v) => s.MiddleClickNewTabSide = v),
        ["tabs.middleClickPosition"] = Text(s => s.MiddleClickNewTabPosition, (s, v) => s.MiddleClickNewTabPosition = v),
        ["tabs.afterCloseActivate"] = Text(s => s.AfterTabCloseActivate, (s, v) => s.AfterTabCloseActivate = v),
        ["tabs.afterCloseCustomTarget"] = Text(s => s.AfterTabCloseCustomTarget, (s, v) => s.AfterTabCloseCustomTarget = v),
        ["tabs.hoverPreview"] = Bool(s => s.TabHoverPreviews, (s, v) => s.TabHoverPreviews = v),
        ["tabs.animateGifPreviews"] = Bool(s => s.AnimateGifPreviews, (s, v) => s.AnimateGifPreviews = v),
        ["tabs.previewResizeModifier"] = Text(s => s.TabPreviewResizeModifier, (s, v) => s.TabPreviewResizeModifier = v),
        ["overlay.pictureBorder"] = Bool(s => s.PictureCounterBorder, (s, v) => s.PictureCounterBorder = v),
    };

    /// <summary>
    /// Stage C single source: every catalogued setting that is one plain GlideSettingsState property, typed, keyed by
    /// SettingsCatalog id. It is <see cref="All"/> (the hotkey-bindable Toggle/Choice rows, unchanged) plus the number, text
    /// and non-bindable toggle rows below. Consumers: SettingsWindow LoadControls/ReadControls load and read every mapped
    /// CheckBox/ToggleSwitch from it (no hand-written line per toggle), and SettingsDelta turns a state change into the
    /// changed setting ids that MainWindow routes to their SettingEffectRegistry owner. Settings that are several
    /// properties, derived or stored elsewhere (thumbnail provider, hotkeys, gestures, title-bar buttons) are not here.
    /// </summary>
    public static IReadOnlyDictionary<string, ValueAccessor> Values { get; } = BuildValues();

    internal enum ValueKind { Bool, Text, Int, Double }

    internal sealed record ValueAccessor(string Id, ValueKind Type, Func<GlideSettingsState, object?> Get, Action<GlideSettingsState, object?> Set);

    private static ValueAccessor Flag(Func<GlideSettingsState, bool> get, Action<GlideSettingsState, bool> set) => new("", ValueKind.Bool, s => get(s), (s, v) => set(s, (bool)v!));
    private static ValueAccessor Int(Func<GlideSettingsState, int> get, Action<GlideSettingsState, int> set) => new("", ValueKind.Int, s => get(s), (s, v) => set(s, (int)v!));
    private static ValueAccessor Double(Func<GlideSettingsState, double> get, Action<GlideSettingsState, double> set) => new("", ValueKind.Double, s => get(s), (s, v) => set(s, (double)v!));
    private static ValueAccessor Str(Func<GlideSettingsState, string> get, Action<GlideSettingsState, string> set) => new("", ValueKind.Text, s => get(s), (s, v) => set(s, (string)v!));

    private static Dictionary<string, ValueAccessor> BuildValues()
    {
        var values = new Dictionary<string, ValueAccessor>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, a) in All)
        {
            if (a.GetBool is { } gb && a.SetBool is { } sb) values[id] = new(id, ValueKind.Bool, s => gb(s), (s, v) => sb(s, (bool)v!));
            else if (a.GetText is { } gt && a.SetText is { } st) values[id] = new(id, ValueKind.Text, s => gt(s), (s, v) => st(s, (string)v!));
        }
        foreach (var (id, v) in NonBindableRows()) values.Add(id, v with { Id = id });
        return values;
    }

    /// <summary>Numbers, paths/text and toggles that are not hotkey-bindable (SettingActionCatalog.Excluded or Number/Text kind).</summary>
    private static Dictionary<string, ValueAccessor> NonBindableRows() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["general.historySize"] = Int(s => s.HistorySize, (s, v) => s.HistorySize = v),
        ["general.openFileDefaultDirectory"] = Str(s => s.OpenFileDefaultDirectory, (s, v) => s.OpenFileDefaultDirectory = v),
        ["navigation.rateLimitMs"] = Int(s => s.NavigationRateLimitMs, (s, v) => s.NavigationRateLimitMs = v),
        ["general.newExplorerTabLastLocation"] = Flag(s => s.NewExplorerTabsUseLastLocation, (s, v) => s.NewExplorerTabsUseLastLocation = v),
        ["general.newExplorerTabDefaultDirectory"] = Str(s => s.NewExplorerTabDefaultDirectory, (s, v) => s.NewExplorerTabDefaultDirectory = v),
        ["appearance.explorerTheme"] = Str(s => s.ExplorerThemeChoice, (s, v) => s.ExplorerThemeChoice = v),
        ["explorer.navPaneWidth"] = Int(s => s.ExplorerNavPaneWidth, (s, v) => s.ExplorerNavPaneWidth = v),
        ["appearance.tooltipDelaySettings"] = Int(s => s.TooltipDelaySettingsMs, (s, v) => s.TooltipDelaySettingsMs = v),
        ["appearance.tooltipDelayMain"] = Int(s => s.TooltipDelayMainMs, (s, v) => s.TooltipDelayMainMs = v),
        ["appearance.tooltipDelayOverlay"] = Int(s => s.TooltipDelayOverlayMs, (s, v) => s.TooltipDelayOverlayMs = v),
        ["appearance.tooltipDelayExplorer"] = Int(s => s.TooltipDelayExplorerMs, (s, v) => s.TooltipDelayExplorerMs = v),
        ["tabs.previewSize"] = Int(s => s.TabPreviewSizePercent, (s, v) => s.TabPreviewSizePercent = v),
        ["appearance.glowIntensity"] = Int(s => s.GlowIntensityPercent, (s, v) => s.GlowIntensityPercent = v),
        ["general.startupCustomPath"] = Str(s => s.StartupCustomPath, (s, v) => s.StartupCustomPath = v),
        ["general.newTabCustomPath"] = Str(s => s.NewTabCustomPath, (s, v) => s.NewTabCustomPath = v),
        ["fullscreen.hideCursorDelay"] = Double(s => s.FullscreenHideCursorDelaySeconds, (s, v) => s.FullscreenHideCursorDelaySeconds = v),
        ["explorer.scrollSpeed"] = Double(s => s.ExplorerScrollSpeed, (s, v) => s.ExplorerScrollSpeed = v),
        ["explorer.scrollRows"] = Int(s => s.ExplorerScrollRows, (s, v) => s.ExplorerScrollRows = v),
        ["explorer.scrollPixels"] = Int(s => s.ExplorerScrollPixels, (s, v) => s.ExplorerScrollPixels = v),
        ["explorer.altFileManagerPath"] = Str(s => s.AlternativeFileManagerPath, (s, v) => s.AlternativeFileManagerPath = v),
        ["explorer.altFileManagerArguments"] = Str(s => s.AlternativeFileManagerArguments, (s, v) => s.AlternativeFileManagerArguments = v),
        ["tabs.showBackButton"] = Flag(s => s.TabBarShowBackButton, (s, v) => s.TabBarShowBackButton = v),
        ["tabs.showForwardButton"] = Flag(s => s.TabBarShowForwardButton, (s, v) => s.TabBarShowForwardButton = v),
        ["view.zoomStep"] = Int(s => s.ViewportZoomStepPercent, (s, v) => s.ViewportZoomStepPercent = v),
        ["selection.bufferWindowedPercent"] = Int(s => s.SelectionBufferWindowedPercent, (s, v) => s.SelectionBufferWindowedPercent = v),
        ["selection.bufferMaximizedPercent"] = Int(s => s.SelectionBufferMaximizedPercent, (s, v) => s.SelectionBufferMaximizedPercent = v),
        ["performance.previewDelay"] = Int(s => s.AdaptivePreviewDelayMs, (s, v) => s.AdaptivePreviewDelayMs = v),
        ["performance.prefetchDepth"] = Int(s => s.PrefetchDepth, (s, v) => s.PrefetchDepth = v),
        ["performance.prefetchSiblingFolderCount"] = Int(s => s.PrefetchSiblingFolderImageCount, (s, v) => s.PrefetchSiblingFolderImageCount = v),
        ["performance.cacheItems"] = Int(s => s.CacheItems, (s, v) => s.CacheItems = v),
        ["performance.compressedCacheMb"] = Int(s => s.CompressedCacheMegabytes, (s, v) => s.CompressedCacheMegabytes = v),
        ["performance.decodedCacheMb"] = Int(s => s.DecodedCacheMegabytes, (s, v) => s.DecodedCacheMegabytes = v),
        ["performance.rapidPreviewSize"] = Int(s => s.RapidPreviewLongestSide, (s, v) => s.RapidPreviewLongestSide = v),
        ["performance.rapidBrowsePreviewSize"] = Int(s => s.RapidBrowsePreviewLongestSide, (s, v) => s.RapidBrowsePreviewLongestSide = v),
        ["status.revealZoneExpand"] = Int(s => s.StatusRevealZoneExpand, (s, v) => s.StatusRevealZoneExpand = v),
        ["slideshow.interval"] = Int(s => s.SlideshowIntervalMs, (s, v) => s.SlideshowIntervalMs = v),
        ["tabs.minWidth"] = Int(s => s.TabMinWidth, (s, v) => s.TabMinWidth = v),
        ["tabs.maxWidth"] = Int(s => s.TabMaxWidth, (s, v) => s.TabMaxWidth = v),
        ["tabs.closedHistory"] = Int(s => s.ClosedTabHistoryLimit, (s, v) => s.ClosedTabHistoryLimit = v),
        ["folderNav.explore"] = Flag(s => s.FolderNavShowExplore, (s, v) => s.FolderNavShowExplore = v),
        ["overlay.pictureTemplate"] = Str(s => s.PictureCounterTemplate, (s, v) => s.PictureCounterTemplate = v),
        ["overlay.pictureFontSize"] = Int(s => s.PictureCounterFontSize, (s, v) => s.PictureCounterFontSize = v),
        ["overlay.pictureOpacity"] = Int(s => s.PictureCounterOpacity, (s, v) => s.PictureCounterOpacity = v),
        ["overlay.pictureOffsetX"] = Int(s => TextOverlayPlacements.Offset(s, TextOverlayPlacements.SideTabsLayout(s)).X, (s, v) => TextOverlayPlacements.SetOffset(s, TextOverlayPlacements.SideTabsLayout(s), v, null)),
        ["overlay.pictureOffsetY"] = Int(s => TextOverlayPlacements.Offset(s, TextOverlayPlacements.SideTabsLayout(s)).Y, (s, v) => TextOverlayPlacements.SetOffset(s, TextOverlayPlacements.SideTabsLayout(s), null, v)),
        ["overlay.defaultOpacity"] = Int(s => s.OverlayDefaultOpacity, (s, v) => s.OverlayDefaultOpacity = v),
        ["overlay.zoomStep"] = Int(s => s.OverlayZoomStepPercent, (s, v) => s.OverlayZoomStepPercent = v),
        ["overlay.animationSpeedDipsPerSecond"] = Int(s => s.OverlayAnimationSpeedDipsPerSecond, (s, v) => s.OverlayAnimationSpeedDipsPerSecond = v),
        ["overlay.animationTurnIntervalMs"] = Int(s => s.OverlayAnimationTurnIntervalMs, (s, v) => s.OverlayAnimationTurnIntervalMs = v),
        ["overlay.animationTurnAngleDegrees"] = Int(s => s.OverlayAnimationTurnAngleDegrees, (s, v) => s.OverlayAnimationTurnAngleDegrees = v),
        ["overlay.animationRefreshRateHz"] = Int(s => s.OverlayAnimationRefreshRateHz, (s, v) => s.OverlayAnimationRefreshRateHz = v),
        ["overlay.defaultDirectory"] = Str(s => s.OverlayDefaultDirectory, (s, v) => s.OverlayDefaultDirectory = v),
        ["profiles.defaultDirectory"] = Str(s => s.ProfileDefaultDirectory, (s, v) => s.ProfileDefaultDirectory = v),
        ["windows.external1"] = Str(s => s.ExternalProgram1, (s, v) => s.ExternalProgram1 = v),
        ["windows.external2"] = Str(s => s.ExternalProgram2, (s, v) => s.ExternalProgram2 = v),
        ["windows.external3"] = Str(s => s.ExternalProgram3, (s, v) => s.ExternalProgram3 = v),
        ["developer.statusGlobalScale"] = Int(s => s.StatusBarGlobalScalePercent, (s, v) => s.StatusBarGlobalScalePercent = v),
        ["developer.statusMaximizedBoost"] = Int(s => s.StatusBarMaximizedBoostPercent, (s, v) => s.StatusBarMaximizedBoostPercent = v),
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
            case "navigation.endOfFolder":
                state.EndOfFolderAction = Glide.Core.Navigation.EndOfFolderActions.Normalize(state.EndOfFolderAction);
                break;
            case "explorer.locateOpensIn":
                state.ExplorerLocateOpensIn = Glide.Core.Navigation.ExplorerLocateTargets.Normalize(state.ExplorerLocateOpensIn);
                break;
            case "explorer.backReturnsToImage":
                state.ExplorerBackReturnsToImage = Glide.Core.Navigation.ExplorerBackReturnModes.Normalize(state.ExplorerBackReturnsToImage);
                break;
            case "explorer.defaultView":
                state.ExplorerDefaultView = ExplorerDefaultViews.Normalize(state.ExplorerDefaultView);
                break;
            case "explorer.scrollMode":
                state.ExplorerScrollMode = Glide.App.Explorer.ExplorerScrollModes.Normalize(state.ExplorerScrollMode);
                break;
            case "navigation.sortOrder":
                state.NavigationSortOrder = Glide.Core.ImageSortOrder.NormalizeDisplayName(state.NavigationSortOrder);
                break;
            case "tabs.newTabPositionKey":
                state.NewTabPositionKey = Glide.Core.Workspace.NewTabKeys.Normalize(state.NewTabPositionKey);
                break;
            case "tabs.newTabForegroundKey":
                state.NewTabForegroundKey = Glide.Core.Workspace.NewTabKeys.Normalize(state.NewTabForegroundKey);
                break;
            case "tabs.newTabPosition":
                state.NewTabPosition = Glide.Core.Workspace.NewTabPositions.Normalize(state.NewTabPosition);
                break;
            case "tabs.middleClickSide":
                state.MiddleClickNewTabSide = Glide.Core.Workspace.MiddleClickSides.Normalize(state.MiddleClickNewTabSide);
                break;
            case "tabs.middleClickPosition":
                state.MiddleClickNewTabPosition = Glide.Core.Workspace.MiddleClickPositions.Normalize(state.MiddleClickNewTabPosition);
                break;
            case "tabs.afterCloseCustomTarget":
                state.AfterTabCloseCustomTarget = Glide.Core.Workspace.TabCloseCustomTargets.Normalize(state.AfterTabCloseCustomTarget);
                break;
            case "tabs.afterCloseActivate":
                state.AfterTabCloseActivate = Glide.Core.Workspace.TabCloseActivation.Normalize(state.AfterTabCloseActivate);
                break;
            case "tabs.confirmCloseMultiple":
                if (state.ConfirmCloseMultipleTabs) state.RememberedMultiTabCloseChoice = "Ask";
                break;
        }
    }
}
