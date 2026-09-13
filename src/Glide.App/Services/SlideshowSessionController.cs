using Avalonia.Threading;
using Glide.Core;
using Glide.App.Controls;

namespace Glide.App.Services;

/// <summary>Owns slideshow session state and scheduling; UI owns only its options dialog and chrome.</summary>
public sealed class SlideshowSessionController : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _canStart;
    private readonly Func<int> _count;
    private readonly Func<int> _index;
    private readonly Func<bool> _crossFolders;
    private readonly Func<bool> _loop;
    private readonly Func<bool> _shuffle;
    private readonly Func<int> _direction;
    private readonly Func<bool> _startInFullscreen;
    private readonly Func<int> _intervalMs;
    private readonly Func<Task<bool>> _configure;
    private readonly Func<ViewerViewSnapshot> _captureView;
    private readonly Action<ViewerViewSnapshot> _restoreView;
    private readonly Func<bool> _isFullscreen;
    private readonly Action _toggleFullscreen;
    private readonly Func<int, Task<bool>> _tryFolder;
    private readonly Action<int> _moveToEdge;
    private readonly Action<int> _move;
    private readonly Action<int> _select;
    private readonly Func<Task> _presentCurrent;
    private readonly Action _updateUi;
    private readonly Action<string, object?> _diagnostic;
    private readonly Random _random = new();
    private ViewerViewSnapshot? _startView;
    private bool _startFullscreen;
    private bool _pausedForInactivity;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public bool IsActive { get; private set; }

    public SlideshowSessionController(
        DispatcherTimer timer, Func<bool> canStart, Func<int> count, Func<int> index,
        Func<bool> crossFolders, Func<bool> loop, Func<bool> shuffle, Func<int> direction,
        Func<bool> startInFullscreen, Func<int> intervalMs,
        Func<Task<bool>> configure, Func<ViewerViewSnapshot> captureView, Action<ViewerViewSnapshot> restoreView,
        Func<bool> isFullscreen, Action toggleFullscreen, Func<int, Task<bool>> tryFolder,
        Action<int> moveToEdge, Action<int> move, Action<int> select, Func<Task> presentCurrent,
        Action updateUi, Action<string, object?> diagnostic)
    {
        _timer = timer; _canStart = canStart; _count = count; _index = index;
        _crossFolders = crossFolders; _loop = loop; _shuffle = shuffle; _direction = direction;
        _startInFullscreen = startInFullscreen; _intervalMs = intervalMs;
        _configure = configure; _captureView = captureView; _restoreView = restoreView;
        _isFullscreen = isFullscreen; _toggleFullscreen = toggleFullscreen; _tryFolder = tryFolder;
        _moveToEdge = moveToEdge; _move = move; _select = select; _presentCurrent = presentCurrent;
        _updateUi = updateUi; _diagnostic = diagnostic;
        _timer.Tick += TimerTick;
    }

    public async Task ToggleAsync()
    {
        if (IsActive) { if (IsRunning) Pause(); else Resume(); return; }
        if (!_canStart() || !await _configure().ConfigureAwait(true) || (_count() <= 1 && !_crossFolders())) return;
        IsActive = true;
        IsRunning = true;
        _pausedForInactivity = false;
        _startView = _captureView();
        _startFullscreen = _isFullscreen();
        if (_startInFullscreen() && !_isFullscreen()) _toggleFullscreen();
        SetTimer();
        _timer.Start();
        _updateUi();
        _diagnostic("started", new
        {
            intervalMs = _intervalMs(), loop = _loop(), shuffle = _shuffle(),
            direction = _direction() < 0 ? "Backward" : "Forward",
            requestedFullscreen = _startInFullscreen(), startFullscreen = _startFullscreen
        });
    }

    public void Pause()
    {
        if (!IsActive || !IsRunning) return;
        IsRunning = false; _timer.Stop(); _updateUi(); _diagnostic("paused", new { index = _index(), count = _count() });
    }

    public void Resume()
    {
        if (!IsActive || IsRunning || (_count() <= 1 && !_crossFolders())) return;
        IsRunning = true; SetTimer(); _timer.Start(); _updateUi();
        _diagnostic("resumed", new { index = _index(), count = _count(), intervalMs = _intervalMs() });
    }

    public void PauseForInactivity()
    {
        if (!IsActive || !IsRunning) return;
        _pausedForInactivity = true;
        Pause();
        _diagnostic("paused_inactive", new { index = _index() });
    }

    public void ResumeFromInactivity()
    {
        if (!_pausedForInactivity) return;
        _pausedForInactivity = false;
        Resume();
        _diagnostic("resumed_active", new { index = _index() });
    }

    public void Stop(bool restoreStartingMode)
    {
        if (!IsActive && !IsRunning) return;
        IsRunning = false; IsActive = false; _pausedForInactivity = false; _timer.Stop();
        _diagnostic("stopped", new { restoreStartingMode }); _updateUi();
        if (!restoreStartingMode) { _startView = null; return; }
        if (_startView is { } view) _restoreView(view);
        if (_startFullscreen != _isFullscreen()) _toggleFullscreen();
        _startView = null;
    }

    private async void TimerTick(object? sender, EventArgs e)
    {
        if (!IsActive || !IsRunning || _count() == 0) return;
        if (_shuffle() && _count() > 1)
        {
            var next = _index(); while (next == _index()) next = _random.Next(_count());
            _select(next); await AdvanceAsync(); return;
        }

        var direction = _direction() < 0 ? -1 : 1;
        var atBoundary = direction > 0 ? _index() >= _count() - 1 : _index() <= 0;
        if (atBoundary)
        {
            if (_crossFolders() && await _tryFolder(direction)) return;
            if (!_loop()) { Stop(true); return; }
            _moveToEdge(direction);
        }
        else _move(direction);
        await AdvanceAsync();
    }

    private Task AdvanceAsync() => _presentCurrent();
    private void SetTimer() => _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_intervalMs(), 250, 600000));
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Tick -= TimerTick; _timer.Stop(); }
}
