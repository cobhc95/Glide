using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.Core.Commands;

namespace Glide.App.Controls;

public enum LeftImageDragMode
{
    Selection,
    Pan,
    MoveWindow
}

public enum RightImageDragMode
{
    Smart,
    PanImageOnly,
    MoveWindow,
    Disabled
}

public readonly record struct ViewerViewSnapshot(string Mode, double Zoom, Vector Pan);
public readonly record struct ViewportScrollState(
    bool HorizontalVisible, double HorizontalMaximum, double HorizontalValue,
    bool VerticalVisible, double VerticalMaximum, double VerticalValue);
public readonly record struct ViewportPresentationFrame(
    Bitmap? Bitmap, Size SourceSize, Size BitmapSize, long RequestId, string? Path);


public sealed class GestureActionRequestedEventArgs : EventArgs
{
    public GestureActionRequestedEventArgs(string actionId, Point position)
    {
        ActionId = actionId;
        Position = position;
    }

    public string ActionId { get; }
    public Point Position { get; }
}

/// <summary>
/// Owns image presentation transforms, image-coordinate selection and direct viewport pointer interaction.
/// Physical gestures are configurable, while navigation/window ownership remains outside this control.
/// </summary>
public sealed class ImageViewport : Control
{
    public static readonly StyledProperty<Bitmap?> BitmapProperty =
        AvaloniaProperty.Register<ImageViewport, Bitmap?>(nameof(Bitmap));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ImageViewport, IBrush?>(nameof(Background));

    private enum FitMode { FitImage, FitWidth, FitHeight, Manual }

    private double _zoom = 1.0;
    private Vector _pan;
    private FitMode _fitMode = FitMode.FitImage;
    private int _quarterTurns;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private bool _panning;
    private IPointer? _capturedPointer;
    private bool _releasingPointerCapture;
    private Point _lastPointer;
    private bool _selecting;
    private Point _selectionStartImage;
    private Rect? _selectionImage;
    private Rect _lastImageDest;
    private ViewerViewSnapshot? _panStartView;
    private Point _panStartPointer;
    private bool _panMoved;
    private bool _rightPanGesture;
    private Point _hoverPointer;
    private bool _hoverSelection;
    private Point _pressPoint;
    private long _leftPressTicks;
    private bool _leftPressOnImage;
    private bool _rightPressOnImage;
    private bool _rightPressOnSelection;
    private bool _selectionZoomOutCursorPressed;
    private bool _middlePressOnImage;
    private long _lastRightClickTicks;
    private Point _lastRightClickPoint;
    private long _lastInteractiveInvalidateTicks;
    private long _lastInteractiveViewChangedTicks;
    private Matrix? _selectionViewToImage;
    private Point _selectionStartView;
    private Rect? _selectionViewRect;
    private Size? _presentationSourceSize;
    private BitmapInterpolationMode _configuredInterpolationMode = BitmapInterpolationMode.MediumQuality;
    private string _interactivePanQuality = "Full quality";
    private bool _interactiveQualityActive;
    private Bitmap? _interactionBitmap;
    private ViewportPresentationFrame _interactionFrame;
    private bool _interactionBitmapActive;
    private ViewportPresentationFrame _presentedFrame;
    private long _panStartedTicks;
    private int _panUpdateCount;
    private double _panTravelPixels;
    private Color _accentColor = Color.Parse("#38A9F5");
    private IBrush _selectionFill = new SolidColorBrush(Color.FromArgb(30, 56, 169, 245));
    private IBrush _selectionStroke = new SolidColorBrush(Color.FromArgb(245, 56, 169, 245));
    private readonly Cursor _handCursor = new(StandardCursorType.Hand);

    public event EventHandler<int>? BrowseRequested;
    // Kept separate from click navigation so MainWindow can detect a wheel burst and switch to
    // frame-paced reduced previews without changing isolated-notch semantics.
    public event EventHandler<int>? WheelBrowseRequested;
    public event EventHandler<PointerPressedEventArgs>? NativeWindowDragRequested;
    public event Action<IPointer, Point, Point>? DeferredRightWindowDragRequested;
    public event EventHandler? ViewChanged;
    public event Action<Point>? ContextMenuRequested;
    public event EventHandler<GestureActionRequestedEventArgs>? GestureActionRequested;
    public event Action<string, object?>? InteractionDiagnostic;
    public event Action<bool>? SelectionZoomOutCursorPressedChanged;
    public event Action<long>? PresentationDrawRecorded;
    public long PresentationRequestId => _presentedFrame.RequestId;
    public ViewportPresentationFrame PresentedFrame => _presentedFrame;
    internal void RecordPresentationDrawForTests(long requestId) => PresentationDrawRecorded?.Invoke(requestId);

    public event Action<Bitmap?>? PriorBitmapReleased;

    public bool PointerCenteredZoomEnabled { get; set; } = true;
    // Right-drag policy is intentionally independent from stationary right-click/context-menu
    // behavior. Smart is Glide's default: pan only when the image is actually cropped/zoomed;
    // otherwise move a normal window and do nothing when maximized/fullscreen.
    public RightImageDragMode RightDragMode { get; set; } = RightImageDragMode.Smart;
    public bool RightDragWindowMoveAllowed { get; set; } = true;
    public bool MiddleDragPanEnabled { get; set; }
    public bool WindowedWheelZoom { get; set; }
    public bool CtrlWheelZoomEnabled { get; set; } = true;
    public bool InvertWheelDirection { get; set; }
    public bool BackgroundWindowDragEnabled { get; set; } = true;
    public bool SelectionClickZoomEnabled { get; set; } = true;
    public bool SelectionRightClickZoomOutEnabled { get; set; } = true;
    public bool ReverseSelectionZoomOutScale { get; set; }
    public bool PreserveManualZoomOnBitmapChange { get; set; }
    public bool IsFullscreen { get; set; }
    public bool FullscreenClickNavigationEnabled { get; set; } = true;
    public bool SlideshowRightClickStopEnabled { get; set; }
    public LeftImageDragMode LeftDragMode { get; set; } = LeftImageDragMode.Selection;
    public IReadOnlyDictionary<string, string>? GestureBindings { get; set; }
    public ImageSelectionOverlay? SelectionOverlayTarget { get; set; }

    public event Action? SlideshowRightClickStopRequested;

    public Bitmap? Bitmap
    {
        get => _presentedFrame.Bitmap;
        set => SwapPresentedFrame(value, value?.PixelSize.Width ?? 0, value?.PixelSize.Height ?? 0,
            _presentedFrame.RequestId, _presentedFrame.Path);
    }

    public ViewportPresentationFrame SwapPresentedFrame(Bitmap? bitmap, int sourceWidth, int sourceHeight,
        long requestId, string? path)
    {
        var old = _presentedFrame;
        var preserveManual = PreserveManualZoomOnBitmapChange && _fitMode == FitMode.Manual;
        var size = sourceWidth > 0 && sourceHeight > 0
            ? new Size(sourceWidth, sourceHeight)
            : bitmap is null ? default : _presentedFrame.SourceSize;
        var bitmapSize = bitmap is null ? default : new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);

        _presentedFrame = new ViewportPresentationFrame(bitmap, size, bitmapSize, requestId, path);
        _presentationSourceSize = bitmap is null ? null : size;
        SetValue(BitmapProperty, bitmap);
        _selectionImage = null;
        _selectionViewRect = null;
        _hoverSelection = false;
        Cursor = null;
        _pan = default;
        _quarterTurns = 0;
        _flipHorizontal = false;
        _flipVertical = false;
        if (!preserveManual)
        {
            _fitMode = FitMode.FitImage;
            _zoom = 1;
        }

        InteractionDiagnostic?.Invoke("viewport.frame_swap", new
        {
            oldRequest = old.RequestId, newRequest = requestId,
            oldBitmap = old.Bitmap is null ? (int?)null : RuntimeHelpers.GetHashCode(old.Bitmap),
            newBitmap = bitmap is null ? (int?)null : RuntimeHelpers.GetHashCode(bitmap),
            path,
            transition = false
        });

