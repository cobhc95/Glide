using Glide.Core.Commands;

namespace Glide.App.Settings;

/// <summary>
/// Human-readable runtime/settings state for Glide 3.0. The model deliberately stays flat:
/// every visible Settings control must map to one explicit property here or be disabled in the UI.
/// This makes zero-context AI debugging possible without reverse-engineering control state.
/// </summary>
public sealed record GlideSettingsState
{
    // Settings schema/migration provenance. Increment only when a one-time persisted migration is added.
    public int SettingsSchemaVersion { get; set; } = 1;

    // General
    public bool RecentHistoryEnabled { get; set; } = true;
    public int HistorySize { get; set; } = 8;
    public bool RememberWindowPlacement { get; set; } = true;
    public bool ReuseSingleInstance { get; set; } = true;
    public bool ContinueSiblingFolders { get; set; } = true;
    public bool HierarchicalFolderTraversal { get; set; } = true;
    public bool ConfirmHierarchicalFolderTraversal { get; set; } = true;
    public bool RememberLastOpenLocation { get; set; } = true;
    // New Explorer tabs have their own navigation-memory scope. Default = last Explorer-tab folder.
    public bool NewExplorerTabsUseLastLocation { get; set; } = true;
    public string NewExplorerTabDefaultDirectory { get; set; } = string.Empty;
    public string LastNewExplorerTabDirectory { get; set; } = string.Empty;
    public string LastOpenDirectory { get; set; } = string.Empty;
    public string LastOpenFileDirectory { get; set; } = string.Empty;
    // ThemeChoice is authoritative. LightTheme is retained only for backwards-compatible profile/settings migration.
    public string ThemeChoice { get; set; } = "Dark";
    // Glide Explorer theme: Follow Glide theme (default), Dark, Neutral, or Light.
    public string ExplorerThemeChoice { get; set; } = "Follow Glide theme";
    public bool LightTheme { get; set; }
    public string AccentChoice { get; set; } = "Glide blue";
    public string GlowChoice { get; set; } = "Follow accent";
    public string CustomAccentHex { get; set; } = "#38A9F5";
    public string CustomGlowHex { get; set; } = "#38A9F5";
    public string MainBackgroundChoice { get; set; } = "Follow theme";
    public string CustomMainBackgroundHex { get; set; } = "#101214";
    public int GlowIntensityPercent { get; set; } = 100;
    public bool ShowHomeTips { get; set; } = true;
    public bool ShowRecentOnHome { get; set; } = true;
    public bool ConfirmDiscardSettingsChanges { get; set; } = true;

    // Launch/workspace destinations. These are intentionally independent of HomePageMode:
    // HomePageMode controls the Home command/last-tab fallback, while these control process startup/new tabs.
    public string StartupAction { get; set; } = "Welcome tab";
    public string StartupCustomPath { get; set; } = string.Empty;
    public string NewTabAction { get; set; } = "Explorer tab";
    public string NewTabCustomPath { get; set; } = string.Empty;

    // Welcome-page layout is deliberately small and durable rather than storing raw pixels.
    public string WelcomeRecentPosition { get; set; } = "Below tips";
    public string WelcomeRecentSize { get; set; } = "Normal";

