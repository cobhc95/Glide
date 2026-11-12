using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Glide.Imaging;

namespace Glide.App.Controls;

/// <summary>
/// One playing video overlay. A background pump pulls GPU-scaled frames from the native player into
/// a scratch buffer; the UI thread copies the newest frame into the back of two alternating
/// <see cref="WriteableBitmap"/>s and publishes it. Both overlay renderers detect new content by
/// bitmap reference, so alternating references is what makes a new frame visible without any
/// renderer changes. The pump never queues more than one frame: a slow UI drops frames instead of
/// building latency.
/// </summary>
/// <summary>Per-overlay playback preferences, carried over when the overlay moves to another video.</summary>
internal readonly record struct VideoOverlayOptions(bool Muted = false, double Volume = 1, bool Loop = false, bool AutoPlayNext = true);

internal sealed class VideoOverlaySession : IDisposable
{
    /// <summary>Longest side of decoded frames. Keeps per-frame copies small; overlays rarely exceed it.</summary>
    internal const int MaxFrameSide = 960;

    private readonly NativeVideoPlayer _player;
    private readonly WriteableBitmap[] _bitmaps;
    private readonly byte[] _scratch;
    private readonly Thread _pump;
    private readonly CancellationTokenSource _stop = new();
    private int _front;
    private int _framePending;
    private readonly object _infoGate = new();
    private VideoPlaybackInfo _info;

