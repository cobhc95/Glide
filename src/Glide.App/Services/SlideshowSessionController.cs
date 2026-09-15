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
    private readonly Func<int, Task> _moveToEdge;
    private readonly Func<int, Task> _move;
    private readonly Func<int, Task> _select;
    private readonly Func<Task> _presentCurrent;
    private readonly Action _updateUi;
    private readonly Action<string, object?> _diagnostic;
    private readonly Random _random = new();
    private ViewerViewSnapshot? _startView;
    private bool _startFullscreen;
    private bool _pausedForInactivity;
    private bool _disposed;
    private int _tickInFlight;
    private long _sessionGeneration;

    public bool IsRunning { get; private set; }
    public bool IsActive { get; private set; }

    public SlideshowSessionController(
        DispatcherTimer timer, Func<bool> canStart, Func<int> count, Func<int> index,
        Func<bool> crossFolders, Func<bool> loop, Func<bool> shuffle, Func<int> direction,
        Func<bool> startInFullscreen, Func<int> intervalMs,
        Func<Task<bool>> configure, Func<ViewerViewSnapshot> captureView, Action<ViewerViewSnapshot> restoreView,
        Func<bool> isFullscreen, Action toggleFullscreen, Func<int, Task<bool>> tryFolder,
        Func<int, Task> moveToEdge, Func<int, Task> move, Func<int, Task> select, Func<Task> presentCurrent,
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
        Interlocked.Increment(ref _sessionGeneration);
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
        Interlocked.Increment(ref _sessionGeneration);
        IsRunning = false; _timer.Stop(); _updateUi(); _diagnostic("paused", new { index = _index(), count = _count() });
    }

    public void Resume()
    {
        if (!IsActive || IsRunning || (_count() <= 1 && !_crossFolders())) return;
        Interlocked.Increment(ref _sessionGeneration);
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
        Interlocked.Increment(ref _sessionGeneration);
        IsRunning = false; IsActive = false; _pausedForInactivity = false; _timer.Stop();
        _diagnostic("stopped", new { restoreStartingMode }); _updateUi();
        if (!restoreStartingMode) { _startView = null; return; }
        if (_startView is { } view) _restoreView(view);
        if (_startFullscreen != _isFullscreen()) _toggleFullscreen();
        _startView = null;
    }

    private async void TimerTick(object? sender, EventArgs e)
    {
        // DispatcherTimer uses an async-void callback. Without a top-level exception barrier, any
        // race from rapid manual navigation/Home can escape onto the UI thread and terminate Glide.
        // Never allow overlapping ticks either: a slow decode must not queue a second slideshow
        // mutation while the first one is still moving/presenting the navigator.
        if (!IsActive || !IsRunning || _count() == 0) return;
        if (Interlocked.Exchange(ref _tickInFlight, 1) != 0) return;
        var generation = Volatile.Read(ref _sessionGeneration);
        try
        {
            if (!SessionIsCurrent(generation)) return;
            if (_shuffle() && _count() > 1)
            {
                var next = _index(); while (next == _index()) next = _random.Next(_count());
                await _select(next).ConfigureAwait(true);
                if (!SessionIsCurrent(generation)) return;
                await AdvanceAsync().ConfigureAwait(true);
                return;
            }

            var direction = _direction() < 0 ? -1 : 1;
            var atBoundary = direction > 0 ? _index() >= _count() - 1 : _index() <= 0;
            if (atBoundary)
            {
                if (_crossFolders() && await _tryFolder(direction).ConfigureAwait(true)) return;
                if (!SessionIsCurrent(generation)) return;
                if (!_loop()) { Stop(true); return; }
                await _moveToEdge(direction).ConfigureAwait(true);
            }
            else
            {
                await _move(direction).ConfigureAwait(true);
            }

            if (!SessionIsCurrent(generation)) return;
            await AdvanceAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected when Home, a new image request, or slideshow stop invalidates in-flight work.
        }
        catch (Exception ex)
        {
            // This callback is async void: swallowing/logging here is a process-safety boundary.
            _diagnostic("tick_failed", new { error = ex.GetType().Name, ex.Message, index = SafeIndex(), count = SafeCount() });
        }
        finally
        {
            Volatile.Write(ref _tickInFlight, 0);
        }
    }

    private bool SessionIsCurrent(long generation) =>
        !_disposed && IsActive && IsRunning && generation == Volatile.Read(ref _sessionGeneration);

    private int SafeIndex() { try { return _index(); } catch { return -1; } }
    private int SafeCount() { try { return _count(); } catch { return -1; } }

    private Task AdvanceAsync() => _presentCurrent();
    private void SetTimer() => _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_intervalMs(), 250, 600000));
    public void Dispose() { if (_disposed) return; Interlocked.Increment(ref _sessionGeneration); _disposed = true; _timer.Tick -= TimerTick; _timer.Stop(); }
}
