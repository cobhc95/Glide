using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Diagnostics;
using Glide.Core;
using Glide.Core.Commands;
using Glide.Imaging;

namespace Glide.App.Controls;

/// <summary>
/// Window-in-Window overlays implemented using the legacy Glide rendering model: one surface,
/// plain state records, direct bitmap source/destination rectangles, and one invalidation per
/// interaction update. No per-overlay Avalonia controls/layout/transforms exist on the hot path.
/// </summary>
public sealed class WindowInWindowOverlayManager : IDisposable
{
    private sealed record Item(OverlayState State, Bitmap Bitmap);

    private sealed class AnimationState
    {
        public Vector Velocity;
        public double TurnElapsedSeconds;
        public long LastTick;
        public bool IsRunning;
        public string Region = "Anywhere";
        public int SpeedDipsPerSecond = 90;
        public int TurnIntervalMs = 2500;
        public int TurnAngleDegrees = 120;
        public bool PauseWhileInteracting = true;
        public bool AvoidOverlap = true;
        public long MotionRevision;
    }

    private enum ResizeMode { Proportional, WidthOnly, HeightOnly }

    private readonly LegacyOverlaySurface _surface;
    private readonly Control _inputHost;
    private readonly ImageLoadCoordinator _loader;
    private readonly List<Item> _items = new();
    private readonly DispatcherTimer _chromeIdleTimer;
    private readonly DispatcherTimer _nativeAnimationControlTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Guid? _selected;
    private Guid? _hovered;
    private Guid? _chromeVisibleId;
    private IPointer? _capturedPointer;
    private (Guid Id, Point Origin, double X, double Y)? _drag;
    // Defer fullscreen body presses until movement proves this is a drag; a plain
    // click must remain available to the viewer's fullscreen navigation handler.
    private (Guid Id, Point Origin, double X, double Y)? _pendingFullscreenDrag;
    private (Guid Id, Point Origin, double Width, double Height, double ContentScale, ResizeMode Mode)? _resize;
    private (Guid Id, Point Origin, double PanX, double PanY, bool Started)? _pan;
    private Guid? _opacityDrag;
    private Size _lastHostSize;
    private readonly Dictionary<Guid, AnimationState> _animations = new();
    private readonly HashSet<Bitmap> _retiredCompositionBitmaps = new(ReferenceEqualityComparer.Instance);
    private bool _animationFramePending;
    private long _lastAnimationRenderTick;
    private readonly Dictionary<(int Width2, int Height2), Dictionary<Guid, OverlayGeometry>> _geometrySnapshots = new();

    private readonly record struct OverlayGeometry(double X, double Y, double Width, double Height);

    public event Action<string, object?>? Diagnostic;
    public event Action? OverlayCountChanged;
    public double DefaultOpacity { get; set; } = 1.0;
    public bool WheelZoomEnabled { get; set; } = true;
    public bool CtrlWheelZoomEnabled { get; set; } = true;
    public bool RightDragPanEnabled { get; set; } = true;
    public bool HighlightSelected { get; set; } = true;
    public bool RememberZoom { get; set; } = true;
    public int ZoomStepPercent { get; set; } = 20;
    public bool AdaptivePositioningEnabled { get; set; } = true;
    public bool ScaleWithHostResize { get; set; } = true;
    public string AnimationRegion { get; set; } = "Anywhere";
    public int AnimationSpeedDipsPerSecond { get; set; } = 90;
    public int AnimationTurnIntervalMs { get; set; } = 2500;
    public int AnimationTurnAngleDegrees { get; set; } = 120;
    public bool AnimationPauseWhileInteracting { get; set; } = true;
    public bool AnimationAvoidOverlap { get; set; } = true;
    public int AnimationRefreshRateHz { get; set; } = 144;
    public Action<string>? OpenFileLocationRequested { get; set; }
    public Action<string>? OpenInNewTabRequested { get; set; }
    public Action<string>? OpenWithRequested { get; set; }
    public event Action<bool>? ContextMenuVisibilityChanged;
    public Func<Size, Rect?>? ImageRectForSize { get; set; }
    public IBrush AccentBrush { get; set; } = new SolidColorBrush(Color.Parse("#36B8F4"));
    public Func<string, string?>? ResolveGesture { get; set; }
    public Func<bool>? IsFullscreen { get; set; }
    /// <summary>Called once when a fullscreen overlay press is released without becoming a drag.</summary>
    public Action<int>? FullscreenNavigationRequested { get; set; }
    public IReadOnlyList<OverlayState> States => _items.Select(x => x.State).ToArray();
    public bool HasOverlays => _items.Count > 0;
    public bool HasSelection => _selected is Guid id && FindIndex(id) >= 0;
    public bool HasRunningAnimations => _animations.Values.Any(animation => animation.IsRunning);

    // Pointer capture is also used for a deferred fullscreen click. Capture by itself is not an
    // edit operation and must never pause native motion while the viewer decides whether the press
    // becomes navigation or a drag. Only an established drag/resize/pan/opacity edit is interactive.
    private bool IsPointerInteractionActive => _drag is not null || _resize is not null || _pan is { Started: true } || _opacityDrag is not null;

    public WindowInWindowOverlayManager(LegacyOverlaySurface surface, Control inputHost, ImageLoadCoordinator loader, Window owner)
    {
        _surface = surface;
        _inputHost = inputHost;
        _loader = loader;
        _surface.Manager = this;
        // Animated overlays need a presentation clock that is independent from Avalonia's main
        // compositor. A main-canvas bitmap upload can legitimately occupy Avalonia's render thread;
        // if overlays share that thread their motion pauses during image navigation. On Windows the
        // raw owned-HWND renderer therefore owns visible animation and motion timing. It remains
        // click-through and owned by Glide; this manager still owns all hit testing and gestures.
        _surface.EnableNativeWindowRenderer(owner);
        // The render surface is deliberately non-hit-testable. Overlay gestures are intercepted
        // at the viewer-container tunnel stage only when the pointer is actually over an overlay.
        // Empty-image-area input therefore continues to ImageViewport unchanged.
        _inputHost.AddHandler(InputElement.PointerMovedEvent, SurfacePointerMoved, RoutingStrategies.Tunnel, true);
        _inputHost.AddHandler(InputElement.PointerPressedEvent, SurfacePointerPressed, RoutingStrategies.Tunnel, true);
        _inputHost.AddHandler(InputElement.PointerReleasedEvent, SurfacePointerReleased, RoutingStrategies.Tunnel, true);
        _inputHost.AddHandler(InputElement.PointerWheelChangedEvent, SurfacePointerWheelChanged, RoutingStrategies.Tunnel, true);
        _inputHost.PointerCaptureLost += (_, _) => ClearPointerState();
        _inputHost.PointerExited += (_, _) =>
        {
            _hovered = null;
            if (!IsPointerInteractionActive) ScheduleChromeHide(immediate: true);
        };
        _inputHost.DoubleTapped += SurfaceDoubleTapped;
        _inputHost.SizeChanged += HostSizeChanged;
        _lastHostSize = _inputHost.Bounds.Size;

        _chromeIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _chromeIdleTimer.Tick += (_, _) =>
        {
            if (IsPointerInteractionActive) return;
            _chromeIdleTimer.Stop();
            _chromeVisibleId = null;
            Invalidate();
        };
        // Native windows own the visible 120/144+ Hz motion loop. This timer only advances turn
        // timing and velocity bookkeeping, so keep it off the high-rate path and out of the way of
        // navigation input and image presentation.
        _nativeAnimationControlTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _nativeAnimationControlTimer.Tick += (_, _) =>
        {
            if (_disposed || !_animations.Values.Any(x => x.IsRunning))
            {
                _nativeAnimationControlTimer.Stop();
                return;
            }
            AnimationFrame(TimeSpan.Zero);
        };
    }

    private void HostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        var next = _inputHost.Bounds.Size;
        var previous = _lastHostSize;
        _lastHostSize = next;
        if (previous.Width <= 0 || previous.Height <= 0 || next.Width <= 0 || next.Height <= 0) return;
        if (Math.Abs(previous.Width - next.Width) < 0.25 && Math.Abs(previous.Height - next.Height) < 0.25) return;
        if (_items.Count == 0) return;

