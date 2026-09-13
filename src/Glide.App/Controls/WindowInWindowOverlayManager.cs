using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Interactivity;
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

    private readonly LegacyOverlaySurface _surface;
    private readonly Control _inputHost;
    private readonly ImageLoadCoordinator _loader;
    private readonly List<Item> _items = new();
    private readonly DispatcherTimer _chromeIdleTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Guid? _selected;
    private Guid? _hovered;
    private Guid? _chromeVisibleId;
    private IPointer? _capturedPointer;
    private (Guid Id, Point Origin, double X, double Y)? _drag;
    private (Guid Id, Point Origin, double Width, double Height)? _resize;
    private (Guid Id, Point Origin, double PanX, double PanY, bool Started)? _pan;
    private Guid? _opacityDrag;
    private Size _lastHostSize;
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
    public Action<string>? OpenFileLocationRequested { get; set; }
    public Action<string>? OpenInNewTabRequested { get; set; }
    public Action<string>? OpenWithRequested { get; set; }
    public Func<Size, Rect?>? ImageRectForSize { get; set; }
    public IBrush AccentBrush { get; set; } = new SolidColorBrush(Color.Parse("#36B8F4"));
    public Func<string, string?>? ResolveGesture { get; set; }
    public IReadOnlyList<OverlayState> States => _items.Select(x => x.State).ToArray();
    public bool HasOverlays => _items.Count > 0;
    public bool HasSelection => _selected is Guid id && FindIndex(id) >= 0;

    private bool IsPointerInteractionActive => _drag is not null || _resize is not null || _pan is not null || _opacityDrag is not null || _capturedPointer is not null;

    public WindowInWindowOverlayManager(LegacyOverlaySurface surface, Control inputHost, ImageLoadCoordinator loader)
    {
        _surface = surface;
        _inputHost = inputHost;
        _loader = loader;
        _surface.Manager = this;
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
    }

    private void HostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        var next = _inputHost.Bounds.Size;
        var previous = _lastHostSize;
        _lastHostSize = next;
        if (previous.Width <= 0 || previous.Height <= 0 || next.Width <= 0 || next.Height <= 0) return;
        if (Math.Abs(previous.Width - next.Width) < 0.25 && Math.Abs(previous.Height - next.Height) < 0.25) return;
        if (!AdaptivePositioningEnabled || _items.Count == 0) return;

        SaveGeometrySnapshot(previous);
        // When proportional scaling is enabled, reflow from the immediately previous host size even
        // if this target size has an older snapshot. That keeps overlay dimensions visually relative
        // to the host while resizing/maximizing/fullscreen transitions. Turning the option off restores
        // the historical exact-geometry snapshot behaviour.
        if (ScaleWithHostResize) ReflowGeometry(previous, next);
        else if (!RestoreGeometrySnapshot(next)) ReflowGeometry(previous, next);
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
            _items[i] = item with { State = FitFullyVisible(requested, next) };
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
            var result = await _loader.LoadForegroundAsync(path, cancellationToken).ConfigureAwait(true);
            if (_disposed || cancellationToken.IsCancellationRequested || result is null) { result?.Bitmap.Dispose(); return false; }
            var bitmap = result.Bitmap;
            var size = bitmap.PixelSize;
            var width = Math.Clamp(size.Width / 3.0, 180, 640);
            var ratio = size.Height / Math.Max(1.0, size.Width);
            var height = Math.Clamp(width * ratio, 120, 480);
            if (height >= 480) width = Math.Clamp(height / Math.Max(.0001, ratio), 180, 640);
            var state = new OverlayState(Guid.NewGuid(), Path.GetFullPath(path), 36 + _items.Count * 22, 36 + _items.Count * 22,
                width, height, DefaultOpacity, 1, ZIndex: _items.Count).Normalize();
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
                var result = await _loader.LoadForegroundAsync(state.Path, linked.Token).ConfigureAwait(true);
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
        foreach (var item in _items) item.Bitmap.Dispose();
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
        _items[i].Bitmap.Dispose();
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
        OverlayLayoutStore.Save(path, States.Select(x => preserveViewState ? x : x with { Zoom = 1, PanX = 0, PanY = 0 }));

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
                var result = await _loader.LoadForegroundAsync(state.Path, linked.Token).ConfigureAwait(true);
                if (_disposed || linked.IsCancellationRequested) throw new OperationCanceledException(linked.Token);
                if (result is null) throw new InvalidDataException("Overlay decode returned no frame.");
                staged.Add(new Item(ClampTransform(state.Normalize(), result.Bitmap), result.Bitmap));
            }
            Clear();
            _items.AddRange(staged); staged.Clear();
            SortZ(); Invalidate(); OverlayCountChanged?.Invoke();
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
            var s = item.State;
            var dest = new Rect(s.X, s.Y, s.Width, s.Height);
            var src = SourceRect(item);
            using (context.PushOpacity(Math.Clamp(s.Opacity, 0.0, 1.0)))
                context.DrawImage(item.Bitmap, src, dest);
            if (_chromeVisibleId == s.Id)
                DrawChrome(context, s);
        }
    }

    private Rect SourceRect(Item item)
    {
        var s = item.State;
        var pw = Math.Max(1.0, item.Bitmap.PixelSize.Width);
        var ph = Math.Max(1.0, item.Bitmap.PixelSize.Height);
        var z = Math.Clamp(s.Zoom, 1.0, 16.0);
        var sw = pw / z;
        var sh = ph / z;
        var centerX = 0.5 - s.PanX / Math.Max(1.0, s.Width * z);
        var centerY = 0.5 - s.PanY / Math.Max(1.0, s.Height * z);
        var halfX = .5 / z;
        var halfY = .5 / z;
        centerX = Math.Clamp(centerX, halfX, 1 - halfX);
        centerY = Math.Clamp(centerY, halfY, 1 - halfY);
        var sx = Math.Clamp(centerX * pw - sw / 2, 0, Math.Max(0, pw - sw));
        var sy = Math.Clamp(centerY * ph - sh / 2, 0, Math.Max(0, ph - sh));
        return new Rect(sx, sy, sw, sh);
    }

    private void DrawChrome(DrawingContext context, OverlayState s)
    {
        var accent = AccentBrush;
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

        var resize = ResizeRect(s);
        context.DrawRectangle(glass, null, resize, 5, 5);
        for (var k = 0; k < 3; k++)
            context.DrawLine(new Pen(accent, 1.25),
                new Point(rect.Right - 7 - k * 5, rect.Bottom - 5),
                new Point(rect.Right - 5, rect.Bottom - 7 - k * 5));

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
    private static Rect ResizeRect(OverlayState s) => new(s.X + s.Width - 28, s.Y + s.Height - 28, 24, 24);
    private static Rect SliderRect(OverlayState s)
    {
        var h = Math.Min(130.0, Math.Max(70.0, s.Height - 80.0));
        return new Rect(s.X + 8, s.Y + (s.Height - h) / 2, 22, h);
    }
    private static Rect ZoomOutRect(OverlayState s) => new(s.X + Math.Max(4, s.Width / 2 - 36), s.Y + Math.Max(4, s.Height - 38), 30, 30);
    private static Rect ZoomInRect(OverlayState s) => new(s.X + Math.Max(4, s.Width / 2 + 6), s.Y + Math.Max(4, s.Height - 38), 30, 30);

    private void SurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(_inputHost);
        var i = HitTest(p);
        if (i < 0) return;
        var item = _items[i];
        _selected = item.State.Id;
        ShowTransientChrome(item.State.Id, hold: true);
        var cp = e.GetCurrentPoint(_inputHost);

        if (cp.Properties.IsLeftButtonPressed)
        {
            if (_chromeVisibleId == item.State.Id && CloseRect(item.State).Contains(p))
            {
                Remove(item.State.Id); e.Handled = true; return;
            }
            if (_chromeVisibleId == item.State.Id && ZoomOutRect(item.State).Contains(p))
            {
                var step = 1 + Math.Clamp(ZoomStepPercent, 1, 100) / 100.0;
                ZoomSelected(1.0 / step); e.Handled = true; return;
            }
            if (_chromeVisibleId == item.State.Id && ZoomInRect(item.State).Contains(p))
            {
                var step = 1 + Math.Clamp(ZoomStepPercent, 1, 100) / 100.0;
                ZoomSelected(step); e.Handled = true; return;
            }
            if (_chromeVisibleId == item.State.Id && ResizeRect(item.State).Contains(p))
                _resize = (item.State.Id, p, item.State.Width, item.State.Height);
            else if (_chromeVisibleId == item.State.Id && SliderRect(item.State).Contains(p))
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
                var ratio = bitmap.PixelSize.Height / Math.Max(1.0, bitmap.PixelSize.Width);
                var width = Math.Max(80, resize.Width + p.X - resize.Origin.X);
                var height = Math.Max(60, width * ratio);
                if (height <= 60.0001) width = Math.Max(80, 60 / Math.Max(.0001, ratio));
                SetState(i, ClampTransform(_items[i].State with { Width = width, Height = height }, bitmap));
                Invalidate();
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
            if (id is Guid hover) ShowTransientChrome(hover);
            else ScheduleChromeHide(immediate: true);
        }
        else if (id is Guid same)
        {
            // Pointer motion is interaction; reveal chrome, but do not allocate/rebuild anything.
            ShowTransientChrome(same);
        }
    }

    private void SurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var rightClickWithoutDrag = e.InitialPressMouseButton == MouseButton.Right && _pan is { Started: false };
        var middle = e.InitialPressMouseButton == MouseButton.Middle;
        e.Pointer.Capture(null);
        ClearPointerState(releaseCapture: false);
        if (middle)
        {
            var action = OverlayGesturePolicy.Click(ResolveGesture?.Invoke("overlay.middleClick"));
            if (action == OverlayGestureAction.BringToFront) BringSelectedToFront();
            else if (action == OverlayGestureAction.ResetZoom) ResetSelectedZoom();
        }
        if (rightClickWithoutDrag) OpenContextMenu();
        ScheduleChromeHide();
        e.Handled = rightClickWithoutDrag || middle;
    }

    private void SurfacePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var p = e.GetPosition(_inputHost);
        var hit = HitTest(p);
        if (hit < 0 && _selected is Guid selected) hit = FindIndex(selected);
        if (hit < 0) return;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var action = OverlayGesturePolicy.Wheel(ResolveGesture?.Invoke(ctrl ? "overlay.ctrlWheel" : "overlay.wheel"), ctrl ? CtrlWheelZoomEnabled : WheelZoomEnabled, e.Delta.Y);
        if (action == OverlayGestureAction.None) return;
        _selected = _items[hit].State.Id;
        ShowTransientChrome(_selected.Value);
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
            var result = await _loader.LoadForegroundAsync(path, _lifetime.Token).ConfigureAwait(true);
            if (_disposed || result is null) { result?.Bitmap.Dispose(); return; }
            i = FindIndex(id);
            if (i < 0) { result.Bitmap.Dispose(); return; }

            var old = _items[i];
            var nextState = ClampTransform(old.State, result.Bitmap);
            _items[i] = new Item(nextState, result.Bitmap);
            old.Bitmap.Dispose();
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
                new Separator(),
                Item("Opacity 100%", () => SetSelectedOpacity(1.0)),
                Item("Opacity 75%", () => SetSelectedOpacity(.75)),
                Item("Opacity 50%", () => SetSelectedOpacity(.50)),
                new Separator(),
                Item("Bring to front", BringSelectedToFront),
                Item("Refresh", () => _ = RefreshSelectedAsync(id)),
                new Separator(),
                Item("Open file location in Explorer", () => { var i = FindIndex(id); if (i >= 0) OpenFileLocationRequested?.Invoke(_items[i].State.Path); }),
                Item("Open in new tab", () => { var i = FindIndex(id); if (i >= 0) OpenInNewTabRequested?.Invoke(_items[i].State.Path); }),
                Item("Open with external program...", () => { var i = FindIndex(id); if (i >= 0) OpenWithRequested?.Invoke(_items[i].State.Path); }),
                new Separator(),
                Item("Close overlay", RemoveSelected)
            }
        };
        menu.Open(_inputHost);
    }

    private OverlayState ClampTransform(OverlayState requested, Bitmap bitmap)
    {
        var s = requested.Normalize() with { Zoom = Math.Clamp(requested.Zoom, 1.0, 16.0) };
        var maxPanX = Math.Max(0, s.Width * (s.Zoom - 1) / 2);
        var maxPanY = Math.Max(0, s.Height * (s.Zoom - 1) / 2);
        return s with { PanX = Math.Clamp(s.PanX, -maxPanX, maxPanX), PanY = Math.Clamp(s.PanY, -maxPanY, maxPanY) };
    }

    private int HitTest(Point p)
    {
        for (var i = _items.Count - 1; i >= 0; --i)
        {
            var s = _items[i].State;
            if (new Rect(s.X, s.Y, s.Width, s.Height).Contains(p)) return i;
        }
        return -1;
    }

    private int FindIndex(Guid id) => _items.FindIndex(x => x.State.Id == id);
    private void SetState(int i, OverlayState state) => _items[i] = _items[i] with { State = state };

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
        _drag = null; _resize = null; _pan = null; _opacityDrag = null;
    }

    private void Invalidate() => _surface.InvalidateVisual();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        ClearPointerState();
        _chromeIdleTimer.Stop();
        foreach (var item in _items) item.Bitmap.Dispose();
        _items.Clear();
        _surface.Manager = null;
        _lifetime.Dispose();
    }
}