    // Interface & Behavior
    public bool ShowStatusSurface { get; set; } = true;
    public bool StatusShowOnHoverWhenClosed { get; set; } = false;
    public bool ShowScrollbars { get; set; } = true;
    public bool FullPathInTitle { get; set; }
    public bool HideCursorFullscreen { get; set; } = true;
    public int FullscreenHideCursorDelaySeconds { get; set; } = 2;
    public bool AutoHideFullscreenChrome { get; set; } = true;
    public bool TabsEnabled { get; set; } = true;
    // Explorer launches while another Glide instance is available.
    public string ExternalOpenBehavior { get; set; } = "Open in new tab";
    // Behavior only when Explorer asks Glide to open an image that is already open in any tab/window.
    // New process is the default to keep the Explorer gesture independent and unsurprising.
    public string SameImageAlreadyOpenBehavior { get; set; } = "Open new instance";
    public bool ConfirmCloseMultipleTabs { get; set; } = true;
    // Ask, Close all, or Close current tab.
    public string RememberedMultiTabCloseChoice { get; set; } = "Ask";
    // Legacy compatibility flag retained for imported profiles. New caption behavior is governed by the six action mappings below.
    public bool FullscreenXClosesApp { get; set; }
    public string CaptionWindowedMinimizeAction { get; set; } = "Minimize";
    public string CaptionWindowedMaximizeAction { get; set; } = "Maximize / restore";
    public string CaptionWindowedCloseAction { get; set; } = "Close Glide";
    public string CaptionFullscreenMinimizeAction { get; set; } = "Minimize";
    public string CaptionFullscreenMaximizeAction { get; set; } = "Exit fullscreen (restore previous placement)";
    public string CaptionFullscreenCloseAction { get; set; } = "Exit fullscreen maximized";
    public List<string> TitleBarButtons { get; set; } = TitleBarButtonCatalog.DefaultButtonIds.ToList();
    public bool TabBarShowBackButton { get; set; } = true;
    public bool TabBarShowForwardButton { get; set; } = true;
    // Navigate-to-folder title action: Internal browser (default) or Windows Explorer.
    public string NavigateToFolderBehavior { get; set; } = "Internal browser";
    public bool FullscreenStatusAlwaysOn { get; set; } = true;
    public bool FullscreenKeepTabBarOpen { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoCenterWindowOnRestore { get; set; }
    // What F11/Escape/fullscreen toggle should do when leaving fullscreen.
    // Default restores the exact pre-fullscreen normal placement/state captured on entry.
    public string FullscreenExitBehavior { get; set; } = "Restore size and location";
    public string DefaultViewMode { get; set; } = "Fit image";
    public bool PointerZoom { get; set; } = true;
    public bool PreserveManualZoomOnNavigate { get; set; }
    public int ViewportZoomStepPercent { get; set; } = 10;
    public bool UpscaleSmallImages { get; set; }
    public bool HighPrecisionSubpixelRendering { get; set; } = true;
    public bool ShowImageLoadingIndicator { get; set; } = true;
    public bool EnableTabFocusNavigation { get; set; }
    public bool EscStopsSlideshow { get; set; } = true;
    public bool EscExitsFullscreen { get; set; } = true;
    public bool EscWindowedConfirm { get; set; }
    // "Ask", "Close" or "KeepOpen". Only used when EscWindowedConfirm is enabled.
    public string EscWindowedRememberedChoice { get; set; } = "Ask";

    // Mouse & Fullscreen
    public bool DoubleClickFullscreen { get; set; } = true;
    public bool DoubleClickExitFullscreen { get; set; }
    public bool FullscreenClickNavigation { get; set; } = true;
    public bool WindowedWheelZoom { get; set; }
    public bool InvertWheelDirection { get; set; }
    public string LeftDragMode { get; set; } = "Create selection (Glide)";
    // Controls whether a left-drag gesture is allowed to turn into a native window move.
    // Default keeps ordinary/restored windows draggable but blocks that transition when maximized/fullscreen.
    public string LeftWindowDragBehavior { get; set; } = "Smart (normal window only)";
    // Smart = pan a genuinely cropped/zoomed image; otherwise move a normal window;
    // do nothing for the drag when maximized/fullscreen. Stationary right-click is unchanged.
    public string RightDragBehavior { get; set; } = "Smart (pan image / move window)";
    // Legacy compatibility field retained so older settings imports remain harmless.
    public bool RightDragPan { get; set; } = true;
    public bool SelectionClickZoom { get; set; } = true;
    public bool SelectionRightClickZoomOut { get; set; } = true;
    public bool ReverseSelectionZoomOutScale { get; set; } = true;
    public bool BackgroundDragWindow { get; set; } = true;
    public bool CtrlWheelZoom { get; set; } = true;
    public bool MiddleDragPan { get; set; }

    // Advanced gesture matrix. Every slot is persisted by stable ID and resolves through GestureCatalog.
    public Dictionary<string, string> Gestures { get; set; } = GestureCatalog.CreateDefaultMap();

    // Performance & Startup
    public string InitialImageQuality { get; set; } = "Balanced";
    public bool AdaptiveFastPreview { get; set; } = true; // decoder-scaled first useful frame
    public int AdaptivePreviewDelayMs { get; set; } = 40;
    public bool SequentialForegroundReads { get; set; } = true;
    public bool PredictivePrefetch { get; set; } = true;
    public bool BackgroundRefinement { get; set; } = true;
    public bool PurgeCacheOnMinimize { get; set; }
    public bool StartupDiagnostics { get; set; }
    public int PrefetchDepth { get; set; } = 5;
    public bool PrefetchSiblingFolders { get; set; }
    public int PrefetchSiblingFolderImageCount { get; set; } = 3;
    public int CacheItems { get; set; } = 16;
    public int CompressedCacheMegabytes { get; set; } = 256;
    public int DecodedCacheMegabytes { get; set; } = 256;
    public int RapidPreviewLongestSide { get; set; } = 3000;
    // Reduced-resolution frame used specifically while rapid keyboard/wheel browsing is active.
    // Presets stage sensible defaults, but the user may override this independently afterwards.
    public int RapidBrowsePreviewLongestSide { get; set; } = 1440;
    public bool ProgressiveColorFirstPreview { get; set; } = true;
    // "Fast", "Balanced", or "Full quality". The stock Balanced performance preset intentionally
    // uses Full quality while dragging/panning; users can still override this independently.
    public string InteractivePanQuality { get; set; } = "Full quality";
    public bool SpeedBoostEnabled { get; set; } = true;
    public bool StartWithWindowsInBackground { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool PerformanceUserCustom { get; set; }
    public bool PerformanceUserCustomSaved { get; set; }
    public string UserCustomPanQuality { get; set; } = "Full quality";
    public bool UserCustomAdaptivePreview { get; set; } = true;
    public int UserCustomRefinementDelayMs { get; set; } = 40;
    public bool UserCustomSequentialReads { get; set; } = true;
    public bool UserCustomPrefetch { get; set; } = true;
    public int UserCustomPrefetchDepth { get; set; } = 5;
    public bool UserCustomBackgroundRefinement { get; set; } = true;
    public int UserCustomCacheItems { get; set; } = 16;
    public int UserCustomCompressedCacheMb { get; set; } = 256;
    public int UserCustomDecodedCacheMb { get; set; } = 256;
    public int UserCustomPreviewLongestSide { get; set; } = 3000;
    public int UserCustomRapidBrowsePreviewSize { get; set; } = 1440;
    public bool UserCustomProgressiveColor { get; set; } = true;

    // Status bar
    public bool StatusShowNavigation { get; set; } = true;
    public bool StatusShowZoom { get; set; } = true;
    public bool StatusShowSlideshow { get; set; } = true;
    public bool StatusShowFit { get; set; } = true;
    public bool StatusShowInfo { get; set; } = true;
    public bool StatusShowOptions { get; set; } = true;
    public bool StatusShowClose { get; set; } = true;
    public bool StatusStatIndex { get; set; } = true;
    public bool StatusStatResolution { get; set; } = true;
    public bool StatusStatZoom { get; set; } = true;
    public bool StatusStatFileSize { get; set; }
    public bool StatusStatFormat { get; set; }
    // Current five-step status-size ladder preserves the visual mapping of older saved values during migration.
    public string StatusBarSize { get; set; } = "Medium";
    public int StatusBarGlobalScalePercent { get; set; } = 100;
    public bool StatusBarAutoFit { get; set; } = true;
    public int StatusBarMaximizedBoostPercent { get; set; } = 15;

    // Slideshow
    public int SlideshowIntervalMs { get; set; } = 3000;
    public bool SlideshowLoop { get; set; } = true;
    public bool SlideshowCrossFolders { get; set; } = true;
    public bool SlideshowShuffle { get; set; }
    public string SlideshowDirection { get; set; } = "Forward";
    public bool SlideshowStartFullscreen { get; set; }
    public bool SlideshowPauseWhenInactive { get; set; } = true;

    // Tabs & Workspace (only the currently implemented subset is active in UI)
    public int TabMinWidth { get; set; } = 120;
    public int TabMaxWidth { get; set; } = 240;
    public bool TabOverflowArrows { get; set; } = true;
    public bool TabDetachEnabled { get; set; } = true;
    public bool TabAttachEnabled { get; set; } = true;
    public int ClosedTabHistoryLimit { get; set; } = 20;
    public bool CloseEmptyWindowAfterDetach { get; set; }
    public bool DetachedWindowHomeTab { get; set; }
    public bool DoubleClickTabCloses { get; set; } = true;
    // "Open home page" or "Close program". Applies when the user closes the final ordinary tab.
    public string LastTabCloseBehavior { get; set; } = "Keep last tab open";
    // "Welcome page", "Browser page", or "Recent pictures page".
    public string HomePageMode { get; set; } = "Welcome page";
    public bool FolderNavShowGroup { get; set; } = true;
    public bool FolderNavShowPrevious { get; set; } = true;
    public bool FolderNavShowNext { get; set; } = true;
    public bool FolderNavShowExplore { get; set; } = true;
    public bool FolderNavSkipEmpty { get; set; } = true;
    public bool FolderNavIncludeHidden { get; set; }
    public bool FolderNavWrap { get; set; }
    public bool FolderNavOpenFirstImage { get; set; } = true;

    // Text overlay template (legacy property names retained for profile/settings compatibility; distinct from Window-in-Window image overlays)
    public bool PictureCounterEnabled { get; set; } = true;
    public string PictureCounterTemplate { get; set; } = "[{index}/{total}]";
    public int PictureCounterFontSize { get; set; } = 13;
    public bool PictureCounterBold { get; set; } = true;
    public int PictureCounterOpacity { get; set; } = 85;
    public string PictureCounterPosition { get; set; } = "Top right";
    public string PictureCounterColor { get; set; } = "White";
    public bool PictureCounterShadow { get; set; } = true;

    // Window in Window image overlays
    public int OverlayDefaultOpacity { get; set; } = 100;
    public bool OverlayRememberFolder { get; set; } = true;
    public bool OverlayPersistLayoutBetweenSessions { get; set; }
    // Whole-application frameless/transparent presentation is opt-in. Normal Glide remains the default.
    public bool AlwaysStartWholeAppOverlayMode { get; set; }
    // Whole-app Overlay defaults to Window-in-Window direct manipulation: left-drag moves the window.
    // When true, normal viewer selection/pan/click semantics are retained instead.
    public bool WholeAppOverlayUseWindowedInteractions { get; set; }
    public bool OverlayKeyboardZoom { get; set; } = true;
    public bool OverlayWheelZoom { get; set; } = true;
    public bool OverlaySelectedHighlight { get; set; } = true;
    public int OverlayZoomStepPercent { get; set; } = 10;
    public bool OverlayRememberZoom { get; set; } = true;
    public bool OverlayRightDragPan { get; set; } = true;
    // Developer override: false is the normal adaptive behaviour that preserves relative placement
    // and keeps overlays visible across maximize/fullscreen transitions.
    public bool OverlayAbsoluteCoordinatesOnResize { get; set; }
    public bool OverlayScaleWithWindow { get; set; } = true;
    public string LastOverlayDirectory { get; set; } = string.Empty;
    public string LastProfileDirectory { get; set; } = string.Empty;
    public string OpenFileDefaultDirectory { get; set; } = string.Empty;
    public string OverlayDefaultDirectory { get; set; } = string.Empty;
    public string ProfileDefaultDirectory { get; set; } = string.Empty;

    // Hotkeys
    public Dictionary<string, List<string>> Hotkeys { get; set; } = HotkeyCatalog.CreateDefaultMap();

    // Windows Integration
    public string ExternalProgram1 { get; set; } = string.Empty;
    public string ExternalProgram2 { get; set; } = string.Empty;
    public string ExternalProgram3 { get; set; } = string.Empty;

    // persisted placement
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowWasMaximized { get; set; }

    public GlideSettingsState CloneState() => this with
    {
        Hotkeys = Hotkeys.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        Gestures = Gestures.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
        TitleBarButtons = TitleBarButtonCatalog.Normalize(TitleBarButtons).ToList()
    };

    public bool ContentEquals(GlideSettingsState other)
    {
        var left = CloneState();
        var right = other.CloneState();
        var leftHotkeys = left.Hotkeys;
        var rightHotkeys = right.Hotkeys;
        var leftGestures = left.Gestures;
        var rightGestures = right.Gestures;
        var leftTitleBarButtons = left.TitleBarButtons;
        var rightTitleBarButtons = right.TitleBarButtons;
        // Record equality compares collection properties by object identity. Replace the
        // collection properties on both clones with the SAME sentinels so the record
        // comparison covers only scalar/value content; compare the real collections
        // semantically below. Using separate empty collections here would make two
        // otherwise identical settings states compare unequal.
        var ignoredHotkeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var ignoredGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ignoredTitleBarButtons = new List<string>();
        left.Hotkeys = ignoredHotkeys;
        right.Hotkeys = ignoredHotkeys;
        left.Gestures = ignoredGestures;
        right.Gestures = ignoredGestures;
        left.TitleBarButtons = ignoredTitleBarButtons;
        right.TitleBarButtons = ignoredTitleBarButtons;
        if (!Equals(left, right)) return false;
        if (leftHotkeys.Count != rightHotkeys.Count) return false;
        foreach (var kv in leftHotkeys)
        {
            if (!rightHotkeys.TryGetValue(kv.Key, out var values)) return false;
            if (!kv.Value.SequenceEqual(values, StringComparer.OrdinalIgnoreCase)) return false;
        }
        if (leftGestures.Count != rightGestures.Count) return false;
        foreach (var kv in leftGestures)
        {
            if (!rightGestures.TryGetValue(kv.Key, out var action) || !string.Equals(kv.Value, action, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!leftTitleBarButtons.SequenceEqual(rightTitleBarButtons, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }
}
