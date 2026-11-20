namespace Glide.Core.Settings;

// Stage C (C2): read-only per-feature views of the live settings. Each is a struct over the GlideSettingsState it was
// made from, so it never goes stale (MainWindow builds one per access from its current _settings) and costs nothing.
// A feature reads its slice instead of the whole 300-property bag, which documents what it depends on. Writes still
// go through the state (SettingsService / ChangeSettingAsync). Members keep the state property name minus the
// feature prefix. Add a member when a feature starts reading a new property; tools/count-settings-reads.py measures
// the remaining direct _settings reads.

/// <summary>Read-only view of the settings for next/previous, rate limit, skip hint, sort order, end of folder and sibling-folder traversal.</summary>
public readonly struct NavigationOptions(GlideSettingsState settings)
{
    public string SortOrder => settings.NavigationSortOrder;
    public bool SortDescending => settings.NavigationSortDescending;
    public string EndOfFolderAction => settings.EndOfFolderAction;
    public bool HierarchicalFolderTraversal => settings.HierarchicalFolderTraversal;
    public bool ConfirmHierarchicalFolderTraversal => settings.ConfirmHierarchicalFolderTraversal;
    public string HierarchicalPreviousFolderEntry => settings.HierarchicalPreviousFolderEntry;
    public int RateLimitMs => settings.NavigationRateLimitMs;
    public string RateLimitInput => settings.NavigationRateLimitInput;
    public bool RateLimitShowIndicator => settings.NavigationRateLimitShowIndicator;
    public string RateLimitIndicatorPosition => settings.NavigationRateLimitIndicatorPosition;
    public bool RateLimitWindowed => settings.NavigationRateLimitWindowed;
    public bool RateLimitFullscreen => settings.NavigationRateLimitFullscreen;
    public bool RateLimitSlideshow => settings.NavigationRateLimitSlideshow;
    public bool SkipHint => settings.NavigationSkipHint;
    public bool Counter => settings.ShowFolderCounter;
    public bool FolderCounterIncludeGrandparent => settings.FolderCounterIncludeGrandparent;
    public bool FolderNavShowGroup => settings.FolderNavShowGroup;
    public bool FolderNavShowPrevious => settings.FolderNavShowPrevious;
    public bool FolderNavShowNext => settings.FolderNavShowNext;
    public bool FolderNavShowExplore => settings.FolderNavShowExplore;
    public bool FolderNavSkipEmpty => settings.FolderNavSkipEmpty;
    public bool FolderNavIncludeHidden => settings.FolderNavIncludeHidden;
    public bool FolderNavWrap => settings.FolderNavWrap;
    public bool FolderNavOpenFirstImage => settings.FolderNavOpenFirstImage;
    public string FolderNavOrder => settings.FolderNavOrder;
}

/// <summary>Read-only view of the settings for the slideshow controller's options.</summary>
public readonly struct SlideshowOptions(GlideSettingsState settings)
{
    public int IntervalMs => settings.SlideshowIntervalMs;
    public bool Loop => settings.SlideshowLoop;
    public bool CrossFolders => settings.SlideshowCrossFolders;
    public bool Shuffle => settings.SlideshowShuffle;
    public string Direction => settings.SlideshowDirection;
    public bool StartFullscreen => settings.SlideshowStartFullscreen;
    public bool PauseWhenInactive => settings.SlideshowPauseWhenInactive;
    public bool RightClickStops => settings.SlideshowRightClickStops;
    public string LeftClickAction => settings.SlideshowLeftClickAction;
    public bool ShowQualityIndicator => settings.SlideshowShowQualityIndicator;
}

/// <summary>Read-only view of the settings for Glide's Explorer tab.</summary>
public readonly struct ExplorerOptions(GlideSettingsState settings)
{
    public string OpenImagesWith => settings.ExplorerOpenImagesWith;
    public string ThemeChoice => settings.ExplorerThemeChoice;
    public string DefaultView => settings.ExplorerDefaultView;
    public bool ShowHiddenFiles => settings.ExplorerShowHiddenFiles;
    public bool KeepSelectionInView => settings.ExplorerKeepSelectionInView;
    public bool ClickToRename => settings.ExplorerClickToRename;
    public bool ShowOnlyImages => settings.ExplorerShowOnlyImages;
    public bool AddressBarOverPictures => settings.ShowAddressBarOverPictures;
    public bool InfoDate => settings.ExplorerInfoDate;
    public bool ItemInfoTooltip => settings.ExplorerItemInfoTooltip;
    public bool InfoResolution => settings.ExplorerInfoResolution;
    public bool InfoFileSize => settings.ExplorerInfoFileSize;
    public double ScrollSpeed => settings.ExplorerScrollSpeed;
    public string ScrollMode => settings.ExplorerScrollMode;
    public int ScrollRows => settings.ExplorerScrollRows;
    public int ScrollPixels => settings.ExplorerScrollPixels;
    public string LocateOpensIn => settings.ExplorerLocateOpensIn;
    public string BackReturnsToImage => settings.ExplorerBackReturnsToImage;
    public string AlternativeFileManagerPath => settings.AlternativeFileManagerPath;
    public string AlternativeFileManagerArguments => settings.AlternativeFileManagerArguments;
}

