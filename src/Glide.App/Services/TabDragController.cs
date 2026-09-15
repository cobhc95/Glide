using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Glide.App;

namespace Glide.App.Services;

/// <summary>
/// Tab-origin pointer state machine.  The composition root supplies only workspace/UI callbacks;
/// threshold classification, capture cancellation, attach preview ownership, tear-off dispatch and
/// release-time transfer decisions live here and are therefore independently auditable.
/// </summary>
public sealed class TabDragController
{
    private readonly TabDragHost _host;
    private readonly TabAttachCoordinator _ordinaryAttach;
    private Guid? _id;
    private Point _start;
    private PixelPoint _startScreen;
    private PixelPoint _lastScreen;
    private bool _active;
    private bool _singlePending;
    private int _targetIndex = -1;

    public bool IsActive => _id is not null;

    public TabDragController(TabDragHost host, TabAttachCoordinator ordinaryAttach)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ordinaryAttach = ordinaryAttach ?? throw new ArgumentNullException(nameof(ordinaryAttach));
    }

    public async Task PointerPressedAsync(Guid id, Border body, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(body);
        if (point.Properties.IsMiddleButtonPressed)
        {
            await _host.CloseMiddleAsync(id).ConfigureAwait(true);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        _host.SelectForDrag(id);
        _id = id;
        _start = _host.StripPoint(e);
        _lastScreen = _startScreen = _host.ScreenPoint(e);
        _active = false;
        _targetIndex = _host.TabCount() == 1 ? 0 : _host.IndexOfTab(id);
        _singlePending = _host.TabCount() == 1 && !_host.IsFullscreen();
        _host.Capture(body, e.Pointer);
        _host.Diagnostic("press", new { id, ownership = _singlePending ? "single_tab_pending" : "multi_tab" });
    }

    public async Task PointerMovedAsync(Guid id, Border body, PointerEventArgs e)
    {
        if (_id != id || !_host.IsLeftPressed(body, e)) return;
        var now = _host.StripPoint(e);
        _lastScreen = _host.ScreenPoint(e);
        if (_singlePending)
        {
            var dx = _lastScreen.X - _startScreen.X;
            var dy = _lastScreen.Y - _startScreen.Y;
            if (!Glide.Core.TabDragPolicy.CrossedThreshold(dx, dy)) return;
            _singlePending = false;
            _active = true;
            _host.ReleaseCapture(e.Pointer);
            ClearState();
            _host.Diagnostic("single_tab_native_window_drag", new { id, dx, dy, ownership = "threshold_deferred", moveLoop = "native-windows" });
            await _host.BeginSoleNativeMoveAsync(id, _lastScreen).ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (!_active && !Glide.Core.TabDragPolicy.CrossedThreshold(now.X - _start.X, now.Y - _start.Y)) return;
        _active = true;
        if (_host.TabDetachEnabled() && _host.IsBeyondTearOff(_lastScreen))
        {
            _host.ReleaseCapture(e.Pointer);
            ClearState();
            _host.Diagnostic("tearoff_started", new { id, screenX = _lastScreen.X, screenY = _lastScreen.Y });
            await _host.BeginTearOffAsync(id, _lastScreen).ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        _ordinaryAttach.Update(_lastScreen, _host.TabAttachEnabled(), "merge_target_entered", "merge_target_left", "tabs");
        _targetIndex = _ordinaryAttach.Target?.GetTabInsertIndex(_lastScreen) ??
            (_host.TabStripContains(_lastScreen) ? _host.GetInsertIndex(_lastScreen) : -1);
        _host.ApplyPreview(id, now.X - _start.X, _ordinaryAttach.Target is not null);
        _host.Diagnostic("drag", new { id, screenX = _lastScreen.X, screenY = _lastScreen.Y, targetWindow = _ordinaryAttach.Target?.Title ?? "", targetIndex = _targetIndex });
        e.Handled = true;
    }

    public async Task PointerReleasedAsync(Guid id, PointerReleasedEventArgs e)
    {
        if (_id != id) return;
        _lastScreen = _host.ScreenPoint(e);
        var wasDrag = _active;
        var target = _ordinaryAttach.Target;
        var targetIndex = _targetIndex;
        _host.ReleaseCapture(e.Pointer);
        try
        {
            if (wasDrag && target is { IsVisible: true } && target.CanAcceptTabAttach && _host.TabAttachEnabled())
            {
                if (await _host.AttachAsync(id, target, Math.Max(0, targetIndex)).ConfigureAwait(true))
                    _host.Diagnostic("merge_completed", new { id, target = target.Title, targetIndex });
            }
            else if (wasDrag && !_host.TabStripContains(_lastScreen) && _host.TabDetachEnabled())
            {
                await _host.DetachAsync(id, _lastScreen).ConfigureAwait(true);
            }
            else if (wasDrag && targetIndex >= 0)
            {
                await _host.ReorderAsync(id, targetIndex).ConfigureAwait(true);
            }
        }
        finally
        {
            ClearState();
            _host.RebuildTabs();
        }
        if (_host.ContainsTab(id))
        {
            if (!wasDrag) _host.Diagnostic("click_activated", new { id, count = _host.TabCount() });
            await _host.ActivateAsync().ConfigureAwait(true);
        }
        e.Handled = wasDrag;
    }

    public void PointerCaptureLost(Guid id)
    {
        if (_id != id) return;
        _host.Diagnostic("drag_capture_lost", new { id, active = _active, target = _ordinaryAttach.Target?.Title ?? "" });
        ClearState();
    }

    public void Clear()
    {
        _ordinaryAttach.Clear();
        ClearState();
    }

    public void ClearAttachTarget()
    {
        _ordinaryAttach.Clear();
        _targetIndex = -1;
    }

    private void ClearState()
    {
        _ordinaryAttach.Clear();
        _host.ClearVisuals();
        _id = null;
        _singlePending = false;
        _active = false;
        _targetIndex = -1;
    }
}

/// <summary>UI/workspace seam for <see cref="TabDragController"/>.</summary>
public sealed class TabDragHost
{
    public required Func<int> TabCount { get; init; }
    public required Func<Guid, int> IndexOfTab { get; init; }
    public required Func<Guid, bool> ContainsTab { get; init; }
    public required Func<bool> IsFullscreen { get; init; }
    public required Func<bool> TabAttachEnabled { get; init; }
    public required Func<bool> TabDetachEnabled { get; init; }
    public required Action<Guid> SelectForDrag { get; init; }
    public required Func<Guid, Task> CloseMiddleAsync { get; init; }
    public required Action<Border, IPointer> Capture { get; init; }
    public required Action<IPointer> ReleaseCapture { get; init; }
    public required Func<Border, PointerEventArgs, bool> IsLeftPressed { get; init; }
    public required Func<PointerEventArgs, Point> StripPoint { get; init; }
    public required Func<PointerEventArgs, PixelPoint> ScreenPoint { get; init; }
    public required Func<PixelPoint, bool> IsBeyondTearOff { get; init; }
    public required Func<PixelPoint, bool> TabStripContains { get; init; }
    public required Func<PixelPoint, int> GetInsertIndex { get; init; }
    public required Action<Guid, double, bool> ApplyPreview { get; init; }
    public required Action ClearVisuals { get; init; }
    public required Func<Guid, PixelPoint, Task> BeginSoleNativeMoveAsync { get; init; }
    public required Func<Guid, PixelPoint, Task> BeginTearOffAsync { get; init; }
    public required Func<Guid, MainWindow, int, Task<bool>> AttachAsync { get; init; }
    public required Func<Guid, PixelPoint, Task> DetachAsync { get; init; }
    public required Func<Guid, int, Task> ReorderAsync { get; init; }
    public required Func<Task> ActivateAsync { get; init; }
    public required Action RebuildTabs { get; init; }
    public required Action<string, object?> Diagnostic { get; init; }
}
