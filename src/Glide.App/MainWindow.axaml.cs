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
    private readonly CodecProviderDecoderBackend _decoderBackend;
    private readonly Action<string, string> _providerFailureHandler;
    private readonly ImageLoadCoordinator _loader;
    private ImageLoadCoordinator? _overlayLoader;
    private string? _selectionZoomDemandPath;
    private readonly InputRouter _inputRouter = new();
    private readonly WorkspaceState _workspace = new();
    private readonly DispatcherTimer _fullscreenCursorTimer = new();
    private CancellationTokenSource? _resizeInputResetCts;
    private CancellationTokenSource? _navigationBoundaryNoticeCts;
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
    private bool _closeConfirmationInProgress;
    private bool _closeConfirmed;
    private bool _forceProcessExit;
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
    private bool _forceCloseFromCaptionAction;
    private PixelPoint _lastNormalWindowPosition;
    private Size _lastNormalWindowSize;
    private bool _normalPlacementKnown;
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

    private sealed record WholeAppOverlaySnapshot(
        WindowState RestoreState, PixelPoint Position, double Width, double Height,
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
        _providerFailureHandler = (provider, error) => _diagnostics.WriteCritical("codec", "provider_failure", new { provider, error });
        _decoderBackend.ProviderFailure += _providerFailureHandler;
        _tabAttach = new TabAttachCoordinator(this, (category, name, data) => _diagnostics.Write(category, name, data));
        _physicalAttach = new TabAttachCoordinator(this, (category, name, data) => _diagnostics.Write(category, name, data));
        _tabDrag = new TabDragController(CreateTabDragHost(), _tabAttach);
        Viewport.SelectionOverlayTarget = SelectionOverlay;
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
        ChromeBorder.AddHandler(InputElement.PointerPressedEvent, ChromePointerPressed, RoutingStrategies.Tunnel, true);
        ChromeBorder.AddHandler(InputElement.PointerReleasedEvent, ChromeMiddleClickReleased, RoutingStrategies.Tunnel, true);
        ViewerStatusSurface.AddHandler(InputElement.PointerPressedEvent, StatusSurfacePointerPressed, RoutingStrategies.Tunnel, true);
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
        AddHandler(DragDrop.DropEvent, DropReceived);
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
            ApplyCompactChromeLayout();
            ApplyStatusBarSize();
            if (_tabDrag.IsActive || _physicalDraggedTabId is not null) return;
            if (Math.Abs(Bounds.Width - _lastTabLayoutWidth) < 24) return;
            _lastTabLayoutWidth = Bounds.Width;
            RebuildTabStrip();
        };
        PositionChanged += (_, _) => TrackNormalWindowPlacement();
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
                if (IsVisible && !_closeForSpeedBoostStandby && _settings.SpeedBoostEnabled && GlideWindowRegistry.Snapshot().Count <= 1)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (WindowState == WindowState.Minimized && !_closeForSpeedBoostStandby)
                            EnterSpeedBoostStandby();
                    }, DispatcherPriority.Background);
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
        AddHandler(InputElement.KeyDownEvent, OverlayKeyboardShortcut, RoutingStrategies.Tunnel, true);

        _fullscreenCursorTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _fullscreenCursorTimer.Tick += (_, _) =>
        {
            _fullscreenCursorTimer.Stop();
            if (WindowState == WindowState.FullScreen && _settings.HideCursorFullscreen)
                Cursor = new Cursor(StandardCursorType.None);
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
            _folderIndexCts?.Cancel(); _folderIndexCts?.Dispose(); _folderIndexCts = null;
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
            ExternalLaunchBroker.MarkActive(this);
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
            if (_settings.SlideshowPauseWhenInactive) _slideshow?.PauseForInactivity();
            if (_wholeAppOverlayMode && _wholeAppOverlayControlsHost is { } overlayControls)
                overlayControls.IsVisible = false;
            EndManualWindowDrag();
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
            if (Environment.TickCount64 < Volatile.Read(ref _suppressCloseUntilTick))
            {
                e.Cancel = true;
                _diagnostics.Write("window", "close_suppressed_after_home", new { remainingMs = Volatile.Read(ref _suppressCloseUntilTick) - Environment.TickCount64 });
                return;
            }

            if (!_closeConfirmed && !_closeConfirmationInProgress && _settings.ConfirmCloseMultipleTabs && _workspace.Tabs.OfType<ImageTabState>().Count() > 1)
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
        };

        // Resolve the final startup geometry before Windows ever shows the HWND. Applying reference
        // size/remembered placement from Opened made the real window visibly resize/reposition after
        // its first composition, reads as a second load even with no external launcher.
        GlidePerformanceTrace.Mark("ctor_placement_start");
        ApplyInitialReferenceSizeAndPlacement();
        GlidePerformanceTrace.Mark("ctor_placement_end");
    }

    private async Task CompleteOpenedStartupAsync(IReadOnlyList<string> explicitStartupPaths)
    {
        if (_windowLifetimeCts.IsCancellationRequested) return;
        if (!_deferWorkspaceActivation)
        {
            if (_earlyStartupTask is not null)
            {
                GlidePerformanceTrace.Mark("opened_early_startup_await_start");
                await _earlyStartupTask;
                GlidePerformanceTrace.Mark("opened_early_startup_await_end");
            }
            else
            {
                // Do not warm/read the startup file in parallel with the authoritative decoder. On a true
                // cold open that duplicated I/O can contend with WIC/Skia and pollute the file cache. The
                // foreground decoder is now the single owner of startup image reads.
                GlidePerformanceTrace.Mark("activate_workspace_start");
                await ActivateWorkspaceAsync();
                GlidePerformanceTrace.Mark("activate_workspace_end");
            }
        }
        if (_windowLifetimeCts.IsCancellationRequested) return;

        // Explicit launch history is bookkeeping, never a prerequisite for first useful pixels.
        if (_settings.RecentHistoryEnabled)
        {
            foreach (var path in explicitStartupPaths)
            {
                if (Directory.Exists(path)) RecentHistoryStore.RecordFolder(path, _settings.HistorySize);
                else if (File.Exists(path) && ImageNavigator.IsSupported(path)) RecentHistoryStore.RecordFile(path, _settings.HistorySize);
            }
        }
        BeginDeferredRecentHistoryInitialization();
        Dispatcher.UIThread.Post(() =>
            MainRoot.AddHandler(InputElement.PointerReleasedEvent, WholeAppOverlayContextPointerReleased, RoutingStrategies.Bubble, false),
            DispatcherPriority.Background);
        if (!_diagnostics.Enabled) Dispatcher.UIThread.Post(_diagnostics.Enable, DispatcherPriority.Background);
        if (_settings.StartupDiagnostics) WriteStartupDiagnostics("window_opened");

        if (App.AutoExportDiagnostics || string.Equals(Environment.GetEnvironmentVariable("GLIDE_AUTO_EXPORT_DIAGNOSTICS"), "1", StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var target = App.AutoExportDiagnosticsFolder ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_DIR") ?? Environment.GetEnvironmentVariable("GLIDE_DIAGNOSTICS_EXPORT_DIR");
                    await RunFullDiagnosticExportAsync(target);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Auto-diagnostic export failed: {ex}");
                }
                finally
                {
                    Close();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private WindowInWindowOverlayManager EnsureOverlays()
    {
        if (_overlays is { } existing) return existing;
        _overlayLoader ??= new ImageLoadCoordinator(_decoderBackend);
        var overlays = new WindowInWindowOverlayManager(OverlayCanvas, ImageView, _overlayLoader)
        {
            ImageRectForSize = size => Viewport.GetPresentationRectForViewportSize(size),
            IsFullscreen = () => WindowState == WindowState.FullScreen,
            FullscreenNavigationRequested = () => _ = NavigateAsync(1)
        };
        overlays.Diagnostic += (name, data) => _diagnostics.Write("overlay", name, data);
        overlays.OverlayCountChanged += UpdateOverlayStatusButtons;
        overlays.OpenFileLocationRequested = path => RevealInWindowsExplorer(path, directory: false);
        overlays.OpenInNewTabRequested = path => Dispatcher.UIThread.Post(async () => await OpenAsImageTabAsync(path));
        overlays.OpenWithRequested = path => WindowsOpenWith.Show(path);
        ApplyOverlaySettingsToManager(overlays);
        _overlays = overlays;
        return overlays;
    }

    private void ApplyOverlaySettingsToManager(WindowInWindowOverlayManager overlays)
    {
        overlays.DefaultOpacity = Math.Clamp(_settings.OverlayDefaultOpacity / 100.0, .1, 1.0);
        overlays.WheelZoomEnabled = _settings.OverlayWheelZoom;
        overlays.CtrlWheelZoomEnabled = _settings.CtrlWheelZoom;
        overlays.RightDragPanEnabled = _settings.OverlayRightDragPan;
        overlays.HighlightSelected = _settings.OverlaySelectedHighlight;
        overlays.RememberZoom = _settings.OverlayRememberZoom;
        overlays.ZoomStepPercent = Math.Clamp(_settings.OverlayZoomStepPercent, 1, 100);
        overlays.AdaptivePositioningEnabled = !_settings.OverlayAbsoluteCoordinatesOnResize;
        overlays.ScaleWithHostResize = _settings.OverlayScaleWithWindow;
        overlays.AccentBrush = new SolidColorBrush(Color.Parse(CurrentAccentHex()));
        overlays.ResolveGesture = slot => _inputRouter.ResolveGesture(slot, _settings.Gestures);
        overlays.RefreshTheme();
    }

    private SlideshowSessionController EnsureSlideshow()
    {
        if (_slideshow is { } existing) return existing;
        var slideshow = new SlideshowSessionController(
            new DispatcherTimer(), () => ImageView.IsVisible && !string.IsNullOrWhiteSpace(_currentPath),
            () => _navigator.Count, () => _navigator.Index, () => _settings.SlideshowCrossFolders,
            () => _settings.SlideshowLoop, () => _settings.SlideshowShuffle,
            () => string.Equals(_settings.SlideshowDirection, "Backward", StringComparison.OrdinalIgnoreCase) ? -1 : 1,
            () => _settings.SlideshowStartFullscreen, () => _settings.SlideshowIntervalMs,
            ConfirmSlideshowSessionOptionsAsync, Viewport.CaptureViewState, Viewport.RestoreViewState,
            () => WindowState == WindowState.FullScreen, ToggleFullscreen,
            direction => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(direction, forceDirectionalEdge: true)),
            direction => RunSerializedNavigationAsync(() =>
            {
                _lastNavigationDirection = Math.Sign(direction);
                _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                if (direction < 0) _navigator.Last(); else _navigator.First();
                if (_navigator.Count > 1) _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
                return Task.CompletedTask;
            }),
            direction => RunSerializedNavigationAsync(() =>
            {
                _lastNavigationDirection = Math.Sign(direction);
                _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                _navigator.Move(direction);
                if (_navigator.Count > 1) _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
                return Task.CompletedTask;
            }),
            index => RunSerializedNavigationAsync(() =>
            {
                _lastNavigationDirection = Math.Sign(index - _navigator.Index);
                _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                _navigator.SelectIndex(index);
                if (_navigator.Count > 1) _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
                return Task.CompletedTask;
            }),
            // Slideshow should consume the prepared neighbour frame immediately. Re-check session
            // state *after* acquiring the navigation gate: Home/manual input may have stopped the
            // slideshow while this continuation was queued behind another navigation operation.
            () => RunSerializedNavigationAsync(async () =>
            {
                if (_slideshow?.IsRunning != true || !ImageView.IsVisible) return;
                await PresentCurrentAsync(forceFull: false).ConfigureAwait(true);
            }), UpdateSlideshowUi,
            (name, data) => _diagnostics.Write("slideshow", name, data));
        _slideshow = slideshow;
        return slideshow;
    }

    internal MainWindow(TabState transferredTab, GlideSettingsState inheritedSettings, bool deferWorkspaceActivation = false) : this()
    {
        _settings = inheritedSettings.CloneState();
        _startupSettingsBaseline = _settings.CloneState();
        _deferredStartupSettingsAdopted = true;
        _deferWorkspaceActivation = deferWorkspaceActivation;
        _workspace.ResetForDetached(transferredTab, _settings.DetachedWindowHomeTab);
    }

    public void QueueOpen(string path)
    {
        _hasExplicitStartupOpen = true;
        _queuedOpen = path;
        _diagnostics.Write("launch", "queue_open", new { path });
    }
    public void QueueOpenPaths(IEnumerable<string> paths)
    {
        var materialized = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (materialized.Length > 0) _hasExplicitStartupOpen = true;
        _startupPaths.Add(materialized,
            path => Directory.Exists(path) || (File.Exists(path) && ImageNavigator.IsSupported(path)),
            (path, reason) => _diagnostics.Write("launch", reason.StartsWith("invalid", StringComparison.OrdinalIgnoreCase) ? "invalid_path_skipped" : "unsupported_path_skipped", new { path, reason }));
    }

    public void TryPrepareEarlyStartupPresentation()
    {
        if (_deferWorkspaceActivation) return;
        var explicitStartupPaths = ApplyQueuedStartupDestinations();
        if (explicitStartupPaths.Length == 0 || _workspace.Active is not ImageTabState imageTab) return;

        GlidePerformanceTrace.Mark("early_startup_prep_start", Path.GetFileName(imageTab.Path));
        _hasExplicitStartupOpen = true;
        _earlyStartupPaths = explicitStartupPaths;
        AdvanceWorkspaceEpoch();
        _earlyStartupTask = LoadPathAsync(imageTab.Path, updateActiveTab: false);
        GlidePerformanceTrace.Mark("early_startup_prep_end");
    }

    private string[] ApplyQueuedStartupDestinations()
    {
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(_queuedOpen)) raw.Add(_queuedOpen);
        _queuedOpen = null;
        raw.AddRange(_startupPaths.Drain());

        var plan = StartupWorkspacePlanner.Create(raw);
        if (plan.Tabs.Count == 0) return Array.Empty<string>();
        _workspace.Reset(plan.Tabs, 0);
        _diagnostics.Write("launch", "explicit_startup_workspace", new { count = plan.Tabs.Count, active = plan.Paths[0] });
        return plan.Paths.ToArray();
    }

    private void AdoptStartupSettingsIfReady(bool applyVisuals)
    {
        if (_deferredStartupSettingsAdopted || _windowLifetimeCts.IsCancellationRequested) return;
        var loaded = App.TryGetStartupSettings();
        if (loaded is null) return;
        _settings = SettingsStore.MergeHydratedPreservingEdits(_startupSettingsBaseline, _settings, loaded);
        _startupSettingsBaseline = _settings.CloneState();
        _deferredStartupSettingsAdopted = true;
        App.PublishCurrentSettings(_settings);
        if (applyVisuals)
        {
            ApplySettingsVisuals();
            ApplyStartupWholeAppOverlayPreference();
            TryScheduleAutomaticOverlayLayoutRestore();
        }
        _diagnostics.Write("startup", "settings_adopted", new { deferred = true });
    }

    private void ContinueDeferredStartupSettingsAdoption()
    {
        if (_deferredStartupSettingsAdopted) return;
        var task = App.StartupSettingsTask;
        if (task is null) return;
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_windowLifetimeCts.IsCancellationRequested) return;
            AdoptStartupSettingsIfReady(applyVisuals: true);
        }, DispatcherPriority.Background), TaskScheduler.Default);
    }

    private void BeginDeferredRecentHistoryInitialization()
    {
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("history_initialization_deferred");
        Dispatcher.UIThread.Post(() =>
        {
            _ = RecentHistoryStore.InitializeAsync().ContinueWith(
                _ => Dispatcher.UIThread.Post(RefreshRecentHistoryHome),
                TaskScheduler.Default);
        }, DispatcherPriority.Background);
    }

    private void ApplyInitialReferenceSizeAndPlacement()
    {
        if (_startupPlacementApplied) return;
        _startupPlacementApplied = true;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;

        Width = 1596.0 / scale;
        Height = 1031.0 / scale;
        MinWidth = 180.0 / scale;
        MinHeight = 520.0 / scale;

        if (!_settings.RememberWindowPlacement || _settings.WindowWidth < 400 || _settings.WindowHeight < 300)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        var saved = new PixelPoint(_settings.WindowX, _settings.WindowY);
        if (IsSafeSavedWindowPosition(saved, Width, Height, scale))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = saved;
        }
        else
        {
            CenterOnCurrentPrimaryScreen(scale);
        }
        _lastNormalWindowPosition = Position;
        _lastNormalWindowSize = new Size(Width, Height);
        _normalPlacementKnown = true;
        if (_settings.WindowWasMaximized)
        {
            WindowState = WindowState.Maximized;
            _lastKnownWindowState = WindowState.Maximized;
            _stateBeforeMinimize = WindowState.Maximized;
        }
        else
        {
            _lastKnownWindowState = WindowState.Normal;
            _stateBeforeMinimize = WindowState.Normal;
        }
    }

    private bool IsSafeSavedWindowPosition(PixelPoint position, double widthDip, double heightDip, double scale)
    {
        if (position.X == int.MinValue || position.Y == int.MinValue || Math.Abs(position.X) >= 50000 || Math.Abs(position.Y) >= 50000) return false;
        var w = Math.Max(240, (int)Math.Round(widthDip * scale));
        var h = Math.Max(160, (int)Math.Round(heightDip * scale));
        foreach (var screen in Screens.All)
        {
            var area = screen.WorkingArea;
            var overlapW = Math.Max(0, Math.Min(position.X + w, area.Right) - Math.Max(position.X, area.X));
            var overlapH = Math.Max(0, Math.Min(position.Y + h, area.Bottom) - Math.Max(position.Y, area.Y));
            if (overlapW >= 80 && overlapH >= 80) return true;
        }
        return false;
    }

    private void CenterOnCurrentPrimaryScreen(double scale)
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var widthPx = Math.Min(area.Width, Math.Max(1, (int)Math.Round(Width * scale)));
        var heightPx = Math.Min(area.Height, Math.Max(1, (int)Math.Round(Height * scale)));
        Position = new PixelPoint(area.X + (area.Width - widthPx) / 2, area.Y + (area.Height - heightPx) / 2);
    }

    private void TrackNormalWindowPlacement()
    {
        // Overlay mode is intentionally transient presentation geometry. Never let moving/resizing
        // the frameless overlay overwrite Glide's remembered ordinary-window placement.
        if (_wholeAppOverlayMode || !_startupPlacementApplied || WindowState != WindowState.Normal || Bounds.Width < 100 || Bounds.Height < 100) return;
        _lastNormalWindowPosition = Position;
        _lastNormalWindowSize = Bounds.Size;
        _normalPlacementKnown = true;
        _settings.WindowX = _lastNormalWindowPosition.X;
        _settings.WindowY = _lastNormalWindowPosition.Y;
        _settings.WindowWidth = _lastNormalWindowSize.Width;
        _settings.WindowHeight = _lastNormalWindowSize.Height;
        _settings.WindowWasMaximized = false;
        App.PublishCurrentSettings(_settings);
    }

    private void SaveWindowPlacement()
    {
        if (!_settings.RememberWindowPlacement || WindowState == WindowState.FullScreen) return;

        if (_wholeAppOverlayMode && _wholeAppOverlaySnapshot is { } overlaySnapshot)
        {
            _settings.WindowWasMaximized = overlaySnapshot.RestoreState == WindowState.Maximized;
            _settings.WindowX = overlaySnapshot.Position.X;
            _settings.WindowY = overlaySnapshot.Position.Y;
            _settings.WindowWidth = overlaySnapshot.Width;
            _settings.WindowHeight = overlaySnapshot.Height;
            SaveSettingsAndPublish();
            return;
        }

        _settings.WindowWasMaximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _stateBeforeMinimize == WindowState.Maximized);
        if (WindowState == WindowState.Normal) TrackNormalWindowPlacement();
        if (_normalPlacementKnown)
        {
            _settings.WindowX = _lastNormalWindowPosition.X;
            _settings.WindowY = _lastNormalWindowPosition.Y;
            _settings.WindowWidth = _lastNormalWindowSize.Width;
            _settings.WindowHeight = _lastNormalWindowSize.Height;
        }
        SaveSettingsAndPublish();
    }

    internal async Task RunLiveDiagnosticNavigationAsync(string fixtureFolder, string reportFolder)
    {
        var files = Directory.Exists(fixtureFolder)
            ? Directory.EnumerateFiles(fixtureFolder, "browse_*.jpg").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        if (files.Length == 0)
        {
            // A skipped/non-Windows corpus is expected during development. Keep the export
            // machine-readable instead of silently omitting evidence (which could be mistaken for
            // an empty or zero-latency run).
            Directory.CreateDirectory(reportFolder);
            File.WriteAllLines(Path.Combine(reportFolder, "live_navigation_results.csv"), new[]
            {
                "status,result",
                "SKIP,No generated navigation corpus was available; generate it on Windows before timing navigation."
            });
            File.WriteAllText(Path.Combine(reportFolder, "live_navigation_status.json"), JsonSerializer.Serialize(new
            {
                status = "SKIP",
                corpus = Path.GetFileName(fixtureFolder),
                fileCount = 0,
                reason = "Performance corpus was absent or platform/dependency generation was skipped."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        var restorePath = _currentPath;
        var rows = new List<string> { "index,file,present_ms" };
        var completed = false;
        try
        {
            await OpenAsImageTabAsync(files[0]);
            await Task.Delay(40);
            for (var i = 0; i < files.Length; i++)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                if (i > 0) await NavigateAsync(1);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                rows.Add($"{i + 1},{Path.GetFileName(files[i])},{elapsed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                // Yield to Avalonia so this is a real visible presentation exercise, not a headless
                // decoder loop. Representative frames are captured from the production MainWindow.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
                if (i == 0 || (i + 1) % 30 == 0 || i == files.Length - 1)
                    Glide.App.Diagnostics.UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, $"live_navigation_{i + 1:000}.png"), out _);
            }
            completed = true;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(reportFolder, "live_navigation_results.csv"), rows);
            File.WriteAllText(Path.Combine(reportFolder, "live_navigation_status.json"), JsonSerializer.Serialize(new
            {
                status = completed ? "PASS" : "FAILED",
                corpus = Path.GetFileName(fixtureFolder),
                fileCount = files.Length,
                result = "live_navigation_results.csv"
            }, new JsonSerializerOptions { WriteIndented = true }));
            var fixtureRoot = Directory.GetParent(fixtureFolder)?.FullName;
            if (!string.IsNullOrWhiteSpace(fixtureRoot))
            {
                File.WriteAllText(Path.Combine(fixtureRoot, "performance_corpus_status.json"), JsonSerializer.Serialize(new
                {
                    corpus = Path.GetFileName(fixtureFolder),
                    generated = true,
                    status = completed ? "LIVE_PASS" : "LIVE_FAILED",
                    fileCount = files.Length,
                    evidence = Path.Combine(reportFolder, "live_navigation_results.csv")
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (!string.IsNullOrWhiteSpace(restorePath) && File.Exists(restorePath))
                await OpenAsImageTabAsync(restorePath);
        }
    }


    internal async Task RunLargeImageFirstPaintDiagnosticAsync(string fixturePath, string reportFolder)
    {
        Directory.CreateDirectory(reportFolder);
        var output = Path.Combine(reportFolder, "large_image_first_paint.csv");
        if (!File.Exists(fixturePath))
        {
            File.WriteAllLines(output, new[]
            {
                "status,fixture,present_ms,decode_ms,route,preview_width,preview_height,progressive_colour_first",
                $"SKIP,{Path.GetFileName(fixturePath)},0,0,missing,0,0,{_settings.ProgressiveColorFirstPreview}"
            });
            return;
        }

        var restorePath = _currentPath;
        var rows = new List<string>
        {
            "status,fixture,present_ms,decode_ms,route,preview_width,preview_height,progressive_colour_first"
        };
        try
        {
            // This is deliberately an application-cache-purged in-process measurement, not a claim
            // of process/OS-cold launch. The standalone benchmark remains authoritative for true cold
            // startup. Its purpose is to make the 21 MP regression class and decode route visible in
            // every Developer Options export.
            _loader.PurgeCaches();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await OpenAsImageTabAsync(fixturePath);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
            var presentMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            rows.Add(string.Join(',',
                "PASS",
                Path.GetFileName(fixturePath),
                presentMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                _currentFirstFrameDecodeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                _currentDecodeRoute,
                _currentFirstFrameWidth,
                _currentFirstFrameHeight,
                _settings.ProgressiveColorFirstPreview));
            Glide.App.Diagnostics.UiDiagnosticCapture.TryCapture(this, Path.Combine(reportFolder, "large_image_first_paint.png"), out _);
        }
        catch (Exception ex)
        {
            rows.Add($"FAIL,{Path.GetFileName(fixturePath)},0,0,{ex.GetType().Name},0,0,{_settings.ProgressiveColorFirstPreview}");
        }
        finally
        {
            File.WriteAllLines(output, rows);
            if (!string.IsNullOrWhiteSpace(restorePath) && File.Exists(restorePath) &&
                !string.Equals(restorePath, fixturePath, StringComparison.OrdinalIgnoreCase))
                await OpenAsImageTabAsync(restorePath);
        }
    }

    private async Task OpenAsImageTabAsync(string path)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path))
        {
            Title = "Glide 4.1.4 — Unsupported or missing image";
            return;
        }
        if (!_workspace.ReplaceActiveWithImage(path)) _workspace.AddImage(path);
        RebuildTabStrip();
        await LoadPathAsync(path, updateActiveTab: true);
        if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFile(path, _settings.HistorySize);
    }

    private void CancelAsyncWorkspaceWork()
    {
        _loadCts?.Cancel();
        _metadataCts?.Cancel();
        _folderIndexCts?.Cancel();
        _rapidNavigationSettleCts?.Cancel();
        _heldNavigationCts?.Cancel();
        _pendingNavigationDelta = 0;
        lock (_wheelNavigationSync) _wheelNavigationQueue.Clear();
    }

    private void AdvanceWorkspaceEpoch()
    {
        Interlocked.Increment(ref _workspaceEpoch);
        CancelAsyncWorkspaceWork();
    }

    private ImageRequestContext BeginImageRequest(string path)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var tabId = _workspace.Active?.Id ?? Guid.Empty;
        return new ImageRequestContext(
            _windowLifetimeId, tabId, Volatile.Read(ref _workspaceEpoch),
            Interlocked.Increment(ref _imageRequestId), path, _loadCts.Token);
    }

    private bool IsRequestCurrent(in ImageRequestContext request, bool requirePresented = false)
    {
        if (_speedBoostStandbyActive || _windowLifetimeCts.IsCancellationRequested ||
            !request.Matches(_windowLifetimeId, _workspace.Active?.Id ?? Guid.Empty,
                Volatile.Read(ref _workspaceEpoch), Volatile.Read(ref _imageRequestId), _currentPath))
            return false;
        return !requirePresented || (ImageView.IsVisible &&
            string.Equals(_presentedPath, request.Path, StringComparison.OrdinalIgnoreCase) &&
            Viewport.PresentationRequestId == request.ImageRequestId);
    }

    private bool IsFolderSessionCurrent(in FolderIndexSession session)
    {
        if (_windowLifetimeCts.IsCancellationRequested) return false;
        return session.Matches(_workspace.Active?.Id ?? Guid.Empty, Volatile.Read(ref _workspaceEpoch),
            Volatile.Read(ref _folderSessionId), _currentPath);
    }

    private async Task LoadPathAsync(string path, bool updateActiveTab)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path)) return;
        _folderIndexCts?.Cancel();
        _folderIndexCts?.Dispose();
        _folderIndexCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var folderSession = new FolderIndexSession(
            _workspace.Active?.Id ?? Guid.Empty,
            Volatile.Read(ref _workspaceEpoch),
            Interlocked.Increment(ref _folderSessionId),
            Path.GetDirectoryName(path) ?? string.Empty,
            _folderIndexCts.Token);
        _pendingNavigationDelta = 0;
        _lastNavigationDirection = 0;

        _navigator.OpenSingleOnly(path);
        _folderIndexReady = false;
        _folderIndexCanonicalReady = false;

        if (updateActiveTab)
        {
            _workspace.ReplaceActiveImage(path);
            folderSession = folderSession with { ActiveTabId = _workspace.Active?.Id ?? Guid.Empty };
        }

        var presentation = await PresentCurrentAsync();
        if (presentation.Accepted && IsFolderSessionCurrent(folderSession))
            _ = IndexContainingFolderAsync(path, folderSession);
    }

    private async Task IndexContainingFolderAsync(string requestedPath, FolderIndexSession session)
    {
        try
        {
            if (!IsFolderSessionCurrent(session)) return;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_start", Path.GetFileName(requestedPath));
            var provisional = await Task.Run(
                () => ImageNavigator.EnumerateSupportedFolder(requestedPath, maxCount: 64),
                session.Token);
            if (!IsFolderSessionCurrent(session)) return;

            _navigator.ReplaceFolderIndex(provisional, requestedPath);
            if (!IsFolderSessionCurrent(session)) return;
            _folderIndexReady = true;
            _folderIndexCanonicalReady = false;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_first_neighbour_ready", $"count={_navigator.Count}");
            UpdatePictureCounter();
            UpdateStatusStats();
            _diagnostics.Write("navigation", "folder_index_provisional", new { path = requestedPath, count = _navigator.Count, bounded = true });
            ScheduleNavigationNeighbours();

            var pending = _pendingNavigationDelta;
            _pendingNavigationDelta = 0;
            if (pending != 0 && IsFolderSessionCurrent(session))
            {
                _lastNavigationDirection = Math.Sign(pending);
                var availableBackward = _navigator.Index;
                var availableForward = Math.Max(0, _navigator.Count - 1 - _navigator.Index);
                var safeDelta = Math.Clamp(pending, -availableBackward, availableForward);
                var remainder = pending - safeDelta;
                if (remainder != 0)
                    _pendingNavigationDelta = Math.Clamp(remainder, -MaxBufferedNavigationCommands, MaxBufferedNavigationCommands);
                if (safeDelta != 0)
                {
                    _navigator.Move(safeDelta);
                    await PresentCurrentAsync();
                    if (!IsFolderSessionCurrent(session)) return;
                }
            }

            var paths = await Task.Run(() => ImageNavigator.EnumerateSupportedFolder(requestedPath), session.Token);
            if (!IsFolderSessionCurrent(session)) return;
            var current = _navigator.Current ?? requestedPath;
            _navigator.ReplaceFolderIndex(paths, current);
            if (!IsFolderSessionCurrent(session)) return;
            _folderIndexCanonicalReady = true;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_complete", $"count={_navigator.Count}");
            UpdatePictureCounter();
            UpdateStatusStats();
            _diagnostics.Write("navigation", "folder_index_ready", new { path = requestedPath, count = _navigator.Count, reconciled = true });
            ScheduleNavigationNeighbours();

            var deferred = _pendingNavigationDelta;
            _pendingNavigationDelta = 0;
            if (deferred != 0 && IsFolderSessionCurrent(session))
            {
                _lastNavigationDirection = Math.Sign(deferred);
                _navigator.Move(deferred);
                await PresentCurrentAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsFolderSessionCurrent(session)) return;
            _folderIndexReady = true;
            _folderIndexCanonicalReady = true;
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("folder_index_failed", ex.GetType().Name);
            _diagnostics.Write("navigation", "folder_index_failed", new { path = requestedPath, error = ex.GetType().Name, ex.Message });
        }
    }

    private async Task NavigateFromViewportAsync(int delta)
    {
        try
        {
            _diagnostics.Write("input", "viewport_browse", new
            {
                delta, slideshowActive = _slideshow?.IsActive == true, slideshowRunning = _slideshow?.IsRunning == true,
                fullscreen = WindowState == WindowState.FullScreen, index = _navigator.Index, count = _navigator.Count
            });
            await NavigateAsync(delta);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try
            {
                _diagnostics.Write("navigation", "viewport_browse_failed", new { delta, error = ex.GetType().Name, ex.Message });
            }
            catch
            {
                // Diagnostics must never turn an input event into an unobserved task failure.
            }
        }
    }

    private async Task<PresentationOutcome> PresentCurrentAsync(bool rapidPreviewOnly = false, bool forceFull = false)
    {
        var path = _navigator.Current;
        if (path is null)
            return new PresentationOutcome(PresentationStatus.Cancelled, default);
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("present_request", ImageFormatRegistry.GetLongestExtension(path));

        var request = BeginImageRequest(path);
        _currentPath = path;
        _metadataCts?.Cancel();
        _metadataCts?.Dispose();
        _metadataCts = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        _currentMetadata = new();
        if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();

        try
        {
            // A warm preview can be both the cache entry and the bitmap currently attached to the
            // viewport. Detach that exact preview before an authoritative full load so AddPrepared
            // cannot dispose it while the decoder is still in flight.
            if (forceFull && Viewport.Bitmap is { } displayed &&
                string.Equals(_presentedPath, path, StringComparison.OrdinalIgnoreCase))
                _loader.DetachPreparedPreview(path, displayed);

            var result = forceFull
                ? await _loader.LoadFullForegroundAsync(path, request.Token)
                : await _loader.LoadForegroundAsync(path,
                    rapidPreviewOnly ? BuildRapidBrowsePolicy() : BuildPerformancePolicy(), request.Token,
                    cachePreview: !rapidPreviewOnly);
            if (result is null || !IsRequestCurrent(request))
            {
                // Successful foreground results are retained by ImageLoadCoordinator's prepared
                // cache. Purge/eviction owns disposal; a stale continuation must not dispose that
                // bitmap a second time after standby has already purged it.
                if (_warmPresentationGateActive)
                    await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
                return new PresentationOutcome(PresentationStatus.Cancelled, request);
            }

            // Arm before assignment so even an immediately scheduled render cannot outrun registration.
            // IMPORTANT: every replacement waits for the new frame to cross the compositor fence before
            // any prior bitmap is eligible for disposal/eviction. Rapid preview frames are deliberately
            // not cached, so disposing them immediately here can otherwise race Skia's render thread and
            // hard-terminate the process with no managed exception.
            var renderedTask = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
            var previousFrame = Viewport.PresentedFrame;
            var hadBitmap = previousFrame.Bitmap is not null;
            var old = previousFrame.Bitmap;
            var retiredInteraction = _interactionPreviewBitmap;
            Viewport.SetInteractionBitmap(null);
            _interactionPreviewBitmap = null;
            Viewport.PreserveManualZoomOnBitmapChange = _settings.PreserveManualZoomOnNavigate;
            var sourceWidth = result.SourceWidth > 0 ? result.SourceWidth : result.Bitmap.PixelSize.Width;
            var sourceHeight = result.SourceHeight > 0 ? result.SourceHeight : result.Bitmap.PixelSize.Height;
            Viewport.SwapPresentedFrame(result.Bitmap, sourceWidth, sourceHeight, request.ImageRequestId, path);
            _presentedPath = path;
            _loader.SetActivePath(path);
            if (!hadBitmap || !_settings.PreserveManualZoomOnNavigate)
                Viewport.ApplyViewMode(_settings.DefaultViewMode);
            ShowImageSurface();

            if (old is not null && !ReferenceEquals(old, result.Bitmap))
            {
                _diagnostics.Write("render", "bitmap_retire_wait", new
                {
                    request = request.ImageRequestId, path, oldWidth = previousFrame.SourceSize.Width, oldHeight = previousFrame.SourceSize.Height,
                    newWidth = sourceWidth, newHeight = sourceHeight, rapidPreviewOnly
                });
                var safeToRetire = await renderedTask.ConfigureAwait(true);
                _diagnostics.Write("render", "bitmap_retire_fence", new { request = request.ImageRequestId, path, safeToRetire });
                if (!safeToRetire || !IsRequestCurrent(request, requirePresented: true))
                    return new PresentationOutcome(PresentationStatus.Cancelled, request);
            }

            if (old is not null && !ReferenceEquals(old, result.Bitmap) && !_loader.IsBitmapCached(old))
                old.Dispose();
            if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, old) &&
                !ReferenceEquals(retiredInteraction, result.Bitmap) && !_loader.IsBitmapCached(retiredInteraction))
                retiredInteraction.Dispose();

            if (GlidePerformanceTrace.Enabled)
            {
                GlidePerformanceTrace.Mark("bitmap_assigned",
                    $"request={request.ImageRequestId};{result.Bitmap.PixelSize.Width}x{result.Bitmap.PixelSize.Height};preview={result.IsPreview};cache={result.CacheHit};prepared={result.PreparedFrameHit};route={result.DecodeRoute}");
                // Benchmark-only negative control. The trace harness can inject a known pre-fence
                // event and must reject the row, proving its contamination gate is effective.
                if (string.Equals(Environment.GetEnvironmentVariable("GLIDE_BENCHMARK_INJECT_PREFENCE_EVENT"), "1", StringComparison.Ordinal))
                    GlidePerformanceTrace.Mark("folder_index_start", $"synthetic=true;request={request.ImageRequestId}");
            }

            if (_warmPresentationGateActive || !_startupFirstImageTimingWritten || GlidePerformanceTrace.Enabled || App.BenchmarkExitAfterFirstFrame || App.NotifyFirstFrameEventName is not null)
            {
                var rendered = await renderedTask;
                if (!rendered || !IsRequestCurrent(request, requirePresented: true))
                {
                    if (GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("frame_render_fence_rejected", $"request={request.ImageRequestId}");
                    if (_warmPresentationGateActive)
                        await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
                    return new PresentationOutcome(PresentationStatus.Cancelled, request);
                }

                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("composition_batch_rendered", $"request={request.ImageRequestId};path={Path.GetFileName(path)}");

                if (App.NotifyFirstFrameEventName is { } notifyName)
                {
                    try
                    {
                        using var readyEvent = EventWaitHandle.OpenExisting(notifyName);
                        readyEvent.Set();
                    }
                    catch { }
                }

                if (App.BenchmarkExitAfterFirstFrame)
                    Dispatcher.UIThread.Post(Close, DispatcherPriority.Background);

                if (_warmPresentationGateActive)
                    await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
            }

            // Everything below is nonessential for the first correct frame and is intentionally gated
            // behind the render fence for this exact request.
            _diagnostics.Enable();
            _workspace.ReplaceActiveImage(path);
            _currentPath = path;
            PublishViewportDemand();
            _currentPixelWidth = result.SourceWidth > 0 ? result.SourceWidth : result.Bitmap.PixelSize.Width;
            _currentPixelHeight = result.SourceHeight > 0 ? result.SourceHeight : result.Bitmap.PixelSize.Height;
            _currentDecodeMs = result.DecodeTime.TotalMilliseconds;
            _currentFirstFrameDecodeMs = result.DecodeTime.TotalMilliseconds;
            _currentFirstFrameWidth = result.Bitmap.PixelSize.Width;
            _currentFirstFrameHeight = result.Bitmap.PixelSize.Height;
            _currentDecodeRoute = result.DecodeRoute;
            _currentFrameIsPreview = result.IsPreview;
            if (!result.IsPreview) CancelPreviewQualityRecovery();
            try { _currentFileSize = new FileInfo(path).Length; } catch { _currentFileSize = 0; }

            var metadataToken = _metadataCts.Token;
            if (!rapidPreviewOnly && IsRequestCurrent(request, requirePresented: true))
                _ = LoadMetadataAfterPaintAsync(path, request, metadataToken);

            UpdatePictureCounter();
            UpdateStatusStats();
            UpdateWindowTitle();
            if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
            if (!rapidPreviewOnly && _settings.TabsEnabled && _workspace.Tabs.Count > 1) RebuildTabStrip();
            _diagnostics.Write("decode", "foreground_presented",    new { path, request = request.ImageRequestId, width = _currentPixelWidth, height = _currentPixelHeight,
                    decodeMs = _currentDecodeMs, cacheHit = result.CacheHit, preparedHit = result.PreparedFrameHit,
                    preview = result.IsPreview, route = result.DecodeRoute, index = _navigator.Index, count = _navigator.Count });
            if (_settings.StartupDiagnostics && !_startupFirstImageTimingWritten)
            {
                _startupFirstImageTimingWritten = true;
                WriteStartupDiagnostics("first_image_presented");
            }
            if (!rapidPreviewOnly) ScheduleNavigationNeighbours();
            if (!rapidPreviewOnly && result.IsPreview && _settings.BackgroundRefinement &&
                IsRequestCurrent(request, requirePresented: true))
                _ = RefinePresentedAsync(result, request);
            if (result.IsPreview && IsRequestCurrent(request, requirePresented: true))
                SchedulePreviewQualityRecovery(path, request.ImageRequestId);

            return new PresentationOutcome(PresentationStatus.Accepted, request);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write("decode", "foreground_cancelled", new { path, request = request.ImageRequestId });
            if (_warmPresentationGateActive)
                await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
            return new PresentationOutcome(PresentationStatus.Cancelled, request);
        }
        catch (Exception ex)
        {
            if (IsRequestCurrent(request))
            {
                _diagnostics.Write("decode", "foreground_failed", new { path, request = request.ImageRequestId, error = ex.GetType().Name, ex.Message });
                Title = $"Glide 4.1.4 — Open failed: {ex.GetType().Name}";
            }
            if (_warmPresentationGateActive)
                await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
            return new PresentationOutcome(PresentationStatus.Failed, request);
        }
    }

    private void ScheduleNavigationNeighbours()
    {
        if (!_folderIndexReady) return;
        if (_navigator.Count >= 2)
            _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);

        if (_settings.PredictivePrefetch && _settings.PrefetchSiblingFolders && _navigator.Current is { } current)
        {
            var folder = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(folder) && !string.Equals(folder, _lastSurroundingPrefetchFolder, StringComparison.OrdinalIgnoreCase))
            {
                _lastSurroundingPrefetchFolder = folder;
                _ = WarmSurroundingFoldersAsync(current);
            }
        }
    }

    private async Task WarmSurroundingFoldersAsync(string currentPath)
    {
        var count = Math.Clamp(_settings.PrefetchSiblingFolderImageCount, 1, 30);
        var options = new SiblingFolderNavigationOptions(
            _settings.FolderNavIncludeHidden,
            _settings.FolderNavWrap,
            _settings.FolderNavSkipEmpty,
            _settings.FolderNavOpenFirstImage,
            _settings.HierarchicalFolderTraversal);
        var warmed = new List<string>();
        try
        {
            foreach (var direction in new[] { -1, 1 })
            {
                var result = await SiblingFolderNavigationService.FindAsync(
                    currentPath, direction, options, forceFirstImage: true, ImageNavigator.IsSupported);
                if (result is null) continue;
                var targets = result.Images.Take(count).ToArray();
                await _loader.WarmAsync(targets);
                warmed.AddRange(targets);
            }
            _diagnostics.Write("prefetch", "surrounding_folders_warmed", new
            {
                folder = Path.GetDirectoryName(currentPath),
                requestedPerFolder = count,
                warmed = warmed.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Write("prefetch", "surrounding_folder_failed", new { path = currentPath, error = ex.GetType().Name });
        }
    }

    private async Task RefinePresentedAsync(ImageLoadResult firstFrame, ImageRequestContext request)
    {
        try
        {
            var refined = await _loader.RefineForegroundAsync(firstFrame, request.Token);
            if (refined is null || !IsRequestCurrent(request, requirePresented: true))
            {
                DisposeRejectedRefinement(refined);
                return;
            }

            // A bounded refinement is still a preview. Decode the authoritative full frame outside
            // the navigation gate, then serialize only the viewport/cache ownership transition.
            // Previously refinement mutated Viewport.Bitmap independently of Home/manual/slideshow
            // navigation; that gave two UI producers ownership of the same bitmap lifetime.
            if (refined.IsPreview)
            {
                DisposeRejectedRefinement(refined);
                if (!IsRequestCurrent(request, requirePresented: true)) return;

                var full = await _loader.LoadFullForegroundAsync(firstFrame.Path, request.Token, cacheResult: false);
                if (full is null || !IsRequestCurrent(request, requirePresented: true))
                {
                    if (full is not null && !_loader.IsBitmapCached(full.Bitmap)) full.Bitmap.Dispose();
                    return;
                }

                await _navigationSerializationGate.WaitAsync(request.Token).ConfigureAwait(true);
                try
                {
                    if (!IsRequestCurrent(request, requirePresented: true))
                    {
                        if (!_loader.IsBitmapCached(full.Bitmap)) full.Bitmap.Dispose();
                        return;
                    }

                    _diagnostics.Write("render", "refinement_gate_enter", new
                    { request = request.ImageRequestId, path = firstFrame.Path, mode = "full_after_bounded" });
                    var fence = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
                    var retiredInteraction = _interactionPreviewBitmap;
                    Viewport.SetInteractionBitmap(null);
                    _interactionPreviewBitmap = null;
                    var detachedPreview = Viewport.ReplaceBitmapPreservingView(full.Bitmap);

                    var rendered = await fence.ConfigureAwait(true);
                    _diagnostics.Write("render", "refinement_fence", new
                    { request = request.ImageRequestId, path = firstFrame.Path, rendered, mode = "full_after_bounded" });
                    if (!rendered || !IsRequestCurrent(request, requirePresented: true))
                        return; // Keep attached frames alive; the next accepted request owns replacement.

                    // Promotion may dispose the detached cached preview. It is now safe because the
                    // replacement frame has crossed the compositor boundary.
                    var fullPromoted = _loader.PromoteRefinement(firstFrame, full);
                    if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, detachedPreview) &&
                        !ReferenceEquals(retiredInteraction, full.Bitmap) && !_loader.IsBitmapCached(retiredInteraction))
                        retiredInteraction.Dispose();

                    _interactionPreviewBitmap = fullPromoted ? null : detachedPreview;
                    Viewport.SetInteractionBitmap(_interactionPreviewBitmap);
                    _currentFrameIsPreview = false;
                    CancelPreviewQualityRecovery();
                    UpdateStatusStats();
                    if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
                    _diagnostics.Write("decode", "foreground_refined_full",
                        new { path = firstFrame.Path, request = request.ImageRequestId, cacheHit = full.CacheHit, preparedHit = full.PreparedFrameHit, promoted = fullPromoted });
                    return;
                }
                finally
                {
                    _navigationSerializationGate.Release();
                }
            }

            await _navigationSerializationGate.WaitAsync(request.Token).ConfigureAwait(true);
            try
            {
                if (!IsRequestCurrent(request, requirePresented: true))
                {
                    DisposeRejectedRefinement(refined);
                    return;
                }

                _diagnostics.Write("render", "refinement_gate_enter", new
                { request = request.ImageRequestId, path = firstFrame.Path, mode = "direct" });
                var fence = _presentationFence.ArmAsync(Viewport, request.ImageRequestId, request.Token);
                var retiredInteraction = _interactionPreviewBitmap;
                Viewport.SetInteractionBitmap(null);
                _interactionPreviewBitmap = null;
                var old = Viewport.ReplaceBitmapPreservingView(refined.Bitmap);

                var rendered = await fence.ConfigureAwait(true);
                _diagnostics.Write("render", "refinement_fence", new
                { request = request.ImageRequestId, path = firstFrame.Path, rendered, mode = "direct" });
                if (!rendered || !IsRequestCurrent(request, requirePresented: true))
                    return; // Do not dispose either side of an uncertain compositor hand-off.

                // Promotion can release the detached cached preview only after the new frame is known
                // to have rendered. This closes the remaining Skia use-after-dispose window.
                var promoted = _loader.PromoteRefinement(firstFrame, refined);
                if (retiredInteraction is not null && !ReferenceEquals(retiredInteraction, old) &&
                    !ReferenceEquals(retiredInteraction, refined.Bitmap) && !_loader.IsBitmapCached(retiredInteraction))
                    retiredInteraction.Dispose();

                _interactionPreviewBitmap = promoted ? null : old;
                Viewport.SetInteractionBitmap(_interactionPreviewBitmap);
                _currentFrameIsPreview = refined.IsPreview;
                if (!refined.IsPreview) CancelPreviewQualityRecovery();
                _currentDecodeMs += refined.DecodeTime.TotalMilliseconds;
                UpdateStatusStats();
                if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
                _diagnostics.Write("decode", "foreground_refined",
                    new { path = firstFrame.Path, request = request.ImageRequestId, refineMs = refined.DecodeTime.TotalMilliseconds,
                        cacheHit = refined.CacheHit, preparedHit = refined.PreparedFrameHit, promoted });
            }
            finally
            {
                _navigationSerializationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _diagnostics.Write("decode", "refinement_cancelled", new { path = firstFrame.Path, request = request.ImageRequestId });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("decode", "refinement_failed",
                new { path = firstFrame.Path, request = request.ImageRequestId, error = ex.GetType().Name, ex.Message });
        }
    }

    private void DisposeRejectedRefinement(ImageLoadResult? result)
    {
        // Refined cache hits remain loader-owned. A newly decoded refinement is MainWindow-owned
        // until it is attached, so dispose only that uncached result when it is rejected.
        if (result is null || result.PreparedFrameHit || _loader.IsBitmapCached(result.Bitmap)) return;
        result.Bitmap.Dispose();
    }

    private void ShowNavigationBoundaryNotice(int direction)
    {
        var message = direction >= 0 ? "End of folder — no next image" : "Start of folder — no previous image";
        NavigationBoundaryNoticeText.Text = message;
        NavigationBoundaryNotice.IsVisible = true;
        StatusStatsText.Text = message;
        _navigationBoundaryNoticeCts?.Cancel();
        _navigationBoundaryNoticeCts?.Dispose();
        var cts = _navigationBoundaryNoticeCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1400, cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested) NavigationBoundaryNotice.IsVisible = false;
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
        });
    }

    private Task NavigateAsync(int delta) => RunSerializedNavigationAsync(() => NavigateCoreAsync(delta));

    private async Task RunSerializedNavigationAsync(Func<Task> action)
    {
        await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
        try { await action().ConfigureAwait(true); }
        finally { _navigationSerializationGate.Release(); }
    }

    private async Task NavigateCoreAsync(int delta)
    {
        if (_navigator.Count == 0 || delta == 0 || _navigationBoundaryPromptActive) return;
        var navigationEpoch = _navigationCommandEpoch;
        _selectionZoomDemandPath = null;
        _lastNavigationDirection = Math.Sign(delta);

        var now = Stopwatch.GetTimestamp();
        var rapid = _lastNavigationRequestTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastNavigationRequestTimestamp, now).TotalMilliseconds <= 150;
        _lastNavigationRequestTimestamp = now;
        _loader.ReportNavigationActivity(rapid ? ImageNavigationActivity.RapidBrowse : ImageNavigationActivity.NormalBrowse);
        _diagnostics.Write("navigation", "requested", new { delta, rapid, index = _navigator.Index, count = _navigator.Count, folderIndexReady = _folderIndexReady, folderIndexCanonicalReady = _folderIndexCanonicalReady });

        if (!_folderIndexReady)
        {
            _pendingNavigationDelta = Math.Clamp(_pendingNavigationDelta + delta, -MaxBufferedNavigationCommands, MaxBufferedNavigationCommands);
            return;
        }

        var atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
        if (atBoundary && !_folderIndexCanonicalReady)
        {
            _pendingNavigationDelta = Math.Clamp(_pendingNavigationDelta + delta, -MaxBufferedNavigationCommands, MaxBufferedNavigationCommands);
            return;
        }
        if (atBoundary && _settings.ContinueSiblingFolders)
        {
            if (await TryNavigateSiblingFolderAsync(Math.Sign(delta))) return;
            ShowNavigationBoundaryNotice(delta);
            // A modal boundary barrier invalidates every other command that was already in flight.
            // Never let an old click/key continuation move inside the newly accepted destination.
            if (navigationEpoch != _navigationCommandEpoch) return;
        }

        if (atBoundary && !_settings.ContinueSiblingFolders)
            ShowNavigationBoundaryNotice(delta);
        _navigator.Move(delta);
        await PresentCurrentAsync(rapidPreviewOnly: rapid);
        if (rapid) ScheduleRapidNavigationSettle();
    }

    private void ScheduleRapidNavigationSettle()
    {
        _rapidNavigationSettleCts?.Cancel();
        _rapidNavigationSettleCts?.Dispose();
        _rapidNavigationSettleCts = new CancellationTokenSource();
        var token = _rapidNavigationSettleCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(170, token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await RunSerializedNavigationAsync(async () =>
                        {
                            if (token.IsCancellationRequested || _navigator.Current is null) return;
                            _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                            await PresentCurrentAsync(rapidPreviewOnly: false, forceFull: true);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _diagnostics.Write("navigation", "rapid_settle_failed", new { error = ex.GetType().Name }); }
                }, DispatcherPriority.Render);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _diagnostics.Write("navigation", "rapid_settle_failed", new { error = ex.GetType().Name }); }
        }, CancellationToken.None);
    }

    private void SchedulePreviewQualityRecovery(string path, long requestId)
    {
        _previewQualityRecoveryCts?.Cancel();
        _previewQualityRecoveryCts?.Dispose();
        var cts = _previewQualityRecoveryCts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // This is a safety net, not the primary refinement path. It fires only if a preview
                // remains attached after normal refinement/rapid-settle should already have completed.
                await Task.Delay(650, token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (token.IsCancellationRequested || !_currentFrameIsPreview ||
                            !string.Equals(_presentedPath, path, StringComparison.OrdinalIgnoreCase) ||
                            Volatile.Read(ref _imageRequestId) != requestId) return;
                        await RunSerializedNavigationAsync(async () =>
                        {
                            if (token.IsCancellationRequested || !_currentFrameIsPreview ||
                                !string.Equals(_navigator.Current, path, StringComparison.OrdinalIgnoreCase)) return;
                            _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                            await PresentCurrentAsync(rapidPreviewOnly: false, forceFull: true);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _diagnostics.Write("decode", "preview_quality_recovery_failed", new { path, error = ex.GetType().Name }); }
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _diagnostics.Write("decode", "preview_quality_recovery_failed", new { path, error = ex.GetType().Name }); }
        }, CancellationToken.None);
    }

    private void CancelPreviewQualityRecovery()
    {
        _previewQualityRecoveryCts?.Cancel();
        _previewQualityRecoveryCts?.Dispose();
        _previewQualityRecoveryCts = null;
    }

    private void PublishViewportDemand()
    {
        if (_currentPath is null) return;
        var view = Viewport.CaptureViewState();
        var width = Math.Max(1, (int)Math.Ceiling(Viewport.Bounds.Width * Math.Max(1.0, RenderScaling)));
        var height = Math.Max(1, (int)Math.Ceiling(Viewport.Bounds.Height * Math.Max(1.0, RenderScaling)));
        var zoom = string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1.0, view.Zoom) : 1.0;
        var selectionZoom = string.Equals(_selectionZoomDemandPath, _currentPath, StringComparison.OrdinalIgnoreCase) ||
            (Viewport.HasSelection && string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase) && zoom > 1.0);
        var exactPixels = string.Equals(view.Mode, "Manual", StringComparison.OrdinalIgnoreCase) && Math.Abs(view.Zoom - 1.0) < 0.001;
        _loader.SetViewportDemand(_currentPath, new ImageViewportDemand(width, height, zoom, selectionZoom, exactPixels));
    }

    private async Task<bool> TryNavigateSiblingFolderAsync(int direction, bool forceFirstImage = false, bool forceDirectionalEdge = false, bool rapidPreviewOnly = false)
    {
        var navigationEpoch = _navigationCommandEpoch;
        var current = _navigator.Current;
        if (current is null || direction == 0 || _navigationBoundaryPromptActive) return false;
        SiblingFolderNavigationResult? result;
        try
        {
            result = await SiblingFolderNavigationService.FindAsync(
                current,
                direction,
                new SiblingFolderNavigationOptions(
                    _settings.FolderNavIncludeHidden,
                    _settings.FolderNavWrap,
                    _settings.FolderNavSkipEmpty,
                    forceDirectionalEdge ? direction > 0 : _settings.FolderNavOpenFirstImage,
                    _settings.HierarchicalFolderTraversal),
                forceFirstImage,
                ImageNavigator.IsSupported);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _diagnostics.Write("navigation", "sibling_folder_failed", new { path = current, error = ex.GetType().Name });
            return false;
        }
        if (result is null || navigationEpoch != _navigationCommandEpoch) return false;
        if (result.CrossedAncestorBoundary && _settings.ConfirmHierarchicalFolderTraversal)
        {
            // A blocking boundary prompt is a hard navigation barrier. Do not allow a second
            // in-flight click/wheel/key request to open another prompt or become stale work that
            // executes after the first dialog closes.
            if (_navigationBoundaryPromptActive) return false;
            var decision = await ConfirmHierarchicalFolderBoundaryAsync(result);
            if (!decision.Continue) return false;
            if (decision.DontRemindAgain)
            {
                _settings.ConfirmHierarchicalFolderTraversal = false;
                SaveSettingsAndPublish();
            }
        }
        _navigator.ReplaceFolderIndex(result.Images, result.SelectedPath);
        _folderIndexReady = true;
        _folderIndexCanonicalReady = true;
        _workspace.ReplaceActiveImage(result.SelectedPath);
        _diagnostics.Write("navigation", result.CrossedAncestorBoundary ? "hierarchical_folder" : "sibling_folder", new
        {
            direction,
            folder = result.Folder,
            path = result.SelectedPath,
            result.CrossedAncestorBoundary,
            result.BoundaryFrom,
            result.BoundaryTo
        });
        await PresentCurrentAsync(rapidPreviewOnly: rapidPreviewOnly, forceFull: !rapidPreviewOnly);
        return true;
    }

    private async Task<(bool Continue, bool DontRemindAgain)> ConfirmHierarchicalFolderBoundaryAsync(SiblingFolderNavigationResult result)
    {
        // Modal navigation prompts are command barriers, not pauses in a producer. Any rapid producer
        // that reached this boundary is cancelled, stale wheel/deferred commands are discarded, and
        // keyboard hold auto-repeat is suppressed until that physical key is released. The user may
        // continue immediately afterwards with a fresh key press, wheel event, or mouse click.
        _navigationBoundaryPromptActive = true;
        InterruptRapidNavigationForBlockingUi("hierarchical_boundary");

        var accepted = false;
        var decided = false;
        var remember = new CheckBox { Content = "Don't remind me again" };
        var proceed = new Button { Content = "Continue", MinWidth = 96 };
        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        var dialog = new Window { Width = 500, Height = 245, Title = "Continue to nearby folder?", Icon = Icon, CanResize = false };
        PopupPlacementStore.Track(dialog, "folder-boundary-confirm");
        proceed.Click += (_, _) => { accepted = true; decided = true; dialog.Close(); };
        cancel.Click += (_, _) => { accepted = false; decided = true; dialog.Close(); };
        dialog.KeyUp += (_, e) =>
        {
            if ((e.Key == Key.Right && _suppressedHeldNavigationDirection > 0) ||
                (e.Key == Key.Left && _suppressedHeldNavigationDirection < 0))
                _suppressedHeldNavigationDirection = 0;
        };
        var from = string.IsNullOrWhiteSpace(result.BoundaryFrom) ? "the current branch" : result.BoundaryFrom;
        var to = string.IsNullOrWhiteSpace(result.Folder) ? "the nearby branch" : result.Folder;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 13,
            Children =
            {
                new TextBlock { Text = "End of this folder branch", FontSize = 19, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Glide has reached the end of {from}. Continue into the nearby folder branch:\n{to}", TextWrapping = TextWrapping.Wrap },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, proceed } }
            }
        };
        try
        {
            await dialog.ShowDialog(this);
            return decided ? (accepted, accepted && remember.IsChecked == true) : (false, false);
        }
        finally
        {
            _navigationBoundaryPromptActive = false;
            _lastWheelNavigationTimestamp = 0;
            _lastNavigationRequestTimestamp = 0;
        }
    }

    private void InterruptRapidNavigationForBlockingUi(string reason)
    {
        unchecked { _navigationCommandEpoch++; }
        if (_heldNavigationKeyIsDown && _heldNavigationDirection != 0)
            _suppressedHeldNavigationDirection = _heldNavigationDirection;

        // Cancellation makes a held-arrow producer terminate at the prompt instead of resuming
        // from the destination after Continue is clicked. A fresh physical action is required.
        StopHeldNavigation(settle: false);

        // Wheel hardware and very fast mouse input can have several already-issued requests. Those
        // requests describe the pre-dialog interaction and must never execute after a modal decision.
        lock (_wheelNavigationSync)
            _wheelNavigationQueue.Clear();

        _pendingNavigationDelta = 0;
        _lastWheelNavigationTimestamp = 0;
        _lastNavigationRequestTimestamp = 0;
        _rapidNavigationSettleCts?.Cancel();
        _diagnostics.Write("navigation", "rapid_input_interrupted", new { reason, maxBuffered = MaxBufferedNavigationCommands });
    }

    private void ShowImageSurface()
    {
        CancelBrowserThumbnailWork();
        HomeHost.IsVisible = false;
        WarmOpenTransitionHost.IsVisible = false;
        BrowserView.IsVisible = false;
        ImageView.IsVisible = true;
        ApplyStatusVisibility();
    }

    private void ShowWarmOpenTransitionSurface()
    {
        CancelBrowserThumbnailWork();
        HomeHost.IsVisible = false;
        ImageView.IsVisible = false;
        BrowserView.IsVisible = false;
        WarmOpenTransitionHost.IsVisible = true;
        ApplyStatusVisibility();
    }

    private void ArmWarmPresentationGate()
    {
        if (_warmPresentationGateActive) return;
        _warmPresentationGateActive = true;
        // Window.Opacity is the top-level compositor gate. Avalonia visual visibility changes alone
        // do not prevent DWM from briefly reusing the previous composed image on a warm HWND.
        Opacity = 0;
        if (_windowsBrowserFrame is not null && TryGetViewportColorRef(out var viewportColorRef))
            _windowsBrowserFrame.ShowPrivacyCurtain(viewportColorRef);
    }

    private bool TryGetViewportColorRef(out uint colorRef)
    {
        colorRef = 0;
        var brush = Viewport.Background as SolidColorBrush ??
            Application.Current?.Resources["BrushViewport"] as SolidColorBrush;
        if (brush is null) return false;
        var color = brush.Color;
        // Win32 COLORREF is 0x00BBGGRR, while Avalonia stores channels as RRGGBB.
        colorRef = (uint)(color.R | (color.G << 8) | (color.B << 16));
        return color.A != 0;
    }

    private async Task ReleaseWarmPresentationGateAsync(bool safeFrameReady)
    {
        if (!_warmPresentationGateActive) return;

        if (!safeFrameReady)
        {
            // An open failure/cancellation must not reveal a blank viewer. Compose the ordinary Home
            // surface first, then reveal it using the user's configured whole-window opacity.
            ShowHomeSurface();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        if (_warmPresentationGateActive)
        {
            Opacity = _sessionOpacity;
            _windowsBrowserFrame?.HidePrivacyCurtain();
            _warmPresentationGateActive = false;
        }
    }

    private HomeSurface EnsureHomeSurface()
    {
        if (_homeSurface is not null) return _homeSurface;
        var home = new HomeSurface();
        _homeSurface = home;
        HomeHost.Content = home;
        home.AddHandler(InputElement.PointerPressedEvent, HomeBackgroundPointerPressed, RoutingStrategies.Tunnel, true);
        home.ResetLayoutRequested += (_, _) => ResetWelcomeLayoutClicked(home, new RoutedEventArgs());
        home.MoveRecentRequested += (_, _) => MoveRecentHistoryClicked(home, new RoutedEventArgs());
        home.ResizeRecentRequested += (_, _) => ResizeRecentHistoryClicked(home, new RoutedEventArgs());
        home.ClearRecentRequested += (_, _) => ClearRecentHistoryClicked(home, new RoutedEventArgs());
        home.CloseRecentRequested += (_, _) => CloseRecentHistoryClicked(home, new RoutedEventArgs());
        home.SettingsRequested += (_, _) => SettingsClicked(home, new RoutedEventArgs());
        home.CollapseStatusRequested += (_, _) => CollapseStatusClicked(home, new RoutedEventArgs());
        home.CloseStatusRequested += (_, _) => CloseStatusClicked(home, new RoutedEventArgs());
        home.OptionsButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        home.StatusCollapseButton.AddHandler(InputElement.PointerPressedEvent, ReliableCommandButtonPressed, RoutingStrategies.Tunnel, true);
        ApplyWelcomeLayout();
        ApplyStatusBarSize();
        return home;
    }

    private void ShowHomeSurface()
    {
        AdvanceWorkspaceEpoch();
        CancelBrowserThumbnailWork();
        _presentedPath = null;
        StopSlideshow(restoreStartingMode: false);
        var home = EnsureHomeSurface();
        HomeHost.IsVisible = true;
        WarmOpenTransitionHost.IsVisible = false;
        ImageView.IsVisible = false;
        BrowserView.IsVisible = false;
        var recentLanding = string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase);
        home.HeadingText.Text = recentLanding ? "Recent pictures" : "Welcome to Glide";
        home.SubtitleText.Text = recentLanding
            ? "Jump straight back into recently opened images."
            : "Fast browsing, precise zooming, and familiar Windows controls.";
        home.TipsGrid.IsVisible = !recentLanding && _settings.ShowHomeTips;
        ApplyWelcomeLayout();
        Title = recentLanding ? "Glide 4.1.4 — Recent pictures" : "Glide 4.1.4 — Home";
        RefreshRecentHistoryHome();
        ApplyStatusVisibility();
    }

    private BrowserSurface EnsureBrowserSurface()
    {
        if (_browserSurface is not null) return _browserSurface;
        var browser = new BrowserSurface();
        _browserSurface = browser;
        BrowserView.Content = browser;
        _browserUiReady = false;
        _browserViewMode = Math.Max(0, browser.ViewModeCombo.SelectedIndex);
        browser.Repeater.ItemTemplate = new FuncDataTemplate<BrowserEntry>((entry, _) => CreateBrowserItem(entry), supportsRecycling: false);
        browser.Repeater.ElementPrepared += BrowserRepeaterElementPrepared;
        browser.Repeater.ElementClearing += BrowserRepeaterElementClearing;
        browser.BackRequested += (_, _) => BrowserBackClicked(browser, new RoutedEventArgs());
        browser.ForwardRequested += (_, _) => BrowserForwardClicked(browser, new RoutedEventArgs());
        browser.UpRequested += (_, _) => BrowserUpClicked(browser, new RoutedEventArgs());
        browser.RefreshRequested += (_, _) => BrowserRefreshClicked(browser, new RoutedEventArgs());
        browser.AddressKeyPressed += (_, e) => BrowserAddressKeyDown(browser.AddressBox, e);
        browser.ViewModeChanged += (_, e) => BrowserViewModeChanged(browser.ViewModeCombo, e);
        browser.WindowsExplorerRequested += (_, _) => BrowserWindowsExplorerClicked(browser, new RoutedEventArgs());
        browser.BrowserKeyPressed += (_, e) => BrowserListKeyDown(browser.ScrollViewer, e);
        _browserUiReady = true;
        ConfigureBrowserRepeaterLayout();
        RefreshDynamicTooltips();
        ApplyManagedExplorerTheme();
        return browser;
    }

    private Button BrowserBackButton => EnsureBrowserSurface().BackButton;
    private Button BrowserForwardButton => EnsureBrowserSurface().ForwardButton;
    private Button BrowserUpButton => EnsureBrowserSurface().UpButton;
    private Button BrowserRefreshButton => EnsureBrowserSurface().RefreshButton;
    private TextBox BrowserAddressBox => EnsureBrowserSurface().AddressBox;
    private ComboBox BrowserViewModeCombo => EnsureBrowserSurface().ViewModeCombo;
    private ScrollViewer BrowserScrollViewer => EnsureBrowserSurface().ScrollViewer;
    private ItemsRepeater BrowserRepeater => EnsureBrowserSurface().Repeater;
    private UniformGridLayout BrowserRepeaterLayout => EnsureBrowserSurface().RepeaterLayout;
    private Grid NativeExplorerHostSurface => EnsureBrowserSurface().NativeHostSurface;

    private void ShowBrowserSurface()
    {
        AdvanceWorkspaceEpoch();
        var browserSurface = EnsureBrowserSurface();
        _presentedPath = null;
        StopSlideshow(restoreStartingMode: false);
        HomeHost.IsVisible = false;
        WarmOpenTransitionHost.IsVisible = false;
        ImageView.IsVisible = false;
        BrowserView.IsVisible = true;
        // Glide owns its production Explorer surface here. The legacy IExplorerBrowser host remains
        // dormant diagnostic code only; there is no production entry point to it. The managed surface
        // is authoritative because it can follow Glide theme/resizing consistently across Windows/DPI.
        BrowserScrollViewer.IsVisible = true;
        ApplyManagedExplorerTheme();
        ApplyStatusVisibility();
    }

    /// <summary>
    /// Native Shell COM objects are created only after a Browser tab becomes the active workspace.
    /// Keeping the factory here also makes the startup contract auditable: constructing MainWindow
    /// or a Home/Image tab cannot instantiate ExplorerBrowser.
    /// </summary>
    private void EnsureNativeExplorerHost(bool recreateIfFailed = false)
    {
        if (NativeExplorerHost is not null && recreateIfFailed && NativeExplorerHost.State == ExplorerHostState.Failed)
        {
            // NativeControlHost owns a real child HWND. A failed host remains an opaque native window
            // and can physically cover Avalonia's managed fallback even when BrowserScrollViewer.IsVisible=true.
            // Detach the failed child so Refresh/address navigation can make a clean COM attempt.
            NativeExplorerHost.FolderNavigated -= NativeExplorerFolderNavigated;
            NativeExplorerHost.StateChanged -= NativeExplorerStateChanged;
            NativeExplorerHost.FileActivated -= NativeExplorerFileActivated;
            NativeExplorerHostSurface.Children.Remove(NativeExplorerHost);
            NativeExplorerHost = null;
        }
        if (NativeExplorerHost is not null) return;
        NativeExplorerHost = new WindowsExplorerHost
        {
            // Keep the native HWND at 1x1 until Explorer has produced a real view. Native child HWNDs
            // always sit above Avalonia-rendered content, so this makes the managed fallback genuinely
            // visible while COM creation/navigation is in flight instead of showing a black rectangle.
            Width = 1,
            Height = 1,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        NativeExplorerHost.FolderNavigated += NativeExplorerFolderNavigated;
        NativeExplorerHost.StateChanged += NativeExplorerStateChanged;
        NativeExplorerHost.Diagnostic += (name, data) => _diagnostics.Write("native_explorer", name, data);
        NativeExplorerHost.CanActivateFile = ImageNavigator.IsSupported;
        NativeExplorerHost.FileActivated += NativeExplorerFileActivated;
        NativeExplorerHostSurface.Children.Add(NativeExplorerHost);
        ApplyEmbeddedExplorerTheme();
        ApplyNativeExplorerSurfaceOwnership(NativeExplorerHost.State);
    }

    private void ApplyNativeExplorerSurfaceOwnership(ExplorerHostState state)
    {
        if (NativeExplorerHost is null)
        {
            BrowserScrollViewer.IsVisible = true;
            return;
        }

        var ready = state == ExplorerHostState.Ready;
        BrowserScrollViewer.IsVisible = !ready;
        // Keep the host attached (1x1 when not ready) so a failure does not trigger an Avalonia
        // visibility destroy/recreate loop. The 1x1 child cannot obscure the managed fallback.
        NativeExplorerHost.IsVisible = true;
        if (ready)
        {
            NativeExplorerHost.ClearValue(WidthProperty);
            NativeExplorerHost.ClearValue(HeightProperty);
            NativeExplorerHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            NativeExplorerHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }
        else
        {
            NativeExplorerHost.Width = 1;
            NativeExplorerHost.Height = 1;
            NativeExplorerHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            NativeExplorerHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }
        _diagnostics.Write("native_explorer", "surface_owner_changed", new
        {
            state = state.ToString(),
            fallbackVisible = BrowserScrollViewer.IsVisible,
            nativeVisible = NativeExplorerHost.IsVisible,
            nativeReady = ready
        });
    }

    private void NativeExplorerStateChanged(ExplorerHostState state)
    {
        // The real HWND only expands to cover the browser after both view creation and navigation
        // complete. Failure removes the opaque child from sight immediately, leaving a usable fallback.
        if (!BrowserView.IsVisible) return;
        ApplyNativeExplorerSurfaceOwnership(state);
    }

    private async Task ActivateWorkspaceAsync()
    {
        switch (_workspace.Active)
        {
            case HomeTabState:
                ShowHomeSurface();
                break;
            case ImageTabState image:
                AdvanceWorkspaceEpoch();
                await LoadPathAsync(image.Path, updateActiveTab: false);
                if (image.ViewState is { } view)
                    Viewport.RestoreViewState(new ViewerViewSnapshot(view.Mode, view.Zoom, new Vector(view.PanX, view.PanY)));
                break;
            case BrowserTabState browser:
                ShowBrowserSurface();
                RestoreBrowserNavigationState(browser);
                NavigateBrowserTo(browser.Folder, addHistory: false);
                Title = $"Glide 4.1.4 — Explorer — {browser.Folder}";
                break;
        }
        RebuildTabStrip();
        if (_workspace.Active is { } active) SelectBrowserHighlight(active.Id);
        UpdateTabNavigationButtons();
    }

    private void CaptureActiveTabRuntimeState()
    {
        if (_workspace.Active is { } active) CaptureTabRuntimeState(active.Id);
    }

    /// <summary>
    /// Commits window-owned runtime state into the framework-free tab record before a tab switches
    /// windows. This keeps transfer atomic: the target never has to reach back into source visuals.
    /// </summary>
    private void CaptureTabRuntimeState(Guid id)
    {
        var tab = _workspace.Tabs.FirstOrDefault(candidate => candidate.Id == id);
        switch (tab)
        {
            case ImageTabState image when _workspace.Active?.Id == id &&
                                          string.Equals(_currentPath, image.Path, StringComparison.OrdinalIgnoreCase):
            {
                var view = Viewport.CaptureViewState();
                _workspace.ReplaceTab(image with
                {
                    ViewState = new ImageTabViewState(view.Mode, view.Zoom, view.Pan.X, view.Pan.Y)
                });
                break;
            }
            case BrowserTabState browser when _browserSessions.TryGetValue(id, out var session):
                _workspace.ReplaceTab(browser with
                {
                    Navigation = new BrowserNavigationState(session.History.ToArray(), session.Index)
                });
                break;
        }
    }

    private void RestoreBrowserNavigationState(BrowserTabState browser)
    {
        if (browser.Navigation is not { } navigation || _browserSessions.ContainsKey(browser.Id)) return;
        var session = new BrowserSession();
        session.History.AddRange(navigation.History.Where(Directory.Exists));
        session.Index = session.History.Count == 0
            ? -1
            : Math.Clamp(navigation.Index, 0, session.History.Count - 1);
        _browserSessions[browser.Id] = session;
    }

    private void ApplyCompactChromeLayout()
    {
        if (ChromeLayout is null || _wholeAppOverlayMode) return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        // Consume blank/title utility space before sacrificing tabs. The close button remains the
        // last permanent control; utility actions, nav and nonessential caption buttons yield first.
        TitleActionHost.IsVisible = width >= 720;
        TabNavigationHost.IsVisible = width >= 560;
        // Caption controls are protected as one Windows-standard unit. At narrow widths tabs keep
        // compressing instead of sacrificing Minimize/Maximize/Close individually.
        CaptionMinimizeButton.IsVisible = true;
        CaptionMaximizeButton.IsVisible = true;
        CaptionCloseButton.IsVisible = true;
        NewTabButton.IsVisible = _settings.TabsEnabled && width >= 360;
    }

    private void RebuildTabStrip()
    {
        if (TabStripPanel is null) return;
        TabStripPanel.Children.Clear();
        _workspace.ClosedHistoryLimit = Math.Clamp(_settings.ClosedTabHistoryLimit, 1, 100);
        // Avalonia Bounds and Width are already device-independent pixels. Do not divide tab
        // dimensions by RenderScaling: doing so made tabs visibly undersized on high-DPI displays.
        var configuredMin = Math.Clamp(_settings.TabMinWidth, 80, 240);
        const double emergencyMin = 18;
        var max = Math.Clamp(_settings.TabMaxWidth, 120, 400);
        if (max < configuredMin) max = configuredMin;
        var count = Math.Max(1, _workspace.Tabs.Count);
        var stripWidth = TabStripScroll.Bounds.Width > 20 ? TabStripScroll.Bounds.Width : Math.Max(20, Bounds.Width * 0.72);
        var utilityButtons = 0; // Back/Forward live outside the scrollable tab strip.
        var availableForTabs = Math.Max(emergencyMin, stripWidth - (NewTabButton.IsVisible ? 43 : 0) - utilityButtons * 35 - Math.Max(0, count - 1) * 3);
        var natural = availableForTabs / count;
        var preferred = natural >= configuredMin
            ? Math.Clamp(natural, configuredMin, max)
            : Math.Clamp(natural, emergencyMin, configuredMin);

        var edgeHitTabs = WindowState is WindowState.Maximized or WindowState.FullScreen;
        if (NewTabButton is not null)
        {
            NewTabButton.Height = edgeHitTabs ? 44 : 36;
            NewTabButton.VerticalAlignment = edgeHitTabs ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;
        }

        foreach (var tab in _workspace.Tabs)
        {
            var active = _workspace.Active?.Id == tab.Id;
            var container = new Border
            {
                DataContext = tab.Id,
                Width = preferred,
                Height = edgeHitTabs ? 44 : 36,
                VerticalAlignment = edgeHitTabs ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center
            };
            container.Classes.Add("glideTab");
            if (active) container.Classes.Add("active");

            var grid = new Grid();
            var body = new Border
            {
                DataContext = tab.Id,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Padding = new Thickness(11, 0, 29, 0)
            };
            body.Classes.Add("tabHit");
            body.PointerPressed += TabDragPointerPressed;
            body.PointerMoved += TabDragPointerMoved;
            body.PointerReleased += TabDragPointerReleased;
            body.PointerCaptureLost += TabDragPointerCaptureLost;

            var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            content.Children.Add(new GlideIconView
            {
                Kind = tab.Kind switch { TabKind.Home => "Home", TabKind.Browser => "Browser", _ => "Image" },
                Width = 14,
                Height = 14,
                StrokeWidth = 1.5,
                IconBrush = new SolidColorBrush(Color.Parse(active ? CurrentAccentHex() : (IsDarkTheme() ? "#9AA6B2" : "#5D6974")))
            });
            content.Children.Add(new TextBlock
            {
                Text = tab.Title,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(40, preferred - 54),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            body.Child = content;
            grid.Children.Add(body);

            var close = new Button
            {
                DataContext = tab.Id,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
                Content = new GlideIconView
                {
                    Kind = "Close",
                    Width = 11,
                    Height = 11,
                    StrokeWidth = 1.4,
                    IconBrush = new SolidColorBrush(Color.Parse("#B4BEC8"))
                }
            };
            close.Classes.Add("tabClose");
            ToolTip.SetTip(close, "Close tab");
            close.Click += TabCloseClicked;
            grid.Children.Add(close);
            container.Child = grid;
            container.ContextMenu = BuildTabContextMenu(tab);
            ToolTip.SetTip(container, "Drag to reorder • middle-click or double-click to close • right-click for tab actions.");
            ToolTip.SetShowDelay(container, 650);
            ToolTip.SetTip(close, "Close tab");
            ToolTip.SetShowDelay(close, 650);
            TabStripPanel.Children.Add(container);
        }
        Dispatcher.UIThread.Post(UpdateTabOverflowButtons, DispatcherPriority.Render);
    }

    private void TabOverflowLeftClicked(object? sender, RoutedEventArgs e)
    {
        ScrollTabStrip(-1);
        e.Handled = true;
    }

    private void TabOverflowRightClicked(object? sender, RoutedEventArgs e)
    {
        ScrollTabStrip(1);
        e.Handled = true;
    }

    private void ScrollTabStrip(int direction)
    {
        if (!_settings.TabOverflowArrows || direction == 0) return;
        var viewport = Math.Max(1, TabStripScroll.Viewport.Width);
        var maxOffset = Math.Max(0, TabStripScroll.Extent.Width - viewport);
        var step = Math.Max(90, Math.Min(Math.Max(_settings.TabMaxWidth, 120) + 3, viewport * 0.65));
        var next = Math.Clamp(TabStripScroll.Offset.X + direction * step, 0, maxOffset);
        TabStripScroll.Offset = new Vector(next, TabStripScroll.Offset.Y);
        Dispatcher.UIThread.Post(UpdateTabOverflowButtons, DispatcherPriority.Render);
    }

    private void UpdateTabOverflowButtons()
    {
        if (TabOverflowLeftButton is null || TabOverflowRightButton is null || TabStripScroll is null) return;
        // At very small widths the three caption buttons and actual tab bodies outrank overflow
        // arrows. Hide the arrows and let the tabs compress/scroll rather than spending 56 DIP on
        // navigation chrome while the user is deliberately squeezing the window.
        if (!_settings.TabsEnabled || !_settings.TabOverflowArrows || Bounds.Width < 330)
        {
            TabOverflowLeftButton.IsVisible = false;
            TabOverflowRightButton.IsVisible = false;
            return;
        }

        var viewport = TabStripScroll.Viewport.Width;
        var extent = TabStripScroll.Extent.Width;
        var maxOffset = Math.Max(0, extent - Math.Max(0, viewport));
        var overflow = maxOffset > 1;
        var x = Math.Clamp(TabStripScroll.Offset.X, 0, maxOffset);
        TabOverflowLeftButton.IsVisible = overflow && x > 1;
        TabOverflowRightButton.IsVisible = overflow && x < maxOffset - 1;
    }

    private async void TabDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: Guid id } body) return;

        // The drag controller captures the pointer on the first press, which means Avalonia's
        // higher-level DoubleTapped gesture is not guaranteed to fire for a tab body. Detect the
        // native click count at the press boundary instead so double-click-to-close remains reliable
        // without weakening drag/reorder behaviour.
        if (_settings.DoubleClickTabCloses && e.GetCurrentPoint(body).Properties.IsLeftButtonPressed && e.ClickCount >= 2)
        {
            e.Handled = true;
            _tabDrag.Clear();
            await CloseTabWithPolicyAsync(id, "double_click");
            return;
        }

        await _tabDrag.PointerPressedAsync(id, body, e);
    }

    private async void TabDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Border { DataContext: Guid id } body)
            await _tabDrag.PointerMovedAsync(id, body, e);
    }

    private void ApplyTabDragPreview(Guid draggedId, double draggedOffsetX)
    {
        var borders = TabStripPanel.Children.OfType<Border>().ToList();
        var originalIndex = _workspace.Tabs.ToList().FindIndex(t => t.Id == draggedId);
        var moving = borders.FirstOrDefault(b => Equals(b.DataContext, draggedId));
        if (moving is null || originalIndex < 0) return;

        moving.RenderTransform = new TranslateTransform(draggedOffsetX, 0);
        moving.ZIndex = 50;
        foreach (var border in borders)
        {
            if (Equals(border.DataContext, draggedId)) continue;
            border.RenderTransform = null;
            border.ZIndex = 0;
        }

        // Cross-window attachment owns a separate highlight. Inside one strip, move neighbours
        // continuously instead of snapping them a whole slot at a threshold. The first neighbour
        // begins yielding after roughly one-quarter tab width and reaches its destination by
        // three-quarters; later neighbours follow the same rolling window. This tracks Chromium's
        // physical feel much more closely while keeping the dragged tab exactly under the pointer.
        if (_tabAttach.Target is not null) return;
        var slot = Math.Max(1, moving.Bounds.Width + 3);
        var slotsTravelled = draggedOffsetX / slot;
        static double Smooth(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t * t * (3 - 2 * t);
        }

        if (slotsTravelled > 0)
        {
            for (var i = originalIndex + 1; i < borders.Count; i++)
            {
                var relative = i - originalIndex;
                var progress = Smooth((slotsTravelled - (relative - 0.75)) / 0.5);
                if (progress <= 0) continue;
                borders[i].RenderTransform = new TranslateTransform(-slot * progress, 0);
            }
        }
        else if (slotsTravelled < 0)
        {
            var travelled = -slotsTravelled;
            for (var i = originalIndex - 1; i >= 0; i--)
            {
                var relative = originalIndex - i;
                var progress = Smooth((travelled - (relative - 0.75)) / 0.5);
                if (progress <= 0) continue;
                borders[i].RenderTransform = new TranslateTransform(slot * progress, 0);
            }
        }
    }

    private async void TabDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border { DataContext: Guid id })
            await _tabDrag.PointerReleasedAsync(id, e);
    }

    private void TabDragPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Border { DataContext: Guid id }) _tabDrag.PointerCaptureLost(id);
    }

    private void ClearTabDragState() => _tabDrag.Clear();

    private void ClearTabVisuals()
    {
        foreach (var border in TabStripPanel.Children.OfType<Border>())
        {
            border.RenderTransform = null;
            border.ZIndex = 0;
            border.Classes.Remove("dragging");
        }
    }

    private TabDragHost CreateTabDragHost() => new()
    {
        TabCount = () => _workspace.Tabs.Count,
        IndexOfTab = id => _workspace.Tabs.ToList().FindIndex(x => x.Id == id),
        ContainsTab = id => _workspace.Tabs.Any(x => x.Id == id),
        IsFullscreen = () => WindowState == WindowState.FullScreen,
        TabAttachEnabled = () => _settings.TabAttachEnabled,
        TabDetachEnabled = () => _settings.TabDetachEnabled,
        SelectForDrag = id =>
        {
            if (_workspace.Active?.Id != id) CaptureActiveTabRuntimeState();
            if (!_workspace.Select(id)) return;
            foreach (var border in TabStripPanel.Children.OfType<Border>())
            {
                border.Classes.Remove("active");
                if (Equals(border.DataContext, id)) border.Classes.Add("active");
            }
        },
        CloseMiddleAsync = id => CloseTabWithPolicyAsync(id, "middle_click"),
        Capture = (body, pointer) => pointer.Capture(body),
        ReleaseCapture = pointer => pointer.Capture(null),
        IsLeftPressed = (body, e) => e.GetCurrentPoint(body).Properties.IsLeftButtonPressed,
        StripPoint = e => e.GetPosition(TabStripPanel),
        ScreenPoint = e => this.PointToScreen(e.GetPosition(this)),
        IsBeyondTearOff = IsBeyondTabTearOffThreshold,
        TabStripContains = point => GetTabStripScreenRect().Contains(point),
        GetInsertIndex = GetTabInsertIndex,
        ApplyPreview = (id, offset, _) => ApplyTabDragPreview(id, offset),
        ClearVisuals = ClearTabVisuals,
        BeginSoleNativeMoveAsync = async (id, cursor) =>
        {
            _suppressNextTabClick = id;
            await BeginHeldSoleTabNativeMoveAsync(id, cursor);
        },
        BeginTearOffAsync = async (id, cursor) =>
        {
            _suppressNextTabClick = id;
            await BeginHeldTabTearOffAsync(id, cursor);
        },
        AttachAsync = async (id, target, index) =>
        {
            CaptureTabRuntimeState(id);
            var transferred = _workspace.RemoveForTransfer(id);
            if (transferred is null) return false;
            await target.AcceptTransferredTabAsync(transferred, Math.Max(0, index));
            _suppressNextTabClick = id;
            _diagnostics.Write("tabs", "attached", new { id, target = target.Title, targetIndex = index });
            HandleSourceAfterTransfer(successfulCrossWindowTransfer: true);
            return true;
        },
        DetachAsync = async (id, cursor) =>
        {
            await DetachTabAsync(id, cursor);
            _suppressNextTabClick = id;
        },
        ReorderAsync = (id, index) =>
        {
            _workspace.Move(id, index);
            _suppressNextTabClick = id;
            _diagnostics.Write("tabs", "reordered", new { id, targetIndex = index });
            return Task.CompletedTask;
        },
        ActivateAsync = ActivateWorkspaceAsync,
        RebuildTabs = RebuildTabStrip,
        Diagnostic = (name, data) => _diagnostics.Write("tabs", name, data)
    };

    private async Task<bool> BeginHeldTabTearOffAsync(Guid id, PixelPoint cursor)
    {
        CaptureTabRuntimeState(id);
        var transferred = _workspace.RemoveForTransfer(id);
        if (transferred is null) return false;

        var detached = new MainWindow(transferred, _settings.CloneState(), deferWorkspaceActivation: true);
        CopyWindowGeometryTo(detached);
        detached.Show();
        detached.Position = new PixelPoint(Math.Max(0, cursor.X - 120), Math.Max(0, cursor.Y - 22));
        RebuildTabStrip();
        _diagnostics.Write("tabs", "held_tearoff_started", new { id, screenX = cursor.X, screenY = cursor.Y, moveLoop = "native-windows" });
        await detached.RunNativeDetachedTabDragAsync(transferred.Id, this, cursor);
        return true;
    }

    private async Task BeginHeldSoleTabNativeMoveAsync(Guid id, PixelPoint cursor)
    {
        _physicalDraggedTabId = id;
        _physicalDragSource = this;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = false;
        UpdateNativeDetachedAttachTarget(cursor);
        PositionChanged += SoleTabNativePositionChanged;
        _diagnostics.Write("tabs", "sole_native_move_started", new { id, screenX = cursor.X, screenY = cursor.Y, moveLoop = "native-windows" });

        var nativeMoveStarted = TryRunNativeWindowMoveLoop();
        PositionChanged -= SoleTabNativePositionChanged;
        if (!TryGetPhysicalCursor(out var releaseCursor)) releaseCursor = cursor;
        UpdateNativeDetachedAttachTarget(releaseCursor);
        if (!nativeMoveStarted)
            _diagnostics.Write("tabs", "sole_native_move_unavailable", new { id, platform = Environment.OSVersion.Platform.ToString() });

        await CompleteSoleTabNativeMoveAsync(id, releaseCursor);
    }

    private void SoleTabNativePositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_physicalDraggedTabId is null || !TryGetPhysicalCursor(out var cursor)) return;
        UpdateNativeDetachedAttachTarget(cursor);
    }

    private async Task CompleteSoleTabNativeMoveAsync(Guid id, PixelPoint cursor)
    {
        var target = _physicalAttach.Target;
        var targetIndex = _physicalAttach.TargetIndex;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = true;
        try
        {
            if (target is { IsVisible: true } && target.CanAcceptTabAttach && _settings.TabAttachEnabled)
            {
                CaptureTabRuntimeState(id);
                var transferred = _workspace.RemoveForTransfer(id);
                if (transferred is not null)
                {
                    await target.AcceptTransferredTabAsync(transferred, Math.Max(0, targetIndex));
                    _diagnostics.Write("tabs", "sole_native_drag_attached", new { id, target = target.Title, targetIndex });
                    HandleSourceAfterTransfer(successfulCrossWindowTransfer: true);
                }
            }
            else
            {
                await ActivateWorkspaceAsync();
                Activate();
                _diagnostics.Write("tabs", "sole_native_drag_committed", new { id, screenX = cursor.X, screenY = cursor.Y });
            }
        }
        finally
        {
            _physicalDraggedTabId = null;
            _physicalDragSource = null;
            if (IsVisible) RebuildTabStrip();
        }
    }

    private async Task RunNativeDetachedTabDragAsync(Guid tabId, MainWindow source, PixelPoint cursor)
    {
        _physicalDraggedTabId = tabId;
        _physicalDragSource = source;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = false;
        Position = new PixelPoint(Math.Max(0, cursor.X - 120), Math.Max(0, cursor.Y - 22));
        UpdateNativeDetachedAttachTarget(cursor);
        PositionChanged += NativeDetachedPositionChanged;
        _diagnostics.Write("tabs", "native_detached_move_started", new { tabId, source = source.Title, screenX = cursor.X, screenY = cursor.Y });

        var nativeMoveStarted = TryRunNativeWindowMoveLoop();
        PositionChanged -= NativeDetachedPositionChanged;
        if (!TryGetPhysicalCursor(out var releaseCursor)) releaseCursor = cursor;
        UpdateNativeDetachedAttachTarget(releaseCursor);

        if (!nativeMoveStarted)
            _diagnostics.Write("tabs", "native_detached_move_unavailable", new { tabId, platform = Environment.OSVersion.Platform.ToString() });

        await CompleteNativeDetachedTabDragAsync(tabId, releaseCursor);
    }

    private void NativeDetachedPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_physicalDraggedTabId is null || !TryGetPhysicalCursor(out var cursor)) return;
        UpdateNativeDetachedAttachTarget(cursor);
    }

    private void UpdateNativeDetachedAttachTarget(PixelPoint cursor)
    {
        _physicalAttach.Update(cursor, _settings.TabAttachEnabled, "native_merge_target_entered", "native_merge_target_left", "tabs");
    }

    private bool TryRunNativeWindowMoveLoop()
    {
        if (!OperatingSystem.IsWindows() || (GetAsyncKeyState(VkLButton) & 0x8000) == 0) return false;
        var platform = TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero) return false;
        try
        {
            ReleaseNativeCapture();
            SendMessageW(platform.Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task CompleteNativeDetachedTabDragAsync(Guid tabId, PixelPoint cursor)
    {
        var attachTarget = _physicalAttach.Target;
        var attachIndex = _physicalAttach.TargetIndex;
        _physicalAttach.Clear();
        MainRoot.IsHitTestVisible = true;

        var attachedElsewhere = false;
        try
        {
            if (attachTarget is { IsVisible: true } && attachTarget.CanAcceptTabAttach && _settings.TabAttachEnabled)
            {
                var transferred = _workspace.RemoveForTransfer(tabId);
                if (transferred is not null)
                {
                    await attachTarget.AcceptTransferredTabAsync(transferred, Math.Max(0, attachIndex));
                    attachedElsewhere = true;
                    _diagnostics.Write("tabs", "native_drag_attached", new { tabId, target = attachTarget.Title, attachIndex });
                }
                if (_workspace.Tabs.Count == 0) Close();
            }
            else
            {
                _deferWorkspaceActivation = false;
                await ActivateWorkspaceAsync();
                Activate();
                _diagnostics.Write("tabs", "native_drag_detached_committed", new { tabId, screenX = cursor.X, screenY = cursor.Y });
            }
        }
        finally
        {
            var sourceWindow = _physicalDragSource;
            _physicalDraggedTabId = null;
            _physicalDragSource = null;
            if (sourceWindow is { IsVisible: true })
            {
                sourceWindow.HandleSourceAfterTransfer(successfulCrossWindowTransfer: attachedElsewhere);
                if (sourceWindow.IsVisible && sourceWindow._workspace.Tabs.Count > 0)
                    await sourceWindow.ActivateWorkspaceAsync();
            }
        }
    }

    private bool IsBeyondTabTearOffThreshold(PixelPoint point)
    {
        if (!GetWindowScreenRect().Contains(point)) return true;
        var strip = GetTabStripScreenRect();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        var verticalTolerance = Math.Max(18, (int)Math.Round(22 * scale));
        return point.Y < strip.Y - verticalTolerance || point.Y > (strip.Y + strip.Height) + verticalTolerance;
    }

    private ScreenPixelRect GetWindowScreenRect()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        // Position is physical; Bounds is logical. Include a small caption/border allowance so a
        // slight vertical tab drag does not accidentally tear off on high-DPI desktops.
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        return new ScreenPixelRect(Position.X - 8, Position.Y - 8, width + 16, height + 48);
    }

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private const int ScClose = 0xF060;
    private const int ScRestore = 0xF120;
    private const int HtCaption = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll", EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseNativeCapture();

    [DllImport("user32.dll", EntryPoint = "GetCapture")]
    private static extern IntPtr GetNativeCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);


    private static (bool Left, bool Right, bool Middle) PhysicalMouseButtons(PointerPoint point)
    {
        var kind = point.Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed) return (true, false, false);
        if (kind == PointerUpdateKind.RightButtonPressed) return (false, true, false);
        if (kind == PointerUpdateKind.MiddleButtonPressed) return (false, false, true);

        return (point.Properties.IsLeftButtonPressed, point.Properties.IsRightButtonPressed, point.Properties.IsMiddleButtonPressed);
    }

    private static bool TryGetPhysicalCursor(out PixelPoint point)
    {
        if (GetCursorPos(out var native))
        {
            point = new PixelPoint(native.X, native.Y);
            return true;
        }
        point = default;
        return false;
    }

    internal ScreenPixelRect GetTabStripScreenRect()
    {
        try
        {
            var originInWindow = TabStripScroll.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
            var topLeft = this.PointToScreen(originInWindow);
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            return new ScreenPixelRect(topLeft.X, topLeft.Y, Math.Max(1, (int)Math.Ceiling(TabStripScroll.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(TabStripScroll.Bounds.Height * scale)));
        }
        catch
        {
            return new ScreenPixelRect(Position.X, Position.Y, Math.Max(1, (int)Width), 50);
        }
    }

    /// <summary>
    /// Forgiving physical-pixel hit zone used only while a tab is being dragged. It covers the
    /// full tab-strip width plus a modest horizontal margin and most of the chrome/content seam;
    /// the top edge remains inset so dropping over a native title bar can never merge a window.
    /// </summary>
    internal ScreenPixelRect GetTabAttachZoneScreenRect()
    {
        var strip = GetTabStripScreenRect();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        var horizontal = Math.Max(24, (int)Math.Round(28 * scale));
        var top = Math.Max(4, (int)Math.Round(6 * scale));
        var bottom = Math.Max(56, (int)Math.Round(78 * scale));
        return strip.Inflate(horizontal, top, bottom);
    }

    internal int GetTabInsertIndex(PixelPoint screenPoint)
    {
        if (_workspace.Tabs.Count == 0) return 0;
        var pointInWindow = this.PointToClient(screenPoint);
        var panelOrigin = TabStripPanel.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var clientX = pointInWindow.X - panelOrigin.X;
        var borders = TabStripPanel.Children.OfType<Border>().Where(x => x.Classes.Contains("glideTab")).ToList();
        for (var i = 0; i < borders.Count; i++)
        {
            var midpoint = borders[i].Bounds.X + borders[i].Bounds.Width / 2;
            if (clientX < midpoint) return i;
        }
        return borders.Count;
    }

    internal void SetTabAttachHighlight(bool enabled)
    {
        if (enabled)
        {
            if (!ChromeBorder.Classes.Contains("attachTargetStrip")) ChromeBorder.Classes.Add("attachTargetStrip");
        }
        else ChromeBorder.Classes.Remove("attachTargetStrip");
    }

    internal void OnAttachTargetClosed(MainWindow closedWindow)
    {
        var ordinaryTarget = _tabAttach.Target;
        if (ordinaryTarget is not null && ReferenceEquals(ordinaryTarget, closedWindow))
        {
            _tabDrag.ClearAttachTarget();
            _diagnostics.Write("tabs", "merge_target_closed", new { target = closedWindow.Title, mode = "ordinary" });
        }

        var nativeTarget = _physicalAttach.Target;
        if (nativeTarget is not null && ReferenceEquals(nativeTarget, closedWindow))
        {
            _physicalAttach.Clear();
            _diagnostics.Write("tabs", "merge_target_closed", new { target = closedWindow.Title, mode = "native" });
        }
    }

    internal async Task AcceptTransferredTabAsync(TabState tab, int index)
    {
        _workspace.InsertTransferred(tab, Math.Clamp(index, 0, _workspace.Tabs.Count), activate: true);
        _diagnostics.Write("tabs", "transfer_received", new { tab.Id, tab.Kind, index, count = _workspace.Tabs.Count });
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
        Activate();
    }

    private async Task DetachTabAsync(Guid id, PixelPoint screenPoint)
    {
        if (!_settings.TabDetachEnabled) return;
        CaptureTabRuntimeState(id);
        var transferred = _workspace.RemoveForTransfer(id);
        if (transferred is null) return;
        var detached = new MainWindow(transferred, _settings.CloneState());
        CopyWindowGeometryTo(detached);
        detached.Show();
        detached.Position = new PixelPoint(Math.Max(0, screenPoint.X - 100), Math.Max(0, screenPoint.Y - 24));
        _diagnostics.Write("tabs", "detached", new { id, screenX = screenPoint.X, screenY = screenPoint.Y });
        HandleSourceAfterTransfer();
        await Task.CompletedTask;
    }

    private void HandleSourceAfterTransfer(bool successfulCrossWindowTransfer = false)
    {
        if (_workspace.Tabs.Count > 0) return;

        // Legacy invariant: if a window's final tab successfully joins another existing Glide
        // window, the source closes automatically. Do not manufacture a Home tab in that case.
        if (successfulCrossWindowTransfer && GlideWindowRegistry.Snapshot().Count > 1)
        {
            Close();
            return;
        }

        if (_settings.CloseEmptyWindowAfterDetach && GlideWindowRegistry.Snapshot().Count > 1)
            Close();
        else
            _workspace.EnsureHomeIfEmpty();
    }

    private ContextMenu BuildTabContextMenu(TabState tab)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        items.Add(Item($"Duplicate tab\t{ShortcutFor(GlideCommand.DuplicateTab)}", () => _ = DuplicateTabByIdAsync(tab.Id)));
        items.Add(Item("Move tab to new window", () => _ = DetachTabAsync(tab.Id, this.PointToScreen(new Point(Bounds.Width / 2, 42))), _settings.TabDetachEnabled));
        if (tab is ImageTabState image)
            items.Add(Item("Open containing folder in Explorer tab", () => OpenBrowserTab(Path.GetDirectoryName(image.Path) ?? "")));
        else if (tab is BrowserTabState browser)
            items.Add(Item("Open first image in folder", () => _ = OpenFirstImageInFolderAsync(browser.Folder)));
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item($"Reopen closed tab\t{ShortcutFor(GlideCommand.RestoreClosedTab)}", () => _ = RestoreClosedTabAsync(), _workspace.ClosedCount > 0));
        items.Add(Item($"Close tab\t{ShortcutFor(GlideCommand.CloseTab)}", () => _ = CloseTabByIdAsync(tab.Id)));
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return menu;
    }

    private async Task DuplicateTabByIdAsync(Guid id)
    {
        if (!_workspace.Select(id)) return;
        if (_workspace.DuplicateActive() is not null) await ActivateWorkspaceAsync();
    }

    private Task CloseTabByIdAsync(Guid id) => CloseTabWithPolicyAsync(id, "command");

    private async Task CloseTabWithPolicyAsync(Guid id, string source)
    {
        if (_workspace.Tabs.Count == 1 && string.Equals(_settings.LastTabCloseBehavior, "Keep last tab open", StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Write("tabs", "last_tab_close_refused", new { id, source });
            return;
        }
        if (!_workspace.Close(id, ensureHome: false)) return;
        _diagnostics.Write("tabs", "closed", new { id, source, remaining = _workspace.Tabs.Count });
        if (_workspace.Tabs.Count == 0)
        {
            if (string.Equals(_settings.LastTabCloseBehavior, "Close program", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            return;
        }
        await ActivateWorkspaceAsync();
    }

    private TabState CreateConfiguredHomeDestination()
    {
        if (string.Equals(_settings.HomePageMode, "Browser page", StringComparison.OrdinalIgnoreCase))
            return _workspace.AddBrowser(ResolveHomeBrowserFolder());
        return _workspace.AddHome();
    }

    private void ApplyConfiguredStartupWorkspace()
    {
        var action = string.IsNullOrWhiteSpace(_settings.StartupAction) ? "Welcome tab" : _settings.StartupAction;
        if (string.Equals(action, "Open last session", StringComparison.OrdinalIgnoreCase))
        {
            var restored = WorkspaceSessionStore.Load();
            if (restored is { } session)
            {
                _workspace.Reset(session.Tabs, session.ActiveIndex);
                _diagnostics.Write("workspace", "startup_session_restored", new { tabs = session.Tabs.Count, session.ActiveIndex });
                return;
            }
            action = "Welcome tab";
        }

        TabState initial = action switch
        {
            "Explorer tab" => new BrowserTabState(Guid.NewGuid(), ResolveHomeBrowserFolder()),
            "Custom" => CreateStandaloneCustomDestination(_settings.StartupCustomPath) ?? new HomeTabState(Guid.NewGuid()),
            _ => new HomeTabState(Guid.NewGuid())
        };
        _workspace.Reset(new[] { initial }, 0);
    }

    private TabState CreateConfiguredNewTabDestination()
    {
        var action = string.IsNullOrWhiteSpace(_settings.NewTabAction) ? "Explorer tab" : _settings.NewTabAction;
        if (string.Equals(action, "Welcome tab", StringComparison.OrdinalIgnoreCase)) return _workspace.AddHome();
        if (string.Equals(action, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            var path = _settings.NewTabCustomPath;
            if (Directory.Exists(path)) return _workspace.AddBrowser(path);
            if (File.Exists(path) && ImageNavigator.IsSupported(path)) return _workspace.AddImage(path);
            return _workspace.AddHome();
        }
        return _workspace.AddBrowser(ResolveFreshExplorerFolder());
    }

    private string ResolveFreshExplorerFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.NewExplorerTabDefaultDirectory) && Directory.Exists(_settings.NewExplorerTabDefaultDirectory))
            return Path.GetFullPath(_settings.NewExplorerTabDefaultDirectory);
        if (_settings.NewExplorerTabsUseLastLocation && Directory.Exists(_settings.LastNewExplorerTabDirectory))
            return Path.GetFullPath(_settings.LastNewExplorerTabDirectory);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures)) return pictures;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    private static TabState? CreateStandaloneCustomDestination(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Directory.Exists(path)) return new BrowserTabState(Guid.NewGuid(), Path.GetFullPath(path));
        if (File.Exists(path) && ImageNavigator.IsSupported(path)) return new ImageTabState(Guid.NewGuid(), Path.GetFullPath(path));
        return null;
    }

    private string ResolveHomeBrowserFolder()
    {
        if (_settings.RememberLastOpenLocation && Directory.Exists(_settings.LastOpenDirectory)) return _settings.LastOpenDirectory;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures) && Directory.Exists(pictures)) return pictures;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile) ? profile : AppContext.BaseDirectory;
    }

    private void OpenBrowserTab(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        _workspace.AddBrowser(folder);
        _ = ActivateWorkspaceAsync();
    }

    private async Task OpenFirstImageInFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string? first = null;
        try { first = Directory.EnumerateFiles(folder).Where(ImageNavigator.IsSupported).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); } catch { }
        if (first is not null) await OpenAsImageTabAsync(first);
    }

    private async void TabCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Guid id }) return;
        await CloseTabWithPolicyAsync(id, "close_button");
    }

    private async void TabNavBackClicked(object? sender, RoutedEventArgs e)
    {
        if (!_titleNavCanBack) return;
        await NavigateTitleUpAsync();
    }

    /// <summary>
    /// Title-bar Back is deliberately an Up/containing-folder operation, not classical history Back.
    /// The currently presented image path is the source of truth. This makes the command independent
    /// of how the file entered Glide (shell/file association, Ctrl+O, CLI, drag-drop, Recent, Explorer).
    /// </summary>
    private async Task NavigateTitleUpAsync()
    {
        var active = _workspace.Active;
        if (ImageView.IsVisible && !string.IsNullOrWhiteSpace(_presentedPath) && File.Exists(_presentedPath))
        {
            var imagePath = Path.GetFullPath(_presentedPath);
            var folder = Path.GetDirectoryName(imagePath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

            // Preserve the stable active tab identity when possible. If a transient/state mismatch is
            // ever observed, repair it here rather than making the UI button silently do nothing.
            var id = active?.Id ?? Guid.NewGuid();
            _tabForwardImageTargets[id] = imagePath;
            _tabForwardFolderTargets[id] = new Stack<string>();
            _browserHighlightTargets[id] = imagePath;

            var browserSession = new BrowserSession();
            browserSession.History.Add(folder);
            browserSession.Index = 0;
            _browserSessions[id] = browserSession;
            var browserState = new BrowserTabState(id, folder)
            {
                Navigation = new BrowserNavigationState(browserSession.History.ToArray(), browserSession.Index)
            };
            if (!_workspace.ReplaceTab(browserState))
            {
                _workspace.AddBrowser(folder);
                if (_workspace.Active is BrowserTabState replacement)
                {
                    id = replacement.Id;
                    _tabForwardImageTargets[id] = imagePath;
                    _tabForwardFolderTargets[id] = new Stack<string>();
                    _browserHighlightTargets[id] = imagePath;
                    _browserSessions[id] = browserSession;
                }
            }
            await ActivateWorkspaceAsync();
            SelectBrowserHighlight(id);
            UpdateTabNavigationButtons();
            _diagnostics.Write("navigation", "title_up_image_to_folder", new { imagePath, folder, tab = id });
            return;
        }

        if (active is BrowserTabState browser)
        {
            var parent = Directory.GetParent(browser.Folder)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return;

            if (!_tabForwardFolderTargets.TryGetValue(browser.Id, out var forwardFolders))
                _tabForwardFolderTargets[browser.Id] = forwardFolders = new Stack<string>();
            forwardFolders.Push(browser.Folder);
            NavigateBrowserTo(parent, addHistory: false);
            UpdateTabNavigationButtons();
            _diagnostics.Write("navigation", "title_up_parent", new { from = browser.Folder, parent, tab = browser.Id });
        }
    }

    private async void TabNavForwardClicked(object? sender, RoutedEventArgs e)
    {
        if (!_titleNavCanForward) return;
        if (_workspace.Active is not BrowserTabState browser) return;

        // Forward first retraces folders traversed by the Up button, then returns to the image state.
        if (_tabForwardFolderTargets.TryGetValue(browser.Id, out var forwardFolders) && forwardFolders.Count > 0)
        {
            var folder = forwardFolders.Pop();
            if (Directory.Exists(folder))
            {
                NavigateBrowserTo(folder, addHistory: false);
                SelectBrowserHighlight(browser.Id);
                UpdateTabNavigationButtons();
                return;
            }
        }

        if (_tabForwardImageTargets.TryGetValue(browser.Id, out var imagePath) &&
            File.Exists(imagePath) && ImageNavigator.IsSupported(imagePath))
        {
            _workspace.ReplaceTab(new ImageTabState(browser.Id, imagePath));
            _tabForwardImageTargets.Remove(browser.Id);
            _tabForwardFolderTargets.Remove(browser.Id);
            _browserHighlightTargets.Remove(browser.Id);
            await ActivateWorkspaceAsync();
            UpdateTabNavigationButtons();
        }
    }

    private void ApplyTabUtilityButtonVisibility()
    {
        TabNavBackButton.IsVisible = _settings.TabBarShowBackButton;
        TabNavForwardButton.IsVisible = _settings.TabBarShowForwardButton;
        TabNavBackButton.ContextMenu = BuildTabUtilityContextMenu("Up", () => { _settings.TabBarShowBackButton = false; SaveSettingsAndPublish(); ApplyTabUtilityButtonVisibility(); });
        TabNavForwardButton.ContextMenu = BuildTabUtilityContextMenu("Forward", () => { _settings.TabBarShowForwardButton = false; SaveSettingsAndPublish(); ApplyTabUtilityButtonVisibility(); });
        UpdateTabNavigationButtons();
    }

    private void UpdateTabNavigationButtons()
    {
        if (TabNavBackButton is null || TabNavForwardButton is null) return;
        var canBack = false;
        var canForward = false;
        switch (_workspace.Active)
        {
            case ImageTabState image:
                var sourcePath = ImageView.IsVisible && !string.IsNullOrWhiteSpace(_currentPath) ? _currentPath : image.Path;
                canBack = File.Exists(sourcePath) && Directory.Exists(Path.GetDirectoryName(sourcePath));
                canForward = false;
                break;
            case BrowserTabState browser:
                canBack = Directory.GetParent(browser.Folder) is not null;
                canForward = (_tabForwardFolderTargets.TryGetValue(browser.Id, out var forwardFolders) && forwardFolders.Count > 0)
                    || (_tabForwardImageTargets.TryGetValue(browser.Id, out var target) && File.Exists(target));
                break;
        }
        _titleNavCanBack = canBack;
        _titleNavCanForward = canForward;
        TabNavBackButton.Classes.Set("inactive", !canBack);
        TabNavForwardButton.Classes.Set("inactive", !canForward);
        TabNavBackButton.Opacity = canBack ? 1.0 : 0.34;
        TabNavForwardButton.Opacity = canForward ? 1.0 : 0.34;
        TabNavBackButton.IsEnabled = true;
        TabNavForwardButton.IsEnabled = true;
    }

    private void SelectBrowserHighlight(Guid tabId)
    {
        if (!_browserHighlightTargets.TryGetValue(tabId, out var target) || string.IsNullOrWhiteSpace(target)) return;
        var index = _browserEntries.FindIndex(entry => string.Equals(entry.Path, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _browserSelection.Clear();
        _browserSelection.Add(target);
        _browserSelectionAnchor = index;
        _browserCurrentIndex = index;
        UpdateRealizedBrowserSelection();
        try
        {
            var element = BrowserRepeater.GetOrCreateElement(index);
            element.BringIntoView();
        }
        catch { }
    }

    private ContextMenu BuildTabUtilityContextMenu(string label, Action remove)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        items.Add(Item($"Remove {label} button from title bar", remove));
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item("Customize title-bar buttons…", () => _ = ShowTitleBarCustomizerAsync()));
        items.Add(Item("Open Settings…", () => SettingsClicked(this, new RoutedEventArgs())));
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return menu;
    }

    private async void ChromeMiddleClickReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;

        // A tab middle-click closes on press. Closing rebuilds the strip before release, so using
        // only the release-time visual source can misclassify that same gesture as empty chrome
        // and immediately create a replacement tab. Preserve press ownership across the rebuild:
        // new-tab creation is legal only when the gesture both started and ended on empty chrome.
        var startedOnEmptyChrome = _chromeMiddlePressStartedOnEmptyChrome && ReferenceEquals(_chromeMiddlePressPointer, e.Pointer);
        var pressPosition = _chromeMiddlePressPosition;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        _chromeMiddlePressPointer = null;

        if (!startedOnEmptyChrome) return;
        if (e.Source is Control source && IsChromeInteractiveSource(source)) return;

        var releasePosition = e.GetPosition(ChromeBorder);
        const double clickMovementTolerance = 6.0;
        if (Math.Abs(releasePosition.X - pressPosition.X) > clickMovementTolerance ||
            Math.Abs(releasePosition.Y - pressPosition.Y) > clickMovementTolerance)
            return;

        var tab = CreateConfiguredNewTabDestination();
        _diagnostics.Write("tabs", "middle_click_new_tab", new { tab.Id, tab.Kind });
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
        e.Handled = true;
    }

    private static bool IsChromeInteractiveSource(Control source)
    {
        for (Control? current = source; current is not null; current = current.GetVisualParent() as Control)
        {
            if (current is Button || current.Classes.Contains("glideTab")) return true;
        }
        return false;
    }

    private async void NewTabClicked(object? sender, RoutedEventArgs e)
    {
        var tab = CreateConfiguredNewTabDestination();
        _diagnostics.Write("tabs", "new_tab_destination", new { tab.Id, tab.Kind, newTabAction = _settings.NewTabAction, count = _workspace.Tabs.Count });
        await ActivateWorkspaceAsync();
    }

    private void RefreshRecentHistoryHome()
    {
        if (_homeSurface is null) return;
        _homeSurface.RecentHost.Children.Clear();
        if (!_settings.RecentHistoryEnabled ||
            (!_settings.ShowRecentOnHome && !string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase)))
        {
            _homeSurface.RecentPanel.IsVisible = false;
            return;
        }

        var entries = RecentHistoryStore.Snapshot(_settings.HistorySize);
        if (string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase))
            entries = entries.Where(entry => !string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var entry in entries)
        {
            var button = new Button
            {
                Tag = entry,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Content = $"{(string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase) ? "Folder" : "Image")}   {entry.Path}"
            };
            button.Classes.Add("recentItem");
            ToolTip.SetTip(button, entry.Path);
            ToolTip.SetShowDelay(button, 650);
            button.Click += RecentHistoryItemClicked;
            _homeSurface.RecentHost.Children.Add(button);
        }
        _homeSurface.RecentPanel.IsVisible = entries.Count > 0;
    }

    private async void RecentHistoryItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecentHistoryStore.Entry entry }) return;
        if (string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(entry.Path)) { RefreshRecentHistoryHome(); return; }
            _workspace.AddBrowser(entry.Path);
            RecentHistoryStore.RecordFolder(entry.Path, _settings.HistorySize);
            await ActivateWorkspaceAsync();
            return;
        }
        if (File.Exists(entry.Path)) await OpenAsImageTabAsync(entry.Path);
        else RefreshRecentHistoryHome();
    }

    private void ClearRecentHistoryClicked(object? sender, RoutedEventArgs e)
    {
        RecentHistoryStore.Clear();
        RefreshRecentHistoryHome();
        _diagnostics.Write("history", "cleared");
    }

    private void CloseRecentHistoryClicked(object? sender, RoutedEventArgs e)
    {
        _settings.ShowRecentOnHome = false;
        SaveSettingsAndPublish();
        RefreshRecentHistoryHome();
    }

    private void MoveRecentHistoryClicked(object? sender, RoutedEventArgs e)
    {
        _settings.WelcomeRecentPosition = string.Equals(_settings.WelcomeRecentPosition, "Above tips", StringComparison.OrdinalIgnoreCase)
            ? "Below tips" : "Above tips";
        SaveSettingsAndPublish();
        ApplyWelcomeLayout();
    }

    private void ResizeRecentHistoryClicked(object? sender, RoutedEventArgs e)
    {
        _settings.WelcomeRecentSize = _settings.WelcomeRecentSize switch
        {
            "Compact" => "Large",
            "Large" => "Normal",
            _ => "Compact"
        };
        SaveSettingsAndPublish();
        ApplyWelcomeLayout();
    }

    private void ResetWelcomeLayoutClicked(object? sender, RoutedEventArgs e)
    {
        _settings.WelcomeRecentPosition = "Below tips";
        _settings.WelcomeRecentSize = "Normal";
        _settings.ShowRecentOnHome = true;
        _settings.ShowHomeTips = true;
        SaveSettingsAndPublish();
        ApplySettingsVisuals();
    }

    private void ApplyWelcomeLayout()
    {
        if (_homeSurface is null) return;
        var above = string.Equals(_settings.WelcomeRecentPosition, "Above tips", StringComparison.OrdinalIgnoreCase);
        Grid.SetRow(_homeSurface.RecentPanel, above ? 1 : 2);
        Grid.SetRow(_homeSurface.TipsGrid, above ? 2 : 1);
        _homeSurface.RecentPanel.Margin = new Thickness(0, above ? 20 : 16, 0, 0);
        _homeSurface.RecentMove.Kind = above ? "Down" : "Up";
        var size = string.IsNullOrWhiteSpace(_settings.WelcomeRecentSize) ? "Normal" : _settings.WelcomeRecentSize;
        _homeSurface.RecentScroll.MaxHeight = size switch { "Compact" => 150, "Large" => 420, _ => 10000 };
        _homeSurface.RecentResize.Kind = string.Equals(size, "Large", StringComparison.OrdinalIgnoreCase) ? "Collapse" : "Expand";
        ToolTip.SetTip(_homeSurface.RecentResizeButtonControl, $"Resize Recent. Current size: {size}. Click to cycle Normal → Compact → Large.");
    }

    private async void OpenClicked(object? sender, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = CodecCapabilityRegistry.RoutedExtensions.Select(x => "*" + x).ToArray()
                }
            }
        };
        options.SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OpenFileDefaultDirectory, _settings.LastOpenFileDirectory);
        var files = await StorageProvider.OpenFilePickerAsync(options);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _settings.LastOpenFileDirectory = Path.GetDirectoryName(path) ?? _settings.LastOpenFileDirectory;
            SaveSettingsAndPublish();
            await OpenAsImageTabAsync(path);
        }
    }

    private void TryScheduleAutomaticOverlayLayoutRestore()
    {
        if (_automaticOverlayLayoutRestoreScheduled || !_deferredStartupSettingsAdopted ||
            !_settings.OverlayPersistLayoutBetweenSessions || _windowLifetimeCts.IsCancellationRequested) return;
        _automaticOverlayLayoutRestoreScheduled = true;
        var layoutPath = AutomaticOverlayLayoutPath();
        Dispatcher.UIThread.Post(async () =>
        {
            if (_windowLifetimeCts.IsCancellationRequested || !IsEffectivelyVisible) return;
            try { await EnsureOverlays().LoadLayoutAsync(layoutPath, _windowLifetimeCts.Token); }
            catch (OperationCanceledException) { }
        }, DispatcherPriority.Loaded);
    }

    private static string AutomaticOverlayLayoutPath() =>
        Path.Combine(SettingsStore.GetSettingsDirectory(), "overlay-layout.json");

    private async void OverlayAddClicked(object? sender, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions { Title = "Add Window-in-Window image", AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Supported image formats") { Patterns = CodecCapabilityRegistry.RoutedExtensions.Select(x => "*" + x).ToArray() } } };
        options.SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OverlayDefaultDirectory, _settings.OverlayRememberFolder ? _settings.LastOverlayDirectory : null);
        var files = await StorageProvider.OpenFilePickerAsync(options);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) { if (_settings.OverlayRememberFolder) { _settings.LastOverlayDirectory = Path.GetDirectoryName(path) ?? _settings.LastOverlayDirectory; SaveSettingsAndPublish(); } await EnsureOverlays().AddAsync(path); }
        }
    }

    private void OverlayClearClicked(object? sender, RoutedEventArgs e) => _overlays?.Clear();

    private async void OverlaySaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_overlays?.HasOverlays != true) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save overlay layout",
            SuggestedFileName = "Glide Overlay Layout.glideoverlay",
            SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OverlayDefaultDirectory, _settings.OverlayRememberFolder ? _settings.LastOverlayDirectory : null),
            DefaultExtension = "glideoverlay",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType("Glide overlay layouts") { Patterns = new[] { "*.glideoverlay" } } }
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.OverlayRememberFolder) { _settings.LastOverlayDirectory = Path.GetDirectoryName(path) ?? _settings.LastOverlayDirectory; SaveSettingsAndPublish(); }
        EnsureOverlays().SaveLayout(path);
        _diagnostics.Write("overlay", "layout_saved", new { path });
    }

    private async void OverlayLoadClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load overlay layout",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolvePickerFolderAsync(_settings.OverlayDefaultDirectory, _settings.OverlayRememberFolder ? _settings.LastOverlayDirectory : null),
            FileTypeFilter = new[] { new FilePickerFileType("Glide overlay layouts") { Patterns = new[] { "*.glideoverlay" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_settings.OverlayRememberFolder) { _settings.LastOverlayDirectory = Path.GetDirectoryName(path) ?? _settings.LastOverlayDirectory; SaveSettingsAndPublish(); }
        await EnsureOverlays().LoadLayoutAsync(path);
        _diagnostics.Write("overlay", "layout_loaded", new { path });
    }

    private void UpdateOverlayStatusButtons()
    {
        if (OverlaySaveButton is null || OverlayClearButton is null) return;
        OverlaySaveButton.IsVisible = _overlays?.HasOverlays == true;
        OverlayClearButton.IsVisible = _overlays?.HasOverlays == true;
    }

    private async void OverlayKeyboardShortcut(object? sender, KeyEventArgs e)
    {
        if (!_settings.OverlayKeyboardZoom || !ImageView.IsVisible || _overlays?.HasSelection != true) return;
        if (e.Key == Key.Add || (e.Key == Key.OemPlus && (e.KeyModifiers & KeyModifiers.Control) != 0)) { EnsureOverlays().ZoomSelected(1 + Math.Clamp(_settings.OverlayZoomStepPercent, 1, 100) / 100.0); e.Handled = true; }
        else if (e.Key == Key.Subtract || (e.Key == Key.OemMinus && (e.KeyModifiers & KeyModifiers.Control) != 0)) { var step = 1 + Math.Clamp(_settings.OverlayZoomStepPercent, 1, 100) / 100.0; EnsureOverlays().ZoomSelected(1.0 / step); e.Handled = true; }
        await Task.CompletedTask;
    }

    private async void OpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = "Open folder",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveRememberedPickerFolderAsync()
        };
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        RememberPickerDirectory(folder);
        if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFolder(folder, _settings.HistorySize);
        _workspace.AddBrowser(folder);
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
    }

    private async Task<IStorageFolder?> ResolvePickerFolderAsync(string? configuredDirectory, string? lastDirectory)
    {
        var directory = !string.IsNullOrWhiteSpace(configuredDirectory) ? configuredDirectory : lastDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            var full = Path.GetFullPath(directory);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return await StorageProvider.TryGetFolderFromPathAsync(full);
        }
        catch { return null; }
    }

    private async Task<IStorageFolder?> ResolveRememberedPickerFolderAsync()
    {
        if (!_settings.RememberLastOpenLocation || string.IsNullOrWhiteSpace(_settings.LastOpenDirectory)) return null;
        try
        {
            if (!Directory.Exists(_settings.LastOpenDirectory)) return null;
            // Avoid forcing a network location through the picker startup path: unavailable/slow
            // remembered locations must never make opening a local image feel hung.
            if (Path.GetFullPath(_settings.LastOpenDirectory).StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return await StorageProvider.TryGetFolderFromPathAsync(_settings.LastOpenDirectory);
        }
        catch { return null; }
    }

    private void RememberPickerDirectory(string? directory)
    {
        if (!_settings.RememberLastOpenLocation || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        _settings.LastOpenDirectory = Path.GetFullPath(directory);
        SaveSettingsAndPublish();
    }

    private async void DropReceived(object? sender, DragEventArgs e)
    {
        var item = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        var path = item?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Directory.Exists(path))
        {
            if (_settings.RecentHistoryEnabled) RecentHistoryStore.RecordFolder(path, _settings.HistorySize);
            RememberPickerDirectory(path);
            _workspace.AddBrowser(path);
            RebuildTabStrip();
            await ActivateWorkspaceAsync();
            return;
        }
        await OpenAsImageTabAsync(path);
    }

    private void PreviousFolderTopClicked(object? sender, RoutedEventArgs e) => _ = RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(-1, forceFirstImage: true));
    private void NextFolderTopClicked(object? sender, RoutedEventArgs e) => _ = RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(1, forceFirstImage: true));
    private void PreviousFolderClicked(object? sender, RoutedEventArgs e) => _ = RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(-1));
    private void NextFolderClicked(object? sender, RoutedEventArgs e) => _ = RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(1));

    private async void ExploreFolderClicked(object? sender, RoutedEventArgs e)
    {
        var folder = string.IsNullOrWhiteSpace(_currentPath) ? null : Path.GetDirectoryName(_currentPath);
        if (folder is null || !Directory.Exists(folder)) return;
        _workspace.AddBrowser(folder);
        RebuildTabStrip();
        await ActivateWorkspaceAsync();
    }

    private void NavigateBrowserTo(string folder, bool addHistory)
    {
        if (_workspace.Active is not BrowserTabState browser || !Directory.Exists(folder)) return;
        folder = Path.GetFullPath(folder);
        _settings.LastNewExplorerTabDirectory = folder;
        SaveSettingsAndPublish();
        _workspace.ReplaceActiveBrowserFolder(folder);
        var id = browser.Id;
        if (!_browserSessions.TryGetValue(id, out var session)) _browserSessions[id] = session = new BrowserSession();
        if (addHistory)
        {
            // A new explicit browser navigation creates a new branch, just like a browser: discard
            // the transient Up/Forward chain created by the title-bar navigation controls.
            _tabForwardFolderTargets.Remove(id);
            _tabForwardImageTargets.Remove(id);
            if (session.Index >= 0 && session.Index < session.History.Count - 1)
                session.History.RemoveRange(session.Index + 1, session.History.Count - session.Index - 1);
            if (session.History.Count == 0 || !string.Equals(session.History[^1], folder, StringComparison.OrdinalIgnoreCase))
            {
                session.History.Add(folder);
                session.Index = session.History.Count - 1;
            }
        }
        else if (session.History.Count == 0)
        {
            session.History.Add(folder);
            session.Index = 0;
        }

        if (_workspace.Active is BrowserTabState activeBrowser)
            _workspace.ReplaceTab(activeBrowser with
            {
                Navigation = new BrowserNavigationState(session.History.ToArray(), session.Index)
            });

        BrowserAddressBox.Text = folder;
        RefreshManagedBrowser(folder);
        BrowserBackButton.IsEnabled = session.Index > 0 || (_tabForwardImageTargets.TryGetValue(id, out var backTarget) && File.Exists(backTarget));
        BrowserForwardButton.IsEnabled = session.Index >= 0 && session.Index < session.History.Count - 1;
        BrowserUpButton.IsEnabled = Directory.GetParent(folder) is not null;
        Title = $"Glide 4.1.4 — Explorer — {folder}";
        RebuildTabStrip();
        SelectBrowserHighlight(id);
        UpdateTabNavigationButtons();
        _diagnostics.Write("browser", "navigate", new { folder, addHistory, historyIndex = session.Index, historyCount = session.History.Count });
    }

    private void NativeExplorerFolderNavigated(string folder)
    {
        // IExplorerBrowser has its own in-surface navigation (double-click folder, breadcrumbs, etc.).
        // Keep Glide's typed BrowserTabState/history/address synchronized without navigating the host
        // again, otherwise a Shell navigation event would recursively issue another BrowseToIDList.
        if (!OperatingSystem.IsWindows() || _workspace.Active is not BrowserTabState browser || !Directory.Exists(folder)) return;
        folder = Path.GetFullPath(folder);
        _settings.LastNewExplorerTabDirectory = folder;
        SaveSettingsAndPublish();
        if (string.Equals(browser.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;

        _workspace.ReplaceActiveBrowserFolder(folder);
        if (!_browserSessions.TryGetValue(browser.Id, out var session)) _browserSessions[browser.Id] = session = new BrowserSession();
        if (session.Index >= 0 && session.Index < session.History.Count - 1)
            session.History.RemoveRange(session.Index + 1, session.History.Count - session.Index - 1);
        if (session.History.Count == 0 || !string.Equals(session.History[^1], folder, StringComparison.OrdinalIgnoreCase))
        {
            session.History.Add(folder);
            session.Index = session.History.Count - 1;
        }

        if (_workspace.Active is BrowserTabState activeBrowser)
            _workspace.ReplaceTab(activeBrowser with
            {
                Navigation = new BrowserNavigationState(session.History.ToArray(), session.Index)
            });

        BrowserAddressBox.Text = folder;
        BrowserBackButton.IsEnabled = session.Index > 0 || (_tabForwardImageTargets.TryGetValue(browser.Id, out var nativeBackTarget) && File.Exists(nativeBackTarget));
        BrowserForwardButton.IsEnabled = session.Index >= 0 && session.Index < session.History.Count - 1;
        BrowserUpButton.IsEnabled = Directory.GetParent(folder) is not null;
        Title = $"Glide 4.1.4 — Explorer — {folder}";
        RebuildTabStrip();
        SelectBrowserHighlight(browser.Id);
        UpdateTabNavigationButtons();
        _diagnostics.Write("browser", "native_navigation", new { folder, historyIndex = session.Index, historyCount = session.History.Count });
    }

    private async void NativeExplorerFileActivated(string path)
    {
        if (!File.Exists(path) || !ImageNavigator.IsSupported(path)) return;
        await OpenAsImageTabAsync(path);
    }

    private void CancelBrowserThumbnailWork()
    {
        _browserThumbnailCts?.Cancel();
        _browserThumbnailCts?.Dispose();
        _browserThumbnailCts = null;
        CancelAllBrowserRealizations();
    }

    private async void RefreshManagedBrowser(string folder)
    {
        _browserThumbnailCts?.Cancel();
        _browserThumbnailCts?.Dispose();
        _browserThumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token);
        var token = _browserThumbnailCts.Token;
        var generation = Interlocked.Increment(ref _browserGeneration);
        var activeTabId = (_workspace.Active as BrowserTabState)?.Id ?? Guid.Empty;
        CancelAllBrowserRealizations();
        _browserSelection.Clear();
        _browserSelectionAnchor = -1;
        _browserCurrentIndex = -1;
        _browserEntries.Clear();
        ApplyManagedExplorerTheme();
        BrowserRepeater.ItemsSource = null;
        try
        {
            // Enumeration/sorting is the only O(N) folder operation. UI controls, context menus,
            // tooltips and thumbnail decoders are created strictly by ItemsRepeater realization.
            var entries = await Task.Run(() => EnumerateBrowserEntries(folder, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _browserGeneration) ||
                _workspace.Active is not BrowserTabState active || active.Id != activeTabId ||
                !string.Equals(active.Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
            _browserEntries = entries;
            BrowserRepeater.ItemsSource = _browserEntries;
            ConfigureBrowserRepeaterLayout();
            SelectBrowserHighlight(active.Id);
            _diagnostics.Write("browser", "managed_population_complete", new { folder, count = entries.Count, virtualized = true, generation });
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private sealed record BrowserEntry(string Path, bool Directory);

    private static List<BrowserEntry> EnumerateBrowserEntries(string folder, CancellationToken token)
    {
        var directories = new List<string>();
        var files = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            token.ThrowIfCancellationRequested();
            directories.Add(directory);
        }
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            files.Add(file);
        }
        directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        var entries = new List<BrowserEntry>(directories.Count + files.Count);
        entries.AddRange(directories.Select(path => new BrowserEntry(path, true)));
        entries.AddRange(files.Select(path => new BrowserEntry(path, false)));
        return entries;
    }

    private ListBoxItem CreateBrowserItem(BrowserEntry entry)
    {
        var path = entry.Path;
        var directory = entry.Directory;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_browserThumbnailCts?.Token ?? CancellationToken.None);
        var token = lifetime.Token;
        var compact = _browserViewMode == 2;
        var tileWidth = compact ? 250d : _browserViewMode == 1 ? 126d : 176d;
        var tileHeight = compact ? 42d : _browserViewMode == 1 ? 146d : 188d;
        var previewWidth = _browserViewMode == 1 ? 104 : 152;
        var previewHeight = _browserViewMode == 1 ? 98 : 136;

        Control content;
        Image? thumbnailTarget = null;
        Control? thumbnailFallback = null;
        if (compact)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*"), Margin = new Thickness(2, 0) };
            var icon = new GlideIconView
            {
                Kind = directory ? "Browser" : "Image",
                Width = 18, Height = 18, StrokeWidth = 1.5,
                IconBrush = BrowserBrush("muted"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var text = new TextBlock { Text = Path.GetFileName(path), FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            grid.Children.Add(icon); grid.Children.Add(text);
            content = grid;
        }
        else
        {
            var panel = new StackPanel { Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var previewBorder = new Border
            {
                Width = previewWidth,
                Height = previewHeight,
                CornerRadius = new CornerRadius(6),
                Background = BrowserBrush("surface"),
                BorderBrush = BrowserBrush("border"),
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };
            if (directory)
            {
                previewBorder.Child = new GlideIconView
                {
                    Kind = "Browser", Width = 48, Height = 48, StrokeWidth = 1.3,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IconBrush = BrowserBrush("muted")
                };
            }
            else
            {
                // The browser never equates "thumbnail unavailable" with "file unsupported". Core
                // raster formats use Avalonia's lightweight thumbnail path; provider/specialist formats
                // retain an honest image icon and still open through the authoritative viewer backend.
                var fallbackIcon = new GlideIconView
                {
                    Kind = "Image", Width = 42, Height = 42, StrokeWidth = 1.2,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IconBrush = BrowserBrush("muted")
                };
                if (ImageNavigator.IsSupported(path) && CanUseBrowserThumbnailDecoder(path))
                {
                    var previewStack = new Grid();
                    thumbnailTarget = new Image
                    {
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                    };
                    previewStack.Children.Add(fallbackIcon);
                    previewStack.Children.Add(thumbnailTarget);
                    previewBorder.Child = previewStack;
                    thumbnailFallback = fallbackIcon;
                }
                else
                {
                    previewBorder.Child = fallbackIcon;
                }
            }
            panel.Children.Add(previewBorder);
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(path), FontSize = 12.5, Width = previewWidth,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            });
            content = panel;
        }

        var item = new ListBoxItem
        {
            Tag = path,
            IsSelected = _browserSelection.Contains(path),
            Content = content,
            Width = tileWidth,
            Height = tileHeight,
            Margin = new Thickness(3),
            Padding = compact ? new Thickness(7, 4) : new Thickness(7),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Center
        };
        item.Classes.Add("browserItem");
        _browserRealizationCts[item] = lifetime;
        item.PointerPressed += BrowserItemPointerPressed;
        item.DoubleTapped += BrowserItemDoubleTapped;
        item.ContextMenu = BuildBrowserItemContextMenu(path, directory);
        // Expensive file metadata is loaded off the UI thread only for realized tiles.
        ToolTip.SetTip(item, path);
        ToolTip.SetShowDelay(item, 500);
        _ = LoadBrowserMetadataTooltipAsync(path, directory, item, token);

        if (thumbnailTarget is not null)
            _ = LoadBrowserThumbnailAsync(path, thumbnailTarget, thumbnailFallback, previewWidth, token);
        return item;
    }

    private void ConfigureBrowserRepeaterLayout()
    {
        var compact = _browserViewMode == 2;
        BrowserRepeaterLayout.MinItemWidth = compact ? 250 : _browserViewMode == 1 ? 126 : 176;
        BrowserRepeaterLayout.MinItemHeight = compact ? 42 : _browserViewMode == 1 ? 146 : 188;
    }

    private double BrowserItemStrideWidth() => (_browserViewMode == 2 ? 250d : _browserViewMode == 1 ? 126d : 176d) + 6d;

    private int BrowserEstimatedColumnCount()
    {
        var width = Math.Max(1, BrowserScrollViewer.Bounds.Width - 20);
        return Math.Max(1, (int)(width / BrowserItemStrideWidth()));
    }

    private void SelectBrowserIndex(int index, bool extend, bool toggle, bool bringIntoView)
    {
        if ((uint)index >= (uint)_browserEntries.Count) return;
        BrowserSelectionPolicy.Apply(_browserEntries.Count, i => _browserEntries[i].Path, _browserSelection,
            ref _browserSelectionAnchor, ref _browserCurrentIndex, index, extend, toggle);
        UpdateRealizedBrowserSelection();
        if (bringIntoView)
        {
            try { BrowserRepeater.GetOrCreateElement(index).BringIntoView(); } catch { }
        }
    }

    private void UpdateRealizedBrowserSelection()
    {
        foreach (var item in BrowserRepeater.GetVisualDescendants().OfType<ListBoxItem>())
            item.IsSelected = item.Tag is string path && _browserSelection.Contains(path);
    }

    private void BrowserRepeaterElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        // FuncDataTemplate is configured without recycling; ElementPrepared still re-applies model
        // selection after realization so off-screen selection cannot depend on stale visual state.
        if (e.Element is not ListBoxItem item || (uint)e.Index >= (uint)_browserEntries.Count) return;
        var path = _browserEntries[e.Index].Path;
        item.IsSelected = _browserSelection.Contains(path);
    }

    private void BrowserRepeaterElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (_browserRealizationCts.Remove(e.Element, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        foreach (var image in e.Element.GetVisualDescendants().OfType<Image>())
        {
            if (image.Source is IDisposable disposable) disposable.Dispose();
            image.Source = null;
        }
    }

    private void CancelAllBrowserRealizations()
    {
        foreach (var cts in _browserRealizationCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _browserRealizationCts.Clear();
        if (_browserSurface is null) return;
        foreach (var image in _browserSurface.Repeater.GetVisualDescendants().OfType<Image>())
        {
            if (image.Source is IDisposable disposable) disposable.Dispose();
            image.Source = null;
        }
    }

    private static bool CanUseBrowserThumbnailDecoder(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private async Task LoadBrowserMetadataTooltipAsync(string path, bool directory, Control target, CancellationToken token)
    {
        if (directory) return;
        try
        {
            var tip = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                return $"{path}\n{info.Length / 1024d / 1024d:F2} MB\nModified {info.LastWriteTime:g}";
            }, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) ToolTip.SetTip(target, tip);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task LoadBrowserThumbnailAsync(string path, Image target, Control? fallback, int width, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _browserThumbnailGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            // Thumbnail work is deliberately independent from the foreground image pipeline: opening
            // a browser tab must never steal the decode generation or cancel an image-view request.
            var bitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.SequentialScan);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.LowQuality);
            }, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) { bitmap.Dispose(); return; }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) { bitmap.Dispose(); return; }
                if (target.Source is IDisposable previous) previous.Dispose();
                target.Source = bitmap;
                if (fallback is not null) fallback.IsVisible = false;
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { if (entered) _browserThumbnailGate.Release(); }
    }

    private ContextMenu BuildBrowserItemContextMenu(string path, bool directory)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        var open = new MenuItem { Header = directory ? "Open folder" : "Open" };
        open.Click += async (_, _) =>
        {
            if (directory && Directory.Exists(path)) NavigateBrowserTo(path, addHistory: true);
            else if (File.Exists(path) && ImageNavigator.IsSupported(path)) await OpenAsImageTabAsync(path);
            else OpenWithWindowsShell(path);
        };
        items.Add(open);
        if (OperatingSystem.IsWindows())
        {
            var reveal = new MenuItem { Header = "Show in Windows Explorer" };
            reveal.Click += (_, _) => RevealInWindowsExplorer(path, directory);
            items.Add(reveal);
        }
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(path);
        };
        items.Add(copy);
        var delete = new MenuItem { Header = "Delete to Recycle Bin" };
        delete.Click += (_, _) => DeleteBrowserPath(path, directory);
        items.Add(delete);
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return menu;
    }

    private static void OpenWithWindowsShell(string path)
    {
        if (!OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path))) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private static void RevealInWindowsExplorer(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var args = directory ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch { }
    }

    private bool DeleteBrowserPath(string path, bool directory, bool refresh = true)
    {
        try
        {
            if (directory && Directory.Exists(path))
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else return false;
            if (refresh && _workspace.Active is BrowserTabState browser) RefreshManagedBrowser(browser.Folder);
            return true;
        }
        catch (Exception ex)
        {
            _diagnostics.Write("browser", "delete_failed", new { path, error = ex.GetType().Name, ex.Message });
            return false;
        }
    }

    private void BrowserViewModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Avalonia may raise SelectionChanged while InitializeComponent is still assigning x:Name
        // fields. Returning here prevents a startup-time NullReferenceException before the window can
        // ever be shown. Once construction completes, subsequent user changes run normally.
        if (!_browserUiReady || sender is not ComboBox combo || combo.SelectedIndex < 0) return;
        _browserViewMode = combo.SelectedIndex;
        ConfigureBrowserRepeaterLayout();
        BrowserRepeater.ItemsSource = null;
        BrowserRepeater.ItemsSource = _browserEntries;
    }

    private void BrowserWindowsExplorerClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is BrowserTabState browser) RevealInWindowsExplorer(browser.Folder, directory: true);
    }

    private async void BrowserListKeyDown(object? sender, KeyEventArgs e)
    {
        var selected = _browserSelection.ToArray();
        if (e.Key == Key.Enter && selected.Length == 1)
        {
            var openPath = selected[0];
            if (Directory.Exists(openPath)) NavigateBrowserTo(openPath, addHistory: true);
            else if (File.Exists(openPath) && ImageNavigator.IsSupported(openPath)) await OpenAsImageTabAsync(openPath);
            else OpenWithWindowsShell(openPath);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            var changed = false;
            foreach (var path in selected)
                changed |= DeleteBrowserPath(path, Directory.Exists(path), refresh: false);
            if (changed && _workspace.Active is BrowserTabState browser) RefreshManagedBrowser(browser.Folder);
            e.Handled = selected.Length > 0;
            return;
        }
        if (_browserEntries.Count == 0) return;
        var current = _browserCurrentIndex >= 0 ? _browserCurrentIndex : _browserSelectionAnchor >= 0 ? _browserSelectionAnchor : 0;
        var next = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(_browserEntries.Count - 1, current + 1),
            Key.Up => Math.Max(0, current - BrowserEstimatedColumnCount()),
            Key.Down => Math.Min(_browserEntries.Count - 1, current + BrowserEstimatedColumnCount()),
            Key.Home => 0,
            Key.End => _browserEntries.Count - 1,
            _ => -1
        };
        if (next < 0) return;
        SelectBrowserIndex(next, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift), toggle: false, bringIntoView: true);
        e.Handled = true;
    }

    private void BrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBoxItem { Tag: string path }) return;
        var index = _browserEntries.FindIndex(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        SelectBrowserIndex(index,
            extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            toggle: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            bringIntoView: false);
        (sender as Control)?.Focus();
    }

    private async void BrowserItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBoxItem { Tag: string path }) return;
        if (Directory.Exists(path)) NavigateBrowserTo(path, addHistory: true);
        else if (File.Exists(path) && ImageNavigator.IsSupported(path)) await OpenAsImageTabAsync(path);
        else if (File.Exists(path)) OpenWithWindowsShell(path);
        e.Handled = true;
    }

    private async void BrowserBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState browser || !_browserSessions.TryGetValue(browser.Id, out var session)) return;
        if (session.Index > 0)
        {
            session.Index--;
            NavigateBrowserTo(session.History[session.Index], addHistory: false);
            return;
        }

        if (_tabForwardImageTargets.TryGetValue(browser.Id, out var imagePath) &&
            File.Exists(imagePath) && ImageNavigator.IsSupported(imagePath))
        {
            _workspace.ReplaceTab(new ImageTabState(browser.Id, imagePath));
            _tabForwardImageTargets.Remove(browser.Id);
            _tabForwardFolderTargets.Remove(browser.Id);
            _browserHighlightTargets.Remove(browser.Id);
            _browserSessions.Remove(browser.Id);
            await ActivateWorkspaceAsync();
            UpdateTabNavigationButtons();
            _diagnostics.Write("browser", "back_to_image", new { path = imagePath, tab = browser.Id });
        }
    }

    private void BrowserForwardClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState browser || !_browserSessions.TryGetValue(browser.Id, out var session) || session.Index >= session.History.Count - 1) return;
        session.Index++;
        NavigateBrowserTo(session.History[session.Index], addHistory: false);
    }

    private void BrowserUpClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState browser) return;
        var parent = Directory.GetParent(browser.Folder)?.FullName;
        if (parent is not null) NavigateBrowserTo(parent, addHistory: true);
    }

    private void BrowserRefreshClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Active is BrowserTabState browser) NavigateBrowserTo(browser.Folder, addHistory: false);
    }

    private void BrowserAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var folder = BrowserAddressBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) NavigateBrowserTo(folder, addHistory: true);
        e.Handled = true;
    }

    private async void PreviousClicked(object? sender, RoutedEventArgs e) => await ExecuteCommandAsync(GlideCommand.PreviousImage);
    private async void NextClicked(object? sender, RoutedEventArgs e) => await ExecuteCommandAsync(GlideCommand.NextImage);
    private async void FirstClicked(object? sender, RoutedEventArgs e) => await ExecuteCommandAsync(GlideCommand.FirstImage);
    private async void LastClicked(object? sender, RoutedEventArgs e) => await ExecuteCommandAsync(GlideCommand.LastImage);

    private Task NavigateEdgeAsync(int direction) => RunSerializedNavigationAsync(async () =>
    {
        if (_navigator.Count == 0) return;

        StopHeldNavigation(settle: false, reason: direction < 0 ? "first_image" : "last_image");
        var oldIndex = _navigator.Index;
        var oldPath = _navigator.Current;
        var navigationEpoch = _navigationCommandEpoch;
        var workspaceEpoch = Volatile.Read(ref _workspaceEpoch);
        var targetPath = direction < 0 ? _navigator.First() : _navigator.Last();
        var targetIndex = _navigator.Index;
        _diagnostics.Write("navigation", "edge_navigation_requested", new
        {
            direction, oldIndex, targetIndex, targetPath
        });
        Interlocked.Increment(ref _navigationCommandEpoch);
        _loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);

        // Edge navigation is ordinary navigation. Let the normal bounded preview become visible
        // first; the existing refinement path can promote the full frame afterwards.
        var outcome = await PresentCurrentAsync();
        _diagnostics.Write("navigation", "edge_navigation_presented", new
        {
            outcome = outcome.Status.ToString(), resultingIndex = _navigator.Index,
            request = outcome.Request.ImageRequestId
        });
        if (outcome.Accepted) return;

        // This command owns the serialization gate, but still verify the state before restoring it
        // so a future asynchronous caller cannot overwrite a newer workspace/navigation command.
        var canRollback = navigationEpoch + 1 == _navigationCommandEpoch &&
            workspaceEpoch == Volatile.Read(ref _workspaceEpoch) &&
            _navigator.Index == targetIndex &&
            string.Equals(_navigator.Current, targetPath, StringComparison.OrdinalIgnoreCase);
        if (!canRollback || oldPath is null) return;

        _navigator.SelectIndex(oldIndex);
        _currentPath = oldPath;
        _loader.SetActivePath(oldPath);
        Interlocked.Increment(ref _imageRequestId);
        _loader.InvalidatePending();
        UpdateStatusStats();
        _diagnostics.Write("navigation", "edge_navigation_rollback", new
        {
            oldIndex, failedTargetIndex = targetIndex, outcome = outcome.Status.ToString()
        });
    });
    private void FitClicked(object? sender, RoutedEventArgs e) { _selectionZoomDemandPath = null; Viewport.Fit(); }
    private void FitWidthClicked(object? sender, RoutedEventArgs e) { _selectionZoomDemandPath = null; Viewport.FitWidth(); }
    private void FitHeightClicked(object? sender, RoutedEventArgs e) { _selectionZoomDemandPath = null; Viewport.FitHeight(); }
    private void ActualSizeClicked(object? sender, RoutedEventArgs e) { _selectionZoomDemandPath = null; Viewport.ActualSize(); }
    private void ZoomInClicked(object? sender, RoutedEventArgs e) => Viewport.ZoomBy(1.15);
    private void ZoomOutClicked(object? sender, RoutedEventArgs e) => Viewport.ZoomBy(1 / 1.15);
    private async void SlideshowClicked(object? sender, RoutedEventArgs e) => await ToggleSlideshowAsync();
    private void ReliableStatusSurfaceButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = (e.Source as Visual)?.FindAncestorOfType<Button>() ?? (e.Source as Button);
        if (button is not null)
            ReliableCommandButtonPressed(button, e);
    }

    private async void ReliableCommandButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        _diagnostics.Write("input", "reliable_button_press", new { button = button.Name ?? "<unnamed>", state = WindowState.ToString() });
        if (ReferenceEquals(button, StatusPreviousButton)) await ExecuteCommandAsync(GlideCommand.PreviousImage);
        else if (ReferenceEquals(button, StatusNextButton)) await ExecuteCommandAsync(GlideCommand.NextImage);
        else if (ReferenceEquals(button, StatusFirstButton)) await ExecuteCommandAsync(GlideCommand.FirstImage);
        else if (ReferenceEquals(button, StatusLastButton)) await ExecuteCommandAsync(GlideCommand.LastImage);
        else if (ReferenceEquals(button, SlideshowButton)) await ToggleSlideshowAsync();
        else if (ReferenceEquals(button, SlideshowStopButton)) SlideshowStopClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusZoomOutButton)) Viewport.ZoomBy(1 / 1.15);
        else if (ReferenceEquals(button, StatusZoomInButton)) Viewport.ZoomBy(1.15);
        else if (ReferenceEquals(button, StatusFitWidthButton)) Viewport.FitWidth();
        else if (ReferenceEquals(button, StatusFitHeightButton)) Viewport.FitHeight();
        else if (ReferenceEquals(button, StatusInfoButton)) InfoClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusOptionsButton) || (_homeSurface is not null && ReferenceEquals(button, _homeSurface.OptionsButton))) SettingsClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusCollapseButton) || (_homeSurface is not null && ReferenceEquals(button, _homeSurface.StatusCollapseButton))) CollapseStatusClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, StatusCloseButton)) CloseStatusClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, OverlayAddButton)) { _diagnostics.Write("overlay", "status_command", new { command = "add" }); OverlayAddClicked(button, new RoutedEventArgs()); }
        else if (ReferenceEquals(button, OverlayLoadButton)) { _diagnostics.Write("overlay", "status_command", new { command = "load" }); OverlayLoadClicked(button, new RoutedEventArgs()); }
        else if (ReferenceEquals(button, OverlaySaveButton)) { if (_overlays?.HasOverlays == true) { _diagnostics.Write("overlay", "status_command", new { command = "save" }); OverlaySaveClicked(button, new RoutedEventArgs()); } }
        else if (ReferenceEquals(button, OverlayClearButton)) { if (_overlays?.HasOverlays == true) { _diagnostics.Write("overlay", "status_command", new { command = "clear" }); OverlayClearClicked(button, new RoutedEventArgs()); } }
        else if (ReferenceEquals(button, CaptionMinimizeButton)) CaptionMinimizeClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, CaptionMaximizeButton)) CaptionMaximizeClicked(button, new RoutedEventArgs());
        else if (ReferenceEquals(button, CaptionCloseButton)) CaptionCloseClicked(button, new RoutedEventArgs());
    }

    private void InfoClicked(object? sender, RoutedEventArgs e)
    {
        if (_wholeAppOverlayMode) { ImageInfoOverlay.IsVisible = false; return; }
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        if (ImageInfoOverlay.IsVisible)
        {
            ImageInfoOverlay.IsVisible = false;
            _diagnostics.Write("image_info", "overlay_toggled", new { visible = false, path = _currentPath });
            return;
        }
        UpdateImageInfoOverlayText();
        ImageInfoOverlay.IsVisible = true;
        _diagnostics.Write("image_info", "overlay_toggled", new { visible = true, path = _currentPath });
    }

    private void UpdateImageInfoOverlayText()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var path = _currentPath;
        var created = "";
        var modified = "";
        try
        {
            var info = new FileInfo(path);
            created = $"\nCreated: {info.CreationTime:g}";
            modified = $"\nModified: {info.LastWriteTime:g}";
        }
        catch { }
        var metadata = (_currentMetadata.HasExif || _currentMetadata.FrameCount is not null) ? "\n" + _currentMetadata.ToDisplayText() : "";
        var format = CodecCapabilityRegistry.GetLongestExtension(path).TrimStart('.').ToUpperInvariant();
        ImageInfoText.Text = $"{Path.GetFileName(path)}\n{path}\nFormat: {format}   Dimensions: {_currentPixelWidth} × {_currentPixelHeight}\nFile size: {FormatFileSize(_currentFileSize)}   Index: {Math.Max(0, _navigator.Index + 1)} / {_navigator.Count}\nZoom: {Viewport.ZoomPercent}%   Last decode: {_currentDecodeMs:F1} ms{created}{modified}{metadata}";
    }

    private async Task LoadMetadataAfterPaintAsync(string path, ImageRequestContext request, CancellationToken token)
    {
        ImageMetadata metadata;
        try
        {
            metadata = await ImageMetadataService.ReadAsync(_decoderBackend, path, token);
        }
        catch (OperationCanceledException) { return; }
        if (!IsRequestCurrent(request, requirePresented: true) ||
            !string.Equals(path, request.Path, StringComparison.OrdinalIgnoreCase)) return;
        _currentMetadata = metadata;
        if (ImageInfoOverlay.IsVisible) UpdateImageInfoOverlayText();
        _diagnostics.Write("metadata", "loaded", new { path, request = request.ImageRequestId, exif = metadata.HasExif, orientation = metadata.Orientation });
    }

    private void CollapseStatusClicked(object? sender, RoutedEventArgs e)
    {
        _statusCollapsed = true;
        ApplyStatusVisibility();
        _diagnostics.Write("status", "collapsed", new { surface = HomeHost.IsVisible ? "home" : "viewer" });
    }

    private void RestoreStatusClicked(object? sender, RoutedEventArgs e)
    {
        _settings.ShowStatusSurface = true;
        _statusCollapsed = false;
        _statusSessionClosed = false;
        SaveSettingsAndPublish();
        ApplyStatusVisibility();
        _diagnostics.Write("status", "expanded", new { surface = HomeHost.IsVisible ? "home" : "viewer" });
    }

    private void CloseStatusClicked(object? sender, RoutedEventArgs e)
    {
        // X is a real close, not a temporary hide. Persist the status surface off and do not leave
        // a restore affordance or hover target behind. Restore explicitly via canvas View > Show status bar.
        _settings.ShowStatusSurface = false;
        _statusCollapsed = false;
        _statusSessionClosed = false;
        SaveSettingsAndPublish();
        ApplyStatusVisibility();
        _diagnostics.Write("status", "closed", new { persisted = true });
    }

    private void AutoDismissTransientPanels(object? sender, PointerPressedEventArgs e)
    {
        if (!TransparencyPanel.IsVisible) return;
        if (IsWithinControl(e.Source, TransparencyPanel)) return;
        if (_titleBarButtons.TryGetValue("window.transparency", out var transparencyButton) && IsWithinControl(e.Source, transparencyButton)) return;
        TransparencyPanel.IsVisible = false;
        _diagnostics.Write("window", "transparency_panel_auto_dismissed", new { reason = "outside_pointer_press" });
    }

    private static bool IsWithinControl(object? source, Control ancestor)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void TransparencyClicked(object? sender, RoutedEventArgs e)
    {
        TransparencyPanel.IsVisible = !TransparencyPanel.IsVisible;
        TransparencySlider.Value = Math.Round(_sessionOpacity * 100);
        TransparencyValueText.Text = $"{(int)Math.Round(_sessionOpacity * 100)}%";
        if (TransparencyPanel.IsVisible) TransparencySlider.Focus();
        _diagnostics.Write("window", "transparency_panel_toggled", new { visible = TransparencyPanel.IsVisible, percent = (int)Math.Round(_sessionOpacity * 100) });
    }

    private void TransparencyResetClicked(object? sender, RoutedEventArgs e)
    {
        TransparencySlider.Value = 100;
        TransparencyPanel.IsVisible = false;
        _diagnostics.Write("window", "transparency_reset", new { percent = 100 });
    }

    private void ApplyStartupWholeAppOverlayPreference()
    {
        // This exact flag is part of the compact first-frame policy, so startup Overlay can be
        // decided before full graph hydration without guessing from temporary defaults.
        if (_startupWholeAppOverlayPreferenceApplied) return;
        _startupWholeAppOverlayPreferenceApplied = true;
        if (_settings.AlwaysStartWholeAppOverlayMode)
            EnterWholeAppOverlayMode("startup_preference");
    }

    private Grid EnsureWholeAppOverlayControls()
    {
        if (_wholeAppOverlayControlsHost is { } existing) return existing;

        // Use the exact same visual chrome as per-image Window-in-Window overlays.  Host ownership is
        // intentionally different (top-level window vs canvas item), but close/opacity/resize geometry
        // and visual language must not drift.  The transparent hit targets below adapt those shared
        // regions to top-level window actions without putting this UI on the normal cold-image path.
        var chrome = new LegacyOverlayChrome
        {
            ChromeVisible = true,
            OpacityValue = _sessionOpacity,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ShowResizeGrip = false
        };

        var closeHit = new Button
        {
            Width = 27, Height = 27,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 7, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Opacity = 0.01, Focusable = false, ZIndex = 2
        };
        ToolTip.SetTip(closeHit, "Close Glide");
        closeHit.Click += (_, _) =>
        {
            // Whole-app Overlay X is a real close affordance, not a mode toggle.
            // If this process owns multiple Glide windows, closing Overlay exits the entire
            // current Glide application/session exactly as requested.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        };

        var opacityHit = new Slider
        {
            Minimum = OverlayChromePolicy.WholeWindowMinimumOpacity,
            Maximum = 1.0,
            Value = _sessionOpacity,
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Width = 22,
            Height = 130,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.01, Focusable = false, ZIndex = 2
        };
        ToolTip.SetTip(opacityHit, "Overlay opacity");
        opacityHit.PropertyChanged += (_, args) =>
        {
            if (args.Property != RangeBase.ValueProperty || !_wholeAppOverlayMode) return;
            var value = OverlayChromePolicy.ClampWholeWindowOpacity(opacityHit.Value);
            if (Math.Abs(TransparencySlider.Value - value * 100.0) > 0.01)
                TransparencySlider.Value = value * 100.0;
        };

        Button ZoomHit(string tip, bool zoomIn)
        {
            var button = new Button
            {
                Width = 30, Height = 30,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = zoomIn ? new Thickness(34, 0, 0, 8) : new Thickness(0, 0, 34, 8),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Opacity = 0.01, Focusable = false, ZIndex = 2
            };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Viewport.ZoomBy(zoomIn ? 1.15 : 1.0 / 1.15);
            return button;
        }
        var zoomOutHit = ZoomHit("Zoom out", false);
        var zoomInHit = ZoomHit("Zoom in", true);
        var topmostHit = new Button
        {
            Width = 30, Height = 30,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(118, 0, 0, 8),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Opacity = 0.01, Focusable = false, ZIndex = 2
        };
        ToolTip.SetTip(topmostHit, "Always on top");
        topmostHit.Click += (_, _) =>
        {
            _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
            Topmost = _settings.AlwaysOnTop;
            chrome.TopmostActive = _settings.AlwaysOnTop;
            chrome.InvalidateVisual();
            SaveSettingsAndPublish();
        };

        // No synthetic bottom-right resize grip in whole-app Overlay mode.  The native
        // frame hit-test already resizes from the borders/corners and is substantially more stable
        // than mutating Width/Height from Thumb.DragDelta while the compositor is resizing.

        // Keep the top frame as an explicit drag affordance. In default Overlay interaction mode
        // the entire non-interactive body also drags via the MainRoot tunnel handler; when the user
        // selects Windowed interactions, only this frame band moves the top-level window.
        var dragBand = new Border
        {
            Height = OverlayChromePolicy.FrameDragBandDip,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 38, 0),
            Background = Brushes.Transparent,
            ZIndex = 1
        };
        ToolTip.SetTip(dragBand, "Drag to move the Overlay window");
        dragBand.PointerPressed += WholeAppOverlayChromePointerPressed;

        var host = new Grid
        {
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            ZIndex = 1600
        };
        host.Children.Add(chrome);
        host.Children.Add(dragBand);
        host.Children.Add(closeHit);
        host.Children.Add(opacityHit);
        host.Children.Add(zoomOutHit);
        host.Children.Add(zoomInHit);
        host.Children.Add(topmostHit);
        host.SizeChanged += (_, _) =>
        {
            var slider = OverlayChromePolicy.SliderRect(host.Bounds.Size);
            opacityHit.Height = slider.Height;
        };
        WorkspaceLayer.Children.Add(host);

        chrome.TopmostActive = _settings.AlwaysOnTop;
        _wholeAppOverlayChromeVisual = chrome;
        _wholeAppOverlayOpacityHitTarget = opacityHit;
        _wholeAppOverlayResizeGrip = null;
        _wholeAppOverlayControlsHost = host;
        return host;
    }

    private void WholeAppOverlayBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || _settings.WholeAppOverlayUseWindowedInteractions) return;
        if (!physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        BeginNativeWindowDrag(e);
        e.Handled = true;
    }

    private void WholeAppOverlayChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_wholeAppOverlayMode || _nativeSizeMoveActive || !physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        BeginNativeWindowDrag(e);
        e.Handled = true;
    }

    private void EnterWholeAppOverlayMode(string reason = "context_menu")
    {
        if (_wholeAppOverlayMode) return;

        // Fullscreen has its own captured placement. Temporarily leave it so Overlay mode gets a
        // genuine movable window, then remember that fullscreen must be restored on exit.
        var restoreFullscreen = WindowState == WindowState.FullScreen;
        if (restoreFullscreen) ExitFullscreen(forceMaximized: false);

        var restoreState = WindowState;
        var restorePosition = Position;
        var restoreWidth = Width;
        var restoreHeight = Height;
        if (restoreState == WindowState.Maximized && _normalPlacementKnown)
        {
            restorePosition = _lastNormalWindowPosition;
            restoreWidth = _lastNormalWindowSize.Width;
            restoreHeight = _lastNormalWindowSize.Height;
        }

        _wholeAppOverlaySnapshot = new WholeAppOverlaySnapshot(
            restoreState, restorePosition, restoreWidth, restoreHeight, restoreFullscreen, _sessionOpacity);
        _wholeAppOverlayPreviousTransparencyFallback = TransparencyBackgroundFallback;
        _wholeAppOverlayMode = true;

        // Window-in-window mode should be a movable/resizable floating surface even if Glide had
        // previously been maximized. The previous state is restored exactly when the mode exits.
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            if (_normalPlacementKnown)
            {
                Position = _lastNormalWindowPosition;
                Width = Math.Max(MinWidth, _lastNormalWindowSize.Width);
                Height = Math.Max(MinHeight, _lastNormalWindowSize.Height);
            }
        }

        ApplySettingsVisuals();

        // Overlay is a genuinely transparent top-level surface: only image pixels/chrome render.
        // This preserves PNG alpha all the way to the Windows desktop and leaves letterbox/unused
        // aspect-ratio space completely see-through instead of painting Glide's viewport colour.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        Background = Brushes.Transparent;
        MainRoot.Background = Brushes.Transparent;
        WorkspaceLayer.Background = Brushes.Transparent;
        ImageView.Background = Brushes.Transparent;
        Viewport.SetBackgroundFillSuppressed(true);
        Topmost = _settings.AlwaysOnTop;
        ImageInfoOverlay.IsVisible = false;
        CornerCounterHost.IsVisible = false;
        var overlayControls = EnsureWholeAppOverlayControls();
        overlayControls.IsVisible = true;
        overlayControls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        TransparencyPanel.IsVisible = false;

        ApplyChromeLayoutForWindowState();
        ApplyStatusVisibility();
        UpdateViewportScrollbars();
        RefreshDynamicTooltips();
        _diagnostics.Write("window", "whole_app_overlay_entered", new
        {
            reason, restoreState = restoreState.ToString(), restoreFullscreen,
            transparency = ActualTransparencyLevel.ToString(), Position, Width, Height
        });
    }

    private void ExitWholeAppOverlayMode()
    {
        if (!_wholeAppOverlayMode) return;
        var snapshot = _wholeAppOverlaySnapshot;
        _wholeAppOverlayMode = false;
        _wholeAppOverlaySnapshot = null;

        if (_wholeAppOverlayControlsHost is { } overlayControls)
        {
            overlayControls.IsVisible = false;
            overlayControls.ContextMenu = null;
        }
        TransparencyPanel.IsVisible = false;
        Viewport.SetBackgroundFillSuppressed(false);
        // Restore normal UI opacity on the inner visual root only. The native top-level stays
        // transparent-capable permanently; this is what preserves per-pixel image alpha when
        // Overlay is entered again without recreating the HWND.
        if (Application.Current?.Resources["BrushWindow"] is IBrush normalWindowBrush)
            MainRoot.Background = normalWindowBrush;
        else
            MainRoot.Background = Brushes.Black;
        WorkspaceLayer.Background = null;
        ImageView.Background = null;
        // Keep the top-level permanently transparency-capable. The normal theme paints an opaque
        // background outside Overlay mode, so this has no visual effect in ordinary windows, while
        // re-entering Overlay never depends on recreating/reconfiguring an already-shown HWND.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        if (_wholeAppOverlayPreviousTransparencyFallback is { } fallback)
            TransparencyBackgroundFallback = fallback;
        _wholeAppOverlayPreviousTransparencyFallback = null;

        if (snapshot is not null)
        {
            _sessionOpacity = OverlayChromePolicy.ClampWholeWindowOpacity(snapshot.SessionOpacity);
            Opacity = _sessionOpacity;
            TransparencySlider.Value = _sessionOpacity * 100.0;
            WindowState = WindowState.Normal;
            if (snapshot.Width > 0 && snapshot.Height > 0)
            {
                Width = Math.Max(MinWidth, snapshot.Width);
                Height = Math.Max(MinHeight, snapshot.Height);
                Position = snapshot.Position;
            }
            if (snapshot.RestoreState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }

        ApplySettingsVisuals();
        if (snapshot?.RestoreFullscreen == true && WindowState != WindowState.FullScreen)
            EnterFullscreen();

        _diagnostics.Write("window", "whole_app_overlay_exited", new
        {
            restoredState = WindowState.ToString(), restoredFullscreen = snapshot?.RestoreFullscreen == true,
            Position, Width, Height
        });
    }

    private void UpdateWholeAppOverlayChromeHover(Point pointer)
    {
        if (!_wholeAppOverlayMode || !IsActive || _nativeSizeMoveActive) return;
        var revealEdge = OverlayChromePolicy.RevealEdge(RenderScaling);
        var controls = _wholeAppOverlayControlsHost;
        if (controls is null) return;
        if (!controls.IsVisible)
        {
            if (OverlayChromePolicy.IsNearFrameEdge(pointer, Bounds.Size, revealEdge)) controls.IsVisible = true;
            return;
        }
        if (OverlayChromePolicy.ShouldDismissChrome(pointer, Bounds.Size) &&
            !OverlayChromePolicy.IsNearFrameEdge(pointer, Bounds.Size, revealEdge)) controls.IsVisible = false;
    }

    private void ToggleAlwaysStartWholeAppOverlayMode()
    {
        _settings.AlwaysStartWholeAppOverlayMode = !_settings.AlwaysStartWholeAppOverlayMode;
        SaveSettingsAndPublish();
        if (_wholeAppOverlayMode && _wholeAppOverlayControlsHost is { } controls) controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_startup_preference", new { enabled = _settings.AlwaysStartWholeAppOverlayMode });
    }

    private void ToggleWholeAppOverlayInteractionMode()
    {
        _settings.WholeAppOverlayUseWindowedInteractions = !_settings.WholeAppOverlayUseWindowedInteractions;
        SaveSettingsAndPublish();
        ApplySettingsVisuals();
        if (_wholeAppOverlayControlsHost is { } controls)
            controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        _diagnostics.Write("window", "whole_app_overlay_interaction_mode", new
        {
            windowed = _settings.WholeAppOverlayUseWindowedInteractions,
            mode = _settings.WholeAppOverlayUseWindowedInteractions ? "windowed" : "overlay"
        });
    }

    private void AppendWholeAppOverlayMenuItems(List<MenuItem> items)
    {
        if (!_wholeAppOverlayMode)
        {
            var enter = new MenuItem { Header = "Enter Overlay (Window-in-Window) Mode" };
            enter.Click += (_, _) => EnterWholeAppOverlayMode();
            items.Add(enter);
            return;
        }

        var interactions = new MenuItem
        {
            Header = _settings.WholeAppOverlayUseWindowedInteractions
                ? "Interaction: Windowed mode  ✓"
                : "Interaction: Overlay mode  ✓"
        };
        interactions.Click += (_, _) => ToggleWholeAppOverlayInteractionMode();
        items.Add(interactions);
        items.Add(new MenuItem { Header = "—", IsEnabled = false });

        var always = new MenuItem
        {
            Header = _settings.AlwaysStartWholeAppOverlayMode
                ? "Always enter Glide in Overlay mode  ✓"
                : "Always enter Glide in Overlay mode"
        };
        always.Click += (_, _) => ToggleAlwaysStartWholeAppOverlayMode();
        items.Add(always);

        var exit = new MenuItem { Header = "Exit Overlay mode" };
        exit.Click += (_, _) => ExitWholeAppOverlayMode();
        items.Add(exit);
    }

    private ContextMenu BuildWholeAppOverlayOnlyContextMenu()
    {
        var items = new List<MenuItem>();
        var topmost = new MenuItem { Header = _settings.AlwaysOnTop ? "Always on top  ✓" : "Always on top" };
        topmost.Click += (_, _) => AlwaysOnTopClicked(null, new RoutedEventArgs());
        items.Add(topmost);
        items.Add(new MenuItem { Header = "—", IsEnabled = false });
        AppendWholeAppOverlayMenuItems(items);
        return new ContextMenu { ItemsSource = items };
    }

    private void WholeAppOverlayContextPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || ImageView.IsVisible) return;

        // Respect the more specific tab/browser/control context menus. Empty Home/Explorer/chrome
        // areas receive this tiny whole-app menu, making the mode discoverable throughout Glide.
        for (var current = e.Source as Control; current is not null && !ReferenceEquals(current, MainRoot); current = current.GetVisualParent() as Control)
            if (current.ContextMenu is not null) return;

        var target = e.Source as Control ?? MainRoot;
        BuildWholeAppOverlayOnlyContextMenu().Open(target);
        e.Handled = true;
    }

    private void AlwaysOnTopClicked(object? sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        SaveSettingsAndPublish();
        ApplySettingsVisuals();
        if (_wholeAppOverlayChromeVisual is { } overlayChrome)
        {
            overlayChrome.TopmostActive = _settings.AlwaysOnTop;
            overlayChrome.InvalidateVisual();
        }
    }

    private void RebuildTitleActionStrip()
    {
        if (TitleActionHost is null) return;
        TitleActionHost.Children.Clear();
        _titleBarButtons.Clear();

        var normalized = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons);
        _settings.TitleBarButtons = normalized.ToList();
        foreach (var id in normalized)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            Control icon;
            if (id == "settings")
            {
                // Use the current Windows Fluent Settings glyph rather than Glide's old hand-drawn
                // radial gear. It stays crisp at mixed DPI and visually matches modern Windows chrome.
                var settingsGlyph = new TextBlock
                {
                    Text = "\uE713",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 17,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                };
                settingsGlyph.Classes.Add("modernTitleSettingsGlyph");
                icon = settingsGlyph;
            }
            else
            {
                var vectorIcon = new GlideIconView
                {
                    Kind = definition.IconKind,
                    Width = 17,
                    Height = 17,
                    StrokeWidth = 1.65
                };
                vectorIcon.Classes.Add("toolbarGlyph");
                icon = vectorIcon;
            }
            var button = new Button
            {
                Width = 36,
                Height = 36,
                Padding = new Thickness(8),
                Tag = id,
                Content = icon,
                ContextMenu = BuildTitleActionContextMenu(id)
            };
            button.Classes.Add("glideIcon");
            if (id is "folder.previous" or "folder.next" or "image.previous" or "image.next" or "image.first" or "image.last")
                button.Classes.Add("nav");
            // Home is a safety-critical title-bar action. Execute it on the consumed client-area
            // pointer press instead of the ordinary Click route: in custom chrome a press/release
            // that escapes the Avalonia button can otherwise fall through to native caption hit
            // testing and be interpreted as a window/caption action. Home must only navigate.
            if (id == "home")
            {
                button.PointerPressed += async (_, e) =>
                {
                    var point = e.GetCurrentPoint(button);
                    if (!point.Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    // A Home gesture is never a close gesture. Keep a small cancellation window around
                    // the pointer sequence as a final guard against native/custom-caption races.
                    Volatile.Write(ref _suppressCloseUntilTick, Environment.TickCount64 + 750);
                    _diagnostics.Write("input", "title_home_pressed", new { id });
                    await ExecuteCommandAsync(GlideCommand.Home);
                };
            }
            else
            {
                button.Click += async (_, _) => await ExecuteTitleActionAsync(id, definition.Command);
            }
            TitleActionHost.Children.Add(button);
            _titleBarButtons[id] = button;
        }

        if (_titleBarButtons.TryGetValue("window.alwaysOnTop", out var topmostButton))
            SetActiveClass(topmostButton, _settings.AlwaysOnTop);
        RefreshTitleActionTooltips();
    }

    private async Task ExecuteTitleActionAsync(string id, GlideCommand command)
    {
        // The title-bar folder arrows intentionally retain their legacy "open first image" contract;
        // status-bar/keyboard folder navigation can continue to use the ordinary sibling-folder policy.
        if (id == "folder.previous") await TryNavigateSiblingFolderAsync(-1, forceFirstImage: true);
        else if (id == "folder.next") await TryNavigateSiblingFolderAsync(1, forceFirstImage: true);
        else await ExecuteCommandAsync(command);
    }

    private void RefreshTitleActionTooltips()
    {
        foreach (var (id, button) in _titleBarButtons)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            var label = id == "window.transparency"
                ? $"Window transparency: {(int)Math.Round(_sessionOpacity * 100)}%"
                : definition.Label;
            ToolTip.SetTip(button, $"[{label} ({ShortcutFor(definition.Command)})]");
            ToolTip.SetShowDelay(button, 650);
        }
    }

    private ContextMenu BuildTitleActionContextMenu(string id)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        var current = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons).ToList();
        var index = current.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        items.Add(Item("Move left", () => MoveTitleAction(id, -1), index > 0));
        items.Add(Item("Move right", () => MoveTitleAction(id, 1), index >= 0 && index < current.Count - 1));
        items.Add(Item("Remove from title bar", () => RemoveTitleAction(id), index >= 0));
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item("Customize title-bar buttons…", () => _ = ShowTitleBarCustomizerAsync()));
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return menu;
    }

    private void MoveTitleAction(string id, int delta)
    {
        var ids = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons).ToList();
        var index = ids.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var target = Math.Clamp(index + delta, 0, ids.Count - 1);
        if (target == index) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        _settings.TitleBarButtons = ids;
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }

    private void RemoveTitleAction(string id)
    {
        _settings.TitleBarButtons = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons)
            .Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase)).ToList();
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }

    private async Task ShowTitleBarCustomizerAsync()
    {
        var dialog = new TitleBarCustomizerWindow(_settings.TitleBarButtons)
        {
            Icon = Icon,
            Topmost = Topmost
        };
        var result = await dialog.ShowDialog<IReadOnlyList<string>?>(this);
        if (result is null) return;
        _settings.TitleBarButtons = TitleBarButtonCatalog.Normalize(result).ToList();
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }

    private async void SettingsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var window = new SettingsWindow(_settings, ApplySettingsFromDialog)
            {
                // Modal children of an always-on-top viewer must share the same z-order contract.
                // Otherwise Windows can bury Settings/confirmation dialogs behind the owner.
                Topmost = Topmost
            };
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            // Settings must never be able to take down the viewer process. The Settings constructor
            // is also guarded against XAML-time event reentrancy, but keep this owner boundary as the
            // final crash containment layer and preserve a diagnostic breadcrumb for the next agent.
            _diagnostics.Write("settings", "open_failed", new { error = ex.ToString() });
        }
    }

    private void SaveSettingsAndPublish()
    {
        // The compact first-frame projection is intentionally incomplete. Never serialize that
        // temporary graph over the user's hydrated settings merely because the window closed or a
        // command fired before background hydration completed. If hydration is already available,
        // merge any edits made since the compact baseline; otherwise defer persistence entirely.
        if (!_deferredStartupSettingsAdopted)
        {
            var hydrated = App.TryGetStartupSettings();
            if (hydrated is null)
            {
                _diagnostics.Write("settings", "save_deferred_until_hydrated", new { reason = "startup_settings_not_ready" });
                return;
            }
            _settings = SettingsStore.MergeHydratedPreservingEdits(_startupSettingsBaseline, _settings, hydrated);
            _deferredStartupSettingsAdopted = true;
        }

        SettingsStore.Save(_settings);
        App.PublishCurrentSettings(_settings);
        _startupSettingsBaseline = _settings.CloneState();
    }

    private void ApplySettingsFromDialog(GlideSettingsState state, bool commit)
    {
        _settings = state.CloneState();
        App.PublishCurrentSettings(_settings);
        ApplySettingsVisuals();
        _diagnostics.Write("settings", commit ? "committed" : "previewed", new { theme = _settings.ThemeChoice, accent = _settings.AccentChoice, glow = _settings.GlowChoice, status = _settings.ShowStatusSurface, tabs = _settings.TabsEnabled });
        if (commit)
        {
            SaveSettingsAndPublish();
            App.UpdateTrayIconState();
            if (!_settings.OverlayPersistLayoutBetweenSessions)
            {
                try
                {
                    var path = AutomaticOverlayLayoutPath();
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }
        }
    }

    private ImagePerformancePolicy BuildPerformancePolicy()
    {
        var profile = ImagePerformancePolicy.ForProfile(_settings.InitialImageQuality);
        var renderScale = Math.Max(1.0, RenderScaling);
        var viewportWidth = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Width) * renderScale);
        var viewportHeight = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Height) * renderScale);
        var previewBounds = _settings.AdaptiveFastPreview
            ? ImagePerformanceGovernor.ChoosePreviewBounds(_settings.RapidPreviewLongestSide, viewportWidth, viewportHeight)
            : (_settings.RapidPreviewLongestSide, _settings.RapidPreviewLongestSide);
        return (profile with
        {
            DecoderScaledFirstFrame = _settings.AdaptiveFastPreview,
            // Keep the user setting as the absolute cap while carrying the actual physical viewport
            // as a 2-D box. A portrait photograph in a wide window must not be decoded to the window
            // width merely because the old policy reduced every viewport to one "longest side" value.
            PreviewLongestSide = _settings.RapidPreviewLongestSide,
            PreviewMaxWidth = previewBounds.Item1,
            PreviewMaxHeight = previewBounds.Item2,
            ProgressiveColorFirstPreview = _settings.ProgressiveColorFirstPreview,
            BackgroundRefinement = _settings.BackgroundRefinement,
            RefinementDelayMs = _settings.AdaptivePreviewDelayMs,
            SequentialForegroundReads = _settings.SequentialForegroundReads,
            PredictivePrefetch = _settings.PredictivePrefetch,
            PrefetchDepth = _settings.PrefetchDepth,
            FullNeighbourPredecodeCount = 1,
            CompressedCacheItems = _settings.CacheItems,
            CompressedCacheMegabytes = _settings.CompressedCacheMegabytes,
            DecodedCacheMegabytes = _settings.DecodedCacheMegabytes
        }).Normalize();
    }

    private ImagePerformancePolicy BuildRapidBrowsePolicy()
    {
        var normal = BuildPerformancePolicy();
        var renderScale = Math.Max(1.0, RenderScaling);
        var viewportWidth = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Width) * renderScale);
        var viewportHeight = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Height) * renderScale);
        // Rapid browsing has its own user-visible quality tier. Balanced defaults to 1440 px,
        // deliberately higher than the old hard-coded 960 px frame. The visual performance profile
        // stages 960 / 1440 / 2048 px for Maximum speed / Balanced / Maximum quality, while this
        // setting remains independently editable after the profile is chosen.
        var rapidCap = Math.Clamp(_settings.RapidBrowsePreviewLongestSide, 640, 4096);
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(rapidCap, viewportWidth, viewportHeight);
        var interpolation = _settings.InitialImageQuality switch
        {
            "Maximum speed" => BitmapInterpolationMode.LowQuality,
            "Maximum quality" => BitmapInterpolationMode.HighQuality,
            _ => BitmapInterpolationMode.MediumQuality
        };
        return (normal with
        {
            DecoderScaledFirstFrame = true,
            PreviewLongestSide = rapidCap,
            PreviewMaxWidth = Math.Min(rapidCap, bounds.Item1),
            PreviewMaxHeight = Math.Min(rapidCap, bounds.Item2),
            PreviewInterpolation = interpolation,
            BackgroundRefinement = false,
            PredictivePrefetch = true,
            PrefetchDepth = Math.Max(4, Math.Min(8, normal.PrefetchDepth)),
            FullNeighbourPredecodeCount = 0
        }).Normalize();
    }

    private void ApplyEmbeddedExplorerTheme()
    {
        ApplyManagedExplorerTheme();
        if (NativeExplorerHost is null) return;
        var requested = _settings.ExplorerThemeChoice;
        var effective = string.Equals(requested, "Follow Glide theme", StringComparison.OrdinalIgnoreCase)
            ? _settings.ThemeChoice
            : requested;
        NativeExplorerHost.ApplyTheme(effective);
    }

    private SolidColorBrush BrowserBrush(string role)
    {
        var requested = _settings.ExplorerThemeChoice;
        var follow = string.Equals(requested, "Follow Glide theme", StringComparison.OrdinalIgnoreCase);
        var effective = follow ? _settings.ThemeChoice : requested;
        var dark = string.Equals(effective, "Dark", StringComparison.OrdinalIgnoreCase);
        var neutral = string.Equals(effective, "Neutral", StringComparison.OrdinalIgnoreCase);
        var value = role switch
        {
            "surface" => dark ? "#191D22" : neutral ? "#F1F3F5" : "#FFFFFF",
            "border" => dark ? "#343B43" : "#C9D0D6",
            "muted" => dark ? "#A9B3BC" : "#62707A",
            _ => dark ? "#EEF2F6" : "#1C2329"
        };
        return new SolidColorBrush(Color.Parse(value));
    }

    private void ApplyManagedExplorerTheme()
    {
        if (BrowserView is null) return;
        var requested = _settings.ExplorerThemeChoice;
        if (string.Equals(requested, "Follow Glide theme", StringComparison.OrdinalIgnoreCase))
        {
            BrowserView.Resources.Clear();
            return;
        }

        // These resources shadow only the Explorer subtree, so a Light/Neutral browser can coexist
        // with a Dark Glide window and vice versa without fighting the application-level palette.
        var dark = string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase);
        var neutral = string.Equals(requested, "Neutral", StringComparison.OrdinalIgnoreCase);
        var viewport = dark ? "#111417" : neutral ? "#E8EBEE" : "#F7F8FA";
        var surface = dark ? "#191D22" : neutral ? "#F1F3F5" : "#FFFFFF";
        var hover = dark ? "#252B31" : neutral ? "#DDE2E6" : "#EEF2F6";
        var text = dark ? "#EEF2F6" : "#1C2329";
        var muted = dark ? "#A9B3BC" : "#62707A";
        var border = dark ? "#343B43" : "#C9D0D6";
        var search = dark ? "#101317" : "#FFFFFF";
        BrowserView.Resources["BrushViewport"] = new SolidColorBrush(Color.Parse(viewport));
        BrowserView.Resources["BrushSurface"] = new SolidColorBrush(Color.Parse(surface));
        BrowserView.Resources["BrushSurfaceHover"] = new SolidColorBrush(Color.Parse(hover));
        BrowserView.Resources["BrushChromeButtonHover"] = new SolidColorBrush(Color.Parse(hover));
        BrowserView.Resources["BrushText"] = new SolidColorBrush(Color.Parse(text));
        BrowserView.Resources["BrushMuted"] = new SolidColorBrush(Color.Parse(muted));
        BrowserView.Resources["BrushBorderSoft"] = new SolidColorBrush(Color.Parse(border));
        BrowserView.Resources["BrushInputBorder"] = new SolidColorBrush(Color.Parse(border));
        BrowserView.Resources["BrushSearch"] = new SolidColorBrush(Color.Parse(search));
    }

    private void ApplySettingsVisuals(bool deferSecondaryChrome = false)
    {
        ApplyPalette(_settings.ThemeChoice, _settings.AccentChoice, _settings.GlowChoice, _settings.GlowIntensityPercent, _settings.CustomAccentHex, _settings.CustomGlowHex, _settings.MainBackgroundChoice, _settings.CustomMainBackgroundHex);
        Topmost = _settings.AlwaysOnTop;
        KeyboardNavigation.SetTabNavigation(this, _settings.EnableTabFocusNavigation ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        // Window.Background is a compositor contract, not the normal theme surface. Keep the
        // top-level transparent for the lifetime of the HWND and paint the ordinary application
        // background on MainRoot instead. This is required for true PNG/WebP alpha in Overlay.
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        if (_wholeAppOverlayMode)
            MainRoot.Background = Brushes.Transparent;
        else if (Application.Current?.Resources["BrushWindow"] is IBrush normalWindowBrush)
            MainRoot.Background = normalWindowBrush;
        if (_homeSurface is not null)
        {
            _homeSurface.TipsGrid.IsVisible = _settings.ShowHomeTips && !string.Equals(_settings.HomePageMode, "Recent pictures page", StringComparison.OrdinalIgnoreCase);
            ApplyWelcomeLayout();
            RefreshRecentHistoryHome();
        }
        TabStripHost.IsVisible = _settings.TabsEnabled;
        _workspace.ClosedHistoryLimit = Math.Clamp(_settings.ClosedTabHistoryLimit, 1, 100);

        _loader.Policy = BuildPerformancePolicy();
        _lastSurroundingPrefetchFolder = null;
        ApplyEmbeddedExplorerTheme();
        if (_overlays is { } overlays) ApplyOverlaySettingsToManager(overlays);
        Viewport.SetAccentColor(Color.Parse(CurrentAccentHex()));
        Viewport.SetImageQuality(_settings.InitialImageQuality);
        Viewport.SetInteractivePanQuality(_settings.InteractivePanQuality);
        Viewport.GestureBindings = _settings.Gestures;
        Viewport.PointerCenteredZoomEnabled = _settings.PointerZoom;
        Viewport.RightDragMode = _settings.RightDragBehavior switch
        {
            "Pan image only" => RightImageDragMode.PanImageOnly,
            "Move window" => RightImageDragMode.MoveWindow,
            "Do nothing" => RightImageDragMode.Disabled,
            _ => RightImageDragMode.Smart
        };
        Viewport.RightDragWindowMoveAllowed = WindowState == WindowState.Normal;
        Viewport.MiddleDragPanEnabled = _settings.MiddleDragPan;
        Viewport.WindowedWheelZoom = _settings.WindowedWheelZoom;
        Viewport.CtrlWheelZoomEnabled = _settings.CtrlWheelZoom;
        Viewport.InvertWheelDirection = _settings.InvertWheelDirection;
        // This gate is specifically for a viewport left-drag becoming a native window move.
        // Empty-background dragging has its own user toggle and consults the same state policy below.
        Viewport.BackgroundWindowDragEnabled = IsLeftWindowMoveAllowedForCurrentState();
        var useOverlayInteractions = _wholeAppOverlayMode && !_settings.WholeAppOverlayUseWindowedInteractions;
        Viewport.SelectionClickZoomEnabled = useOverlayInteractions ? false : _settings.SelectionClickZoom;
        Viewport.SelectionRightClickZoomOutEnabled = useOverlayInteractions ? false : _settings.SelectionRightClickZoomOut;
        Viewport.ReverseSelectionZoomOutScale = _settings.ReverseSelectionZoomOutScale;
        Viewport.PreserveManualZoomOnBitmapChange = _settings.PreserveManualZoomOnNavigate;
        Viewport.LeftDragMode = useOverlayInteractions
            ? LeftImageDragMode.MoveWindow
            : _settings.LeftDragMode switch
            {
                "Pan image" => LeftImageDragMode.Pan,
                "Move window" => LeftImageDragMode.MoveWindow,
                _ => LeftImageDragMode.Selection
            };
        Viewport.IsFullscreen = WindowState == WindowState.FullScreen;
        Viewport.FullscreenClickNavigationEnabled = _settings.FullscreenClickNavigation;

        NavigationGroup.IsVisible = _settings.StatusShowNavigation;
        ZoomGroup.IsVisible = _settings.StatusShowZoom;
        SlideshowGroup.IsVisible = _settings.StatusShowSlideshow;
        FitGroup.IsVisible = _settings.StatusShowFit;
        InfoGroup.IsVisible = _settings.StatusShowInfo;
        OverlayGroup.IsVisible = true;
        OptionsGroup.IsVisible = _settings.StatusShowOptions;
        StatusCloseButton.IsVisible = _settings.StatusShowClose;
        UpdateOverlayStatusButtons();
        ApplyStatusBarSize();
        CornerCounterHost.IsVisible = _settings.PictureCounterEnabled && !_wholeAppOverlayMode;
        ApplyPictureCounterVisuals();

        // Keep fullscreen overlay chrome in sync when its auto-hide setting changes live.
        ApplyChromeLayoutForWindowState();

        if (deferSecondaryChrome)
        {
            ApplyStatusVisibility();
            UpdateStatusStats();
            UpdateWindowTitle();
            Dispatcher.UIThread.Post(() =>
            {
                if (_windowLifetimeCts.IsCancellationRequested) return;
                RebuildTitleActionStrip();
                ApplyTabUtilityButtonVisibility();
                if (_titleBarButtons.TryGetValue("window.alwaysOnTop", out var topmostButton)) SetActiveClass(topmostButton, _settings.AlwaysOnTop);
                ApplyStatusVisibility();
                UpdateViewportScrollbars();
                RebuildTabStrip();
                RefreshDynamicTooltips();
                UpdateStatusStats();
                UpdateWindowTitle();
            }, DispatcherPriority.Background);
            return;
        }

        RebuildTitleActionStrip();
        ApplyTabUtilityButtonVisibility();
        if (_titleBarButtons.TryGetValue("window.alwaysOnTop", out var topmostButton)) SetActiveClass(topmostButton, _settings.AlwaysOnTop);
        ApplyStatusVisibility();
        UpdateViewportScrollbars();
        RebuildTabStrip();
        RefreshDynamicTooltips();
        UpdateStatusStats();
        UpdateWindowTitle();
    }

    private void ApplyStatusBarSize()
    {
        if (ViewerStatusSurface is null || StatusStatsText is null) return;
        var normalized = _settings.StatusBarSize switch
        {
            "Very small" => "Very small",
            "Small" => "Small",
            "Large" => "Large",
            "Extra large" => "Extra large",
            _ => "Medium"
        };
        _settings.StatusBarSize = normalized;

        var metrics = normalized switch
        {
            "Very small" => (Height: 40d, MaxWidth: 780d, Icon: 32d, StatsWidth: 170d, Font: 12.5d),
            "Small" => (Height: 48d, MaxWidth: 930d, Icon: 38d, StatsWidth: 210d, Font: 13d),
            "Large" => (Height: 68d, MaxWidth: 1240d, Icon: 54d, StatsWidth: 290d, Font: 14d),
            "Extra large" => (Height: 80d, MaxWidth: 1420d, Icon: 64d, StatsWidth: 340d, Font: 15d),
            _ => (Height: 58d, MaxWidth: 1080d, Icon: 46d, StatsWidth: 250d, Font: 13.5d)
        };

        var scale = Math.Clamp(_settings.StatusBarGlobalScalePercent, 60, 160) / 100.0;
        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
            scale *= 1.0 + Math.Clamp(_settings.StatusBarMaximizedBoostPercent, 0, 30) / 100.0;

        if (_settings.StatusBarAutoFit && Bounds.Width >= 720)
        {
            // Shrink before clipping rather than letting the legacy single-row surface disappear.
            // The configured size remains authoritative; this is a temporary layout factor only.
            var available = Math.Max(96d, Bounds.Width - 20d);
            var desired = metrics.MaxWidth * scale;
            if (desired > available) scale *= available / desired;
        }
        scale = Math.Clamp(scale, 0.08, 2.0);

        // Narrow-window contract: remain horizontal and WRAP into two/three rows. Never turn the
        // status controls into a vertical tower. The XAML uses small independent groups so WrapPanel
        // can break naturally without one large group imposing a hidden minimum width.
        var narrowStatus = Bounds.Width > 0 && Bounds.Width < 720;
        StatusControlsPanel.Orientation = Avalonia.Layout.Orientation.Horizontal;
        StatusStatsText.IsVisible = !narrowStatus || Bounds.Width >= 560;

        foreach (var cls in new[] { "statusVerySmall", "statusSmall", "statusMedium", "statusLarge", "statusExtraLarge" })
            ViewerStatusSurface.Classes.Remove(cls);
        ViewerStatusSurface.Classes.Add(normalized switch
        {
            "Very small" => "statusVerySmall",
            "Small" => "statusSmall",
            "Large" => "statusLarge",
            "Extra large" => "statusExtraLarge",
            _ => "statusMedium"
        });

                var availableWidth = Math.Max(120d, Bounds.Width - 28d);
        var nominalRowHeight = Math.Max(24d, metrics.Icon * scale + 10d);
        var estimatedDesiredWidth = Math.Max(1d, metrics.MaxWidth * scale);
        var rows = narrowStatus ? Math.Clamp((int)Math.Ceiling(estimatedDesiredWidth / availableWidth), 2, 3) : 1;
        ViewerStatusSurface.Height = Math.Max(metrics.Height * scale, nominalRowHeight * rows + 8d);
        ViewerStatusSurface.MaxWidth = narrowStatus ? availableWidth : metrics.MaxWidth * scale;
        // The two widest logical groups are themselves wrap-capable so they cannot establish a
        // hidden minimum width at very small window sizes. Their buttons still run horizontally
        // left-to-right; they simply continue on the next status row when needed.
        NavigationGroup.MaxWidth = narrowStatus ? Math.Max(metrics.Icon * scale * 2.15, 72) : double.PositiveInfinity;
        OverlayGroup.MaxWidth = narrowStatus ? Math.Max(metrics.Icon * scale * 2.15, 72) : double.PositiveInfinity;
        StatusStatsText.MaxWidth = metrics.StatsWidth * scale;
        StatusStatsText.FontSize = metrics.Font * scale;
        foreach (var button in ViewerStatusSurface.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("legacyStatusIcon")))
        {
            button.Width = metrics.Icon * scale;
            button.Height = metrics.Icon * scale;
        }

        if (_homeSurface is not null)
        {
            _homeSurface.StatusSurface.Height = metrics.Height * scale;
            _homeSurface.StatusSurface.MaxWidth = metrics.MaxWidth * scale;
            foreach (var button in _homeSurface.StatusSurface.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("legacyStatusIcon")))
            {
                button.Width = metrics.Icon * scale;
                button.Height = metrics.Icon * scale;
            }
        }
    }

    private void StatusSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ViewerStatusSurface).Properties.IsRightButtonPressed) return;
        e.Handled = true;
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        if (WindowState == WindowState.FullScreen)
        {
            var pin = new MenuItem
            {
                Header = _settings.FullscreenStatusAlwaysOn
                    ? "Keep status bar visible in fullscreen  ✓"
                    : "Keep status bar visible in fullscreen"
            };
            pin.Click += (_, _) =>
            {
                _settings.FullscreenStatusAlwaysOn = !_settings.FullscreenStatusAlwaysOn;
                SaveSettingsAndPublish();
                ApplyStatusVisibility();
            };
            items.Add(pin);
            items.Add(new MenuItem { Header = "—", IsEnabled = false });
        }
        var hoverReveal = new MenuItem { Header = _settings.StatusShowOnHoverWhenClosed ? "Reveal collapsed bar on bottom-edge hover  ✓" : "Reveal collapsed bar on bottom-edge hover" };
        hoverReveal.Click += (_, _) => { _settings.StatusShowOnHoverWhenClosed = !_settings.StatusShowOnHoverWhenClosed; SaveSettingsAndPublish(); ApplyStatusVisibility(); };
        items.Add(hoverReveal);
        items.Add(new MenuItem { Header = "—", IsEnabled = false });
        foreach (var size in new[] { "Very small", "Small", "Medium", "Large", "Extra large" })
        {
            var item = new MenuItem { Header = string.Equals(_settings.StatusBarSize, size, StringComparison.OrdinalIgnoreCase) ? $"{size}  ✓" : size };
            item.Click += (_, _) =>
            {
                _settings.StatusBarSize = size;
                ApplyStatusBarSize();
                SaveSettingsAndPublish();
                UpdateStatusStats();
            };
            items.Add(item);
        }
        menu.ItemsSource = items;
        menu.Open(ViewerStatusSurface);
    }

    private void ApplyStatusVisibility()
    {
        if (_wholeAppOverlayMode)
        {
            ViewerStatusSurface.IsVisible = false;
            if (_homeSurface is not null) _homeSurface.StatusSurface.IsVisible = false;
            StatusRestoreButton.IsVisible = false;
            return;
        }
        var fullscreen = WindowState == WindowState.FullScreen;
        var allowed = _settings.ShowStatusSurface && (!fullscreen || _settings.FullscreenStatusAlwaysOn);
        var expanded = allowed && !_statusCollapsed && !_statusSessionClosed;
        ViewerStatusSurface.IsVisible = expanded && ImageView.IsVisible;
        if (_homeSurface is not null) _homeSurface.StatusSurface.IsVisible = expanded && HomeHost.IsVisible && !fullscreen;
        StatusRestoreButton.IsVisible = _settings.ShowStatusSurface &&
            (_statusCollapsed || (fullscreen && !_settings.FullscreenStatusAlwaysOn)) &&
            (ImageView.IsVisible || HomeHost.IsVisible);
    }

    private void ApplyChromeLayoutForWindowState()
    {
        if (MainRoot.RowDefinitions.Count < 2) return;

        if (_wholeAppOverlayMode)
        {
            MainRoot.RowDefinitions[0].Height = new GridLength(0);
            Grid.SetRow(ChromeBorder, 1);
            ChromeBorder.ZIndex = 1000;
            ChromeBorder.IsVisible = false;
            FullscreenCornerCloseHit.IsVisible = false;
            return;
        }

        var fullscreen = WindowState == WindowState.FullScreen;
        if (fullscreen && _settings.FullscreenKeepTabBarOpen)
        {
            // Pinned fullscreen chrome participates in layout: the image resizes below the 44-DIP row.
            MainRoot.RowDefinitions[0].Height = new GridLength(44);
            Grid.SetRow(ChromeBorder, 0);
            ChromeBorder.ZIndex = 0;
            ChromeBorder.IsVisible = true;
            FullscreenCornerCloseHit.IsVisible = false;
        }
        else if (fullscreen)
        {
            // Default fullscreen remains edge-to-edge. Revealed chrome overlays the image and reserves no space.
            MainRoot.RowDefinitions[0].Height = new GridLength(0);
            Grid.SetRow(ChromeBorder, 1);
            ChromeBorder.ZIndex = 1000;
            ChromeBorder.IsVisible = !_settings.AutoHideFullscreenChrome;
            FullscreenCornerCloseHit.IsVisible = true;
        }
        else
        {
            MainRoot.RowDefinitions[0].Height = new GridLength(44);
            Grid.SetRow(ChromeBorder, 0);
            ChromeBorder.ZIndex = 0;
            ChromeBorder.IsVisible = true;
            FullscreenCornerCloseHit.IsVisible = false;
        }
    }

    private void UpdateIntegratedTitleBarInset()
    {
        // Glide owns the 44-DIP title row; the caption controls are a real grid column rather than
        // an approximate right-side margin. This makes the close hit target reach the top/right edge
        // and prevents tab/tool content from drifting underneath caption controls at any DPI.
        // Fullscreen auto-hide reveals the complete browser chrome, not a reduced tab-only strip.
        // Caption controls therefore remain part of the same 44-DIP row and use the exact same
        // geometry as windowed mode whenever the hot edge reveals ChromeBorder.
        CaptionButtonHost.IsVisible = true;
        ChromeLayout.Margin = new Thickness(8, 0, 0, 0);
    }

    private enum CaptionButtonRole { Minimize, Maximize, Close }

    private void UpdateCaptionButtonState()
    {
        if (CaptionMaximizeIcon is null) return;
        var restore = WindowState is WindowState.Maximized or WindowState.FullScreen;
        CaptionMaximizeIcon.Text = restore ? "\uE923" : "\uE922";
        var fullscreen = WindowState == WindowState.FullScreen;
        ToolTip.SetTip(CaptionMinimizeButton, $"Minimize button — {CaptionActionFor(CaptionButtonRole.Minimize, fullscreen)}");
        ToolTip.SetTip(CaptionMaximizeButton, $"Maximize button — {CaptionActionFor(CaptionButtonRole.Maximize, fullscreen)}");
        ToolTip.SetTip(CaptionCloseButton, $"Close button — {CaptionActionFor(CaptionButtonRole.Close, fullscreen)}");
    }

    private string CaptionActionFor(CaptionButtonRole role, bool fullscreen) => (fullscreen, role) switch
    {
        (false, CaptionButtonRole.Minimize) => _settings.CaptionWindowedMinimizeAction,
        (false, CaptionButtonRole.Maximize) => _settings.CaptionWindowedMaximizeAction,
        (false, CaptionButtonRole.Close) => _settings.CaptionWindowedCloseAction,
        (true, CaptionButtonRole.Minimize) => _settings.CaptionFullscreenMinimizeAction,
        (true, CaptionButtonRole.Maximize) => _settings.CaptionFullscreenMaximizeAction,
        (true, CaptionButtonRole.Close) => _settings.CaptionFullscreenCloseAction,
        _ => "No action"
    };

    private void CaptionMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        ExecuteConfiguredCaptionAction(CaptionButtonRole.Minimize);
        e.Handled = true;
    }

    private void CaptionMaximizeClicked(object? sender, RoutedEventArgs e)
    {
        ExecuteConfiguredCaptionAction(CaptionButtonRole.Maximize);
        e.Handled = true;
    }

    private void CaptionCloseClicked(object? sender, RoutedEventArgs e)
    {
        ExecuteConfiguredCaptionAction(CaptionButtonRole.Close);
        e.Handled = true;
    }

    private void ExecuteConfiguredCaptionAction(CaptionButtonRole role)
    {
        var fullscreen = WindowState == WindowState.FullScreen;
        ExecuteCaptionAction(CaptionActionFor(role, fullscreen));
    }

    private void ExecuteCaptionAction(string? action)
    {
        _diagnostics.Write("window", "caption_action", new
        {
            action = action ?? "<null>", state = WindowState.ToString(),
            suppressClose = Environment.TickCount64 < Volatile.Read(ref _suppressCloseUntilTick)
        });
        switch (action)
        {
            case "Minimize":
                // A fullscreen HWND must leave fullscreen before minimization or Windows can restore
                // it into an ambiguous fullscreen/minimized state on the next activation.
                if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: false);
                if (!TrySendNativeSystemCommand(ScMinimize)) WindowState = WindowState.Minimized;
                break;
            case "Maximize / restore":
                if (WindowState == WindowState.FullScreen)
                {
                    ExitFullscreen(forceMaximized: false);
                    break;
                }
                var restore = WindowState == WindowState.Maximized;
                if (!TrySendNativeSystemCommand(restore ? ScRestore : ScMaximize))
                    WindowState = restore ? WindowState.Normal : WindowState.Maximized;
                break;
            case "Close Glide":
                if (Environment.TickCount64 < Volatile.Read(ref _suppressCloseUntilTick))
                {
                    _diagnostics.Write("window", "caption_close_suppressed_after_home");
                    break;
                }
                _forceCloseFromCaptionAction = true;
                // The custom caption X is under our control, so do not round-trip through WM_CLOSE
                // when Launch Speed Boost owns the last window. Enter standby directly; this avoids
                // a native close racing the async Closing handler and accidentally ending residency.
                if (ShouldEnterSpeedBoostStandby()) EnterSpeedBoostStandby();
                else if (!TrySendNativeSystemCommand(ScClose)) Close();
                break;
            case "Enter fullscreen":
                if (WindowState != WindowState.FullScreen) EnterFullscreen();
                break;
            case "Exit fullscreen (restore previous placement)":
                if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: false);
                break;
            case "Exit fullscreen maximized":
                if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: true);
                break;
            case "No action":
            default:
                break;
        }
    }

    private bool TrySendNativeSystemCommand(int command)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var platform = TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero) return false;
        try
        {
            SendMessageW(platform.Handle, WmSysCommand, (IntPtr)command, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    private void ChromePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var physicalButtons = PhysicalMouseButtons(point);

        // Classify middle-click ownership at press time, before a tab-origin press can close the
        // tab and rebuild the visual tree. This state is consumed by ChromeMiddleClickReleased.
        if (physicalButtons.Middle)
        {
            _chromeMiddlePressPointer = e.Pointer;
            _chromeMiddlePressPosition = e.GetPosition(ChromeBorder);
            _chromeMiddlePressStartedOnEmptyChrome = e.Source is not Control source || !IsChromeInteractiveSource(source);
            return;
        }

        if (physicalButtons.Right && !IsInteractiveSource(e.Source))
        {
            if (WindowState == WindowState.FullScreen)
            {
                ShowFullscreenChromeContextMenu();
                e.Handled = true;
                return;
            }
            if (_wholeAppOverlayMode)
            {
                BuildWholeAppOverlayOnlyContextMenu().Open(ChromeBorder);
                e.Handled = true;
                return;
            }
        }

        // Treat non-interactive chrome exactly like a native Windows caption. This deliberately
        // includes the empty strip immediately after the final tab / plus button: single-drag moves
        // the window, while double-click toggles maximize/restore. Tab bodies and toolbar controls
        // remain interactive and are excluded by IsInteractiveSource.
        if (!physicalButtons.Left || IsInteractiveSource(e.Source)) return;

        if (e.ClickCount >= 2 && WindowState != WindowState.FullScreen)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        BeginNativeWindowDrag(e);
    }


    private void ShowFullscreenChromeContextMenu()
    {
        var menu = new ContextMenu();
        var pin = new MenuItem
        {
            Header = _settings.FullscreenKeepTabBarOpen
                ? "Keep title/tab bar open in fullscreen  ✓"
                : "Keep title/tab bar open in fullscreen"
        };
        pin.Click += (_, _) =>
        {
            _settings.FullscreenKeepTabBarOpen = !_settings.FullscreenKeepTabBarOpen;
            SaveSettingsAndPublish();
            ApplyChromeLayoutForWindowState();
            ApplyStatusBarSize();
        };
        var items = new List<MenuItem> { pin, new() { Header = "-" } };
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        menu.Open(ChromeBorder);
    }

    private bool IsLeftWindowMoveAllowedForCurrentState()
    {
        if (WindowState == WindowState.FullScreen) return false;
        return _settings.LeftWindowDragBehavior switch
        {
            "Never move window" => false,
            "Allow move when maximized" => WindowState is WindowState.Normal or WindowState.Maximized,
            _ => WindowState == WindowState.Normal
        };
    }

    private void HomeBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var physicalButtons = PhysicalMouseButtons(e.GetCurrentPoint(this));
        if (!_settings.BackgroundDragWindow || !IsLeftWindowMoveAllowedForCurrentState() || !physicalButtons.Left || IsInteractiveSource(e.Source)) return;
        BeginNativeWindowDrag(e);
    }

    private void BeginNativeWindowDrag(PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen) return;
        var point = e.GetCurrentPoint(this);
        var physicalButtons = PhysicalMouseButtons(point);
        try
        {
            _diagnostics.Write("input", "native_window_drag_begin", new { source = e.Source?.GetType().Name, position = Position.ToString(), right = physicalButtons.Right });
            if (physicalButtons.Right)
            {
                _manualWindowDragPointer = e.Pointer;
                _manualWindowDragCursorStart = this.PointToScreen(e.GetPosition(this));
                _manualWindowDragWindowStart = Position;
                _manualWindowDragMoved = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (!physicalButtons.Left) return;
            PrepareForNativeWindowMove(e.Source);
            if (OperatingSystem.IsWindows() && TryRunNativeWindowMoveLoop())
            {
                e.Handled = true;
                return;
            }
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            EndManualWindowDrag();
        }
    }

    private void PrepareForNativeWindowMove(object? source)
    {
        var nativeCapture = OperatingSystem.IsWindows() ? GetNativeCapture() : IntPtr.Zero;
        _diagnostics.Write("input", "native_window_move_prepare", new
        {
            source = source?.GetType().Name,
            nativeCapture = nativeCapture.ToInt64(),
            manualWindowDrag = _manualWindowDragPointer is not null,
            zoom = Viewport.ZoomPercent,
            fullscreen = WindowState == WindowState.FullScreen
        });
        // Cancel only transient pointer ownership. CancelInteraction intentionally preserves the
        // committed bitmap/view transform/pan and therefore does not destroy a completed crop/zoom.
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { }
        }
    }

    private void BeginNativeResizeInteraction()
    {
        // WM_ENTERSIZEMOVE is the authoritative start of Windows' modal move/resize loop. Mark it
        // explicitly so no routed client gesture (overlay body drag, image pan/selection, deferred
        // right drag) can race the native border resize. This is especially important for windows
        // created via Ctrl+N, where restored view state can otherwise make a stale drag appear as
        // the image sliding horizontally while the outer window is being resized.
        _nativeSizeMoveActive = true;
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        _chromeMiddlePressPointer = null;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        if (_wholeAppOverlayControlsHost is { } overlayControls) overlayControls.IsVisible = false;
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { }
        }
        _diagnostics.Write("input", "native_resize_begin", new { Position, Width, Height });
    }

    private void ResetPointerStateAfterNativeResize()
    {
        // Called from WM_EXITSIZEMOVE, not inferred from a quiet SizeChanged interval. This is the
        // authoritative boundary where Windows has finished its modal non-client sizing loop.
        _nativeSizeMoveActive = false;
        EndManualWindowDrag();
        Viewport.CancelInteraction();
        _chromeMiddlePressPointer = null;
        _chromeMiddlePressStartedOnEmptyChrome = false;
        _windowsBrowserFrame?.SetSelectionZoomOutPressed(false);
        if (OperatingSystem.IsWindows())
        {
            try { ReleaseNativeCapture(); } catch { }
        }
        Dispatcher.UIThread.Post(() =>
        {
            EndManualWindowDrag();
            Viewport.CancelInteraction();
            Viewport.InvalidateVisual();
        }, DispatcherPriority.Input);
        _diagnostics.Write("input", "native_resize_pointer_state_reset", new { physicalLeft = OperatingSystem.IsWindows() && (GetAsyncKeyState(VkLButton) & 0x8000) != 0, physicalRight = OperatingSystem.IsWindows() && (GetAsyncKeyState(VkRButton) & 0x8000) != 0 });
    }

    private void BeginDeferredRightWindowDrag(IPointer pointer, PixelPoint screenStart, PixelPoint screenCurrent)
    {
        if (WindowState == WindowState.FullScreen) return;
        EndManualWindowDrag();
        _manualWindowDragPointer = pointer;
        _manualWindowDragCursorStart = screenStart;
        _manualWindowDragWindowStart = Position;
        var dx = screenCurrent.X - screenStart.X;
        var dy = screenCurrent.Y - screenStart.Y;
        _manualWindowDragMoved = true;
        try { pointer.Capture(this); } catch { }
        Position = new PixelPoint(_manualWindowDragWindowStart.X + dx, _manualWindowDragWindowStart.Y + dy);
        _diagnostics.Write("input", "deferred_right_window_drag_begin", new { dx, dy });
    }

    private void MainWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        ResetFullscreenCursorTimer();
        var windowPointer = e.GetPosition(this);
        UpdateFullscreenChromeHover(windowPointer);
        UpdateWholeAppOverlayChromeHover(windowPointer);
        UpdateStatusHoverReveal(windowPointer);
        if (_nativeSizeMoveActive) return;
        if (_manualWindowDragPointer is null || e.Pointer != _manualWindowDragPointer) return;
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            EndManualWindowDrag();
            return;
        }
        var cursor = this.PointToScreen(e.GetPosition(this));
        var dx = cursor.X - _manualWindowDragCursorStart.X;
        var dy = cursor.Y - _manualWindowDragCursorStart.Y;
        if (!_manualWindowDragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
        {
            e.Handled = true;
            return;
        }
        _manualWindowDragMoved = true;
        Position = new PixelPoint(_manualWindowDragWindowStart.X + dx, _manualWindowDragWindowStart.Y + dy);
        e.Handled = true;
    }

    private void MainWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Safety reset for native right-zoom cursor feedback if release bubbles at window scope.
        _windowsBrowserFrame?.SetSelectionZoomOutPressed(false);
        if (_manualWindowDragPointer is null || e.Pointer != _manualWindowDragPointer) return;
        var wasStationaryRightClick = !_manualWindowDragMoved;
        EndManualWindowDrag();
        if (wasStationaryRightClick && ImageView.IsVisible) ShowViewerContextMenu();
        e.Handled = true;
    }

    private void UpdateFullscreenChromeHover(Point pointer)
    {
        if (_wholeAppOverlayMode) return;
        if (WindowState != WindowState.FullScreen || !_settings.AutoHideFullscreenChrome || _settings.FullscreenKeepTabBarOpen) return;

        // Hidden fullscreen chrome has no hit-test surface, so use a narrow hot edge to reveal it.
        // Once revealed, keep it visible while the pointer is inside its actual height, then hide it
        // immediately after leaving. The status surface is independent and remains available.
        var revealEdge = Math.Max(14, 18 / Math.Max(1.0, RenderScaling));
        var chromeHeight = Math.Max(44, ChromeBorder.Bounds.Height);
        if (!ChromeBorder.IsVisible)
        {
            if (pointer.Y <= revealEdge) ChromeBorder.IsVisible = true;
            return;
        }

        if (pointer.Y > chromeHeight + 8) ChromeBorder.IsVisible = false;
    }

    private void UpdateStatusHoverReveal(Point pointer)
    {
        if (_wholeAppOverlayMode) return;
        if (!_statusCollapsed || !_settings.StatusShowOnHoverWhenClosed || !_settings.ShowStatusSurface) return;
        var edge = Math.Max(12, 16 / Math.Max(1.0, RenderScaling));
        var nearBottom = pointer.Y >= Math.Max(0, Bounds.Height - edge);
        if (nearBottom && ImageView.IsVisible) ViewerStatusSurface.IsVisible = true;
        else if (ViewerStatusSurface.IsVisible && pointer.Y < Bounds.Height - Math.Max(edge, ViewerStatusSurface.Bounds.Height + 10)) ViewerStatusSurface.IsVisible = false;
    }

    private void EndManualWindowDrag()
    {
        if (_manualWindowDragPointer is { } pointer)
        {
            try { pointer.Capture(null); } catch { }
        }
        _manualWindowDragPointer = null;
        _manualWindowDragMoved = false;
    }

    private static bool IsInteractiveSource(object? source)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (current.Classes.Contains("tabHit") || current.Classes.Contains("glideTab")) return true;
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (current is Button or TextBox or ComboBox or CheckBox or NumericUpDown or ScrollBar or ListBox or ListBoxItem or Slider) return true;
        return false;
    }

    private static bool IsInsideHomeCard(object? source)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
            if (current is Border border && border.Classes.Contains("homeCard")) return true;
        return false;
    }

    private Task ToggleSlideshowAsync() => EnsureSlideshow().ToggleAsync();

    private async Task<bool> ConfirmSlideshowSessionOptionsAsync()
    {
        var interval = new NumericUpDown { Minimum = 250, Maximum = 600000, Increment = 250, Value = _settings.SlideshowIntervalMs, MinWidth = 150 };
        var loop = new CheckBox { Content = "Loop slideshow", IsChecked = _settings.SlideshowLoop };
        var cross = new CheckBox { Content = "Continue across sibling folders", IsChecked = _settings.SlideshowCrossFolders };
        var shuffle = new CheckBox { Content = "Shuffle images", IsChecked = _settings.SlideshowShuffle };
        var direction = new ComboBox
        {
            ItemsSource = new[] { "Forward", "Backward" },
            SelectedItem = string.Equals(_settings.SlideshowDirection, "Backward", StringComparison.OrdinalIgnoreCase) ? "Backward" : "Forward",
            MinWidth = 150
        };
        var startFullscreen = new CheckBox { Content = "Start slideshow in fullscreen", IsChecked = _settings.SlideshowStartFullscreen };
        var pauseInactive = new CheckBox { Content = "Pause while Glide is not the active window", IsChecked = _settings.SlideshowPauseWhenInactive };
        var start = new Button { Content = "Start slideshow", MinWidth = 130 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = "Slideshow options", FontSize = 21, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Choose this slideshow session before it starts.",
            Foreground = new SolidColorBrush(Color.Parse(IsDarkTheme() ? "#AAB5C2" : "#56616B"))
        });
        var intervalRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        intervalRow.Children.Add(new TextBlock { Text = "Interval (ms)", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
        intervalRow.Children.Add(interval);
        root.Children.Add(intervalRow);
        var directionRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
        directionRow.Children.Add(new TextBlock { Text = "Direction", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MinWidth = 170 });
        directionRow.Children.Add(direction);
        root.Children.Add(directionRow);
        root.Children.Add(loop);
        root.Children.Add(cross);
        root.Children.Add(shuffle);
        root.Children.Add(startFullscreen);
        root.Children.Add(pauseInactive);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "Start slideshow", Icon = Icon, Width = 470, SizeToContent = SizeToContent.Height, MinWidth = 420,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root
        };
        PopupPlacementStore.Track(dialog, "slideshow-start");
        start.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var accepted = await dialog.ShowDialog<bool>(this);
        if (!accepted) return false;
        _settings.SlideshowIntervalMs = (int)(interval.Value ?? 3000);
        _settings.SlideshowLoop = loop.IsChecked == true;
        _settings.SlideshowCrossFolders = cross.IsChecked == true;
        _settings.SlideshowShuffle = shuffle.IsChecked == true;
        _settings.SlideshowDirection = direction.SelectedItem?.ToString() == "Backward" ? "Backward" : "Forward";
        _settings.SlideshowStartFullscreen = startFullscreen.IsChecked == true;
        _settings.SlideshowPauseWhenInactive = pauseInactive.IsChecked == true;
        SaveSettingsAndPublish();
        return true;
    }
    private void StopSlideshow(bool restoreStartingMode) => _slideshow?.Stop(restoreStartingMode);

    private void UpdateSlideshowUi()
    {
        var slideshow = _slideshow;
        var active = slideshow?.IsActive == true;
        var running = slideshow?.IsRunning == true;
        if (SlideshowIcon is not null) SlideshowIcon.Kind = running ? "Pause" : "Play";
        if (SlideshowStopButton is not null) SlideshowStopButton.IsVisible = active;
        if (SlideshowButton is not null)
        {
            var verb = !active ? "Start slideshow" : running ? "Pause slideshow" : "Resume slideshow";
            ToolTip.SetTip(SlideshowButton, $"[{verb} ({ShortcutFor(GlideCommand.StartPauseSlideshow)})]");
            ToolTip.SetShowDelay(SlideshowButton, 650);
        }
        if (SlideshowStopButton is not null)
        {
            ToolTip.SetTip(SlideshowStopButton, $"[Stop slideshow session ({ShortcutFor(GlideCommand.StopSlideshow)})]");
            ToolTip.SetShowDelay(SlideshowStopButton, 650);
        }
    }

    private async void SlideshowStopClicked(object? sender, RoutedEventArgs e)
    {
        StopSlideshow(restoreStartingMode: true);
        await Task.CompletedTask;
    }

    private void ToggleFullscreen()
    {
        if (_wholeAppOverlayMode)
        {
            // Fullscreen and whole-app Overlay are mutually exclusive presentation shells. F11 from
            // Overlay mode returns to the preserved normal shell and then enters fullscreen.
            ExitWholeAppOverlayMode();
            if (WindowState != WindowState.FullScreen) EnterFullscreen();
            return;
        }
        if (WindowState == WindowState.FullScreen) ExitFullscreen(forceMaximized: false);
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (WindowState == WindowState.FullScreen) return;
        CancelSelectionForFullscreenTransition();
        var priorState = WindowState;
        var priorPosition = Position;
        var priorWidth = Width;
        var priorHeight = Height;
        _fullscreen.Capture(priorState, priorPosition, priorWidth, priorHeight);
        WindowState = WindowState.FullScreen;
        ApplyChromeLayoutForWindowState();
        _diagnostics.Write("fullscreen", "entered", new { previousState = priorState, previousPosition = priorPosition, previousWidth = priorWidth, previousHeight = priorHeight });
        ResetFullscreenCursorTimer();
        FinishFullscreenTransition();
    }

    private void ExitFullscreen(bool forceMaximized)
    {
        if (WindowState != WindowState.FullScreen) return;
        CancelSelectionForFullscreenTransition();
        Cursor = null;
        _fullscreenCursorTimer.Stop();
        WindowState = WindowState.Normal;
        var snapshot = _fullscreen.Restore();
        var exitBehavior = forceMaximized ? "Maximized" : (_settings.FullscreenExitBehavior ?? "Restore size and location");
        if (string.Equals(exitBehavior, "Restore size and location", StringComparison.OrdinalIgnoreCase))
        {
            if (snapshot.Width > 0 && snapshot.Height > 0)
            {
                Width = snapshot.Width;
                Height = snapshot.Height;
                Position = snapshot.Position;
            }
            if (snapshot.State == WindowState.Maximized) WindowState = WindowState.Maximized;
            else if (snapshot.State == WindowState.Minimized) WindowState = WindowState.Minimized;
        }
        else if (string.Equals(exitBehavior, "Minimized", StringComparison.OrdinalIgnoreCase))
        {
            WindowState = WindowState.Minimized;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
        ApplyChromeLayoutForWindowState();
        _diagnostics.Write("fullscreen", "exited", new
        {
            restoredState = WindowState.ToString(),
            forcedMaximized = forceMaximized, exitBehavior,
            position = Position.ToString(), Width, Height
        });
        FinishFullscreenTransition();
    }

    private void CancelSelectionForFullscreenTransition()
    {
        // Double-click enters fullscreen during the same pointer sequence that can begin a legacy
        // selection. Cancel capture/interaction first (not merely the visible rectangle), then clear
        // both semantic and fast-overlay state so the second click cannot leave a tiny selection box.
        Viewport.CancelInteraction();
        Viewport.ClearSelection();
        SelectionOverlay.SetSelection(null, default);
    }

    private void FinishFullscreenTransition()
    {
        Viewport.IsFullscreen = WindowState == WindowState.FullScreen;
        ApplyStatusVisibility();
        UpdateCaptionButtonState();
        // A final render-queue clear protects against a queued pointer/selection invalidation from the
        // double-click event that initiated the transition without changing legacy selection logic.
        Dispatcher.UIThread.Post(() => SelectionOverlay.SetSelection(null, default), DispatcherPriority.Render);
    }

    private void ResetFullscreenCursorTimer()
    {
        if (WindowState != WindowState.FullScreen || !_settings.HideCursorFullscreen)
        {
            Cursor = null;
            _fullscreenCursorTimer.Stop();
            return;
        }
        Cursor = null;
        _fullscreenCursorTimer.Stop();
        _fullscreenCursorTimer.Start();
    }

    private Task CloseActiveTabAsync()
    {
        var active = _workspace.Active;
        return active is null ? Task.CompletedTask : CloseTabWithPolicyAsync(active.Id, "active_command");
    }

    private async Task RestoreClosedTabAsync()
    {
        if (_workspace.RestoreClosed() is not null) await ActivateWorkspaceAsync();
    }

    private void CopyWindowGeometryTo(MainWindow child)
    {
        if (WindowState == WindowState.Normal)
        {
            child.Width = Math.Max(child.MinWidth, Bounds.Width);
            child.Height = Math.Max(child.MinHeight, Bounds.Height);
            child.Position = Position;
        }
        else if (_normalPlacementKnown)
        {
            child.Width = Math.Max(child.MinWidth, _lastNormalWindowSize.Width);
            child.Height = Math.Max(child.MinHeight, _lastNormalWindowSize.Height);
            child.Position = _lastNormalWindowPosition;
        }
        else
        {
            child.Width = Math.Max(child.MinWidth, Bounds.Width);
            child.Height = Math.Max(child.MinHeight, Bounds.Height);
        }

        if (WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _stateBeforeMinimize == WindowState.Maximized))
            child.WindowState = WindowState.Maximized;
    }

    private static TabState CloneTabForIndependentWindow(TabState tab) => tab switch
    {
        ImageTabState image => new ImageTabState(Guid.NewGuid(), image.Path) { ViewState = image.ViewState },
        BrowserTabState browser => new BrowserTabState(Guid.NewGuid(), browser.Folder) { Navigation = browser.Navigation },
        _ => new HomeTabState(Guid.NewGuid())
    };

    private async Task OpenCurrentInNewWindowAsync()
    {
        CaptureActiveTabRuntimeState();
        var source = _workspace.Active;
        if (source is null) return;
        // Ctrl+N means a new independent Glide window focused on the same current content. For image
        // tabs this is exactly the same picture/view; Home/Explorer mirror their current destination.
        var cloned = CloneTabForIndependentWindow(source);
        var child = new MainWindow(cloned, _settings.CloneState(), deferWorkspaceActivation: true);
        CopyWindowGeometryTo(child);
        child.Show();
        await child.ActivateWorkspaceAsync();
        child.Activate();
    }

    private async Task DuplicateSessionWindowAsync()
    {
        CaptureActiveTabRuntimeState();
        var activeIndex = Math.Max(0, _workspace.ActiveIndex);
        var clones = _workspace.Tabs.Select(CloneTabForIndependentWindow).ToArray();
        if (clones.Length == 0) return;
        var child = new MainWindow(clones[0], _settings.CloneState(), deferWorkspaceActivation: true);
        child._workspace.Reset(clones, Math.Clamp(activeIndex, 0, clones.Length - 1));
        CopyWindowGeometryTo(child);
        child.Show();
        await child.ActivateWorkspaceAsync();
        child.Activate();
    }

    private async Task DuplicateActiveTabAsync()
    {
        if (_workspace.DuplicateActive() is not null) await ActivateWorkspaceAsync();
    }

    private async Task CycleTabAsync(int delta)
    {
        if (_workspace.SelectRelative(delta)) await ActivateWorkspaceAsync();
    }

    private async Task SelectTabIndexAsync(int index)
    {
        if (_workspace.Tabs.Count == 0) return;
        index = Math.Clamp(index, 0, _workspace.Tabs.Count - 1);
        if (_workspace.Select(_workspace.Tabs[index].Id)) await ActivateWorkspaceAsync();
    }

    private async Task HandleEscapeAsync()
    {
        // Mature Glide Escape hierarchy: stop/restore an active slideshow session first, then leave
        // fullscreen, then clear an ordinary windowed selection, then apply optional close policy.
        if (_slideshow?.IsActive == true && _settings.EscStopsSlideshow)
        {
            StopSlideshow(restoreStartingMode: true);
            return;
        }
        if (WindowState == WindowState.FullScreen && _settings.EscExitsFullscreen)
        {
            ToggleFullscreen();
            return;
        }
        if (Viewport.ClearSelection()) return;
        if (WindowState != WindowState.FullScreen && _settings.EscWindowedConfirm)
        {
            if (string.Equals(_settings.EscWindowedRememberedChoice, "Close", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            if (string.Equals(_settings.EscWindowedRememberedChoice, "KeepOpen", StringComparison.OrdinalIgnoreCase)) return;

            var choice = await ConfirmCloseAsync();
            if (choice.Remember)
            {
                _settings.EscWindowedRememberedChoice = choice.Close ? "Close" : "KeepOpen";
                SaveSettingsAndPublish();
            }
            if (choice.Close) Close();
        }
    }

    private async Task<(bool Close, bool Remember)> ConfirmCloseAsync()
    {
        var closeRequested = false;
        var decided = false;
        var dialog = new Window { Width = 420, Height = 215, Title = "Close Glide?", Icon = Icon, CanResize = false };
        PopupPlacementStore.Track(dialog, "escape-close-confirm");
        var remember = new CheckBox { Content = "Remember my choice" };
        var yes = new Button { Content = "Close", MinWidth = 90 };
        var no = new Button { Content = "Keep open", MinWidth = 90 };
        yes.Click += (_, _) => { closeRequested = true; decided = true; dialog.Close(); };
        no.Click += (_, _) => { closeRequested = false; decided = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 13,
            Children =
            {
                new TextBlock { Text = "Close Glide?", FontSize = 19, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Escape is configured to ask before closing the window.", TextWrapping = TextWrapping.Wrap },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } }
            }
        };
        await dialog.ShowDialog(this);
        return decided ? (closeRequested, remember.IsChecked == true) : (false, false);
    }

    private void UpdatePictureCounter()
    {
        if (CornerCounterText is null || CornerCounterShadowText is null) return;

        var path = _currentPath ?? string.Empty;
        var file = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        var stem = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileNameWithoutExtension(path);
        var extension = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path).TrimStart('.');
        var folderPath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        var folder = string.IsNullOrWhiteSpace(folderPath)
            ? string.Empty
            : Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(folderPath)) folder = folderPath;
        var width = Math.Max(0, _currentPixelWidth);
        var height = Math.Max(0, _currentPixelHeight);
        var megapixels = width > 0 && height > 0 ? (width * (double)height / 1_000_000d).ToString("0.##") : "0";
        var format = string.IsNullOrWhiteSpace(path) ? string.Empty : CodecCapabilityRegistry.GetLongestExtension(path).TrimStart('.').ToUpperInvariant();
        var template = string.IsNullOrWhiteSpace(_settings.PictureCounterTemplate) ? "[{index}/{total}]" : _settings.PictureCounterTemplate;
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{index}"] = _navigator.Count > 0 ? (_navigator.Index + 1).ToString() : "0",
            ["{total}"] = _navigator.Count.ToString(),
            ["{file}"] = file,
            ["{filename}"] = file,
            ["{stem}"] = stem,
            ["{extension}"] = extension,
            ["{format}"] = format,
            ["{folder}"] = folder,
            ["{folderpath}"] = folderPath,
            ["{path}"] = path,
            ["{width}"] = width.ToString(),
            ["{height}"] = height.ToString(),
            ["{dimensions}"] = width > 0 && height > 0 ? $"{width}×{height}" : string.Empty,
            ["{megapixels}"] = megapixels,
            ["{filesize}"] = _currentFileSize > 0 ? FormatFileSize(_currentFileSize) : "0 B",
            ["{filesizebytes}"] = Math.Max(0, _currentFileSize).ToString(),
            ["{zoom}"] = $"{Viewport.ZoomPercent}%"
        };
        var text = template;
        foreach (var (token, value) in replacements) text = text.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        CornerCounterText.Text = text;
        CornerCounterShadowText.Text = text;
    }

    private void ApplyPictureCounterVisuals()
    {
        if (CornerCounterHost is null) return;
        var size = Math.Clamp(_settings.PictureCounterFontSize, 8, 48);
        CornerCounterText.FontSize = size;
        CornerCounterShadowText.FontSize = size;
        var weight = _settings.PictureCounterBold ? FontWeight.SemiBold : FontWeight.Normal;
        CornerCounterText.FontWeight = weight;
        CornerCounterShadowText.FontWeight = weight;
        CornerCounterText.Opacity = Math.Clamp(_settings.PictureCounterOpacity, 10, 100) / 100.0;
        CornerCounterShadowText.Opacity = _settings.PictureCounterShadow ? CornerCounterText.Opacity * 0.85 : 0;
        var colour = _settings.PictureCounterColor switch
        {
            "Soft gray" => IsDarkTheme() ? "#D2D8DE" : "#56616B",
            "Accent" => CurrentAccentHex(),
            "Amber" => "#F1B84B",
            _ => IsDarkTheme() ? "#FFFFFF" : "#20262C"
        };
        CornerCounterText.Foreground = new SolidColorBrush(Color.Parse(colour));

        var position = _settings.PictureCounterPosition ?? "Top right";
        CornerCounterHost.HorizontalAlignment = position.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Layout.HorizontalAlignment.Left
            : position.Contains("center", StringComparison.OrdinalIgnoreCase)
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Right;
        CornerCounterHost.VerticalAlignment = position.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Layout.VerticalAlignment.Bottom
            : Avalonia.Layout.VerticalAlignment.Top;
        CornerCounterHost.Margin = CornerCounterHost.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Top
            ? new Thickness(15, 28, 15, 0)
            : new Thickness(15, 0, 15, _settings.ShowStatusSurface ? 82 : 16);
        UpdatePictureCounter();
    }

    private void UpdateViewportScrollbars()
    {
        if (ViewportHScroll is null || ViewportVScroll is null) return;
        if (_wholeAppOverlayMode)
        {
            ViewportHScroll.IsVisible = false;
            ViewportVScroll.IsVisible = false;
            return;
        }
        var state = Viewport.GetScrollState();
        _syncingViewportScrollbars = true;
        try
        {
            ViewportHScroll.Minimum = 0;
            ViewportHScroll.Maximum = Math.Max(0, state.HorizontalMaximum);
            ViewportHScroll.ViewportSize = Math.Max(1, Viewport.Bounds.Width);
            ViewportHScroll.Value = Math.Clamp(state.HorizontalValue, 0, ViewportHScroll.Maximum);
            ViewportHScroll.IsVisible = _settings.ShowScrollbars && state.HorizontalVisible && ImageView.IsVisible;

            ViewportVScroll.Minimum = 0;
            ViewportVScroll.Maximum = Math.Max(0, state.VerticalMaximum);
            ViewportVScroll.ViewportSize = Math.Max(1, Viewport.Bounds.Height);
            ViewportVScroll.Value = Math.Clamp(state.VerticalValue, 0, ViewportVScroll.Maximum);
            ViewportVScroll.IsVisible = _settings.ShowScrollbars && state.VerticalVisible && ImageView.IsVisible;
        }
        finally
        {
            _syncingViewportScrollbars = false;
        }
    }

    private void UpdateStatusStats()
    {
        if (StatusStatsText is null) return;
        if (string.IsNullOrWhiteSpace(_currentPath) || _navigator.Count == 0)
        {
            StatusStatsText.Text = "Ready";
            return;
        }

        var parts = new List<string>();
        if (_settings.StatusStatIndex) parts.Add($"{_navigator.Index + 1} / {_navigator.Count}");
        if (_settings.StatusStatResolution && _currentPixelWidth > 0) parts.Add($"{_currentPixelWidth} × {_currentPixelHeight}");
        if (_settings.StatusStatZoom) parts.Add($"{Viewport.ZoomPercent}%");
        if (_settings.StatusStatFileSize && _currentFileSize > 0) parts.Add(FormatFileSize(_currentFileSize));
        if (_settings.StatusStatFormat) parts.Add(CodecCapabilityRegistry.GetLongestExtension(_currentPath).TrimStart('.').ToUpperInvariant());
        if (Viewport.TryGetSelectionPixelRect(out var selection)) parts.Add($"Sel {selection.Width} × {selection.Height}");
        StatusStatsText.Text = parts.Count == 0 ? Path.GetFileName(_currentPath) : string.Join("  •  ", parts);
    }

    private void UpdateWindowTitle(string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !ImageView.IsVisible)
        {
            if (HomeHost.IsVisible) Title = "Glide 4.1.4 — Home";
            return;
        }
        var display = _settings.FullPathInTitle ? _currentPath : Path.GetFileName(_currentPath);
        var index = _navigator.Count > 0 ? $"[{_navigator.Index + 1}/{_navigator.Count}]" : string.Empty;
        Title = prefix is null
            ? $"Glide 4.1.4 — {display}  {index}  {Viewport.ZoomPercent}%"
            : $"Glide 4.1.4 — {prefix} — {display}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static void SetActiveClass(Control control, bool active)
    {
        if (active)
        {
            if (!control.Classes.Contains("active")) control.Classes.Add("active");
        }
        else control.Classes.Remove("active");
    }

    private bool IsDarkTheme() => string.Equals(_settings.ThemeChoice, "Dark", StringComparison.OrdinalIgnoreCase);

    private string CurrentAccentHex() => string.Equals(_settings.AccentChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? NormalizeHex(_settings.CustomAccentHex, "#38A9F5") : ResolveAccentHex(_settings.AccentChoice);

    private static string ResolveAccentHex(string? choice) => choice switch
    {
        "Cyan" => "#22C7E8",
        "Teal" => "#30C7B5",
        "Emerald" => "#32C97D",
        "Lime" => "#8CCF45",
        "Violet" => "#A98CFF",
        "Indigo" => "#6E83FF",
        "Magenta" => "#D36CE6",
        "Rose" => "#EE6C9A",
        "Crimson" => "#D94B61",
        "Orange" => "#F08A45",
        "Amber" => "#E0A54A",
        _ => "#38A9F5"
    };

    private static string ResolveAccentSoftHex(string? choice) => choice switch
    {
        "Teal" => "#234842",
        "Violet" => "#44395E",
        "Amber" => "#4B3D28",
        _ => "#27435E"
    };

    private static string ResolveGlowHex(string? choice, string accentChoice) => choice switch
    {
        "Ice blue" => "#78D6FF",
        "Cyan" => "#3BDAF5",
        "Teal" => "#4DDBC9",
        "Emerald" => "#55D99A",
        "Violet" => "#BCA7FF",
        "Magenta" => "#E28AF0",
        "Rose" => "#FF8DB0",
        "Amber" => "#F4C66A",
        "White" => "#FFFFFF",
        _ => ResolveAccentHex(accentChoice)
    };

    private static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim(); if (!text.StartsWith('#')) text = "#" + text;
        try { _ = Color.Parse(text); return text; } catch { return fallback; }
    }

    private static void ApplyPalette(string? themeChoice, string accentChoice, string? glowChoice, int glowIntensityPercent, string customAccentHex, string customGlowHex, string backgroundChoice, string customBackgroundHex)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        static SolidColorBrush B(string value) => new(Color.Parse(value));

        var theme = themeChoice is "Light" or "Neutral" ? themeChoice : "Dark";
        var accent = string.Equals(accentChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? NormalizeHex(customAccentHex, "#38A9F5") : ResolveAccentHex(accentChoice);
        var accentColor = Color.Parse(accent);
        var glowHex = string.Equals(glowChoice, "Custom", StringComparison.OrdinalIgnoreCase) ? NormalizeHex(customGlowHex, accent) : ResolveGlowHex(glowChoice, accentChoice);
        var glowColor = Color.Parse(glowHex);
        var glowStrength = Math.Clamp(glowIntensityPercent, 0, 100) / 100.0;
        static Color Shift(Color c, double factor) => Color.FromArgb(c.A,
            (byte)Math.Clamp((int)Math.Round(c.R * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.G * factor), 0, 255),
            (byte)Math.Clamp((int)Math.Round(c.B * factor), 0, 255));
        static SolidColorBrush A(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B));
        byte GlowAlpha(int full) => (byte)Math.Clamp((int)Math.Round(full * glowStrength), 0, 255);

        resources["SystemAccentColor"] = accentColor;
        resources["SystemAccentColorLight1"] = Shift(accentColor, 1.12);
        resources["SystemAccentColorLight2"] = Shift(accentColor, 1.24);
        resources["SystemAccentColorLight3"] = Shift(accentColor, 1.36);
        resources["SystemAccentColorDark1"] = Shift(accentColor, 0.86);
        resources["SystemAccentColorDark2"] = Shift(accentColor, 0.72);
        resources["SystemAccentColorDark3"] = Shift(accentColor, 0.58);
        resources["BrushAccent"] = B(accent);
        resources["BrushNavAccent"] = B(accent);

        if (theme == "Light")
        {
            // Existing Light profile intentionally remains unchanged.
            resources["BrushWindow"] = B("#F4F5F6"); resources["BrushViewport"] = B("#E9EBED"); resources["BrushChrome"] = B("#EEF0F1");
            resources["BrushTabStrip"] = B("#E6E9EB"); resources["BrushSurface"] = B("#FFFFFF"); resources["BrushSurfaceRaised"] = B("#FFFFFF");
            resources["BrushSurfaceHover"] = B("#DDEAF5"); resources["BrushCard"] = B("#FFFFFF"); resources["BrushCardAccent"] = B("#D8E8F7");
            resources["BrushBorder"] = B("#BCC5CC"); resources["BrushBorderSoft"] = B("#D4DADF"); resources["BrushText"] = B("#171A1C");
            resources["BrushMuted"] = B("#56616B"); resources["BrushMuted2"] = B("#79838C"); resources["BrushSettingsRail"] = B("#E8EBED");
            resources["BrushChromeButton"] = B("#F3F3F3"); resources["BrushChromeButtonHover"] = B("#E5E5E5"); resources["BrushChromeButtonPressed"] = B("#DADADA"); resources["BrushChromeButtonBorder"] = B("#F3F3F3");
            resources["BrushTabHover"] = B("#E7E7E7"); resources["BrushTabActive"] = B("#FFFFFF"); resources["BrushTabCloseHover"] = B("#D0D0D0");
            resources["BrushStatusSurface"] = B("#EAF7F8F9"); resources["BrushStatusButton"] = B("#FFFFFF"); resources["BrushStatusButtonHover"] = B("#E4EFF7"); resources["BrushStatusButtonPressed"] = B("#D5E7F4");
            resources["BrushStatusBorder"] = B("#C8D0D6"); resources["BrushStatusButtonBorder"] = B("#C7D0D7"); resources["BrushStatusNavBorder"] = B("#8FBAD3");
            resources["BrushSettingsNavHover"] = B("#DDE5EB"); resources["BrushSettingsNavSelected"] = B("#CFE1F0");
            resources["BrushInput"] = B("#FFFFFF"); resources["BrushInputBorder"] = B("#AEB8C0"); resources["BrushSearch"] = B("#FFFFFF"); resources["BrushFooter"] = B("#ECEFF1");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#F8F9FA"); resources["BrushSecondaryActionHover"] = B("#E5EAEE");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
        else if (theme == "Neutral")
        {
            resources["BrushWindow"] = B("#C4C7C9"); resources["BrushViewport"] = B("#B8BCC0"); resources["BrushChrome"] = B("#B8BBBD");
            resources["BrushTabStrip"] = B("#B7BABD"); resources["BrushSurface"] = B("#D4D6D8"); resources["BrushSurfaceRaised"] = B("#DADCDD");
            resources["BrushSurfaceHover"] = B("#C5D3DD"); resources["BrushCard"] = B("#D1D3D5"); resources["BrushCardAccent"] = B("#C5D4DF");
            resources["BrushBorder"] = B("#9EA4A9"); resources["BrushBorderSoft"] = B("#B4B9BD"); resources["BrushText"] = B("#202428");
            resources["BrushMuted"] = B("#525B62"); resources["BrushMuted2"] = B("#687177"); resources["BrushSettingsRail"] = B("#BEC1C4");
            resources["BrushChromeButton"] = B("#B8BBBD"); resources["BrushChromeButtonHover"] = B("#C6C9CB"); resources["BrushChromeButtonPressed"] = B("#AEB2B4"); resources["BrushChromeButtonBorder"] = B("#B8BBBD");
            resources["BrushTabHover"] = B("#C8CBCD"); resources["BrushTabActive"] = B("#D8DADB"); resources["BrushTabCloseHover"] = B("#B5B9BC");
            resources["BrushStatusSurface"] = B("#E6CDD0D2"); resources["BrushStatusButton"] = B("#D6D8D9"); resources["BrushStatusButtonHover"] = B("#C7D8E3"); resources["BrushStatusButtonPressed"] = B("#B9CDD9");
            resources["BrushStatusBorder"] = B("#A3AAAF"); resources["BrushStatusButtonBorder"] = B("#A7AEB3"); resources["BrushStatusNavBorder"] = B("#769CB4");
            resources["BrushSettingsNavHover"] = B("#C6CDD2"); resources["BrushSettingsNavSelected"] = B("#B9CCD9");
            resources["BrushInput"] = B("#D9DBDC"); resources["BrushInputBorder"] = B("#969EA4"); resources["BrushSearch"] = B("#D9DBDC"); resources["BrushFooter"] = B("#BEC2C5");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#D2D4D5"); resources["BrushSecondaryActionHover"] = B("#C4C8CB");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
        else
        {
            // Existing Dark profile intentionally remains unchanged and remains the default.
            resources["BrushWindow"] = B("#181A1D"); resources["BrushViewport"] = B("#0E0F10"); resources["BrushChrome"] = B("#1A2224");
            resources["BrushTabStrip"] = B("#161616"); resources["BrushSurface"] = B("#1B1E23"); resources["BrushSurfaceRaised"] = B("#202329");
            resources["BrushSurfaceHover"] = B("#21364A"); resources["BrushCard"] = B("#191C1F"); resources["BrushCardAccent"] = B("#22384C");
            resources["BrushBorder"] = B("#343C42"); resources["BrushBorderSoft"] = B("#2A3035"); resources["BrushText"] = B("#F2F4F6");
            resources["BrushMuted"] = B("#AAB5C2"); resources["BrushMuted2"] = B("#7E8995"); resources["BrushSettingsRail"] = B("#1A1D20");
            resources["BrushChromeButton"] = B("#161616"); resources["BrushChromeButtonHover"] = B("#303030"); resources["BrushChromeButtonPressed"] = B("#3A3A3A"); resources["BrushChromeButtonBorder"] = B("#161616");
            resources["BrushTabHover"] = B("#292929"); resources["BrushTabActive"] = B("#333333"); resources["BrushTabCloseHover"] = B("#474747");
            resources["BrushStatusSurface"] = B("#D9202327"); resources["BrushStatusButton"] = B("#1B1F24"); resources["BrushStatusButtonHover"] = B("#23384A"); resources["BrushStatusButtonPressed"] = B("#2A455E");
            resources["BrushStatusBorder"] = B("#30373D"); resources["BrushStatusButtonBorder"] = B("#303840"); resources["BrushStatusNavBorder"] = B("#28536A");
            resources["BrushSettingsNavHover"] = B("#202A33"); resources["BrushSettingsNavSelected"] = B("#29465F");
            resources["BrushInput"] = B("#202226"); resources["BrushInputBorder"] = B("#30363C"); resources["BrushSearch"] = B("#202328"); resources["BrushFooter"] = B("#181B1E");
            resources["BrushPrimaryAction"] = B("#2C76AA"); resources["BrushPrimaryActionHover"] = B("#3787BC"); resources["BrushSecondaryAction"] = B("#1A1C20"); resources["BrushSecondaryActionHover"] = B("#242A30");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        }

        var bright = theme != "Dark";
        var background = backgroundChoice switch
        {
            "Black" => "#000000", "Dark gray" => "#202124", "Neutral gray" => "#777777", "Light gray" => "#D0D0D0", "White" => "#FFFFFF",
            "Custom" => NormalizeHex(customBackgroundHex, "#101214"), _ => null
        };
        if (background is not null) resources["BrushViewport"] = B(background);

        // Glow is deliberately limited to transient/selection surfaces. At the default Follow-accent
        // 100% setting, Dark/Light retain Alpha-5's established accent-derived interaction strengths.
        // At 0%, the ordinary theme hover surfaces remain intact rather than becoming transparent.
        resources["BrushAccentSoft"] = string.Equals(glowChoice, "Follow accent", StringComparison.OrdinalIgnoreCase) && glowStrength >= 0.999
            ? B(ResolveAccentSoftHex(accentChoice))
            : A(glowColor, GlowAlpha(bright ? 54 : 66));
        if (glowStrength > 0)
        {
            resources["BrushSurfaceHover"] = A(glowColor, GlowAlpha(bright ? 30 : 34));
            resources["BrushCardAccent"] = A(glowColor, GlowAlpha(bright ? 34 : 38));
            resources["BrushStatusButtonHover"] = A(glowColor, GlowAlpha(bright ? 34 : 40));
            resources["BrushStatusButtonPressed"] = A(glowColor, GlowAlpha(bright ? 52 : 62));
            resources["BrushSettingsNavHover"] = A(glowColor, GlowAlpha(bright ? 28 : 32));
            resources["BrushSettingsNavSelected"] = A(glowColor, GlowAlpha(bright ? 48 : 58));
        }
        resources["BrushStatusNavBorder"] = A(accentColor, bright ? (byte)150 : (byte)135);
        resources["BrushPrimaryAction"] = B(accent);
        resources["BrushPrimaryActionHover"] = new SolidColorBrush(Shift(accentColor, bright ? 0.90 : 1.10));
    }

    private async void MainPointerShortcutPressed(object? sender, PointerPressedEventArgs e)
    {
        // Plain pointer presses over the viewer are owned by the viewport/overlay gesture
        // routes.  Letting the global mouse-hotkey route consume them first is especially
        // dangerous in fullscreen: a rapid slideshow click can be interpreted twice (once by
        // navigation and once by a configured mouse command such as CloseWindow).  Modified
        // pointer shortcuts and X buttons remain available, while ordinary left/right/middle
        // clicks are arbitrated by the visual that owns the gesture.
        var point = e.GetCurrentPoint(this);
        var physical = PhysicalMouseButtons(point);
        // PointerUpdateKind is not stable across repeated presses (some backends report a generic
        // move/update kind while the button is physically down).  Classify the physical buttons
        // first so an unmodified ordinary click can never fall through to a mapped command such as
        // CloseWindow, regardless of the update kind reported by the platform.
        if (e.KeyModifiers == KeyModifiers.None && (physical.Left || physical.Right || physical.Middle))
            return;
        var shortcut = CanonicalMouseShortcut(e, this);
        if (shortcut is null) return;
        var command = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
        if (command == GlideCommand.None) return;
        _diagnostics.Write("input", "mouse_hotkey_command", new { command = command.ToString(), shortcut });
        e.Handled = true;
        await ExecuteCommandAsync(command);
    }

    private static string? CanonicalMouseShortcut(PointerPressedEventArgs e, Control relativeTo)
    {
        var point = e.GetCurrentPoint(relativeTo);
        string? button = null;
        var update = point.Properties.PointerUpdateKind.ToString();
        if (update.Contains("XButton1", StringComparison.OrdinalIgnoreCase)) button = "MouseX1";
        else if (update.Contains("XButton2", StringComparison.OrdinalIgnoreCase)) button = "MouseX2";
        else
        {
            var physical = PhysicalMouseButtons(point);
            if (physical.Middle) button = "MouseMiddle";
            else if (physical.Right && !physical.Left) button = "MouseRight";
            else if (physical.Left) button = "MouseLeft";
        }
        if (button is null) return null;
        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(button);
        return string.Join("+", parts);
    }

    private async void GlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? GlideCommand.PreviousTab : GlideCommand.NextTab;
            e.Handled = true;
            _diagnostics.Write("input", "global_tab_cycle", new { command = command.ToString() });
            await ExecuteCommandAsync(command);
            return;
        }

        if (!_settings.EnableTabFocusNavigation && e.Key == Key.Tab && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // When Tab focus navigation is disabled, suppress Tab and Shift+Tab from navigating focus through UI buttons
            // unless active input is inside a text box.
            if (e.Source is not TextBox)
            {
                var shortcut = CanonicalShortcut(e);
                var mapped = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
                if (mapped == GlideCommand.None)
                {
                    e.Handled = true;
                }
            }
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Home)
            {
                if (_homeKeyDown)
                {
                    e.Handled = true;
                    return;
                }
                _homeKeyDown = true;
            }

            var shortcut = CanonicalShortcut(e);
            var command = _inputRouter.ResolveShortcut(shortcut, _settings.Hotkeys);
            if (command == GlideCommand.None) return;

        // Arrow navigation has two distinct intents:
        //   1. a tap is exactly one ordinary navigation command (same semantics as clicking the UI),
        //   2. a genuine hold becomes frame-paced rapid browsing only after a short hold threshold.
        // Never treat the first KeyDown as permission to start a 120 Hz producer: a perfectly normal
        // human key press lasts many render frames and previously caused one tap to skip several images.
        // Windows auto-repeat KeyDown events are ignored while the physical key remains down; Glide owns
        // the held playback loop after the threshold, so there is still only one navigation producer.
        if ((e.Key == Key.Right && command == GlideCommand.NextImage) ||
            (e.Key == Key.Left && command == GlideCommand.PreviousImage))
        {
            var direction = command == GlideCommand.NextImage ? 1 : -1;
            if (_navigationBoundaryPromptActive)
            {
                e.Handled = true;
                return;
            }
            if (_suppressedHeldNavigationDirection == direction)
            {
                // Windows can resume auto-repeat as soon as a modal dialog closes even though the
                // user never released the key. That repeat is stale input: require KeyUp first.
                e.Handled = true;
                return;
            }
            if (_suppressedHeldNavigationDirection != 0 && _suppressedHeldNavigationDirection != direction)
                _suppressedHeldNavigationDirection = 0;
            if (!_heldNavigationKeyIsDown || _heldNavigationDirection != direction)
                StartHeldNavigation(direction);
            e.Handled = true;
            return;
        }

        _diagnostics.Write("input", "command", new { command = command.ToString(), shortcut });
        await ExecuteCommandAsync(command);
            e.Handled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "key_command_failed", new { key = e.Key.ToString(), error = ex.GetType().Name, ex.Message });
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home) _homeKeyDown = false;
        if ((e.Key == Key.Right && _suppressedHeldNavigationDirection > 0) ||
            (e.Key == Key.Left && _suppressedHeldNavigationDirection < 0))
        {
            _suppressedHeldNavigationDirection = 0;
            e.Handled = true;
        }
        if ((e.Key == Key.Right && _heldNavigationDirection > 0) ||
            (e.Key == Key.Left && _heldNavigationDirection < 0))
        {
            StopHeldNavigation(settle: true);
            e.Handled = true;
        }
    }

    private void QueueWheelNavigation(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _navigationBoundaryPromptActive) return;

        var now = Stopwatch.GetTimestamp();
        // One isolated notch is intentionally identical to clicking Previous/Next. Only a sustained
        // burst switches subsequent notches to the rapid-preview path. A 220 ms window tolerates
        // ordinary wheel hardware while still requiring clear repeated intent.
        var rapid = _lastWheelNavigationTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(_lastWheelNavigationTimestamp, now) <= TimeSpan.FromMilliseconds(220);
        _lastWheelNavigationTimestamp = now;

        lock (_wheelNavigationSync)
        {
            // A bounded queue keeps input responsive without turning old wheel ticks into seconds of
            // post-input movement. Five frames is enough to absorb a short decode/render hiccup.
            while (_wheelNavigationQueue.Count >= MaxBufferedNavigationCommands)
                _wheelNavigationQueue.Dequeue();
            _wheelNavigationQueue.Enqueue((direction, rapid));
            if (_wheelNavigationProcessorRunning) return;
            _wheelNavigationProcessorRunning = true;
        }
        _ = ProcessWheelNavigationQueueAsync();
    }

    private async Task ProcessWheelNavigationQueueAsync()
    {
        var usedRapidPreview = false;
        try
        {
            while (true)
            {
                (int Direction, bool Rapid) request;
                lock (_wheelNavigationSync)
                {
                    if (_wheelNavigationQueue.Count == 0)
                    {
                        _wheelNavigationProcessorRunning = false;
                        break;
                    }
                    request = _wheelNavigationQueue.Dequeue();
                }

                if (_navigationBoundaryPromptActive)
                    break;

                if (request.Rapid)
                {
                    // Serialize every wheel notch and wait for a render turn after each reduced frame.
                    // This preserves the same no-teleport invariant as held keyboard navigation: fast
                    // input may lower preview fidelity, but it does not deliberately skip images.
                    usedRapidPreview |= await NavigateRapidStepAsync(request.Direction, CancellationToken.None);
                }
                else
                {
                    await NavigateAsync(request.Direction);
                }
            }
        }
        finally
        {
            var restart = false;
            lock (_wheelNavigationSync)
            {
                if (_wheelNavigationQueue.Count == 0)
                    _wheelNavigationProcessorRunning = false;
                else
                {
                    _wheelNavigationProcessorRunning = true;
                    restart = true;
                }
            }

            // Do not arm full-resolution refinement between individual queued wheel frames. That would
            // compete with the next reduced decode on slower hardware. Refine once the serialized burst
            // has actually drained, matching the keyboard hold/release contract.
            if (!restart && usedRapidPreview) ScheduleRapidNavigationSettle();
            if (restart) _ = ProcessWheelNavigationQueueAsync();
        }
    }

    private void StartHeldNavigation(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0 || _navigationBoundaryPromptActive) return;

        if (_heldNavigationCts is { IsCancellationRequested: false } &&
            _heldNavigationDirection == direction && _heldNavigationKeyIsDown)
            return;

        StopHeldNavigation(settle: false);
        _heldNavigationDirection = direction;
        _heldNavigationKeyIsDown = true;
        _heldNavigationCts = new CancellationTokenSource();
        var token = _heldNavigationCts.Token;
        _ = RunHeldNavigationAsync(direction, token);
    }

    private void StopHeldNavigation(bool settle, string? reason = null)
    {
        var wasActive = _heldNavigationCts is not null || _heldNavigationKeyIsDown;
        _heldNavigationCts?.Cancel();
        _heldNavigationCts?.Dispose();
        _heldNavigationCts = null;
        _heldNavigationDirection = 0;
        _heldNavigationKeyIsDown = false;
        if (wasActive && reason is not null)
            _diagnostics.Write("navigation", "held_playback_cancelled", new { reason });
        if (settle) ScheduleRapidNavigationSettle();
    }

    private async Task RunHeldNavigationAsync(int direction, CancellationToken token)
    {
        // A fresh press is one normal navigation action, including the exact same sibling / hierarchical
        // folder-boundary policy used by status-bar/title-bar mouse buttons. Only if the key is STILL down
        // after the hold threshold do we enter the reduced-resolution playback path. This makes a tap
        // deterministic regardless of whether it lasts 30 ms or a few render frames.
        const int holdActivationMs = 180;
        var minimumFrameTime = TimeSpan.FromSeconds(1.0 / 120.0);
        try
        {
            await NavigateAsync(direction);
            if (token.IsCancellationRequested) return;

            await Task.Delay(holdActivationMs, token);
            if (token.IsCancellationRequested) return;

            _diagnostics.Write("navigation", "held_playback_started", new { direction, holdActivationMs });
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                if (!await NavigateRapidStepAsync(direction, token)) break;

                var elapsed = Stopwatch.GetElapsedTime(started);
                var remaining = minimumFrameTime - elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, token);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
    }

    private Task<bool> NavigateRapidStepAsync(int delta, CancellationToken token) =>
        RunSerializedNavigationResultAsync(() => NavigateRapidStepCoreAsync(delta, token));

    private async Task<bool> RunSerializedNavigationResultAsync(Func<Task<bool>> action)
    {
        await _navigationSerializationGate.WaitAsync().ConfigureAwait(true);
        try { return await action().ConfigureAwait(true); }
        finally { _navigationSerializationGate.Release(); }
    }

    private async Task<bool> NavigateRapidStepCoreAsync(int delta, CancellationToken token)
    {
        if (_navigationBoundaryPromptActive || _navigator.Count == 0 || delta == 0 || token.IsCancellationRequested) return false;
        var navigationEpoch = _navigationCommandEpoch;
        _selectionZoomDemandPath = null;
        _lastNavigationDirection = Math.Sign(delta);
        _loader.ReportNavigationActivity(ImageNavigationActivity.RapidBrowse);

        if (!_folderIndexReady) return false;
        var atBoundary = delta > 0 ? _navigator.Index >= _navigator.Count - 1 : _navigator.Index <= 0;
        if (atBoundary && !_folderIndexCanonicalReady) return false;
        if (atBoundary)
        {
            // Held keyboard navigation must obey the SAME global cross-folder policy as mouse/icon
            // navigation. The lookup happens only at a real boundary, never on every frame. If automatic
            // continuation is disabled, stop here; otherwise enter the next sibling / hierarchical branch
            // using the same settings and confirmation rules as NavigateAsync.
            if (!_settings.ContinueSiblingFolders) return false;
            if (!await TryNavigateSiblingFolderAsync(Math.Sign(delta), rapidPreviewOnly: true)) return false;
            if (navigationEpoch != _navigationCommandEpoch && _navigationBoundaryPromptActive) return false;

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (token.IsCancellationRequested) return false;
            if (_navigator.Count > 1)
                _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);
            return true;
        }

        _navigator.Move(delta);
        _diagnostics.Write("navigation", "held_frame_requested", new
        {
            delta, index = _navigator.Index, count = _navigator.Count
        });

        // Schedule prefetching of the next frames immediately so background decoding
        // runs concurrently while presenting and rendering the current frame.
        if (_navigator.Count > 1)
            _loader.SchedulePrefetch(_navigator.Paths, _navigator.Index, _lastNavigationDirection);

        await PresentCurrentAsync(rapidPreviewOnly: true);
        if (token.IsCancellationRequested) return false;

        await Task.Yield();
        return true;
    }

    private async Task ExecuteGestureActionAsync(string actionId)
    {
        actionId = GestureCatalog.NormalizeAction(actionId);
        if (actionId is GestureCatalog.Default or GestureCatalog.Nothing) return;
        if (actionId == GestureCatalog.ContextMenu)
        {
            var menu = BuildViewerContextMenu();
            menu.Open(Viewport);
            return;
        }
        if (actionId == GestureCatalog.ClearSelection)
        {
            Viewport.ClearSelection();
            return;
        }
        var command = GestureCatalog.CommandForAction(actionId);
        if (command != GlideCommand.None) await ExecuteCommandAsync(command);
    }

    private Task NavigateHomeAsync() => RunSerializedNavigationAsync(NavigateHomeCoreAsync);

    private async Task NavigateHomeCoreAsync()
    {
        _diagnostics.Write("workspace", "home_begin", new
        {
            active = _workspace.Active?.Kind.ToString(), currentPath = _currentPath,
            slideshowActive = _slideshow?.IsActive == true, request = Volatile.Read(ref _imageRequestId),
            workspaceEpoch = Volatile.Read(ref _workspaceEpoch)
        });
        // Home mutates the same workspace/navigator/presentation state as image navigation. It must
        // therefore own the navigation gate for the entire transition. Previously Home could race an
        // async slideshow tick or rapid click continuation, which could then present into a workspace
        // that had already switched to Home.
        StopHeldNavigation(settle: false, reason: "home");
        StopSlideshow(false);
        Interlocked.Increment(ref _navigationCommandEpoch);
        AdvanceWorkspaceEpoch();
        Interlocked.Increment(ref _imageRequestId);
        _loader.InvalidatePending();
        _diagnostics.Write("workspace", "home_invalidated", new
        { request = Volatile.Read(ref _imageRequestId), workspaceEpoch = Volatile.Read(ref _workspaceEpoch) });

        if (string.Equals(_settings.HomePageMode, "Browser page", StringComparison.OrdinalIgnoreCase))
        {
            var homeFolder = ResolveHomeBrowserFolder();
            var browser = _workspace.Tabs.OfType<BrowserTabState>()
                .FirstOrDefault(t => string.Equals(t.Folder, homeFolder, StringComparison.OrdinalIgnoreCase));
            browser ??= (BrowserTabState)CreateConfiguredHomeDestination();
            _workspace.Select(browser.Id);
        }
        else
        {
            var home = _workspace.Tabs.FirstOrDefault(t => t.Kind == TabKind.Home);
            home ??= CreateConfiguredHomeDestination();
            _workspace.Select(home.Id);
        }

        _diagnostics.Write("workspace", "home_navigate", new
        {
            active = _workspace.Active?.Kind.ToString(),
            tabs = _workspace.Tabs.Count,
            visible = IsVisible
        });
        await ActivateWorkspaceAsync().ConfigureAwait(true);
        _diagnostics.Write("workspace", "home_complete", new
        { active = _workspace.Active?.Kind.ToString(), imageVisible = ImageView.IsVisible, homeVisible = HomeHost.IsVisible });
    }

    private async Task ExecuteCommandAsync(GlideCommand command)
    {
        // Command entry points include async-void Avalonia event handlers and native key/pointer
        // callbacks.  Never allow a transient decode/navigation failure to escape those callbacks
        // and terminate the process (Home and rapid slideshow input are especially exposed).
        try { await _commands.ExecuteAsync(command).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _diagnostics.Write("input", "command_failed", new { command = command.ToString(), error = ex.GetType().Name, ex.Message });
        }
    }

    internal IReadOnlyCollection<GlideCommand> ProductionMappedCommandsForTests() => _commands.MappedCommands;

    private MainWindowCommandDispatcher CreateCommandDispatcher()
    {
        Task Event(Action<object?, RoutedEventArgs> action) { action(this, new RoutedEventArgs()); return Task.CompletedTask; }
        var actions = new Dictionary<GlideCommand, Func<Task>>
        {
            [GlideCommand.OpenFile] = () => Event(OpenClicked), [GlideCommand.OpenFolder] = () => Event(OpenFolderClicked),
            [GlideCommand.RefreshFolder] = async () => { if (_workspace.Active is BrowserTabState b) NavigateBrowserTo(b.Folder, false); else if (_currentPath is not null) await LoadPathAsync(_currentPath, true); },
            [GlideCommand.Home] = NavigateHomeAsync,
            [GlideCommand.PreviousImage] = () => NavigateAsync(-1), [GlideCommand.NextImage] = () => NavigateAsync(1),
            [GlideCommand.FirstImage] = () => NavigateEdgeAsync(-1), [GlideCommand.LastImage] = () => NavigateEdgeAsync(1),
            [GlideCommand.PreviousFolder] = () => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(-1)), [GlideCommand.NextFolder] = () => RunSerializedNavigationResultAsync(() => TryNavigateSiblingFolderAsync(1)),
            [GlideCommand.ExploreParentFolder] = () => { if (_currentPath is not null) OpenBrowserTab(Path.GetDirectoryName(_currentPath) ?? ""); return Task.CompletedTask; },
            [GlideCommand.FitImage] = () => { Viewport.Fit(); return Task.CompletedTask; }, [GlideCommand.FitWidth] = () => { Viewport.FitWidth(); return Task.CompletedTask; }, [GlideCommand.FitHeight] = () => { Viewport.FitHeight(); return Task.CompletedTask; }, [GlideCommand.ActualSize] = () => { Viewport.ActualSize(); return Task.CompletedTask; },
            [GlideCommand.ToggleFitActual] = () => { if (Viewport.ZoomPercent == 100) Viewport.Fit(); else Viewport.ActualSize(); return Task.CompletedTask; }, [GlideCommand.ZoomIn] = () => { Viewport.ZoomBy(1.15); return Task.CompletedTask; }, [GlideCommand.ZoomOut] = () => { Viewport.ZoomBy(1 / 1.15); return Task.CompletedTask; },
            [GlideCommand.RotateLeft] = () => { Viewport.RotateLeft(); return Task.CompletedTask; }, [GlideCommand.RotateRight] = () => { Viewport.RotateRight(); return Task.CompletedTask; }, [GlideCommand.FlipHorizontal] = () => { Viewport.FlipHorizontal(); return Task.CompletedTask; }, [GlideCommand.FlipVertical] = () => { Viewport.FlipVertical(); return Task.CompletedTask; },
            [GlideCommand.NewTab] = async () => { CreateConfiguredNewTabDestination(); await ActivateWorkspaceAsync(); }, [GlideCommand.NewWindowSameImage] = OpenCurrentInNewWindowAsync, [GlideCommand.DuplicateSessionWindow] = DuplicateSessionWindowAsync, [GlideCommand.CloseTab] = CloseActiveTabAsync, [GlideCommand.RestoreClosedTab] = RestoreClosedTabAsync, [GlideCommand.DuplicateTab] = DuplicateActiveTabAsync,
            [GlideCommand.NextTab] = () => CycleTabAsync(1), [GlideCommand.PreviousTab] = () => CycleTabAsync(-1),
            [GlideCommand.SelectTab1] = () => SelectTabIndexAsync(0), [GlideCommand.SelectTab2] = () => SelectTabIndexAsync(1), [GlideCommand.SelectTab3] = () => SelectTabIndexAsync(2), [GlideCommand.SelectTab4] = () => SelectTabIndexAsync(3), [GlideCommand.SelectTab5] = () => SelectTabIndexAsync(4), [GlideCommand.SelectTab6] = () => SelectTabIndexAsync(5), [GlideCommand.SelectTab7] = () => SelectTabIndexAsync(6), [GlideCommand.SelectTab8] = () => SelectTabIndexAsync(7), [GlideCommand.SelectLastTab] = () => SelectTabIndexAsync(Math.Max(0, _workspace.Tabs.Count - 1)),
            [GlideCommand.DetachTab] = async () => { if (_workspace.Active is { } a) await DetachTabAsync(a.Id, this.PointToScreen(new Point(Bounds.Width / 2, 48))); }, [GlideCommand.CloseWindow] = () => { Close(); return Task.CompletedTask; }, [GlideCommand.ToggleFullscreen] = () => { ToggleFullscreen(); return Task.CompletedTask; }, [GlideCommand.StartPauseSlideshow] = ToggleSlideshowAsync, [GlideCommand.StopSlideshow] = () => { StopSlideshow(true); return Task.CompletedTask; },
            [GlideCommand.ToggleImageInfo] = () => Event(InfoClicked), [GlideCommand.ClearSelection] = HandleEscapeAsync, [GlideCommand.Settings] = () => Event(SettingsClicked), [GlideCommand.AddOverlay] = () => Event(OverlayAddClicked), [GlideCommand.ResetSelectedOverlayZoom] = () => { EnsureOverlays().ResetSelectedZoom(); return Task.CompletedTask; }, [GlideCommand.BringSelectedOverlayFront] = () => { EnsureOverlays().BringSelectedToFront(); return Task.CompletedTask; },
            [GlideCommand.ToggleAlwaysOnTop] = () => Event(AlwaysOnTopClicked), [GlideCommand.ToggleTransparency] = () => Event(TransparencyClicked), [GlideCommand.ShowContextMenu] = () => { ShowViewerContextMenu(); return Task.CompletedTask; }, [GlideCommand.BrowserBack] = () => Event(BrowserBackClicked), [GlideCommand.BrowserForward] = () => Event(BrowserForwardClicked), [GlideCommand.BrowserUp] = () => Event(BrowserUpClicked),
            [GlideCommand.CopyImage] = () => { CopyImagePixels(); return Task.CompletedTask; }, [GlideCommand.ExportSelection] = ExportSelectionAsync, [GlideCommand.CopyFile] = () => { CopyImageFile(); return Task.CompletedTask; }, [GlideCommand.CopyFileName] = () => CopyTextAsync(_currentPath is null ? null : Path.GetFileName(_currentPath)), [GlideCommand.CopyFolderPath] = () => CopyTextAsync(_currentPath is null ? null : Path.GetDirectoryName(_currentPath)), [GlideCommand.CopyFullPath] = () => CopyTextAsync(_currentPath), [GlideCommand.RenameFile] = RenameCurrentFileAsync, [GlideCommand.DeleteFile] = DeleteCurrentFileAsync, [GlideCommand.OpenContainingFolder] = () => { OpenContainingFolder(); return Task.CompletedTask; }, [GlideCommand.ExternalProgram1] = () => { LaunchExternal(1); return Task.CompletedTask; }, [GlideCommand.ExternalProgram2] = () => { LaunchExternal(2); return Task.CompletedTask; }, [GlideCommand.ExternalProgram3] = () => { LaunchExternal(3); return Task.CompletedTask; }
        };
        actions[GlideCommand.ToggleStatusSurface] = () => { if (!_settings.ShowStatusSurface) { _settings.ShowStatusSurface = true; _statusCollapsed = false; SaveSettingsAndPublish(); } else _statusCollapsed = !_statusCollapsed; ApplyStatusVisibility(); return Task.CompletedTask; };
        MainWindowCommandDispatcher.AssertComplete(actions.Keys);
        return new MainWindowCommandDispatcher(actions);
    }

    private static string CanonicalShortcut(KeyEventArgs e)
    {
        // OemPlus is the physical =/+ key on common layouts.  Treat Shift+OemPlus as the semantic
        // "+" shortcut (without serializing a redundant "Shift++"), while an unshifted press remains
        // "=". Numpad Add is always "+". This keeps capture and runtime lookup byte-for-byte aligned.
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

    private string ShortcutFor(GlideCommand command)
    {
        var shortcuts = HotkeyCatalog.ShortcutsForCommand(command, _settings.Hotkeys);
        return shortcuts.Count == 0 ? "Unassigned" : string.Join(" / ", shortcuts);
    }

    private void RefreshDynamicTooltips()
    {
        void Tip(Control c, string text, GlideCommand? command = null)
        {
            ToolTip.SetTip(c, command is null ? text : $"[{text} ({ShortcutFor(command.Value)})]");
            ToolTip.SetShowDelay(c, 650);
        }
        Tip(NewTabButton, "New tab", GlideCommand.NewTab);
        foreach (var (id, button) in _titleBarButtons)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            var label = id == "window.transparency"
                ? $"Window transparency: {(int)Math.Round(_sessionOpacity * 100)}%"
                : definition.Label;
            Tip(button, label, definition.Command);
        }
        if (_homeSurface is not null) Tip(_homeSurface.OptionsButton, "Settings", GlideCommand.Settings);
        Tip(StatusZoomOutButton, "Zoom out", GlideCommand.ZoomOut);
        Tip(StatusZoomInButton, "Zoom in", GlideCommand.ZoomIn);
        Tip(StatusFitWidthButton, "Fit width", GlideCommand.FitWidth);
        Tip(StatusFitHeightButton, "Fit height", GlideCommand.FitHeight);
        Tip(SlideshowButton, _slideshow?.IsActive != true ? "Start slideshow" : _slideshow.IsRunning ? "Pause slideshow" : "Resume slideshow", GlideCommand.StartPauseSlideshow);
        Tip(SlideshowStopButton, "Stop slideshow session", GlideCommand.StopSlideshow);
        Tip(StatusInfoButton, "Image information", GlideCommand.ToggleImageInfo);
        Tip(StatusOptionsButton, "Settings", GlideCommand.Settings);
        Tip(StatusFirstButton, "First image", GlideCommand.FirstImage);
        Tip(StatusPreviousButton, "Previous image", GlideCommand.PreviousImage);
        Tip(StatusNextButton, "Next image", GlideCommand.NextImage);
        Tip(StatusLastButton, "Last image", GlideCommand.LastImage);
        Tip(StatusActualButton, "100% / actual size", GlideCommand.ActualSize);
        Tip(StatusCollapseButton, "Collapse status surface", GlideCommand.ToggleStatusSurface);
        if (_homeSurface is not null) Tip(_homeSurface.StatusCollapseButton, "Collapse status surface", GlideCommand.ToggleStatusSurface);
        Tip(StatusRestoreButton, "Expand status surface", GlideCommand.ToggleStatusSurface);
        Tip(StatusCloseButton, "Hide status surface");
        if (_browserSurface is not null)
        {
            Tip(_browserSurface.BackButton, "Back", GlideCommand.BrowserBack);
            Tip(_browserSurface.ForwardButton, "Forward", GlideCommand.BrowserForward);
            Tip(_browserSurface.UpButton, "Up one folder", GlideCommand.BrowserUp);
            Tip(_browserSurface.RefreshButton, "Refresh", GlideCommand.RefreshFolder);
        }
    }

    private void CopyImagePixels()
    {
        if (Viewport.Bitmap is not { } bitmap) return;
        Avalonia.PixelRect? selection = Viewport.TryGetSelectionPixelRect(out var selected) ? selected : null;
        var ok = WindowsClipboard.TrySetBitmap(bitmap, selection);
        _diagnostics.Write("clipboard", ok ? "image_pixels_copied" : "image_pixels_copy_failed", new
        {
            selected = selection is not null,
            width = selection?.Width ?? bitmap.PixelSize.Width,
            height = selection?.Height ?? bitmap.PixelSize.Height
        });
    }

    private async Task ExportSelectionAsync()
    {
        if (Viewport.Bitmap is not { } bitmap || !Viewport.TryGetSelectionPixelRect(out var selection) || selection.Width <= 0 || selection.Height <= 0)
        {
            _diagnostics.Write("selection", "export_rejected_no_selection", null);
            return;
        }
        var stem = string.IsNullOrWhiteSpace(_currentPath) ? "glide-selection" : Path.GetFileNameWithoutExtension(_currentPath) + "-selection";
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export selected region",
                SuggestedFileName = stem + ".png",
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("Bitmap image") { Patterns = new[] { "*.bmp" } }
                }
            });
            if (file is null) return;
            var destination = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(destination)) return;
            await ImageSelectionExportService.ExportAsync(bitmap, selection, destination);
            _diagnostics.Write("selection", "exported", new { path = destination, selection.Width, selection.Height });
        }
        catch (Exception ex)
        {
            _diagnostics.Write("selection", "export_failed", new { error = ex.Message });
        }
    }

    private void CopyImageFile()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var ok = WindowsClipboard.TrySetFileDrop(_currentPath);
        _diagnostics.Write("clipboard", ok ? "image_file_copied" : "image_file_copy_failed", new { path = _currentPath });
    }

    private async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    internal void RestoreFromMinimizedIfNeeded()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeMinimize == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void ClearPresentedImageForSpeedBoostStandby()
    {
        // Privacy invariant: standby leaves no displayed bitmap attached to either viewport render
        // path. Request/cache invalidation happens before this detach so stale work cannot reattach it.
        var viewportBitmap = Viewport.Bitmap;
        var interactionBitmap = _interactionPreviewBitmap;

        Viewport.CancelInteraction();
        Viewport.SetInteractionBitmap(null);
        _interactionPreviewBitmap = null;
        Viewport.SwapPresentedFrame(null, 0, 0, 0, null);

        _currentPath = null;
        _presentedPath = null;
        _selectionZoomDemandPath = null;
        _currentMetadata = new();
        _currentDecodeMs = 0;
        _currentFirstFrameDecodeMs = 0;
        _currentFirstFrameWidth = 0;
        _currentFirstFrameHeight = 0;
        _currentPixelWidth = 0;
        _currentPixelHeight = 0;
        _currentFileSize = 0;
        _currentDecodeRoute = "none";
        _currentFrameIsPreview = false;
        CancelPreviewQualityRecovery();
        _loader.SetActivePath(null);

        DisposeDetachedBitmap(viewportBitmap);
        if (!ReferenceEquals(interactionBitmap, viewportBitmap))
            DisposeDetachedBitmap(interactionBitmap);
        try { _loader.PurgeCaches(); } catch { }
    }

    private void DisposeDetachedBitmap(Bitmap? bitmap)
    {
        // Cached bitmaps are disposed by ImageLoadCoordinator.PurgeCaches. Only dispose an image
        // that was not in the loader cache, avoiding double-disposal across both ownership domains.
        if (bitmap is null || _loader.IsBitmapCached(bitmap)) return;
        bitmap.Dispose();
    }

    private void ReturnToSpeedBoostStandbyAfterDelegatedOpen()
    {
        _speedBoostStandbyActive = true;
        _warmPresentationGateActive = false;
        Opacity = _sessionOpacity;
        WarmOpenTransitionHost.IsVisible = false;
        Hide();
    }

    private bool ShouldEnterSpeedBoostStandby()
        => !_forceProcessExit && !_closeForSpeedBoostStandby && !_speedBoostStandbyActive &&
           _settings.SpeedBoostEnabled && GlideWindowRegistry.Snapshot().Count <= 1;

    internal void EnterSpeedBoostStandby()
    {
        if (_closeForSpeedBoostStandby) return;
        _speedBoostStandbyActive = true;
        _warmPresentationGateActive = false;
        Opacity = _sessionOpacity;

        // Cancel both UI request epochs and the loader generation before detaching the old frame.
        AdvanceWorkspaceEpoch();
        Interlocked.Increment(ref _imageRequestId);
        _loader.InvalidatePending();
        ClearPresentedImageForSpeedBoostStandby();

        // A close-to-tray/standby transition must never leave the temporary warm-open surface or
        // the empty image surface visible if the user presses X before an image request finishes.
        WarmOpenTransitionHost.IsVisible = false;
        ImageView.IsVisible = false;
        Hide();
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (OperatingSystem.IsWindows())
            {
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
        }
        catch { }
        App.PublishCurrentSettings(_settings);
        App.UpdateTrayIconState(_settings);
        _closeForSpeedBoostStandby = true;
        // Closing the warm window is what releases Avalonia's composition target. The process and
        // broker remain resident; App creates a fresh render window on the next activation.
        Dispatcher.UIThread.Post(Close, DispatcherPriority.Send);
    }

    internal void ExitStandby()
    {
        var warmOpenTransitionVisible = WarmOpenTransitionHost.IsVisible;
        if (!IsVisible)
        {
            ArmWarmPresentationGate();
            Show();
            // Speed Boost reuses this HWND instead of running the normal Opened/attach path.
            // Refresh the native frame so Windows Snap Layouts and edge snapping remain available
            // after the Hide/Show standby transition.
            _windowsBrowserFrame?.RefreshForWarmRestore();
        }
        _speedBoostStandbyActive = false;

        // X from an image clears the viewport, so a no-file warm activation lands on normal Home
        // instead of exposing an empty viewer. An incoming Explorer image owns the transition path.
        if (!warmOpenTransitionVisible && _workspace.Active is ImageTabState && _presentedPath is null)
            ShowHomeSurface();
    }

    internal async Task<ExternalLaunchItemResult> HandleExternalActivateAsync(Guid requestId)
    {
        ExitStandby();
        RestoreFromMinimizedIfNeeded();
        Activate();
        await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
        return new ExternalLaunchItemResult(requestId, ExternalLaunchItemStatus.Accepted, "Activated");
    }

    internal void ForceExit()
    {
        _diagnostics.Write("window", "force_exit_requested", new { state = WindowState.ToString(), active = _workspace.Active?.Kind.ToString() });
        // Force-exit bypasses the ordinary Closing persistence branch, so commit placement first.
        // In particular, never persist Minimized as a launch state: SaveWindowPlacement uses the
        // remembered pre-minimize state and the last normal bounds.
        try
        {
            CaptureActiveTabRuntimeState();
            WorkspaceSessionStore.Save(_workspace.Tabs, _workspace.ActiveIndex);
            SaveWindowPlacement();
        }
        catch (Exception ex)
        {
            _diagnostics.Write("window", "force_exit_persistence_failed", new { error = ex.GetType().Name, ex.Message });
        }

        _forceProcessExit = true;
        Close();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    internal void OpenSettingsDialog()
    {
        SettingsClicked(this, new RoutedEventArgs());
    }

    internal async Task<ExternalLaunchItemResult> HandleExternalFolderOpenAsync(Guid requestId, string path)
    {
        ExitStandby();
        if (!Directory.Exists(path))
        {
            await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
            return new(requestId, ExternalLaunchItemStatus.Rejected, "Folder does not exist.");
        }
        var fullPath = Path.GetFullPath(path);
        var behavior = CurrentExternalOpenBehavior;
        if (string.Equals(behavior, "Open new window", StringComparison.OrdinalIgnoreCase))
        {
            var child = new MainWindow();
            child.QueueOpen(fullPath);
            child.Show();
            await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
            return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened folder in new window.");
        }

        if (string.Equals(behavior, "Overwrite existing tab", StringComparison.OrdinalIgnoreCase) &&
            _workspace.Active is { } active)
        {
            _workspace.ReplaceTab(new BrowserTabState(active.Id, fullPath));
            await ActivateWorkspaceAsync();
        }
        else
        {
            _workspace.AddBrowser(fullPath);
            await ActivateWorkspaceAsync();
        }
        RestoreFromMinimizedIfNeeded();
        Activate();
        await ReleaseWarmPresentationGateAsync(safeFrameReady: true);
        return new(requestId, ExternalLaunchItemStatus.Accepted, "Folder accepted.");
    }

    internal bool ContainsOpenImage(string path)
    {
        var full = Path.GetFullPath(path);
        return _workspace.Tabs.OfType<ImageTabState>().Any(tab =>
            string.Equals(Path.GetFullPath(tab.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    internal async Task RefreshExistingImageFromExternalAsync(string path)
    {
        var full = Path.GetFullPath(path);
        var existing = _workspace.Tabs.OfType<ImageTabState>().FirstOrDefault(tab =>
            string.Equals(Path.GetFullPath(tab.Path), full, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        _workspace.Select(existing.Id);
        await ActivateWorkspaceAsync();
        // Force an actual refresh rather than silently reusing the currently presented frame.
        _loader.InvalidatePath(full);
        await LoadPathAsync(full, updateActiveTab: false);
        RestoreFromMinimizedIfNeeded();
        Activate();
    }

    private static bool SpawnIndependentGlideProcess(string path)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                ArgumentList = { "--force-new-instance", Path.GetFullPath(path) }
            });
            return true;
        }
        catch { return false; }
    }

    internal async Task<ExternalLaunchItemResult> HandleExternalOpenAsync(Guid requestId, string path, string behavior)
    {
        var wasSpeedBoostStandby = _speedBoostStandbyActive;
        if (!File.Exists(path))
            return new(requestId, ExternalLaunchItemStatus.Rejected, "File does not exist.");
        if (!ImageNavigator.IsSupported(path))
            return new(requestId, ExternalLaunchItemStatus.Rejected, "Unsupported image type.");

        // Set the transition surface while the warm window is still hidden. Showing the HWND first
        // allows Windows to present the last composed image for one frame before this handler runs.
        if (wasSpeedBoostStandby)
            ShowWarmOpenTransitionSurface();
        ExitStandby();

        var duplicateWindow = ExternalLaunchBroker.FindWindowContainingImage(path);
        if (duplicateWindow is not null)
        {
            // Standby deliberately clears the displayed bitmap and presented path for privacy, but
            // retains the image tab's path so workspace identity and navigation survive. If Explorer
            // opens that same path again, this warm window is the receiver we want; applying the
            // normal duplicate policy here would incorrectly spawn a cold process (the default is
            // "Open new instance"). Refresh through the existing tab so the privacy gate remains in
            // place until the new frame is actually ready.
            if (wasSpeedBoostStandby && ReferenceEquals(duplicateWindow, this))
            {
                await duplicateWindow.RefreshExistingImageFromExternalAsync(path);
                RestoreFromMinimizedIfNeeded();
                Activate();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Reopened image in warm Glide instance.");
            }

            var duplicateBehavior = _settings.SameImageAlreadyOpenBehavior ?? "Open new instance";
            if (string.Equals(duplicateBehavior, "Refresh existing image", StringComparison.OrdinalIgnoreCase))
            {
                await duplicateWindow.RefreshExistingImageFromExternalAsync(path);
                if (wasSpeedBoostStandby && !ReferenceEquals(duplicateWindow, this))
                    ReturnToSpeedBoostStandbyAfterDelegatedOpen();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Refreshed already-open image.");
            }
            if (string.Equals(duplicateBehavior, "Open new tab", StringComparison.OrdinalIgnoreCase))
            {
                await OpenAsImageTabAsync(path);
                if (wasSpeedBoostStandby)
                    ShowImageSurface();
                RestoreFromMinimizedIfNeeded();
                Activate();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened duplicate image in new tab.");
            }
            if (SpawnIndependentGlideProcess(path))
            {
                if (wasSpeedBoostStandby)
                    ReturnToSpeedBoostStandbyAfterDelegatedOpen();
                return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened duplicate image in independent Glide process.");
            }
        }

        if (string.Equals(behavior, "Open new window", StringComparison.OrdinalIgnoreCase))
        {
            var child = new MainWindow();
            child.QueueOpen(path);
            child.Show();
            if (wasSpeedBoostStandby)
                ReturnToSpeedBoostStandbyAfterDelegatedOpen();
            return new(requestId, ExternalLaunchItemStatus.Accepted, "Opened image in new window.");
        }
        if (string.Equals(behavior, "Overwrite existing tab", StringComparison.OrdinalIgnoreCase) &&
            _workspace.Active is { } active && _workspace.Tabs.Count > 0)
            _workspace.Close(active.Id, ensureHome: false);
        // Cold opens and warm starts activated from the Start menu retain the normal welcome surface.
        // Only the external image-open path from Speed Boost uses the neutral transition surface.
        if (!wasSpeedBoostStandby && HomeHost.IsVisible)
            ShowImageSurface();
        await OpenAsImageTabAsync(path);
        if (wasSpeedBoostStandby)
        {
            ShowImageSurface();
            // PresentCurrentAsync releases the gate only after the matching image draw/composition.
            // If loading was rejected before that point, reveal a newly composed Home surface.
            if (_warmPresentationGateActive && _presentedPath is null)
                await ReleaseWarmPresentationGateAsync(safeFrameReady: false);
        }
        RestoreFromMinimizedIfNeeded();
        Activate();
        return new(requestId, ExternalLaunchItemStatus.Accepted, "Image accepted.");
    }

    private async Task<string> ShowMultiTabCloseDialogAsync()
    {
        var remember = new CheckBox { Content = "Remember my choice" };
        var closeCurrent = new Button { Content = "Close only current tab", MinWidth = 170 };
        var closeAll = new Button { Content = "Close all", MinWidth = 130, Classes = { "primaryAction" } };
        var dialog = new Window
        {
            Title = "Close Glide tabs?", Width = 470, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(22), Spacing = 16, Children =
            {
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "⚠", FontSize = 26, Foreground = new SolidColorBrush(Color.Parse("#E5B53B")) }, new TextBlock { Text = "This Glide window contains multiple image tabs.\nWhat would you like to close?", TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } },
                remember,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10, Children = { closeCurrent, closeAll } }
            }}
        };
        PopupPlacementStore.Track(dialog, "multi-tab-close-confirm");
        string result = "Cancel";
        closeCurrent.Click += (_, _) => { result = "Close current tab"; dialog.Close(); };
        closeAll.Click += (_, _) => { result = "Close all"; dialog.Close(); };
        await dialog.ShowDialog(this);
        if (remember.IsChecked == true && result != "Cancel") { _settings.RememberedMultiTabCloseChoice = result; _settings.ConfirmCloseMultipleTabs = false; SaveSettingsAndPublish(); }
        return result;
    }

    private void OpenContainingFolder()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var path = _currentPath;
        var folder = Path.GetDirectoryName(path);
        if (string.Equals(_settings.NavigateToFolderBehavior, "Internal browser", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && _workspace.Active is { } active)
        {
            _ = OpenContainingFolderInCurrentTabAsync(active.Id, path, folder);
            return;
        }
        var opened = _fileOperations.TryOpenContainingFolder(path);
        _diagnostics.Write("file", opened ? "open_containing_folder_external" : "open_containing_folder_failed", new { path });
    }

    private async Task OpenContainingFolderInCurrentTabAsync(Guid tabId, string imagePath, string folder)
    {
        _tabForwardImageTargets[tabId] = imagePath;
        _tabForwardFolderTargets[tabId] = new Stack<string>();
        _browserHighlightTargets[tabId] = imagePath;

        var session = new BrowserSession();
        session.History.Add(folder);
        session.Index = 0;
        _browserSessions[tabId] = session;

        _workspace.ReplaceTab(new BrowserTabState(tabId, folder)
        {
            Navigation = new BrowserNavigationState(session.History.ToArray(), session.Index)
        });
        await ActivateWorkspaceAsync();
        SelectBrowserHighlight(tabId);
        _diagnostics.Write("file", "open_containing_folder_internal", new { path = imagePath, folder, sameTab = true });
    }

    private async Task RenameCurrentFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath)) return;
        var oldPath = _currentPath;
        var box = new TextBox { Text = Path.GetFileName(oldPath), MinWidth = 330 };
        var dialog = new Window { Width = 470, Height = 180, Title = "Rename image", Icon = Icon, CanResize = false };
        PopupPlacementStore.Track(dialog, "rename-image");
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var rename = new Button { Content = "Rename", MinWidth = 88 };
        var accepted = false;
        cancel.Click += (_, _) => dialog.Close();
        rename.Click += (_, _) => { accepted = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "New file name", FontWeight = FontWeight.SemiBold }, box,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, rename } }
            }
        };
        await dialog.ShowDialog(this);
        if (!accepted || string.IsNullOrWhiteSpace(box.Text)) return;
        var newPath = _fileOperations.ValidateRename(oldPath, box.Text.Trim());
        if (newPath is null) { _diagnostics.Write("file", "rename_rejected", new { path = oldPath }); return; }
        try
        {
            newPath = await _fileOperations.RenameAsync(oldPath, box.Text.Trim());
            if (newPath is null) return;
            _workspace.ReplaceActiveImage(newPath);
            await LoadPathAsync(newPath, updateActiveTab: true);
            RebuildTabStrip();
        }
        catch { }
    }

    private async Task DeleteCurrentFileAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath)) return;
        var oldPath = _currentPath;
        var oldIndex = Math.Max(0, _navigator.Index);
        var folder = Path.GetDirectoryName(oldPath);
        try
        {
            if (!await _fileOperations.RecycleAsync(oldPath)) return;
            _diagnostics.Write("file", "recycle_delete", new { path = oldPath });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _diagnostics.Write("file", "recycle_delete_failed", new { path = oldPath, error = ex.Message });
            return;
        }

        string? replacement = null;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            try
            {
                var remaining = Directory.EnumerateFiles(folder).Where(ImageNavigator.IsSupported)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
                if (remaining.Count > 0) replacement = remaining[Math.Min(oldIndex, remaining.Count - 1)];
            }
            catch { }
        }

        if (replacement is not null)
        {
            await LoadPathAsync(replacement, updateActiveTab: true);
            RebuildTabStrip();
        }
        else
        {
            await CloseActiveTabAsync();
        }
    }

    private void ShowViewerContextMenu()
    {
        var menu = BuildViewerContextMenu();
        menu.Open(Viewport);
    }

    private ContextMenu BuildViewerContextMenu()
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        MenuItem Item(string label, GlideCommand command, bool enabled = true)
        {
            var suffix = ShortcutFor(command);
            var item = new MenuItem { Header = suffix == "Unassigned" ? label : $"{label}\t{suffix}", IsEnabled = enabled };
            item.Click += async (_, _) => await ExecuteCommandAsync(command);
            return item;
        }
        items.Add(Item("Fit image", GlideCommand.FitImage, Viewport.Bitmap is not null));
        items.Add(Item("Fit width", GlideCommand.FitWidth, Viewport.Bitmap is not null));
        items.Add(Item("Fit height", GlideCommand.FitHeight, Viewport.Bitmap is not null));
        items.Add(Item("Actual size (100%)", GlideCommand.ActualSize, Viewport.Bitmap is not null));
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item("Rotate left", GlideCommand.RotateLeft, Viewport.Bitmap is not null));
        items.Add(Item("Rotate right", GlideCommand.RotateRight, Viewport.Bitmap is not null));
        items.Add(Item("Flip horizontally", GlideCommand.FlipHorizontal, Viewport.Bitmap is not null));
        items.Add(Item("Flip vertically", GlideCommand.FlipVertical, Viewport.Bitmap is not null));
        // Primary Copy Image/Selection Pixels - easily and directly visible
        items.Add(Item(Viewport.HasSelection ? "Copy selected pixels" : "Copy image pixels", GlideCommand.CopyImage, Viewport.Bitmap is not null));

        // Tidied secondary copy and export actions
        var copyOther = new MenuItem { Header = "Copy other" };
        copyOther.ItemsSource = new object[]
        {
            Item("Copy image file", GlideCommand.CopyFile, _currentPath is not null),
            Item("Copy full path", GlideCommand.CopyFullPath, _currentPath is not null),
            Item("Copy file name", GlideCommand.CopyFileName, _currentPath is not null),
            Item("Copy folder path", GlideCommand.CopyFolderPath, _currentPath is not null),
            new MenuItem { Header = "-" },
            Item("Export selected region…", GlideCommand.ExportSelection, Viewport.HasSelection && Viewport.Bitmap is not null)
        };
        items.Add(copyOther);

        // File operations
        items.Add(Item("Open containing folder", GlideCommand.OpenContainingFolder, _currentPath is not null));
        items.Add(Item("Rename…", GlideCommand.RenameFile, _currentPath is not null));
        items.Add(Item("Delete to Recycle Bin", GlideCommand.DeleteFile, _currentPath is not null));
        items.Add(new MenuItem { Header = "-" });

        var navigation = new MenuItem { Header = "Navigation" };
        navigation.ItemsSource = new object[]
        {
            Item("Previous image", GlideCommand.PreviousImage, _navigator.Count > 1),
            Item("Next image", GlideCommand.NextImage, _navigator.Count > 1),
            Item("First image", GlideCommand.FirstImage, _navigator.Count > 1),
            Item("Last image", GlideCommand.LastImage, _navigator.Count > 1),
            new MenuItem { Header = "-" },
            Item("Up to containing folder", GlideCommand.ExploreParentFolder, _currentPath is not null)
        };
        items.Add(navigation);

        var view = new MenuItem { Header = "View" };
        var showStatus = new MenuItem { Header = _settings.ShowStatusSurface ? "Show status bar  ✓" : "Show status bar" };
        showStatus.Click += (_, _) =>
        {
            _settings.ShowStatusSurface = !_settings.ShowStatusSurface;
            _statusCollapsed = false;
            _statusSessionClosed = false;
            SaveSettingsAndPublish();
            ApplyStatusVisibility();
        };
        view.ItemsSource = new object[]
        {
            showStatus,
            Item(ImageInfoOverlay.IsVisible ? "Hide image information" : "Show image information", GlideCommand.ToggleImageInfo, Viewport.Bitmap is not null)
        };
        items.Add(view);

        var presentation = new MenuItem { Header = "Presentation" };
        presentation.ItemsSource = new object[]
        {
            Item(WindowState == WindowState.FullScreen ? "Exit fullscreen" : "Fullscreen", GlideCommand.ToggleFullscreen),
            Item(_slideshow?.IsRunning == true ? "Pause slideshow" : "Start slideshow", GlideCommand.StartPauseSlideshow, _navigator.Count > 0),
            Item("Stop slideshow", GlideCommand.StopSlideshow, _slideshow?.IsActive == true),
            Item((_settings.AlwaysOnTop || _wholeAppOverlayMode) ? "Always on top  ✓" : "Always on top", GlideCommand.ToggleAlwaysOnTop)
        };
        items.Add(presentation);

        if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram1) || !string.IsNullOrWhiteSpace(_settings.ExternalProgram2) || !string.IsNullOrWhiteSpace(_settings.ExternalProgram3))
        {
            var external = new MenuItem { Header = "Open with configured program" };
            var externalItems = new List<MenuItem>();
            if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram1)) externalItems.Add(Item(Path.GetFileNameWithoutExtension(_settings.ExternalProgram1), GlideCommand.ExternalProgram1, _currentPath is not null));
            if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram2)) externalItems.Add(Item(Path.GetFileNameWithoutExtension(_settings.ExternalProgram2), GlideCommand.ExternalProgram2, _currentPath is not null));
            if (!string.IsNullOrWhiteSpace(_settings.ExternalProgram3)) externalItems.Add(Item(Path.GetFileNameWithoutExtension(_settings.ExternalProgram3), GlideCommand.ExternalProgram3, _currentPath is not null));
            external.ItemsSource = externalItems;
            items.Add(external);
        }

        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item("Settings", GlideCommand.Settings));
        items.Add(Item("Exit program", GlideCommand.CloseWindow));
        menu.ItemsSource = items;
        return menu;
    }

    private void LaunchExternal(int slot)
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var program = slot switch { 1 => _settings.ExternalProgram1, 2 => _settings.ExternalProgram2, _ => _settings.ExternalProgram3 };
        var launched = _fileOperations.TryLaunchExternal(program, _currentPath);
        _diagnostics.Write("file", launched ? "external_launched" : "external_launch_failed", new { slot, program, path = _currentPath });
    }

    internal async Task CaptureDiagnosticVisualScenariosAsync(string folder)
    {
        var originalState = WindowState;
        var originalWidth = Width;
        var originalHeight = Height;
        var originalPosition = Position;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;

        var rows = new List<string> { "scenario,physical_width,physical_height,capture,status" };
        var scenarios = new (string Name, int Width, int Height)[]
        {
            ("compact", 900, 600),
            ("landscape", 1400, 760),
            ("portrait", 850, 950),
            ("ultrawide", 1800, 760)
        };

        try
        {
            WindowState = WindowState.Normal;
            foreach (var scenario in scenarios)
            {
                Width = Math.Max(MinWidth, scenario.Width / scale);
                Height = Math.Max(MinHeight, scenario.Height / scale);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(120);
                var file = $"main_resize_{scenario.Name}.png";
                var ok = UiDiagnosticCapture.TryCapture(this, Path.Combine(folder, file), out var error);
                UiDiagnosticCapture.WriteGeometry(this, Path.Combine(folder, $"main_resize_{scenario.Name}_geometry.txt"));
                UiDiagnosticCapture.WriteLayoutAudit(this, Path.Combine(folder, $"main_resize_{scenario.Name}_layout_audit.txt"));
                rows.Add($"{scenario.Name},{scenario.Width},{scenario.Height},{file},{(ok ? "CAPTURED" : "FAIL:" + (error ?? "unknown").Replace(',', ';'))}");
            }
        }
        finally
        {
            Width = originalWidth;
            Height = originalHeight;
            Position = originalPosition;
            WindowState = originalState;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }

        File.WriteAllLines(Path.Combine(folder, "main_resize_scenarios.csv"), rows);

        var combinedVisual = Path.Combine(folder, "combined_visual_results.csv");
        if (File.Exists(combinedVisual))
        {
            var appended = scenarios.Select(scenario =>
                $"\"main_resize_{scenario.Name}\",\"Chrome/content/status stay inside bounds after resize and repaint settles\",\"CAPTURED\",\"main_resize_{scenario.Name}.png\",true,\"Review geometry and stale/ghost paint against legacy Glide\"");
            File.AppendAllLines(combinedVisual, appended);
        }
    }

    public void WriteDiagnosticEvidence(string folder)
    {
        Directory.CreateDirectory(folder);
        _diagnostics.ExportJsonLines(Path.Combine(folder, "behaviour_events.jsonl"));
        File.WriteAllText(Path.Combine(folder, "codec_provider_inventory.json"), JsonSerializer.Serialize(CodecProviderRuntime.Shared.Artifacts, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(folder, "main_runtime_state.txt"), new[]
        {
            $"title={Title}",
            $"windowState={WindowState}",
            $"position={Position}",
            $"logicalSize={Width:F1}x{Height:F1}",
            $"renderScaling={TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0:F3}",
            $"activeTab={_workspace.Active?.Kind}:{_workspace.Active?.Title}",
            $"tabCount={_workspace.Tabs.Count}",
            $"closedTabCount={_workspace.ClosedCount}",
            $"currentPath={_currentPath ?? ""}",
            $"imageIndex={_navigator.Index}",
            $"imageCount={_navigator.Count}",
            $"imagePixels={_currentPixelWidth}x{_currentPixelHeight}",
            $"firstFrameDecodeMs={_currentFirstFrameDecodeMs:F3}",
            $"firstFrameDecodeRoute={_currentDecodeRoute}",
            $"zoomPercent={Viewport.ZoomPercent}",
            $"selection={Viewport.HasSelection}",
            $"slideshowSessionActive={_slideshow?.IsActive == true}",
            $"slideshowRunning={_slideshow?.IsRunning == true}",
            $"statusCollapsed={_statusCollapsed}",
            $"sessionOpacity={_sessionOpacity:F2}",
            $"topmost={Topmost}",
            $"settingsPath={SettingsStore.GetSettingsPath()}"
        });
    }

    private void WriteStartupDiagnostics(string phase)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(local, "Glide", "Startup Diagnostics");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "startup-timings.jsonl");
            var cache = _loader.GetCacheSnapshot();
            var row = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                phase,
                elapsedMs = Math.Round(_startupStopwatch.Elapsed.TotalMilliseconds, 3),
                queuedOpen = _queuedOpen,
                currentPath = _currentPath,
                currentDecodeMs = Math.Round(_currentDecodeMs, 3),
                firstFrameDecodeMs = Math.Round(_currentFirstFrameDecodeMs, 3),
                currentDecodeRoute = _currentDecodeRoute,
                folderIndexReady = _folderIndexReady,
                folderIndexCanonicalReady = _folderIndexCanonicalReady,
                profile = _settings.InitialImageQuality,
                compressedItems = cache.CompressedItems,
                compressedBytes = cache.CompressedBytes,
                decodedItems = cache.DecodedItems,
                decodedBytes = cache.DecodedBytes
            });
            File.AppendAllText(path, row + Environment.NewLine);
        }
        catch { }
    }
    internal async Task<string> RunFullDiagnosticExportAsync(string? targetDir = null)
    {
        var settingsWindow = new SettingsWindow(_settings, ApplySettingsFromDialog)
        {
            Topmost = Topmost
        };
        settingsWindow.Show(this);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(100);
            return await settingsWindow.ExportDiagnosticsAsync(targetDir, this);
        }
        finally
        {
            try { settingsWindow.Close(); } catch { }
        }
    }

    private sealed class BrowserSession
    {
        public List<string> History { get; } = new();
        public int Index { get; set; } = -1;
    }
}
