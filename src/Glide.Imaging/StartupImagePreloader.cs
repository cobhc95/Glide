using System.Diagnostics;
using Avalonia.Media.Imaging;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Proactively pre-reads and pre-decodes the cold startup image on a worker thread
/// concurrently while Avalonia initializes its Win32 platform, Skia renderer, and XAML tree.
/// This completely overlaps image decode time with framework startup latency.
/// </summary>
public static class StartupImagePreloader
{
    private static readonly object Sync = new();
    private static string? _preloadedPath;
    private static Task<StartupPreloadResult?>? _preloadTask;

    internal sealed class StartupPreloadResult : IDisposable
    {
        public string Path { get; }
        internal NativeImageDecoder.NativeDecodedImage NativeImage { get; set; }
        public int NativeStatus { get; internal set; }
        public byte[]? PreloadedBytes { get; internal set; }
        public ImageDimensions Dimensions { get; internal set; }
        public Bitmap? Bitmap { get; internal set; }
        public bool IsConsumed { get; set; }

        public StartupPreloadResult(string path)
        {
            Path = path;
        }

        public void Dispose()
        {
            if (Bitmap is not null)
            {
                if (!IsConsumed) Bitmap.Dispose();
                Bitmap = null;
            }
            if (NativeImage.Data != IntPtr.Zero)
            {
                if (!IsConsumed)
                {
                    NativeImageDecoder.RawGlideFreeBuffer(NativeImage.Data);
                }
                NativeImage = default;
            }
            PreloadedBytes = null;
        }
    }

    public static void Start(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        path = Path.GetFullPath(path);
        lock (Sync)
        {
            _preloadedPath = path;
            _preloadTask = Task.Run(() => PreloadWorker(path));
        }
    }

    private static StartupPreloadResult? PreloadWorker(string path)
    {
        var started = Stopwatch.GetTimestamp();
        if (GlidePerformanceTrace.Enabled)
            GlidePerformanceTrace.Mark("startup_preload_start", Path.GetFileName(path));

        try
        {
            var result = new StartupPreloadResult(path);

            if (OperatingSystem.IsWindows() && NativeImageDecoder.SupportsDirectNativeDecode(path))
            {
                var status = NativeImageDecoder.RawGlideDecodeImage(path, 0, out var native);
                if (status == 1 && native.Data != IntPtr.Zero)
                {
                    result.NativeImage = native;
                    result.NativeStatus = 1;
                    result.Dimensions = new ImageDimensions((int)native.Width, (int)native.Height);
                    if (GlidePerformanceTrace.Enabled)
                    {
                        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        GlidePerformanceTrace.Mark("startup_preload_native_decoded",
                            $"{native.Width}x{native.Height};elapsed={elapsedMs:F2}ms");
                    }
                    return result;
                }
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 0 && fileInfo.Length <= 128 * 1024 * 1024)
            {
                var bytes = File.ReadAllBytes(path);
                ImageHeaderProbe.TryProbe(bytes, out var dims);
                result.PreloadedBytes = bytes;
                result.Dimensions = dims;
                if (GlidePerformanceTrace.Enabled)
                {
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    GlidePerformanceTrace.Mark("startup_preload_bytes_ready",
                        $"len={bytes.Length};dim={dims.Width}x{dims.Height};elapsed={elapsedMs:F2}ms");
                }
                return result;
            }

            return null;
        }
        catch (Exception ex)
        {
            if (GlidePerformanceTrace.Enabled)
                GlidePerformanceTrace.Mark("startup_preload_error", ex.GetType().Name);
            return null;
        }
    }

    internal static bool TryConsume(string path, out StartupPreloadResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path);
        Task<StartupPreloadResult?>? task = null;
        lock (Sync)
        {
            if (_preloadedPath is null || !string.Equals(_preloadedPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return false;
            task = _preloadTask;
            _preloadedPath = null;
            _preloadTask = null;
        }

        if (task is null) return false;

        try
        {
            // Wait for background preload if still in flight (usually already completed)
            if (!task.IsCompleted)
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("startup_preload_await_wait", Path.GetFileName(path));
                task.Wait(500);
            }

            if (task.IsCompletedSuccessfully && task.Result is { } preloaded)
            {
                result = preloaded;
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("startup_preload_consumed", Path.GetFileName(path));
                return true;
            }
        }
        catch { }

        return false;
    }
}