        if (AdaptivePositioningEnabled)
        {
            SaveGeometrySnapshot(previous);
            // When proportional scaling is enabled, reflow from the immediately previous host size even
            // if this target size has an older snapshot. That keeps overlay dimensions visually relative
            // to the host while resizing/maximizing/fullscreen transitions. Turning the option off restores
            // the historical exact-geometry snapshot behaviour.
            if (ScaleWithHostResize) ReflowGeometry(previous, next);
            else if (!RestoreGeometrySnapshot(next)) ReflowGeometry(previous, next);
        }
        ClampAnimatedOverlaysToHost(next);
        Invalidate();
        Diagnostic?.Invoke("overlay_host_reflow", new
        {
            from = $"{previous.Width:F1}x{previous.Height:F1}",
            to = $"{next.Width:F1}x{next.Height:F1}",
            restoredExact = _geometrySnapshots.ContainsKey(SizeKey(next)),
            count = _items.Count
        });
    }

    private void SaveGeometrySnapshot(Size size)
    {
        var snapshot = new Dictionary<Guid, OverlayGeometry>();
        foreach (var item in _items)
            snapshot[item.State.Id] = new OverlayGeometry(item.State.X, item.State.Y, item.State.Width, item.State.Height);
        _geometrySnapshots[SizeKey(size)] = snapshot;
        // Bound history: normal/maximized/fullscreen plus a few manual-resize states are enough.
        if (_geometrySnapshots.Count > 12)
            _geometrySnapshots.Remove(_geometrySnapshots.Keys.First());
    }

    private bool RestoreGeometrySnapshot(Size size)
    {
        if (!_geometrySnapshots.TryGetValue(SizeKey(size), out var snapshot)) return false;
        var restoredAny = false;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (!snapshot.TryGetValue(item.State.Id, out var geometry)) continue;
            _items[i] = item with
            {
                State = item.State with
                {
                    X = geometry.X, Y = geometry.Y, Width = geometry.Width, Height = geometry.Height
                }
            };
            restoredAny = true;
        }
        return restoredAny;
    }

    private void ReflowGeometry(Size previous, Size next)
    {
        var oldWindow = new Rect(0, 0, previous.Width, previous.Height);
        var newWindow = new Rect(0, 0, next.Width, next.Height);
        var oldImage = ImageRectForSize?.Invoke(previous);
        var newImage = ImageRectForSize?.Invoke(next);

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var state = item.State;
            var centre = new Point(state.X + state.Width / 2, state.Y + state.Height / 2);
            var hasImageAnchors = oldImage.HasValue && newImage.HasValue &&
                                  oldImage.Value.Width > 1 && oldImage.Value.Height > 1 &&
                                  newImage.Value.Width > 1 && newImage.Value.Height > 1;
            var imageAnchored = hasImageAnchors &&
                                (oldImage!.Value.Contains(centre) || oldImage.Value.Intersects(new Rect(state.X, state.Y, state.Width, state.Height)));
            var oldAnchor = imageAnchored ? oldImage!.Value : oldWindow;
            var newAnchor = imageAnchored ? newImage!.Value : newWindow;

            var nx = oldAnchor.Width <= 0 ? 0.5 : (centre.X - oldAnchor.X) / oldAnchor.Width;
            var ny = oldAnchor.Height <= 0 ? 0.5 : (centre.Y - oldAnchor.Y) / oldAnchor.Height;
            var scale = ScaleWithHostResize
                ? Math.Clamp(Math.Min(newAnchor.Width / Math.Max(1.0, oldAnchor.Width), newAnchor.Height / Math.Max(1.0, oldAnchor.Height)), 0.05, 20.0)
                : 1.0;
            var newWidth = state.Width * scale;
            var newHeight = state.Height * scale;
            var requested = state with
            {
                X = newAnchor.X + nx * newAnchor.Width - newWidth / 2,
                Y = newAnchor.Y + ny * newAnchor.Height - newHeight / 2,
                Width = newWidth,
                Height = newHeight
            };
            SetState(i, FitFullyVisible(requested, next));
        }
    }

    private static OverlayState FitFullyVisible(OverlayState state, Size host)
    {
        if (host.Width <= 0 || host.Height <= 0) return state;
        var width = state.Width;
        var height = state.Height;
        if (width > host.Width || height > host.Height)
        {
            var scale = Math.Min(host.Width / Math.Max(1, width), host.Height / Math.Max(1, height));
            scale = Math.Clamp(scale, 0.05, 1.0);
            width *= scale;
            height *= scale;
        }
        var maxX = Math.Max(0, host.Width - width);
        var maxY = Math.Max(0, host.Height - height);
        return state with
        {
            X = Math.Clamp(state.X, 0, maxX),
            Y = Math.Clamp(state.Y, 0, maxY),
            Width = width,
            Height = height
        };
    }

    private static (int Width2, int Height2) SizeKey(Size size) =>
        ((int)Math.Round(size.Width * 2), (int)Math.Round(size.Height * 2));

    private void ClampAnimatedOverlaysToHost(Size host)
    {
        if (_animations.Count == 0 || host.Width <= 0 || host.Height <= 0) return;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (!_animations.TryGetValue(item.State.Id, out var animation)) continue;
            var bounds = AnimationBounds(item.State, host, animation.Region);
            SetState(i, item.State with
            {
                X = Math.Clamp(item.State.X, bounds.Left, bounds.Right),
                Y = Math.Clamp(item.State.Y, bounds.Top, bounds.Bottom)
            });
        }
    }

    public void RefreshTheme() => Invalidate();

    public async Task<bool> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (!File.Exists(path)) { Diagnostic?.Invoke("overlay_unavailable", new { path, reason = "missing" }); return false; }
        var capability = CodecCapabilityRegistry.Describe(path);
        if (capability.Tier == CodecTier.Unsupported) { Diagnostic?.Invoke("overlay_unavailable", new { path, reason = capability.Provider, tier = capability.Tier.ToString() }); return false; }
        try
        {
            var result = await _loader.LoadFullForegroundAsync(path, cancellationToken, cacheResult: false).ConfigureAwait(true);
            if (_disposed || cancellationToken.IsCancellationRequested || result is null) { result?.Bitmap.Dispose(); return false; }
            var bitmap = result.Bitmap;
            var size = bitmap.PixelSize;
            var width = Math.Clamp(size.Width / 3.0, 180, 640);
            var ratio = size.Height / Math.Max(1.0, size.Width);
            var height = Math.Clamp(width * ratio, 120, 480);
            if (height >= 480) width = Math.Clamp(height / Math.Max(.0001, ratio), 180, 640);
            var contentScale = Math.Max(0.0001, Math.Min(width / Math.Max(1.0, size.Width), height / Math.Max(1.0, size.Height)));
            var state = new OverlayState(Guid.NewGuid(), Path.GetFullPath(path), 36 + _items.Count * 22, 36 + _items.Count * 22,
                width, height, DefaultOpacity, 1, ZIndex: _items.Count, ContentScale: contentScale).Normalize();
            _items.Add(new Item(state, bitmap));
            _selected = state.Id;
            ShowTransientChrome(state.Id);
            SortZ();
            Invalidate();
            Diagnostic?.Invoke("overlay_added", new { state.Path, state.Id });
            OverlayCountChanged?.Invoke();
            return true;
        }
        catch (Exception ex) { Diagnostic?.Invoke("overlay_decode_failed", new { path, error = ex.GetType().Name }); return false; }
    }

    public async Task RestoreAsync(IEnumerable<OverlayState> states, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        foreach (var state in states)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!File.Exists(state.Path) || CodecCapabilityRegistry.Describe(state.Path).Tier == CodecTier.Unsupported) continue;
            try
            {
                var result = await _loader.LoadFullForegroundAsync(state.Path, linked.Token, cacheResult: false).ConfigureAwait(true);
                if (_disposed || linked.IsCancellationRequested) { result?.Bitmap.Dispose(); return; }
                if (result is not null) _items.Add(new Item(ClampTransform(state.Normalize(), result.Bitmap), result.Bitmap));
            }
            catch (OperationCanceledException) { throw; }
            catch { Diagnostic?.Invoke("overlay_restore_failed", new { state.Path }); }
        }
        SortZ();
        Invalidate();
        OverlayCountChanged?.Invoke();
    }

    public void Clear()
    {
        ClearPointerState();
        StopAllAnimations();
        foreach (var item in _items) RetireCompositionBitmap(item.Bitmap);
        _items.Clear();
        _selected = _hovered = _chromeVisibleId = null;
        _chromeIdleTimer.Stop();
        Invalidate();
        Diagnostic?.Invoke("overlay_clear_all", null);
        OverlayCountChanged?.Invoke();
    }

    public void RemoveSelected() { if (_selected is Guid id) Remove(id); }

    public void Remove(Guid id)
    {
        var i = FindIndex(id); if (i < 0) return;
        ClearPointerState();
        StopAnimation(id);
        _animations.Remove(id);
        RetireCompositionBitmap(_items[i].Bitmap);
        _items.RemoveAt(i);
        if (_selected == id) _selected = _items.Count == 0 ? null : _items[Math.Min(i, _items.Count - 1)].State.Id;
        if (_hovered == id) _hovered = null;
        if (_chromeVisibleId == id) _chromeVisibleId = null;
        Invalidate();
        Diagnostic?.Invoke("overlay_removed", new { id });
        OverlayCountChanged?.Invoke();
    }

    public void BringSelectedToFront()
    {
        if (_selected is not Guid id) return;
        var i = FindIndex(id); if (i < 0) return;
        var max = _items.Count == 0 ? 0 : _items.Max(x => x.State.ZIndex) + 1;
        _items[i] = _items[i] with { State = _items[i].State with { ZIndex = max } };
        SortZ();
        Invalidate();
        Diagnostic?.Invoke("overlay_brought_to_front", new { id });
    }

    public void SetSelectedOpacity(double opacity)
    {
        if (_selected is Guid id) UpdateState(id, s => s with { Opacity = Math.Clamp(opacity, .1, 1.0) });
    }

    public void ZoomSelected(double factor)
    {
        if (_selected is not Guid id) return;
        var i = FindIndex(id); if (i < 0) return;
        SetState(i, ClampTransform(_items[i].State with { Zoom = _items[i].State.Zoom * factor }, _items[i].Bitmap));
        Invalidate();
    }

    public void ResetSelectedZoom()
    {
        if (_selected is not Guid id) return;
        var i = FindIndex(id); if (i < 0) return;
        SetState(i, _items[i].State with { Zoom = 1, PanX = 0, PanY = 0 });
        Invalidate();
        Diagnostic?.Invoke("overlay_zoom_reset", new { id });
    }

    public void SaveLayout(string path) => SaveLayout(path, preserveViewState: RememberZoom);

    /// <summary>
    /// Saves the complete overlay geometry/configuration when preserveViewState is true. Automatic
    /// session persistence always uses true so an explicitly enabled restore round-trips position,
    /// size, opacity, zoom and pan exactly; manual layouts continue to honour RememberZoom.
    /// </summary>
    public void SaveLayout(string path, bool preserveViewState) =>
        OverlayLayoutStore.Save(path, States.Select(state =>
        {
            var saved = preserveViewState ? state : state with { Zoom = 1, PanX = 0, PanY = 0 };
            if (!_animations.TryGetValue(state.Id, out var animation))
                return saved with { AnimationConfigured = false, AnimationRunning = false };
            return saved with
            {
                AnimationConfigured = true,
                AnimationRunning = animation.IsRunning,
                AnimationRegion = animation.Region,
                AnimationSpeedDipsPerSecond = animation.SpeedDipsPerSecond,
                AnimationTurnIntervalMs = animation.TurnIntervalMs,
                AnimationTurnAngleDegrees = animation.TurnAngleDegrees,
                AnimationPauseWhileInteracting = animation.PauseWhileInteracting,
                AnimationAvoidOverlap = animation.AvoidOverlap
            };
        }));

    public async Task LoadLayoutAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        var staged = new List<Item>();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (!OverlayLayoutStore.TryLoad(path, out var loadedStates)) return;
            foreach (var state in loadedStates)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!File.Exists(state.Path) || CodecCapabilityRegistry.Describe(state.Path).Tier == CodecTier.Unsupported) continue;
                var result = await _loader.LoadFullForegroundAsync(state.Path, linked.Token, cacheResult: false).ConfigureAwait(true);
                if (_disposed || linked.IsCancellationRequested) throw new OperationCanceledException(linked.Token);
                if (result is null) throw new InvalidDataException("Overlay decode returned no frame.");
                staged.Add(new Item(ClampTransform(state.Normalize(), result.Bitmap), result.Bitmap));
            }
            Clear();
            _items.AddRange(staged); staged.Clear();
            foreach (var item in _items.Where(x => x.State.AnimationConfigured))
            {
                var state = item.State;
                var animation = new AnimationState
                {
                    IsRunning = state.AnimationRunning,
                    Region = state.AnimationRegion,
                    SpeedDipsPerSecond = state.AnimationSpeedDipsPerSecond,
                    TurnIntervalMs = state.AnimationTurnIntervalMs,
                    TurnAngleDegrees = state.AnimationTurnAngleDegrees,
                    PauseWhileInteracting = state.AnimationPauseWhileInteracting,
                    AvoidOverlap = state.AnimationAvoidOverlap,
                    LastTick = Stopwatch.GetTimestamp()
                };
                SetRandomVelocity(animation);
                _animations[state.Id] = animation;
            }
            SortZ(); Invalidate(); OverlayCountChanged?.Invoke();
            RequestAnimationFrame();
        }
        catch (OperationCanceledException) { Diagnostic?.Invoke("overlay_layout_cancelled", null); }
        catch (Exception ex) { Diagnostic?.Invoke("overlay_layout_rejected", new { error = ex.GetType().Name }); }
        finally { foreach (var item in staged) item.Bitmap.Dispose(); }
    }

    internal void RenderSurface(DrawingContext context)
    {
        if (_items.Count == 0) return;
        foreach (var item in _items.OrderBy(x => x.State.ZIndex))
        {
            if (item.Bitmap is null) continue;
            var s = item.State;
            var dest = new Rect(s.X, s.Y, s.Width, s.Height);
            var src = SourceRect(item);
            using (context.PushOpacity(Math.Clamp(s.Opacity, 0.0, 1.0)))
            {
                try
                {
                    context.DrawImage(item.Bitmap, src, dest);
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke("overlay_render_failed", new { item.State.Path, error = ex.GetType().Name });
                }
            }
            var chromeVisible = _chromeVisibleId == s.Id ||
                                (_drag?.Id == s.Id) || (_resize?.Id == s.Id) ||
                                (_pan?.Id == s.Id) || (_opacityDrag == s.Id);
            if (chromeVisible)
                // Hover-visible chrome includes the glow; both controls and glow disappear together
                // when the pointer leaves the overlay.
                DrawChrome(context, s, true);
        }
    }

    private Rect SourceRect(Item item)
    {
        var s = item.State;
        if (item.Bitmap is null) return new Rect(0, 0, 1, 1);
        try
        {
            var size = item.Bitmap.PixelSize; var pw = Math.Max(1.0, size.Width); var ph = Math.Max(1.0, size.Height);
            var z = Math.Clamp(s.Zoom, 1.0, 16.0);
            var baseScale = s.ContentScale > 0.000001 ? s.ContentScale : Math.Max(0.0001, Math.Min(s.Width / pw, s.Height / ph));
            var scale = baseScale * z;
            var sw = Math.Min(pw, Math.Max(1.0, s.Width / scale)); var sh = Math.Min(ph, Math.Max(1.0, s.Height / scale));
            var centerX = 0.5 - s.PanX / Math.Max(1.0, s.Width * z); var centerY = 0.5 - s.PanY / Math.Max(1.0, s.Height * z);
            var halfX = sw / (2 * pw); var halfY = sh / (2 * ph); centerX = Math.Clamp(centerX, halfX, 1-halfX); centerY = Math.Clamp(centerY, halfY, 1-halfY);
            return new Rect(Math.Clamp(centerX*pw-sw/2,0,Math.Max(0,pw-sw)), Math.Clamp(centerY*ph-sh/2,0,Math.Max(0,ph-sh)), sw, sh);
        } catch { return new Rect(0,0,1,1); }
    }

    private void DrawChrome(DrawingContext context, OverlayState s, bool isInteracting = false)
    {
        var accent = AccentBrush;
        if (isInteracting)
        {
            var glow = new SolidColorBrush(Color.FromArgb(64, 56, 184, 244));
            context.DrawRectangle(glow, null, new Rect(s.X - 2, s.Y - 2, s.Width + 4, s.Height + 4), 9, 9);
        }
        var panel = new SolidColorBrush(Color.FromArgb(225, 32, 36, 42));
        var glass = new SolidColorBrush(Color.FromArgb(205, 45, 50, 58));
        var text = Brushes.White;
        var rect = new Rect(s.X, s.Y, s.Width, s.Height);
        context.DrawRectangle(null, new Pen(accent, 1.35), rect, 7, 7);

        var close = CloseRect(s);
        var cc = close.Center;
        context.DrawEllipse(panel, null, cc, 13, 13);
        context.DrawLine(new Pen(text, 1.8), new Point(cc.X - 4, cc.Y - 4), new Point(cc.X + 4, cc.Y + 4));
        context.DrawLine(new Pen(text, 1.8), new Point(cc.X + 4, cc.Y - 4), new Point(cc.X - 4, cc.Y + 4));

        var resize = ResizeRectVisual(s);
        context.DrawRectangle(glass, null, resize, 5, 5);
        for (var k = 0; k < 3; k++)
            context.DrawLine(new Pen(accent, 1.25),
                new Point(rect.Right - 7 - k * 5, rect.Bottom - 5),
                new Point(rect.Right - 5, rect.Bottom - 7 - k * 5));

        // The lower-right corner remains the true resize control. The centred right and top
        // handles are crop controls: they change only the overlay viewport on one axis while the
        // image scale/zoom stays fixed.
        var widthHandle = ResizeWidthRect(s);
        var heightHandle = ResizeHeightRect(s);
        context.DrawRectangle(glass, new Pen(accent, 1.2), widthHandle, 6, 6);
        context.DrawRectangle(glass, new Pen(accent, 1.2), heightHandle, 6, 6);

        // Right-middle: horizontal resize button ↔
        var wc = widthHandle.Center;
        context.DrawLine(new Pen(text, 2), new Point(wc.X - 7, wc.Y), new Point(wc.X + 7, wc.Y));
        context.DrawLine(new Pen(text, 2), new Point(wc.X - 7, wc.Y), new Point(wc.X - 3, wc.Y - 4));
        context.DrawLine(new Pen(text, 2), new Point(wc.X - 7, wc.Y), new Point(wc.X - 3, wc.Y + 4));
        context.DrawLine(new Pen(text, 2), new Point(wc.X + 7, wc.Y), new Point(wc.X + 3, wc.Y - 4));
        context.DrawLine(new Pen(text, 2), new Point(wc.X + 7, wc.Y), new Point(wc.X + 3, wc.Y + 4));

        // Top-middle: vertical resize button ↕
        var hc = heightHandle.Center;
        context.DrawLine(new Pen(text, 2), new Point(hc.X, hc.Y - 6), new Point(hc.X, hc.Y + 6));
        context.DrawLine(new Pen(text, 2), new Point(hc.X, hc.Y - 6), new Point(hc.X - 4, hc.Y - 2));
        context.DrawLine(new Pen(text, 2), new Point(hc.X, hc.Y - 6), new Point(hc.X + 4, hc.Y - 2));
        context.DrawLine(new Pen(text, 2), new Point(hc.X, hc.Y + 6), new Point(hc.X - 4, hc.Y + 2));
        context.DrawLine(new Pen(text, 2), new Point(hc.X, hc.Y + 6), new Point(hc.X + 4, hc.Y + 2));

        var slider = SliderRect(s);
        context.DrawRectangle(glass, null, slider, 10, 10);
        var sliderX = slider.Center.X;
        context.DrawLine(new Pen(text, 2), new Point(sliderX, slider.Top + 10), new Point(sliderX, slider.Bottom - 10));
        var ty = slider.Bottom - 10 - (slider.Height - 20) * s.Opacity;
        context.DrawEllipse(accent, null, new Point(sliderX, ty), 5, 5);

        DrawZoomButton(context, ZoomOutRect(s), false, glass, accent, text);
        DrawZoomButton(context, ZoomInRect(s), true, glass, accent, text);
    }

    private static void DrawZoomButton(DrawingContext context, Rect rect, bool plus, IBrush fill, IBrush accent, IBrush text)
    {
        context.DrawRectangle(fill, new Pen(accent, 1.2), rect, 7, 7);
        var c = rect.Center;
        context.DrawLine(new Pen(text, 2), new Point(c.X - 5, c.Y), new Point(c.X + 5, c.Y));
        if (plus) context.DrawLine(new Pen(text, 2), new Point(c.X, c.Y - 5), new Point(c.X, c.Y + 5));
    }

    private static Rect CloseRect(OverlayState s) => new(s.X + s.Width - 34, s.Y + 7, 27, 27);
    private static Rect ResizeRect(OverlayState s) => new(s.X + s.Width - 36, s.Y + s.Height - 36, 40, 40);
    private static Rect ResizeRectVisual(OverlayState s) => new(s.X + s.Width - 28, s.Y + s.Height - 28, 24, 24);
    private static Rect ResizeWidthRect(OverlayState s) => new(s.X + s.Width - 22, s.Y + s.Height / 2 - 22, 22, 44);
    private static Rect ResizeHeightRect(OverlayState s) => new(s.X + s.Width / 2 - 22, s.Y, 44, 22);
    private static Rect SliderRect(OverlayState s)
    {
        var h = Math.Min(130.0, Math.Max(70.0, s.Height - 80.0));
        return new Rect(s.X + 8, s.Y + (s.Height - h) / 2, 22, h);
    }
    private static Rect ZoomOutRect(OverlayState s) => new(s.X + Math.Max(4, s.Width / 2 - 36), s.Y + Math.Max(4, s.Height - 38), 30, 30);
    private static Rect ZoomInRect(OverlayState s) => new(s.X + Math.Max(4, s.Width / 2 + 6), s.Y + Math.Max(4, s.Height - 38), 30, 30);

    // This manager listens on the whole ImageView using tunnel routing so that the
    // drawn overlay chrome remains interactive.  A real viewer control can therefore
    // also reach this handler when an overlay is underneath it.  Never turn such a
    // press into an overlay interaction: doing so captures the pointer and makes
    // PauseWhileInteracting pause animations for the lifetime of the press.
    //
    // Keep this source-based and narrow.  The overlay itself is drawn by
    // LegacyOverlaySurface, so its hit-tested chrome has no Control ancestor and is
    // unaffected.  Once an overlay interaction has started, release/cleanup remains
    // fully handled by this manager.
    private static bool IsViewerControlSource(object? source)
    {
        for (var current = source as Control; current is not null; current = current.GetVisualParent() as Control)
        {
            if (current is Button or ToggleButton or TextBox or ComboBox or NumericUpDown or ScrollBar or ListBox or ListBoxItem or Slider ||
                current.Classes.Contains("glideTab") || current.Classes.Contains("tabHit"))
                return true;
        }

        return false;
    }

    private void SurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsViewerControlSource(e.Source)) return;

        var p = e.GetPosition(_inputHost);
        var i = HitTest(p);
        if (i < 0) return;
        var item = _items[i];
        _selected = item.State.Id;
        ShowTransientChrome(item.State.Id, hold: true);
        var cp = e.GetCurrentPoint(_inputHost);

        if (cp.Properties.IsLeftButtonPressed)
        {
            var chromeHit =
                CloseRect(item.State).Contains(p) || ZoomOutRect(item.State).Contains(p) ||
                ZoomInRect(item.State).Contains(p) || ResizeRect(item.State).Contains(p) ||
                ResizeWidthRect(item.State).Contains(p) || ResizeHeightRect(item.State).Contains(p) ||
                SliderRect(item.State).Contains(p);
            if (IsFullscreen?.Invoke() == true && !chromeHit)
            {
                _pendingFullscreenDrag = (item.State.Id, p, item.State.X, item.State.Y);
                _selected = null;
                // The overlay owns this press immediately.  Waiting for movement without
                // capturing/handling lets ImageViewport see the same press and navigate before
                // we know whether this is a drag.
                Capture(e.Pointer);
                e.Handled = true;
                return;
            }
            if (CloseRect(item.State).Contains(p))
            {
                Remove(item.State.Id); e.Handled = true; return;
            }
            if (ZoomOutRect(item.State).Contains(p))
            {
                var step = 1 + Math.Clamp(ZoomStepPercent, 1, 100) / 100.0;
                ZoomSelected(1.0 / step); e.Handled = true; return;
            }
            if (ZoomInRect(item.State).Contains(p))
            {
                var step = 1 + Math.Clamp(ZoomStepPercent, 1, 100) / 100.0;
                ZoomSelected(step); e.Handled = true; return;
            }
            if (ResizeRect(item.State).Contains(p))
                _resize = (item.State.Id, p, item.State.Width, item.State.Height, EnsureContentScale(item.State, item.Bitmap).ContentScale, ResizeMode.Proportional);
            else if (ResizeWidthRect(item.State).Contains(p))
                _resize = (item.State.Id, p, item.State.Width, item.State.Height, EnsureContentScale(item.State, item.Bitmap).ContentScale, ResizeMode.WidthOnly);
            else if (ResizeHeightRect(item.State).Contains(p))
                _resize = (item.State.Id, p, item.State.Width, item.State.Height, EnsureContentScale(item.State, item.Bitmap).ContentScale, ResizeMode.HeightOnly);
            else if (SliderRect(item.State).Contains(p))
            {
                _opacityDrag = item.State.Id;
                UpdateOpacityFromPoint(item.State.Id, p.Y);
            }
            else _drag = (item.State.Id, p, item.State.X, item.State.Y);
            Capture(e.Pointer); e.Handled = true;
        }
        else if (cp.Properties.IsRightButtonPressed)
        {
            // Always capture a right press that starts on an overlay. Previously an unzoomed overlay
            // left the event unhandled, so the underlying ImageViewport intermittently won the same
            // release and opened its context menu instead. A non-pannable overlay still uses this
            // lightweight click tracker, giving deterministic overlay-first ownership.
            _pan = (item.State.Id, p, item.State.PanX, item.State.PanY, false);
            Capture(e.Pointer); e.Handled = true;
        }
        else if (cp.Properties.IsMiddleButtonPressed)
        {
            Capture(e.Pointer); e.Handled = true;
        }
    }

    private void SurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_inputHost);
        if (_pendingFullscreenDrag is { } pending)
        {
            var dx = p.X - pending.Origin.X;
            var dy = p.Y - pending.Origin.Y;
            if (Math.Abs(dx) >= 4 || Math.Abs(dy) >= 4)
            {
                _pendingFullscreenDrag = null;
                _drag = pending;
                Capture(e.Pointer);
                UpdateStateNoInvalidate(pending.Id, s => s with { X = pending.X + dx, Y = pending.Y + dy });
                Invalidate();
                e.Handled = true;
            }
            return;
        }
        if (_drag is { } drag)
        {
            UpdateStateNoInvalidate(drag.Id, s => s with { X = drag.X + p.X - drag.Origin.X, Y = drag.Y + p.Y - drag.Origin.Y });
            Invalidate(); e.Handled = true; return;
        }
        if (_resize is { } resize)
        {
            var i = FindIndex(resize.Id); if (i >= 0)
            {
                var bitmap = _items[i].Bitmap;
                var current = EnsureContentScale(_items[i].State, bitmap);
                var dx = p.X - resize.Origin.X;
                var dy = p.Y - resize.Origin.Y;
                var requestedWidth = Math.Max(80, resize.Width + dx);
                var requestedHeight = resize.Mode == ResizeMode.HeightOnly
                    ? Math.Max(60, resize.Height - dy)
                    : Math.Max(60, resize.Height + dy);

                if (resize.Mode is ResizeMode.WidthOnly or ResizeMode.HeightOnly)
                {
                    // The two centred handles are crop-only controls. Keep image zoom and
                    // ContentScale exactly as they were when the drag began; changing only the
                    // viewport dimension reveals/hides source pixels at the same rendered scale.
                    // Expansion is capped at the full source-image extent, so dragging beyond the
                    // image edge simply stops rather than stretching or zooming the picture.
                    var pixelSize = bitmap.PixelSize;
                    var renderedScale = Math.Max(0.0001, resize.ContentScale) * Math.Clamp(current.Zoom, 1.0, 16.0);
                    var naturalMaxWidth = Math.Max(80.0, Math.Max(1.0, pixelSize.Width) * renderedScale);
                    var naturalMaxHeight = Math.Max(60.0, Math.Max(1.0, pixelSize.Height) * renderedScale);

                    if (resize.Mode == ResizeMode.WidthOnly)
                    {
                        var maxWidth = Math.Max(resize.Width, naturalMaxWidth);
                        var width = Math.Clamp(requestedWidth, 80.0, maxWidth);
                        SetState(i, ClampTransform(current with
                        {
                            Width = width,
                            Height = resize.Height,
                            ContentScale = resize.ContentScale
                        }, bitmap));
                    }
                    else
                    {
                        var maxHeight = Math.Max(resize.Height, naturalMaxHeight);
                        var height = Math.Clamp(requestedHeight, 60.0, maxHeight);
                        var bottom = current.Y + current.Height;
                        SetState(i, ClampTransform(current with
                        {
                            Y = bottom - height,
                            Width = resize.Width,
                            Height = height,
                            ContentScale = resize.ContentScale
                        }, bitmap));
                    }
                    Invalidate();
                }
                else
                {
                    double width;
                    double height;
                    if (current.PreserveAspectRatio)
                    {
                        // The lower-right corner is the proportional resize control. Choose the
                        // stronger drag axis so it remains responsive while preserving aspect ratio.
                        var sx = requestedWidth / Math.Max(1.0, resize.Width);
                        var sy = requestedHeight / Math.Max(1.0, resize.Height);
                        var scale = Math.Abs(sx - 1.0) >= Math.Abs(sy - 1.0) ? sx : sy;
                        scale = Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.05, 20.0);
                        width = Math.Max(80, resize.Width * scale);
                        height = Math.Max(60, resize.Height * scale);
                    }
                    else
                    {
                        width = requestedWidth;
                        height = requestedHeight;
                    }

                    // Corner resizing changes the rendered image scale with the overlay. Zoom itself
                    // is deliberately left alone, preventing resize feedback from multiplying Zoom
                    // and eliminating runaway/infinite zoom behaviour.
                    var sizeScale = Math.Min(width / Math.Max(1.0, resize.Width), height / Math.Max(1.0, resize.Height));
                    var contentScale = Math.Clamp(resize.ContentScale * (double.IsFinite(sizeScale) ? sizeScale : 1.0), 0.0001, 1000.0);
                    SetState(i, ClampTransform(current with
                    {
                        Width = width,
                        Height = height,
                        ContentScale = contentScale
                    }, bitmap));
                    Invalidate();
                }
            }
            e.Handled = true; return;
        }
        if (_opacityDrag is Guid opacityId)
        {
            UpdateOpacityFromPoint(opacityId, p.Y); e.Handled = true; return;
        }
        if (_pan is { } pan)
        {
            var dx = p.X - pan.Origin.X; var dy = p.Y - pan.Origin.Y;
            var started = pan.Started || Math.Abs(dx) >= 4 || Math.Abs(dy) >= 4;
            _pan = (pan.Id, pan.Origin, pan.PanX, pan.PanY, started);
            if (started && RightDragPanEnabled)
            {
                var i = FindIndex(pan.Id); if (i >= 0 && _items[i].State.Zoom > 1.001)
                {
                    SetState(i, ClampTransform(_items[i].State with { PanX = pan.PanX + dx, PanY = pan.PanY + dy }, _items[i].Bitmap));
                    Invalidate();
                }
            }
            e.Handled = true; return;
        }

        var hit = HitTest(p);
        var id = hit >= 0 ? _items[hit].State.Id : (Guid?)null;
        if (_hovered != id)
        {
            _hovered = id;
            if (id is Guid hover) ShowTransientChrome(hover, hold: true);
            else ScheduleChromeHide(immediate: true);
        }
        else if (id is Guid same)
        {
            // Hover is interaction: keep controls/glow visible for as long as the pointer remains
            // over the overlay, without requiring a click.
            ShowTransientChrome(same, hold: true);
        }
    }

    private void SurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // This handler is registered on the whole ImageView with handledEventsToo. Never release a
        // pointer merely because an overlay manager exists: the underlying ImageViewport may own
        // that capture (notably fullscreen right-click = Previous). Releasing somebody else's
        // capture fires PointerCaptureLost on ImageViewport and cancels its pending click gesture.
        var ownsOverlayPointer = ReferenceEquals(_capturedPointer, e.Pointer) ||
            _pendingFullscreenDrag is not null || _drag is not null || _resize is not null ||
            _pan is not null || _opacityDrag is not null;
        if (!ownsOverlayPointer) return;

        var fullscreenClick = _pendingFullscreenDrag is not null &&
            e.InitialPressMouseButton == MouseButton.Left;
        _pendingFullscreenDrag = null;
        var rightClickWithoutDrag = e.InitialPressMouseButton == MouseButton.Right && _pan is { Started: false };
        var middle = e.InitialPressMouseButton == MouseButton.Middle;
        try { _capturedPointer?.Capture(null); } catch { }
        ClearPointerState(releaseCapture: false);
        if (middle)
        {
            var action = OverlayGesturePolicy.Click(ResolveGesture?.Invoke("overlay.middleClick"));
            if (action == OverlayGestureAction.BringToFront) BringSelectedToFront();
            else if (action == OverlayGestureAction.ResetZoom) ResetSelectedZoom();
        }
        if (rightClickWithoutDrag)
            // An overlay owns its right-click in every presentation mode. The canvas receives
            // right-click navigation only when hit testing finds no overlay underneath it.
            OpenContextMenu();
        if (fullscreenClick)
        {
            // A pending press that never crossed the drag threshold is a click.  Navigation is
            // dispatched here exactly once; a drag has already cleared this state on movement.
            FullscreenNavigationRequested?.Invoke(1);
            e.Handled = true;
        }
        if (_hovered is Guid hoverId) ShowTransientChrome(hoverId, hold: true);
        else ScheduleChromeHide(immediate: true);
        e.Handled = e.Handled || rightClickWithoutDrag || middle;
    }

    private void SurfacePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (IsViewerControlSource(e.Source)) return;

        var p = e.GetPosition(_inputHost);
        // Wheel zoom is pointer-owned: hovering an overlay and scrolling must zoom that overlay
        // immediately, even when it was never clicked/selected first.
        var hit = HitTest(p);
        if (hit < 0) return;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var configured = ResolveGesture?.Invoke(ctrl ? "overlay.ctrlWheel" : "overlay.wheel");
        var enabled = ctrl ? CtrlWheelZoomEnabled : WheelZoomEnabled;
        var action = OverlayGesturePolicy.Wheel(configured, enabled, e.Delta.Y);
        if (action == OverlayGestureAction.None) return;
        _selected = _items[hit].State.Id;
        ShowTransientChrome(_selected.Value, hold: true);
        if (action == OverlayGestureAction.BringToFront) BringSelectedToFront();
        else if (action == OverlayGestureAction.ResetZoom) ResetSelectedZoom();
        else
        {
            var step = 1 + Math.Clamp(ZoomStepPercent, 1, 100) / 100.0;
            ZoomSelected(action == OverlayGestureAction.ZoomIn ? step : 1.0 / step);
        }
        e.Handled = true;
    }

    private void SurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsViewerControlSource(e.Source)) return;

        var i = HitTest(e.GetPosition(_inputHost)); if (i < 0) return;
        _selected = _items[i].State.Id;
        var action = OverlayGesturePolicy.Click(ResolveGesture?.Invoke("overlay.doubleClick"));
        if (action == OverlayGestureAction.BringToFront) BringSelectedToFront();
        else if (action == OverlayGestureAction.ResetZoom) ResetSelectedZoom();
        if (action != OverlayGestureAction.None) e.Handled = true;
    }

    private void UpdateOpacityFromPoint(Guid id, double y)
    {
        var i = FindIndex(id); if (i < 0) return;
        var slider = SliderRect(_items[i].State);
        var t = (slider.Bottom - 10 - y) / Math.Max(1.0, slider.Height - 20);
        SetState(i, _items[i].State with { Opacity = Math.Clamp(t, .1, 1.0) });
        Invalidate();
    }

    private async Task RefreshSelectedAsync(Guid id)
    {
        var i = FindIndex(id);
        if (i < 0) return;
        var path = _items[i].State.Path;
        if (!File.Exists(path)) { Diagnostic?.Invoke("overlay_refresh_failed", new { path, reason = "missing" }); return; }

        try
        {
            _loader.InvalidatePath(path);
            var result = await _loader.LoadFullForegroundAsync(path, _lifetime.Token, cacheResult: false).ConfigureAwait(true);
            if (_disposed || result is null) { result?.Bitmap.Dispose(); return; }
            i = FindIndex(id);
            if (i < 0) { result.Bitmap.Dispose(); return; }

            var old = _items[i];
            var nextState = ClampTransform(old.State, result.Bitmap);
            _items[i] = new Item(nextState, result.Bitmap);
            RetireCompositionBitmap(old.Bitmap);
            Invalidate();
            Diagnostic?.Invoke("overlay_refreshed", new { path, id });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostic?.Invoke("overlay_refresh_failed", new { path, error = ex.GetType().Name }); }
    }

    private void OpenContextMenu()
    {
        if (_selected is not Guid id) return;
        MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => { _selected = id; action(); };
            return mi;
        }
        var menu = new ContextMenu
        {
            Items =
            {
                Item("Zoom in\t+", () => ZoomSelected(1.20)),
                Item("Zoom out\t−", () => ZoomSelected(1.0 / 1.20)),
                Item("Restore default zoom", ResetSelectedZoom),
                Item($"{(FindIndex(id) >= 0 && _items[FindIndex(id)].State.PreserveAspectRatio ? "✓ " : "")}Preserve aspect ratio",
                    () => ToggleSelectedAspectRatio(id)),
                new Separator(),
                Item("Opacity 100%", () => SetSelectedOpacity(1.0)),
                Item("Opacity 75%", () => SetSelectedOpacity(.75)),
                Item("Opacity 50%", () => SetSelectedOpacity(.50)),
                new Separator(),
                Item("Bring to front", BringSelectedToFront),
                CreateAnimationMenu(id),
                Item("Refresh", () => _ = RefreshSelectedAsync(id)),
                new Separator(),
                Item("Open file location in Explorer", () => { var i = FindIndex(id); if (i >= 0) OpenFileLocationRequested?.Invoke(_items[i].State.Path); }),
                Item("Open in new tab", () => { var i = FindIndex(id); if (i >= 0) OpenInNewTabRequested?.Invoke(_items[i].State.Path); }),
                Item("Open with external program...", () => { var i = FindIndex(id); if (i >= 0) OpenWithRequested?.Invoke(_items[i].State.Path); }),
                new Separator(),
                Item("Close overlay", RemoveSelected),
                Item("Close all overlays", Clear)
            }
        };
        menu.Opened += (_, _) => ContextMenuVisibilityChanged?.Invoke(true);
        menu.Closed += (_, _) => ContextMenuVisibilityChanged?.Invoke(false);
        menu.Open(_inputHost);
    }

    private void ToggleSelectedAspectRatio(Guid id)
    {
        var i = FindIndex(id);
        if (i < 0) return;
        SetState(i, _items[i].State with { PreserveAspectRatio = !_items[i].State.PreserveAspectRatio });
        Invalidate();
    }

    internal void RefreshCompositionSnapshot()
    {
        var host = _inputHost.Bounds.Size;
        var items = new List<OverlayRenderItem>(_items.Count);
        foreach (var item in _items)
        {
            var state = item.State;
            var running = _animations.TryGetValue(state.Id, out var animation) && animation.IsRunning &&
                          !(animation.PauseWhileInteracting && IsPointerInteractionActive);
            var bounds = animation is null
                ? new Rect(state.X, state.Y, 0, 0)
                : AnimationBounds(state, host, animation.Region);
            var isInteracting = (_drag?.Id == state.Id) || (_resize?.Id == state.Id) ||
                                (_pan?.Id == state.Id) || (_opacityDrag == state.Id);
            items.Add(new OverlayRenderItem(
                state.Id,
                item.Bitmap,
                SourceRect(item),
                new Rect(state.X, state.Y, state.Width, state.Height),
                state.Opacity,
                state.ZIndex,
                (_chromeVisibleId == state.Id || isInteracting ? OverlayChromeSelection.Controls : OverlayChromeSelection.None) |
                (_chromeVisibleId == state.Id || isInteracting ? OverlayChromeSelection.Active : OverlayChromeSelection.None),
                running ? animation!.Velocity : default,
                running,
                bounds,
                AnimationRefreshRateHz,
                animation?.AvoidOverlap ?? AnimationAvoidOverlap,
                animation?.MotionRevision ?? 0));
        }
        var accent = (AccentBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x36, 0xB8, 0xF4);
        PixelPoint hostOrigin;
        try { hostOrigin = _inputHost.PointToScreen(new Point(0, 0)); }
        catch { hostOrigin = default; }
        var scaling = TopLevel.GetTopLevel(_inputHost)?.RenderScaling ?? 1.0;
        _surface.PublishCompositorSnapshot(new OverlayRenderSnapshot(items, AnimationRefreshRateHz, accent,
            ApplyCompositorMotion, hostOrigin, scaling));
    }

    private void ApplyCompositorMotion(IReadOnlyDictionary<Guid, OverlayCompositionMotion> motion)
    {
        if (_disposed || !_surface.UsesCompositionRenderer) return;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (!motion.TryGetValue(item.State.Id, out var latest)) continue;
            _items[i] = item with { State = item.State with { X = latest.Position.X, Y = latest.Position.Y } };
            if (_animations.TryGetValue(item.State.Id, out var animation) && animation.IsRunning)
                animation.Velocity = latest.Velocity;
        }
    }

    private MenuItem CreateAnimationMenu(Guid id)
    {
        var animation = GetOrCreateAnimationState(id);
        var animate = new MenuItem { Header = "Animate" };
        animate.Items.Add(CreateMenuItem("Start random motion", () => StartAnimation(id, randomize: true)));
        animate.Items.Add(CreateMenuItem("Change direction", () => ChangeAnimationDirection(id)));
        animate.Items.Add(CreateMenuItem("Stop animation", () => StopAnimation(id)));
        animate.Items.Add(CreateMenuItem("Animate all overlays", StartAllAnimations));
        animate.Items.Add(CreateMenuItem("Stop all animations", () => StopAllAnimations("menu_all")));
        animate.Items.Add(new Separator());
        animate.Items.Add(CreateAnimationRegionMenu(id, animation));
        animate.Items.Add(CreateAnimationSpeedMenu(id, animation));
        animate.Items.Add(CreateAnimationTurnsMenu(id, animation));
        animate.Items.Add(CreateMenuItem($"{(animation.PauseWhileInteracting ? "✓ " : "")}Pause while interacting", () =>
        {
            GetOrCreateAnimationState(id).PauseWhileInteracting = !animation.PauseWhileInteracting;
        }));
        animate.Items.Add(CreateMenuItem($"{(animation.AvoidOverlap ? "✓ " : "")}Avoid other overlays", () =>
        {
            GetOrCreateAnimationState(id).AvoidOverlap = !animation.AvoidOverlap;
        }));
        animate.Items.Add(new Separator());
        animate.Items.Add(CreateMenuItem("Use Settings defaults", () => ApplyAnimationDefaults(GetOrCreateAnimationState(id))));
        return animate;
    }

    private MenuItem CreateAnimationRegionMenu(Guid id, AnimationState animation)
    {
        var menu = new MenuItem { Header = $"Region: {animation.Region}" };
        foreach (var region in new[] { "Anywhere", "Top half", "Bottom half", "Top left quarter", "Top right quarter", "Bottom left quarter", "Bottom right quarter" })
            menu.Items.Add(CreateMenuItem($"{(string.Equals(animation.Region, region, StringComparison.OrdinalIgnoreCase) ? "✓ " : "")}{region}", () =>
            {
                var state = GetOrCreateAnimationState(id);
                state.Region = region;
                ClampAnimatedOverlaysToHost(_inputHost.Bounds.Size);
                Invalidate();
            }));
        return menu;
    }

    private MenuItem CreateAnimationSpeedMenu(Guid id, AnimationState animation)
    {
        var menu = new MenuItem { Header = $"Speed: {animation.SpeedDipsPerSecond} DIPs/s" };
        foreach (var option in new[] { ("Slow", 50), ("Normal", 90), ("Fast", 280), ("Very fast", 560) })
            menu.Items.Add(CreateMenuItem($"{(animation.SpeedDipsPerSecond == option.Item2 ? "✓ " : "")}{option.Item1} ({option.Item2})", () =>
            {
                GetOrCreateAnimationState(id).SpeedDipsPerSecond = option.Item2;
            }));
        return menu;
    }

    private MenuItem CreateAnimationTurnsMenu(Guid id, AnimationState animation)
    {
        var menu = new MenuItem { Header = animation.TurnIntervalMs <= 0 ? "Turns: Off" : $"Turns: {animation.TurnIntervalMs} ms / {animation.TurnAngleDegrees}°" };
        var options = new[] { ("Off", 0, 0), ("Gentle", 5000, 45), ("Normal", 2500, 120), ("Frequent", 1000, 180) };
        foreach (var option in options)
            menu.Items.Add(CreateMenuItem($"{(animation.TurnIntervalMs == option.Item2 && animation.TurnAngleDegrees == option.Item3 ? "✓ " : "")}{option.Item1}", () =>
            {
                var state = GetOrCreateAnimationState(id);
                state.TurnIntervalMs = option.Item2;
                state.TurnAngleDegrees = option.Item3;
                state.TurnElapsedSeconds = 0;
            }));
        return menu;
    }

    private MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void StartAnimation(Guid id) => StartAnimation(id, randomize: true);

    private void StartAllAnimations()
    {
        foreach (var item in _items)
            StartAnimation(item.State.Id, randomize: true);
        Diagnostic?.Invoke("overlay_animation_start_all", new { count = _items.Count });
    }

    private AnimationState GetOrCreateAnimationState(Guid id)
    {
        if (_animations.TryGetValue(id, out var existing)) return existing;
        var created = new AnimationState();
        ApplyAnimationDefaults(created);
        _animations[id] = created;
        return created;
    }

    private void ApplyAnimationDefaults(AnimationState animation)
    {
        animation.Region = AnimationRegion;
        animation.SpeedDipsPerSecond = Math.Clamp(AnimationSpeedDipsPerSecond, 1, 2000);
        animation.TurnIntervalMs = Math.Clamp(AnimationTurnIntervalMs, 0, 60000);
        animation.TurnAngleDegrees = Math.Clamp(AnimationTurnAngleDegrees, 0, 180);
        animation.PauseWhileInteracting = AnimationPauseWhileInteracting;
        animation.AvoidOverlap = AnimationAvoidOverlap;
        animation.TurnElapsedSeconds = 0;
        if (animation.IsRunning) SetRandomVelocity(animation);
    }

    private void StartAnimation(Guid id, bool randomize)
    {
        var index = FindIndex(id);
        if (index < 0 || _disposed) return;
        var animation = GetOrCreateAnimationState(id);
        animation.IsRunning = true;
        if (randomize || animation.Velocity.Length <= 0.001)
            SetRandomVelocity(animation);
        animation.TurnElapsedSeconds = 0;
        animation.LastTick = Stopwatch.GetTimestamp();
        if (_surface.UsesCompositionRenderer) RefreshCompositionSnapshot();
        RequestAnimationFrame();
        Diagnostic?.Invoke("overlay_animation_start", new { id, region = animation.Region, speed = animation.SpeedDipsPerSecond });
    }

    public void ChangeAnimationDirection(Guid id)
    {
        if (FindIndex(id) < 0 || _disposed) return;
        var animation = GetOrCreateAnimationState(id);
        animation.IsRunning = true;
        SetRandomVelocity(animation);
        animation.TurnElapsedSeconds = 0;
        animation.LastTick = Stopwatch.GetTimestamp();
        if (_surface.UsesCompositionRenderer) RefreshCompositionSnapshot();
        RequestAnimationFrame();
        Diagnostic?.Invoke("overlay_animation_turn", new { id, reason = "manual" });
    }

    public void StopAnimation(Guid id, string reason = "menu")
    {
        if (!_animations.TryGetValue(id, out var animation) || !animation.IsRunning) return;
        animation.IsRunning = false;
        if (_surface.UsesCompositionRenderer) RefreshCompositionSnapshot();
        // A queued frame cannot be cancelled through Avalonia. Leave the pending flag intact so
        // a stop/start sequence reuses that callback instead of creating duplicate frame loops.
        Diagnostic?.Invoke("overlay_animation_stop", new { id, reason });
    }

    private void StopAllAnimations(string reason = "clear")
    {
        foreach (var id in _animations.Keys.ToArray())
            Diagnostic?.Invoke("overlay_animation_stop", new { id, reason });
        _animations.Clear();
        _nativeAnimationControlTimer.Stop();
        if (_surface.UsesCompositionRenderer) RefreshCompositionSnapshot();
    }

    private void SetRandomVelocity(AnimationState animation)
    {
        var angle = Random.Shared.NextDouble() * Math.PI * 2;
        var speed = Math.Clamp(animation.SpeedDipsPerSecond, 1, 2000);
        animation.Velocity = new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed);
        animation.MotionRevision++;
    }

    private void ApplyRandomTurn(Guid id, AnimationState animation)
    {
        var velocity = animation.Velocity;
        var speed = velocity.Length;
        if (speed <= 0.001)
        {
            SetRandomVelocity(animation);
            return;
        }
        var maxAngle = Math.Clamp(animation.TurnAngleDegrees, 0, 180) * Math.PI / 180.0;
        if (maxAngle <= 0) return;
        var angle = (Random.Shared.NextDouble() * 2 - 1) * maxAngle;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        animation.Velocity = new Vector(
            (velocity.X * cos - velocity.Y * sin) / speed * Math.Clamp(animation.SpeedDipsPerSecond, 1, 2000),
            (velocity.X * sin + velocity.Y * cos) / speed * Math.Clamp(animation.SpeedDipsPerSecond, 1, 2000));
        animation.MotionRevision++;
        Diagnostic?.Invoke("overlay_animation_turn", new { id, reason = "interval", maxAngleDegrees = Math.Clamp(animation.TurnAngleDegrees, 0, 180) });
    }

    private void RequestAnimationFrame()
    {
        if (_disposed || !_animations.Values.Any(x => x.IsRunning) || _animationFramePending) return;
        if (_surface.UsesNativeWindowRenderer)
        {
            // Native windows own the high-rate motion loop. Keep only a low-rate UI control tick
            // for random turns and state relays so animated overlays cannot crowd input or canvas
            // presentation on Avalonia's render queue.
            if (!_nativeAnimationControlTimer.IsEnabled) _nativeAnimationControlTimer.Start();
            return;
        }
        var topLevel = TopLevel.GetTopLevel(_inputHost);
        if (topLevel is null) return;
        _animationFramePending = true;
        topLevel.RequestAnimationFrame(AnimationFrame);
    }

    private void AnimationFrame(TimeSpan _)
    {
        _animationFramePending = false;
        if (_disposed || !_animations.Values.Any(x => x.IsRunning)) return;

        var now = Stopwatch.GetTimestamp();
        var refreshRate = Math.Clamp(AnimationRefreshRateHz, 60, 360);
        if (_lastAnimationRenderTick != 0 &&
            (now - _lastAnimationRenderTick) / (double)Stopwatch.Frequency < (1.0 / refreshRate) * 0.9)
        {
            RequestAnimationFrame();
            return;
        }
        _lastAnimationRenderTick = now;

        var host = _inputHost.Bounds.Size;
        var changed = false;
        var compositorDriven = _surface.UsesCompositionRenderer;
        if (host.Width > 0 && host.Height > 0)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!_animations.TryGetValue(item.State.Id, out var animation) || !animation.IsRunning) continue;
                var delta = animation.LastTick == 0
                    ? 0
                    : (now - animation.LastTick) / (double)Stopwatch.Frequency;
                animation.LastTick = now;
                // A blocked/minimized UI can deliver a very old callback. Do not teleport overlays after it.
                delta = Math.Clamp(delta, 0, 0.05);
                if (animation.PauseWhileInteracting && IsPointerInteractionActive) continue;

                var turnInterval = Math.Max(0, animation.TurnIntervalMs) / 1000.0;
                if (turnInterval > 0)
                {
                    animation.TurnElapsedSeconds += delta;
                    if (animation.TurnElapsedSeconds >= turnInterval)
                    {
                        animation.TurnElapsedSeconds %= turnInterval;
                        ApplyRandomTurn(item.State.Id, animation);
                        if (compositorDriven) RefreshCompositionSnapshot();
                    }
                }

                var speed = Math.Clamp(animation.SpeedDipsPerSecond, 1, 2000);
                var velocity = animation.Velocity;
                if (velocity.Length <= 0.001)
                {
                    SetRandomVelocity(animation);
                    velocity = animation.Velocity;
                    if (compositorDriven) RefreshCompositionSnapshot();
                }
                else if (Math.Abs(velocity.Length - speed) > 0.01)
                {
                    velocity *= speed / velocity.Length;
                    animation.Velocity = velocity;
                }

                // The compositor is the sole position owner while available. The UI loop still
                // advances turn timing and publishes velocity/profile changes, while compositor
                // motion is relayed back for hit testing, chrome and persistence.
                if (compositorDriven)
                {
                    animation.Velocity = velocity;
                    continue;
                }

                var bounds = AnimationBounds(item.State, host, animation.Region);
                var proposedX = item.State.X + velocity.X * delta;
                var proposedY = item.State.Y + velocity.Y * delta;
                var lockX = bounds.Right <= bounds.Left;
                var lockY = bounds.Bottom <= bounds.Top;
                var hitLeft = !lockX && proposedX <= bounds.Left && velocity.X < 0;
                var hitRight = !lockX && proposedX >= bounds.Right && velocity.X > 0;
                var hitTop = !lockY && proposedY <= bounds.Top && velocity.Y < 0;
                var hitBottom = !lockY && proposedY >= bounds.Bottom && velocity.Y > 0;
                var x = lockX ? bounds.Left : Math.Clamp(proposedX, bounds.Left, bounds.Right);
                var y = lockY ? bounds.Top : Math.Clamp(proposedY, bounds.Top, bounds.Bottom);
                if (hitLeft || hitRight || hitTop || hitBottom)
                    velocity = ChooseInwardVelocity(bounds, x, y, speed, hitLeft, hitRight, hitTop, hitBottom, lockX, lockY);
                else
                    velocity = new Vector(lockX ? 0 : velocity.X, lockY ? 0 : velocity.Y);
                animation.Velocity = velocity;
                if (Math.Abs(x - item.State.X) > 0.0001 || Math.Abs(y - item.State.Y) > 0.0001)
                {
                    _items[i] = item with { State = item.State with { X = x, Y = y } };
                    changed = true;
                }
            }
            if (!compositorDriven) changed |= ResolveOverlayCollisions(host);
        }
        if (changed) Invalidate();
        RequestAnimationFrame();
    }

    private bool ResolveOverlayCollisions(Size host)
    {
        var changed = false;
        const double separationEpsilon = 0.05;
        for (var firstIndex = 0; firstIndex < _items.Count - 1; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < _items.Count; secondIndex++)
            {
                var firstItem = _items[firstIndex];
                var secondItem = _items[secondIndex];
                var firstResponsive = _animations.TryGetValue(firstItem.State.Id, out var firstAnimation) &&
                                      firstAnimation.IsRunning && firstAnimation.AvoidOverlap;
                var secondResponsive = _animations.TryGetValue(secondItem.State.Id, out var secondAnimation) &&
                                       secondAnimation.IsRunning && secondAnimation.AvoidOverlap;
                if (!firstResponsive && !secondResponsive) continue;

                var firstRect = new Rect(firstItem.State.X, firstItem.State.Y, firstItem.State.Width, firstItem.State.Height);
                var secondRect = new Rect(secondItem.State.X, secondItem.State.Y, secondItem.State.Width, secondItem.State.Height);
                var overlapX = Math.Min(firstRect.Right, secondRect.Right) - Math.Max(firstRect.Left, secondRect.Left);
                var overlapY = Math.Min(firstRect.Bottom, secondRect.Bottom) - Math.Max(firstRect.Top, secondRect.Top);
                if (overlapX <= 0 || overlapY <= 0) continue;

                var separateHorizontally = overlapX < overlapY ||
                    (Math.Abs(overlapX - overlapY) < 0.0001 &&
                     Math.Abs(firstRect.Center.X - secondRect.Center.X) >= Math.Abs(firstRect.Center.Y - secondRect.Center.Y));
                var direction = separateHorizontally
                    ? (firstRect.Center.X < secondRect.Center.X ||
                       (Math.Abs(firstRect.Center.X - secondRect.Center.X) < 0.0001 && firstItem.State.Id.CompareTo(secondItem.State.Id) < 0) ? -1.0 : 1.0)
                    : (firstRect.Center.Y < secondRect.Center.Y ||
                       (Math.Abs(firstRect.Center.Y - secondRect.Center.Y) < 0.0001 && firstItem.State.Id.CompareTo(secondItem.State.Id) < 0) ? -1.0 : 1.0);
                var penetration = (separateHorizontally ? overlapX : overlapY) + separationEpsilon;
                var firstShare = firstResponsive ? (secondResponsive ? penetration / 2 : penetration) : 0;
                var secondShare = secondResponsive ? (firstResponsive ? penetration / 2 : penetration) : 0;

                if (firstResponsive && firstAnimation is not null)
                {
                    var bounds = AnimationBounds(firstItem.State, host, firstAnimation.Region);
                    var x = separateHorizontally ? Math.Clamp(firstItem.State.X + direction * firstShare, bounds.Left, bounds.Right) : firstItem.State.X;
                    var y = separateHorizontally ? firstItem.State.Y : Math.Clamp(firstItem.State.Y + direction * firstShare, bounds.Top, bounds.Bottom);
                    firstAnimation.Velocity = ChooseCollisionVelocity(firstAnimation.SpeedDipsPerSecond, separateHorizontally, direction);
                    firstAnimation.TurnElapsedSeconds = 0;
                    _items[firstIndex] = firstItem with { State = firstItem.State with { X = x, Y = y } };
                    changed = true;
                }
                if (secondResponsive && secondAnimation is not null)
                {
                    var bounds = AnimationBounds(secondItem.State, host, secondAnimation.Region);
                    var x = separateHorizontally ? Math.Clamp(secondItem.State.X - direction * secondShare, bounds.Left, bounds.Right) : secondItem.State.X;
                    var y = separateHorizontally ? secondItem.State.Y : Math.Clamp(secondItem.State.Y - direction * secondShare, bounds.Top, bounds.Bottom);
                    secondAnimation.Velocity = ChooseCollisionVelocity(secondAnimation.SpeedDipsPerSecond, separateHorizontally, -direction);
                    secondAnimation.TurnElapsedSeconds = 0;
                    _items[secondIndex] = secondItem with { State = secondItem.State with { X = x, Y = y } };
                    changed = true;
                }

                Diagnostic?.Invoke("overlay_animation_collision", new { first = firstItem.State.Id, second = secondItem.State.Id });
            }
        }
        return changed;
    }

    private static Vector ChooseCollisionVelocity(double configuredSpeed, bool horizontal, double outwardDirection)
    {
        var speed = Math.Clamp(configuredSpeed, 1, 2000);
        var minimumOutward = speed * 0.35;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var angle = Random.Shared.NextDouble() * Math.PI * 2;
            var candidate = new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed);
            var outward = horizontal ? candidate.X * outwardDirection : candidate.Y * outwardDirection;
            if (outward >= minimumOutward) return candidate;
        }
        return horizontal ? new Vector(outwardDirection * speed, 0) : new Vector(0, outwardDirection * speed);
    }

    private Rect AnimationBounds(OverlayState state, Size host, string? region = null)
    {
        var left = 0.0;
        var top = 0.0;
        var right = host.Width;
        var bottom = host.Height;
        switch ((region ?? AnimationRegion).Trim().ToLowerInvariant())
        {
            case "top half":
                bottom /= 2;
                break;
            case "bottom half":
                top = bottom / 2;
                break;
            case "top left quarter":
                right /= 2;
                bottom /= 2;
                break;
            case "top right quarter":
                left = right / 2;
                bottom /= 2;
                break;
            case "bottom left quarter":
                right /= 2;
                top = bottom / 2;
                break;
            case "bottom right quarter":
                left = right / 2;
                top = bottom / 2;
                break;
        }
        var maxX = Math.Max(left, right - state.Width);
        var maxY = Math.Max(top, bottom - state.Height);
        return new Rect(left, top, Math.Max(0, maxX - left), Math.Max(0, maxY - top));
    }

    private static Vector ChooseInwardVelocity(
        Rect bounds,
        double x,
        double y,
        double speed,
        bool hitLeft,
        bool hitRight,
        bool hitTop,
        bool hitBottom,
        bool lockX,
        bool lockY)
    {
        if (lockX && lockY) return default;
        var minimumInward = speed * 0.15;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var angle = Random.Shared.NextDouble() * Math.PI * 2;
            var candidate = new Vector(lockX ? 0 : Math.Cos(angle) * speed, lockY ? 0 : Math.Sin(angle) * speed);
            if (hitLeft && candidate.X < minimumInward) continue;
            if (hitRight && candidate.X > -minimumInward) continue;
            if (hitTop && candidate.Y < minimumInward) continue;
            if (hitBottom && candidate.Y > -minimumInward) continue;
            var length = candidate.Length;
            if (length > 0.001) return candidate * (speed / length);
        }

        // An extremely unlikely run of unsuitable random angles still gets a stable inward
        // fallback, preventing an overlay from repeatedly pushing against the same boundary.
        var targetX = lockX ? x : bounds.Left + bounds.Width * (0.25 + Random.Shared.NextDouble() * 0.5);
        var targetY = lockY ? y : bounds.Top + bounds.Height * (0.25 + Random.Shared.NextDouble() * 0.5);
        var inward = new Vector(targetX - x, targetY - y);
        return inward.Length > 0.001 ? inward * (speed / inward.Length) : default;
    }

    private static OverlayState EnsureContentScale(OverlayState state, Bitmap bitmap)
    {
        if (state.ContentScale > 0.000001) return state;
        var scale = Math.Max(0.0001, Math.Min(state.Width / Math.Max(1.0, bitmap.PixelSize.Width), state.Height / Math.Max(1.0, bitmap.PixelSize.Height)));
        return state with { ContentScale = scale };
    }

    private OverlayState ClampTransform(OverlayState requested, Bitmap bitmap)
    {
        var s = EnsureContentScale(requested.Normalize(), bitmap) with { Zoom = Math.Clamp(requested.Zoom, 1.0, 16.0) };
        var maxPanX = Math.Max(0, s.Width * (s.Zoom - 1) / 2);
        var maxPanY = Math.Max(0, s.Height * (s.Zoom - 1) / 2);
        return s with { PanX = Math.Clamp(s.PanX, -maxPanX, maxPanX), PanY = Math.Clamp(s.PanY, -maxPanY, maxPanY) };
    }

    private int HitTestChrome(Point p)
    {
        for (var i = _items.Count - 1; i >= 0; --i)
        {
            var s = _items[i].State;
            if (ResizeRect(s).Contains(p) ||
                ResizeWidthRect(s).Contains(p) ||
                ResizeHeightRect(s).Contains(p) ||
                CloseRect(s).Contains(p) ||
                ZoomOutRect(s).Contains(p) ||
                ZoomInRect(s).Contains(p) ||
                SliderRect(s).Contains(p))
                return i;
        }
        return -1;
    }

    private int HitTest(Point p)
    {
        var chrome = HitTestChrome(p);
        if (chrome >= 0) return chrome;

        for (var i = _items.Count - 1; i >= 0; --i)
        {
            var s = _items[i].State;
            if (new Rect(s.X, s.Y, s.Width, s.Height).Contains(p)) return i;
        }
        return -1;
    }

    private int FindIndex(Guid id) => _items.FindIndex(x => x.State.Id == id);
    private void SetState(int i, OverlayState state)
    {
        var previous = _items[i].State;
        _items[i] = _items[i] with { State = state };
        if (_animations.TryGetValue(state.Id, out var animation) &&
            (Math.Abs(previous.X - state.X) > 0.0001 || Math.Abs(previous.Y - state.Y) > 0.0001 ||
             Math.Abs(previous.Width - state.Width) > 0.0001 || Math.Abs(previous.Height - state.Height) > 0.0001))
        {
            // A user resize/drag or host reflow is authoritative. Tell the native motion
            // backend to accept the new geometry once, preventing stale-position zig-zagging.
            animation.MotionRevision++;
            animation.LastTick = Stopwatch.GetTimestamp();
        }
    }

    private void UpdateState(Guid id, Func<OverlayState, OverlayState> update)
    {
        var i = FindIndex(id); if (i < 0) return;
        SetState(i, ClampTransform(update(_items[i].State), _items[i].Bitmap));
        Invalidate();
    }

    private void UpdateStateNoInvalidate(Guid id, Func<OverlayState, OverlayState> update)
    {
        var i = FindIndex(id); if (i < 0) return;
        SetState(i, update(_items[i].State));
    }

    private void SortZ() => _items.Sort((a, b) => a.State.ZIndex.CompareTo(b.State.ZIndex));

    private void ShowTransientChrome(Guid id, bool hold = false)
    {
        var changed = _chromeVisibleId != id;
        _chromeVisibleId = id;
        _chromeIdleTimer.Stop();
        if (!hold) _chromeIdleTimer.Start();
        if (changed) Invalidate();
    }

    private void ScheduleChromeHide(bool immediate = false)
    {
        _chromeIdleTimer.Stop();
        if (_chromeVisibleId is null) return;
        if (immediate && !IsPointerInteractionActive)
        {
            _chromeVisibleId = null; Invalidate(); return;
        }
        _chromeIdleTimer.Start();
    }

    private void Capture(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(_inputHost);
    }

    private void ClearPointerState(bool releaseCapture = true)
    {
        if (releaseCapture)
        {
            try { _capturedPointer?.Capture(null); } catch { }
        }
        _capturedPointer = null;
        _drag = null; _pendingFullscreenDrag = null; _resize = null; _pan = null; _opacityDrag = null;
    }

    private void Invalidate()
    {
        if (_surface.UsesCompositionRenderer) RefreshCompositionSnapshot();
        else _surface.InvalidateVisual();
    }

    private void RetireCompositionBitmap(Bitmap bitmap)
    {
        if (!_surface.UsesCompositionRenderer)
        {
            bitmap.Dispose();
            return;
        }
        if (!_retiredCompositionBitmaps.Add(bitmap)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (_retiredCompositionBitmaps.Remove(bitmap)) bitmap.Dispose();
            }, DispatcherPriority.Background);
        });
    }

    public void SetTopmost(bool topmost) => _surface.SetTopmost(topmost);
    public void SetModalSuppressed(bool suppressed) => _surface.SetModalSuppressed(suppressed);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        StopAllAnimations();
        ClearPointerState();
        _chromeIdleTimer.Stop();
        _nativeAnimationControlTimer.Stop();
        _surface.DisableCompositionRenderer();
        foreach (var item in _items) item.Bitmap.Dispose();
        foreach (var bitmap in _retiredCompositionBitmaps) bitmap.Dispose();
        _retiredCompositionBitmaps.Clear();
        _items.Clear();
        _surface.Manager = null;
        _lifetime.Dispose();
    }
}
