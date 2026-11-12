using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Diagnostics;
using Glide.App.Platform;
using Glide.App.Printing;
using Glide.App.Settings;
using Glide.App.Services;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Core.Workspace;
using Glide.Imaging;
using Glide.Input;
using Glide.Diagnostics.Runtime;
using Microsoft.VisualBasic.FileIO;

namespace Glide.App;

/// <summary>
/// Main composition root. UI controls dispatch into explicit workspace/viewer state; decoding, input
/// policy and settings persistence remain isolated so a zero-context agent can identify ownership fast.
/// </summary>
public partial class MainWindow : Window
{
    internal bool CanAcceptTabAttach => _settings.TabAttachEnabled && _physicalDraggedTabId is null;
    internal string CurrentExternalOpenBehavior => _settings.ExternalOpenBehavior;
    private readonly NavigationCoordinator _navigator = new();
    private FolderWatchService? _folderWatch;
    private int _folderRescanRunning;
    private readonly CodecProviderDecoderBackend _decoderBackend;
    private readonly Action<string, string> _providerFailureHandler;
    private readonly ImageLoadCoordinator _loader;
    private ImageLoadCoordinator? _overlayLoader;
    private string? _selectionZoomDemandPath;
    // Session-scoped default view mode set by the status bar Fit controls when their scope is
    // "This session" or "This session and future sessions". Null falls back to the persisted
    // DefaultViewMode setting.
    private string? _sessionDefaultViewMode;
    private readonly InputRouter _inputRouter = new();
    // Do not create a placeholder Home tab before startup arguments are classified. An explicit
    // Explorer image launch must begin with the requested image only; standalone launches create
    // their configured Home/Explorer destination during startup planning.
    private readonly WorkspaceState _workspace = new(createHome: false);
    private readonly DispatcherTimer _fullscreenCursorTimer = new();
    private readonly FullscreenCursorAutoHide _cursorAutoHide = new();
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);
    private readonly DispatcherTimer _hardTopmostTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private int _topmostTransientDepth;
    private IDisposable? _overlayContextTopmostScope;
    private CancellationTokenSource? _resizeInputResetCts;
    private CancellationTokenSource? _navigationBoundaryNoticeCts;
    private CancellationTokenSource? _navigationRateLimitNoticeCts;
    private long _lastRateLimitedNavigationTimestamp;
    private readonly DiagnosticsCoordinator _diagnostics = new();
    private readonly FileOperationController _fileOperations = new();
    private WindowInWindowOverlayManager? _overlays;
    private readonly MainWindowCommandDispatcher _commands;
    private SlideshowSessionController? _slideshow;
    private readonly FullscreenSessionController _fullscreen = new();
    private readonly TabAttachCoordinator _tabAttach;
    private readonly TabAttachCoordinator _physicalAttach;
    private readonly TabDragController _tabDrag;
    private readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, Button> _titleBarButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly PresentationFence _presentationFence = new();
    private readonly Guid _windowLifetimeId = Guid.NewGuid();
    private readonly CancellationTokenSource _windowLifetimeCts = new();
    private long _workspaceEpoch;
    private long _imageRequestId;
    private long _folderSessionId;
    private GlideSettingsState _startupSettingsBaseline = new();

    private enum PresentationStatus { Accepted, Cancelled, Failed }
    private readonly record struct PresentationOutcome(PresentationStatus Status, ImageRequestContext Request)
    {
        public bool Accepted => Status == PresentationStatus.Accepted;
    }

    private Guid? _suppressNextTabClick;

    // Once a tab crosses the source-window boundary while the physical left button is still down,
    // ownership moves to the detached HWND's native Windows move loop. PositionChanged callbacks
    // only observe attach targets; Windows itself owns pointer tracking, Snap and monitor edges.
    private Guid? _physicalDraggedTabId;
    private MainWindow? _physicalDragSource;
    private bool _deferWorkspaceActivation;
    private readonly Dictionary<Guid, BrowserSession> _browserSessions = new();
    // Browser-like Back/Forward state is tab-local. When Back is pressed from an image, that same
    // tab becomes an Explorer view and remembers the image as the terminal Forward destination.
    private readonly Dictionary<Guid, string> _tabForwardImageTargets = new();
    private readonly Dictionary<Guid, Stack<string>> _tabForwardFolderTargets = new();
    private readonly Dictionary<Guid, string> _browserHighlightTargets = new();
    private bool _titleNavCanBack;
    private bool _titleNavCanForward;
    private readonly StartupPathQueue _startupPaths = new();
    private bool _hasExplicitStartupOpen;

    private GlideSettingsState _settings = App.TryGetStartupSettings() ??
        (App.StartupFirstFramePolicy is { } firstFramePolicy
            ? SettingsStore.ApplyFirstFramePolicy(new GlideSettingsState(), firstFramePolicy)
            : new GlideSettingsState());
    private bool _deferredStartupSettingsAdopted;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _folderIndexCts;
    private bool _folderIndexReady = true;
    private bool _folderIndexCanonicalReady = true;
    private int _pendingNavigationDelta;
    private int _lastNavigationDirection;
    private long _lastNavigationRequestTimestamp;
    private CancellationTokenSource? _rapidNavigationSettleCts;
    // One writer for navigator mutation and the corresponding viewport presentation.  Input,
    // slideshow ticks and delayed rapid-settle callbacks all arrive independently.
    private readonly SemaphoreSlim _navigationSerializationGate = new(1, 1);
    private CancellationTokenSource? _heldNavigationCts;
    private int _heldNavigationDirection;
    private bool _heldNavigationKeyIsDown;
    private readonly Queue<(int Direction, bool Rapid)> _wheelNavigationQueue = new();
    private readonly object _wheelNavigationSync = new();
    private bool _wheelNavigationProcessorRunning;
    private long _lastWheelNavigationTimestamp;
    private const int MaxBufferedNavigationCommands = 5;
    private bool _navigationBoundaryPromptActive;
    private int _navigationCommandEpoch;
    private int _suppressedHeldNavigationDirection;
    private CancellationTokenSource? _browserThumbnailCts;
    private readonly SemaphoreSlim _browserThumbnailGate = new(4, 4);
    // TODO(perf): a trivial shared thumbnail MemoryCache could reuse decoded bitmaps across folder
    // revisits. Deliberately not re-architecting virtualization here (too risky); ItemsRepeater
    // realization stays the single owner of per-tile CTS/bitmap lifetime.
    private readonly Dictionary<Control, CancellationTokenSource> _browserRealizationCts = new();
    private readonly HashSet<string> _browserSelection = new(StringComparer.OrdinalIgnoreCase);
    private List<BrowserEntry> _browserEntries = new();
    private int _browserSelectionAnchor = -1;
    private int _browserCurrentIndex = -1;
    private long _browserGeneration;
    private int _browserViewMode; // 0 large icons, 1 medium icons, 2 compact
    private bool _browserUiReady;
    private string? _queuedOpen;
    private bool _statusCollapsed;
    private bool _statusSessionClosed;
    private bool _statusHoverRevealed;
    private bool _collapseAnchorsCaptured;
    private double? _collapseAnchorViewer;
    private double? _collapseAnchorHome;
    private bool _closeConfirmationInProgress;
    private bool _closeConfirmed;
    private bool _forceProcessExit;
    private bool _forceCloseFromCaptionAction;
    private bool _closeForSpeedBoostStandby;
    private bool _homeKeyDown;
    private double _sessionOpacity = 1.0;
    private bool _startupPlacementApplied;
    private bool _startupFirstImageTimingWritten;
    private bool _syncingViewportScrollbars;
    private double _lastTabLayoutWidth;
    private string? _lastSurroundingPrefetchFolder;

    private string? _currentPath;
    // Last image that actually reached the viewport. Navigation semantics such as Up use this,
    // never a merely requested/in-flight path.
    private string? _presentedPath;
    private int _currentPixelWidth;
    private int _currentPixelHeight;
    private long _currentFileSize;
    private double _currentDecodeMs;
    private double _currentFirstFrameDecodeMs;
    private int _currentFirstFrameWidth;
    private int _currentFirstFrameHeight;
    private string _currentDecodeRoute = "none";
    private bool _currentFrameIsPreview;
    private CancellationTokenSource? _previewQualityRecoveryCts;
    private Bitmap? _interactionPreviewBitmap;
    private ImageMetadata _currentMetadata = new();
    private CancellationTokenSource? _metadataCts;
    private IPointer? _manualWindowDragPointer;
    private PixelPoint _manualWindowDragCursorStart;
    private PixelPoint _manualWindowDragWindowStart;
    private bool _manualWindowDragMoved;
    private long _suppressCloseUntilTick;
    private IPointer? _chromeMiddlePressPointer;
    private Point _chromeMiddlePressPosition;
    private bool _chromeMiddlePressStartedOnEmptyChrome;
    private WindowsBrowserFrameController? _windowsBrowserFrame;
    private WindowsExplorerHost? NativeExplorerHost;
    private PixelPoint _lastNormalWindowPosition;
    private Size _lastNormalWindowSize;
    // The ordinary (restored) window bounds captured when fullscreen is entered. Leaving fullscreen
    // re-applies them before moving to maximized/other states, so the Win32 restore rectangle and the
    // remembered placement are the real normal bounds, never the maximized or fullscreen geometry.
    private PixelPoint _fullscreenNormalPosition;
    private Size _fullscreenNormalSize;
    private bool _fullscreenNormalKnown;
    private bool _normalPlacementKnown;
    private PixelPoint? _validatedStartupNormalPosition;
    private WindowState _stateBeforeMinimize = WindowState.Normal;
    private WindowState _lastKnownWindowState = WindowState.Normal;

    // Whole-application Overlay / Window-in-Window mode is deliberately session state, separate
    // from the existing per-image WindowInWindowOverlayManager. Normal Glide stays the default.
    private bool _wholeAppOverlayMode;
    private bool _nativeSizeMoveActive;
    private HomeSurface? _homeSurface;
    private BrowserSurface? _browserSurface;
    private bool _startupWholeAppOverlayPreferenceApplied;
    private bool _speedBoostStandbyActive;
    private bool _standbyClosePending;
    private bool _pendingExternalReceiver;
    // Secondary windows may temporarily be the only visible window during Speed Boost delegation,
    // but a newly restored primary must always reclaim the single taskbar representative slot.
    internal bool IsSecondaryWindow { get; private set; }
    internal bool IsStandbyClosePending => _standbyClosePending;
    // A hidden warm HWND can retain its last DWM-composed pixels across Hide/Show. Keep the HWND
    // visually suppressed until a new image or Home surface has completed a safe composition.
    private bool _warmPresentationGateActive;
    private bool _automaticOverlayLayoutRestoreScheduled;
    private WholeAppOverlaySnapshot? _wholeAppOverlaySnapshot;
    private IBrush? _wholeAppOverlayPreviousTransparencyFallback;
    private Grid? _wholeAppOverlayControlsHost;
    private LegacyOverlayChrome? _wholeAppOverlayChromeVisual;
    private Slider? _wholeAppOverlayOpacityHitTarget;
    private Thumb? _wholeAppOverlayResizeGrip;
    private string? _wholeAppOverlayPlacementPath;
    private bool _applyingWholeAppOverlayPlacement;

    private sealed record WholeAppOverlaySnapshot(
        WindowState RestoreState, PixelPoint Position, double Width, double Height,
        double MinWidth, double MinHeight,
        bool RestoreFullscreen, double SessionOpacity);

    private Task? _earlyStartupTask;
    private string[]? _earlyStartupPaths;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private const int DwmwaTransitionsForcedisabled = 3;

    private void DisableDwmTransitions(string source)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var platform = TryGetPlatformHandle();
            if (platform is not null && platform.Handle != IntPtr.Zero)
            {
                int disable = 1;
                var hr = DwmSetWindowAttribute(platform.Handle, DwmwaTransitionsForcedisabled, ref disable, sizeof(int));
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("disable_dwm_transitions", $"src={source};hr={hr};hwnd={platform.Handle}");
            }
            else
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("disable_dwm_transitions_no_handle", $"src={source}");
            }
        }
        catch { }
    }

    public MainWindow()
    {
        GlidePerformanceTrace.Mark("ctor_init_component_start");
        InitializeComponent();
        _hardTopmostTimer.Tick += (_, _) => ReassertNativeTopmost();
        GlidePerformanceTrace.Mark("ctor_init_component_end");
        _diagnostics.Write("window", "constructed", new { lifetime = _windowLifetimeId, pid = Environment.ProcessId });
        DisableDwmTransitions("ctor");
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true)
                DisableDwmTransitions("visible");
        };
        // The HWND must be created transparency-capable from its very first show. Switching
        // TransparencyLevelHint only after entering Overlay mode is too late on some Windows/Avalonia
        // compositor paths and causes transparent PNG pixels to be flattened against an opaque surface.
        // Normal Glide remains visually opaque because its normal theme background is still painted.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        // CRITICAL INVARIANT: the top-level Window itself remains transparent for its entire
        // lifetime. Normal Glide paints BrushWindow on MainRoot instead. Never put an opaque
        // brush back on Window.Background: doing so can lock an already-created Windows/Avalonia
        // composition surface into an opaque/redirection path and makes PNG alpha appear grey
        // when Whole-App Overlay is enabled later.
        Background = Brushes.Transparent;
        _startupSettingsBaseline = _settings.CloneState();
        // XAML can raise ComboBox.SelectionChanged while InitializeComponent is still constructing
        // named controls. Explorer handlers must not touch the partially-built visual tree.
        // Home and Explorer subtrees are intentionally not constructed here. Explicit image cold
        // launch materializes only the primary viewer; each secondary workspace builds on first use.
        Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows())
                _windowsBrowserFrame ??= WindowsBrowserFrameController.TryAttach(
                    this, CaptionMinimizeButton, CaptionMaximizeButton, CaptionCloseButton,
                    screenPoint => Viewport.IsPointInsideSelection(Viewport.PointToClient(screenPoint)),
                    screenPoint => Viewport.IsPointOverImage(Viewport.PointToClient(screenPoint)),
                    () => ExecuteConfiguredCaptionAction(CaptionButtonRole.Minimize),
                    () => ExecuteConfiguredCaptionAction(CaptionButtonRole.Maximize),
                    () => ExecuteConfiguredCaptionAction(CaptionButtonRole.Close),
                    BeginNativeResizeInteraction,
                    ResetPointerStateAfterNativeResize,
                    TitleActionHost);
        };
        Closed += (_, _) =>
        {
            _gifPlayer?.Stop();
            try { _windowLifetimeCts.Cancel(); } catch { }
            CancelAsyncWorkspaceWork();
            _windowsBrowserFrame?.Dispose();
            _windowsBrowserFrame = null;
        };
        // Do not touch CodecProviderRuntime.Shared here. The resolver lambda is dormant until a
        // non-core suffix genuinely needs an optional codec, so installing dozens of codec DLLs has
        // zero effect on normal cold launch or common JPEG/PNG browsing.
        _decoderBackend = new CodecProviderDecoderBackend(lazyResolver: path =>
        {
            var provider = CodecProviderRuntime.Shared.ResolveProvider(path);
            if (provider is not null)
                _diagnostics.Write("codec", "provider_resolved_on_demand", new { provider = provider.Info.Id, provider.Info.Version, path = Path.GetExtension(path) });
            return provider;
        });
        _loader = new ImageLoadCoordinator(_decoderBackend);
        _loader.IsBitmapInActiveUse = bitmap => bitmap is not null && ReferenceEquals(Viewport.Bitmap, bitmap);
        _providerFailureHandler = (provider, error) => _diagnostics.WriteCritical("codec", "provider_failure", new { provider, error });
        _decoderBackend.ProviderFailure += _providerFailureHandler;
        _tabAttach = new TabAttachCoordinator(this, (category, name, data) => _diagnostics.Write(category, name, data));
        _physicalAttach = new TabAttachCoordinator(this, (category, name, data) => _diagnostics.Write(category, name, data));
        _tabDrag = new TabDragController(CreateTabDragHost(), _tabAttach);
        Viewport.SelectionOverlayTarget = SelectionOverlay;
        Viewport.PriorBitmapReleased += bitmap =>
        {
            if (bitmap is not null && !_loader.IsBitmapCached(bitmap))
                bitmap.Dispose();
        };
        // Window-in-window overlay decoding and slideshow infrastructure are intentionally lazy.
        // A plain cold file-open should construct only the primary image path before first pixels.
        GlidePerformanceTrace.Mark("ctor_create_commands_start");
        _commands = CreateCommandDispatcher();
        GlidePerformanceTrace.Mark("ctor_create_commands_end");
        // Tunnel handlers preserve legacy drag-anywhere behavior even when a TextBlock/ScrollViewer
        // sits under the pointer. Interactive controls are explicitly excluded by the handlers.
        MainRoot.AddHandler(InputElement.PointerPressedEvent, AutoDismissTransientPanels, RoutingStrategies.Tunnel, true);
        MainRoot.AddHandler(InputElement.PointerPressedEvent, MainPointerShortcutPressed, RoutingStrategies.Tunnel, true);
        MainRoot.AddHandler(InputElement.PointerPressedEvent, WholeAppOverlayBodyPointerPressed, RoutingStrategies.Tunnel, true);
        MainRoot.AddHandler(InputElement.PointerWheelChangedEvent, WholeAppOverlayPointerWheelChanged, RoutingStrategies.Tunnel, true);
        ChromeBorder.AddHandler(InputElement.PointerPressedEvent, ChromePointerPressed, RoutingStrategies.Tunnel, true);
        ChromeBorder.AddHandler(InputElement.PointerReleasedEvent, ChromeMiddleClickReleased, RoutingStrategies.Tunnel, true);
        ChromeBorder.AddHandler(InputElement.PointerMovedEvent, ChromePointerMovedForDrag, RoutingStrategies.Bubble, true);
        ChromeBorder.AddHandler(InputElement.PointerReleasedEvent, (_, _) => ClearPendingChromeDrag(), RoutingStrategies.Bubble, true);
        ChromeBorder.PointerCaptureLost += (_, _) => _pendingChromeDragPress = null;
        ViewerStatusSurface.AddHandler(InputElement.PointerPressedEvent, StatusSurfacePointerPressed, RoutingStrategies.Tunnel, true);
        // Ctrl+Tab MRU cycling: remember whether the triggering key was chorded with Ctrl, and end the
        // cycle (committing the landed-on tab as most recent) when Ctrl is released or focus leaves.
        AddHandler(InputElement.KeyDownEvent, (_, e) => _lastKeyDownHadCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), RoutingStrategies.Tunnel, true);
        AddHandler(InputElement.KeyUpEvent, (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl) { _lastKeyDownHadCtrl = false; EndTabMruCycle(); } }, RoutingStrategies.Tunnel, true);
        Deactivated += (_, _) => { _lastKeyDownHadCtrl = false; EndTabMruCycle(); CloseActiveOwnedMenu(); };
        PositionChanged += (_, _) => CloseActiveOwnedMenu();
        AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            // A press anywhere in the window while an owned menu is open dismisses it, so a
            // stray popup can never remain after the click that should have closed it.
            if (_activeOwnedMenu is { IsOpen: true } open && !e.GetCurrentPoint(this).Properties.IsRightButtonPressed &&
                !IsWithinControl(e.Source, open)) CloseActiveOwnedMenu();
        }, RoutingStrategies.Tunnel, true);
        // Tabs no longer shrink until they overlap; when they overflow, the wheel scrolls the strip.
        TabStripScroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            var maxOffset = Math.Max(0, TabStripScroll.Extent.Width - TabStripScroll.Viewport.Width);
            if (maxOffset <= 1) return;
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            TabStripScroll.Offset = new Vector(Math.Clamp(TabStripScroll.Offset.X - delta * 60, 0, maxOffset), TabStripScroll.Offset.Y);
            UpdateTabOverflowButtons();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // The slideshow button lives inside ImageView, whose overlay/input layers also observe pointer
        // events. Register a target-level handled-events-too route so the status control remains
        // authoritative even when an overlay happens to overlap the status surface.
        // Status/chrome controls must own the physical press even when the image/overlay input
        // surface has already marked the routed event handled. This is deliberately uniform: the
        // slideshow button previously had this protection alone, which is why it was the only
        // reliable status control in the field.
        ViewerStatusSurface.AddHandler(InputElement.PointerPressedEvent, ReliableStatusSurfaceButtonPressed, RoutingStrategies.Tunnel, true);
        CaptionMinimizeButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        CaptionMaximizeButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        CaptionCloseButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        TabNavigationHost.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                var posBack = e.GetPosition(TabNavBackButton);
                if (TabNavBackButton.IsVisible && new Rect(0, 0, TabNavBackButton.Bounds.Width, TabNavBackButton.Bounds.Height).Contains(posBack))
                {
                    TabNavBackButton.ContextMenu?.Open(TabNavBackButton);
                    e.Handled = true;
                    return;
                }
                var posFwd = e.GetPosition(TabNavForwardButton);
                if (TabNavForwardButton.IsVisible && new Rect(0, 0, TabNavForwardButton.Bounds.Width, TabNavForwardButton.Bounds.Height).Contains(posFwd))
                {
                    TabNavForwardButton.ContextMenu?.Open(TabNavForwardButton);
                    e.Handled = true;
                    return;
                }
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, DragOverReceived, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DragOverEvent, DragOverReceived, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(DragDrop.DropEvent, DropReceived, RoutingStrategies.Bubble, true);
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        AddHandler(InputElement.KeyDownEvent, GlobalKeyDown, RoutingStrategies.Tunnel, true);
        PointerMoved += MainWindowPointerMoved;
        PointerReleased += MainWindowPointerReleased;
        SizeChanged += (_, _) =>
        {
            // Native non-client resizing can end without a normal Avalonia release sequence. Clear
            // transient pointer ownership both during and just after the resize so a stale secondary
            // button/capture can never make the next physical left click behave like a right click.
            EndManualWindowDrag();
            Viewport.CancelInteraction();
            _resizeInputResetCts?.Cancel();
            _resizeInputResetCts?.Dispose();
            var resizeReset = _resizeInputResetCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(90, resizeReset.Token).ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (resizeReset.IsCancellationRequested) return;
                        EndManualWindowDrag();
                        Viewport.CancelInteraction();
                    }, DispatcherPriority.Input);
                }
                catch (OperationCanceledException) { }
            });

            TrackNormalWindowPlacement();
            SaveCurrentWholeAppOverlayPlacement();
            ApplyCompactChromeLayout();
            ApplyStatusBarSize();
            if (_wholeAppOverlayMode) Dispatcher.UIThread.Post(AlignWholeAppOverlayChromeToPixels, DispatcherPriority.Render);
            if (_tabDrag.IsActive || _physicalDraggedTabId is not null) return;
            if (Math.Abs(Bounds.Width - _lastTabLayoutWidth) < 24) return;
            _lastTabLayoutWidth = Bounds.Width;
            RebuildTabStrip();
        };
        // PositionChanged is raised from inside Windows' modal move loop (WM_WINDOWPOSCHANGED). An
        // exception escaping here crosses the native WndProc boundary and fast-fails the process, so
        // this handler must never throw. Heavy settings cloning/publishing is deferred until the
        // move loop ends (ResetPointerStateAfterNativeResize).
        PositionChanged += (_, _) =>
        {
            if (_nativeSizeMoveActive) return;
            try { TrackNormalWindowPlacement(); SaveCurrentWholeAppOverlayPlacement(); }
            catch (Exception ex) { _diagnostics.Write("window", "position_changed_failed", new { ex.Message }); }
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property != WindowStateProperty) return;
            var oldState = _lastKnownWindowState;
            var newState = WindowState;
            _lastKnownWindowState = newState;
            if (oldState != WindowState.Minimized)
                _stateBeforeMinimize = oldState;

            ApplyChromeLayoutForWindowState();
            Viewport.IsFullscreen = WindowState == WindowState.FullScreen;
            ApplySelectionBuffer();
            Viewport.RightDragWindowMoveAllowed = WindowState == WindowState.Normal;
            Viewport.BackgroundWindowDragEnabled = IsLeftWindowMoveAllowedForCurrentState();
            ApplyStatusBarSize();
            ApplyStatusVisibility();
            UpdateIntegratedTitleBarInset();
            UpdateCaptionButtonState();
            if (newState == WindowState.Minimized)
            {
                SaveWindowPlacement();
                if (_settings.PurgeCacheOnMinimize)
                    _loader.PurgeCaches();
                // Minimize is a genuine Windows minimize: the window stays in the taskbar and can be
                // restored exactly as the user expects. Speed Boost standby is reserved for *closing*
                // the last window (see ShouldEnterSpeedBoostStandby / the Closing handler), so
                // minimizing must never hide the render window and leave only the tray icon. Entering
                // standby here also depended on the live window count, which made minimize behave
                // differently ("the window sometimes just disappeared") depending on how many Glide
                // windows were open.
            }
        };
        ApplyChromeLayoutForWindowState();
        ApplyCompactChromeLayout();
        UpdateIntegratedTitleBarInset();
        UpdateCaptionButtonState();

        Viewport.BrowseRequested += (_, delta) => _ = NavigateFromViewportAsync(delta);
        Viewport.WheelBrowseRequested += (_, delta) => QueueWheelNavigation(delta);
        Viewport.DoubleTapped += async (_, e) =>
        {
            var fullscreen = WindowState == WindowState.FullScreen;
            var position = e.GetPosition(Viewport);
            // The authoritative 24-slot gesture contract has a windowed image double-click and
            // an empty-background double-click, but no separate fullscreen double-click slot.
            // Fullscreen double-click therefore remains governed by the explicit fullscreen setting.
            var slot = !fullscreen
                ? (Viewport.IsPointOverImage(position) ? "windowed.doubleLeftImage" : "background.doubleLeft")
                : null;
            if (slot is not null)
            {
                var configured = _inputRouter.ResolveGesture(slot, _settings.Gestures);
                if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
                {
                    await ExecuteGestureActionAsync(configured);
                    e.Handled = true;
                    return;
                }
            }
            if ((!fullscreen && _settings.DoubleClickFullscreen) || (fullscreen && _settings.DoubleClickExitFullscreen))
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        };
        Viewport.GestureActionRequested += async (_, e) => await ExecuteGestureActionAsync(e.ActionId);
        Viewport.InteractionDiagnostic += (name, data) =>
        {
            if (string.Equals(name, "selection_zoom_in", StringComparison.OrdinalIgnoreCase)) _selectionZoomDemandPath = _currentPath;
            if (string.Equals(name, "selection_zoom_out", StringComparison.OrdinalIgnoreCase)) _selectionZoomDemandPath = null;
            _diagnostics.Write("viewport", name, data);
        };
        Viewport.SelectionZoomOutCursorPressedChanged += pressed => _windowsBrowserFrame?.SetSelectionZoomOutPressed(pressed);
        Viewport.NativeWindowDragRequested += (_, e) => BeginNativeWindowDrag(e);
        Viewport.DeferredRightWindowDragRequested += (pointer, press, current) =>
            BeginDeferredRightWindowDrag(pointer, Viewport.PointToScreen(press), Viewport.PointToScreen(current));
        Viewport.ViewChanged += (_, _) =>
        {
            UpdateViewportScrollbars(); UpdateStatusStats(); UpdatePictureCounter(); UpdateWindowTitle();
            PublishViewportDemand();
        };
        ViewportHScroll.PropertyChanged += (_, args) =>
        {
            if (!_syncingViewportScrollbars && args.Property == RangeBase.ValueProperty)
                Viewport.SetHorizontalScrollOffset(ViewportHScroll.Value);
        };
        ViewportVScroll.PropertyChanged += (_, args) =>
        {
            if (!_syncingViewportScrollbars && args.Property == RangeBase.ValueProperty)
                Viewport.SetVerticalScrollOffset(ViewportVScroll.Value);
        };
        TransparencySlider.PropertyChanged += (_, args) =>
        {
            if (args.Property != RangeBase.ValueProperty) return;
            _sessionOpacity = OverlayChromePolicy.ClampWholeWindowOpacity(TransparencySlider.Value / 100.0);
            Opacity = _sessionOpacity;
            TransparencyValueText.Text = $"{(int)Math.Round(_sessionOpacity * 100)}%";
            if (_wholeAppOverlayChromeVisual is { } wholeChrome)
            {
                wholeChrome.OpacityValue = _sessionOpacity;
                wholeChrome.InvalidateVisual();
            }
            if (_wholeAppOverlayOpacityHitTarget is { } wholeSlider && Math.Abs(wholeSlider.Value - _sessionOpacity) > 0.0001)
                wholeSlider.Value = _sessionOpacity;
            RefreshDynamicTooltips();
        };
        OverlayOpacitySlider.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty) _overlays?.SetSelectedOpacity(OverlayOpacitySlider.Value / 100.0); };
        Viewport.ContextMenuRequested += _ =>
        {
            // Gesture-owned only: never attach this transient menu to Viewport.ContextMenu. Doing so
            // lets Avalonia auto-open it on later right-button releases even after zoom-out or panning.
            var menu = BuildViewerContextMenu();
            menu.Open(Viewport);
        };
        Viewport.SlideshowRightClickStopRequested += () => StopSlideshow(restoreStartingMode: true);
        Viewport.SlideshowLeftClickRequested += () => _ = HandleSlideshowLeftClickAsync();
        AddHandler(InputElement.KeyDownEvent, OverlayKeyboardShortcut, RoutingStrategies.Tunnel, true);

        _fullscreenCursorTimer.Interval = FullscreenCursorAutoHide.TickInterval;
        _fullscreenCursorTimer.Tick += (_, _) =>
        {
            if (WindowState != WindowState.FullScreen || !_settings.HideCursorFullscreen || !IsActive)
            {
                StopFullscreenCursorAutoHide();
                return;
            }
            if (_cursorAutoHide.Tick(DateTime.UtcNow)) ApplyFullscreenCursorVisibility();
        };

        Closed += (_, _) =>
        {
            _diagnostics.Write("window", "closed", new
            {
                lifetime = _windowLifetimeId, forceProcessExit = _forceProcessExit,
                speedBoostStandby = _closeForSpeedBoostStandby, state = WindowState.ToString(),
                active = _workspace.Active?.Kind.ToString(), currentPath = _currentPath
            });
            // Overlay persistence is a full-settings decision. A compact cold-launch snapshot must
            // never delete or overwrite the persisted layout on a very-early close.
            if (_deferredStartupSettingsAdopted)
            {
                var automaticOverlayLayout = AutomaticOverlayLayoutPath();
                if (_settings.OverlayPersistLayoutBetweenSessions)
                {
                    try { if (_overlays is { } overlays) { Directory.CreateDirectory(Path.GetDirectoryName(automaticOverlayLayout)!); overlays.SaveLayout(automaticOverlayLayout, preserveViewState: true); } } catch { }
                }
                else
                {
                    try { if (File.Exists(automaticOverlayLayout)) File.Delete(automaticOverlayLayout); } catch { }
                }
            }
            _overlays?.Dispose();
            _slideshow?.Dispose();
            _loader.Dispose();
            _overlayLoader?.Dispose();
            Viewport.SetInteractionBitmap(null);
            _interactionPreviewBitmap?.Dispose();
            _interactionPreviewBitmap = null;
            _decoderBackend.ProviderFailure -= _providerFailureHandler;
            _decoderBackend.Dispose();
            _loadCts?.Cancel(); _loadCts?.Dispose(); _loadCts = null;
            _hardTopmostTimer.Stop();
            _folderIndexCts?.Cancel(); _folderIndexCts?.Dispose(); _folderIndexCts = null;
            _folderWatch?.Dispose(); _folderWatch = null;
            _metadataCts?.Cancel(); _metadataCts?.Dispose(); _metadataCts = null;
            CancelPreviewQualityRecovery();
            CancelBrowserThumbnailWork();
            EndManualWindowDrag();
            // Closing must clear an ordinary in-strip drag too; otherwise a destination/source
            // preview can remain logically armed until the next pointer event.
            ClearTabDragState();
            _physicalAttach.Clear();
            GlideWindowRegistry.Unregister(this);
            if (_closeForSpeedBoostStandby)
            {
                ExternalLaunchBroker.DetachForStandby(this);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    ReferenceEquals(desktop.MainWindow, this))
                    desktop.MainWindow = null;
            }
            else
                ExternalLaunchBroker.Unregister(this);
        };

        Activated += (_, _) =>
        {
            if (WindowState == WindowState.FullScreen) ResetFullscreenCursorTimer();
            ExternalLaunchBroker.MarkActive(this);
            ReassertNativeTopmost();
            if (_settings.SlideshowPauseWhenInactive) _slideshow?.ResumeFromInactivity();
            // Overlay glow/chrome is an active-window affordance. Do not leave a luminous frame
            // floating over another application while Glide is not the foreground window.
            if (_wholeAppOverlayMode && _wholeAppOverlayControlsHost is { } overlayControls)
            {
                overlayControls.IsVisible = false; // reveal again only when the pointer touches an edge
                _wholeAppOverlayChromeVisual?.InvalidateVisual();
            }
        };

        Deactivated += (_, _) =>
        {
            StopFullscreenCursorAutoHide();
            if (_settings.SlideshowPauseWhenInactive) _slideshow?.PauseForInactivity();
            if (_wholeAppOverlayMode && _wholeAppOverlayControlsHost is { } overlayControls)
                overlayControls.IsVisible = false;
            EndManualWindowDrag();
            // A missed KeyUp while activation is lost must not leave the held-navigation producer
            // skipping images in the background (reported on laptop keyboards).
            if (_heldNavigationKeyIsDown && _heldNavigationDirection != 0)
                _suppressedHeldNavigationDirection = _heldNavigationDirection;
            StopHeldNavigation(settle: false, reason: "window_deactivated");
            _homeKeyDown = false;
            // Losing activation must never strand an ordinary tab/viewport pointer capture.
            // A detached tab inside the native Windows move loop is intentionally exempt: the OS
            // owns that physical drag until the real button is released.
            if (_physicalDraggedTabId is null)
            {
                ClearTabDragState();
                Viewport.CancelInteraction();
            }
        };

        Closing += async (_, e) =>
        {
            _diagnostics.Write("window", "closing_requested", new
            {
                forceProcessExit = _forceProcessExit, state = WindowState.ToString(),
                active = _workspace.Active?.Kind.ToString(), currentPath = _currentPath,
                suppressUntil = Volatile.Read(ref _suppressCloseUntilTick), now = Environment.TickCount64
            });
            if (_forceProcessExit) return;
            // The internal standby close must never be cancelled by user-facing close policies
            // (home-suppression window, fullscreen X handling). A cancelled standby close leaves a
            // hidden window that still owns the broker, so Explorer opens never get a receiver.
            if (_closeForSpeedBoostStandby) return;
            if (Environment.TickCount64 < Volatile.Read(ref _suppressCloseUntilTick))
            {
                e.Cancel = true;
                _diagnostics.Write("window", "close_suppressed_after_home", new { remainingMs = Volatile.Read(ref _suppressCloseUntilTick) - Environment.TickCount64 });
                return;
            }

            // Speed Boost standby hides this HWND and then posts Close(); a manual caption close can
            // also arrive while the window is hidden. Showing the modal multi-tab confirmation against
            // a non-visible owner throws in Avalonia and, because this handler is async void, the
            // escaped exception terminates the process (the 2026-09-17 startup-crash.log regression).
            // Confirmation is only meaningful for a visible, user-initiated close, so skip it during
            // standby and for any hidden-owner teardown.
            if (!_closeConfirmed && !_closeConfirmationInProgress && IsVisible && !_closeForSpeedBoostStandby &&
                _settings.ConfirmCloseMultipleTabs && _workspace.Tabs.OfType<ImageTabState>().Count() > 1)
            {
                e.Cancel = true;
                _closeConfirmationInProgress = true;
                try
                {
                    var choice = _settings.RememberedMultiTabCloseChoice;
                    if (string.Equals(choice, "Ask", StringComparison.OrdinalIgnoreCase))
                        choice = await ShowMultiTabCloseDialogAsync();
                    if (string.Equals(choice, "Close current tab", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_workspace.Active is { } active) await CloseTabWithPolicyAsync(active.Id, "window_close_dialog");
                        return;
                    }
                    if (!string.Equals(choice, "Close all", StringComparison.OrdinalIgnoreCase)) return;
                    _closeConfirmed = true;
                    Dispatcher.UIThread.Post(Close);
                }
                catch (Exception ex)
                {
                    // Never let a confirmation failure escape an async void handler. Keep the window
                    // open and log instead of crashing the process.
                    _diagnostics.Write("window", "multi_tab_close_confirmation_failed", new { error = ex.GetType().Name, ex.Message });
                }
                finally { _closeConfirmationInProgress = false; }
                return;
            }
            if (WindowState == WindowState.FullScreen && !_forceCloseFromCaptionAction && !_settings.FullscreenXClosesApp)
            {
                e.Cancel = true;
                ToggleFullscreen();
                return;
            }
            // IMPORTANT: Avalonia's Closing event is synchronous even when the handler is async.
            // If Speed Boost is going to keep the last window resident, cancellation must be set
            // BEFORE the first await. Previously we awaited RecentHistoryStore.FlushAsync() and only
            // then set e.Cancel; by that point the native close could already have completed, tearing
            // down the MainWindow, broker, decoder and warm process. That made the X button defeat
            // the resident Launch Speed Boost design.
            var enterSpeedBoostStandby = ShouldEnterSpeedBoostStandby();
            if (enterSpeedBoostStandby)
                e.Cancel = true;

            // The internal Speed Boost standby close must not overwrite the user's placement.
            if (!_closeForSpeedBoostStandby)
            {
                CaptureActiveTabRuntimeState();
                WorkspaceSessionStore.Save(_workspace.Tabs, _workspace.ActiveIndex);
                await RecentHistoryStore.FlushAsync();
                SaveWindowPlacement();
            }

            if (enterSpeedBoostStandby)
            {
                EnterSpeedBoostStandby();
                return;
            }
        };

        Opened += async (_, _) =>
        {
            DisableDwmTransitions("opened");
            GlidePerformanceTrace.Mark("window_opened_start");
            GlideWindowRegistry.Register(this);
            ExternalLaunchBroker.Start(this);
            App.UpdateTrayIconState();
            // Never wait for settings I/O/deserialization on first presentation. On a normal cold
            // launch the parallel load is usually complete by now; otherwise Glide renders with
            // safe defaults and hydrates secondary behavior immediately after the task completes.
            AdoptStartupSettingsIfReady(applyVisuals: false);
            ContinueDeferredStartupSettingsAdoption();
            if (_settings.StartupDiagnostics) _diagnostics.Enable();
            _diagnostics.Write("app", "opened", new { renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0, settingsPath = SettingsStore.GetSettingsPath() });

            var explicitStartupPaths = _earlyStartupPaths ?? (!_deferWorkspaceActivation ? ApplyQueuedStartupDestinations() : Array.Empty<string>());
            var isColdImageOpen = (!_deferWorkspaceActivation && explicitStartupPaths.Length > 0 && _workspace.Active is ImageTabState) || _earlyStartupTask is not null;

            ApplySettingsVisuals(deferSecondaryChrome: isColdImageOpen);
            ApplyStartupWholeAppOverlayPreference();

            if (!isColdImageOpen)
            {
                if (!_deferWorkspaceActivation && explicitStartupPaths.Length == 0 && !_hasExplicitStartupOpen)
                {
                    // No-file startup actions depend on the full graph. Await hydration only on this
                    // non-image path so the decision is deterministic without taxing explicit cold opens.
                    if (!_deferredStartupSettingsAdopted && App.StartupSettingsTask is { } startupSettingsTask)
                    {
                        try { await startupSettingsTask; } catch { }
                        if (!_windowLifetimeCts.IsCancellationRequested) AdoptStartupSettingsIfReady(applyVisuals: true);
                    }
                    if (!_windowLifetimeCts.IsCancellationRequested) ApplyConfiguredStartupWorkspace();
                }
                TryScheduleAutomaticOverlayLayoutRestore();
                RebuildTabStrip();
            }
            else
            {
                // Paint the functional shell and present the startup image immediately on the first frame.
                // Pipelined background pre-decoding ensures the bitmap is already waiting in memory.
                ShowImageSurface();
            }

            GlidePerformanceTrace.Mark("opened_call_complete_startup_start");
            await CompleteOpenedStartupAsync(explicitStartupPaths);
            GlidePerformanceTrace.Mark("opened_call_complete_startup_end");

            // Setting Avalonia's WindowState before the HWND is shown can make the client area
            // occupy the screen while Windows still considers the HWND restored. Reassert the
            // native maximize command after the real frame exists so the taskbar, Snap and edge
            // maximization state match an actual click on Windows' maximize button.
            Dispatcher.UIThread.Post(RestoreValidatedStartupNormalPosition, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(EnsureNativeMaximizedState, DispatcherPriority.Loaded);

            if (!string.IsNullOrWhiteSpace(App.CapturePrintDialogImage))
                Dispatcher.UIThread.Post(async () => await CapturePrintDialogForDiagnosticsAsync(App.CapturePrintDialogImage!, App.CapturePrintDialogOutput!), DispatcherPriority.Background);
        };

        // Resolve the final startup geometry before Windows ever shows the HWND. Applying reference
        // size/remembered placement from Opened made the real window visibly resize/reposition after
        // its first composition, reads as a second load even with no external launcher.
        GlidePerformanceTrace.Mark("ctor_placement_start");
        ApplyInitialReferenceSizeAndPlacement();
        GlidePerformanceTrace.Mark("ctor_placement_end");
    }
}

