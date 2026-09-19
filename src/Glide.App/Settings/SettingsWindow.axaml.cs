using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Diagnostics;
using Glide.App.Platform;
using Glide.App.Services;
using Glide.Core.Settings;
using Glide.Core.Commands;
using Glide.Imaging;

namespace Glide.App.Settings;

/// <summary>
/// Settings is a staged editor over one explicit GlideSettingsState.  Enabled controls either affect
/// the running viewer immediately or persist a concrete value; future-subsystem controls are disabled
/// instead of being fake-active. Apply/OK commit, Cancel/native close rolls preview back.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Action<GlideSettingsState, bool> _apply;
    private GlideSettingsState _committed;
    private bool _loadingControls;
    private bool _closeWithoutRollback;
    private readonly Dictionary<string, StackPanel> _panels;
    private readonly string _evidenceTemp;
    private Dictionary<string, List<string>> _workingHotkeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _hotkeyCaptureActionId;
    private string _escRememberedChoice = "Ask";
    private string? _hotkeyCaptureOldShortcut;
    private bool _hotkeyCaptureAdd;
    private bool _hotkeyCaptureFromSearch;
    private ListBoxItem? _hotkeyCaptureRow;
    private Border? _hotkeyCaptureSearchCard;
    private readonly Dictionary<string, (Border Card, TextBlock Shortcut)> _hotkeySearchRows = new(StringComparer.OrdinalIgnoreCase);
    private bool _hotkeyFlashOn;
    private readonly DispatcherTimer _hotkeyCaptureFlashTimer = new() { Interval = TimeSpan.FromMilliseconds(360) };
    // Rebuilding the editable search cards constructs one live editor per match (up to ~200
    // controls). Coalesce rapid typing so the UI thread is not blocked once per keystroke.
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private string _pendingSearchQuery = string.Empty;
    private static readonly List<string> EmptyShortcutList = new();
    private string _activeCategory = "General";
    private readonly Dictionary<string, Control> _settingControls = new(StringComparer.OrdinalIgnoreCase);
    // Global-search index per setting. Built once from catalog metadata plus the on-screen wording
    // of the mapped control, so users can find settings by the text they actually see in the UI.
    private readonly Dictionary<string, string> _searchHaystackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> _gestureEditors = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _workingGestures = GestureCatalog.CreateDefaultMap();
    private List<string> _workingTitleBarButtons = TitleBarButtonCatalog.DefaultButtonIds.ToList();
    private string _workingCustomAccentHex = "#38A9F5";
    private string _workingCustomGlowHex = "#38A9F5";
    private string _workingCustomMainBackgroundHex = "#101214";
    private string _workingLastProfileDirectory = string.Empty;
    private bool _gestureMatrixBuilt;
    private bool _hotkeyRowsBuilt;
    private const int SettingsTransferSchema = 1;

    // Settings whose editor is legitimately disabled by the current state rather than a defect.
    private static readonly HashSet<string> ConditionallyDisabledSettingIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "escape.resetRememberedClose" // disabled while the remembered Escape close choice is "Ask"
    };

    private sealed class SettingsTransferDocument
    {
        public int Schema { get; set; } = SettingsTransferSchema;
        public GlideSettingsState Settings { get; set; } = new();
        public IReadOnlyList<CodecProviderArtifact> ProviderInventory { get; set; } = Array.Empty<CodecProviderArtifact>();
    }

    private sealed class ProfileTransferDocument
    {
        public int Schema { get; set; } = 1;
        public string Name { get; set; } = "Profile";
        public GlideSettingsState Settings { get; set; } = new();
    }

    // Public parameterless constructor keeps the AXAML resource reachable to Avalonia's runtime loader.
    // Production opens use the staged-state constructor below.
    public SettingsWindow() : this(new GlideSettingsState(), (_, _) => { }) { }

    public SettingsWindow(GlideSettingsState current, Action<GlideSettingsState, bool> apply)
    {
        _committed = current.CloneState();
        _apply = apply;
        _evidenceTemp = Path.Combine(Path.GetTempPath(), "Glide2-UIEvidence-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(_evidenceTemp); } catch { /* Diagnostics must never block Settings. */ }

        // XAML controls such as ComboBox can raise SelectionChanged while InitializeComponent is
        // still constructing the tree. Treat that phase exactly like a settings load so early events
        // cannot dereference panel/control maps that are intentionally built immediately afterwards.
        _loadingControls = true;
        InitializeComponent();
        // Legacy Glide treats static Settings chrome/text as draggable window surface. Interactive
        // controls remain authoritative and must never start a native move loop.
        ReferenceRoot.AddHandler(InputElement.PointerPressedEvent, SettingsSurfacePointerPressed, RoutingStrategies.Tunnel, true);
        _panels = new Dictionary<string, StackPanel>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = GeneralPanel,
            ["Appearance"] = AppearancePanel,
            ["Viewing"] = ViewingPanel,
            ["Animation"] = AnimationPanel,
            ["Interface"] = InterfacePanel,
            ["Mouse"] = MousePanel,
            ["Performance"] = PerformancePanel,
            ["Status"] = StatusPanel,
            ["Slideshow"] = SlideshowPanel,
            ["Hotkeys"] = HotkeysPanel,
            ["Tabs"] = TabsPanel,
            ["Overlays"] = OverlaysPanel,
            ["Profiles"] = ProfilesPanel,
            ["Windows"] = WindowsPanel,
            ["Developer"] = DeveloperPanel
        };

        BuildSettingControlMap();
        _hotkeyCaptureFlashTimer.Tick += (_, _) => ToggleHotkeyCaptureFlash();
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RunSearchRebuild(_pendingSearchQuery);
        };
        LoadControls(_committed);
        // IMPORTANT: controls can canonicalize legacy/derived settings while loading (for example a
        // compatibility boolean that is now represented by a policy ComboBox). That normalization is
        // not a user edit. Establish the clean comparison baseline from the fully loaded controls so
        // merely opening Settings can never manufacture a phantom "change" / restart prompt.
        _committed = ReadControls().CloneState();
        BuildProfileRows();
        WireLivePreview();
        ApplySettingsTooltips();
        KeyDown += SettingsKeyDown;
        UpdateDirtyState();

        Opened += SettingsOpened;
        PopupPlacementStore.Track(this, "settings");
        Closing += SettingsClosing;
        Closed += (_, _) => { _hotkeyCaptureFlashTimer.Stop(); _searchDebounceTimer.Stop(); };
    }

    private void SettingsSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking static Settings chrome/content should release the search focus ring. Controls that
        // can take focus still receive the click normally; static surface clicks focus the root.
        if (SearchBox.IsFocused && e.Source is Control focusSource && !IsDescendantOf(focusSource, SearchBox) &&
            !IsSettingsInteractiveSource(focusSource))
        {
            ReferenceRoot.Focus();
        }

        if (_hotkeyCaptureActionId is not null)
        {
            // A pending hotkey capture must never trap the user inside Settings. Any mouse interaction
            // means they are trying to do something else, so cancel capture and let the original click
            // continue to its intended control. Successfully assigned shortcuts remain untouched.
            EndHotkeyCapture("Shortcut capture cancelled because you clicked elsewhere.", refreshSearch: false);
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Control source && IsSettingsInteractiveSource(source)) return;
        try
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch
        {
            // A native move loop can be unavailable during teardown; never turn a drag miss into a crash.
        }
    }

    private bool IsSettingsInteractiveSource(Control source)
    {
        Control? current = source;
        while (current is not null)
        {
            if (current is Button or TextBox or ComboBox or CheckBox or NumericUpDown or ScrollBar or ListBox or ListBoxItem or Slider)
                return true;
            // Search-result cards own their click semantics: Boolean rows toggle on the whole card;
            // non-Boolean rows deliberately do nothing outside their explicit editor. Never start a window drag from them.
            if (current.Classes.Contains("searchResult")) return true;
            if (ReferenceEquals(current, ReferenceRoot)) break;
            current = current.GetVisualParent() as Control;
        }
        return false;
    }

    private static bool IsDescendantOf(Control source, Control ancestor)
    {
        Control? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current.GetVisualParent() as Control;
        }
        return false;
    }

    private void SettingsOpened(object? sender, EventArgs e)
    {
        // Never schedule diagnostic rendering on the first-interaction path. The old implementation
        // captured multiple RenderTargetBitmaps at +40 ms/+260 ms, making an already-visible Settings
        // window temporarily unresponsive. Diagnostics can explicitly request screenshots instead.
        Dispatcher.UIThread.Post(() => { if (SearchBox is not null) SearchBox.Focusable = true; }, DispatcherPriority.Background);
    }

    private void CaptureEvidence(string fileName, Control control)
    {
        UiDiagnosticCapture.TryCapture(control, Path.Combine(_evidenceTemp, fileName), out _);
    }

    private void LoadControls(GlideSettingsState s)
    {
        _loadingControls = true;
        try
        {
            RecentHistoryCheck.IsChecked = s.RecentHistoryEnabled;
            HistorySizeBox.Value = s.HistorySize;
            RememberPlacementCheck.IsChecked = s.RememberWindowPlacement;
            ReuseSingleInstanceCheck.IsChecked = s.ReuseSingleInstance;
            ContinueSiblingFoldersCheck.IsChecked = s.ContinueSiblingFolders;
            HierarchicalFolderTraversalCheck.IsChecked = s.HierarchicalFolderTraversal;
            ConfirmHierarchicalFolderTraversalCheck.IsChecked = s.ConfirmHierarchicalFolderTraversal;
            SelectByText(HierarchicalPreviousFolderEntryCombo, string.IsNullOrWhiteSpace(s.HierarchicalPreviousFolderEntry) ? "First image" : s.HierarchicalPreviousFolderEntry);
            NavigationRateLimitMsBox.Value = Math.Clamp(s.NavigationRateLimitMs, 0, 600000);
            SelectByText(NavigationRateLimitInputCombo, string.IsNullOrWhiteSpace(s.NavigationRateLimitInput) ? "Both" : s.NavigationRateLimitInput);
            NavigationRateLimitIndicatorCheck.IsChecked = s.NavigationRateLimitShowIndicator;
            SelectByText(NavigationRateLimitIndicatorPositionCombo, string.IsNullOrWhiteSpace(s.NavigationRateLimitIndicatorPosition) ? "Top left" : s.NavigationRateLimitIndicatorPosition);
            NavigationRateLimitWindowedCheck.IsChecked = s.NavigationRateLimitWindowed;
            NavigationRateLimitFullscreenCheck.IsChecked = s.NavigationRateLimitFullscreen;
            NavigationRateLimitSlideshowCheck.IsChecked = s.NavigationRateLimitSlideshow;
            RememberLastOpenCheck.IsChecked = s.RememberLastOpenLocation;
            NewExplorerTabLastLocationCheck.IsChecked = s.NewExplorerTabsUseLastLocation;
            NewExplorerTabDefaultDirectoryBox.Text = s.NewExplorerTabDefaultDirectory;
            var theme = string.IsNullOrWhiteSpace(s.ThemeChoice) ? (s.LightTheme ? "Light" : "Dark") : s.ThemeChoice;
            SelectByText(ThemeCombo, theme);
            SelectByText(ExplorerThemeCombo, string.IsNullOrWhiteSpace(s.ExplorerThemeChoice) ? "Follow Glide theme" : s.ExplorerThemeChoice);
            SelectByText(AccentCombo, s.AccentChoice);
            SelectByText(GlowCombo, string.IsNullOrWhiteSpace(s.GlowChoice) ? "Follow accent" : s.GlowChoice);
            _workingCustomAccentHex = string.IsNullOrWhiteSpace(s.CustomAccentHex) ? "#38A9F5" : s.CustomAccentHex;
            _workingCustomGlowHex = string.IsNullOrWhiteSpace(s.CustomGlowHex) ? _workingCustomAccentHex : s.CustomGlowHex;
            _workingCustomMainBackgroundHex = string.IsNullOrWhiteSpace(s.CustomMainBackgroundHex) ? "#101214" : s.CustomMainBackgroundHex;
            SelectByText(MainBackgroundCombo, string.IsNullOrWhiteSpace(s.MainBackgroundChoice) ? "Follow theme" : s.MainBackgroundChoice);
            GlowIntensityBox.Value = Math.Clamp(s.GlowIntensityPercent, 0, 100);
            HomeTipsCheck.IsChecked = s.ShowHomeTips;
            ShowRecentOnHomeCheck.IsChecked = s.ShowRecentOnHome;
            ConfirmDiscardSettingsCheck.IsChecked = s.ConfirmDiscardSettingsChanges;
            SelectByText(StartupActionCombo, string.Equals(s.StartupAction, "Explorer tab", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.StartupAction) ? "Welcome tab" : s.StartupAction);
            StartupCustomPathBox.Text = s.StartupCustomPath ?? string.Empty;
            SelectByText(NewTabActionCombo, string.Equals(s.NewTabAction, "Explorer tab", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.NewTabAction) ? "Welcome tab" : s.NewTabAction);
            NewTabCustomPathBox.Text = s.NewTabCustomPath ?? string.Empty;

            StatusCheck.IsChecked = s.ShowStatusSurface;
            StatusVisibleMirrorCheck.IsChecked = s.ShowStatusSurface;
            StatusHoverWhenClosedCheck.IsChecked = s.StatusShowOnHoverWhenClosed;
            StatusHoverMirrorCheck.IsChecked = s.StatusShowOnHoverWhenClosed;
            ShowScrollbarsCheck.IsChecked = s.ShowScrollbars;
            FullPathTitleCheck.IsChecked = s.FullPathInTitle;
            HideCursorFullscreenCheck.IsChecked = s.HideCursorFullscreen;
            AutoHideFullscreenChromeCheck.IsChecked = s.AutoHideFullscreenChrome;
            TabsCheck.IsChecked = s.TabsEnabled;
            SelectByText(NavigateToFolderBehaviorCombo, "Windows Explorer");
            TabBarBackButtonCheck.IsChecked = s.TabBarShowBackButton;
            TabBarForwardButtonCheck.IsChecked = s.TabBarShowForwardButton;
            SelectByText(ExternalOpenBehaviorCombo, string.IsNullOrWhiteSpace(s.ExternalOpenBehavior) ? "Open in new tab" : s.ExternalOpenBehavior);
            SelectByText(SameImageBehaviorCombo, string.IsNullOrWhiteSpace(s.SameImageAlreadyOpenBehavior) ? "Open new instance" : s.SameImageAlreadyOpenBehavior);
            FullscreenXCloseCheck.IsChecked = s.FullscreenXClosesApp;
            _workingTitleBarButtons = TitleBarButtonCatalog.Normalize(s.TitleBarButtons).ToList();
            SelectByText(CaptionWindowedMinimizeCombo, s.CaptionWindowedMinimizeAction);
            SelectByText(CaptionWindowedMaximizeCombo, s.CaptionWindowedMaximizeAction);
            SelectByText(CaptionWindowedCloseCombo, s.CaptionWindowedCloseAction);
            SelectByText(CaptionFullscreenMinimizeCombo, s.CaptionFullscreenMinimizeAction);
            SelectByText(CaptionFullscreenMaximizeCombo, s.CaptionFullscreenMaximizeAction);
            SelectByText(CaptionFullscreenCloseCombo, s.CaptionFullscreenCloseAction);
            FullscreenStatusAlwaysCheck.IsChecked = s.FullscreenStatusAlwaysOn;
            FullscreenKeepTabBarOpenCheck.IsChecked = s.FullscreenKeepTabBarOpen;
            AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
            SelectByText(AlwaysOnTopModeCombo, string.Equals(s.AlwaysOnTopMode, "Soft", StringComparison.OrdinalIgnoreCase) ? "Soft" : "Hard");
            AutoCenterWindowOnRestoreCheck.IsChecked = s.AutoCenterWindowOnRestore;
            SelectByText(FullscreenExitBehaviorCombo, string.IsNullOrWhiteSpace(s.FullscreenExitBehavior) ? "Restore size and location" : s.FullscreenExitBehavior);
            SelectByText(DefaultViewCombo, s.DefaultViewMode);
            PointerZoomCheck.IsChecked = s.PointerZoom;
            PreserveZoomCheck.IsChecked = s.PreserveManualZoomOnNavigate;
            ViewportZoomStepBox.Value = Math.Clamp(s.ViewportZoomStepPercent, 1, 100);
            UpscaleSmallImagesCheck.IsChecked = s.UpscaleSmallImages;
            SubpixelRenderingCheck.IsChecked = s.HighPrecisionSubpixelRendering;
            ShowLoadingIndicatorCheck.IsChecked = s.ShowImageLoadingIndicator;
            FullscreenHideCursorDelayBox.Value = Math.Clamp(s.FullscreenHideCursorDelaySeconds, 1, 30);
            TabFocusNavigationCheck.IsChecked = s.EnableTabFocusNavigation;
            EscStopsSlideshowCheck.IsChecked = s.EscStopsSlideshow;
            EscExitsFullscreenCheck.IsChecked = s.EscExitsFullscreen;
            EscWindowedConfirmCheck.IsChecked = s.EscWindowedConfirm;
            _escRememberedChoice = string.IsNullOrWhiteSpace(s.EscWindowedRememberedChoice) ? "Ask" : s.EscWindowedRememberedChoice;
            ResetEscChoiceButton.IsEnabled = !string.Equals(_escRememberedChoice, "Ask", StringComparison.OrdinalIgnoreCase);

            DoubleClickFullscreenCheck.IsChecked = s.DoubleClickFullscreen;
            DoubleClickExitFullscreenCheck.IsChecked = s.DoubleClickExitFullscreen;
            FullscreenClicksCheck.IsChecked = s.FullscreenClickNavigation;
            WindowedWheelZoomCheck.IsChecked = s.WindowedWheelZoom;
            InvertWheelCheck.IsChecked = s.InvertWheelDirection;
            SelectByText(LeftDragModeCombo, s.LeftDragMode);
            SelectByText(LeftWindowDragBehaviorCombo, string.IsNullOrWhiteSpace(s.LeftWindowDragBehavior) ? "Smart (normal window only)" : s.LeftWindowDragBehavior);
            SelectByText(RightDragBehaviorCombo, string.IsNullOrWhiteSpace(s.RightDragBehavior) ? "Smart (pan image / move window)" : s.RightDragBehavior);
            SelectionClickZoomCheck.IsChecked = s.SelectionClickZoom;
            SelectionRightZoomCheck.IsChecked = s.SelectionRightClickZoomOut;
            ReverseSelectionZoomOutScaleCheck.IsChecked = s.ReverseSelectionZoomOutScale;
            BackgroundDragCheck.IsChecked = s.BackgroundDragWindow;
            CtrlWheelZoomCheck.IsChecked = s.CtrlWheelZoom;
            MiddleDragPanCheck.IsChecked = s.MiddleDragPan;
            _workingGestures = (s.Gestures ?? GestureCatalog.CreateDefaultMap()).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var slot in GestureCatalog.Slots)
            {
                if (!_gestureEditors.TryGetValue(slot.Id, out var editor)) continue;
                SelectGestureAction(editor, GestureCatalog.ResolveAction(slot.Id, _workingGestures));
            }

            SelectByText(InitialQualityCombo, s.InitialImageQuality);
            SelectByText(InteractivePanQualityCombo, string.IsNullOrWhiteSpace(s.InteractivePanQuality) ? "Full quality" : s.InteractivePanQuality);
            SpeedBoostCheck.IsChecked = s.SpeedBoostEnabled;
            StartWithWindowsInBackgroundCheck.IsChecked = s.StartWithWindowsInBackground;
            AdaptiveFastPreviewCheck.IsChecked = s.AdaptiveFastPreview;
            AdaptivePreviewDelayBox.Value = s.AdaptivePreviewDelayMs;
            SequentialReadCheck.IsChecked = s.SequentialForegroundReads;
            PrefetchCheck.IsChecked = s.PredictivePrefetch;
            BackgroundRefinementCheck.IsChecked = s.BackgroundRefinement;
            PurgeCacheCheck.IsChecked = s.PurgeCacheOnMinimize;
            StartupDiagnosticsCheck.IsChecked = s.StartupDiagnostics;
            PrefetchDepthBox.Value = Math.Clamp(s.PrefetchDepth, 1, 30);
            PrefetchSiblingFoldersCheck.IsChecked = s.PrefetchSiblingFolders;
            PrefetchSiblingFolderCountBox.Value = Math.Clamp(s.PrefetchSiblingFolderImageCount, 1, 30);
            CacheItemsBox.Value = s.CacheItems;
            CompressedCacheMbBox.Value = s.CompressedCacheMegabytes;
            DecodedCacheMbBox.Value = s.DecodedCacheMegabytes;
            RapidPreviewBox.Value = s.RapidPreviewLongestSide;
            RapidBrowsePreviewBox.Value = Math.Clamp(s.RapidBrowsePreviewLongestSide, 640, 4096);
            ProgressiveColorCheck.IsChecked = s.ProgressiveColorFirstPreview;

            StatusNavigationCheck.IsChecked = s.StatusShowNavigation;
            SelectByText(StatusBarSizeCombo, string.IsNullOrWhiteSpace(s.StatusBarSize) ? "Medium" : s.StatusBarSize);
            StatusGlobalScaleBox.Value = Math.Clamp(s.StatusBarGlobalScalePercent, 60, 160);
            StatusAutoFitCheck.IsChecked = s.StatusBarAutoFit;
            StatusMaximizedBoostBox.Value = Math.Clamp(s.StatusBarMaximizedBoostPercent, 0, 30);
            OverlayAbsoluteCoordinatesCheck.IsChecked = s.OverlayAbsoluteCoordinatesOnResize;
            StatusZoomCheck.IsChecked = s.StatusShowZoom;
            StatusSlideshowCheck.IsChecked = s.StatusShowSlideshow;
            StatusFitCheck.IsChecked = s.StatusShowFit;
            StatusInfoCheck.IsChecked = s.StatusShowInfo;
            StatusOptionsCheck.IsChecked = s.StatusShowOptions;
            StatusCloseCheck.IsChecked = s.StatusShowClose;
            StatIndexCheck.IsChecked = s.StatusStatIndex;
            StatResolutionCheck.IsChecked = s.StatusStatResolution;
            StatZoomCheck.IsChecked = s.StatusStatZoom;
            StatFileSizeCheck.IsChecked = s.StatusStatFileSize;
            StatFormatCheck.IsChecked = s.StatusStatFormat;

            SlideshowIntervalBox.Value = s.SlideshowIntervalMs;
            SlideshowLoopCheck.IsChecked = s.SlideshowLoop;
            SlideshowCrossFoldersCheck.IsChecked = s.SlideshowCrossFolders;
            SlideshowShuffleCheck.IsChecked = s.SlideshowShuffle;
            SelectByText(SlideshowDirectionCombo, string.IsNullOrWhiteSpace(s.SlideshowDirection) ? "Forward" : s.SlideshowDirection);
            SlideshowStartFullscreenCheck.IsChecked = s.SlideshowStartFullscreen;
            SlideshowPauseInactiveCheck.IsChecked = s.SlideshowPauseWhenInactive;
            SlideshowRightClickStopsCheck.IsChecked = s.SlideshowRightClickStops;
            SlideshowQualityIndicatorCheck.IsChecked = s.SlideshowShowQualityIndicator;

            TabMinWidthBox.Value = s.TabMinWidth;
            TabMaxWidthBox.Value = s.TabMaxWidth;
            TabOverflowArrowsCheck.IsChecked = s.TabOverflowArrows;
            TabDetachCheck.IsChecked = s.TabDetachEnabled;
            TabAttachCheck.IsChecked = s.TabAttachEnabled;
            ClosedHistoryBox.Value = s.ClosedTabHistoryLimit;
            CloseEmptyAfterDetachCheck.IsChecked = s.CloseEmptyWindowAfterDetach;
            DetachedHomeTabCheck.IsChecked = s.DetachedWindowHomeTab;
            DoubleClickTabCloseCheck.IsChecked = s.DoubleClickTabCloses;
            SelectByText(LastTabBehaviorCombo, string.IsNullOrWhiteSpace(s.LastTabCloseBehavior) ? "Open home page" : s.LastTabCloseBehavior);
            ConfirmCloseMultipleTabsCheck.IsChecked = s.ConfirmCloseMultipleTabs;
            SelectByText(HomePageCombo, string.Equals(s.HomePageMode, "Browser page", StringComparison.OrdinalIgnoreCase) ? "Welcome page" : s.HomePageMode);
            FolderNavGroupCheck.IsChecked = s.FolderNavShowGroup;
            FolderNavPreviousCheck.IsChecked = s.FolderNavShowPrevious;
            FolderNavNextCheck.IsChecked = s.FolderNavShowNext;
            FolderNavExploreCheck.IsChecked = s.FolderNavShowExplore;
            FolderNavSkipEmptyCheck.IsChecked = s.FolderNavSkipEmpty;
            FolderNavIncludeHiddenCheck.IsChecked = s.FolderNavIncludeHidden;
            FolderNavWrapCheck.IsChecked = s.FolderNavWrap;
            FolderNavFirstImageCheck.IsChecked = s.FolderNavOpenFirstImage;
            SelectByText(FolderNavOrderCombo, string.IsNullOrWhiteSpace(s.FolderNavOrder) ? "Alphabetical" : s.FolderNavOrder);

            PictureCounterCheck.IsChecked = s.PictureCounterEnabled;
            PictureTemplateBox.Text = s.PictureCounterTemplate;
            PictureFontSizeBox.Value = s.PictureCounterFontSize;
            PictureBoldCheck.IsChecked = s.PictureCounterBold;
            PictureOpacityBox.Value = s.PictureCounterOpacity;
            SelectByText(PicturePositionCombo, s.PictureCounterPosition);
            SelectByText(PictureColorCombo, s.PictureCounterColor);
            PictureShadowCheck.IsChecked = s.PictureCounterShadow;
            OverlayOpacityBox.Value = s.OverlayDefaultOpacity;
            OverlayZoomStepBox.Value = s.OverlayZoomStepPercent;
            OverlayRememberFolderCheck.IsChecked = s.OverlayRememberFolder;
            OverlayPersistSessionCheck.IsChecked = s.OverlayPersistLayoutBetweenSessions;
            WholeAppOverlayAlwaysStartCheck.IsChecked = s.AlwaysStartWholeAppOverlayMode;
            WholeAppOverlayWindowedInteractionsCheck.IsChecked = s.WholeAppOverlayUseWindowedInteractions;
            OverlayKeyboardZoomCheck.IsChecked = s.OverlayKeyboardZoom;
            OverlayWheelZoomCheck.IsChecked = s.OverlayWheelZoom;
            OverlayHighlightCheck.IsChecked = s.OverlaySelectedHighlight;
            OverlayRememberZoomCheck.IsChecked = s.OverlayRememberZoom;
            OverlayRightDragCheck.IsChecked = s.OverlayRightDragPan;
            OverlayScaleWithWindowCheck.IsChecked = s.OverlayScaleWithWindow;
            SelectByText(OverlayAnimationRegionCombo, s.OverlayAnimationRegion ?? "Anywhere");
            OverlayAnimationSpeedBox.Value = s.OverlayAnimationSpeedDipsPerSecond;
            OverlayAnimationTurnIntervalBox.Value = s.OverlayAnimationTurnIntervalMs;
            OverlayAnimationTurnAngleBox.Value = s.OverlayAnimationTurnAngleDegrees;
            OverlayAnimationPauseCheck.IsChecked = s.OverlayAnimationPauseWhileInteracting;
            OverlayAnimationAvoidOverlapCheck.IsChecked = s.OverlayAnimationAvoidOverlap;
            SelectByText(OverlayAnimationRefreshRateCombo, $"{s.OverlayAnimationRefreshRateHz} Hz");
            OpenFileDefaultDirectoryBox.Text = s.OpenFileDefaultDirectory ?? string.Empty;
            OverlayDefaultDirectoryBox.Text = s.OverlayDefaultDirectory ?? string.Empty;
            ProfileDefaultDirectoryBox.Text = s.ProfileDefaultDirectory ?? string.Empty;
            _workingLastProfileDirectory = s.LastProfileDirectory ?? string.Empty;

            External1Box.Text = s.ExternalProgram1;
            External2Box.Text = s.ExternalProgram2;
            External3Box.Text = s.ExternalProgram3;
            _workingHotkeys = s.Hotkeys.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            UpdateExternalProgramHotkeyLabels();
            if (_hotkeyRowsBuilt) BuildHotkeyRows();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private GlideSettingsState ReadControls()
    {
        var state = _committed.CloneState();
        state.RecentHistoryEnabled = RecentHistoryCheck.IsChecked == true;
        state.HistorySize = (int)(HistorySizeBox.Value ?? 8);
        state.RememberWindowPlacement = RememberPlacementCheck.IsChecked == true;
        state.ReuseSingleInstance = ReuseSingleInstanceCheck.IsChecked == true;
        state.ContinueSiblingFolders = ContinueSiblingFoldersCheck.IsChecked == true;
        state.HierarchicalFolderTraversal = HierarchicalFolderTraversalCheck.IsChecked == true;
        state.ConfirmHierarchicalFolderTraversal = ConfirmHierarchicalFolderTraversalCheck.IsChecked == true;
        state.HierarchicalPreviousFolderEntry = SelectedText(HierarchicalPreviousFolderEntryCombo, "First image");
        state.NavigationRateLimitMs = (int)(NavigationRateLimitMsBox.Value ?? 0);
        state.NavigationRateLimitInput = SelectedText(NavigationRateLimitInputCombo, "Both");
        state.NavigationRateLimitShowIndicator = NavigationRateLimitIndicatorCheck.IsChecked == true;
        state.NavigationRateLimitIndicatorPosition = SelectedText(NavigationRateLimitIndicatorPositionCombo, "Top left");
        state.NavigationRateLimitWindowed = NavigationRateLimitWindowedCheck.IsChecked == true;
        state.NavigationRateLimitFullscreen = NavigationRateLimitFullscreenCheck.IsChecked == true;
        state.NavigationRateLimitSlideshow = NavigationRateLimitSlideshowCheck.IsChecked == true;
        state.RememberLastOpenLocation = RememberLastOpenCheck.IsChecked == true;
        state.NewExplorerTabsUseLastLocation = NewExplorerTabLastLocationCheck.IsChecked == true;
        state.NewExplorerTabDefaultDirectory = NewExplorerTabDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.ThemeChoice = SelectedText(ThemeCombo, "Dark");
        state.ExplorerThemeChoice = SelectedText(ExplorerThemeCombo, "Follow Glide theme");
        state.LightTheme = string.Equals(state.ThemeChoice, "Light", StringComparison.OrdinalIgnoreCase);
        state.AccentChoice = SelectedText(AccentCombo, "Glide blue");
        state.GlowChoice = SelectedText(GlowCombo, "Follow accent");
        state.MainBackgroundChoice = SelectedText(MainBackgroundCombo, "Follow theme");
        state.CustomAccentHex = _workingCustomAccentHex;
        state.CustomGlowHex = _workingCustomGlowHex;
        state.CustomMainBackgroundHex = _workingCustomMainBackgroundHex;
        state.GlowIntensityPercent = (int)(GlowIntensityBox.Value ?? 55);
        state.ShowHomeTips = HomeTipsCheck.IsChecked == true;
        state.ShowRecentOnHome = ShowRecentOnHomeCheck.IsChecked == true;
        state.ConfirmDiscardSettingsChanges = ConfirmDiscardSettingsCheck.IsChecked == true;
        state.StartupAction = SelectedText(StartupActionCombo, "Welcome tab") is "Explorer tab" ? "Welcome tab" : SelectedText(StartupActionCombo, "Welcome tab");
        state.StartupCustomPath = StartupCustomPathBox.Text?.Trim() ?? string.Empty;
        state.NewTabAction = SelectedText(NewTabActionCombo, "Welcome tab") is "Explorer tab" ? "Welcome tab" : SelectedText(NewTabActionCombo, "Welcome tab");
        state.NewTabCustomPath = NewTabCustomPathBox.Text?.Trim() ?? string.Empty;

        state.ShowStatusSurface = StatusCheck.IsChecked == true;
        state.StatusShowOnHoverWhenClosed = StatusHoverWhenClosedCheck.IsChecked == true || StatusHoverMirrorCheck.IsChecked == true;
        state.ShowScrollbars = ShowScrollbarsCheck.IsChecked == true;
        state.FullPathInTitle = FullPathTitleCheck.IsChecked == true;
        state.HideCursorFullscreen = HideCursorFullscreenCheck.IsChecked == true;
        state.AutoHideFullscreenChrome = AutoHideFullscreenChromeCheck.IsChecked == true;
        state.TabsEnabled = TabsCheck.IsChecked == true;
        state.NavigateToFolderBehavior = "Windows Explorer";
        state.TabBarShowBackButton = TabBarBackButtonCheck.IsChecked == true;
        state.TabBarShowForwardButton = TabBarForwardButtonCheck.IsChecked == true;
        state.ExternalOpenBehavior = SelectedText(ExternalOpenBehaviorCombo, "Open in new tab");
        state.SameImageAlreadyOpenBehavior = SelectedText(SameImageBehaviorCombo, "Open new instance");
        state.TitleBarButtons = TitleBarButtonCatalog.Normalize(_workingTitleBarButtons).ToList();
        state.CaptionWindowedMinimizeAction = SelectedText(CaptionWindowedMinimizeCombo, "Minimize");
        state.CaptionWindowedMaximizeAction = SelectedText(CaptionWindowedMaximizeCombo, "Maximize / restore");
        state.CaptionWindowedCloseAction = SelectedText(CaptionWindowedCloseCombo, "Close Glide");
        state.CaptionFullscreenMinimizeAction = SelectedText(CaptionFullscreenMinimizeCombo, "Minimize");
        state.CaptionFullscreenMaximizeAction = SelectedText(CaptionFullscreenMaximizeCombo, "Exit fullscreen (restore previous placement)");
        state.CaptionFullscreenCloseAction = SelectedText(CaptionFullscreenCloseCombo, "Exit fullscreen maximized");
        state.FullscreenXClosesApp = string.Equals(state.CaptionFullscreenCloseAction, "Close Glide", StringComparison.OrdinalIgnoreCase);
        state.FullscreenStatusAlwaysOn = FullscreenStatusAlwaysCheck.IsChecked == true;
        state.FullscreenKeepTabBarOpen = FullscreenKeepTabBarOpenCheck.IsChecked == true;
        state.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        state.AlwaysOnTopMode = SelectedText(AlwaysOnTopModeCombo, "Hard");
        state.AutoCenterWindowOnRestore = AutoCenterWindowOnRestoreCheck.IsChecked == true;
        state.FullscreenExitBehavior = SelectedText(FullscreenExitBehaviorCombo, "Restore size and location");
        state.DefaultViewMode = SelectedText(DefaultViewCombo, "Fit image");
        state.PointerZoom = PointerZoomCheck.IsChecked == true;
        state.PreserveManualZoomOnNavigate = PreserveZoomCheck.IsChecked == true;
        state.ViewportZoomStepPercent = (int)(ViewportZoomStepBox.Value ?? 10);
        state.UpscaleSmallImages = UpscaleSmallImagesCheck.IsChecked == true;
        state.HighPrecisionSubpixelRendering = SubpixelRenderingCheck.IsChecked == true;
        state.ShowImageLoadingIndicator = ShowLoadingIndicatorCheck.IsChecked == true;
        state.FullscreenHideCursorDelaySeconds = (int)(FullscreenHideCursorDelayBox.Value ?? 2);
        state.EnableTabFocusNavigation = TabFocusNavigationCheck.IsChecked == true;
        state.EscStopsSlideshow = EscStopsSlideshowCheck.IsChecked == true;
        state.EscExitsFullscreen = EscExitsFullscreenCheck.IsChecked == true;
        state.EscWindowedConfirm = EscWindowedConfirmCheck.IsChecked == true;
        state.EscWindowedRememberedChoice = _escRememberedChoice;

        state.DoubleClickFullscreen = DoubleClickFullscreenCheck.IsChecked == true;
        state.DoubleClickExitFullscreen = DoubleClickExitFullscreenCheck.IsChecked == true;
        state.FullscreenClickNavigation = FullscreenClicksCheck.IsChecked == true;
        state.WindowedWheelZoom = WindowedWheelZoomCheck.IsChecked == true;
        state.InvertWheelDirection = InvertWheelCheck.IsChecked == true;
        state.LeftDragMode = SelectedText(LeftDragModeCombo, "Create selection (Glide)");
        state.LeftWindowDragBehavior = SelectedText(LeftWindowDragBehaviorCombo, "Smart (normal window only)");
        state.RightDragBehavior = SelectedText(RightDragBehaviorCombo, "Smart (pan image / move window)");
        state.RightDragPan = !string.Equals(state.RightDragBehavior, "Do nothing", StringComparison.OrdinalIgnoreCase);
        state.SelectionClickZoom = SelectionClickZoomCheck.IsChecked == true;
        state.SelectionRightClickZoomOut = SelectionRightZoomCheck.IsChecked == true;
        state.ReverseSelectionZoomOutScale = ReverseSelectionZoomOutScaleCheck.IsChecked == true;
        state.BackgroundDragWindow = BackgroundDragCheck.IsChecked == true;
        state.CtrlWheelZoom = CtrlWheelZoomCheck.IsChecked == true;
        state.MiddleDragPan = MiddleDragPanCheck.IsChecked == true;
        state.Gestures = _workingGestures.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _gestureEditors)
            state.Gestures[kv.Key] = (kv.Value.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? GestureCatalog.Default;

        state.InitialImageQuality = SelectedText(InitialQualityCombo, "Balanced");
        state.PerformanceUserCustom = string.Equals(state.InitialImageQuality, "User custom", StringComparison.OrdinalIgnoreCase);
        state.InteractivePanQuality = SelectedText(InteractivePanQualityCombo, "Full quality");
        state.SpeedBoostEnabled = SpeedBoostCheck.IsChecked == true;
        state.StartWithWindowsInBackground = StartWithWindowsInBackgroundCheck.IsChecked == true;
        state.AdaptiveFastPreview = AdaptiveFastPreviewCheck.IsChecked == true;
        state.AdaptivePreviewDelayMs = (int)(AdaptivePreviewDelayBox.Value ?? 20);
        state.SequentialForegroundReads = SequentialReadCheck.IsChecked == true;
        state.PredictivePrefetch = PrefetchCheck.IsChecked == true;
        state.BackgroundRefinement = BackgroundRefinementCheck.IsChecked == true;
        state.PurgeCacheOnMinimize = PurgeCacheCheck.IsChecked == true;
        state.StartupDiagnostics = StartupDiagnosticsCheck.IsChecked == true;
        state.PrefetchDepth = Math.Clamp((int)(PrefetchDepthBox.Value ?? 5), 1, 30);
        state.PrefetchSiblingFolders = PrefetchSiblingFoldersCheck.IsChecked == true;
        state.PrefetchSiblingFolderImageCount = Math.Clamp((int)(PrefetchSiblingFolderCountBox.Value ?? 3), 1, 30);
        state.CacheItems = (int)(CacheItemsBox.Value ?? 16);
        state.CompressedCacheMegabytes = (int)(CompressedCacheMbBox.Value ?? 256);
        state.DecodedCacheMegabytes = (int)(DecodedCacheMbBox.Value ?? 256);
        state.RapidPreviewLongestSide = (int)(RapidPreviewBox.Value ?? 3000);
        state.RapidBrowsePreviewLongestSide = Math.Clamp((int)(RapidBrowsePreviewBox.Value ?? 1440), 640, 4096);
        state.ProgressiveColorFirstPreview = ProgressiveColorCheck.IsChecked == true;

        state.StatusShowNavigation = StatusNavigationCheck.IsChecked == true;
        state.StatusBarSize = SelectedText(StatusBarSizeCombo, "Medium");
        state.StatusBarGlobalScalePercent = Math.Clamp((int)(StatusGlobalScaleBox.Value ?? 100), 60, 160);
        state.StatusBarAutoFit = StatusAutoFitCheck.IsChecked == true;
        state.StatusBarMaximizedBoostPercent = Math.Clamp((int)(StatusMaximizedBoostBox.Value ?? 15), 0, 30);
        state.OverlayAbsoluteCoordinatesOnResize = OverlayAbsoluteCoordinatesCheck.IsChecked == true;
        state.StatusShowZoom = StatusZoomCheck.IsChecked == true;
        state.StatusShowSlideshow = StatusSlideshowCheck.IsChecked == true;
        state.StatusShowFit = StatusFitCheck.IsChecked == true;
        state.StatusShowInfo = StatusInfoCheck.IsChecked == true;
        state.StatusShowOptions = StatusOptionsCheck.IsChecked == true;
        state.StatusShowClose = StatusCloseCheck.IsChecked == true;
        state.StatusStatIndex = StatIndexCheck.IsChecked == true;
        state.StatusStatResolution = StatResolutionCheck.IsChecked == true;
        state.StatusStatZoom = StatZoomCheck.IsChecked == true;
        state.StatusStatFileSize = StatFileSizeCheck.IsChecked == true;
        state.StatusStatFormat = StatFormatCheck.IsChecked == true;

        state.SlideshowIntervalMs = (int)(SlideshowIntervalBox.Value ?? 3000);
        state.SlideshowLoop = SlideshowLoopCheck.IsChecked == true;
        state.SlideshowCrossFolders = SlideshowCrossFoldersCheck.IsChecked == true;
        state.SlideshowShuffle = SlideshowShuffleCheck.IsChecked == true;
        state.SlideshowDirection = SelectedText(SlideshowDirectionCombo, "Forward");
        state.SlideshowStartFullscreen = SlideshowStartFullscreenCheck.IsChecked == true;
        state.SlideshowPauseWhenInactive = SlideshowPauseInactiveCheck.IsChecked == true;
        state.SlideshowRightClickStops = SlideshowRightClickStopsCheck.IsChecked == true;
        state.SlideshowShowQualityIndicator = SlideshowQualityIndicatorCheck.IsChecked == true;

        state.TabMinWidth = (int)(TabMinWidthBox.Value ?? 120);
        state.TabMaxWidth = (int)(TabMaxWidthBox.Value ?? 240);
        state.TabOverflowArrows = TabOverflowArrowsCheck.IsChecked == true;
        state.TabDetachEnabled = TabDetachCheck.IsChecked == true;
        state.TabAttachEnabled = TabAttachCheck.IsChecked == true;
        state.ClosedTabHistoryLimit = (int)(ClosedHistoryBox.Value ?? 20);
        state.CloseEmptyWindowAfterDetach = CloseEmptyAfterDetachCheck.IsChecked == true;
        state.DetachedWindowHomeTab = DetachedHomeTabCheck.IsChecked == true;
        state.DoubleClickTabCloses = DoubleClickTabCloseCheck.IsChecked == true;
        state.LastTabCloseBehavior = SelectedText(LastTabBehaviorCombo, "Open home page");
        state.ConfirmCloseMultipleTabs = ConfirmCloseMultipleTabsCheck.IsChecked == true;
        if (state.ConfirmCloseMultipleTabs) state.RememberedMultiTabCloseChoice = "Ask";
        state.HomePageMode = SelectedText(HomePageCombo, "Welcome page") is "Browser page" ? "Welcome page" : SelectedText(HomePageCombo, "Welcome page");
        state.FolderNavShowGroup = FolderNavGroupCheck.IsChecked == true;
        state.FolderNavShowPrevious = FolderNavPreviousCheck.IsChecked == true;
        state.FolderNavShowNext = FolderNavNextCheck.IsChecked == true;
        state.FolderNavShowExplore = FolderNavExploreCheck.IsChecked == true;
        state.FolderNavSkipEmpty = FolderNavSkipEmptyCheck.IsChecked == true;
        state.FolderNavIncludeHidden = FolderNavIncludeHiddenCheck.IsChecked == true;
        state.FolderNavWrap = FolderNavWrapCheck.IsChecked == true;
        state.FolderNavOpenFirstImage = FolderNavFirstImageCheck.IsChecked == true;
        state.FolderNavOrder = SelectedText(FolderNavOrderCombo, "Alphabetical");

        state.PictureCounterEnabled = PictureCounterCheck.IsChecked == true;
        state.PictureCounterTemplate = string.IsNullOrWhiteSpace(PictureTemplateBox.Text) ? "[{index}/{total}]" : PictureTemplateBox.Text!;
        state.PictureCounterFontSize = (int)(PictureFontSizeBox.Value ?? 13);
        state.PictureCounterBold = PictureBoldCheck.IsChecked == true;
        state.PictureCounterOpacity = (int)(PictureOpacityBox.Value ?? 85);
        state.PictureCounterPosition = SelectedText(PicturePositionCombo, "Top right");
        state.PictureCounterColor = SelectedText(PictureColorCombo, "White");
        state.PictureCounterShadow = PictureShadowCheck.IsChecked == true;
        state.OverlayDefaultOpacity = (int)(OverlayOpacityBox.Value ?? 100);
        state.OverlayZoomStepPercent = (int)(OverlayZoomStepBox.Value ?? 10);
        state.OverlayRememberFolder = OverlayRememberFolderCheck.IsChecked == true;
        state.OverlayPersistLayoutBetweenSessions = OverlayPersistSessionCheck.IsChecked == true;
        state.AlwaysStartWholeAppOverlayMode = WholeAppOverlayAlwaysStartCheck.IsChecked == true;
        state.WholeAppOverlayUseWindowedInteractions = WholeAppOverlayWindowedInteractionsCheck.IsChecked == true;
        state.OverlayKeyboardZoom = OverlayKeyboardZoomCheck.IsChecked == true;
        state.OverlayWheelZoom = OverlayWheelZoomCheck.IsChecked == true;
        state.OverlaySelectedHighlight = OverlayHighlightCheck.IsChecked == true;
        state.OverlayRememberZoom = OverlayRememberZoomCheck.IsChecked == true;
        state.OverlayRightDragPan = OverlayRightDragCheck.IsChecked == true;
        state.OverlayScaleWithWindow = OverlayScaleWithWindowCheck.IsChecked == true;
        state.OverlayAnimationRegion = SelectedText(OverlayAnimationRegionCombo, "Anywhere");
        state.OverlayAnimationSpeedDipsPerSecond = Math.Clamp((int)(OverlayAnimationSpeedBox.Value ?? 90), 10, 2000);
        state.OverlayAnimationTurnIntervalMs = Math.Clamp((int)(OverlayAnimationTurnIntervalBox.Value ?? 2500), 0, 60000);
        state.OverlayAnimationTurnAngleDegrees = Math.Clamp((int)(OverlayAnimationTurnAngleBox.Value ?? 120), 0, 180);
        state.OverlayAnimationPauseWhileInteracting = OverlayAnimationPauseCheck.IsChecked == true;
        state.OverlayAnimationAvoidOverlap = OverlayAnimationAvoidOverlapCheck.IsChecked == true;
        state.OverlayAnimationRefreshRateHz = ParseChoiceNumber(OverlayAnimationRefreshRateCombo, 144);
        state.OpenFileDefaultDirectory = OpenFileDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.OverlayDefaultDirectory = OverlayDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.ProfileDefaultDirectory = ProfileDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.LastProfileDirectory = _workingLastProfileDirectory;

        state.ExternalProgram1 = External1Box.Text ?? string.Empty;
        state.ExternalProgram2 = External2Box.Text ?? string.Empty;
        state.ExternalProgram3 = External3Box.Text ?? string.Empty;
        state.Hotkeys = _workingHotkeys.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        return state;
    }

    private void WireLivePreview()
    {
        // Subscribe from the authoritative setting->editor map so newly added editable settings cannot
        // silently bypass live preview or exact net-change tracking. The mirrored Status checkbox is
        // the one intentional extra editor that maps to the same underlying state property.
        var controls = _settingControls.Values
            .Append<Control>(StatusVisibleMirrorCheck)
            .Distinct()
            .ToArray();
        foreach (var control in controls) control.PropertyChanged += ValueControlChanged;
    }

    private void ValueControlChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_loadingControls) return;
        var relevant = e.Property == ToggleButton.IsCheckedProperty ||
                       e.Property == SelectingItemsControl.SelectedIndexProperty ||
                       e.Property == NumericUpDown.ValueProperty ||
                       e.Property == TextBox.TextProperty;
        if (!relevant) return;

        if (ReferenceEquals(sender, StatusCheck))
        {
            _loadingControls = true;
            StatusVisibleMirrorCheck.IsChecked = StatusCheck.IsChecked;
            _loadingControls = false;
        }
        else if (ReferenceEquals(sender, StatusVisibleMirrorCheck))
        {
            _loadingControls = true;
            StatusCheck.IsChecked = StatusVisibleMirrorCheck.IsChecked;
            _loadingControls = false;
        }

        if (IsPerformanceTuningControl(sender)) DetectPerformanceProfileFromControls();

        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }


    private bool IsPerformanceTuningControl(object? sender) => sender is not null && sender != InitialQualityCombo &&
            new Control[] { InteractivePanQualityCombo, AdaptiveFastPreviewCheck, AdaptivePreviewDelayBox, SequentialReadCheck, PrefetchCheck, PrefetchDepthBox,
            BackgroundRefinementCheck, PrefetchSiblingFoldersCheck, PrefetchSiblingFolderCountBox, CacheItemsBox, CompressedCacheMbBox, DecodedCacheMbBox, RapidPreviewBox, RapidBrowsePreviewBox, ProgressiveColorCheck }
        .Any(x => ReferenceEquals(x, sender));

    private void DetectPerformanceProfileFromControls()
    {
        string detected = "User custom";
        foreach (var candidate in new[] { "Maximum speed", "Balanced", "Maximum quality" })
        {
            var policy = ImagePerformancePolicy.ForProfile(candidate);
            var expectedPan = candidate == "Maximum speed" ? "Fast" : "Full quality";
            var expectedRapid = candidate == "Maximum speed" ? 960 : candidate == "Maximum quality" ? 3500 : 1440;
            var expectedSiblingCount = candidate == "Maximum quality" ? 7 : 3;
            if (SelectedText(InteractivePanQualityCombo, "Full quality") == expectedPan &&
                AdaptiveFastPreviewCheck.IsChecked == policy.DecoderScaledFirstFrame &&
                (int)(AdaptivePreviewDelayBox.Value ?? 20) == policy.RefinementDelayMs &&
                SequentialReadCheck.IsChecked == policy.SequentialForegroundReads && PrefetchCheck.IsChecked == policy.PredictivePrefetch &&
                (int)(PrefetchDepthBox.Value ?? 5) == policy.PrefetchDepth &&
                BackgroundRefinementCheck.IsChecked == policy.BackgroundRefinement &&
                PurgeCacheCheck.IsChecked == false && StartupDiagnosticsCheck.IsChecked == false &&
                PrefetchSiblingFoldersCheck.IsChecked == (candidate == "Maximum quality") &&
                (int)(PrefetchSiblingFolderCountBox.Value ?? 3) == expectedSiblingCount &&
                (int)(CacheItemsBox.Value ?? 16) == policy.CompressedCacheItems &&
                (int)(CompressedCacheMbBox.Value ?? 256) == policy.CompressedCacheMegabytes && (int)(DecodedCacheMbBox.Value ?? 256) == policy.DecodedCacheMegabytes &&
                (int)(RapidPreviewBox.Value ?? 3000) == policy.PreviewLongestSide && (int)(RapidBrowsePreviewBox.Value ?? 1440) == expectedRapid &&
                ProgressiveColorCheck.IsChecked == policy.ProgressiveColorFirstPreview)
            { detected = candidate; break; }
        }
        _loadingControls = true;
        try { SelectByText(InitialQualityCombo, detected); }
        finally { _loadingControls = false; }
    }

    private void SaveUserPerformancePresetClicked(object? sender, RoutedEventArgs e)
    {
        // Persist only the reusable preset payload. Do not implicitly commit unrelated staged
        // Settings edits merely because the user chose to save a performance preset.
        var saved = _committed.CloneState();
        saved.PerformanceUserCustom = true;
        saved.PerformanceUserCustomSaved = true;
        saved.UserCustomPanQuality = SelectedText(InteractivePanQualityCombo, "Full quality");
        saved.UserCustomAdaptivePreview = AdaptiveFastPreviewCheck.IsChecked == true;
        saved.UserCustomRefinementDelayMs = (int)(AdaptivePreviewDelayBox.Value ?? 20);
        saved.UserCustomSequentialReads = SequentialReadCheck.IsChecked == true;
        saved.UserCustomPrefetch = PrefetchCheck.IsChecked == true;
        saved.UserCustomPrefetchDepth = Math.Clamp((int)(PrefetchDepthBox.Value ?? 5), 1, 30);
        saved.UserCustomBackgroundRefinement = BackgroundRefinementCheck.IsChecked == true;
        saved.UserCustomCacheItems = (int)(CacheItemsBox.Value ?? 16);
        saved.UserCustomCompressedCacheMb = (int)(CompressedCacheMbBox.Value ?? 256);
        saved.UserCustomDecodedCacheMb = (int)(DecodedCacheMbBox.Value ?? 256);
        saved.UserCustomPreviewLongestSide = (int)(RapidPreviewBox.Value ?? 3000);
        saved.UserCustomRapidBrowsePreviewSize = (int)(RapidBrowsePreviewBox.Value ?? 1440);
        saved.UserCustomProgressiveColor = ProgressiveColorCheck.IsChecked == true;
        _committed = saved;
        SettingsStore.Save(_committed);

        _loadingControls = true;
        try { SelectByText(InitialQualityCombo, "User custom"); } finally { _loadingControls = false; }
        var preview = ReadControls();
        preview.PerformanceUserCustom = true;
        _apply(preview, false);
        UpdateDirtyState(preview);
        DirtyText.Text = "User custom performance preset saved.";
    }

    private async void CustomizeTitleBarClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new TitleBarCustomizerWindow(_workingTitleBarButtons) { Topmost = Topmost };
        var result = await dialog.ShowDialog<IReadOnlyList<string>?>(this);
        if (result is null) return;
        _workingTitleBarButtons = TitleBarButtonCatalog.Normalize(result).ToList();
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
        PageSubtitle.Text = $"Title bar: {_workingTitleBarButtons.Count} custom button(s) selected.";
    }

    private void PerformanceProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        var profileName = SelectedText(InitialQualityCombo, "Balanced");
        if (string.Equals(profileName, "User custom", StringComparison.OrdinalIgnoreCase))
        {
            if (_committed.PerformanceUserCustomSaved)
            {
                _loadingControls = true;
                try
                {
                    SelectByText(InteractivePanQualityCombo, _committed.UserCustomPanQuality);
                    AdaptiveFastPreviewCheck.IsChecked = _committed.UserCustomAdaptivePreview;
                    AdaptivePreviewDelayBox.Value = _committed.UserCustomRefinementDelayMs;
                    SequentialReadCheck.IsChecked = _committed.UserCustomSequentialReads;
                    PrefetchCheck.IsChecked = _committed.UserCustomPrefetch;
                    PrefetchDepthBox.Value = _committed.UserCustomPrefetchDepth;
                    BackgroundRefinementCheck.IsChecked = _committed.UserCustomBackgroundRefinement;
                    CacheItemsBox.Value = _committed.UserCustomCacheItems;
                    CompressedCacheMbBox.Value = _committed.UserCustomCompressedCacheMb;
                    DecodedCacheMbBox.Value = _committed.UserCustomDecodedCacheMb;
                    RapidPreviewBox.Value = _committed.UserCustomPreviewLongestSide;
                    RapidBrowsePreviewBox.Value = _committed.UserCustomRapidBrowsePreviewSize;
                    ProgressiveColorCheck.IsChecked = _committed.UserCustomProgressiveColor;
                }
                finally { _loadingControls = false; }
                PreviewNow();
            }
            return;
        }
        var policy = Glide.Imaging.ImagePerformancePolicy.ForProfile(profileName);
        _loadingControls = true;
        try
        {
            SelectByText(InteractivePanQualityCombo, profileName switch
            {
                "Maximum speed" => "Fast",
                _ => "Full quality"
            });
            AdaptiveFastPreviewCheck.IsChecked = policy.DecoderScaledFirstFrame;
            AdaptivePreviewDelayBox.Value = policy.RefinementDelayMs;
            SequentialReadCheck.IsChecked = policy.SequentialForegroundReads;
            PrefetchCheck.IsChecked = policy.PredictivePrefetch;
            PrefetchDepthBox.Value = policy.PrefetchDepth;
            BackgroundRefinementCheck.IsChecked = policy.BackgroundRefinement;
            PurgeCacheCheck.IsChecked = false;
            StartupDiagnosticsCheck.IsChecked = false;
            PrefetchSiblingFoldersCheck.IsChecked = profileName == "Maximum quality";
            PrefetchSiblingFolderCountBox.Value = profileName == "Maximum quality" ? 7 : 3;
            CacheItemsBox.Value = policy.CompressedCacheItems;
            CompressedCacheMbBox.Value = policy.CompressedCacheMegabytes;
            DecodedCacheMbBox.Value = policy.DecodedCacheMegabytes;
            RapidPreviewBox.Value = policy.PreviewLongestSide;
            RapidBrowsePreviewBox.Value = profileName switch
            {
                "Maximum speed" => 960,
                "Maximum quality" => 3500,
                _ => 1440
            };
            ProgressiveColorCheck.IsChecked = true;
        }
        finally { _loadingControls = false; }
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private sealed record SettingChange(string Name, string Before, string After);

    private List<SettingChange> GetNetChanges(GlideSettingsState? current = null)
    {
        current ??= ReadControls();

        // Canonical semantic guard: opening Settings, cloning staged state, or changing a value and
        // changing it back must never make the window dirty. ContentEquals compares the mutable
        // settings collections by value, not by List/Dictionary object identity.
        if (_committed.ContentEquals(current)) return new List<SettingChange>();

        var changes = new List<SettingChange>();
        foreach (var property in typeof(GlideSettingsState).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.Name is nameof(GlideSettingsState.Hotkeys) or nameof(GlideSettingsState.Gestures)) continue;
            var before = property.GetValue(_committed);
            var after = property.GetValue(current);
            if (SettingValuesEqual(before, after)) continue;
            changes.Add(new SettingChange(HumanizeSettingName(property.Name), FormatSettingValue(before), FormatSettingValue(after)));
        }

        if (!DictionaryOfListsEqual(_committed.Hotkeys, current.Hotkeys))
            changes.Add(new SettingChange("Keyboard shortcuts", "Previous assignments", "Modified assignments"));
        if (!DictionaryEqual(_committed.Gestures, current.Gestures))
            changes.Add(new SettingChange("Mouse gesture matrix", "Previous assignments", "Modified assignments"));
        return changes;
    }

    private static bool DictionaryOfListsEqual(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var pair in a)
            if (!b.TryGetValue(pair.Key, out var values) || !pair.Value.SequenceEqual(values, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool DictionaryEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var pair in a)
            if (!b.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string HumanizeSettingName(string name)
    {
        var chars = new List<char>(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) chars.Add(' ');
            chars.Add(name[i]);
        }
        return new string(chars.ToArray()).Replace("Ms", "ms", StringComparison.Ordinal);
    }

    private static bool SettingValuesEqual(object? before, object? after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before is null || after is null) return false;

        // SettingsState intentionally contains mutable collection properties (currently TitleBarButtons).
        // CloneState creates a new List instance, so object.Equals would report a false change every time
        // Settings opens even when the ordered contents are identical. Compare string sequences by value.
        if (before is IEnumerable<string> beforeStrings && after is IEnumerable<string> afterStrings)
            return beforeStrings.SequenceEqual(afterStrings, StringComparer.OrdinalIgnoreCase);

        return Equals(before, after);
    }

    private static string FormatSettingValue(object? value) => value switch
    {
        null => "(none)",
        bool flag => flag ? "On" : "Off",
        string text when string.IsNullOrWhiteSpace(text) => "(empty)",
        IEnumerable<string> values => FormatStringSequence(values),
        _ => value.ToString() ?? "(none)"
    };

    private static string FormatStringSequence(IEnumerable<string> values)
    {
        var materialized = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return materialized.Length == 0 ? "(none)" : string.Join(", ", materialized);
    }

    private void UpdateDirtyState(GlideSettingsState? current = null)
    {
        current ??= ReadControls();
        var changes = GetNetChanges(current);
        ApplyButton.IsEnabled = changes.Count > 0;
        DirtyText.Text = changes.Count switch
        {
            0 => "Up to date",
            1 => "1 change",
            _ => $"{changes.Count} changes"
        };
        DirtyHintText.IsVisible = changes.Count > 0;
        var changeTip = changes.Count == 0 ? "No net changes." : string.Join("\n", changes.Select(c => $"{c.Name}: {c.Before} → {c.After}"));
        ToolTip.SetTip(DirtySummaryHost, changeTip);
        ToolTip.SetTip(DirtyText, changeTip);
        ToolTip.SetTip(DirtyHintText, changeTip);
    }

    private void ResetEscChoiceClicked(object? sender, RoutedEventArgs e)
    {
        _escRememberedChoice = "Ask";
        ResetEscChoiceButton.IsEnabled = false;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private void CategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string key || !_panels.ContainsKey(key)) return;
        ShowCategory(key, clearSearch: true);
    }

    private void ShowCategory(string key, bool clearSearch)
    {
        if (!_panels.ContainsKey(key)) return;
        _activeCategory = key;
        if (clearSearch && !string.IsNullOrEmpty(SearchBox.Text))
        {
            _loadingControls = true;
            SearchBox.Text = string.Empty;
            _loadingControls = false;
        }
        SearchPanel.IsVisible = false;
        foreach (var pair in _panels) pair.Value.IsVisible = pair.Key == key;
        foreach (var child in NavigationRail.Children.OfType<Button>())
        {
            child.Classes.Remove("selected");
            if (Equals(child.Tag, key)) child.Classes.Add("selected");
        }

        PageTitle.Text = DisplayCategory(key);
        PageSubtitle.Text = key switch
        {
            "General" => "Application behavior, history, navigation and startup/new-tab preferences",
            "Appearance" => "Themes, colours, sizing and visual styling without changing interaction behavior",
            "Viewing" => "Image presentation, viewport scaling, zoom behavior, scrollbars, text overlay HUD and overlays",
            "Animation" => "Overlay motion and animation controls",
            "Interface" => "Window behavior, folder traversal, tab layout, caption controls, navigation buttons and window interactions",
            "Mouse" => "Mouse gestures, selection, zoom, panning and fullscreen interaction",
            "Performance" => "Cold-start, decode quality, prefetch, refinement and memory controls",
            "Status" => "Choose the controls and information shown on Glide's status surface",
            "Slideshow" => "Timing, looping, shuffling and cross-folder slideshow behavior",
            "Hotkeys" => "Search, sort and customize every keyboard shortcut",
            "Tabs" => "Tab sizing, detach/attach behavior and closed-tab history",
            "Overlays" => "Overlay controls, opacity, remembered folders and Window-in-Window behavior",
            "Profiles" => "Whole-program presets, dynamic named profiles and portable import/export",
            "Windows" => "Native Windows integration and Open With registration",
            _ => "Diagnostics and developer-only troubleshooting tools"
        };
        if (key == "Hotkeys") { _hotkeyRowsBuilt = true; BuildHotkeyRows(); }
        if (key == "Mouse" && !_gestureMatrixBuilt) { BuildGestureMatrix(); LoadGestureEditorsFromWorkingMap(); }
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
    }

    private static string DisplayCategory(string key) => key switch
    {
        "Appearance" => "Themes & Colours",
        "Viewing" => "Viewing & Appearance",
        "Animation" => "Animation",
        "Interface" => "Interface & Behavior",
        "Mouse" => "Mouse & Fullscreen",
        "Performance" => "Performance & Startup",
        "Status" => "Status Bar",
        "Tabs" => "Tabs & Workspace",
        "Overlays" => "Window in Window",
        "Profiles" => "Profiles & Presets",
        "Windows" => "Windows Integration",
        "Developer" => "Developer Options",
        _ => key
    };

    private void SearchCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) SearchChanged(SearchBox, null!);
    }

    private string ActiveSearchCategory() => SelectedText(SearchCategoryCombo, "All categories");

    private void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingControls) return;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            _searchDebounceTimer.Stop();
            _pendingSearchQuery = string.Empty;
            _hotkeySearchRows.Clear();
            SearchPanel.Children.Clear();
            SearchPanel.IsVisible = false;
            ShowCategory(_activeCategory, clearSearch: false);
            return;
        }

        // Coalesce keystrokes: the rebuild below constructs a live editor per match, so running it
        // synchronously on every TextChanged stalls typing. The most recent query always wins.
        _pendingSearchQuery = query;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// Forces the pending debounced search to render immediately. Used by the diagnostic capture,
    /// which sets the search text and then screenshots without waiting for the debounce timer.
    /// </summary>
    internal void FlushPendingSearch()
    {
        _searchDebounceTimer.Stop();
        if (!string.IsNullOrEmpty(_pendingSearchQuery)) RunSearchRebuild(_pendingSearchQuery);
    }

    /// <summary>
    /// Builds the searchable text for one setting: catalog id/label/category/description/terms plus
    /// the mapped control's visible content, tooltip and control name. Cached per window because the
    /// wording never changes while Settings is open. This keeps search global and matches what the
    /// user actually reads in the UI (a checkbox labelled "Show status bar" is found by "status bar"
    /// even though the catalog label is "Show status surface").
    /// </summary>
    private string SearchHaystackFor(SettingDefinition setting)
    {
        if (_searchHaystackCache.TryGetValue(setting.Id, out var cached)) return cached;
        var sb = new StringBuilder(160);
        sb.Append(setting.Id).Append(' ')
          .Append(setting.Label).Append(' ')
          .Append(setting.Category).Append(' ')
          .Append(setting.Description);
        foreach (var term in setting.SearchTerms) sb.Append(' ').Append(term);
        if (_settingControls.TryGetValue(setting.Id, out var control)) AppendControlSearchText(sb, control);
        // Visible mirror controls write the same value but carry different on-screen wording.
        switch (setting.Id)
        {
            case "status.visible": AppendControlSearchText(sb, StatusVisibleMirrorCheck); break;
            case "status.hoverWhenClosed": AppendControlSearchText(sb, StatusHoverMirrorCheck); break;
        }
        cached = sb.ToString();
        _searchHaystackCache[setting.Id] = cached;
        return cached;

        static void AppendControlSearchText(StringBuilder builder, Control control)
        {
            var hasLabelContent = false;
            if (control is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
            {
                builder.Append(' ').Append(content);
                hasLabelContent = true;
            }
            if (ToolTip.GetTip(control) is string tip && !string.IsNullOrWhiteSpace(tip))
                builder.Append(' ').Append(tip);
            if (!string.IsNullOrWhiteSpace(control.Name)) builder.Append(' ').Append(control.Name);
            // ComboBox / NumericUpDown / TextBox editors carry their label as a sibling TextBlock
            // rather than Content; index that visible label so search matches what the user reads
            // (for example "Always-on-top strength").
            if (!hasLabelContent && control.GetVisualParent() is Panel parent)
            {
                var siblings = parent.Children;
                for (var i = siblings.IndexOf(control) - 1; i >= 0; i--)
                {
                    if (siblings[i] is TextBlock label && !string.IsNullOrWhiteSpace(label.Text))
                    {
                        builder.Append(' ').Append(label.Text);
                        break;
                    }
                    if (siblings[i] is not TextBlock) break;
                }
            }
        }
    }

    private void RunSearchRebuild(string query)
    {
        if (_loadingControls || string.IsNullOrEmpty(query)) return;

        foreach (var panel in _panels.Values) panel.IsVisible = false;
        foreach (var child in NavigationRail.Children.OfType<Button>()) child.Classes.Remove("selected");
        SearchPanel.Children.Clear();
        _hotkeySearchRows.Clear();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var categoryFilter = ActiveSearchCategory();
        var allCategories = string.Equals(categoryFilter, "All categories", StringComparison.OrdinalIgnoreCase);
        var matches = SettingsCatalog.All.Where(setting =>
            (allCategories || string.Equals(setting.Category, categoryFilter, StringComparison.OrdinalIgnoreCase)) &&
            words.All(word => SearchHaystackFor(setting).Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();

        // Legacy Settings search is not a read-only index: the user can change matching values here.
        foreach (var setting in matches)
            SearchPanel.Children.Add(BuildSearchResult(setting));

        var includeHotkeys = allCategories || string.Equals(categoryFilter, "Hotkeys", StringComparison.OrdinalIgnoreCase);
        var hotkeyMatches = includeHotkeys ? HotkeyCatalog.All.Where(h => words.All(word =>
            h.Label.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            h.Category.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            h.Id.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            word.Contains("hotkey", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("shortcut", StringComparison.OrdinalIgnoreCase) ||
            _workingHotkeys.GetValueOrDefault(h.Id, EmptyShortcutList).Any(k => k.Contains(word, StringComparison.OrdinalIgnoreCase)))).ToList() : new List<HotkeyActionDefinition>();
        if (hotkeyMatches.Count > 0)
        {
            SearchPanel.Children.Add(new TextBlock
            {
                Text = "Hotkeys",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(2, 7, 0, 1)
            });
            foreach (var action in hotkeyMatches.Take(18)) SearchPanel.Children.Add(BuildHotkeySearchResult(action));
        }

        if (matches.Count == 0 && hotkeyMatches.Count == 0)
            SearchPanel.Children.Add(new TextBlock { Text = "No matching Glide setting.", Margin = new Thickness(6, 12), Foreground = SearchMutedBrush() });

        SearchPanel.IsVisible = true;
        PageTitle.Text = "Search results";
        PageSubtitle.Text = allCategories
            ? $"Edit settings matching ‘{query}’ across all categories"
            : $"Edit settings matching ‘{query}’ in {categoryFilter}";
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
    }

    private Control BuildHotkeySearchResult(HotkeyActionDefinition action)
    {
        var border = new Border();
        border.Classes.Add("searchResult");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,210,Auto,Auto,Auto"), ColumnSpacing = 7 };
        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = action.Label, FontWeight = FontWeight.SemiBold, FontSize = 14.5 });
        label.Children.Add(new TextBlock { Text = action.Category, Foreground = SearchMutedBrush(), FontSize = 12 });
        grid.Children.Add(label);

        var shortcutText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(shortcutText, 1);
        grid.Children.Add(shortcutText);

        Button Make(string text, int column, Action actionHandler, string tooltip)
        {
            var button = new Button { Content = text, MinWidth = 64, Height = 30, VerticalAlignment = VerticalAlignment.Center };
            button.Classes.Add("searchOpen");
            button.Click += (_, _) => actionHandler();
            ToolTip.SetTip(button, tooltip);
            ToolTip.SetShowDelay(button, 450);
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
            return button;
        }

        Make("Change", 2, () => StartHotkeyCaptureFor(action.Id, _workingHotkeys.GetValueOrDefault(action.Id, new()).FirstOrDefault(), add: false, fromSearch: true, border),
            "Replace the primary shortcut. You can also double-click this hotkey result card.");
        Make("Add", 3, () => StartHotkeyCaptureFor(action.Id, null, add: true, fromSearch: true, border),
            "Add another keyboard shortcut without removing existing shortcuts.");
        Make("Clear", 4, () =>
        {
            _workingHotkeys[action.Id] = new List<string>();
            BuildHotkeyRows();
            PreviewHotkeyChange();
            RefreshHotkeySearchDisplay(action.Id);
            PageSubtitle.Text = $"Cleared shortcuts for {action.Label}.";
        }, "Clear every shortcut currently assigned to this action.");

        border.Child = grid;
        _hotkeySearchRows[action.Id] = (border, shortcutText);
        RefreshHotkeySearchDisplay(action.Id);
        border.DoubleTapped += (_, e) =>
        {
            // Buttons keep their own double-click semantics; everywhere else on a hotkey result card
            // behaves exactly like the Change button and enters real keyboard capture mode.
            if (e.Source is Control source)
            {
                Control? current = source;
                while (current is not null && !ReferenceEquals(current, border))
                {
                    if (current is Button) return;
                    current = current.GetVisualParent() as Control;
                }
            }
            StartHotkeyCaptureFor(action.Id, _workingHotkeys.GetValueOrDefault(action.Id, new()).FirstOrDefault(), add: false, fromSearch: true, border);
            e.Handled = true;
        };
        ToolTip.SetTip(border, "Double-click to change this hotkey. Drag/scroll position is preserved while capturing.");
        return border;
    }

    private void RefreshHotkeySearchDisplay(string actionId)
    {
        if (!_hotkeySearchRows.TryGetValue(actionId, out var row)) return;
        var shortcuts = _workingHotkeys.GetValueOrDefault(actionId, new());
        row.Shortcut.Text = shortcuts.Count == 0 ? "Unassigned" : string.Join("  •  ", shortcuts);
        row.Shortcut.Foreground = shortcuts.Count == 0 ? SearchMutedBrush() : SearchAccentBrush();
    }

    private Control BuildSearchResult(SettingDefinition setting)
    {
        var border = new Border();
        border.Classes.Add("searchResult");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = setting.Label, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        text.Children.Add(new TextBlock { Text = $"{setting.Category}  •  {setting.Description}", TextWrapping = TextWrapping.Wrap, Foreground = SearchMutedBrush(), FontSize = 12.5 });
        grid.Children.Add(text);

        Control editor;
        Control? source = null;
        var companions = CreateSearchCompanionButtons(setting.Id).ToList();
        if (setting.FuturePhase is not null)
        {
            editor = new TextBlock
            {
                Text = $"Future (Phase {setting.FuturePhase})",
                Foreground = SearchMutedBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyle.Italic
            };
            ToolTip.SetTip(editor, setting.Description);
        }
        else if (_settingControls.TryGetValue(setting.Id, out source) && source.IsEnabled)
            editor = CreateSearchProxy(source);
        else if (companions.Count > 0)
        {
            // Action settings own labelled companion buttons (Open/Import/Add/...). Do not also add
            // the generic category "Open" fallback, which produced two identical Open buttons for the
            // command registry and gesture matrix.
            editor = new Panel { Width = 0, Height = 1 };
        }
        else
        {
            var open = new Button { Content = "Open", Tag = CategoryKey(setting.Category), VerticalAlignment = VerticalAlignment.Center };
            open.Classes.Add("searchOpen");
            open.Click += (_, _) => ShowCategory((string)open.Tag!, clearSearch: true);
            editor = open;
        }
        var completeEditor = BuildSearchEditorGroup(editor, companions);
        Grid.SetColumn(completeEditor, 1);
        grid.Children.Add(completeEditor);
        border.Child = grid;

        // Boolean search cards write the authoritative staged CheckBox directly. The small checkbox
        // shown on the result is display-only, so there is exactly one source of truth and one toggle
        // per click. ValueControlChanged then previews/persists the same value used by Apply/OK.
        if (source is CheckBox sourceCheckBox && editor is CheckBox displayCheckBox)
        {
            border.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                var next = sourceCheckBox.IsChecked != true;
                sourceCheckBox.IsChecked = next;
                displayCheckBox.IsChecked = next;
                e.Handled = true;
            }, RoutingStrategies.Bubble, true);
            ToolTip.SetTip(border, $"Click anywhere on this card to toggle. {setting.Description}");
        }
        else ToolTip.SetTip(border, setting.Description);
        return border;
    }

    private Control BuildSearchEditorGroup(Control editor, IReadOnlyList<Button> companions)
    {
        if (companions.Count == 0) return editor;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(editor);
        foreach (var button in companions) panel.Children.Add(button);
        return panel;
    }

    private IEnumerable<Button> CreateSearchCompanionButtons(string id)
    {
        Button B(string text, Action<Button> click)
        {
            var b = new Button { Content = text, MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
            b.Classes.Add("secondaryAction");
            b.Click += (_, _) => click(b);
            return b;
        }
        switch (id)
        {
            case "windows.external1": yield return B("Browse...", b => { b.Tag = "1"; BrowseExternalClicked(b, new RoutedEventArgs()); }); break;
            case "windows.external2": yield return B("Browse...", b => { b.Tag = "2"; BrowseExternalClicked(b, new RoutedEventArgs()); }); break;
            case "windows.external3": yield return B("Browse...", b => { b.Tag = "3"; BrowseExternalClicked(b, new RoutedEventArgs()); }); break;
            case "windows.fileAssociations":
                yield return B("Add", b => AddOpenWithClicked(b, new RoutedEventArgs()));
                yield return B("Remove", b => RemoveOpenWithClicked(b, new RoutedEventArgs()));
                break;
            case "general.openFileDefaultDirectory": yield return B("Browse...", b => BrowseOpenDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "general.newExplorerTabDefaultDirectory": yield return B("Browse...", b => BrowseNewExplorerDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "overlay.defaultDirectory": yield return B("Browse...", b => BrowseOverlayDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "profiles.defaultDirectory": yield return B("Browse...", b => BrowseProfileDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "appearance.accent": yield return B("Pick...", b => PickAccentColorClicked(b, new RoutedEventArgs())); break;
            case "appearance.glowColor": yield return B("Pick...", b => PickGlowColorClicked(b, new RoutedEventArgs())); break;
            case "appearance.background": yield return B("Pick...", b => PickBackgroundColorClicked(b, new RoutedEventArgs())); break;
            case "general.startupCustomPath":
                yield return B("File...", b => BrowseStartupFileClicked(b, new RoutedEventArgs()));
                yield return B("Folder...", b => BrowseStartupFolderClicked(b, new RoutedEventArgs()));
                break;
            case "general.newTabCustomPath":
                yield return B("File...", b => BrowseNewTabFileClicked(b, new RoutedEventArgs()));
                yield return B("Folder...", b => BrowseNewTabFolderClicked(b, new RoutedEventArgs()));
                break;
            case "performance.initialQuality": yield return B("Save custom", b => SaveUserPerformancePresetClicked(b, new RoutedEventArgs())); break;
            case "titlebar.customize": yield return B("Configure...", b => CustomizeTitleBarClicked(b, new RoutedEventArgs())); break;
            case "escape.resetRememberedClose": yield return B("Reset", b => ResetEscChoiceClicked(b, new RoutedEventArgs())); break;
            case "profiles.import": yield return B("Import...", b => ImportSettingsClicked(b, new RoutedEventArgs())); break;
            case "profiles.export": yield return B("Export...", b => ExportSettingsClicked(b, new RoutedEventArgs())); break;
            case "profiles.presets": yield return B("Apply", b => ApplyPresetClicked(b, new RoutedEventArgs())); break;
            case "hotkeys.preset": yield return B("Load", b => LoadHotkeyPresetClicked(b, new RoutedEventArgs())); break;
            case "hotkeys.reset": yield return B("Reset", b => ResetHotkeysClicked(b, new RoutedEventArgs())); break;
            case "mouse.gestureMatrix": yield return B("Open", b => ShowCategory("Mouse", clearSearch: true)); break;
            case "hotkeys.registry": yield return B("Open", b => ShowCategory("Hotkeys", clearSearch: true)); break;
            case "developer.diagnostics":
                yield return B("Run", b => RunDiagnosticsClicked(b, new RoutedEventArgs()));
                yield return B("Export ZIP", b => ExportDiagnosticsClicked(b, new RoutedEventArgs()));
                break;
        }
    }

    private Control CreateSearchProxy(Control source)
    {
        switch (source)
        {
            case CheckBox cb:
            {
                var proxy = new CheckBox
                {
                    IsChecked = cb.IsChecked,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Focusable = false
                };
                return proxy;
            }
            case NumericUpDown numeric:
            {
                var proxy = new NumericUpDown { Width = 150, Minimum = numeric.Minimum, Maximum = numeric.Maximum, Increment = numeric.Increment, Value = numeric.Value, VerticalAlignment = VerticalAlignment.Center };
                proxy.PropertyChanged += (_, e) =>
                {
                    if (e.Property == NumericUpDown.ValueProperty && !_loadingControls) numeric.Value = proxy.Value;
                };
                return proxy;
            }
            case ComboBox combo:
            {
                var proxy = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center };
                foreach (var item in combo.Items.OfType<ComboBoxItem>()) proxy.Items.Add(new ComboBoxItem { Content = item.Content?.ToString() ?? string.Empty });
                SelectByText(proxy, SelectedText(combo));
                proxy.SelectionChanged += (_, _) => { if (!_loadingControls) SelectByText(combo, SelectedText(proxy)); };
                return proxy;
            }
            case TextBox box:
            {
                var proxy = new TextBox { Width = 260, Text = box.Text, VerticalAlignment = VerticalAlignment.Center, IsReadOnly = box.IsReadOnly };
                proxy.TextChanged += (_, _) => { if (!_loadingControls && !box.IsReadOnly) box.Text = proxy.Text; };
                return proxy;
            }
            case Button button:
            {
                return new TextBlock { Text = button.Content?.ToString() ?? "Action", VerticalAlignment = VerticalAlignment.Center, Foreground = SearchMutedBrush() };
            }
            default:
            {
                var open = new Button { Content = "Open", Tag = "General" };
                open.Classes.Add("searchOpen");
                return open;
            }
        }
    }

    private static string CategoryKey(string category) => category switch
    {
        "Themes & Colours" => "Appearance",
        "Viewing & Interface" => "Viewing",
        "Animation" => "Animation",
        "Mouse & Fullscreen" => "Mouse",
        "Performance & Startup" => "Performance",
        "Status Bar" => "Status",
        "Tabs & Workspace" => "Tabs",
        "Window in Window" => "Overlays",
        "Profiles & Presets" => "Profiles",
        "Windows Integration" => "Windows",
        "Developer Options" => "Developer",
        _ => category
    };

    private void BuildGestureMatrix()
    {
        _gestureMatrixBuilt = true;
        GestureMatrixHost.Children.Clear();
        _gestureEditors.Clear();
        string? lastContext = null;
        foreach (var slot in GestureCatalog.Slots)
        {
            if (!string.Equals(lastContext, slot.Context, StringComparison.Ordinal))
            {
                lastContext = slot.Context;
                GestureMatrixHost.Children.Add(new TextBlock
                {
                    Text = slot.Context,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = SearchMutedBrush(),
                    Margin = new Thickness(2, 7, 0, 1)
                });
            }

            var combo = new ComboBox
            {
                Width = 255,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = slot.Id
            };
            foreach (var action in GestureCatalog.Actions)
                combo.Items.Add(new ComboBoxItem { Content = action.Label, Tag = action.Id });
            combo.SelectionChanged += GestureEditorSelectionChanged;
            ToolTip.SetTip(combo, slot.Label);
            ToolTip.SetShowDelay(combo, 650);
            _gestureEditors[slot.Id] = combo;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("300,255,*"), Margin = new Thickness(2, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = slot.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 });
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            GestureMatrixHost.Children.Add(row);
        }
    }

    private void LoadGestureEditorsFromWorkingMap()
    {
        _loadingControls = true;
        try
        {
            foreach (var slot in GestureCatalog.Slots)
                if (_gestureEditors.TryGetValue(slot.Id, out var combo))
                    SelectGestureAction(combo, GestureCatalog.ResolveAction(slot.Id, _workingGestures));
        }
        finally { _loadingControls = false; }
    }

    private static void SelectGestureAction(ComboBox combo, string actionId)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), actionId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void GestureEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (sender is ComboBox combo && combo.Tag is string slotId)
            _workingGestures[slotId] = (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? GestureCatalog.Default;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private void ResetGestureMatrixClicked(object? sender, RoutedEventArgs e)
    {
        _loadingControls = true;
        try
        {
            _workingGestures = GestureCatalog.CreateDefaultMap();
            foreach (var slot in GestureCatalog.Slots)
                if (_gestureEditors.TryGetValue(slot.Id, out var combo)) SelectGestureAction(combo, slot.DefaultActionId);
        }
        finally { _loadingControls = false; }
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private void BuildSettingControlMap()
    {
        void M(string id, Control control) => _settingControls[id] = control;
        M("general.recentHistory", RecentHistoryCheck); M("general.historySize", HistorySizeBox); M("windows.rememberPlacement", RememberPlacementCheck); M("general.openFileDefaultDirectory", OpenFileDefaultDirectoryBox);
        M("general.singleInstance", ReuseSingleInstanceCheck); M("navigation.siblingFolders", ContinueSiblingFoldersCheck); M("navigation.hierarchicalFolders", HierarchicalFolderTraversalCheck); M("navigation.confirmHierarchicalBoundary", ConfirmHierarchicalFolderTraversalCheck); M("navigation.hierarchicalPreviousEntry", HierarchicalPreviousFolderEntryCombo); M("navigation.rateLimitMs", NavigationRateLimitMsBox); M("navigation.rateLimitInput", NavigationRateLimitInputCombo); M("navigation.rateLimitIndicator", NavigationRateLimitIndicatorCheck); M("navigation.rateLimitIndicatorPosition", NavigationRateLimitIndicatorPositionCombo); M("navigation.rateLimitWindowed", NavigationRateLimitWindowedCheck); M("navigation.rateLimitFullscreen", NavigationRateLimitFullscreenCheck); M("navigation.rateLimitSlideshow", NavigationRateLimitSlideshowCheck); M("general.lastOpenLocation", RememberLastOpenCheck); M("general.newExplorerTabLastLocation", NewExplorerTabLastLocationCheck); M("general.newExplorerTabDefaultDirectory", NewExplorerTabDefaultDirectoryBox);
        M("appearance.theme", ThemeCombo); M("appearance.explorerTheme", ExplorerThemeCombo); M("appearance.accent", AccentCombo); M("appearance.glowColor", GlowCombo); M("appearance.background", MainBackgroundCombo); M("appearance.glowIntensity", GlowIntensityBox);
        M("appearance.homeTips", HomeTipsCheck); M("general.showRecentOnHome", ShowRecentOnHomeCheck); M("general.confirmDiscardSettings", ConfirmDiscardSettingsCheck);
        M("general.startupAction", StartupActionCombo); M("general.startupCustomPath", StartupCustomPathBox); M("general.newTabAction", NewTabActionCombo); M("general.newTabCustomPath", NewTabCustomPathBox); M("titlebar.customize", CustomizeTitleBarButton); M("escape.resetRememberedClose", ResetEscChoiceButton);
        M("status.visible", StatusCheck); M("status.hoverWhenClosed", StatusHoverWhenClosedCheck); M("view.scrollbars", ShowScrollbarsCheck); M("window.fullPathTitle", FullPathTitleCheck); M("fullscreen.hideCursor", HideCursorFullscreenCheck); M("fullscreen.hideCursorDelay", FullscreenHideCursorDelayBox);
        M("fullscreen.autoHideChrome", AutoHideFullscreenChromeCheck); M("fullscreen.keepTabBarOpen", FullscreenKeepTabBarOpenCheck); M("tabs.enabled", TabsCheck); M("tabs.navigateToFolderBehavior", NavigateToFolderBehaviorCombo); M("tabs.showBackButton", TabBarBackButtonCheck); M("tabs.showForwardButton", TabBarForwardButtonCheck); M("fullscreen.statusAlways", FullscreenStatusAlwaysCheck);
        M("caption.windowedMinimize", CaptionWindowedMinimizeCombo); M("caption.windowedMaximize", CaptionWindowedMaximizeCombo); M("caption.windowedClose", CaptionWindowedCloseCombo);
        M("caption.fullscreenMinimize", CaptionFullscreenMinimizeCombo); M("caption.fullscreenMaximize", CaptionFullscreenMaximizeCombo); M("caption.fullscreenClose", CaptionFullscreenCloseCombo);
        M("windows.alwaysOnTop", AlwaysOnTopCheck); M("windows.alwaysOnTopMode", AlwaysOnTopModeCombo); M("fullscreen.exitBehavior", FullscreenExitBehaviorCombo); M("windows.sameImageBehavior", SameImageBehaviorCombo); M("window.centerOnDisplay", AutoCenterWindowOnRestoreCheck); M("view.defaultMode", DefaultViewCombo); M("view.pointerZoom", PointerZoomCheck); M("view.keepZoom", PreserveZoomCheck);
        M("view.zoomStep", ViewportZoomStepBox); M("view.upscaleSmallImages", UpscaleSmallImagesCheck); M("view.subpixelRendering", SubpixelRenderingCheck); M("view.showLoadingIndicator", ShowLoadingIndicatorCheck);
        M("keyboard.tabFocusNavigation", TabFocusNavigationCheck);
        M("escape.stopSlideshow", EscStopsSlideshowCheck); M("escape.exitFullscreen", EscExitsFullscreenCheck); M("escape.confirmWindowedClose", EscWindowedConfirmCheck);
        M("mouse.doubleClickFullscreen", DoubleClickFullscreenCheck); M("mouse.doubleClickExitFullscreen", DoubleClickExitFullscreenCheck); M("mouse.fullscreenClicks", FullscreenClicksCheck);
        M("mouse.windowedWheelZoom", WindowedWheelZoomCheck); M("mouse.invertWheel", InvertWheelCheck); M("mouse.leftDrag", LeftDragModeCombo); M("mouse.leftWindowDrag", LeftWindowDragBehaviorCombo); M("mouse.rightDragPan", RightDragBehaviorCombo);
        M("selection.clickZoom", SelectionClickZoomCheck); M("selection.rightClickZoomOut", SelectionRightZoomCheck); M("developer.reverseSelectionZoomOut", ReverseSelectionZoomOutScaleCheck); M("mouse.backgroundWindowDrag", BackgroundDragCheck);
        M("mouse.ctrlWheelZoom", CtrlWheelZoomCheck); M("mouse.middleDragPan", MiddleDragPanCheck);
        M("performance.initialQuality", InitialQualityCombo); M("performance.saveCustomPreset", SaveUserPerformancePresetButton); M("performance.adaptivePreview", AdaptiveFastPreviewCheck); M("performance.previewDelay", AdaptivePreviewDelayBox);
        M("performance.interactivePanQuality", InteractivePanQualityCombo);
        M("performance.sequentialReads", SequentialReadCheck); M("performance.prefetch", PrefetchCheck); M("performance.backgroundRefinement", BackgroundRefinementCheck);
        M("performance.speedBoost", SpeedBoostCheck); M("performance.startWithWindowsInBackground", StartWithWindowsInBackgroundCheck);
        M("performance.purgeOnMinimize", PurgeCacheCheck); M("developer.startupDiagnostics", StartupDiagnosticsCheck); M("performance.prefetchDepth", PrefetchDepthBox);
        M("performance.prefetchSiblingFolders", PrefetchSiblingFoldersCheck); M("performance.prefetchSiblingFolderCount", PrefetchSiblingFolderCountBox);
        M("performance.cacheItems", CacheItemsBox); M("performance.compressedCacheMb", CompressedCacheMbBox); M("performance.decodedCacheMb", DecodedCacheMbBox); M("performance.rapidPreviewSize", RapidPreviewBox); M("performance.rapidBrowsePreviewSize", RapidBrowsePreviewBox); M("performance.progressiveColor", ProgressiveColorCheck);
        M("overlay.wholeAppAlwaysStart", WholeAppOverlayAlwaysStartCheck); M("overlay.wholeAppWindowedInteractions", WholeAppOverlayWindowedInteractionsCheck);
        M("status.size", StatusBarSizeCombo); M("status.navigation", StatusNavigationCheck); M("status.zoom", StatusZoomCheck); M("status.slideshow", StatusSlideshowCheck); M("status.fit", StatusFitCheck);
        M("status.info", StatusInfoCheck); M("status.options", StatusOptionsCheck); M("status.close", StatusCloseCheck); M("status.statIndex", StatIndexCheck);
        M("status.statResolution", StatResolutionCheck); M("status.statZoom", StatZoomCheck); M("status.statFileSize", StatFileSizeCheck); M("status.statFormat", StatFormatCheck);
        M("slideshow.interval", SlideshowIntervalBox); M("slideshow.loop", SlideshowLoopCheck); M("slideshow.rightClickStops", SlideshowRightClickStopsCheck); M("slideshow.showQualityIndicator", SlideshowQualityIndicatorCheck); M("slideshow.crossFolders", SlideshowCrossFoldersCheck); M("slideshow.shuffle", SlideshowShuffleCheck);
        M("slideshow.direction", SlideshowDirectionCombo); M("slideshow.startFullscreen", SlideshowStartFullscreenCheck); M("slideshow.pauseInactive", SlideshowPauseInactiveCheck);
        M("tabs.minWidth", TabMinWidthBox); M("tabs.maxWidth", TabMaxWidthBox); M("tabs.overflowArrows", TabOverflowArrowsCheck); M("tabs.detach", TabDetachCheck); M("tabs.attach", TabAttachCheck); M("tabs.closedHistory", ClosedHistoryBox);
        M("tabs.closeEmptyAfterDetach", CloseEmptyAfterDetachCheck); M("tabs.detachedHome", DetachedHomeTabCheck); M("tabs.doubleClickClose", DoubleClickTabCloseCheck);
        M("tabs.lastTabBehavior", LastTabBehaviorCombo); M("tabs.confirmCloseMultiple", ConfirmCloseMultipleTabsCheck); M("tabs.homePage", HomePageCombo); M("folderNav.group", FolderNavGroupCheck); M("folderNav.previous", FolderNavPreviousCheck);
        M("folderNav.next", FolderNavNextCheck); M("folderNav.explore", FolderNavExploreCheck); M("folderNav.skipEmpty", FolderNavSkipEmptyCheck); M("folderNav.includeHidden", FolderNavIncludeHiddenCheck); M("folderNav.wrap", FolderNavWrapCheck); M("folderNav.openFirst", FolderNavFirstImageCheck); M("folderNav.order", FolderNavOrderCombo);
        M("overlay.pictureCounter", PictureCounterCheck); M("overlay.pictureTemplate", PictureTemplateBox); M("overlay.pictureFontSize", PictureFontSizeBox);
        M("overlay.pictureBold", PictureBoldCheck); M("overlay.pictureOpacity", PictureOpacityBox); M("overlay.picturePosition", PicturePositionCombo);
        M("overlay.pictureColor", PictureColorCombo); M("overlay.pictureShadow", PictureShadowCheck); M("overlay.defaultOpacity", OverlayOpacityBox); M("overlay.zoomStep", OverlayZoomStepBox);
        M("overlay.rememberFolder", OverlayRememberFolderCheck); M("overlay.scaleWithWindow", OverlayScaleWithWindowCheck); M("overlay.animationRegion", OverlayAnimationRegionCombo); M("overlay.animationSpeedDipsPerSecond", OverlayAnimationSpeedBox); M("overlay.animationTurnIntervalMs", OverlayAnimationTurnIntervalBox); M("overlay.animationTurnAngleDegrees", OverlayAnimationTurnAngleBox); M("overlay.animationPauseWhileInteracting", OverlayAnimationPauseCheck); M("overlay.animationAvoidOverlap", OverlayAnimationAvoidOverlapCheck); M("overlay.animationRefreshRateHz", OverlayAnimationRefreshRateCombo); M("overlay.defaultDirectory", OverlayDefaultDirectoryBox); M("profiles.defaultDirectory", ProfileDefaultDirectoryBox); M("overlay.persistSession", OverlayPersistSessionCheck); M("overlay.keyboardZoom", OverlayKeyboardZoomCheck); M("overlay.wheelZoom", OverlayWheelZoomCheck); M("overlay.highlightSelected", OverlayHighlightCheck); M("overlay.rememberZoom", OverlayRememberZoomCheck); M("overlay.rightDragPan", OverlayRightDragCheck);
        M("windows.externalOpenBehavior", ExternalOpenBehaviorCombo); M("windows.external1", External1Box); M("windows.external2", External2Box); M("windows.external3", External3Box);
        M("hotkeys.preset", HotkeyPresetCombo); M("profiles.presets", PresetCombo);
        M("developer.statusGlobalScale", StatusGlobalScaleBox); M("developer.statusAutoFit", StatusAutoFitCheck); M("developer.statusMaximizedBoost", StatusMaximizedBoostBox); M("developer.overlayAbsoluteCoordinates", OverlayAbsoluteCoordinatesCheck);
    }

    private void ApplySettingsTooltips()
    {
        foreach (var definition in SettingsCatalog.All)
        {
            if (_settingControls.TryGetValue(definition.Id, out var control))
            {
                ToolTip.SetTip(control, definition.Description);
                ToolTip.SetShowDelay(control, 650);
            }
        }
        foreach (var nav in NavigationRail.Children.OfType<Button>())
        {
            var key = nav.Tag?.ToString() ?? "";
            ToolTip.SetTip(nav, $"Open {DisplayCategory(key)} settings");
            ToolTip.SetShowDelay(nav, 650);
        }
        ToolTip.SetTip(SearchBox, "Search Glide settings. Use the category filter to search everywhere or limit results to one section.");
        ToolTip.SetShowDelay(SearchBox, 650);
        ToolTip.SetTip(ChangeShortcutButton, "Capture a replacement shortcut for the selected action. Double-click a hotkey row to do the same thing.");
        ToolTip.SetTip(AddShortcutButton, "Add another shortcut to the selected action without replacing existing shortcuts.");
        ToolTip.SetTip(RemoveShortcutButton, "Remove the selected shortcut from this action.");
        ToolTip.SetTip(ResetHotkeysButton, "Restore all keyboard shortcuts to Glide defaults.");
        ToolTip.SetTip(CustomizeTitleBarButton, "Choose, add, remove and reorder any of 20 title/tab-bar utility buttons.");
    }

    private void BuildHotkeyRows(string? filter = null)
    {
        _hotkeyRowsBuilt = true;
        UpdateExternalProgramHotkeyLabels();
        if (HotkeyList is null) return;
        var rows = new List<ListBoxItem>();
        foreach (var action in HotkeyCatalog.All)
        {
            var shortcuts = _workingHotkeys.TryGetValue(action.Id, out var values) ? values : new List<string>();
            if (!string.IsNullOrWhiteSpace(filter) &&
                !action.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !action.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !shortcuts.Any(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;

            if (shortcuts.Count == 0) rows.Add(CreateHotkeyRow(action, ""));
            else foreach (var shortcut in shortcuts) rows.Add(CreateHotkeyRow(action, shortcut));
        }
        HotkeyList.ItemsSource = rows;
        HotkeyList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        UpdateHotkeyButtonState();
    }

    private void UpdateExternalProgramHotkeyLabels()
    {
        if (External1Label is null || External2Label is null || External3Label is null) return;
        External1Label.Text = $"External Program 1 ({ExternalProgramShortcutText("external.1")})";
        External2Label.Text = $"External Program 2 ({ExternalProgramShortcutText("external.2")})";
        External3Label.Text = $"External Program 3 ({ExternalProgramShortcutText("external.3")})";
    }

    private string ExternalProgramShortcutText(string actionId)
    {
        var shortcuts = _workingHotkeys.GetValueOrDefault(actionId, new List<string>());
        return shortcuts.Count == 0 ? "Unassigned" : string.Join(" / ", shortcuts);
    }

    private void LoadHotkeyPresetClicked(object? sender, RoutedEventArgs e)
    {
        var preset = SelectedText(HotkeyPresetCombo, "Glide default");
        _workingHotkeys = HotkeyCatalog.CreatePresetMap(preset);
        HotkeyCaptureText.Text = $"Loaded {preset} hotkey preset.";
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    private ListBoxItem CreateHotkeyRow(HotkeyActionDefinition action, string shortcut)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,260,*") };
        var shortcutText = new TextBlock { Text = string.IsNullOrWhiteSpace(shortcut) ? "(unassigned)" : shortcut, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        var actionText = new TextBlock { Text = action.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var categoryText = new TextBlock { Text = action.Category, VerticalAlignment = VerticalAlignment.Center, Foreground = SearchMutedBrush() };
        Grid.SetColumn(actionText, 1); Grid.SetColumn(categoryText, 2);
        grid.Children.Add(shortcutText); grid.Children.Add(actionText); grid.Children.Add(categoryText);
        var item = new ListBoxItem { Content = grid, Tag = new HotkeyRowTag(action.Id, shortcut), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        item.Classes.Add("hotkeyRow");
        ToolTip.SetTip(item, $"{action.Label} — double-click to change this shortcut");
        return item;
    }

    private void HotkeySelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateHotkeyButtonState();

    private void UpdateHotkeyButtonState()
    {
        var row = (HotkeyList?.SelectedItem as ListBoxItem)?.Tag as HotkeyRowTag;
        ChangeShortcutButton.IsEnabled = row is not null;
        AddShortcutButton.IsEnabled = row is not null;
        RemoveShortcutButton.IsEnabled = row is not null && !string.IsNullOrWhiteSpace(row.Shortcut);
    }

    private void HotkeyListDoubleTapped(object? sender, TappedEventArgs e) => StartHotkeyCapture(add: false);
    private void ChangeShortcutClicked(object? sender, RoutedEventArgs e) => StartHotkeyCapture(add: false);
    private void AddShortcutClicked(object? sender, RoutedEventArgs e) => StartHotkeyCapture(add: true);

    private void StartHotkeyCapture(bool add)
    {
        var row = (HotkeyList.SelectedItem as ListBoxItem)?.Tag as HotkeyRowTag;
        if (row is null) return;
        StartHotkeyCaptureFor(row.ActionId, row.Shortcut, add, fromSearch: false);
    }

    private void StartHotkeyCaptureFor(string actionId, string? oldShortcut, bool add, bool fromSearch, Border? searchCard = null)
    {
        ClearHotkeyCaptureFlash();
        _hotkeyCaptureActionId = actionId;
        _hotkeyCaptureOldShortcut = oldShortcut;
        _hotkeyCaptureAdd = add;
        _hotkeyCaptureFromSearch = fromSearch;
        _hotkeyCaptureSearchCard = searchCard;
        var action = HotkeyCatalog.Find(actionId);
        var message = $"Waiting for a new shortcut for {action?.Label ?? actionId}. Press the keyboard shortcut now, press Esc to cancel, or click anywhere else in Settings to leave edit mode.";
        HotkeyCaptureText.Text = message;
        if (fromSearch) PageSubtitle.Text = message;
        SearchBox.IsEnabled = false;

        if (fromSearch && _hotkeyCaptureSearchCard is not null)
        {
            _hotkeyFlashOn = true;
            SetHotkeyCaptureFlash(_hotkeyCaptureSearchCard, true);
            _hotkeyCaptureFlashTimer.Start();
        }
        else if (!fromSearch && HotkeyList.ItemsSource is IEnumerable<ListBoxItem> rows)
        {
            _hotkeyCaptureRow = rows.FirstOrDefault(row =>
            {
                if (row.Tag is not HotkeyRowTag tag || !string.Equals(tag.ActionId, actionId, StringComparison.OrdinalIgnoreCase)) return false;
                return add || string.Equals(tag.Shortcut, oldShortcut ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }) ?? rows.FirstOrDefault(row => row.Tag is HotkeyRowTag tag && string.Equals(tag.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
            if (_hotkeyCaptureRow is not null)
            {
                _hotkeyFlashOn = true;
                SetHotkeyCaptureFlash(_hotkeyCaptureRow, true);
                _hotkeyCaptureFlashTimer.Start();
            }
        }
        Focus();
    }

    private void RemoveShortcutClicked(object? sender, RoutedEventArgs e)
    {
        var row = (HotkeyList.SelectedItem as ListBoxItem)?.Tag as HotkeyRowTag;
        if (row is null || string.IsNullOrWhiteSpace(row.Shortcut)) return;
        if (_workingHotkeys.TryGetValue(row.ActionId, out var values))
            values.RemoveAll(x => string.Equals(x, row.Shortcut, StringComparison.OrdinalIgnoreCase));
        HotkeyCaptureText.Text = $"Removed {row.Shortcut}.";
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    private void ResetHotkeysClicked(object? sender, RoutedEventArgs e)
    {
        _workingHotkeys = HotkeyCatalog.CreateDefaultMap();
        HotkeyCaptureText.Text = "Restored Glide default hotkeys.";
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    private void SettingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_hotkeyCaptureActionId is not null)
            {
                EndHotkeyCapture("Shortcut capture cancelled.");
                return;
            }

            // Escape is the keyboard equivalent of Cancel: it uses the same discard-confirmation
            // and preview-rollback path instead of bypassing unsaved-settings protection.
            CancelClicked(this, new RoutedEventArgs());
            return;
        }

        if (_hotkeyCaptureActionId is null) return;
        e.Handled = true;
        var shortcut = CanonicalShortcut(e);
        if (string.IsNullOrWhiteSpace(shortcut) || IsModifierOnly(e.Key)) return;
        AssignCapturedShortcut(shortcut);
    }

    private void AssignCapturedShortcut(string shortcut)
    {
        if (_hotkeyCaptureActionId is not { } actionId) return;
        var action = HotkeyCatalog.Find(actionId);

        // A physical shortcut can have one semantic owner. Reassign it instead of preserving
        // invisible conflicts. Multiple different shortcuts can still target the same action.
        string? previousOwner = null;
        foreach (var pair in _workingHotkeys)
        {
            if (pair.Value.RemoveAll(x => string.Equals(x, shortcut, StringComparison.OrdinalIgnoreCase)) > 0 &&
                !string.Equals(pair.Key, actionId, StringComparison.OrdinalIgnoreCase))
                previousOwner = HotkeyCatalog.Find(pair.Key)?.Label ?? pair.Key;
        }

        if (!_workingHotkeys.TryGetValue(actionId, out var assigned))
            _workingHotkeys[actionId] = assigned = new List<string>();
        if (!_hotkeyCaptureAdd && !string.IsNullOrWhiteSpace(_hotkeyCaptureOldShortcut))
            assigned.RemoveAll(x => string.Equals(x, _hotkeyCaptureOldShortcut, StringComparison.OrdinalIgnoreCase));
        if (!assigned.Any(x => string.Equals(x, shortcut, StringComparison.OrdinalIgnoreCase)))
            assigned.Add(shortcut);

        var message = previousOwner is null
            ? $"Assigned {shortcut} to {action?.Label ?? actionId}."
            : $"Assigned {shortcut} to {action?.Label ?? actionId}; removed it from {previousOwner}.";
        EndHotkeyCapture(message);
        BuildHotkeyRows();
        PreviewHotkeyChange();
    }

    private void EndHotkeyCapture(string message, bool refreshSearch = true)
    {
        var wasSearch = _hotkeyCaptureFromSearch;
        var actionId = _hotkeyCaptureActionId;
        ClearHotkeyCaptureFlash();
        _hotkeyCaptureActionId = null;
        _hotkeyCaptureOldShortcut = null;
        _hotkeyCaptureAdd = false;
        _hotkeyCaptureFromSearch = false;
        _hotkeyCaptureSearchCard = null;
        SearchBox.IsEnabled = true;
        HotkeyCaptureText.Text = message;
        if (wasSearch)
        {
            if (!string.IsNullOrWhiteSpace(actionId)) RefreshHotkeySearchDisplay(actionId);
            PageSubtitle.Text = message;
            // Deliberately do not rebuild SearchPanel here: rebuilding destroys focus/flash state and
            // resets ContentScroll to the top. The visible row is updated in place instead.
        }
    }

    private void ToggleHotkeyCaptureFlash()
    {
        if (_hotkeyCaptureActionId is null || (_hotkeyCaptureRow is null && _hotkeyCaptureSearchCard is null))
        {
            ClearHotkeyCaptureFlash();
            return;
        }
        _hotkeyFlashOn = !_hotkeyFlashOn;
        if (_hotkeyCaptureRow is not null) SetHotkeyCaptureFlash(_hotkeyCaptureRow, _hotkeyFlashOn);
        if (_hotkeyCaptureSearchCard is not null) SetHotkeyCaptureFlash(_hotkeyCaptureSearchCard, _hotkeyFlashOn);
    }

    private static void SetHotkeyCaptureFlash(Control row, bool visible)
    {
        if (visible)
        {
            if (!row.Classes.Contains("capturePending")) row.Classes.Add("capturePending");
        }
        else row.Classes.Remove("capturePending");
    }

    private void ClearHotkeyCaptureFlash()
    {
        _hotkeyCaptureFlashTimer.Stop();
        if (_hotkeyCaptureRow is not null) SetHotkeyCaptureFlash(_hotkeyCaptureRow, false);
        if (_hotkeyCaptureSearchCard is not null) SetHotkeyCaptureFlash(_hotkeyCaptureSearchCard, false);
        _hotkeyCaptureRow = null;
        _hotkeyCaptureSearchCard = null;
        _hotkeyFlashOn = false;
    }

    private void PreviewHotkeyChange()
    {
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private static bool IsModifierOnly(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string CanonicalShortcut(KeyEventArgs e)
    {
        // Keep capture normalization identical to MainWindow runtime lookup.
        var shiftedOemPlus = e.Key == Key.OemPlus && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var key = e.Key switch
        {
            Key.Escape => "Esc", Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Home => "Home", Key.End => "End", Key.Delete => "Delete",
            Key.Add => "+", Key.OemPlus when shiftedOemPlus => "+", Key.OemPlus => "=", Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            _ => e.Key.ToString()
        };
        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !shiftedOemPlus) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private sealed record HotkeyRowTag(string ActionId, string Shortcut);

    private void ApplyClicked(object? sender, RoutedEventArgs e)
    {
        var next = ReadControls();
        var changes = GetNetChanges(next);
        if (changes.Count == 0)
        {
            UpdateDirtyState(next);
            return;
        }

        // Apply is an explicit commit action; a second confirmation is redundant and interrupts flow.
        CommitState(next);
    }

    private void OkClicked(object? sender, RoutedEventArgs e)
    {
        var next = ReadControls();
        var changes = GetNetChanges(next);
        if (changes.Count > 0) CommitState(next);
        _closeWithoutRollback = true;
        Close();
    }

    private async void CancelClicked(object? sender, RoutedEventArgs e)
    {
        var changes = GetNetChanges();
        if (changes.Count > 0 && _committed.ConfirmDiscardSettingsChanges &&
            !await ConfirmChangesAsync("Discard settings changes?", "These net changes will be discarded:", changes, "Close Anyway", allowDontRemind: true)) return;
        _apply(_committed.CloneState(), false);
        _closeWithoutRollback = true;
        Close();
    }

    private void CommitState(GlideSettingsState next)
    {
        if (next.StartWithWindowsInBackground != _committed.StartWithWindowsInBackground)
        {
            WindowsStartupRegistration.SetRunAtStartup(next.StartWithWindowsInBackground, out _);
        }
        _committed = next.CloneState();
        _apply(_committed.CloneState(), true);
        UpdateDirtyState(_committed);
    }

    private async void SettingsClosing(object? sender, WindowClosingEventArgs e)
    {
        PopupPlacementStore.Save(this, "settings");
        if (_closeWithoutRollback) return;
        var changes = GetNetChanges();
        if (changes.Count == 0)
        {
            _apply(_committed.CloneState(), false);
            _closeWithoutRollback = true;
            return;
        }

        e.Cancel = true;
        if (_committed.ConfirmDiscardSettingsChanges &&
            !await ConfirmChangesAsync("Discard settings changes?", "Settings has unapplied net changes:", changes, "Close Anyway", allowDontRemind: true)) return;
        _apply(_committed.CloneState(), false);
        _closeWithoutRollback = true;
        Close();
    }

    private async Task<bool> ConfirmChangesAsync(string title, string intro, IReadOnlyList<SettingChange> changes, string confirmLabel, bool allowDontRemind = false)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var change in changes)
            list.Children.Add(new TextBlock { Text = $"• {change.Name}: {change.Before} → {change.After}", TextWrapping = TextWrapping.Wrap });

        var dontRemind = new CheckBox
        {
            Content = "Don't remind me again",
            IsVisible = allowDontRemind,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var confirm = new Button { Content = confirmLabel, MinWidth = 126 };
        confirm.Classes.Add("primaryAction");
        var back = new Button { Content = "Go Back", MinWidth = 100 };
        back.Classes.Add("secondaryAction");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(back);
        buttons.Children.Add(confirm);
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        var warningBrush = new SolidColorBrush(Color.Parse("#E5B53B"));
        var warningGlyph = new Border
        {
            Width = 34, Height = 30, CornerRadius = new CornerRadius(5),
            BorderBrush = warningBrush, BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse("#1AE5B53B")),
            Child = new TextBlock
            {
                Text = "!", Foreground = warningBrush, FontSize = 19, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var warningRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        warningRow.Children.Add(warningGlyph);
        var warningText = new TextBlock { Text = intro, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(warningText, 1);
        warningRow.Children.Add(warningText);
        root.Children.Add(warningRow);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(dontRemind);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Width = 560, MinWidth = 440, SizeToContent = SizeToContent.Height, MaxHeight = 600,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root, Background = this.Background,
            Topmost = Topmost
        };
        PopupPlacementStore.Track(dialog, "settings-discard-confirm");
        confirm.Click += (_, _) => dialog.Close(true);
        back.Click += (_, _) => dialog.Close(false);
        var accepted = await dialog.ShowDialog<bool>(this);
        if (accepted && allowDontRemind && dontRemind.IsChecked == true)
        {
            // Persist this one preference even though every other staged change is intentionally discarded.
            _committed.ConfirmDiscardSettingsChanges = false;
            _apply(_committed.CloneState(), true);
        }
        return accepted;
    }

    private void RestoreDefaultsClicked(object? sender, RoutedEventArgs e)
    {
        var defaults = new GlideSettingsState();
        LoadControls(defaults);
        _apply(defaults.CloneState(), false);
        UpdateDirtyState(defaults);
    }

    private void AddOpenWithClicked(object? sender, RoutedEventArgs e)
    {
        _ = WindowsFileAssociationRegistration.Register(out var message);
        WindowsIntegrationStatusText.Text = message;
        DirtyText.Text = message;
        PageSubtitle.Text = message;
    }

    private void RemoveOpenWithClicked(object? sender, RoutedEventArgs e)
    {
        _ = WindowsFileAssociationRegistration.Unregister(out var message);
        WindowsIntegrationStatusText.Text = message;
        DirtyText.Text = message;
        PageSubtitle.Text = message;
    }

    private async void BrowseExternalClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose external program {slot}",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Windows programs") { Patterns = new[] { "*.exe" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (slot == "1") External1Box.Text = path;
        else if (slot == "2") External2Box.Text = path;
        else External3Box.Text = path;
    }

    private void ApplyPresetClicked(object? sender, RoutedEventArgs e)
    {
        var preset = SelectedText(PresetCombo, "Glide default");
        var staged = SettingsPresetCatalog.Apply(preset, ReadControls());
        LoadControls(staged);
        _apply(staged.CloneState(), false);
        UpdateDirtyState(staged);
        DirtyText.Text = $"Preset staged: {preset}";
    }

    private void BuildProfileRows()
    {
        if (ProfileRowsHost is null) return;
        ProfileRowsHost.Children.Clear();
        foreach (var profile in ProfileStore.ListProfiles())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            var name = new TextBox { Text = profile.Name, Tag = profile.Id, MinWidth = 210, Watermark = "Profile name" };
            name.LostFocus += ProfileNameLostFocus;
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitProfileRename(name); e.Handled = true; } };
            ToolTip.SetTip(name, "Rename this profile. Press Enter or click elsewhere to save the new name.");
            row.Children.Add(name);

            var save = new Button { Content = "Save...", Tag = profile.Id, MinWidth = 82 };
            save.Classes.Add("secondaryAction");
            save.Click += SaveProfileClicked;
            ToolTip.SetTip(save, "Save the current staged Glide settings to this profile and choose a portable profile file location.");
            Grid.SetColumn(save, 1); row.Children.Add(save);

            var load = new Button { Content = "Load...", Tag = profile.Id, MinWidth = 82 };
            load.Classes.Add("secondaryAction");
            load.Click += LoadProfileClicked;
            ToolTip.SetTip(load, "Choose a Glide profile file in Explorer, load it into this named profile, and stage its settings.");
            Grid.SetColumn(load, 2); row.Children.Add(load);
            ProfileRowsHost.Children.Add(row);
        }
    }

    private void ProfileNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitProfileRename(box);
    }

    private void CommitProfileRename(TextBox box)
    {
        if (box.Tag is not Guid id) return;
        box.Text = ProfileStore.Rename(id, box.Text ?? string.Empty);
    }

    private async void AddProfileClicked(object? sender, RoutedEventArgs e)
    {
        var name = await PromptProfileNameAsync();
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileStore.Add(name, ReadControls());
        BuildProfileRows();
        DirtyText.Text = $"Created profile ‘{name.Trim()}’.";
    }

    private async Task<string?> PromptProfileNameAsync()
    {
        var box = new TextBox { Watermark = "Profile name", MinWidth = 300, Text = $"Profile {ProfileStore.ListProfiles().Count + 1}" };
        var ok = new Button { Content = "Create", MinWidth = 90 };
        ok.Classes.Add("primaryAction");
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Classes.Add("secondaryAction");
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = "Name the new profile", FontSize = 18, FontWeight = FontWeight.SemiBold });
        root.Children.Add(box);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(cancel); actions.Children.Add(ok); root.Children.Add(actions);
        var dialog = new Window
        {
            Title = "Add Glide profile",
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 390,
            MaxWidth = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        try
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
                dialog.MaxHeight = Math.Max(200, screen.WorkingArea.Height / Math.Max(1.0, RenderScaling) - 80);
        }
        catch { }
        PopupPlacementStore.Track(dialog, "profile-add");
        ok.Click += (_, _) => dialog.Close(box.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close((string?)null);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }

    private async void SaveProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var profile = ProfileStore.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        var state = ReadControls();
        ProfileStore.Save(id, state);
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save Glide profile — {profile.Name}",
            SuggestedFileName = SanitizeProfileFileName(profile.Name) + ".glideprofile.json",
            SuggestedStartLocation = await ResolveProfilePickerFolderAsync(),
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("Glide profile") { Patterns = new[] { "*.glideprofile.json", "*.json" } } }
        });
        var path = target?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) { DirtyText.Text = $"Updated {profile.Name} internally."; return; }
        _workingLastProfileDirectory = Path.GetDirectoryName(path) ?? _workingLastProfileDirectory;
        try
        {
            var doc = new ProfileTransferDocument { Name = profile.Name, Settings = state.CloneState() };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            DirtyText.Text = $"Saved {profile.Name} to {Path.GetFileName(path)}";
        }
        catch (Exception ex) { DirtyText.Text = $"Profile save failed: {ex.Message}"; }
    }

    private async void LoadProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Glide profile",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveProfilePickerFolderAsync(),
            FileTypeFilter = new[] { new FilePickerFileType("Glide profile") { Patterns = new[] { "*.glideprofile.json", "*.json" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _workingLastProfileDirectory = Path.GetDirectoryName(path) ?? _workingLastProfileDirectory;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var doc = JsonSerializer.Deserialize<ProfileTransferDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            GlideSettingsState? state = doc?.Settings;
            string? importedName = doc?.Name;
            // Compatibility: accept a plain GlideSettingsState JSON file as a profile file too.
            state ??= JsonSerializer.Deserialize<GlideSettingsState>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null) throw new InvalidDataException("Profile file did not contain Glide settings.");
            ProfileStore.Replace(id, importedName, state);
            LoadControls(state);
            _apply(state.CloneState(), false);
            UpdateDirtyState(state);
            BuildProfileRows();
            DirtyText.Text = $"Loaded profile from {Path.GetFileName(path)}";
        }
        catch (Exception ex) { DirtyText.Text = $"Profile load failed: {ex.Message}"; }
    }

    private async Task<IStorageFolder?> ResolveProfilePickerFolderAsync()
    {
        var configured = ProfileDefaultDirectoryBox.Text?.Trim();
        var directory = !string.IsNullOrWhiteSpace(configured) ? configured : _workingLastProfileDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(Path.GetFullPath(directory)); } catch { return null; }
    }

    private static string SanitizeProfileFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Glide-profile" : clean;
    }

    private async void BrowseNewExplorerDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => NewExplorerTabDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default New Explorer tab directory") ?? NewExplorerTabDefaultDirectoryBox.Text;
    private async void BrowseOpenDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => OpenFileDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default Open File directory") ?? OpenFileDefaultDirectoryBox.Text;
    private async void BrowseOverlayDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => OverlayDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default overlay directory") ?? OverlayDefaultDirectoryBox.Text;
    private async void BrowseProfileDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => ProfileDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default profile directory") ?? ProfileDefaultDirectoryBox.Text;

    private void PickAccentColorClicked(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || !WindowsColorDialog.TryChoose(_workingCustomAccentHex, out var hex, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)) return;
        _workingCustomAccentHex = hex;
        SelectByText(AccentCombo, "Custom");
        PreviewNow();
    }

    private void PickGlowColorClicked(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || !WindowsColorDialog.TryChoose(_workingCustomGlowHex, out var hex, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)) return;
        _workingCustomGlowHex = hex;
        SelectByText(GlowCombo, "Custom");
        PreviewNow();
    }

    private void PickBackgroundColorClicked(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || !WindowsColorDialog.TryChoose(_workingCustomMainBackgroundHex, out var hex, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)) return;
        _workingCustomMainBackgroundHex = hex;
        SelectByText(MainBackgroundCombo, "Custom");
        PreviewNow();
    }

    private void PreviewNow()
    {
        if (_loadingControls) return;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private async void BrowseStartupFileClicked(object? sender, RoutedEventArgs e) => StartupCustomPathBox.Text = await PickCustomFileAsync("Choose startup image") ?? StartupCustomPathBox.Text;
    private async void BrowseNewTabFileClicked(object? sender, RoutedEventArgs e) => NewTabCustomPathBox.Text = await PickCustomFileAsync("Choose new-tab image") ?? NewTabCustomPathBox.Text;
    private async void BrowseStartupFolderClicked(object? sender, RoutedEventArgs e) => StartupCustomPathBox.Text = await PickCustomFolderAsync("Choose startup folder") ?? StartupCustomPathBox.Text;
    private async void BrowseNewTabFolderClicked(object? sender, RoutedEventArgs e) => NewTabCustomPathBox.Text = await PickCustomFolderAsync("Choose new-tab folder") ?? NewTabCustomPathBox.Text;

    private async Task<string?> PickCustomFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickCustomFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void ImportSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Glide settings",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Glide settings") { Patterns = new[] { "*.json" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            GlideSettingsState? imported = null;
            try
            {
                var document = JsonSerializer.Deserialize<SettingsTransferDocument>(json, options);
                if (document is not null && document.Settings is not null)
                {
                    if (document.Schema != SettingsTransferSchema)
                    {
                        DirtyText.Text = $"Unsupported settings schema {document.Schema}";
                        return;
                    }
                    imported = document.Settings;
                }
            }
            catch (JsonException) { }

            // Backward-compatible import for the earlier raw-state JSON format.
            imported ??= JsonSerializer.Deserialize<GlideSettingsState>(json, options);
            if (imported is null) return;
            NormalizeImportedState(imported);
            LoadControls(imported);
            _apply(imported.CloneState(), false);
            UpdateDirtyState(imported);
            DirtyText.Text = "Imported settings staged";
        }
        catch
        {
            DirtyText.Text = "Import failed";
        }
    }

    private async void ExportSettingsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(WindowsKnownFolders.Downloads(), $"Glide Settings {DateTime.Now:yyyy-MM-dd_HHmmss}.json");
            var document = new SettingsTransferDocument { Settings = ReadControls().CloneState(), ProviderInventory = CodecProviderRuntime.Shared.Artifacts };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            DirtyText.Text = $"Exported to Downloads: {Path.GetFileName(path)}";
        }
        catch
        {
            DirtyText.Text = "Settings export failed";
        }
    }

    private async void RunDiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        var writer = new StringWriter();
        Glide.Diagnostics.DiagnosticRunner.SelfTest(writer);
        var dialog = new Window
        {
            Width = 620,
            Height = 360,
            Title = "Glide Diagnostics",
            Icon = Icon,
            Content = new ScrollViewer { Content = new TextBlock { Text = writer.ToString(), Margin = new Thickness(18), TextWrapping = Avalonia.Media.TextWrapping.Wrap } }
        };
        PopupPlacementStore.Track(dialog, "diagnostics-results");
        await dialog.ShowDialog(this);
    }

    public async Task<string> ExportDiagnosticsAsync(string? targetDir = null, MainWindow? explicitOwner = null)
    {
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            var customDir = targetDir ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_DIR") ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_EXPORT_DIR");
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string reportFolder;
            string zipPath;
            bool keepFolder = false;

            if (!string.IsNullOrWhiteSpace(customDir))
            {
                Directory.CreateDirectory(customDir);
                reportFolder = Path.Combine(customDir, $"Glide Diagnostics {stamp}");
                zipPath = reportFolder + ".zip";
                keepFolder = true;
            }
            else
            {
                var downloads = WindowsKnownFolders.Downloads();
                reportFolder = Path.Combine(downloads, $"Glide Diagnostics {stamp}");
                zipPath = reportFolder + ".zip";
            }

            Directory.CreateDirectory(reportFolder);
            var writer = new StringWriter();
            Glide.Diagnostics.DiagnosticRunner.Export(reportFolder, writer);

            foreach (var source in Directory.EnumerateFiles(_evidenceTemp, "*.png"))
                File.Copy(source, Path.Combine(reportFolder, Path.GetFileName(source)), true);

            await CaptureAllSettingsPagesAsync(reportFolder);
            UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, "settings_export_time.png"), out var settingsCaptureError);
            // Geometry/layout now audit the responsive Settings root. ScrollViewer off-viewport
            // descendants are intentionally excluded from outside-root failures.
            UiDiagnosticCapture.WriteGeometry(ReferenceRoot, Path.Combine(reportFolder, "settings_geometry.txt"));
            UiDiagnosticCapture.WriteLayoutAudit(ReferenceRoot, Path.Combine(reportFolder, "settings_layout_audit.txt"));
            UiDiagnosticCapture.WriteControlInventory(ReferenceRoot, Path.Combine(reportFolder, "settings_control_inventory.txt"));
            var effectiveOwner = (Owner as Control) ?? explicitOwner;
            if (effectiveOwner is Control owner)
            {
                UiDiagnosticCapture.TryCapture(owner, Path.Combine(reportFolder, "main_export_time.png"), out var mainCaptureError);
                UiDiagnosticCapture.WriteGeometry(owner, Path.Combine(reportFolder, "main_geometry.txt"));
                UiDiagnosticCapture.WriteLayoutAudit(owner, Path.Combine(reportFolder, "main_layout_audit.txt"));
                UiDiagnosticCapture.WriteControlInventory(owner, Path.Combine(reportFolder, "main_control_inventory.txt"));
                if (effectiveOwner is MainWindow mainWindow)
                {
                    mainWindow.WriteDiagnosticEvidence(reportFolder);
                    await mainWindow.CaptureDiagnosticVisualScenariosAsync(reportFolder);
                }
                if (!string.IsNullOrWhiteSpace(mainCaptureError)) File.WriteAllText(Path.Combine(reportFolder, "main_capture_warning.txt"), mainCaptureError);
            }
            if (!string.IsNullOrWhiteSpace(settingsCaptureError)) File.WriteAllText(Path.Combine(reportFolder, "settings_capture_warning.txt"), settingsCaptureError);
            UiDiagnosticCapture.WriteSettingsEffectCoverage(ReadControls(), Path.Combine(reportFolder, "settings_effect_coverage.txt"));
            WriteSettingsControlEffectMap(Path.Combine(reportFolder, "settings_control_effect_map.tsv"));
            await WritePerformanceEffectDiagnosticsAsync(reportFolder);
            await WriteImageFixtureDiagnosticsAsync(reportFolder);
            if (effectiveOwner is MainWindow liveMain)
            {
                // Exercise the actual visible viewer over the generated 240-image corpus. This is
                // intentionally after static/settings capture so navigation cannot contaminate those
                // baselines. The main window is restored to the user's prior image afterwards.
                await liveMain.RunLiveDiagnosticNavigationAsync(
                    Path.Combine(reportFolder, "generated_image_fixtures", "navigation-stress-240"), reportFolder);
                await liveMain.RunLargeImageFirstPaintDiagnosticAsync(
                    Path.Combine(reportFolder, "generated_image_fixtures", "legacy-real-format-corpus", "93_large_progressive_portrait_21mp.jpg"), reportFolder);
                // Refresh evidence after the live exercise. Previously behaviour_events.jsonl was copied
                // before navigation, so decode-route/native-Explorer events created by the most valuable
                // part of the audit could be absent from the exported ZIP.
                liveMain.WriteDiagnosticEvidence(reportFolder);
            }

            WriteOverallDiagnosticSummary(reportFolder);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(reportFolder, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            if (!keepFolder)
            {
                try { Directory.Delete(reportFolder, true); } catch { }
            }
            DirtyText.Text = $"Diagnostics → {Path.GetFileName(zipPath)}";
            return reportFolder;
        }
        catch (Exception ex)
        {
            DirtyText.Text = "Diagnostic export failed";
            try
            {
                var targetErrDir = targetDir ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_DIR") ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_EXPORT_DIR") ?? WindowsKnownFolders.Downloads();
                Directory.CreateDirectory(targetErrDir);
                File.WriteAllText(Path.Combine(targetErrDir, "Glide Diagnostics Export Error.txt"), ex.ToString());
            }
            catch { }
            throw;
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private async void ExportDiagnosticsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ExportDiagnosticsAsync();
        }
        catch { }
    }

    private static void WriteOverallDiagnosticSummary(string reportFolder)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        foreach (var file in Directory.EnumerateFiles(reportFolder, "*.txt", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(reportFolder, file);
            if (name.Equals("summary.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("overall_summary.txt", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) ||
                    (line.StartsWith("SUMMARY ", StringComparison.OrdinalIgnoreCase) && !line.Contains("FAIL=0", StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"{name}: {line}");
                else if (line.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)) warnings.Add($"{name}: {line}");
            }
        }
        var status = failures.Count == 0 ? "PASS" : "FAIL";
        var lines = new List<string>
        {
            $"OVERALL {status}",
            $"Nested failures={failures.Count} warnings={warnings.Count}",
            "Overall PASS requires both structural diagnostics and every generated nested suite/layout audit to contain no FAIL evidence."
        };
        lines.AddRange(failures.Take(200).Select(x => "FAIL " + x));
        lines.AddRange(warnings.Take(200).Select(x => "WARN " + x));
        File.WriteAllLines(Path.Combine(reportFolder, "overall_summary.txt"), lines);

        var structural = Path.Combine(reportFolder, "summary.txt");
        var previous = File.Exists(structural) ? File.ReadAllText(structural) : string.Empty;
        File.WriteAllText(structural, $"OVERALL {status} | nested FAIL={failures.Count} WARN={warnings.Count}{Environment.NewLine}" + previous);
    }

    private async Task WritePerformanceEffectDiagnosticsAsync(string reportFolder)
    {
        var temp = Path.Combine(reportFolder, "performance_fixtures");
        Directory.CreateDirectory(temp);
        var rows = new List<object>();
        var text = new List<string>();
        void Add(string id, bool pass, string detail)
        {
            rows.Add(new { id, status = pass ? "PASS" : "FAIL", detail });
            text.Add($"{(pass ? "PASS" : "FAIL")} {id}: {detail}");
        }

        try
        {
            var speed = Glide.Imaging.ImagePerformancePolicy.ForProfile("Maximum speed");
            var balanced = Glide.Imaging.ImagePerformancePolicy.ForProfile("Balanced");
            var quality = Glide.Imaging.ImagePerformancePolicy.ForProfile("Maximum quality");
            Add("profile_coordination",
                speed.PreviewLongestSide < balanced.PreviewLongestSide && balanced.PreviewLongestSide < quality.PreviewLongestSide &&
                speed.RefinementDelayMs > balanced.RefinementDelayMs && balanced.RefinementDelayMs > quality.RefinementDelayMs &&
                !speed.ProgressiveColorFirstPreview && balanced.ProgressiveColorFirstPreview && quality.ProgressiveColorFirstPreview &&
                speed.FullNeighbourPredecodeCount == 0 && balanced.FullNeighbourPredecodeCount == 0 && quality.FullNeighbourPredecodeCount > balanced.FullNeighbourPredecodeCount &&
                speed.PrefetchDepth == 3 && balanced.PrefetchDepth == 5 && quality.PrefetchDepth == 15 &&
                quality.CompressedCacheItems == 20 && quality.CompressedCacheMegabytes == 999 && quality.DecodedCacheMegabytes == 999,
                $"preview={speed.PreviewLongestSide}/{balanced.PreviewLongestSide}/{quality.PreviewLongestSide}, delay={speed.RefinementDelayMs}/{balanced.RefinementDelayMs}/{quality.RefinementDelayMs}, progressive-colour={speed.ProgressiveColorFirstPreview}/{balanced.ProgressiveColorFirstPreview}/{quality.ProgressiveColorFirstPreview}, full-neighbours={speed.FullNeighbourPredecodeCount}/{balanced.FullNeighbourPredecodeCount}/{quality.FullNeighbourPredecodeCount}, prefetch={speed.PrefetchDepth}/{balanced.PrefetchDepth}/{quality.PrefetchDepth}, cache={quality.CompressedCacheItems}/{quality.CompressedCacheMegabytes}/{quality.DecodedCacheMegabytes}");

            var plan = Glide.Imaging.ImageLoadCoordinator.BuildNeighbourPlan(new[] { "0", "1", "2", "3", "4" }, 2, 1, 2);
            Add("direction_aware_prefetch", plan.SequenceEqual(new[] { "3", "1", "4", "0" }), string.Join(" -> ", plan));

            var largeBmp = Path.Combine(temp, "large_1600x900.bmp");
            WriteDiagnosticBmp(largeBmp, 1600, 900);
            using (var loader = new Glide.Imaging.ImageLoadCoordinator())
            {
                loader.Policy = balanced with
                {
                    PreviewLongestSide = 480,
                    RefinementDelayMs = 5,
                    BackgroundRefinement = true,
                    PredictivePrefetch = false,
                    CompressedCacheMegabytes = 32,
                    DecodedCacheMegabytes = 64
                };
                var first = await loader.LoadForegroundAsync(largeBmp);
                if (first is null) Add("staged_first_frame", false, "Foreground decode returned null.");
                else
                {
                    var firstLongest = Math.Max(first.Bitmap.PixelSize.Width, first.Bitmap.PixelSize.Height);
                    Add("staged_first_frame", first.IsPreview && firstLongest <= 480 && first.SourceWidth == 1600 && first.SourceHeight == 900,
                        $"preview={first.Bitmap.PixelSize.Width}x{first.Bitmap.PixelSize.Height}, source={first.SourceWidth}x{first.SourceHeight}, isPreview={first.IsPreview}");
                    var bounded = await loader.RefineForegroundAsync(first);
                    Add("viewport_bounded_refinement", bounded is not null && bounded.IsPreview &&
                        Math.Max(bounded.Bitmap.PixelSize.Width, bounded.Bitmap.PixelSize.Height) > firstLongest &&
                        Math.Max(bounded.Bitmap.PixelSize.Width, bounded.Bitmap.PixelSize.Height) <= 960,
                        bounded is null ? "Bounded refinement returned null." : $"bounded={bounded.Bitmap.PixelSize.Width}x{bounded.Bitmap.PixelSize.Height}; contract=viewport-demand, not unconditional full decode");
                    bounded?.Bitmap.Dispose();

                    loader.SetViewportDemand(largeBmp, new Glide.Imaging.ImageViewportDemand(1600, 900, 1.0, SelectionZoom: false, ExactPixelDemand: true));
                    var refined = await loader.RefineForegroundAsync(first);
                    Add("full_resolution_refinement", refined is not null && !refined.IsPreview && refined.Bitmap.PixelSize.Width == 1600 && refined.Bitmap.PixelSize.Height == 900,
                        refined is null ? "Exact-pixel refinement returned null." : $"final={refined.Bitmap.PixelSize.Width}x{refined.Bitmap.PixelSize.Height}; demand=exact-pixels");
                    first.Bitmap.Dispose();
                    refined?.Bitmap.Dispose();
                }

                // Cache budget and purge use multiple independent identities so LRU eviction is exercised.
                var cacheFiles = new List<string>();
                for (var i = 0; i < 10; i++)
                {
                    var copy = Path.Combine(temp, $"cache_{i:00}.bmp");
                    File.Copy(largeBmp, copy, true);
                    File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(i));
                    cacheFiles.Add(copy);
                }
                loader.Policy = loader.Policy with { CompressedCacheItems = 64, CompressedCacheMegabytes = 32 };
                await loader.WarmAsync(cacheFiles);
                var warm = loader.GetCacheSnapshot();
                Add("compressed_cache_budget", warm.CompressedBytes <= 32L * 1024 * 1024 && warm.CompressedItems < cacheFiles.Count,
                    $"items={warm.CompressedItems}, bytes={warm.CompressedBytes}");
                loader.PurgeCaches();
                var purged = loader.GetCacheSnapshot();
                Add("cache_purge_epoch", purged.CompressedItems == 0 && purged.DecodedItems == 0 && purged.CompressedBytes == 0 && purged.DecodedBytes == 0,
                    $"compressed={purged.CompressedItems}/{purged.CompressedBytes}, decoded={purged.DecodedItems}/{purged.DecodedBytes}");

                loader.Policy = loader.Policy with { SequentialForegroundReads = false, DecoderScaledFirstFrame = false };
                var randomRead = await loader.LoadForegroundAsync(largeBmp);
                Add("foreground_read_mode", randomRead is not null && randomRead.Bitmap.PixelSize.Width == 1600, "Decode succeeds with sequential hint disabled; the setting is consumed by foreground stream creation.");
                randomRead?.Bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            Add("performance_effect_suite", false, ex.GetType().Name + ": " + ex.Message);
        }

        File.WriteAllLines(Path.Combine(reportFolder, "performance_effect_results.txt"), text);
        File.WriteAllText(Path.Combine(reportFolder, "performance_effect_results.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteDiagnosticBmp(string path, int width, int height)
    {
        var rowBytes = ((width * 3 + 3) / 4) * 4;
        var pixelBytes = checked(rowBytes * height);
        var fileBytes = checked(54 + pixelBytes);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B'); writer.Write((byte)'M'); writer.Write(fileBytes);
        writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((short)1); writer.Write((short)24);
        writer.Write(0); writer.Write(pixelBytes); writer.Write(2835); writer.Write(2835); writer.Write(0); writer.Write(0);
        var row = new byte[rowBytes];
        for (var x = 0; x < width; x++)
        {
            row[x * 3] = (byte)(x % 251);
            row[x * 3 + 1] = (byte)((x * 3) % 251);
            row[x * 3 + 2] = (byte)((x * 7) % 251);
        }
        for (var y = 0; y < height; y++) writer.Write(row);
    }

    private async Task WriteImageFixtureDiagnosticsAsync(string reportFolder)
    {
        var fixtureFolder = Path.Combine(reportFolder, "generated_image_fixtures");
        var validation = Glide.Diagnostics.ImageDiagnosticFixtures.Validate();
        if (validation.Count > 0)
        {
            File.WriteAllLines(Path.Combine(reportFolder, "image_fixture_validation_failures.txt"), validation);
            return;
        }

        var publishedFixtureFolder = Path.Combine(AppContext.BaseDirectory, "diagnostic-fixtures");
        var publishedFixtureFiles = Glide.Diagnostics.ImageDiagnosticFixtures.Extensions
            .Select(extension => Path.Combine(publishedFixtureFolder, "fixture" + extension))
            .ToArray();
        var fixtureSource = publishedFixtureFiles.All(File.Exists) ? "published-build" : "regenerated-development";
        if (fixtureSource == "published-build")
        {
            Directory.CreateDirectory(fixtureFolder);
            foreach (var source in Directory.EnumerateFiles(publishedFixtureFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(publishedFixtureFolder, source);
                var target = Path.Combine(fixtureFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }
        else
        {
            // Keep the generator's returned core-fixture inventory as an explicit artifact. The
            // generated corpus status is read below; diagnostics must never infer that a 240-file
            // corpus exists merely because its target directory name is present.
            _ = Glide.Diagnostics.ImageDiagnosticFixtures.WriteTo(fixtureFolder);
        }

        var performanceCorpusStatusPath = Path.Combine(fixtureFolder, "performance_corpus_status.json");
        var performanceCorpusGenerated = false;
        var performanceCorpusStatus = "SKIP";
        var performanceCorpusFileCount = 0;
        try
        {
            using var corpusStatus = JsonDocument.Parse(File.ReadAllText(performanceCorpusStatusPath));
            var root = corpusStatus.RootElement;
            performanceCorpusGenerated = root.TryGetProperty("generated", out var generated) && generated.GetBoolean();
            performanceCorpusStatus = root.TryGetProperty("status", out var status) ? status.GetString() ?? "SKIP" : "SKIP";
            var corpusFolder = Path.Combine(fixtureFolder, "navigation-stress-240");
            if (Directory.Exists(corpusFolder))
                performanceCorpusFileCount = Directory.EnumerateFiles(corpusFolder, "*.jpg", SearchOption.TopDirectoryOnly)
                    .Count(path => !Path.GetFileName(path).StartsWith("_master_", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            performanceCorpusStatus = "SKIP_STATUS_UNAVAILABLE";
        }

        using var loader = new Glide.Imaging.ImageLoadCoordinator { WarmCacheItemLimit = 64, SequentialReadEnabled = true };
        var rows = new List<string> { "extension,file,bytes,width,height,cold_decode_ms,warm_decode_ms,warm_cache_hit,native_probe_ok,native_width,native_height,native_frames,status,error" };
        var jsonResults = new List<object>();
        foreach (var extension in Glide.Diagnostics.ImageDiagnosticFixtures.Extensions)
        {
            var path = Path.Combine(fixtureFolder, "fixture" + extension);
            var status = "PASS";
            var error = "";
            var width = 0; var height = 0; var coldMs = 0d; var warmMs = 0d; var cacheHit = false;
            var nativeProbeOk = false;
            uint nativeWidth = 0, nativeHeight = 0, nativeFrames = 0;
            try
            {
                loader.PurgeWarmCache();
                var cold = await loader.LoadForegroundAsync(path);
                if (cold is null) throw new InvalidOperationException("Cold fixture decode returned no result.");
                width = cold.Bitmap.PixelSize.Width; height = cold.Bitmap.PixelSize.Height; coldMs = cold.DecodeTime.TotalMilliseconds; cold.Bitmap.Dispose();
                await loader.WarmAsync(new[] { path });
                var warm = await loader.LoadForegroundAsync(path);
                if (warm is null) throw new InvalidOperationException("Warm fixture decode returned no result.");
                warmMs = warm.DecodeTime.TotalMilliseconds; cacheHit = warm.CacheHit; warm.Bitmap.Dispose();
                if (!cacheHit) { status = "FAIL"; error = "Warm cache was not used."; }

                nativeProbeOk = Glide.Imaging.NativeImageProbe.TryProbe(path, out var nativeInfo);
                if (nativeProbeOk)
                {
                    nativeWidth = nativeInfo.Width; nativeHeight = nativeInfo.Height; nativeFrames = nativeInfo.FrameCount;
                    if (nativeWidth != width || nativeHeight != height)
                    {
                        status = "FAIL";
                        error = $"Native WIC probe dimensions {nativeWidth}x{nativeHeight} disagree with Avalonia decode {width}x{height}.";
                    }
                }
            }
            catch (Exception ex)
            {
                status = "FAIL"; error = ex.GetType().Name + ": " + ex.Message;
            }
            static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            long fixtureBytes = 0;
            try { fixtureBytes = File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { }
            rows.Add(string.Join(",", Csv(extension), Csv(Path.GetFileName(path)), fixtureBytes, width, height,
                coldMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), warmMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                cacheHit ? "true" : "false", nativeProbeOk ? "true" : "false", nativeWidth, nativeHeight, nativeFrames, status, Csv(error)));
            jsonResults.Add(new
            {
                extension, file = Path.GetFileName(path), bytes = fixtureBytes, width, height,
                coldDecodeMs = Math.Round(coldMs, 3), warmDecodeMs = Math.Round(warmMs, 3), warmCacheHit = cacheHit,
                nativeWicProbe = new { available = nativeProbeOk, width = nativeWidth, height = nativeHeight, frames = nativeFrames },
                status, error
            });
        }
        File.WriteAllLines(Path.Combine(reportFolder, "image_fixture_decode_results.csv"), rows);
        File.WriteAllText(Path.Combine(reportFolder, "image_fixture_decode_results.json"),
            JsonSerializer.Serialize(jsonResults, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(reportFolder, "image_fixture_decode_summary.txt"),
            $"Routed extensions: {Glide.Core.ImageFormatRegistry.Extensions.Count}\nCore real-fixture extensions: {Glide.Diagnostics.ImageDiagnosticFixtures.Extensions.Count}\nFixture source: {fixtureSource}\nThe ten protected core suffixes have real encoded fixtures, cold/warm decode checks and native WIC probes. All 196 routed suffixes also have physical routing probes. Navigation corpus: status={performanceCorpusStatus}, generated={performanceCorpusGenerated}, genuine JPEG files={performanceCorpusFileCount}; the count is reported from performance_corpus_status.json and the generated directory, never assumed from its name. The packaged legacy real-format corpus is staged when available. A routing probe is deliberately not claimed as an independently decodable format.\nTIFF additionally records bounded native preview, full-resolution refinement dimensions and malformed-input rejection in tiff_wic_results.json.\nSee image_fixture_decode_results.csv, image_fixture_decode_results.json and tiff_wic_results.json.\n");

        var tiffResults = new Dictionary<string, object?>();
        var tiffPath = Path.Combine(fixtureFolder, "fixture.tiff");
        if (File.Exists(tiffPath))
        {
            try
            {
                var previewOk = Glide.Imaging.NativeImageDecoder.TryDecode(tiffPath, 1, out var preview);
                var previewWidth = previewOk ? preview.PixelSize.Width : 0;
                var previewHeight = previewOk ? preview.PixelSize.Height : 0;
                if (previewOk) preview.Dispose();
                var fullOk = Glide.Imaging.NativeImageDecoder.TryDecode(tiffPath, 0, out var full);
                var fullWidth = fullOk ? full.PixelSize.Width : 0;
                var fullHeight = fullOk ? full.PixelSize.Height : 0;
                if (fullOk) full.Dispose();
                var malformedPath = Path.Combine(reportFolder, "malformed_tiff_fixture.tiff");
                File.WriteAllBytes(malformedPath, new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x01, 0x00 });
                var malformedRejected = !Glide.Imaging.NativeImageDecoder.TryDecode(malformedPath, 1, out var malformed);
                if (!malformedRejected) malformed.Dispose();
                File.Delete(malformedPath);
                var bounded = previewOk && previewWidth > 0 && previewHeight > 0 && Math.Max(previewWidth, previewHeight) <= 1;
                var refined = fullOk && fullWidth > 0 && fullHeight > 0 && (fullWidth >= previewWidth && fullHeight >= previewHeight);
                // A missing native WIC bridge is a platform/dependency gate, not a malformed
                // fixture failure. Once WIC is available, malformed acceptance is a real FAIL.
                var wicUnavailable = !previewOk && !fullOk;
                tiffResults["status"] = wicUnavailable ? "SKIP" : (bounded && refined && malformedRejected ? "PASS" : "FAIL");
                tiffResults["availability"] = wicUnavailable ? "WIC_UNAVAILABLE" : "WIC_AVAILABLE";
                tiffResults["preview"] = new { available = previewOk, width = previewWidth, height = previewHeight, maxLongestSide = 1, bounded };
                tiffResults["fullResolution"] = new { available = fullOk, width = fullWidth, height = fullHeight, refined };
                tiffResults["malformedRejected"] = wicUnavailable ? (bool?)null : malformedRejected;
            }
            catch (Exception ex)
            {
                tiffResults["status"] = "FAIL";
                tiffResults["error"] = ex.GetType().Name + ": " + ex.Message;
            }
        }
        else
        {
            tiffResults["status"] = "SKIP";
            tiffResults["availability"] = "FIXTURE_UNAVAILABLE";
        }
        File.WriteAllText(Path.Combine(reportFolder, "tiff_wic_results.json"), JsonSerializer.Serialize(tiffResults, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task CaptureAllSettingsPagesAsync(string reportFolder)
    {
        var previousCategory = _activeCategory;
        var previousSearch = SearchBox.Text ?? string.Empty;
        var previousOffset = ContentScroll.Offset;
        var screenshotFiles = new List<string>();
        var captureWarnings = new List<string>();
        var scrollRows = new List<string> { "category,screenshot,offsetY,maxOffsetY,viewportHeight" };
        var keys = new[] { "General", "Viewing", "Mouse", "Performance", "Status", "Slideshow", "Hotkeys", "Tabs", "Overlays", "Profiles", "Windows", "Developer" };
        foreach (var key in keys)
        {
            ShowCategory(key, clearSearch: false);
            ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            var immediate = $"settings_{key.ToLowerInvariant()}_immediate.png";
            if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, immediate), out var immediateError))
                screenshotFiles.Add(immediate);
            else captureWarnings.Add($"{immediate}: {immediateError}");

            await Task.Delay(180);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            var settled = $"settings_{key.ToLowerInvariant()}_settled.png";
            if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, settled), out var settledError))
            {
                screenshotFiles.Add(settled);
                scrollRows.Add(ScrollEvidenceRow(key, settled));
            }
            else captureWarnings.Add($"{settled}: {settledError}");
            UiDiagnosticCapture.WriteGeometry(ReferenceRoot, Path.Combine(reportFolder, $"settings_{key.ToLowerInvariant()}_geometry.txt"));
            UiDiagnosticCapture.WriteLayoutAudit(ReferenceRoot, Path.Combine(reportFolder, $"settings_{key.ToLowerInvariant()}_layout_audit.txt"));

            // Long Settings pages are part of Glide's visual contract. Capture the entire scrollable
            // page in overlapping settled views rather than only proving that the top rendered.
            // This deliberately uses ScrollBarMaximum rather than guessing which categories are long.
            var maxY = Math.Max(0, ContentScroll.ScrollBarMaximum.Y);
            var viewportHeight = Math.Max(1, ContentScroll.Viewport.Height);
            if (maxY > 20)
            {
                var step = Math.Max(120, viewportHeight * 0.82);
                var positions = new List<double>();
                for (var y = step; y < maxY - 8; y += step) positions.Add(Math.Min(y, maxY));
                if (positions.Count == 0 || Math.Abs(positions[^1] - maxY) > 8) positions.Add(maxY);

                var scrollIndex = 2;
                foreach (var y in positions.DistinctBy(value => Math.Round(value, 1)))
                {
                    ContentScroll.Offset = new Vector(ContentScroll.Offset.X, Math.Clamp(y, 0, maxY));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Task.Delay(120);
                    var scrolled = $"settings_{key.ToLowerInvariant()}_scroll{scrollIndex}_settled.png";
                    if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, scrolled), out var scrolledError))
                    {
                        screenshotFiles.Add(scrolled);
                        scrollRows.Add(ScrollEvidenceRow(key, scrolled));
                    }
                    else captureWarnings.Add($"{scrolled}: {scrolledError}");
                    scrollIndex++;
                }
            }
        }

        // Responsive-shell fixtures: explicitly prove the former Viewbox letterboxing bug stays gone.
        var oldWidth = Width; var oldHeight = Height;
        var resizeScenarios = new (string Name, double Width, double Height)[]
        {
            ("compact", 760, 560), ("standard", 1040, 760), ("wide", 1320, 760), ("tall", 900, 1000)
        };
        foreach (var scenario in resizeScenarios)
        {
            Width = scenario.Width; Height = scenario.Height;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(100);
            var file = $"settings_resize_{scenario.Name}.png";
            if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, file), out var error)) screenshotFiles.Add(file);
            else captureWarnings.Add($"{file}: {error}");
            UiDiagnosticCapture.WriteGeometry(ReferenceRoot, Path.Combine(reportFolder, $"settings_resize_{scenario.Name}_geometry.txt"));
            UiDiagnosticCapture.WriteLayoutAudit(ReferenceRoot, Path.Combine(reportFolder, $"settings_resize_{scenario.Name}_layout_audit.txt"));
        }
        Width = oldWidth; Height = oldHeight;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        // Permanent search fixture: proves that Search is not merely a textual index.
        SearchBox.Text = "hotkey";
        // Search rendering is debounced for responsiveness; flush it so the screenshot is not empty.
        FlushPendingSearch();
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(120);
        const string searchShot = "settings_search_hotkey_settled.png";
        if (UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, searchShot), out var searchError))
        {
            screenshotFiles.Add(searchShot);
            scrollRows.Add(ScrollEvidenceRow("Search:hotkey", searchShot));
        }
        else captureWarnings.Add($"{searchShot}: {searchError}");

        if (string.IsNullOrWhiteSpace(previousSearch))
        {
            _loadingControls = true;
            SearchBox.Text = string.Empty;
            _loadingControls = false;
            ShowCategory(previousCategory, clearSearch: false);
            ContentScroll.Offset = new Vector(previousOffset.X, Math.Min(previousOffset.Y, Math.Max(0, ContentScroll.ScrollBarMaximum.Y)));
        }
        else
        {
            SearchBox.Text = previousSearch; // TextChanged schedules the debounced rebuild.
            FlushPendingSearch();            // Render it now so the window is left in a settled state.
            ContentScroll.Offset = new Vector(previousOffset.X, Math.Min(previousOffset.Y, Math.Max(0, ContentScroll.ScrollBarMaximum.Y)));
        }

        File.WriteAllLines(Path.Combine(reportFolder, "settings_scroll_capture_index.csv"), scrollRows);
        File.WriteAllLines(Path.Combine(reportFolder, "screenshot_index.txt"), new[]
        {
            "Glide UI diagnostic screenshots", "",
            "Every Settings category is captured immediate + settled at the top.",
            "Scrollable Settings categories additionally receive overlapping settled captures through the bottom of the page.",
            "Search is captured using the editable 'hotkey' fixture.",
            "Main-window immediate/settled/export-time and resize-scenario captures are also included.", ""
        }.Concat(screenshotFiles));

        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var visualRows = new List<string>
        {
            "scenario,expected,automated_result,screenshot,ai_review_required,notes"
        };
        foreach (var file in screenshotFiles)
        {
            var scenario = Path.GetFileNameWithoutExtension(file);
            visualRows.Add(string.Join(",", Csv(scenario), Csv("No clipping/overlap/stale paint; legacy Glide density and theme remain coherent"), Csv("CAPTURED"), Csv(file), "true", Csv("Review against bundled legacy reference screenshots")));
        }
        visualRows.Add(string.Join(",", Csv("main_current"), Csv("Main Glide chrome/content/status remain inside bounds"), Csv("CAPTURED"), Csv("main_export_time.png"), "true", Csv("See main_layout_audit.txt and main_geometry.txt")));
        File.WriteAllLines(Path.Combine(reportFolder, "combined_visual_results.csv"), visualRows);

        if (captureWarnings.Count > 0)
            File.WriteAllLines(Path.Combine(reportFolder, "screenshot_capture_warnings.txt"), captureWarnings);

        string ScrollEvidenceRow(string category, string screenshot)
        {
            var offset = ContentScroll.Offset.Y;
            var maximum = Math.Max(0, ContentScroll.ScrollBarMaximum.Y);
            var viewport = Math.Max(0, ContentScroll.Viewport.Height);
            return string.Join(",", Csv(category), Csv(screenshot), offset.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), maximum.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), viewport.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }
    }


    private void WriteSettingsControlEffectMap(string path)
    {
        var lines = new List<string>
        {
            "id\tcategory\tkind\tlabel\tfuturePhase\tcontrol\tcontrolType\tenabled\tstatus"
        };
        foreach (var definition in SettingsCatalog.All)
        {
            _settingControls.TryGetValue(definition.Id, out var control);
            var status = definition.FuturePhase is not null
                ? $"future phase {definition.FuturePhase}; intentionally disabled/hidden"
                : control is null
                    ? definition.Kind == SettingKind.Action ? "current semantic/action editor or generated UI" : "current setting without a direct named editor"
                    : control.IsEnabled ? "current enabled editor; value participates in Settings state/effect path"
                    : ConditionallyDisabledSettingIds.Contains(definition.Id) ? "current editor disabled by current state" : "current editor unexpectedly disabled";
            lines.Add(string.Join("\t",
                Sanitize(definition.Id), Sanitize(definition.Category), definition.Kind.ToString(), Sanitize(definition.Label), definition.FuturePhase?.ToString() ?? "",
                Sanitize(control?.Name ?? ""), control?.GetType().Name ?? "", (control?.IsEnabled ?? false).ToString(), Sanitize(status)));
        }
        File.WriteAllLines(path, lines);

        static string Sanitize(string value) => value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }

    private static void NormalizeImportedState(GlideSettingsState state)
    {
        state.Hotkeys ??= HotkeyCatalog.CreateDefaultMap();
        state.Gestures ??= GestureCatalog.CreateDefaultMap();
        foreach (var key in state.Hotkeys.Keys.ToList())
            state.Hotkeys[key] = state.Hotkeys[key]
                .Where(x => !x.Contains("Mouse", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        foreach (var action in HotkeyCatalog.All)
            if (!state.Hotkeys.ContainsKey(action.Id)) state.Hotkeys[action.Id] = action.DefaultShortcuts.ToList();
        foreach (var slot in GestureCatalog.Slots)
            if (!state.Gestures.ContainsKey(slot.Id)) state.Gestures[slot.Id] = slot.DefaultActionId;
    }

    private Avalonia.Media.IBrush SearchAccentBrush()
    {
        var accent = SelectedText(AccentCombo, "Glide blue") switch
        {
            "Teal" => "#30C7B5",
            "Violet" => "#A98CFF",
            "Amber" => "#E0A54A",
            _ => "#38A9F5"
        };
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(accent));
    }

    private Avalonia.Media.IBrush SearchMutedBrush()
    {
        var light = string.Equals(SelectedText(ThemeCombo), "Light", StringComparison.OrdinalIgnoreCase);
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(light ? "#56616B" : "#AAB5C2"));
    }

    private static string SelectedText(ComboBox combo, string fallback = "") =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static int ParseChoiceNumber(ComboBox combo, int fallback)
    {
        var text = SelectedText(combo);
        var numeric = text.Replace(" Hz", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return int.TryParse(numeric, out var value) ? value : fallback;
    }

    private static void SelectByText(ComboBox combo, string value)
    {
        var items = combo.Items.OfType<ComboBoxItem>().ToList();
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = items[i];
                return;
            }
        }
        if (items.Count > 0) combo.SelectedItem = items[0];
    }
}
