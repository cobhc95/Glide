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

    public static void Start(string path) => Start(path, 0, 0);

    /// <summary>
    /// Starts the overlapped cold-start decode. <paramref name="previewMaxWidth"/>/<paramref name="previewMaxHeight"/>
    /// is the first-frame box (physical pixels; 0 = full resolution). A large JPEG/JPEG XR is decoded
    /// decoder-scaled into that box, exactly like the foreground first frame, instead of at full
    /// resolution: a 100 MP JPEG used to spend ~900 ms here, outlive the consumer's 500 ms wait, get
    /// decoded a second time by the foreground path and leak its 400 MB native buffer.
    /// </summary>
    public static void Start(string path, int previewMaxWidth, int previewMaxHeight)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        path = Path.GetFullPath(path);
        lock (Sync)
        {
            _preloadedPath = path;
            _preloadTask = Task.Run(() => PreloadWorker(path, previewMaxWidth, previewMaxHeight));
        }
    }

    /// <summary>Tests: runs the preload worker synchronously without touching the shared startup slot.</summary>
    internal static StartupPreloadResult? PreloadForTests(string path, int previewMaxWidth, int previewMaxHeight)
        => PreloadWorker(Path.GetFullPath(path), previewMaxWidth, previewMaxHeight);

    private static StartupPreloadResult? PreloadWorker(string path, int previewMaxWidth, int previewMaxHeight)
    {
        var started = Stopwatch.GetTimestamp();
        if (GlidePerformanceTrace.Enabled)
            GlidePerformanceTrace.Mark("startup_preload_start", Path.GetFileName(path));

        try
        {
            var result = new StartupPreloadResult(path);

            // Security (decompression bombs): the probe reports a declared size even when it refuses it
            // (beyond 65,535 px or 512 MP). This worker used to ignore that and decode such a file at full
            // resolution (a few-MB PNG declaring 30000x30000 made the native bridge allocate ~3.6 GB),
            // bypassing the cap ImageLoadCoordinator enforces. Over-cap, unknown-size and frames that do not
            // fit the free memory (DecodeMemoryGuard) are left to the foreground path, which shows a bounded
            // preview or refuses them.
            var headerWithinCap = ImageHeaderProbe.TryProbe(path, out var header);
            var headerOverCap = header.IsValid && (ImageLoadCoordinator.IsDecompressionBomb(header) || !DecodeMemoryGuard.FitsAvailableMemory(header));

            // A missing/incompatible Glide.Native.dll must not lose the managed bytes fallback below.
            try
            {
            if (OperatingSystem.IsWindows() && NativeImageDecoder.SupportsDirectNativeDecode(path))
            {
                var status = 0;
                NativeImageDecoder.NativeDecodedImage native = default;
                if (previewMaxWidth > 0 && previewMaxHeight > 0 && header.IsValid &&
                    NativeImageDecoder.SupportsNativeScaledPreview(path) &&
                    Math.Max(header.Width, header.Height) > Math.Min(previewMaxWidth, previewMaxHeight))
                {
                    // Orientation-aware, decoder-scaled (DCT) first frame, colour-first progressive level.
                    status = NativeImageDecoder.RawGlideDecodeImageBounded(path, (uint)previewMaxWidth, (uint)previewMaxHeight, out native);
                }
                else if ((headerWithinCap && !headerOverCap) || (!header.IsValid && NativeProbeWithinCap(path)))
                {
                    // 5.2.0: a TIFF goes through the bounded entry with a box that never reduces it, so the native TIFF reader (parallel tiles,
                    // BigTIFF, CMYK) decodes it in full when it is the better choice; otherwise this is WIC's full decode as before.
                    status = NativeImageDecoder.IsTiffPath(path)
                        ? NativeImageDecoder.RawGlideDecodeImageBounded(path, 65535, 65535, out native)
                        : NativeImageDecoder.RawGlideDecodeImage(path, 0, out native);
                }
                else if (GlidePerformanceTrace.Enabled)
                {
                    GlidePerformanceTrace.Mark("startup_preload_full_decode_refused", header.IsValid ? $"{header.Width}x{header.Height}" : "unknown");
                }
                if (status != 1 && native.Data != IntPtr.Zero)
                {
                    NativeImageDecoder.RawGlideFreeBuffer(native.Data);
                    native = default;
                }
                if (status == 1 && native.Data != IntPtr.Zero)
                {
                    result.NativeImage = native;
                    result.NativeStatus = 1;
                    // Source dimensions (not the possibly reduced output) so the consumer can tell a
                    // preview from a full frame; NormalizeSourceOrientation handles EXIF rotation.
                    result.Dimensions = header.IsValid ? header : new ImageDimensions((int)native.Width, (int)native.Height);
                    if (GlidePerformanceTrace.Enabled)
                    {
                        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        GlidePerformanceTrace.Mark("startup_preload_native_decoded",
                            $"{native.Width}x{native.Height};elapsed={elapsedMs:F2}ms");
                    }
                    return result;
                }
            }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("startup_preload_native_unavailable", ex.GetType().Name);
            }

            // Preload bytes stay as byte[] (not NativeCompressedBuffer): the handoff to the
            // foreground path is a short-lived MemoryStream read, so native-lifetime management
            // is not trivial here. Bounded by the 128MB cap to limit LOH impact. Minimal change.
            // The consumer decodes preloaded bytes at full resolution, so they are handed over only for a
            // frame whose size is known and allowed (R3: an unknown size used to be decoded unbounded).
            if (headerOverCap) return null;
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 0 && fileInfo.Length <= 128 * 1024 * 1024)
            {
                var bytes = File.ReadAllBytes(path);
                if (!ImageHeaderProbe.TryProbe(bytes, out var dims) || !DecodeMemoryGuard.AllowsFullDecode(dims))
                    return null;
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

    /// <summary>WIC header size for files the managed probe could not read (e.g. a JPEG whose SOF is past 256 KB of metadata).</summary>
    private static bool NativeProbeWithinCap(string path)
    {
        if (!NativeImageProbe.TryProbe(path, out var info) || info.Width == 0 || info.Height == 0) return false;
        var dimensions = new ImageDimensions((int)Math.Min(info.Width, (uint)int.MaxValue), (int)Math.Min(info.Height, (uint)int.MaxValue));
        return !ImageLoadCoordinator.IsDecompressionBomb(dimensions) && DecodeMemoryGuard.FitsAvailableMemory(dimensions);
    }

    internal static async Task<StartupPreloadResult?> TryConsumeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path);
        Task<StartupPreloadResult?>? task = null;
        lock (Sync)
        {
            if (_preloadedPath is null || !string.Equals(_preloadedPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return null;
            task = _preloadTask;
            _preloadedPath = null;
            _preloadTask = null;
        }

        if (task is null) return null;

        try
        {
            // Fix (perf): await WaitAsync(timeout) instead of blocking task.Wait(500).
            if (!task.IsCompleted)
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("startup_preload_await_wait", Path.GetFileName(path));
                await task.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }

            if (task.IsCompletedSuccessfully && task.Result is { } preloaded)
            {
                if (GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("startup_preload_consumed", Path.GetFileName(path));
                return preloaded;
            }
        }
        catch (OperationCanceledException)
        {
            DisposeWhenDone(task);
            throw;
        }
        catch { /* preload is speculative; a failure just means the normal load path runs */ }

        // Timed out or failed: this consumer will decode itself, so the preload result (a native
        // pixel buffer of up to hundreds of MB) must be freed when the worker finishes. It used to leak.
        DisposeWhenDone(task);
        return null;
    }

    private static void DisposeWhenDone(Task<StartupPreloadResult?> task)
    {
        _ = task.ContinueWith(static t =>
        {
            if (t.IsCompletedSuccessfully) t.Result?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
