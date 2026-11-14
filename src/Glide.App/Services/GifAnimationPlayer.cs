using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// Plays an animated GIF in the main viewport. The normal pipeline has already presented the first
/// frame; this player decodes the composited frames off the UI thread and then swaps them into the
/// viewport on a timer via <see cref="ImageViewport.SetAnimationFrame"/>, which never alters zoom,
/// pan or fit. Playback is bound to the viewport's presentation generation: as soon as any other
/// frame is presented (navigation, Home, Speed Boost standby, close) the player stops by itself.
/// </summary>
internal sealed class GifAnimationPlayer : IDisposable
{
    // Frames retired by Stop() are disposed only after this delay so Skia's render thread can never
    // still be rasterizing a bitmap that was handed to the compositor (see MainWindow.Navigation).
    private static readonly TimeSpan RetireDelay = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for all decoded frames of one animation (single copy, see DecodeFrames).</summary>
    internal const long PlayerMemoryBudgetBytes = 512L * 1024 * 1024;

    private readonly ImageViewport _viewport;
    private readonly Action<string, object> _diagnostics;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private List<(WriteableBitmap Bitmap, int DelayMs)>? _frames;
    private int _generation;
    private int _index;
    private bool _paused;

    /// <summary>Raised on the UI thread when playback starts, stops, pauses, resumes or steps.</summary>
    public event Action? StateChanged;

    /// <summary>True while a multi-frame GIF is loaded for the frame the viewport presents now.</summary>
    public bool HasAnimation => _frames is { Count: > 1 } && _viewport.PresentationGeneration == _generation;
    public bool IsPaused => HasAnimation && _paused;
    public int FrameIndex => HasAnimation ? _index : 0;
    public int FrameCount => HasAnimation ? _frames!.Count : 0;

    public GifAnimationPlayer(ImageViewport viewport, Action<string, object> diagnostics)
    {
        _viewport = viewport;
        _diagnostics = diagnostics;
    }

    public static bool IsGif(string? path) =>
        !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts playback for the frame the viewport is presenting now (UI thread).</summary>
    public void Start(string path)
    {
        Stop();
        var generation = _viewport.PresentationGeneration;
        var presentedPath = _viewport.PresentedFrame.Path;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run<List<(WriteableBitmap Bitmap, int DelayMs)>?>(() => DecodeFrames(path, cts.Token), cts.Token).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted || task.Result is not { } frames)
            {
                if (task.IsFaulted) _diagnostics("gif_decode_failed", new { path, error = task.Exception?.GetBaseException().Message });
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || _viewport.PresentationGeneration != generation ||
                    !string.Equals(_viewport.PresentedFrame.Path, presentedPath, StringComparison.OrdinalIgnoreCase))
                {
                    RetireLater(frames);
                    return;
                }
                _frames = frames;
                _generation = generation;
                _index = 0;
                _paused = false;
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(frames[0].DelayMs) };
                _timer.Tick += Tick;
                _timer.Start();
                _diagnostics("gif_playback_started", new { path, frameCount = frames.Count });
                StateChanged?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    private static List<(WriteableBitmap Bitmap, int DelayMs)>? DecodeFrames(string path, CancellationToken token)
    {
        byte[] bytes;
        // Same sharing as every other user-image read: never block delete/rename of the open file.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length > 512L * 1024 * 1024) return null;
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        token.ThrowIfCancellationRequested();

        // Stream each composited frame straight into its display bitmap. The player used to decode every
        // frame into a managed byte[] first and then copy it, holding both copies at once (2x the frame
        // memory, all on the large object heap). With one copy the same peak budget now admits larger
        // animations: 512 MB of frames (e.g. 60 frames at 1920x1080) versus up to 768 MB peak before.
        var frames = new List<(WriteableBitmap Bitmap, int DelayMs)>();
        try
        {
            var size = GifAnimationDecoder.DecodeFrames(bytes, (canvas, width, height, delayMs) =>
            {
                token.ThrowIfCancellationRequested();
                var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul);
                try
                {
                    using var locked = bitmap.Lock();
                    GifAnimationDecoder.CopyCanvasToBgra(canvas, width, height, locked.Address, locked.RowBytes);
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
                frames.Add((bitmap, delayMs));
                return true;
            }, PlayerMemoryBudgetBytes, token);
            if (size is null || frames.Count < 2)
            {
                foreach (var (bitmap, _) in frames) bitmap.Dispose();
                return null;
            }
            return frames;
        }
        catch
        {
            foreach (var (bitmap, _) in frames) bitmap.Dispose();
            throw;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_paused) return;
        if (_frames is not { Count: > 0 } frames || _viewport.PresentationGeneration != _generation)
        {
            Stop();
            return;
        }
        _index = (_index + 1) % frames.Count;
        _viewport.SetAnimationFrame(frames[_index].Bitmap);
        if (_timer is not null) _timer.Interval = TimeSpan.FromMilliseconds(frames[_index].DelayMs);
    }

    /// <summary>Pauses on the current frame, or resumes from it (UI thread).</summary>
    public bool TogglePause() => IsPaused ? Resume() : Pause();

    public bool Pause()
    {
        if (!HasAnimation || _paused) return false;
        _paused = true;
        _timer?.Stop();
        _diagnostics("gif_paused", new { frame = _index });
        StateChanged?.Invoke();
        return true;
    }

    public bool Resume()
    {
        if (!HasAnimation || !_paused) return false;
        _paused = false;
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(_frames![_index].DelayMs);
            _timer.Start();
        }
        _diagnostics("gif_resumed", new { frame = _index });
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Shows the previous (-1) or next (+1) frame, wrapping at either end. Stepping pauses playback
    /// first so the chosen frame stays on screen.
    /// </summary>
    public bool Step(int delta)
    {
        if (!HasAnimation || _frames is not { } frames) return false;
        if (!_paused)
        {
            _paused = true;
            _timer?.Stop();
        }
        var count = frames.Count;
        _index = ((_index + delta) % count + count) % count;
        _viewport.SetAnimationFrame(frames[_index].Bitmap);
        _diagnostics("gif_step", new { frame = _index, delta });
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Stops playback and restores the statically presented frame (UI thread).</summary>
    public void Stop()
    {
        var hadState = _frames is not null || _paused;
        _paused = false;
        _cts?.Cancel();
        _cts = null;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Tick;
            _timer = null;
        }
        if (_frames is { } frames)
        {
            _frames = null;
            if (_viewport.PresentationGeneration == _generation) _viewport.SetAnimationFrame(null);
            RetireLater(frames);
        }
        if (hadState) StateChanged?.Invoke();
    }

    private static void RetireLater(List<(WriteableBitmap Bitmap, int DelayMs)> frames) =>
        DispatcherTimer.RunOnce(() => { foreach (var (bitmap, _) in frames) bitmap.Dispose(); }, RetireDelay);

    public void Dispose() => Stop();
}
