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

    private readonly ImageViewport _viewport;
    private readonly Action<string, object> _diagnostics;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private List<(WriteableBitmap Bitmap, int DelayMs)>? _frames;
    private int _generation;
    private int _index;

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
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(frames[0].DelayMs) };
                _timer.Tick += Tick;
                _timer.Start();
                _diagnostics("gif_playback_started", new { path, frameCount = frames.Count });
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
        var animation = GifAnimationDecoder.Decode(bytes, cancellationToken: token);
        if (animation is null) return null;

        var frames = new List<(WriteableBitmap Bitmap, int DelayMs)>(animation.Frames.Count);
        try
        {
            foreach (var frame in animation.Frames)
            {
                token.ThrowIfCancellationRequested();
                var bitmap = new WriteableBitmap(new PixelSize(animation.Width, animation.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var locked = bitmap.Lock())
                {
                    var rowBytes = animation.Width * 4;
                    for (var y = 0; y < animation.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(frame.Bgra, y * rowBytes, locked.Address + y * locked.RowBytes, rowBytes);
                }
                frames.Add((bitmap, frame.DelayMs));
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
        if (_frames is not { Count: > 0 } frames || _viewport.PresentationGeneration != _generation)
        {
            Stop();
            return;
        }
        _index = (_index + 1) % frames.Count;
        _viewport.SetAnimationFrame(frames[_index].Bitmap);
        if (_timer is not null) _timer.Interval = TimeSpan.FromMilliseconds(frames[_index].DelayMs);
    }

    /// <summary>Stops playback and restores the statically presented frame (UI thread).</summary>
    public void Stop()
    {
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
    }

    private static void RetireLater(List<(WriteableBitmap Bitmap, int DelayMs)> frames) =>
        DispatcherTimer.RunOnce(() => { foreach (var (bitmap, _) in frames) bitmap.Dispose(); }, RetireDelay);

    public void Dispose() => Stop();
}