        InvalidateVisual();
        InvalidateSelectionOverlay();
        RaiseViewChanged();
        return old;
    }

    public Bitmap? ReplaceBitmapPreservingView(Bitmap? bitmap)
    {
        var old = _presentedFrame.Bitmap;
        var source = _presentedFrame.SourceSize;
        SwapPresentedFramePreservingView(bitmap, source, _presentedFrame.RequestId, _presentedFrame.Path);
        return old;
    }

    private void SwapPresentedFramePreservingView(Bitmap? bitmap, Size source, long requestId, string? path)
    {
        var old = _presentedFrame;
        var bitmapSize = bitmap is null ? default : new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        _presentedFrame = new ViewportPresentationFrame(bitmap, source, bitmapSize, requestId, path);
        SetValue(BitmapProperty, bitmap);
        InvalidateVisual();
        RaiseViewChanged();
        InteractionDiagnostic?.Invoke("viewport.frame_swap", new
        {
            oldRequest = old.RequestId, newRequest = requestId,
            oldBitmap = old.Bitmap is null ? (int?)null : RuntimeHelpers.GetHashCode(old.Bitmap),
            newBitmap = bitmap is null ? (int?)null : RuntimeHelpers.GetHashCode(bitmap), path
        });
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    private bool _suppressBackgroundFill;

    /// <summary>
    /// Whole-app Overlay mode can expose real top-level transparency without replacing the
    /// theme-aware Background DynamicResource. No extra visual/backdrop control is required.
    /// </summary>
    public void SetBackgroundFillSuppressed(bool suppressed)
    {
        if (_suppressBackgroundFill == suppressed) return;
        _suppressBackgroundFill = suppressed;
        InvalidateVisual();
    }

    public void SetAccentColor(Color color)
    {
        _accentColor = color;
        _selectionFill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
        _selectionStroke = new SolidColorBrush(Color.FromArgb(245, color.R, color.G, color.B));
        InvalidateVisual();
        InvalidateSelectionOverlay();
    }

    public void SetImageQuality(string quality)
    {
        _configuredInterpolationMode = quality switch
        {
            "Maximum speed" => BitmapInterpolationMode.LowQuality,
            "Maximum quality" => BitmapInterpolationMode.HighQuality,
            _ => BitmapInterpolationMode.MediumQuality
        };
        if (!_interactiveQualityActive) RenderOptions.SetBitmapInterpolationMode(this, _configuredInterpolationMode);
        InvalidateVisual();
    }

    public void SetInteractivePanQuality(string? quality)
    {
        _interactivePanQuality = quality switch
        {
            "Fast" => "Fast",
            "Full quality" => "Full quality",
            _ => "Balanced"
        };
    }

    /// <summary>
    /// Sets the logical source dimensions used for layout while a decoder-scaled preview is on screen.
    /// A preview must occupy exactly the same fitted rectangle as the final frame; only its sampling
    /// resolution may differ. This prevents the Maximum speed first frame from appearing as a small
    /// image surrounded by black borders and then jumping when refinement arrives.
    /// </summary>
    public void SetPresentationSourceSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _presentationSourceSize = new Size(width, height);
        _pan = ClampPan(_pan);
        InvalidateVisual();
    }

    private Size CoordinateImageSize => _presentedFrame.SourceSize;

    /// <summary>
    /// Supplies the decoder-scaled first frame retained by MainWindow after full-resolution refinement.
    /// Large-image gestures can temporarily render this already-decoded screen-scale frame instead of
    /// repeatedly sampling a multi-megapixel bitmap. Ownership stays with MainWindow.
    /// </summary>
    public void SetInteractionBitmap(Bitmap? bitmap)
    {
        _interactionBitmap = bitmap;
        _interactionFrame = bitmap is null
            ? default
            : new ViewportPresentationFrame(bitmap, new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height), _presentedFrame.RequestId, _presentedFrame.Path);
        if (bitmap is null && _interactionBitmapActive)
        {
            _interactionBitmapActive = false;
            InvalidateVisual();
        }
    }

    private bool CanUseInteractionBitmap(bool preferAnySmallerPreview = false)
    {
        var full = Bitmap;
        var preview = _interactionBitmap;
        if (full is null || preview is null || ReferenceEquals(full, preview)) return false;
        var fullPixels = (long)full.PixelSize.Width * full.PixelSize.Height;
        var previewPixels = (long)preview.PixelSize.Width * preview.PixelSize.Height;
        if (previewPixels <= 0 || previewPixels >= fullPixels) return false;
        if (preferAnySmallerPreview) return true;
        // The independently selectable Balanced interaction mode preserves Glide's established behavior: only large images use a genuinely
        // smaller retained preview so interaction cost follows viewport size rather than source size.
        return fullPixels >= 8_000_000 && previewPixels < fullPixels * 0.80;
    }

    private void BeginInteractiveQuality()
    {
        if (_interactiveQualityActive) return;
        _interactiveQualityActive = true;

        if (string.Equals(_interactivePanQuality, "Full quality", StringComparison.OrdinalIgnoreCase))
        {
            _interactionBitmapActive = false;
            RenderOptions.SetBitmapInterpolationMode(this, _configuredInterpolationMode);
            return;
        }

        var fast = string.Equals(_interactivePanQuality, "Fast", StringComparison.OrdinalIgnoreCase);
        _interactionBitmapActive = CanUseInteractionBitmap(preferAnySmallerPreview: fast);
        // The independently selectable Balanced interaction mode retains the low-cost sampling path. Fast additionally
        // accepts any smaller retained preview. Full quality above keeps the configured full frame.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        if (_interactionBitmapActive) InvalidateVisual();
    }

    private void EndInteractiveQuality()
    {
        if (!_interactiveQualityActive) return;
        var restoreFull = _interactionBitmapActive;
        _interactiveQualityActive = false;
        _interactionBitmapActive = false;
        RenderOptions.SetBitmapInterpolationMode(this, _configuredInterpolationMode);
        if (restoreFull) InvalidateVisual();
    }

    private void InvalidateSelectionOverlayInteractive(bool force = false)
    {
        // Hot path: geometry is already in view coordinates. Push four doubles directly to the
        // lightweight overlay and let Avalonia coalesce redundant invalidations naturally. Do not
        // impose a timer gate: a 7 ms gate can miss the event nearest a 120/144 Hz compositor frame.
        SelectionOverlayTarget?.SetSelection(_selectionViewRect, _accentColor);
    }

    private void InvalidateSelectionOverlay()
    {
        _selectionViewRect = _selectionImage is { } imageSelection && Bitmap is not null && _lastImageDest.Width > 0 && _lastImageDest.Height > 0
            ? PixelSnap(ImageRectToView(imageSelection, _lastImageDest, CoordinateImageSize))
            : null;
        SelectionOverlayTarget?.SetSelection(_selectionViewRect, _accentColor);
    }

    public bool IsPointInsideSelection(Point point) =>
        _selectionViewRect is { Width: > 0, Height: > 0 } selection && selection.Contains(point);

    public bool HasSelection => _selectionImage is { Width: > 0, Height: > 0 };
    public bool TryGetSelectionPixelRect(out PixelRect rect)
    {
        if (Bitmap is not { } bitmap || _selectionImage is not { } selection)
        {
            rect = default;
            return false;
        }
        var coordinateSize = CoordinateImageSize;
        var left = Math.Clamp((int)Math.Floor(selection.Left), 0, (int)Math.Round(coordinateSize.Width));
        var top = Math.Clamp((int)Math.Floor(selection.Top), 0, (int)Math.Round(coordinateSize.Height));
        var right = Math.Clamp((int)Math.Ceiling(selection.Right), 0, (int)Math.Round(coordinateSize.Width));
        var bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom), 0, (int)Math.Round(coordinateSize.Height));
        rect = right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
        return rect.Width > 0 && rect.Height > 0;
    }
    public bool IsPointOverImage(Point point) => Bitmap is not null && _lastImageDest.Contains(point);
    public double CurrentScale => GetScale();
    public int ZoomPercent => (int)Math.Round(CurrentScale * 100);

    public ImageViewport()
    {
        ClipToBounds = true;
        // The canvas is deliberately removed from keyboard focus traversal. Logical keyboard commands
        // are handled by MainWindow. Even if platform/input routing transiently assigns focus, the
        // presentation surface must NEVER draw Avalonia's focus adorner around the image.
        Focusable = false;
        FocusAdorner = null;
        AffectsRender<ImageViewport>(BitmapProperty, BackgroundProperty);
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerExited += (_, _) =>
        {
            _hoverSelection = false;
            Cursor = null;
            if (!_panning) _selectionViewToImage = null;
            InvalidateSelectionOverlayInteractive(force: true);
        };
        PointerCaptureLost += OnPointerCaptureLost;
        SizeChanged += (_, _) =>
        {
            _pan = ClampPan(_pan);
            InvalidateVisual();
            // Base image destination is refreshed during Render; refresh selection geometry directly
            // afterwards rather than making MainWindow proxy a second-control invalidation.
            Dispatcher.UIThread.Post(InvalidateSelectionOverlay, DispatcherPriority.Render);
            RaiseViewChanged();
        };
    }



    public void CancelInteraction()
    {
        var hadInteraction = _panning || _selecting || _leftPressOnImage || _rightPressOnImage || _middlePressOnImage;
        if (_selecting) { _selectionImage = null; _selectionViewRect = null; }
        _panning = false;
        _selecting = false;
        _panMoved = false;
        _rightPanGesture = false;
        _leftPressOnImage = false;
        _rightPressOnImage = false;
        _rightPressOnSelection = false;
        SetSelectionZoomOutCursorPressed(false);
        _middlePressOnImage = false;
        _hoverSelection = false;
        _selectionViewToImage = null;
        Cursor = null;
        EndInteractiveQuality();
        ReleaseCapture();
        InvalidateSelectionOverlay();
        if (hadInteraction)
        {
            RequestCleanRedraw(force: true);
            RaiseViewChanged();
        }
    }

    private void SetSelectionZoomOutCursorPressed(bool pressed)
    {
        if (_selectionZoomOutCursorPressed == pressed) return;
        _selectionZoomOutCursorPressed = pressed;
        SelectionZoomOutCursorPressedChanged?.Invoke(pressed);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Releasing our own capture at the end of a completed gesture is expected and must not
        // be interpreted as cancellation. More importantly, some platform/input paths can report
        // capture loss as the physical button is released before PointerReleased has finished.
        // The old generic CancelInteraction() path deleted an in-progress selection in that case,
        // making the box flash during drag and disappear the instant the mouse button was released.
        if (_releasingPointerCapture) return;

        _capturedPointer = null;
        if (_selecting)
        {
            CompleteSelectionInteraction();
            _leftPressOnImage = false;
            return;
        }

        CancelInteraction();
    }

    private bool CompleteSelectionInteraction()
    {
        _selecting = false;

        // Keep the nullable Rect in an explicitly assigned local. Using a pattern variable
        // inside `is not ... || ...` does not make that variable definitely assigned in the
        // compiler's flow analysis for the later else branch (CS0165).
        var selection = _selectionImage;
        var tiny = selection is null || selection.Value.Width < 3 || selection.Value.Height < 3;
        if (tiny)
        {
            _selectionImage = null;
            _selectionViewRect = null;
        }
        else
        {
            var completedSelection = selection.GetValueOrDefault();
            InteractionDiagnostic?.Invoke("selection_created", new
            {
                completedSelection.X,
                completedSelection.Y,
                completedSelection.Width,
                completedSelection.Height
            });
        }

        _selectionViewToImage = null;
        Cursor = null;
        InvalidateSelectionOverlayInteractive(force: true);
        RaiseViewChanged();
        return tiny;
    }

    public void Fit() { _fitMode = FitMode.FitImage; _pan = default; InvalidateVisual(); RaiseViewChanged(); }
    public void FitWidth() { _fitMode = FitMode.FitWidth; _pan = default; InvalidateVisual(); RaiseViewChanged(); }
    public void FitHeight() { _fitMode = FitMode.FitHeight; _pan = default; InvalidateVisual(); RaiseViewChanged(); }
    public void ActualSize() { _fitMode = FitMode.Manual; _zoom = 1; _pan = default; InvalidateVisual(); RaiseViewChanged(); }

    public void ApplyViewMode(string mode)
    {
        switch (mode)
        {
            case "Fit width": FitWidth(); break;
            case "Fit height": FitHeight(); break;
            case "100%": ActualSize(); break;
            default: Fit(); break;
        }
    }


    /// <summary>
    /// Returns the image destination rectangle Glide would use for a supplied viewport size without
    /// mutating the live view. Window-in-Window overlay reflow uses this to preserve image-relative
    /// placement while maximize/fullscreen changes the available viewer geometry.
    /// </summary>
    public Rect? GetPresentationRectForViewportSize(Size viewportSize)
    {
        if (Bitmap is null || viewportSize.Width <= 0 || viewportSize.Height <= 0) return null;
        var coordinateSize = CoordinateImageSize;
        var display = GetDisplayImageSize(coordinateSize);
        if (display.Width <= 0 || display.Height <= 0) return null;
        var w = Math.Max(1, display.Width);
        var h = Math.Max(1, display.Height);
        var scale = _fitMode switch
        {
            FitMode.FitWidth => Math.Min(32, viewportSize.Width / w),
            FitMode.FitHeight => Math.Min(32, viewportSize.Height / h),
            FitMode.FitImage => Math.Min(1.0, Math.Min(viewportSize.Width / w, viewportSize.Height / h)),
            _ => _zoom
        };
        var width = display.Width * scale;
        var height = display.Height * scale;
        var pan = _fitMode == FitMode.Manual ? ClampPanForViewport(_pan, viewportSize, width, height) : default;
        return new Rect((viewportSize.Width - width) / 2 + pan.X, (viewportSize.Height - height) / 2 + pan.Y, width, height);
    }

    private static Vector ClampPanForViewport(Vector candidate, Size viewportSize, double imageWidth, double imageHeight)
    {
        var halfOverflowX = Math.Max(0, imageWidth - viewportSize.Width) / 2;
        var halfOverflowY = Math.Max(0, imageHeight - viewportSize.Height) / 2;
        return new Vector(
            Math.Clamp(candidate.X, -halfOverflowX, halfOverflowX),
            Math.Clamp(candidate.Y, -halfOverflowY, halfOverflowY));
    }

    public ViewerViewSnapshot CaptureViewState() => new(_fitMode.ToString(), _zoom, _pan);

    public void RestoreViewState(ViewerViewSnapshot snapshot)
    {
        _fitMode = Enum.TryParse<FitMode>(snapshot.Mode, out var mode) ? mode : FitMode.FitImage;
        _zoom = Math.Clamp(snapshot.Zoom, 0.05, 32);
        _pan = snapshot.Pan;
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void ZoomBy(double factor) => ZoomBy(factor, null);

    public void ZoomBy(double factor, Point? pointer)
    {
        if (Bitmap is null) return;
        var oldScale = GetScale();
        _fitMode = FitMode.Manual;
        _zoom = Math.Clamp(oldScale * factor, 0.05, 32);
        if (PointerCenteredZoomEnabled && pointer is { } before)
        {
            var ratio = _zoom / Math.Max(0.0001, oldScale);
            var centre = new Point(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
            var vector = before - centre;
            _pan += vector * (1 - ratio);
        }
        _pan = ClampPan(_pan);
        InvalidateVisual();
        if (_selectionImage is not null) Dispatcher.UIThread.Post(InvalidateSelectionOverlay, DispatcherPriority.Render);
        RaiseViewChanged();
    }

    public ViewportScrollState GetScrollState()
    {
        if (Bitmap is not { } bitmap || _fitMode != FitMode.Manual) return default;
        var display = GetDisplayImageSize(CoordinateImageSize);
        var scale = GetScale();
        var overflowX = Math.Max(0, display.Width * scale - Bounds.Width);
        var overflowY = Math.Max(0, display.Height * scale - Bounds.Height);
        return new ViewportScrollState(
            overflowX > 0.5, overflowX, Math.Clamp(overflowX / 2 - _pan.X, 0, overflowX),
            overflowY > 0.5, overflowY, Math.Clamp(overflowY / 2 - _pan.Y, 0, overflowY));
    }

    public void SetHorizontalScrollOffset(double value)
    {
        if (Bitmap is not { } bitmap || _fitMode != FitMode.Manual) return;
        var display = GetDisplayImageSize(CoordinateImageSize);
        var overflow = Math.Max(0, display.Width * GetScale() - Bounds.Width);
        if (overflow <= 0) return;
        _pan = new Vector(overflow / 2 - Math.Clamp(value, 0, overflow), _pan.Y);
        _pan = ClampPan(_pan);
        InvalidateVisual();
        if (_selectionImage is not null) Dispatcher.UIThread.Post(InvalidateSelectionOverlay, DispatcherPriority.Render);
        RaiseViewChanged();
    }

    public void SetVerticalScrollOffset(double value)
    {
        if (Bitmap is not { } bitmap || _fitMode != FitMode.Manual) return;
        var display = GetDisplayImageSize(CoordinateImageSize);
        var overflow = Math.Max(0, display.Height * GetScale() - Bounds.Height);
        if (overflow <= 0) return;
        _pan = new Vector(_pan.X, overflow / 2 - Math.Clamp(value, 0, overflow));
        _pan = ClampPan(_pan);
        InvalidateVisual();
        if (_selectionImage is not null) Dispatcher.UIThread.Post(InvalidateSelectionOverlay, DispatcherPriority.Render);
        RaiseViewChanged();
    }

    public bool ClearSelection()
    {
        if (_selectionImage is null && !_hoverSelection) return false;
        _selectionImage = null;
        _selectionViewRect = null;
        _hoverSelection = false;
        Cursor = null;
        InvalidateSelectionOverlayInteractive(force: true);
        RaiseViewChanged();
        return true;
    }

    private void RequestCleanRedraw(bool force = false)
    {
        // Pointer movement can arrive far faster than the compositor paints. Queueing a second
        // render-priority invalidation for every event created an avoidable backlog and visible
        // selection trails. Cap interactive overlay invalidation at roughly one frame per 7 ms;
        // this keeps high-refresh interaction fluid while still coalescing redundant mouse events.
        // release/cancel paths force the final clean frame.
        var now = Environment.TickCount64;
        if (!force && now - _lastInteractiveInvalidateTicks < 7) return;
        _lastInteractiveInvalidateTicks = now;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_suppressBackgroundFill && Background is { } background) context.FillRectangle(background, Bounds);
        var frame = _presentedFrame;
        var interaction = _interactionFrame;
        var bitmapFrame = _interactionBitmapActive && interaction.Bitmap is not null ? interaction : frame;
        var bitmap = bitmapFrame.Bitmap;
        InteractionDiagnostic?.Invoke("viewport.render_begin", new
        { request = frame.RequestId, bitmap = bitmap is null ? (int?)null : RuntimeHelpers.GetHashCode(bitmap) });
        if (bitmap is null || bitmapFrame.BitmapSize.Width <= 0 || bitmapFrame.BitmapSize.Height <= 0 ||
            bitmapFrame.SourceSize.Width <= 0 || bitmapFrame.SourceSize.Height <= 0)
        {
            _lastImageDest = default;
            return;
        }
        try
        {
            var source = new Rect(bitmapFrame.BitmapSize);
            var coordinateSize = frame.SourceSize;
            var scale = GetScale();
            var displaySize = GetDisplayImageSize(coordinateSize);
            var width = displaySize.Width * scale;
            var height = displaySize.Height * scale;
            _pan = ClampPan(_pan);
            var left = (Bounds.Width - width) / 2 + _pan.X;
            var top = (Bounds.Height - height) / 2 + _pan.Y;
            _lastImageDest = new Rect(left, top, width, height);

            using (context.PushTransform(BuildImageToViewMatrix(bitmapFrame.BitmapSize, _lastImageDest)))
                context.DrawImage(bitmap, source, source);

            if (frame.RequestId != 0) PresentationDrawRecorded?.Invoke(frame.RequestId);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException or InvalidOperationException)
        {
            InteractionDiagnostic?.Invoke("viewport.render_skipped_invalid_frame", new
            { request = frame.RequestId, error = ex.GetType().Name, message = ex.Message });
        }
    }

    public void RotateLeft()
    {
        _quarterTurns = (_quarterTurns + 3) % 4;
        _pan = default;
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void RotateRight()
    {
        _quarterTurns = (_quarterTurns + 1) % 4;
        _pan = default;
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void FlipHorizontal()
    {
        _flipHorizontal = !_flipHorizontal;
        _pan = default;
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void FlipVertical()
    {
        _flipVertical = !_flipVertical;
        _pan = default;
        InvalidateVisual();
        RaiseViewChanged();
    }

    private Rect PixelSnap(Rect rect)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        scaling = Math.Max(1.0, scaling);
        double Snap(double v) => Math.Round(v * scaling) / scaling;
        var left = Snap(rect.Left);
        var top = Snap(rect.Top);
        var right = Snap(rect.Right);
        var bottom = Snap(rect.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private bool CanPanImage()
    {
        if (Bitmap is not { } bitmap) return false;
        var display = GetDisplayImageSize(CoordinateImageSize);
        var scale = GetScale();
        // Ownership is based on what the user can actually see, not the internal fit-mode enum.
        // Fit-width/fit-height can themselves crop one axis; in that case right-hold must pan the
        // picture, and BeginPan converts the current presentation scale to manual mode seamlessly.
        return display.Width * scale > Bounds.Width + 0.5 || display.Height * scale > Bounds.Height + 0.5;
    }

    private bool ShouldPanOnRightDrag()
    {
        if (!CanPanImage()) return false;
        return RightDragMode is RightImageDragMode.Smart or RightImageDragMode.PanImageOnly;
    }

    private bool ShouldMoveWindowOnRightDrag()
    {
        return RightDragMode switch
        {
            RightImageDragMode.MoveWindow => RightDragWindowMoveAllowed,
            RightImageDragMode.Smart => !CanPanImage() && RightDragWindowMoveAllowed,
            _ => false
        };
    }

    private Vector ClampPan(Vector candidate)
    {
        if (Bitmap is not { } bitmap || _fitMode != FitMode.Manual) return default;
        var display = GetDisplayImageSize(CoordinateImageSize);
        var scale = GetScale();
        var halfOverflowX = Math.Max(0, display.Width * scale - Bounds.Width) / 2;
        var halfOverflowY = Math.Max(0, display.Height * scale - Bounds.Height) / 2;
        return new Vector(
            Math.Clamp(candidate.X, -halfOverflowX, halfOverflowX),
            Math.Clamp(candidate.Y, -halfOverflowY, halfOverflowY));
    }

    private double GetScale()
    {
        var bitmap = Bitmap;
        if (bitmap is null) return 1;
        var display = GetDisplayImageSize(CoordinateImageSize);
        var w = Math.Max(1, display.Width);
        var h = Math.Max(1, display.Height);
        return _fitMode switch
        {
            FitMode.FitWidth => Math.Min(32, Bounds.Width / w),
            FitMode.FitHeight => Math.Min(32, Bounds.Height / h),
            FitMode.FitImage => Math.Min(1.0, Math.Min(Bounds.Width / w, Bounds.Height / h)),
            _ => _zoom
        };
    }

    private Size GetDisplayImageSize(Size imageSize) => (_quarterTurns & 1) == 0
        ? imageSize
        : new Size(imageSize.Height, imageSize.Width);

    private Point ImageToDisplay(Point p, Size imageSize)
    {
        var rotatedSize = GetDisplayImageSize(imageSize);
        Point rotated = _quarterTurns switch
        {
            1 => new Point(imageSize.Height - p.Y, p.X),
            2 => new Point(imageSize.Width - p.X, imageSize.Height - p.Y),
            3 => new Point(p.Y, imageSize.Width - p.X),
            _ => p
        };
        if (_flipHorizontal) rotated = new Point(rotatedSize.Width - rotated.X, rotated.Y);
        if (_flipVertical) rotated = new Point(rotated.X, rotatedSize.Height - rotated.Y);
        return rotated;
    }

    private Matrix BuildImageToViewMatrix(Size imageSize, Rect imageDest)
    {
        var p0 = ImageToDisplay(new Point(0, 0), imageSize);
        var px = ImageToDisplay(new Point(1, 0), imageSize);
        var py = ImageToDisplay(new Point(0, 1), imageSize);
        var sx = imageDest.Width / Math.Max(1, GetDisplayImageSize(imageSize).Width);
        var sy = imageDest.Height / Math.Max(1, GetDisplayImageSize(imageSize).Height);
        return new Matrix(
            (px.X - p0.X) * sx,
            (px.Y - p0.Y) * sy,
            (py.X - p0.X) * sx,
            (py.Y - p0.Y) * sy,
            imageDest.X + p0.X * sx,
            imageDest.Y + p0.Y * sy);
    }

    private string ResolveGesture(string slotId) => GestureCatalog.ResolveAction(slotId, GestureBindings);

    private bool DispatchGestureAction(string actionId, Point position)
    {
        actionId = GestureCatalog.NormalizeAction(actionId);
        switch (actionId)
        {
            case GestureCatalog.Default:
                return false;
            case GestureCatalog.Nothing:
                return true;
            case GestureCatalog.ContextMenu:
                ContextMenuRequested?.Invoke(position);
                return true;
            case GestureCatalog.ClearSelection:
                ClearSelection();
                return true;
            default:
                GestureActionRequested?.Invoke(this, new GestureActionRequestedEventArgs(actionId, position));
                return true;
        }
    }

    private bool DispatchPressedGesture(string actionId, PointerPressedEventArgs e, Point position, bool rightButton = false)
    {
        actionId = GestureCatalog.NormalizeAction(actionId);
        switch (actionId)
        {
            case GestureCatalog.Default:
                return false;
            case GestureCatalog.Nothing:
                e.Handled = true;
                return true;
            case GestureCatalog.Pan:
                BeginPan(e, position, rightButton);
                return true;
            case GestureCatalog.CreateSelection:
                if (Bitmap is null || !_lastImageDest.Contains(position)) { e.Handled = true; return true; }
                _selecting = true;
                _selectionStartImage = ViewToImage(position);
                _selectionImage = new Rect(_selectionStartImage, new Size(0, 0));
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                InvalidateSelectionOverlay();
                e.Handled = true;
                return true;
            case GestureCatalog.MoveWindow:
                if (BackgroundWindowDragEnabled) NativeWindowDragRequested?.Invoke(this, e);
                e.Handled = true;
                return true;
            default:
                if (DispatchGestureAction(actionId, position))
                {
                    e.Handled = true;
                    return true;
                }
                return false;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Bitmap is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var slot = IsFullscreen
            ? (ctrl ? "wheel.fullscreenCtrl" : "wheel.fullscreen")
            : (ctrl ? "wheel.windowedCtrl" : "wheel.windowed");
        var configured = ResolveGesture(slot);
        if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
        {
            var position = e.GetPosition(this);
            if (configured == GestureCatalog.ZoomIn || configured == GestureCatalog.ZoomOut)
            {
                var configuredZoomIn = configured == GestureCatalog.ZoomIn;
                ZoomBy(configuredZoomIn ? 1.12 : 1 / 1.12, position);
                e.Handled = true;
                return;
            }
            if (DispatchGestureAction(configured, position))
            {
                e.Handled = true;
                return;
            }
        }

        var direction = e.Delta.Y > 0 ? -1 : 1;
        if (InvertWheelDirection) direction *= -1;

        var shouldZoom = WindowedWheelZoom || (ctrl && CtrlWheelZoomEnabled);
        if (!shouldZoom)
        {
            WheelBrowseRequested?.Invoke(this, direction);
            e.Handled = true;
            return;
        }

        var zoomIn = e.Delta.Y > 0;
        if (InvertWheelDirection) zoomIn = !zoomIn;
        var factor = zoomIn ? 1.12 : 1 / 1.12;
        ZoomBy(factor, e.GetPosition(this));
        e.Handled = true;
    }

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;

    private static (bool Left, bool Right, bool Middle) ReadPressedButtons(PointerPoint point)
    {
        var kind = point.Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed) return (true, false, false);
        if (kind == PointerUpdateKind.RightButtonPressed) return (false, true, false);
        if (kind == PointerUpdateKind.MiddleButtonPressed) return (false, false, true);

        return (point.Properties.IsLeftButtonPressed, point.Properties.IsRightButtonPressed, point.Properties.IsMiddleButtonPressed);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var pressed = ReadPressedButtons(point);
        _pressPoint = point.Position;
        if (pressed.Left) _leftPressTicks = Environment.TickCount64;
        _leftPressOnImage = pressed.Left && Bitmap is not null && _lastImageDest.Contains(point.Position);
        // In fullscreen the entire viewer surface owns plain right-click navigation, including
        // letterbox/pillarbox space. Overlay tunnel routing runs before this control and still owns
        // overlay right-clicks, so broadening the canvas hit here cannot steal overlay menus.
        _rightPressOnImage = pressed.Right && Bitmap is not null && (IsFullscreen || _lastImageDest.Contains(point.Position));
        _rightPressOnSelection = _rightPressOnImage && Bitmap is not null && IsPointInsideSelection(point.Position);
        _middlePressOnImage = pressed.Middle && Bitmap is not null && _lastImageDest.Contains(point.Position);

        // Slideshow owns a viewer right-click only while it is actively playing and the user has
        // enabled this session option. Handle it before selection/pan/context-menu ownership so a
        // slideshow stop can never also open a normal viewer menu or zoom a retained selection.
        if (pressed.Right && SlideshowRightClickStopEnabled)
        {
            SlideshowRightClickStopRequested?.Invoke();
            _rightPressOnImage = false;
            _rightPressOnSelection = false;
            e.Handled = true;
            return;
        }

        // Avalonia exposes a generic DoubleTapped event, but the legacy gesture matrix distinguishes
        // double-right from double-left. Detect double-right from physical presses so that slot is not
        // a decorative setting. Keep a tight spatial/time threshold to avoid turning a slow pan into it.
        if (!IsFullscreen && _rightPressOnImage)
        {
            var nowTicks = Environment.TickCount64;
            var closeInTime = nowTicks - _lastRightClickTicks is >= 0 and <= 500;
            var closeInSpace = Math.Abs(point.Position.X - _lastRightClickPoint.X) <= 5 && Math.Abs(point.Position.Y - _lastRightClickPoint.Y) <= 5;
            _lastRightClickTicks = nowTicks;
            _lastRightClickPoint = point.Position;
            if (closeInTime && closeInSpace)
            {
                var configuredDoubleRight = ResolveGesture("windowed.doubleRightImage");
                if (!string.Equals(configuredDoubleRight, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
                    DispatchPressedGesture(configuredDoubleRight, e, point.Position, rightButton: true))
                {
                    _rightPressOnImage = false;
                    return;
                }
            }
        }

        // Fullscreen click navigation has one authoritative route here.  It runs before selection/pan
        // so monitor-edge clicks cannot also fall through into another action path.
        if (IsFullscreen && Bitmap is not null)
        {
            var fullscreenSlot = pressed.Left ? "fullscreen.leftClickImage"
                : pressed.Right ? "fullscreen.rightClickImage"
                : pressed.Middle ? "fullscreen.middleClickImage" : null;
            if (fullscreenSlot is not null)
            {
                var configured = ResolveGesture(fullscreenSlot);
                if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
                    DispatchPressedGesture(configured, e, point.Position, pressed.Right)) return;
            }
            if (FullscreenClickNavigationEnabled && pressed.Left)
            {
                // Fullscreen mouse-button navigation is deliberately unambiguous: left-click
                // advances and right-click (handled on release below) goes backwards. The older
                // half-of-screen rule made the result depend on pointer position and conflicted
                // with overlay routing.
                BrowseRequested?.Invoke(this, 1);
                e.Handled = true;
                return;
            }
            // Right-click navigation is deliberately deferred until release. This gives right-drag
            // enough time to become a pan on cropped/zoomed content, or a no-op drag on a fitted
            // image, without accidentally navigating backwards on the initial button press.
        }

        var overImage = Bitmap is not null && (_lastImageDest.Contains(point.Position) || (IsFullscreen && pressed.Right));
        if (!overImage && (pressed.Left || pressed.Right))
        {
            var slot = pressed.Left ? "drag.leftEmpty" : "drag.rightEmpty";
            var configured = ResolveGesture(slot);
            if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
            {
                DispatchPressedGesture(configured, e, point.Position, rightButton: pressed.Right);
                return;
            }
            // Only primary-button empty-space drag moves the window. Secondary-button movement was
            // stealing normal image/context-menu gestures whenever destination hit-testing was even
            // slightly outside the rendered rectangle. Right click is reserved for Glide commands.
            if (pressed.Left && BackgroundWindowDragEnabled)
            {
                NativeWindowDragRequested?.Invoke(this, e);
                e.Handled = true;
            }
            else if (pressed.Right)
            {
                ContextMenuRequested?.Invoke(point.Position);
                e.Handled = true;
            }
            return;
        }

        if (Bitmap is null) return;

        if (_rightPressOnSelection)
        {
            var configured = ResolveGesture("selection.rightClick");
            if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
                DispatchPressedGesture(configured, e, point.Position, rightButton: true))
            {
                _rightPressOnSelection = false;
                _rightPressOnImage = false;
                return;
            }

            // The retained selection owns a plain right-button press immediately. Do not defer this
            // action to PointerReleased: Windows/Avalonia/native context-menu routing can consume or
            // reorder the release path, which made selection zoom-out unreliable even though the
            // rectangle and hit-test were valid. This mirrors the legacy contract: once a selection
            // exists, left click inside zooms in and right click inside zooms out. Right-drag pan
            // remains available everywhere outside the active selection.
            if (SelectionRightClickZoomOutEnabled && _selectionImage is { } selected)
            {
                // Cursor feedback is deliberately independent from the selection lifetime. Zoom-out
                // consumes the retained rectangle immediately on press, but legacy Glide keeps the
                // native minus-magnifier visible for as long as the physical right button is held.
                // Do not introduce pointer capture or delay the working press-to-zoom action.
                SetSelectionZoomOutCursorPressed(true);
                InteractionDiagnostic?.Invoke("selection_zoom_out", new
                {
                    selected.X,
                    selected.Y,
                    selected.Width,
                    selected.Height,
                    beforeZoomPercent = ZoomPercent,
                    trigger = "right_press"
                });
                _selectionImage = null;
                _selectionViewRect = null;
                _hoverSelection = false;
                Cursor = null;
                EndInteractiveQuality();
                InvalidateSelectionOverlay();
                ZoomOutFromSelection(selected);
                _rightPressOnSelection = false;
                _rightPressOnImage = false;
                e.Handled = true;
                return;
            }
        }

        if (pressed.Right)
        {
            var configured = ResolveGesture("drag.rightImage");
            if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
                DispatchPressedGesture(configured, e, point.Position, rightButton: true))
            {
                // A custom press-owned gesture must not later fall through into the default
                // stationary right-click context menu. Pan keeps ownership until release.
                if (!_panning) _rightPressOnImage = false;
                return;
            }
            if (ShouldPanOnRightDrag())
            {
                BeginPan(e, point.Position, rightButton: true);
                return;
            }
            // Do not hand a plain right press directly to native window dragging. A stationary
            // press+release outside a selection is Glide's context-menu gesture; only promote it to
            // a window drag after the pointer actually crosses the click threshold in OnMoved.
            e.Pointer.Capture(this);
            _capturedPointer = e.Pointer;
            e.Handled = true;
            return;
        }

        if (pressed.Middle)
        {
            var configured = ResolveGesture("drag.middleImage");
            if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
                DispatchPressedGesture(configured, e, point.Position)) return;
            if (MiddleDragPanEnabled)
            {
                BeginPan(e, point.Position, rightButton: false);
                return;
            }
        }

        if (!pressed.Left || !_lastImageDest.Contains(point.Position)) return;

        if (_selectionImage is { } selection && ImageRectToView(selection, _lastImageDest, CoordinateImageSize).Contains(point.Position))
        {
            var configured = ResolveGesture("selection.leftClick");
            if (!string.Equals(configured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
                DispatchPressedGesture(configured, e, point.Position)) return;
            if (SelectionClickZoomEnabled)
            {
                // A selection is a one-shot zoom command. After zoom-in the rectangle disappears;
                // zooming out later requires drawing a new selection and right-clicking inside it.
                InteractionDiagnostic?.Invoke("selection_zoom_in", new { selection.X, selection.Y, selection.Width, selection.Height, beforeZoomPercent = ZoomPercent });
                _selectionImage = null;
                _selectionViewRect = null;
                _hoverSelection = false;
                Cursor = null;
                EndInteractiveQuality();
                InvalidateSelectionOverlay();
                ZoomToSelection(selection);
                e.Handled = true;
                return;
            }
        }

        var leftDragConfigured = ResolveGesture("drag.leftImage");
        if (!string.Equals(leftDragConfigured, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase) &&
            DispatchPressedGesture(leftDragConfigured, e, point.Position)) return;

        switch (LeftDragMode)
        {
            case LeftImageDragMode.Pan:
                BeginPan(e, point.Position, rightButton: false);
                break;
            case LeftImageDragMode.MoveWindow:
                if (BackgroundWindowDragEnabled)
                {
                    NativeWindowDragRequested?.Invoke(this, e);
                    e.Handled = true;
                }
                break;
            default:
                _selecting = true;
                // Selection is overlay-only. Do not swap the base bitmap or interpolation mode here.
                // Cache the inverse transform once so every mouse-move avoids rebuilding/inverting it.
                _selectionViewToImage = TryBuildViewToImageMatrix();
                _selectionStartView = ClampToImageView(point.Position);
                _selectionStartImage = ViewToImage(_selectionStartView);
                _selectionImage = new Rect(_selectionStartImage, new Size(0, 0));
                _selectionViewRect = new Rect(_selectionStartView, new Size(0, 0));
                SelectionOverlayTarget?.SetSelection(_selectionViewRect, _accentColor);
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                e.Handled = true;
                break;
        }
    }

    private void BeginPan(PointerPressedEventArgs e, Point position, bool rightButton)
    {
        _panStartView = CaptureViewState();
        _panStartPointer = position;
        _panMoved = false;
        _panStartedTicks = Environment.TickCount64;
        _panUpdateCount = 0;
        _panTravelPixels = 0;
        _rightPanGesture = rightButton;
        _panning = true;
        BeginInteractiveQuality();
        // Preserve the exact pre-pan presentation scale before switching to manual mode.
        // Reading GetScale() after setting Manual would reuse the stale _zoom field and could
        // make the image jump on the first middle/left pan from a fit mode.
        var presentationScale = GetScale();
        _fitMode = FitMode.Manual;
        _zoom = presentationScale;
        _lastPointer = position;
        e.Pointer.Capture(this);
        _capturedPointer = e.Pointer;
        Cursor = _handCursor;
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var pointer = e.GetPosition(this);
        _hoverPointer = pointer;

        // A stationary right press outside a selection is reserved for the context menu. Promote
        // it to window dragging only after real movement, so holding/dragging can never also open
        // the menu on release. Cropped images enter the dedicated pan path earlier in OnPressed.
        if (_rightPressOnImage && !_rightPressOnSelection && !_panning &&
            e.GetCurrentPoint(this).Properties.IsRightButtonPressed &&
            (Math.Abs(pointer.X - _pressPoint.X) > 3 || Math.Abs(pointer.Y - _pressPoint.Y) > 3))
        {
            ReleaseCapture();
            _rightPressOnImage = false;
            if (BackgroundWindowDragEnabled && ShouldMoveWindowOnRightDrag())
                DeferredRightWindowDragRequested?.Invoke(e.Pointer, _pressPoint, pointer);
            // Even when the configured policy resolves to no movement, crossing the drag threshold
            // consumes the gesture so release cannot accidentally become a context-menu click.
            e.Handled = true;
            return;
        }

        // A right press inside a selection on a non-cropped image is ambiguous until movement:
        // stationary release means one-shot zoom-out; a real hold+drag means move the native
        // window. Defer ownership until the pointer crosses the same 3 px click threshold.
        if (_rightPressOnSelection && !_panning && e.GetCurrentPoint(this).Properties.IsRightButtonPressed &&
            (Math.Abs(pointer.X - _pressPoint.X) > 3 || Math.Abs(pointer.Y - _pressPoint.Y) > 3))
        {
            if (ShouldPanOnRightDrag())
            {
                // Promote the pending selection right-press to a pan only after real movement. Seed the
                // pan at the original press point and consume this event's full delta immediately.
                _panStartView = CaptureViewState();
                _panStartPointer = _pressPoint;
                _lastPointer = _pressPoint;
                _panMoved = true;
                _panStartedTicks = Environment.TickCount64;
                _panUpdateCount = 0;
                _panTravelPixels = 0;
                _rightPanGesture = true;
                _panning = true;
                // Capture only once the 3 px threshold has proven this is a drag/pan rather than the
                // stationary selection right-click that owns proportional zoom-out.
                e.Pointer.Capture(this);
                _capturedPointer = e.Pointer;
                BeginInteractiveQuality();
                var presentationScale = GetScale();
                _fitMode = FitMode.Manual;
                _zoom = presentationScale;
                Cursor = _handCursor;

                var delta = pointer - _lastPointer;
                _pan = ClampPan(_pan + delta);
                _lastPointer = pointer;
                _panUpdateCount++;
                _panTravelPixels += Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
                RequestCleanRedraw();
                if (_selectionImage is not null) Dispatcher.UIThread.Post(InvalidateSelectionOverlay, DispatcherPriority.Render);
                RaiseViewChangedInteractive();
                e.Handled = true;
                return;
            }

            ReleaseCapture();
            _rightPressOnSelection = false;
            _rightPressOnImage = false;
            _hoverSelection = false;
            Cursor = null;
            if (BackgroundWindowDragEnabled && ShouldMoveWindowOnRightDrag())
                DeferredRightWindowDragRequested?.Invoke(e.Pointer, _pressPoint, pointer);
            e.Handled = true;
            return;
        }

        if (_panning)
        {
            if (!_panMoved && (Math.Abs(pointer.X - _panStartPointer.X) > 3 || Math.Abs(pointer.Y - _panStartPointer.Y) > 3)) _panMoved = true;
            if (_panMoved)
            {
                var delta = pointer - _lastPointer;
                _pan += delta;
                _pan = ClampPan(_pan);
                _panUpdateCount++;
                _panTravelPixels += Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
                // Keep image motion on the cheapest possible path. The compositor can repaint at the
                // pointer/display cadence while status text, title and scrollbar synchronization are
                // throttled independently.
                RequestCleanRedraw();
                if (_selectionImage is not null) Dispatcher.UIThread.Post(InvalidateSelectionOverlay, DispatcherPriority.Render);
                RaiseViewChangedInteractive();
            }
            _lastPointer = pointer;
            e.Handled = true;
            return;
        }

        if (_selecting && Bitmap is not null)
        {
            var clampedView = ClampToImageView(pointer);
            var now = ViewToImage(clampedView);
            _selectionImage = NormalizeAndClip(_selectionStartImage, now, CoordinateImageSize);
            _selectionViewRect = PixelSnap(NormalizeViewRect(_selectionStartView, clampedView));
            _hoverSelection = false;
            InvalidateSelectionOverlayInteractive();
            e.Handled = true;
            return;
        }

        _hoverSelection = Bitmap is not null && IsPointInsideSelection(pointer);
        // Hover is semantic state only. The visible +/- magnifier is now owned by Win32 WM_SETCURSOR,
        // matching legacy 1.2.125, so pointer movement never invalidates an Avalonia cursor visual.
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Pure visual feedback reset. This must not participate in gesture ownership/capture.
        SetSelectionZoomOutCursorPressed(false);
        var completedPanInteraction = false;
        if (_panning)
        {
            completedPanInteraction = true;
            var wasRightGesture = _rightPanGesture;
            var wasRightClick = wasRightGesture && !_panMoved;
            var clickPosition = e.GetPosition(this);
            var restore = _panStartView;
            _panning = false;
            _rightPanGesture = false;
            Cursor = null;
            EndInteractiveQuality();
            ReleaseCapture();

            if (wasRightClick)
            {
                if (_rightPressOnSelection && _selectionImage is { } selected && SelectionRightClickZoomOutEnabled)
                {
                    // Right-clicking a newly drawn selection is a one-shot zoom-out command.
                    // The rectangle disappears exactly like a left-click zoom-in selection.
                    InteractionDiagnostic?.Invoke("selection_zoom_out", new { selected.X, selected.Y, selected.Width, selected.Height, beforeZoomPercent = ZoomPercent });
                    _selectionImage = null;
                    _selectionViewRect = null;
                    _hoverSelection = false;
                    EndInteractiveQuality();
                    InvalidateSelectionOverlay();
                    ZoomOutFromSelection(selected);
                }
                else
                {
                    if (restore is { } prior) RestoreViewState(prior);
                    var configuredClick = ResolveGesture(IsFullscreen ? "fullscreen.rightClickImage" : "windowed.rightClickImage");
                    if (string.Equals(configuredClick, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
                    {
                        // A stationary fullscreen right-click is always Previous, even when the
                        // press entered the right-drag pan candidate path. Actual movement still pans.
                        if (IsFullscreen && FullscreenClickNavigationEnabled) BrowseRequested?.Invoke(this, -1);
                        else ContextMenuRequested?.Invoke(clickPosition);
                    }
                    else
                        DispatchGestureAction(configuredClick, clickPosition);
                }
            }
            else
            {
                // Pan rendering was intentionally decoupled from dependent UI work. Flush the final
                // status/title/scrollbar state when the physical drag ends.
                RaiseViewChanged();
                var elapsedMs = Math.Max(1, Environment.TickCount64 - _panStartedTicks);
                InteractionDiagnostic?.Invoke("pan_completed", new
                {
                    button = wasRightGesture ? "right" : "other",
                    elapsedMs,
                    pointerUpdates = _panUpdateCount,
                    pointerUpdatesPerSecond = Math.Round(_panUpdateCount * 1000.0 / elapsedMs, 1),
                    travelPixels = Math.Round(_panTravelPixels, 1),
                    zoomPercent = ZoomPercent
                });
            }

            _rightPressOnImage = false;
            e.Handled = true;
        }

        // When the image is not currently cropped there is nothing to pan, but a right-click inside
        // a selection still owns zoom-out. Capture/release lets us distinguish a real click from an
        // accidental move without handing the gesture to native window dragging.
        if (!completedPanInteraction && _rightPressOnSelection && SelectionRightClickZoomOutEnabled && _selectionImage is { } clickSelection)
        {
            var release = e.GetPosition(this);
            var stationary = Math.Abs(release.X - _pressPoint.X) <= 3 && Math.Abs(release.Y - _pressPoint.Y) <= 3;
            var stillInside = Bitmap is not null && IsPointInsideSelection(release);
            ReleaseCapture();
            if (stationary && stillInside)
            {
                InteractionDiagnostic?.Invoke("selection_zoom_out", new { clickSelection.X, clickSelection.Y, clickSelection.Width, clickSelection.Height, beforeZoomPercent = ZoomPercent });
                _selectionImage = null;
                _selectionViewRect = null;
                _hoverSelection = false;
                Cursor = null;
                EndInteractiveQuality();
                InvalidateSelectionOverlay();
                ZoomOutFromSelection(clickSelection);
                e.Handled = true;
            }
            else
            {
                // Keep the selection for a later deliberate action. Pointer shape remains unchanged.
                _hoverSelection = stillInside;
                InvalidateSelectionOverlay();
            }
        }

        if (!completedPanInteraction && _rightPressOnImage && !_rightPressOnSelection)
        {
            var release = e.GetPosition(this);
            var stationary = Math.Abs(release.X - _pressPoint.X) <= 3 && Math.Abs(release.Y - _pressPoint.Y) <= 3;
            ReleaseCapture();
            if (stationary)
            {
                var configuredClick = ResolveGesture(IsFullscreen ? "fullscreen.rightClickImage" : "windowed.rightClickImage");
                if (string.Equals(configuredClick, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsFullscreen && FullscreenClickNavigationEnabled) BrowseRequested?.Invoke(this, -1);
                    else ContextMenuRequested?.Invoke(release);
                }
                else
                    DispatchGestureAction(configuredClick, release);
            }
            _rightPressOnImage = false;
            e.Handled = true;
        }

        if (_selecting)
        {
            var tiny = CompleteSelectionInteraction();
            ReleaseCapture();
            if (tiny && _leftPressOnImage)
            {
                var configuredClick = ResolveGesture("windowed.leftClickImage");
                if (!string.Equals(configuredClick, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
                    DispatchGestureAction(configuredClick, e.GetPosition(this));
            }
            _leftPressOnImage = false;
            e.Handled = true;
        }
        else if (_middlePressOnImage)
        {
            var configuredClick = ResolveGesture(IsFullscreen ? "fullscreen.middleClickImage" : "windowed.middleClickImage");
            if (!string.Equals(configuredClick, GestureCatalog.Default, StringComparison.OrdinalIgnoreCase))
            {
                DispatchGestureAction(configuredClick, e.GetPosition(this));
                e.Handled = true;
            }
            _middlePressOnImage = false;
        }

        _leftPressOnImage = false;
        _rightPressOnImage = false;
        _rightPressOnSelection = false;
        SetSelectionZoomOutCursorPressed(false);
        _middlePressOnImage = false;
    }

    private void ReleaseCapture()
    {
        var pointer = _capturedPointer;
        _capturedPointer = null;
        if (pointer is null) return;

        _releasingPointerCapture = true;
        try
        {
            pointer.Capture(null);
        }
        finally
        {
            _releasingPointerCapture = false;
        }
    }

    private Matrix? TryBuildViewToImageMatrix()
    {
        if (Bitmap is null || _lastImageDest.Width <= 0 || _lastImageDest.Height <= 0) return null;
        var matrix = BuildImageToViewMatrix(CoordinateImageSize, _lastImageDest);
        return matrix.TryInvert(out var inverse) ? inverse : null;
    }

    private Point ViewToImage(Point p)
    {
        var coordinateSize = CoordinateImageSize;
        var inverse = _selecting && _selectionViewToImage is { } cached
            ? cached
            : TryBuildViewToImageMatrix();
        if (inverse is not { } viewToImage) return default;
        var image = viewToImage.Transform(p);
        return new Point(
            Math.Clamp(image.X, 0, coordinateSize.Width),
            Math.Clamp(image.Y, 0, coordinateSize.Height));
    }

    private Point ClampToImageView(Point point)
    {
        if (_lastImageDest.Width <= 0 || _lastImageDest.Height <= 0) return point;
        return new Point(
            Math.Clamp(point.X, _lastImageDest.Left, _lastImageDest.Right),
            Math.Clamp(point.Y, _lastImageDest.Top, _lastImageDest.Bottom));
    }

    private static Rect NormalizeViewRect(Point a, Point b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X, b.X);
        var bottom = Math.Max(a.Y, b.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect NormalizeAndClip(Point a, Point b, Size imageSize)
    {
        var left = Math.Clamp(Math.Min(a.X, b.X), 0, imageSize.Width);
        var top = Math.Clamp(Math.Min(a.Y, b.Y), 0, imageSize.Height);
        var right = Math.Clamp(Math.Max(a.X, b.X), 0, imageSize.Width);
        var bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, imageSize.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private Rect ImageRectToView(Rect imageRect, Rect imageDest, Size imageSize)
    {
        var matrix = BuildImageToViewMatrix(imageSize, imageDest);
        var points = new[]
        {
            matrix.Transform(new Point(imageRect.Left, imageRect.Top)),
            matrix.Transform(new Point(imageRect.Right, imageRect.Top)),
            matrix.Transform(new Point(imageRect.Right, imageRect.Bottom)),
            matrix.Transform(new Point(imageRect.Left, imageRect.Bottom))
        };
        var left = points.Min(x => x.X); var top = points.Min(x => x.Y);
        var right = points.Max(x => x.X); var bottom = points.Max(x => x.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private Rect ImageRectToDisplay(Rect imageRect, Size imageSize)
    {
        var points = new[]
        {
            ImageToDisplay(new Point(imageRect.Left, imageRect.Top), imageSize),
            ImageToDisplay(new Point(imageRect.Right, imageRect.Top), imageSize),
            ImageToDisplay(new Point(imageRect.Right, imageRect.Bottom), imageSize),
            ImageToDisplay(new Point(imageRect.Left, imageRect.Bottom), imageSize)
        };
        var left = points.Min(x => x.X); var top = points.Min(x => x.Y);
        var right = points.Max(x => x.X); var bottom = points.Max(x => x.Y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void ZoomOutFromSelection(Rect selection)
    {
        var bitmap = Bitmap;
        if (bitmap is null || selection.Width <= 0 || selection.Height <= 0) return;

        var oldScale = GetScale();
        var viewSelection = ImageRectToView(selection, _lastImageDest, CoordinateImageSize);
        var widthFraction = Math.Clamp(viewSelection.Width / Math.Max(1, Bounds.Width), 0.02, 1.0);
        var heightFraction = Math.Clamp(viewSelection.Height / Math.Max(1, Bounds.Height), 0.02, 1.0);
        var selectionFraction = Math.Max(widthFraction, heightFraction);

        // Glide default: reverse proportional mapping so a smaller selection produces the stronger
        // zoom-out. Users may disable ReverseSelectionZoomOutScale to restore direct mapping. This
        // affects only selection-size scaling, never selection geometry or the one-shot input contract.
        var effectiveFraction = ReverseSelectionZoomOutScale ? 1.02 - selectionFraction : selectionFraction;
        effectiveFraction = Math.Clamp(effectiveFraction, 0.02, 1.0);
        var factor = 1.0 / (1.0 + effectiveFraction);
        _fitMode = FitMode.Manual;
        _zoom = Math.Clamp(oldScale * factor, 0.05, 32);

        // Anchor around the selected region's centre so zoom-out feels spatially stable.
        var anchor = new Point(viewSelection.X + viewSelection.Width / 2, viewSelection.Y + viewSelection.Height / 2);
        var ratio = _zoom / Math.Max(0.0001, oldScale);
        var centre = new Point(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        var vector = anchor - centre;
        _pan += vector * (1 - ratio);
        _pan = ClampPan(_pan);
        InvalidateVisual();
        RaiseViewChanged();
    }

    private void RaiseViewChangedInteractive()
    {
        var now = Environment.TickCount64;
        if (now - _lastInteractiveViewChangedTicks < 33) return;
        _lastInteractiveViewChangedTicks = now;
        RaiseViewChanged();
    }

    private void ZoomToSelection(Rect selection)
    {
        var bitmap = Bitmap;
        if (bitmap is null || selection.Width <= 0 || selection.Height <= 0) return;
        _fitMode = FitMode.Manual;
        var coordinateSize = CoordinateImageSize;
        var displaySize = GetDisplayImageSize(coordinateSize);
        var displaySelection = ImageRectToDisplay(selection, coordinateSize);
        _zoom = Math.Clamp(Math.Min(Bounds.Width / Math.Max(1, displaySelection.Width), Bounds.Height / Math.Max(1, displaySelection.Height)), 0.05, 32);
        var selectionCentre = new Point(displaySelection.X + displaySelection.Width / 2, displaySelection.Y + displaySelection.Height / 2);
        var imageCentre = new Point(displaySize.Width / 2, displaySize.Height / 2);
        _pan = new Vector((imageCentre.X - selectionCentre.X) * _zoom, (imageCentre.Y - selectionCentre.Y) * _zoom);
        _pan = ClampPan(_pan);
        InvalidateVisual();
        RaiseViewChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
    }

    private void RaiseViewChanged() => ViewChanged?.Invoke(this, EventArgs.Empty);
}
