using System.Runtime.InteropServices;

namespace Glide.Imaging;

/// <summary>Playback state snapshot reported by <see cref="NativeVideoPlayer"/>.</summary>
public readonly record struct VideoPlaybackInfo(
    VideoPlaybackState State, int Width, int Height, bool Playing, bool Ended, bool Muted, bool Loop,
    int Error, double Duration, double Position, double Volume, VideoBackend Backend = VideoBackend.MediaFoundation);

public enum VideoPlaybackState { Loading = 0, Ready = 1, Failed = -1 }

/// <summary>
/// Media Foundation uses Windows' own (often hardware) decoders; FFmpeg is the optional, downloaded software
/// fallback for formats Windows can't decode on this PC (WebM, HEVC, AV1, MPEG-2 without extensions).
/// </summary>
public enum VideoBackend { MediaFoundation = 1, Ffmpeg = 2 }

/// <summary>
/// Managed owner of one Media Foundation player in Glide.Native. Every method is safe to call from
/// any thread and after disposal (calls become no-ops). Frames are copied into caller buffers as
/// opaque top-down BGRA, already scaled to the requested size on the GPU when one is available.
/// </summary>
public sealed partial class NativeVideoPlayer : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _handle;

    private NativeVideoPlayer(IntPtr handle) => _handle = handle;

    /// <summary>Starts opening <paramref name="path"/>. Playback begins (looping) once loaded.</summary>
    public static NativeVideoPlayer? TryOpen(string path, out int hresult, VideoBackend backend = VideoBackend.MediaFoundation)
    {
        hresult = -1;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            hresult = GlideVideoOpenExW(path, backend == VideoBackend.Ffmpeg ? 1 : 0, out var handle);
            return hresult >= 0 && handle != IntPtr.Zero ? new NativeVideoPlayer(handle) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the file is H.264 beyond the GPU hardware decode limit, which Media Foundation cannot
    /// play in reasonable time. Reads only the container header; false when the file can't be probed.
    /// </summary>
    public static bool ExceedsHardwareDecodeLimit(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return GlideVideoExceedsHardwareDecodeLimitW(path, out var exceeds) >= 0 && exceeds != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Polls until metadata is known, the source fails, or the timeout elapses.</summary>
    public async Task<VideoPlaybackInfo> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var info = GetInfo();
            if (info.State == VideoPlaybackState.Failed || (info.State == VideoPlaybackState.Ready && info.Width > 0 && info.Height > 0))
                return info;
            if (DateTime.UtcNow >= deadline) return info with { State = VideoPlaybackState.Failed };
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    public VideoPlaybackInfo GetInfo()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || GlideVideoGetInfo(_handle, out var n) < 0) return new VideoPlaybackInfo { };
            return new VideoPlaybackInfo((VideoPlaybackState)n.State, n.Width, n.Height, n.Playing != 0, n.Ended != 0,
                n.Muted != 0, n.Loop != 0, n.Error, n.Duration, n.Position, n.Volume, n.Backend == 2 ? VideoBackend.Ffmpeg : VideoBackend.MediaFoundation);
        }
    }

    public void SetPlaying(bool playing) { lock (_gate) if (_handle != IntPtr.Zero) GlideVideoSetPlaying(_handle, playing ? 1 : 0); }
    public void Seek(double seconds) { lock (_gate) if (_handle != IntPtr.Zero) GlideVideoSeek(_handle, seconds); }
    public void SetMuted(bool muted) { lock (_gate) if (_handle != IntPtr.Zero) GlideVideoSetMuted(_handle, muted ? 1 : 0); }
    public void SetVolume(double volume) { lock (_gate) if (_handle != IntPtr.Zero) GlideVideoSetVolume(_handle, volume); }
    public void SetLoop(bool loop) { lock (_gate) if (_handle != IntPtr.Zero) GlideVideoSetLoop(_handle, loop ? 1 : 0); }

    /// <summary>
    /// Writes the current frame into <paramref name="destination"/> (width*height*4 bytes). Returns
    /// true only when a new frame was written; <paramref name="force"/> re-reads the current frame.
    /// </summary>
    public unsafe bool TryReadFrame(byte[] destination, int width, int height, bool force = false)
    {
        if (width <= 0 || height <= 0 || destination.Length < (long)width * height * 4) return false;
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return false;
            fixed (byte* pixels = destination)
                return GlideVideoReadFrame(_handle, pixels, width, height, width * 4, force ? 1 : 0) == 1;
        }
    }

    public void Dispose()
    {
        IntPtr handle;
        lock (_gate) { handle = _handle; _handle = IntPtr.Zero; }
        if (handle != IntPtr.Zero) GlideVideoClose(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVideoInfo
    {
        public int State, Width, Height, Playing, Ended, Muted, Loop, Error;
        public double Duration, Position, Volume;
        public int Backend, Reserved;
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoOpenExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideVideoOpenExW(string path, int backend, out IntPtr handle);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoExceedsHardwareDecodeLimitW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideVideoExceedsHardwareDecodeLimitW(string path, out int exceeds);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoClose")]
    private static partial void GlideVideoClose(IntPtr handle);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoGetInfo")]
    private static partial int GlideVideoGetInfo(IntPtr handle, out NativeVideoInfo info);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoSetPlaying")]
    private static partial int GlideVideoSetPlaying(IntPtr handle, int playing);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoSeek")]
    private static partial int GlideVideoSeek(IntPtr handle, double seconds);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoSetMuted")]
    private static partial int GlideVideoSetMuted(IntPtr handle, int muted);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoSetVolume")]
    private static partial int GlideVideoSetVolume(IntPtr handle, double volume);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoSetLoop")]
    private static partial int GlideVideoSetLoop(IntPtr handle, int loop);

    [LibraryImport("Glide.Native", EntryPoint = "GlideVideoReadFrame")]
    private static unsafe partial int GlideVideoReadFrame(IntPtr handle, byte* destination, int width, int height, int stride, int force);
}