/// <summary>Read-only view of the settings for the status bar (visibility, groups, statistics, size, auto-hide).</summary>
public readonly struct StatusBarOptions(GlideSettingsState settings)
{
    public bool ShowStatusSurface => settings.ShowStatusSurface;
    public bool ShowOnHoverWhenClosed => settings.StatusShowOnHoverWhenClosed;
    public string FitScope => settings.StatusFitScope;
    public bool ShowNavigation => settings.StatusShowNavigation;
    public bool ShowZoom => settings.StatusShowZoom;
    public bool ShowSlideshow => settings.StatusShowSlideshow;
    public bool ShowFit => settings.StatusShowFit;
    public bool ShowInfo => settings.StatusShowInfo;
    public bool ShowOptions => settings.StatusShowOptions;
    public bool ShowClose => settings.StatusShowClose;
    public bool StatIndex => settings.StatusStatIndex;
    public bool StatResolution => settings.StatusStatResolution;
    public bool StatZoom => settings.StatusStatZoom;
    public bool StatFileSize => settings.StatusStatFileSize;
    public bool StatFormat => settings.StatusStatFormat;
    public bool StatFileName => settings.StatusStatFileName;
    public string Size => settings.StatusBarSize;
    public bool CollapsedToCorner => settings.StatusCollapsedToCorner;
    public bool AutoHide => settings.StatusAutoHide;
    public int RevealZoneExpand => settings.StatusRevealZoneExpand;
    public string Alignment => settings.StatusBarAlignment;
    public int GlobalScalePercent => settings.StatusBarGlobalScalePercent;
    public bool AutoFit => settings.StatusBarAutoFit;
    public bool StackInfoLine => settings.StatusStackInfoLine;
    public bool ReserveFitSpace => settings.StatusReserveFitSpace;
    public int MaximizedBoostPercent => settings.StatusBarMaximizedBoostPercent;
    public bool GifControlsOnlyOnGifs => settings.StatusGifControlsOnlyOnGifs;
    public bool FileNameClickActions => settings.StatusFileNameClickActions;
    public string Groups => settings.StatusBarGroups;
}

/// <summary>Read-only view of the settings for the picture counter (text overlay) and overlay windows.</summary>
public readonly struct OverlayOptions(GlideSettingsState settings)
{
    public bool AlwaysOnTop => settings.OverlayAlwaysOnTop;
    public bool PictureCounterEnabled => settings.PictureCounterEnabled;
    public string PictureCounterTemplate => settings.PictureCounterTemplate;
    public int PictureCounterFontSize => settings.PictureCounterFontSize;
    public bool PictureCounterBold => settings.PictureCounterBold;
    public int PictureCounterOpacity => settings.PictureCounterOpacity;
    /// <summary>The position of the card matching the tab layout (<paramref name="sideTabs"/>: tabs at the side).</summary>
    public string PictureCounterPosition(bool sideTabs) => TextOverlayPlacements.Position(settings, sideTabs);
    public string PictureCounterColor => settings.PictureCounterColor;
    public string PictureCounterAlignment(bool sideTabs) => TextOverlayPlacements.Alignment(settings, sideTabs);
    /// <summary>Offset from <see cref="PictureCounterAnchor"/>: non-zero only for the Custom position (presets always reset).</summary>
    public (int X, int Y) PictureCounterOffset(bool sideTabs) => TextOverlayPlacements.Offset(settings, sideTabs);
    /// <summary>The picture position the overlay is anchored at (the preset, or the Custom position's anchor).</summary>
    public string PictureCounterAnchor(bool sideTabs) => TextOverlayPlacements.PictureAnchor(settings, sideTabs);
    public bool PictureCounterShadow => settings.PictureCounterShadow;
    public bool PictureCounterBorder => settings.PictureCounterBorder;
    public int DefaultOpacity => settings.OverlayDefaultOpacity;
    public bool RememberFolder => settings.OverlayRememberFolder;
    public bool PersistLayoutBetweenSessions => settings.OverlayPersistLayoutBetweenSessions;
    public bool KeyboardZoom => settings.OverlayKeyboardZoom;
    public bool WheelZoom => settings.OverlayWheelZoom;
    public bool SelectedHighlight => settings.OverlaySelectedHighlight;
    public int ZoomStepPercent => settings.OverlayZoomStepPercent;
    public bool RememberZoom => settings.OverlayRememberZoom;
    public bool RightDragPan => settings.OverlayRightDragPan;
    public bool AbsoluteCoordinatesOnResize => settings.OverlayAbsoluteCoordinatesOnResize;
    public bool ScaleWithWindow => settings.OverlayScaleWithWindow;
    public string AnimationRegion => settings.OverlayAnimationRegion;
    public int AnimationSpeedDipsPerSecond => settings.OverlayAnimationSpeedDipsPerSecond;
    public int AnimationTurnIntervalMs => settings.OverlayAnimationTurnIntervalMs;
    public int AnimationTurnAngleDegrees => settings.OverlayAnimationTurnAngleDegrees;
    public bool AnimationPauseWhileInteracting => settings.OverlayAnimationPauseWhileInteracting;
    public bool AnimationAvoidOverlap => settings.OverlayAnimationAvoidOverlap;
    public int AnimationRefreshRateHz => settings.OverlayAnimationRefreshRateHz;
    public string DefaultDirectory => settings.OverlayDefaultDirectory;
}