    public string Path { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public Bitmap Current => _bitmaps[_front];
    public IReadOnlyList<Bitmap> Bitmaps => _bitmaps;
    public VideoPlaybackInfo Info { get { lock (_infoGate) return _info; } }
    public bool IsDisposed => _stop.IsCancellationRequested;
    /// <summary>When the video ends (and is not looping), the overlay moves to the next video in its folder.</summary>
    public bool AutoPlayNext { get; set; } = true;
    public VideoOverlayOptions Options
    {
        get { var info = Info; return new VideoOverlayOptions(info.Muted, info.Volume, info.Loop, AutoPlayNext); }
    }

    /// <summary>Raised on the UI thread after <see cref="Current"/> changed to a new frame.</summary>
    public event Action<VideoOverlaySession>? FrameReady;
    /// <summary>Raised on the UI thread when play/pause/mute/loop/ended state changes.</summary>
    public event Action<VideoOverlaySession>? StateChanged;

    private VideoOverlaySession(string path, NativeVideoPlayer player, VideoPlaybackInfo info, int width, int height)
    {
        Path = path;
        _player = player;
        _info = info;
        FrameWidth = width;
        FrameHeight = height;
        _scratch = new byte[checked(width * height * 4)];
        _bitmaps = new[] { CreateBitmap(width, height), CreateBitmap(width, height) };
        if (_player.TryReadFrame(_scratch, width, height, force: true)) CopyToBitmap(_bitmaps[0]);
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "Glide video overlay", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>
    /// Opens and waits for the first metadata. Windows' Media Foundation (hardware decode) is tried
    /// first; if it can't play the file, FFmpeg is used, after offering to download it when it isn't
    /// on this PC yet. Returns null (with a reason) only when neither can.
    /// </summary>
    /// <summary>Asks the user to download the decoder pack; true once it is installed.</summary>
    public static Func<CancellationToken, Task<bool>>? DecoderPackRequested { get; set; }

    internal const string DecoderPackDeclined = "decoder_pack_declined";

    public static async Task<(VideoOverlaySession? Session, string? Failure)> OpenAsync(
        string path, VideoOverlayOptions options, CancellationToken cancellationToken)
    {
        // Media Foundation can't hardware-decode these files and would stall for its full timeout first.
        var beyondHardware = await Task.Run(() => NativeVideoPlayer.ExceedsHardwareDecodeLimit(path), cancellationToken).ConfigureAwait(true);
        NativeVideoPlayer? player = null;
        VideoPlaybackInfo info = default;
        string? failure = "hardware_decode_limit";
        if (!beyondHardware) (player, info, failure) = await TryOpenBackendAsync(path, VideoBackend.MediaFoundation, options, cancellationToken).ConfigureAwait(true);
        if (player is null)
        {
            if (!VideoDecoderPack.TryActivate())
            {
                if (DecoderPackRequested is not { } request) return (null, failure);
                if (!await request(cancellationToken).ConfigureAwait(true) || !VideoDecoderPack.TryActivate())
                    return (null, DecoderPackDeclined);
            }
            var fallback = await TryOpenBackendAsync(path, VideoBackend.Ffmpeg, options, cancellationToken).ConfigureAwait(true);
            if (fallback.Player is null) return (null, failure);
            (player, info) = (fallback.Player, fallback.Info);
        }
        try
        {
            var scale = Math.Min(1.0, (double)MaxFrameSide / Math.Max(info.Width, info.Height));
            var width = Math.Max(2, (int)Math.Round(info.Width * scale));
            var height = Math.Max(2, (int)Math.Round(info.Height * scale));
            var session = new VideoOverlaySession(path, player, player.GetInfo(), width, height) { AutoPlayNext = options.AutoPlayNext };
            session._pump.Start();
            return (session, null);
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    private static async Task<(NativeVideoPlayer? Player, VideoPlaybackInfo Info, string? Failure)> TryOpenBackendAsync(
        string path, VideoBackend backend, VideoOverlayOptions options, CancellationToken cancellationToken)
    {
        var player = await Task.Run(() => NativeVideoPlayer.TryOpen(path, out _, backend), cancellationToken).ConfigureAwait(true);
        if (player is null) return (null, default, "open_failed");
        try
        {
            player.SetMuted(options.Muted);
            player.SetVolume(options.Volume);
            player.SetLoop(options.Loop);
            var info = await player.WaitForReadyAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(true);
            if (info.State == VideoPlaybackState.Ready && info.Width > 0 && info.Height > 0) return (player, info, null);
            player.Dispose();
            return (null, info, info.Error != 0 ? $"codec_unavailable_0x{info.Error:X8}" : "not_ready");
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    public void TogglePlaying() => SetPlaying(!Info.Playing || Info.Ended);
    public void SetPlaying(bool playing) { _player.SetPlaying(playing); RefreshInfo(); }
    public void ToggleMuted() { _player.SetMuted(!Info.Muted); RefreshInfo(); }
    public void ToggleLoop() { _player.SetLoop(!Info.Loop); RefreshInfo(); }
    public void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        _player.SetVolume(volume);
        // Raising the volume of a muted video is an explicit request to hear it.
        if (volume > 0 && Info.Muted) _player.SetMuted(false);
        RefreshInfo();
    }
    public void Restart() { _player.Seek(0); _player.SetPlaying(true); RefreshInfo(); }

    public void SeekFraction(double fraction)
    {
        var duration = Info.Duration;
        if (duration <= 0) return;
        _player.Seek(Math.Clamp(fraction, 0, 1) * duration);
        RefreshInfo();
    }

    /// <summary>Playback progress in [0, 1]; 0 when the duration is unknown.</summary>
    public double Progress
    {
        get
        {
            var info = Info;
            return info.Duration > 0 ? Math.Clamp(info.Position / info.Duration, 0, 1) : 0;
        }
    }

    private void RefreshInfo()
    {
        var next = _player.GetInfo();
        VideoPlaybackInfo previous;
        lock (_infoGate) { previous = _info; _info = next; }
        if (previous.Playing != next.Playing || previous.Muted != next.Muted || previous.Loop != next.Loop || previous.Ended != next.Ended)
            Dispatcher.UIThread.Post(() => { if (!IsDisposed) StateChanged?.Invoke(this); });
    }

    private void PumpLoop()
    {
        var token = _stop.Token;
        var lastInfo = 0L;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Volatile.Read(ref _framePending) == 0 && _player.TryReadFrame(_scratch, FrameWidth, FrameHeight))
                {
                    Volatile.Write(ref _framePending, 1);
                    Dispatcher.UIThread.Post(PresentPendingFrame, DispatcherPriority.Render);
                }
                var now = Environment.TickCount64;
                if (now - lastInfo >= 200) { lastInfo = now; RefreshInfo(); }
            }
            catch
            {
                // A transient native failure must never take down the UI process; keep polling.
            }
            token.WaitHandle.WaitOne(5);
        }
    }

    private void PresentPendingFrame()
    {
        if (IsDisposed) return;
        var back = 1 - _front;
        CopyToBitmap(_bitmaps[back]);
        _front = back;
        Volatile.Write(ref _framePending, 0);
        var position = _player.GetInfo().Position;
        lock (_infoGate) _info = _info with { Position = position };
        FrameReady?.Invoke(this);
    }

    private void CopyToBitmap(WriteableBitmap bitmap)
    {
        using var frame = bitmap.Lock();
        var rowBytes = FrameWidth * 4;
        if (frame.RowBytes == rowBytes)
        {
            Marshal.Copy(_scratch, 0, frame.Address, rowBytes * FrameHeight);
            return;
        }
        for (var y = 0; y < FrameHeight; y++)
            Marshal.Copy(_scratch, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
    }

    private static WriteableBitmap CreateBitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    /// <summary>Stops the pump and closes the player. Bitmaps stay alive for the caller to retire.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        if (_pump.IsAlive && Thread.CurrentThread != _pump) _pump.Join(500);
        _player.Dispose();
        FrameReady = null;
        StateChanged = null;
    }
}
