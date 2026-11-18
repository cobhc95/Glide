using Glide.Core.Commands;

namespace Glide.Core.Settings;

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
    // Settings search: also list matches found only in descriptions, tooltips and search terms (after the title matches).
    public bool SearchShowRelatedResults { get; set; } = true;
    // Order of the images in a folder for next / previous (see Glide.Core.ImageSortOrder). Default matches Windows Explorer.
    public string NavigationSortOrder { get; set; } = "Name (like Explorer)";
    public bool NavigationSortDescending { get; set; }
    public int HistorySize { get; set; } = 8;
    public bool RememberWindowPlacement { get; set; } = true;
    public bool ReuseSingleInstance { get; set; } = true;
    // "When the last image is reached" (5.0.7, navigation.endOfFolder): an EndOfFolderActions name. Replaces the old
    // ContinueSiblingFolders checkbox (schema 15 migrates it: on = next folder, off = do nothing).
    public string EndOfFolderAction { get; set; } = Glide.Core.Navigation.EndOfFolderActions.Default;
    public bool HierarchicalFolderTraversal { get; set; } = true;
    public bool ConfirmHierarchicalFolderTraversal { get; set; } = true;
    // When Previous crosses into a parent/grandparent branch, choose which image opens.
    // "First image" is the user-facing default; "Last image" preserves strict reversal semantics.
    public string HierarchicalPreviousFolderEntry { get; set; } = "First image";
    // Optional navigation-rate limiter. 0 ms means unlimited. 120 ms since 5.0.6 (was 80): laptop keyboards often bounce,
    // so one press could skip several pictures; schema 14 moves an untouched 80 to 120 once.
    public int NavigationRateLimitMs { get; set; } = 120;
    // Mouse only, Keyboard only, or Both.
    public string NavigationRateLimitInput { get; set; } = "Both";
    public bool NavigationRateLimitShowIndicator { get; set; } = true;
    public string NavigationRateLimitIndicatorPosition { get; set; } = "Top left";
    public bool NavigationRateLimitWindowed { get; set; } = true;
    public bool NavigationRateLimitFullscreen { get; set; } = true;
    public bool NavigationRateLimitSlideshow { get; set; } = true;
    // navigation.skipHint (5.0.6): show the "Skipping several pictures at once?" toast when a key press seems to skip
    // pictures (a sticky or bouncing next / previous key). The toast's "Don't remind me again" turns it off.
    public bool NavigationSkipHint { get; set; } = true;
    public bool RememberLastOpenLocation { get; set; } = true;
    // New Explorer tabs have their own navigation-memory scope. Default = last Explorer-tab folder.
    public bool NewExplorerTabsUseLastLocation { get; set; } = true;
    public string NewExplorerTabDefaultDirectory { get; set; } = string.Empty;
    public string LastNewExplorerTabDirectory { get; set; } = string.Empty;
    // Explorer tab (4.8.0): what double-click / Enter does with an image; middle-click does the other.
    public string ExplorerOpenImagesWith { get; set; } = "Double-click: current tab, middle-click: new tab";
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
    // Retired with the Home feature cards (4.7.0); kept so older settings files still deserialize.
    public bool ShowHomeTips { get; set; } = true;
    public bool ShowRecentOnHome { get; set; } = true;
    public bool ConfirmDiscardSettingsChanges { get; set; } = true;

    // Updates (5.0.1). Two real settings (Settings > General & startup > Updates) ...
    /// <summary>Check GitHub once a day for a newer Glide and offer to open its release page. Nothing is ever downloaded or installed.</summary>
    public bool UpdateCheckDaily { get; set; } = true;
    /// <summary>"Don't remind me again": no update prompt (the Settings card still shows an available update).</summary>
    public bool UpdateDontRemind { get; set; }
    // ... and the update checker's own memory (UTC times). These are not settings: Settings never edits them, the Settings change
    // list ignores them, and the live UpdateService overlays its copy whenever the main window saves (like WindowPlacements).
    public DateTime? UpdateLastCheckedUtc { get; set; }
    public DateTime? UpdateLastAttemptUtc { get; set; }
    public bool UpdateLastAttemptFailed { get; set; }
    public string UpdateLatestKnownVersion { get; set; } = string.Empty;
    public string UpdateLatestKnownUrl { get; set; } = string.Empty;
    public DateTime? UpdateLatestKnownPublishedUtc { get; set; }
    public string UpdateLastPromptedVersion { get; set; } = string.Empty;
    public DateTime? UpdateLastPromptedUtc { get; set; }

    /// <summary>Names of the update checker's memory properties (not user settings).</summary>
    public static readonly string[] UpdateStateProperties =
    [
        nameof(UpdateLastCheckedUtc), nameof(UpdateLastAttemptUtc), nameof(UpdateLastAttemptFailed), nameof(UpdateLatestKnownVersion),
        nameof(UpdateLatestKnownUrl), nameof(UpdateLatestKnownPublishedUtc), nameof(UpdateLastPromptedVersion), nameof(UpdateLastPromptedUtc)
    ];

    /// <summary>The update checker's memory as a standalone object.</summary>
    public Glide.Core.Updates.UpdateCheckState ReadUpdateState() => new()
    {
        LastCheckedUtc = UpdateLastCheckedUtc, LastAttemptUtc = UpdateLastAttemptUtc, LastAttemptFailed = UpdateLastAttemptFailed,
        LatestKnownVersion = UpdateLatestKnownVersion ?? string.Empty, LatestKnownUrl = UpdateLatestKnownUrl ?? string.Empty,
        LatestKnownPublishedUtc = UpdateLatestKnownPublishedUtc,
        LastPromptedVersion = UpdateLastPromptedVersion ?? string.Empty, LastPromptedUtc = UpdateLastPromptedUtc
    };

    public void WriteUpdateState(Glide.Core.Updates.UpdateCheckState state)
    {
        UpdateLastCheckedUtc = state.LastCheckedUtc; UpdateLastAttemptUtc = state.LastAttemptUtc; UpdateLastAttemptFailed = state.LastAttemptFailed;
        UpdateLatestKnownVersion = state.LatestKnownVersion ?? string.Empty; UpdateLatestKnownUrl = state.LatestKnownUrl ?? string.Empty;
        UpdateLatestKnownPublishedUtc = state.LatestKnownPublishedUtc;
        UpdateLastPromptedVersion = state.LastPromptedVersion ?? string.Empty; UpdateLastPromptedUtc = state.LastPromptedUtc;
    }

    /// <summary>Makes a loaded settings file's update memory safe: junk versions and absurd URLs are dropped, times are UTC.</summary>
    public void NormalizeUpdateState()
    {
        static DateTime? Utc(DateTime? value) => value is { } v
            ? (v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc))
            : null;
        UpdateLastCheckedUtc = Utc(UpdateLastCheckedUtc);
        UpdateLastAttemptUtc = Utc(UpdateLastAttemptUtc);
        UpdateLatestKnownPublishedUtc = Utc(UpdateLatestKnownPublishedUtc);
        UpdateLastPromptedUtc = Utc(UpdateLastPromptedUtc);
        if (!Glide.Core.Updates.UpdateVersion.TryParse(UpdateLatestKnownVersion, out _))
        {
            UpdateLatestKnownVersion = string.Empty;
            UpdateLatestKnownUrl = string.Empty;
        }
        if ((UpdateLatestKnownUrl ?? string.Empty).Length > 2048) UpdateLatestKnownUrl = string.Empty;
        UpdateLatestKnownUrl ??= string.Empty;
        UpdateLastPromptedVersion = (UpdateLastPromptedVersion ?? string.Empty).Length > 64 ? string.Empty : UpdateLastPromptedVersion ?? string.Empty;
    }

    // Launch/workspace destinations. These are intentionally independent of HomePageMode:
    // HomePageMode controls the Home command/last-tab fallback, while these control process startup/new tabs.
    public string StartupAction { get; set; } = "Welcome tab";
    public string StartupCustomPath { get; set; } = string.Empty;
    public string NewTabAction { get; set; } = "Welcome tab";
    public string NewTabCustomPath { get; set; } = string.Empty;

    // Welcome-page layout is deliberately small and durable rather than storing raw pixels.
    public string WelcomeRecentPosition { get; set; } = "Below tips";
    public string WelcomeRecentSize { get; set; } = "Normal";

    // Interface & Behavior
    public bool ShowStatusSurface { get; set; } = true;
    /// <summary>Reveal a collapsed status bar when the pointer enters the box it would occupy. On by default; files without the key get true.</summary>
    public bool StatusShowOnHoverWhenClosed { get; set; } = true;
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
    // Legacy hidden value from the removed embedded Explorer ("Internal browser" / "Windows Explorer"). It has
    // no UI or effect any more and is only kept so older settings files round-trip; see ShowInExplorerTarget.
    public string NavigateToFolderBehavior { get; set; } = "Windows Explorer";
    // What the title-bar "Show in Explorer" button (file.locate) does: ShowInExplorerTargets.GlideTab
    // (default) or ShowInExplorerTargets.WindowsExplorer. Also switchable from the button's right-click menu.
    public string ShowInExplorerTarget { get; set; } = ShowInExplorerTargets.GlideTab;
    // Explorer tab (4.8.0) view and filter preferences.
    public string ExplorerDefaultView { get; set; } = "Large thumbnails";
    public bool ExplorerShowHiddenFiles { get; set; }
    // 5.0.6: off = changing the sort keeps the scroll position; on = the view scrolls to the selected item.
    public bool ExplorerKeepSelectionInView { get; set; }
    // 5.0.6: click the name of the single selected item (slowly, not a double-click) to rename it, as in Windows Explorer.
    public bool ExplorerClickToRename { get; set; } = true;
    public bool ExplorerShowOnlyImages { get; set; } = true;
    // 5.1.0: the Explorer navigation row (Back ... breadcrumb) also above pictures in picture tabs. Off by default.
    public bool ShowAddressBarOverPictures { get; set; }
    // Round 3 #6: extra info lines under Explorer thumbnails (icon views), each off by default.
    // 5.1.0 Tooltips (Glide.Core.Settings.TooltipAreas): one show delay per area, and the Explorer item info tooltip on/off.
    public int TooltipDelaySettingsMs { get; set; } = 600;
    public int TooltipDelayMainMs { get; set; } = 600;
    public int TooltipDelayOverlayMs { get; set; } = 600;
    public int TooltipDelayExplorerMs { get; set; } = 1500;
    public bool ExplorerItemInfoTooltip { get; set; } = true;
    public bool ExplorerInfoDate { get; set; }
    public bool ExplorerInfoResolution { get; set; }
    public bool ExplorerInfoFileSize { get; set; }
    // Round 3 #12: drive bar (navigation pane) width in pixels; set by dragging the divider, remembered (120-600).
    public int ExplorerNavPaneWidth { get; set; } = 208;
    // Explorer wheel speed multiplier (0.25 - 4). One notch scrolls 1 / 2 / 3 rows by view; this scales it.
    public double ExplorerScrollSpeed { get; set; } = 1.0;
    // Explorer wheel scroll amount mode (Explorer.ExplorerScrollModes) and its fixed rows / pixels values.
    public string ExplorerScrollMode { get; set; } = "One row per notch";
    public int ExplorerScrollRows { get; set; } = 3;
    public int ExplorerScrollPixels { get; set; } = 100;
    // What Back does in an Explorer tab reached from a picture (Glide.Core.Navigation.ExplorerBackReturnModes).
    public string ExplorerLocateOpensIn { get; set; } = Glide.Core.Navigation.ExplorerLocateTargets.Default;
    public string ExplorerBackReturnsToImage { get; set; } = "Folders, then back to the picture";
    // Alternative file manager for the Explorer toolbar's "Show in" button (right / middle click) and its action.
    // Empty path = none. Arguments use {path} for the folder or selected file; empty arguments = "{path}" quoted.
    public string AlternativeFileManagerPath { get; set; } = string.Empty;
    public string AlternativeFileManagerArguments { get; set; } = "\"{path}\"";
    public bool FullscreenStatusAlwaysOn { get; set; } = true;
    public bool FullscreenKeepTabBarOpen { get; set; }
    /// <summary>Fullscreen with the Explorer tab selected keeps the title/tab bar docked above the Explorer navigation row. On by default; files without the key get true.</summary>
    public bool FullscreenKeepTitleBarForExplorer { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    // Overlay mode owns an independent topmost preference; normal-window topmost must not leak into it.
    public bool OverlayAlwaysOnTop { get; set; }
    // Soft uses the ordinary OS topmost band (default); Hard periodically reasserts native HWND_TOPMOST.
    public string AlwaysOnTopMode { get; set; } = "Soft";
    // Per-image Overlay geometry. Keys are normalized full image paths. Kept bounded by MainWindow.
    public Dictionary<string, OverlayWindowPlacementState> OverlayWindowPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AutoCenterWindowOnRestore { get; set; }
    // What F11/Escape/fullscreen toggle should do when leaving fullscreen.
    // Default restores the exact pre-fullscreen normal placement/state captured on entry.
    public string FullscreenExitBehavior { get; set; } = "Restore size and location";
    public string DefaultViewMode { get; set; } = "Fit image";
    // Play animated GIFs in the main viewer (the first frame is always shown immediately).
    public bool AnimateGifs { get; set; } = true;
    // How the status bar Fit Width/Fit Height controls propagate the chosen fit mode: only the
    // current image, every image in this session, or this session plus future launches (default).
    public string StatusFitScope { get; set; } = "This session and future sessions";
    public bool PointerZoom { get; set; } = true;
    public bool PreserveManualZoomOnNavigate { get; set; }
    public int ViewportZoomStepPercent { get; set; } = Glide.Core.ZoomStepPolicy.DefaultStepPercent;
    public bool UpscaleSmallImages { get; set; }
    public bool HighPrecisionSubpixelRendering { get; set; } = true;
    public bool ShowImageLoadingIndicator { get; set; } = true;

    // Backward compatibility aliases for general/windowed
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
    // Selection buffer: a left-press in the empty area around the picture starts a selection
    // (clamped to the image). Each percentage is the share of the empty space on each side of the
    // image that counts as buffer; 100 = the whole empty area. 0 disables the buffer for that state.
    public bool SelectionBufferEnabled { get; set; } = true;
    public int SelectionBufferWindowedPercent { get; set; } = 33;
    public int SelectionBufferMaximizedPercent { get; set; } = 100;
    // When true the maximized percentage applies in every window state, not only when maximized.
    public bool SelectionBufferAllModes { get; set; }
    public bool ReverseSelectionZoomOutScale { get; set; } = true;
    public bool BackgroundDragWindow { get; set; } = true;
    public bool CtrlWheelZoom { get; set; } = true;
    public bool MiddleDragPan { get; set; }

    // Advanced gesture matrix. Every slot is persisted by stable ID and resolves through GestureCatalog.
    // Since 4.8.0 (schema 7) the mouse toggles above that map to a gesture row are folded into this
    // map by a one-time migration; those toggle properties are kept only so older files and profiles
    // can still be read and migrated (GestureLegacyMigration). They no longer drive runtime behaviour.
    public Dictionary<string, string> Gestures { get; set; } = GestureCatalog.CreateDefaultMap();
    // User-defined gestures (area + input + optional held key -> action), at most 6 per area.
    public List<CustomGesture> CustomGestures { get; set; } = new();
    // Most recently picked gesture actions (newest first), shown as "Recent" chips in the action picker.
    public List<string> GestureRecentActions { get; set; } = new();

    // Performance & Startup
    public string InitialImageQuality { get; set; } = "Maximum quality";
    public bool AdaptiveFastPreview { get; set; } = true; // decoder-scaled first useful frame
    public int AdaptivePreviewDelayMs { get; set; } = 5;
    public bool SequentialForegroundReads { get; set; } = true;
    public bool PredictivePrefetch { get; set; } = true;
    public bool BackgroundRefinement { get; set; } = true;
    public bool PurgeCacheOnMinimize { get; set; }
    public bool StartupDiagnostics { get; set; }
    public int PrefetchDepth { get; set; } = 15;
    public bool PrefetchSiblingFolders { get; set; } = true;
    public int PrefetchSiblingFolderImageCount { get; set; } = 7;
    public int CacheItems { get; set; } = 20;
    public int CompressedCacheMegabytes { get; set; } = 999;
    public int DecodedCacheMegabytes { get; set; } = 999;
    public int RapidPreviewLongestSide { get; set; } = 2400;
    // Reduced-resolution frame used specifically while rapid keyboard/wheel browsing is active.
    // Presets stage sensible defaults, but the user may override this independently afterwards.
    public int RapidBrowsePreviewLongestSide { get; set; } = 3500;
    public bool ProgressiveColorFirstPreview { get; set; } = true;
    // "Fast", "Balanced", or "Full quality". The stock Balanced performance preset intentionally
    // uses Full quality while dragging/panning; users can still override this independently.
    public string InteractivePanQuality { get; set; } = "Full quality";
    public bool SpeedBoostEnabled { get; set; } = true;
    public bool StartWithWindowsInBackground { get; set; }
    // Legacy master switch (no UI). False is honoured as "Never" by SpeedBoostTrayIcon.
    public bool ShowTrayIcon { get; set; } = true;
    // "Always", "Only while Glide is closed" or "Never". Speed Boost standby works without the icon:
    // launching Glide again wakes the warm instance.
    public string SpeedBoostTrayIcon { get; set; } = "Always";
    public bool PerformanceUserCustom { get; set; }
    public bool PerformanceUserCustomSaved { get; set; }
    public string UserCustomPanQuality { get; set; } = "Full quality";
    public bool UserCustomAdaptivePreview { get; set; } = true;
    public int UserCustomRefinementDelayMs { get; set; } = 20;
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
    // File name (or full path when "Show full path in title" is on) as the first status segment.
    public bool StatusStatFileName { get; set; } = true;
    // Current five-step status-size ladder preserves the visual mapping of older saved values during migration.
    public string StatusBarSize { get; set; } = "Small";
    /// <summary>Where the floating status bar sits: Center (default), Left or Right.</summary>
    /// <summary>When collapsed, park the expand button in the lower-right corner, away from the image.</summary>
    public bool StatusCollapsedToCorner { get; set; } = true;
    /// <summary>Windows-taskbar style auto-hide: the status bar is fully hidden until the pointer reaches the bottom edge.</summary>
    public bool StatusAutoHide { get; set; }
    /// <summary>Adjustment in DIP (-40..300, default 0 = exactly the box the status bar occupies) of the invisible area that reveals a collapsed or auto-hidden status bar.</summary>
    public int StatusRevealZoneExpand { get; set; } = Glide.Core.StatusBarAutoHide.DefaultRevealZoneExpand;
    public string StatusBarAlignment { get; set; } = "Center";
    public int StatusBarGlobalScalePercent { get; set; } = 100;
    public bool StatusBarAutoFit { get; set; } = true;
    /// <summary>On (default): the file information always sits on its own line above the button groups, even when everything would fit on one line.</summary>
    public bool StatusStackInfoLine { get; set; } = true;
    /// <summary>Fit modes size the image to the area above the visible status bar instead of under it.</summary>
    public bool StatusReserveFitSpace { get; set; }
    public int StatusBarMaximizedBoostPercent { get; set; } = 15;
    /// <summary>Off (default) = the GIF controls always show, greyed out on non-GIF images; on = only while an animated GIF is open (pre-5.0.3).</summary>
    public bool StatusGifControlsOnlyOnGifs { get; set; }
    /// <summary>single-click the status-bar file name renames the file, double-click opens its location.</summary>
    public bool StatusFileNameClickActions { get; set; }
    /// <summary>ordered, comma-separated status-bar group ids that are shown (see StatusBarGroupCatalog). Empty = default.</summary>
    public string StatusBarGroups { get; set; } = string.Empty;

    // Slideshow
    public int SlideshowIntervalMs { get; set; } = 3000;
    public bool SlideshowLoop { get; set; } = true;
    public bool SlideshowCrossFolders { get; set; } = true;
    public bool SlideshowShuffle { get; set; }
    public string SlideshowDirection { get; set; } = "Forward";
    public bool SlideshowStartFullscreen { get; set; }
    public bool SlideshowPauseWhenInactive { get; set; } = true;
    public bool SlideshowRightClickStops { get; set; } = true;
    // What a left-click on the picture does while a slideshow is active (see SlideshowLeftClickActions).
    public string SlideshowLeftClickAction { get; set; } = "Pause / resume slideshow";
    public bool SlideshowShowQualityIndicator { get; set; } = true;

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
    public bool MiddleClickTabCloses { get; set; } = true;
    // Filename size in the tab tooltip: "Small", "Medium" (default) or "Large".
    public string TabToolTipTextSize { get; set; } = "Medium";
    /// <summary>where the tab strip sits: "Top" (default) or "Side" (vertical tab panel on the left).</summary>
    public string TabStripPosition { get; set; } = "Top";
    /// <summary>the side tab panel hides until the pointer reaches the window's left edge.</summary>
    public bool TabSideAutoHide { get; set; }
    /// <summary>width (DIP) of the vertical tab pane, set by dragging its right edge (150-480, default 220).</summary>
    public int TabSidePanelWidth { get; set; } = 220;
    /// <summary>the top tab strip hides until the pointer reaches the top edge.</summary>
    public bool TabTopAutoHide { get; set; }
    // "Tab order" cycles left-to-right; "Most recently used" cycles like Alt+Tab while Ctrl is held.
    public string TabCycleOrder { get; set; } = "Tab order";
    // "Open home page", "Keep last tab open", or "Close program". Applies when the user closes the final ordinary tab.
    public string LastTabCloseBehavior { get; set; } = "Open home page";
    // 5.0.6: tabs opened "in a new tab" from inside Glide (Glide.Core.Workspace.NewTabPolicy). SwitchToNewTabs is
    // "Open new tabs in the [foreground / background]"; NewTabPositionKey / NewTabForegroundKey flip them per open.
    public string NewTabPosition { get; set; } = Glide.Core.Workspace.NewTabPositions.Default;
    public bool SwitchToNewTabs { get; set; } = true; // on by default since 5.0.6 (schema 13 migration in SettingsStore)
    // Round 3 #18: the key held to open one new tab at the other position / on the other side (NewTabKeys; None = off).
    // They replaced the "Shift and Alt change how a new tab opens" checkbox (NewTabModifiers), migrated in SettingsStore.
    public string NewTabPositionKey { get; set; } = Glide.Core.Workspace.NewTabKeys.Default;
    public string NewTabForegroundKey { get; set; } = Glide.Core.Workspace.NewTabKeys.Default;
    // 5.1.0: Explorer middle-click has its own side and position (MiddleClickSides / MiddleClickPositions).
    public string MiddleClickNewTabSide { get; set; } = Glide.Core.Workspace.MiddleClickSides.Default;
    public string MiddleClickNewTabPosition { get; set; } = Glide.Core.Workspace.MiddleClickPositions.Default;
    // 5.1.0: the new tab shown after closing a tab when "After closing a tab, activate" is Custom… (TabCloseCustomTargets).
    public string AfterTabCloseCustomTarget { get; set; } = Glide.Core.Workspace.TabCloseCustomTargets.Default;
    // 5.0.6: which tab becomes active after the active tab is closed (Glide.Core.Workspace.TabCloseActivation).
    public string AfterTabCloseActivate { get; set; } = Glide.Core.Workspace.TabCloseActivation.Default;
    // 5.0.6: hovering a background tab shows a small preview of its picture / folder in the tab tooltip.
    public bool TabHoverPreviews { get; set; } = true;
    // 5.0.6: an animated GIF tab's hover preview plays while the tooltip is open (Services/TabGifPreview). Off by default.
    public bool AnimateGifPreviews { get; set; }
    // Round 3 #5: tab hover preview size in percent (TabPreviewSize.Min..Max), and the key that with the mouse wheel over
    // the tab resizes an open preview ("Ctrl", "Shift", "Alt" or "None" = off).
    public int TabPreviewSizePercent { get; set; } = Glide.Core.Workspace.TabPreviewSize.Default;
    public string TabPreviewResizeModifier { get; set; } = Glide.Core.Workspace.TabPreviewSize.DefaultModifier;
    // "Welcome page" or "Recent pictures page". Legacy Browser page values are migrated to Welcome.
    public string HomePageMode { get; set; } = "Welcome page";
    public bool ShowFolderCounter { get; set; }
    public bool FolderCounterIncludeGrandparent { get; set; }
    public bool FolderNavShowGroup { get; set; } = true;
    public bool FolderNavShowPrevious { get; set; } = true;
    public bool FolderNavShowNext { get; set; } = true;
    public bool FolderNavShowExplore { get; set; } = true;
    public bool FolderNavSkipEmpty { get; set; } = true;
    public bool FolderNavIncludeHidden { get; set; }
    public bool FolderNavWrap { get; set; }
    public bool FolderNavOpenFirstImage { get; set; } = true;
    // Folder ordering is deliberately alphabetical by default. Date modes are oldest first,
    // with deterministic name/path tie-breakers in the traversal service.
    public string FolderNavOrder { get; set; } = "Alphabetical";

    // Text overlay template (legacy property names retained for profile/settings compatibility; distinct from Window-in-Window image overlays)
    public bool PictureCounterEnabled { get; set; } = true;
    public string PictureCounterTemplate { get; set; } = "[{index}/{total}]";
    public int PictureCounterFontSize { get; set; } = 13;
    public bool PictureCounterBold { get; set; } = true;
    public int PictureCounterOpacity { get; set; } = 85;
    public string PictureCounterPosition { get; set; } = "Top right";
    public string PictureCounterColor { get; set; } = "White";
    public bool PictureCounterShadow { get; set; } = true;
    /// <summary>Encloses every line of the text overlay in one box styled like the Image information panel (5.0.6).</summary>
    public bool PictureCounterBorder { get; set; }

    // Window in Window image overlays
    public int OverlayDefaultOpacity { get; set; } = 100;
    public bool OverlayRememberFolder { get; set; } = true;
    public bool OverlayPersistLayoutBetweenSessions { get; set; }
    // Whole-application frameless/transparent presentation is opt-in. Normal Glide remains the default.
    public bool AlwaysStartWholeAppOverlayMode { get; set; }
    // Whole-app Overlay defaults to Window-in-Window direct manipulation: left-drag moves the window.
    // When true, normal viewer selection/pan/click semantics are retained instead.
    public bool WholeAppOverlayUseWindowedInteractions { get; set; }
    // Round 3 #20: optional thin drag strip along the Overlay's top edge, and a lock that stops drag-moving the window.
    public bool WholeAppOverlayShowDragStrip { get; set; }
    public bool WholeAppOverlayLockPosition { get; set; }
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
    public string OverlayAnimationRegion { get; set; } = "Anywhere";
    public int OverlayAnimationSpeedDipsPerSecond { get; set; } = 90;
    public int OverlayAnimationTurnIntervalMs { get; set; } = 2500;
    public int OverlayAnimationTurnAngleDegrees { get; set; } = 120;
    public bool OverlayAnimationPauseWhileInteracting { get; set; } = true;
    public bool OverlayAnimationAvoidOverlap { get; set; } = true;
    public int OverlayAnimationRefreshRateHz { get; set; } = 144;
    public string LastOverlayDirectory { get; set; } = string.Empty;
    public string LastProfileDirectory { get; set; } = string.Empty;
    public string OpenFileDefaultDirectory { get; set; } = string.Empty;
    public string OverlayDefaultDirectory { get; set; } = string.Empty;
    public string ProfileDefaultDirectory { get; set; } = string.Empty;

    // Hotkeys
    public Dictionary<string, List<string>> Hotkeys { get; set; } = HotkeyCatalog.CreateDefaultMap();
    /// <summary>User-saved keyboard mappings, offered next to the built-in presets. Older files simply have none.</summary>
    public List<CustomHotkeyPreset> CustomHotkeyPresets { get; set; } = new();

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

    // Settings window placement: remembered when the Settings window closes and restored when it opens.
    // Position is in device pixels, size in DIPs (the size of the window when not maximized); a width or
    // height of 0 means nothing has been saved yet and the compact default size is used.
    public int SettingsWindowX { get; set; } = int.MinValue;
    public int SettingsWindowY { get; set; } = int.MinValue;
    public double SettingsWindowWidth { get; set; }
    public double SettingsWindowHeight { get; set; }
    public bool SettingsWindowWasMaximized { get; set; }
    /// <summary>Every Settings page opens with all its sections expanded (off by default).</summary>
    public bool SettingsAlwaysExpand { get; set; }

    // Every other window's remembered placement (Print, Settings, dialogs ...), keyed by a stable window id
    // (see Glide.App.Services.WindowPlacementCatalog). Older files simply have none; Settings also falls back to the
    // SettingsWindow* properties above. Owned at runtime by WindowPlacementService, which overlays its live copy
    // whenever the main window saves settings.
    public Dictionary<string, WindowPlacementState> WindowPlacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public GlideSettingsState CloneState() => this with
    {
        WindowPlacements = ClonePlacements(WindowPlacements),
        Hotkeys = Hotkeys.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        Gestures = Gestures.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
        CustomGestures = (CustomGestures ?? new()).Where(x => x is not null).Select(x => x.Clone()).ToList(),
        CustomHotkeyPresets = (CustomHotkeyPresets ?? new()).Where(x => x is not null).Select(x => x.Clone()).ToList(),
        GestureRecentActions = (GestureRecentActions ?? new()).ToList(),
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
        var leftCustom = left.CustomGestures;
        var rightCustom = right.CustomGestures;
        var leftRecent = left.GestureRecentActions;
        var rightRecent = right.GestureRecentActions;
        var leftPresets = left.CustomHotkeyPresets;
        var rightPresets = right.CustomHotkeyPresets;
        var ignoredPresets = new List<CustomHotkeyPreset>();
        left.CustomHotkeyPresets = ignoredPresets; right.CustomHotkeyPresets = ignoredPresets;
        var ignoredCustom = new List<CustomGesture>();
        var ignoredRecent = new List<string>();
        left.CustomGestures = ignoredCustom; right.CustomGestures = ignoredCustom;
        left.GestureRecentActions = ignoredRecent; right.GestureRecentActions = ignoredRecent;
        var leftPlacements = left.WindowPlacements;
        var rightPlacements = right.WindowPlacements;
        var ignoredPlacements = new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        left.WindowPlacements = ignoredPlacements; right.WindowPlacements = ignoredPlacements;
        // The main window rebuilds this runtime-owned store on every applied preview (SettingsDefaults.KeepRuntimeOwnedValues),
        // so it must compare by value: by reference every later text overlay preview looked like an unrelated change and
        // re-applied every setting (5.0.6).
        var leftOverlayPlacements = left.OverlayWindowPlacements;
        var rightOverlayPlacements = right.OverlayWindowPlacements;
        var ignoredOverlayPlacements = new Dictionary<string, OverlayWindowPlacementState>(StringComparer.OrdinalIgnoreCase);
        left.OverlayWindowPlacements = ignoredOverlayPlacements; right.OverlayWindowPlacements = ignoredOverlayPlacements;
        // The update checker's memory (last check time...) is bookkeeping, not a setting: it never makes two states "different".
        var noUpdateMemory = new Glide.Core.Updates.UpdateCheckState();
        left.WriteUpdateState(noUpdateMemory); right.WriteUpdateState(noUpdateMemory);
        if (!Equals(left, right)) return false;
        if (!WindowPlacementsEqual(leftPlacements, rightPlacements)) return false;
        if (!OverlayWindowPlacementsEqual(leftOverlayPlacements, rightOverlayPlacements)) return false;
        if (!CustomGesturesEqual(leftCustom, rightCustom)) return false;
        if (!CustomHotkeyPresetsEqual(leftPresets, rightPresets)) return false;
        if (!leftRecent.SequenceEqual(rightRecent, StringComparer.OrdinalIgnoreCase)) return false;
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

    public static Dictionary<string, WindowPlacementState> ClonePlacements(Dictionary<string, WindowPlacementState>? source) =>
        (source ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Semantic equality of two per-window placement stores (same keys, case-insensitive, and the same geometry; the last-used stamp is ignored).</summary>
    public static bool WindowPlacementsEqual(Dictionary<string, WindowPlacementState>? a, Dictionary<string, WindowPlacementState>? b)
    {
        a ??= new(); b ??= new();
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            var other = b.FirstOrDefault(x => string.Equals(x.Key, kv.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (other is null || kv.Value is null || !kv.Value.SameGeometry(other)) return false;
        }
        return true;
    }

    /// <summary>Same keys (case-insensitive) with equal records.</summary>
    public static bool OverlayWindowPlacementsEqual(Dictionary<string, OverlayWindowPlacementState>? a, Dictionary<string, OverlayWindowPlacementState>? b)
    {
        a ??= new(); b ??= new();
        if (a.Count != b.Count) return false;
        var lookup = new Dictionary<string, OverlayWindowPlacementState>(b, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in a)
            if (!lookup.TryGetValue(kv.Key, out var other) || !Equals(kv.Value, other)) return false;
        return true;
    }

    public static bool CustomHotkeyPresetsEqual(IReadOnlyList<CustomHotkeyPreset> a, IReadOnlyList<CustomHotkeyPreset> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
            var left = a[i].Hotkeys ?? new();
            var right = b[i].Hotkeys ?? new();
            if (left.Count != right.Count) return false;
            foreach (var pair in left)
                if (!right.TryGetValue(pair.Key, out var values) || !pair.Value.SequenceEqual(values, StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static bool CustomGesturesEqual(IReadOnlyList<CustomGesture> a, IReadOnlyList<CustomGesture> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Area, b[i].Area, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a[i].Input, b[i].Input, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a[i].Modifier, b[i].Modifier, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a[i].Action, b[i].Action, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>The folded 4.7 mouse toggles as stored in this state (migration input).</summary>
    public LegacyMouseToggles ToLegacyMouseToggles() => new()
    {
        DoubleClickFullscreen = DoubleClickFullscreen,
        DoubleClickExitFullscreen = DoubleClickExitFullscreen,
        FullscreenClickNavigation = FullscreenClickNavigation,
        WindowedWheelZoom = WindowedWheelZoom,
        CtrlWheelZoom = CtrlWheelZoom,
        LeftDragMode = LeftDragMode,
        SelectionClickZoom = SelectionClickZoom,
        SelectionRightClickZoomOut = SelectionRightClickZoomOut,
        MiddleDragPan = MiddleDragPan,
        OverlayWheelZoom = OverlayWheelZoom,
        OverlayRightDragPan = OverlayRightDragPan,
        SlideshowLeftClickAction = SlideshowLeftClickAction,
        SlideshowRightClickStops = SlideshowRightClickStops,
        DoubleClickTabCloses = DoubleClickTabCloses,
        MiddleClickTabCloses = MiddleClickTabCloses,
        ExplorerOpenImagesWith = ExplorerOpenImagesWith
    };

    /// <summary>
    /// Converts the folded toggles into explicit gesture bindings and then resets them to their stock
    /// values, so they are inert afterwards (runtime reads only the gesture map).
    /// </summary>
    public void FoldLegacyMouseToggles()
    {
        Gestures ??= GestureCatalog.CreateDefaultMap();
        GestureLegacyMigration.Apply(Gestures, ToLegacyMouseToggles());
        var d = new GlideSettingsState();
        DoubleClickFullscreen = d.DoubleClickFullscreen;
        DoubleClickExitFullscreen = d.DoubleClickExitFullscreen;
        FullscreenClickNavigation = d.FullscreenClickNavigation;
        WindowedWheelZoom = d.WindowedWheelZoom;
        CtrlWheelZoom = d.CtrlWheelZoom;
        LeftDragMode = d.LeftDragMode;
        SelectionClickZoom = d.SelectionClickZoom;
        SelectionRightClickZoomOut = d.SelectionRightClickZoomOut;
        MiddleDragPan = d.MiddleDragPan;
        OverlayWheelZoom = d.OverlayWheelZoom;
        OverlayRightDragPan = d.OverlayRightDragPan;
        SlideshowLeftClickAction = d.SlideshowLeftClickAction;
        SlideshowRightClickStops = d.SlideshowRightClickStops;
        DoubleClickTabCloses = d.DoubleClickTabCloses;
        MiddleClickTabCloses = d.MiddleClickTabCloses;
        ExplorerOpenImagesWith = d.ExplorerOpenImagesWith;
    }
}

/// <summary>
/// One remembered window geometry in <see cref="GlideSettingsState.WindowPlacements"/>. Position is in device pixels
/// (outer top-left; <see cref="int.MinValue"/> = none), size in DIPs (client size when not maximized; 0 = none, which is
/// what position-only dialogs store).
/// </summary>
public sealed record WindowPlacementState
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public long LastUsedUtcTicks { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSize => Width > 0 && Height > 0 && !double.IsNaN(Width) && !double.IsNaN(Height);

    public bool SameGeometry(WindowPlacementState other) =>
        X == other.X && Y == other.Y && Width.Equals(other.Width) && Height.Equals(other.Height) && Maximized == other.Maximized;
}

public sealed record OverlayWindowPlacementState
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public long LastUsedUtcTicks { get; set; }
}

/// <summary>Values of <see cref="GlideSettingsState.ShowInExplorerTarget"/> (also the Settings combo item texts).</summary>
public static class ShowInExplorerTargets
{
    public const string GlideTab = "Glide's Explorer tab";
    public const string WindowsExplorer = "Windows File Explorer";
    /// <summary>The program set in explorer.altFileManagerPath (offered in the button's menu only once one is set).</summary>
    public const string Alternative = "Alternative file manager";

    /// <summary>
    /// Maps any stored value to one of the two choices. Unknown / empty values and the legacy hidden
    /// "Windows Explorer" default (which every pre-4.8 file carried without the user ever choosing it)
    /// become the new default; "Internal browser" meant Glide's own browser; only the explicit new
    /// "Windows File Explorer" value keeps Windows File Explorer.
    /// </summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), WindowsExplorer, StringComparison.OrdinalIgnoreCase) ? WindowsExplorer
        : string.Equals(value?.Trim(), Alternative, StringComparison.OrdinalIgnoreCase) ? Alternative : GlideTab;

    public static bool IsAlternative(string? value) => Normalize(value) == Alternative;

    public static bool IsWindowsExplorer(string? value) => Normalize(value) == WindowsExplorer;
}
