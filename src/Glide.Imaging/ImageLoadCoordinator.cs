using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Unmanaged memory buffer allocated via NativeMemory or pooled memory owner.
/// Bypasses the Large Object Heap (LOH) entirely and avoids GC pauses during heavy image browsing.
/// </summary>
public sealed class NativeCompressedBuffer : IDisposable
{
    private IntPtr _pointer;
    private readonly long _length;
    private int _disposed;

    public const long MaxAllowedBufferBytes = 1024L * 1024L * 1024L; // 1 GB max compressed stream buffer

    public unsafe NativeCompressedBuffer(long length)
    {
        if (length <= 0 || length > MaxAllowedBufferBytes)
            throw new ArgumentOutOfRangeException(nameof(length), "Native compressed buffer length must be between 1 and 1GB.");
        _length = length;
        _pointer = (IntPtr)NativeMemory.Alloc((nuint)length);
    }

    public IntPtr Pointer => _pointer;
    public long Length => _length;

    public unsafe Span<byte> AsSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new Span<byte>((void*)_pointer, checked((int)_length));
    }

    public unsafe UnmanagedMemoryStream CreateReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new UnmanagedMemoryStream((byte*)_pointer, _length, _length, FileAccess.Read);
    }

    public unsafe UnmanagedMemoryStream CreateWriteStream()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new UnmanagedMemoryStream((byte*)_pointer, _length, _length, FileAccess.Write);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            var ptr = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
            {
                unsafe { NativeMemory.Free((void*)ptr); }
            }
        }
    }
}

/// <summary>
/// Pass-3 staged image pipeline. Foreground first-paint, visible-image refinement and speculative
/// neighbour preparation use independent execution lanes so an old uncancellable codec call cannot
/// consume the capacity needed by the next requested image.
/// </summary>
public sealed class ImageLoadCoordinator : IDisposable
{
    private readonly IImageDecoderBackend _backend;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CompressedEntry> _compressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _compressedLru = new();
    private readonly Dictionary<string, PreparedEntry> _prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _preparedLru = new();
    private readonly SemaphoreSlim _foregroundGate = new(ImagePerformanceGovernor.Current.ForegroundDecodeLanes, ImagePerformanceGovernor.Current.ForegroundDecodeLanes);
    private readonly SemaphoreSlim _refinementGate = new(1, 1);
    private readonly SemaphoreSlim _speculativeGate = new(ImagePerformanceGovernor.Current.SpeculativeDecodeLanes, ImagePerformanceGovernor.Current.SpeculativeDecodeLanes);
    private readonly SemaphoreSlim _compressedWarmGate = new(ImagePerformanceGovernor.Current.CompressedWarmLanes, ImagePerformanceGovernor.Current.CompressedWarmLanes);
    private CancellationTokenSource _prefetchCts = new();
    private long _generation;
    private long _cacheEpoch;
    private long _compressedBytes;
    private long _preparedBytes;
    private ImagePerformancePolicy _policy = ImagePerformancePolicy.ForProfile("Balanced");
    private readonly ImageRefinementScheduler _refinementScheduler = new();
    private readonly Dictionary<string, ImageViewportDemand> _viewportDemands = new(StringComparer.OrdinalIgnoreCase);
    private long _lastForegroundStartTimestamp;
    private ImageNavigationActivity _navigationActivity = ImageNavigationActivity.Idle;
    private string? _lastDemandPath;
    private CancellationTokenSource _activitySettleCts = new();

    private sealed record CompressedEntry(NativeCompressedBuffer Buffer, FileIdentity Identity, LinkedListNode<string> Node);
    private sealed record PreparedEntry(Bitmap Bitmap, bool IsFull, ImageDimensions Source, FileIdentity Identity, long EstimatedBytes, LinkedListNode<string> Node);
    private readonly record struct FileIdentity(long Length, long LastWriteTicksUtc);

    public ImageLoadCoordinator(IImageDecoderBackend? backend = null) => _backend = backend ?? new AvaloniaImageDecoderBackend();

    /// <summary>
    /// Callback to determine if a bitmap is currently active in the viewport or mid-transition,
    /// preventing premature disposal during LRU cache trimming, promotion, or purge.
    /// </summary>
    public Func<Bitmap?, bool>? IsBitmapInActiveUse { get; set; }

    public ImagePerformancePolicy Policy
    {
        get => _policy;
        set
        {
            _policy = (value ?? ImagePerformancePolicy.ForProfile("Balanced")).Normalize();
            TrimCaches();
        }
    }

    // Compatibility properties retained for diagnostic callers from earlier Completion-6 passes.
    public int WarmCacheItemLimit
    {
        get => Policy.CompressedCacheItems;
        set => Policy = Policy with { CompressedCacheItems = value };
    }
    public bool SequentialReadEnabled
    {
        get => Policy.SequentialForegroundReads;
        set => Policy = Policy with { SequentialForegroundReads = value };
    }

    public void SetViewportDemand(string path, ImageViewportDemand demand)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_cacheGate)
        {
            if (!string.Equals(_lastDemandPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _viewportDemands.Clear();
                _lastDemandPath = path;
            }
            _viewportDemands[path] = demand;
        }
    }

    public void ReportNavigationActivity(ImageNavigationActivity activity)
    {
        _navigationActivity = activity;
        if (activity != ImageNavigationActivity.RapidBrowse) return;
        _activitySettleCts.Cancel(); _activitySettleCts.Dispose();
        _activitySettleCts = new CancellationTokenSource();
        var token = _activitySettleCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ImageRefinementScheduler.RapidBrowseWindowMs, token).ConfigureAwait(false); _navigationActivity = ImageNavigationActivity.NormalBrowse; }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }
    public ImageNavigationActivity NavigationActivity => _navigationActivity;

    public async Task<ImageLoadResult?> LoadForegroundAsync(string path, CancellationToken cancellationToken = default)
        => await LoadForegroundAsync(path, Policy, cancellationToken).ConfigureAwait(false);

    /// <summary>Loads a presentation frame at source resolution, bypassing any prepared preview.</summary>
    public async Task<ImageLoadResult?> LoadFullForegroundAsync(string path, CancellationToken cancellationToken = default, bool cacheResult = true)
        => await LoadForegroundAsync(path, Policy with { DecoderScaledFirstFrame = false, BackgroundRefinement = false }, cancellationToken, requireFull: true, cacheResult: cacheResult).ConfigureAwait(false);

    public async Task<ImageLoadResult?> LoadForegroundAsync(string path, ImagePerformancePolicy policy, CancellationToken cancellationToken = default, bool requireFull = false, bool cachePreview = true, bool cacheResult = true)
    {
        policy = policy.Normalize();
        Policy = policy;
        var generation = Interlocked.Increment(ref _generation);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastForegroundStartTimestamp != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastForegroundStartTimestamp, now).TotalMilliseconds < ImageRefinementScheduler.RapidBrowseWindowMs)
            ReportNavigationActivity(ImageNavigationActivity.RapidBrowse);
        else if (_navigationActivity == ImageNavigationActivity.RapidBrowse)
            _navigationActivity = ImageNavigationActivity.NormalBrowse;
        _lastForegroundStartTimestamp = now;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_load_start", ImageFormatRegistry.GetLongestExtension(path));

        if (!requireFull && TryGetPrepared(path, out var prepared))
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_prepared_cache_hit", path);
            return new ImageLoadResult(path, prepared.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                CacheHit: true, IsPreview: !prepared.IsFull, SourceWidth: prepared.Source.Width,
                SourceHeight: prepared.Source.Height, Generation: generation, PreparedFrameHit: true,
                DecodeRoute: "prepared-cache");
        }

        if (StartupImagePreloader.TryConsume(path, out var preloadedResult) && preloadedResult is not null)
        {
            using (preloadedResult)
            {
                if (preloadedResult.Bitmap is not null)
                {
                    var preloadedSource = NormalizeSourceOrientation(preloadedResult.Dimensions, preloadedResult.Bitmap);
                    if (cacheResult) AddPrepared(path, preloadedResult.Bitmap, isFull: true, preloadedSource, epoch: -1);
                    preloadedResult.IsConsumed = true;
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_startup_preload_hit", path);
                    return new ImageLoadResult(path, preloadedResult.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                        CacheHit: true, IsPreview: false, SourceWidth: preloadedSource.Width,
                        SourceHeight: preloadedSource.Height, Generation: generation, PreparedFrameHit: true,
                        DecodeRoute: "startup-preloaded");
                }
                if (preloadedResult.NativeImage.Data != IntPtr.Zero && preloadedResult.NativeStatus == 1)
                {
                    if (NativeImageDecoder.TryCreateBitmapFromNative(preloadedResult.NativeImage, preloadedResult.NativeStatus, out var bitmap))
                    {
                        preloadedResult.IsConsumed = true;
                        var preloadedSource = NormalizeSourceOrientation(preloadedResult.Dimensions, bitmap);
                        if (cacheResult) AddPrepared(path, bitmap, isFull: true, preloadedSource, epoch: -1);
                        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_startup_preload_hit", path);
                        return new ImageLoadResult(path, bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                            CacheHit: true, IsPreview: false, SourceWidth: preloadedSource.Width,
                            SourceHeight: preloadedSource.Height, Generation: generation, PreparedFrameHit: true,
                            DecodeRoute: "startup-native-preloaded");
                    }
                }
                if (preloadedResult.PreloadedBytes is not null && preloadedResult.PreloadedBytes.Length > 0)
                {
                    using var ms = new MemoryStream(preloadedResult.PreloadedBytes, writable: false);
                    var bitmap = _backend.DecodeFull(ms, path);
                    preloadedResult.IsConsumed = true;
                    var preloadedSource = NormalizeSourceOrientation(preloadedResult.Dimensions, bitmap);
                    if (cacheResult) AddPrepared(path, bitmap, isFull: true, preloadedSource, epoch: -1);
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_startup_preload_hit", path);
                    return new ImageLoadResult(path, bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                        CacheHit: false, IsPreview: false, SourceWidth: preloadedSource.Width,
                        SourceHeight: preloadedSource.Height, Generation: generation, PreparedFrameHit: true,
                        DecodeRoute: "startup-memory-preloaded");
                }
            }
        }

        return await Task.Run(async () =>
        {
            await _foregroundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _generation)) return null;

                if (!requireFull && TryGetPrepared(path, out var preparedWhileWaiting))
                {
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_prepared_cache_hit", path);
                    return new ImageLoadResult(path, preparedWhileWaiting.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                        CacheHit: true, IsPreview: !preparedWhileWaiting.IsFull, SourceWidth: preparedWhileWaiting.Source.Width,
                        SourceHeight: preparedWhileWaiting.Source.Height, Generation: generation, PreparedFrameHit: true,
                        DecodeRoute: "prepared-cache");
                }

                var (stream, compressedHit) = OpenDecodeStream(path, policy.SequentialForegroundReads);
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_stream_ready", compressedHit ? "compressed_cache" : "file");
                using (stream)
                {
                    var source = await ProbeDimensionsAsync(path, stream, cancellationToken).ConfigureAwait(false);
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_probe_ready", source.IsValid ? $"{source.Width}x{source.Height}" : "unknown");

                    if (!requireFull && policy.DecoderScaledFirstFrame && _backend is IPathOptimizedImageDecoderBackend pathBackend &&
                        pathBackend.SupportsPathPreview(path) && source.IsValid && RequiresPreview(source, policy))
                    {
                        if (GlidePerformanceTrace.Enabled)
                            GlidePerformanceTrace.Mark("foreground_path_preview_start", $"source={source.Width}x{source.Height};box={policy.PreviewMaxWidth}x{policy.PreviewMaxHeight}");
                        var direct = await pathBackend.TryDecodePreviewFromPathAsync(path, source,
                            policy.PreviewMaxWidth, policy.PreviewMaxHeight, policy.ProgressiveColorFirstPreview,
                            cancellationToken).ConfigureAwait(false);
                        if (direct is { } pathPreview)
                        {
                            if (generation != Volatile.Read(ref _generation))
                            {
                                pathPreview.Bitmap.Dispose();
                                return null;
                            }
                            if (GlidePerformanceTrace.Enabled)
                                GlidePerformanceTrace.Mark("foreground_path_preview_ready",
                                    $"route={pathPreview.Route};output={pathPreview.Bitmap.PixelSize.Width}x{pathPreview.Bitmap.PixelSize.Height};elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms");
                            source = NormalizeSourceOrientation(source, pathPreview.Bitmap);
                            if (cacheResult && (!pathPreview.IsPreview || cachePreview))
                                AddPrepared(path, pathPreview.Bitmap, isFull: !pathPreview.IsPreview, source, epoch: -1);
                            return new ImageLoadResult(path, pathPreview.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                                CacheHit: pathPreview.Route == "shell-cache", IsPreview: pathPreview.IsPreview,
                                SourceWidth: source.Width, SourceHeight: source.Height, Generation: generation, PreparedFrameHit: false,
                                DecodeRoute: pathPreview.Route);
                        }
                        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_path_preview_miss", path);
                    }

                    Bitmap bitmap;
                    var isPreview = false;
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_decode_start", source.IsValid ? $"{source.Width}x{source.Height}" : "unknown");
                    if (source.IsValid && IsDecompressionBomb(source))
                    {
                        throw new InvalidDataException(
                            $"Image dimensions ({source.Width}x{source.Height}) exceed the safe decompression bomb limit of {ImageHeaderProbe.MaxTotalPixels:N0} pixels.");
                    }

                    if (!requireFull && policy.DecoderScaledFirstFrame && source.IsValid && RequiresPreview(source, policy))
                    {
                        var targetLongest = RequiredPreviewLongestSide(source, policy);
                        bitmap = await _backend.DecodePreviewAsync(stream, source, targetLongest, policy.PreviewInterpolation, path, cancellationToken).ConfigureAwait(false);
                        isPreview = bitmap.PixelSize.Width < source.Width || bitmap.PixelSize.Height < source.Height;
                    }
                    else
                    {
                        bitmap = await _backend.DecodeFullAsync(stream, path, cancellationToken).ConfigureAwait(false);
                        if (!source.IsValid) source = new ImageDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    }

                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_decode_ready", $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height};preview={isPreview}");
                    if (generation != Volatile.Read(ref _generation))
                    {
                        bitmap.Dispose();
                        return null;
                    }
                    if (!source.IsValid) source = new ImageDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    source = NormalizeSourceOrientation(source, bitmap);
                    if (cacheResult && (!isPreview || cachePreview))
                        AddPrepared(path, bitmap, isFull: !isPreview, source, epoch: -1);
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_load_end", $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms");
                    return new ImageLoadResult(path, bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                        compressedHit, isPreview, source.Width, source.Height, generation, false,
                        compressedHit ? "compressed-stream" : "stream");
                }
            }
            finally { _foregroundGate.Release(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageLoadResult?> RefineForegroundAsync(ImageLoadResult firstFrame, CancellationToken cancellationToken = default)
    {
        var policy = Policy;
        if (!firstFrame.IsPreview || !policy.BackgroundRefinement) return null;
        if (policy.RefinementDelayMs > 0) await Task.Delay(policy.RefinementDelayMs, cancellationToken).ConfigureAwait(false);
        if (_navigationActivity == ImageNavigationActivity.RapidBrowse)
        {
            await Task.Delay(ImageRefinementScheduler.RapidBrowseWindowMs, cancellationToken).ConfigureAwait(false);
            _navigationActivity = ImageNavigationActivity.NormalBrowse;
        }
        if (firstFrame.Generation != Volatile.Read(ref _generation)) return null;

        ImageViewportDemand demand;
        lock (_cacheGate) demand = _viewportDemands.TryGetValue(firstFrame.Path, out var requested) ? requested : ImageViewportDemand.Unspecified;
        var source = new ImageDimensions(firstFrame.SourceWidth, firstFrame.SourceHeight);
        var plan = _refinementScheduler.Plan(source, demand, _navigationActivity, policy);
        if (!plan.Allowed) return null;

        if (TryGetPrepared(firstFrame.Path, out var prepared) && prepared.IsFull)
        {
            if (firstFrame.Generation != Volatile.Read(ref _generation)) return null;
            if ((long)prepared.Bitmap.PixelSize.Width * prepared.Bitmap.PixelSize.Height <=
                (long)firstFrame.Bitmap.PixelSize.Width * firstFrame.Bitmap.PixelSize.Height)
            { return null; }
            var preparedBudget = Policy.DecodedCacheMegabytes * 1024L * 1024L;
            var preparedBytes = EstimatedDecodedBytes(prepared.Bitmap.PixelSize.Width, prepared.Bitmap.PixelSize.Height);
            var firstBytes = EstimatedDecodedBytes(firstFrame.Bitmap.PixelSize.Width, firstFrame.Bitmap.PixelSize.Height);
            if (preparedBytes > Math.Max(0, preparedBudget - firstBytes))
            { return null; }
            return new ImageLoadResult(firstFrame.Path, prepared.Bitmap, TimeSpan.Zero, true, false,
                prepared.Source.Width, prepared.Source.Height, firstFrame.Generation, true);
        }

        await _refinementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (firstFrame.Generation != Volatile.Read(ref _generation)) return null;
            var (stream, cacheHit) = await OpenDecodeStreamAsync(firstFrame.Path, policy.SequentialForegroundReads, cancellationToken).ConfigureAwait(false);
            using (stream)
            {
                var firstFrameBytes = (long)firstFrame.Bitmap.PixelSize.Width * firstFrame.Bitmap.PixelSize.Height * 4L;
                var sourceBytes = EstimatedDecodedBytes(firstFrame.SourceWidth, firstFrame.SourceHeight);
                var budgetBytes = policy.DecodedCacheMegabytes * 1024L * 1024L;
                var budgetAllowsFull = sourceBytes > 0 && sourceBytes <= budgetBytes - Math.Max(0, firstFrameBytes);
                var full = plan.FullResolution && budgetAllowsFull;
                if (GlidePerformanceTrace.Enabled && !plan.FullResolution)
                    GlidePerformanceTrace.Mark("refinement_bounded", $"reason=viewport_demand;target={plan.LongestSide};budget={budgetBytes}");
                if (plan.FullResolution && !budgetAllowsFull && GlidePerformanceTrace.Enabled)
                    GlidePerformanceTrace.Mark("refinement_bounded_by_budget", $"source={sourceBytes};firstFrame={firstFrameBytes};budget={budgetBytes}");
                var boundedTarget = Math.Min(plan.LongestSide, policy.PreviewLongestSide * 2);
                boundedTarget = ImageRefinementScheduler.BoundLongestSide(source, boundedTarget, Math.Max(0, budgetBytes - firstFrameBytes));
                var isPreview = !full && boundedTarget < Math.Max(firstFrame.SourceWidth, firstFrame.SourceHeight);
                var bitmap = isPreview
                    ? await _backend.DecodePreviewAsync(stream, new ImageDimensions(firstFrame.SourceWidth, firstFrame.SourceHeight), boundedTarget, policy.PreviewInterpolation, firstFrame.Path, cancellationToken).ConfigureAwait(false)
                    : await _backend.DecodeFullAsync(stream, firstFrame.Path, cancellationToken).ConfigureAwait(false);
                if (firstFrame.Generation != Volatile.Read(ref _generation)) { bitmap.Dispose(); return null; }
                if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height <=
                    (long)firstFrame.Bitmap.PixelSize.Width * firstFrame.Bitmap.PixelSize.Height)
                {
                    bitmap.Dispose();
                    return null;
                }
                var outputBytes = EstimatedDecodedBytes(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                if (outputBytes > Math.Max(0, budgetBytes - firstFrameBytes))
                {
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("refinement_bounded_output_rejected", $"output={outputBytes};budget={budgetBytes};firstFrame={firstFrameBytes}");
                    bitmap.Dispose(); return null;
                }
                return new ImageLoadResult(firstFrame.Path, bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started), cacheHit, isPreview,
                    firstFrame.SourceWidth, firstFrame.SourceHeight, firstFrame.Generation, false);
            }
        }
        finally { _refinementGate.Release(); }
    }

    /// <summary>
    /// Makes a successfully displayed refinement the authoritative prepared frame. The caller must
    /// invoke this only after its request/viewport checks have passed (the bitmap may already be
    /// attached to the viewport). A full frame can never be replaced by a preview, and a stale
    /// generation is rejected without taking ownership of the bitmap.
    /// </summary>
    public bool PromoteRefinement(ImageLoadResult firstFrame, ImageLoadResult refined)
    {
        if (!string.Equals(firstFrame.Path, refined.Path, StringComparison.OrdinalIgnoreCase) ||
            refined.Generation != Volatile.Read(ref _generation))
            return false;

        var estimate = EstimatedDecodedBytes(refined.Bitmap.PixelSize.Width, refined.Bitmap.PixelSize.Height);
        var budget = Policy.DecodedCacheMegabytes * 1024L * 1024L;
        if (estimate <= 0 || estimate > budget || !TryIdentity(refined.Path, out var identity)) return false;

        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(refined.Path, out var old) ||
                (old.IsFull && refined.IsPreview) ||
                (long)refined.Bitmap.PixelSize.Width * refined.Bitmap.PixelSize.Height <=
                (long)old.Bitmap.PixelSize.Width * old.Bitmap.PixelSize.Height ||
                _preparedBytes - old.EstimatedBytes + estimate > budget ||
                old.Identity != identity)
                return false;

            var node = old.Node;
            _preparedLru.Remove(node);
            _preparedLru.AddFirst(node);
            _prepared[refined.Path] = new PreparedEntry(refined.Bitmap, !refined.IsPreview,
                new ImageDimensions(refined.SourceWidth, refined.SourceHeight), identity, estimate, node);
            _preparedBytes = _preparedBytes - old.EstimatedBytes + estimate;
            // The old frame is detached by the caller before this method is called.
            // If the viewport is still rendering it (e.g. mid-transition), skip disposal;
            // it will be disposed by PriorBitmapReleased when finished.
            if (IsBitmapInActiveUse?.Invoke(old.Bitmap) != true)
            {
                old.Bitmap.Dispose();
            }
            return true;
        }
    }

    public void SchedulePrefetch(IReadOnlyList<string> paths, int currentIndex, int direction = 0)
    {
        var policy = Policy;
        CancellationTokenSource cts;
        long epoch;
        lock (_cacheGate)
        {
            _prefetchCts.Cancel();
            _prefetchCts.Dispose();
            _prefetchCts = cts = new CancellationTokenSource();
            epoch = ++_cacheEpoch;
        }
        if (!policy.PredictivePrefetch || policy.PrefetchDepth <= 0 || paths.Count < 2) return;
        var plan = BuildNeighbourPlan(paths, currentIndex, direction, policy.PrefetchDepth);
        _ = Task.Run(() => RunPrefetchAsync(plan, policy, epoch, cts.Token), CancellationToken.None);
    }

    public static IReadOnlyList<string> BuildNeighbourPlan(IReadOnlyList<string> paths, int currentIndex, int direction, int depth)
    {
        var result = new List<string>(Math.Max(0, depth) * 2);
        if (paths.Count == 0 || currentIndex < 0 || currentIndex >= paths.Count) return result;
        depth = Math.Clamp(depth, 0, 30);
        for (var step = 1; step <= depth; step++)
        {
            var first = direction < 0 ? currentIndex - step : currentIndex + step;
            var second = direction < 0 ? currentIndex + step : currentIndex - step;
            if (direction == 0) { first = currentIndex + step; second = currentIndex - step; }
            if (first >= 0 && first < paths.Count) result.Add(paths[first]);
            if (second >= 0 && second < paths.Count) result.Add(paths[second]);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task RunPrefetchAsync(IReadOnlyList<string> plan, ImagePerformancePolicy policy, long epoch, CancellationToken token)
    {
        try
        {
            if (plan.Count == 0) return;

            // Glide 2.1 warmed every compressed neighbour before preparing even the nearest likely
            // next image. Glide 2.2 reverses that priority: create the nearest presentation-ready
            // frame first, then spend idle I/O on farther compressed neighbours.
            var fullRemaining = policy.FullNeighbourPredecodeCount;
            if (await PrepareOneAsync(plan[0], policy, epoch, fullRemaining > 0, token).ConfigureAwait(false))
                fullRemaining = Math.Max(0, fullRemaining - 1);

            // Warm farther files only after the highest-value neighbour is presentation-ready.
            for (var i = 1; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                await WarmOneAsync(plan[i], epoch, token).ConfigureAwait(false);
            }

            for (var i = 1; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var usedFull = await PrepareOneAsync(plan[i], policy, epoch, fullRemaining > 0, token).ConfigureAwait(false);
                if (usedFull) fullRemaining = Math.Max(0, fullRemaining - 1);
            }
        }
        catch (OperationCanceledException)
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("prefetch_cancelled", "obsolete_generation_or_cache_epoch");
        }
        catch { /* speculative work is never allowed to break viewing */ }
    }

    private async Task<bool> PrepareOneAsync(string path, ImagePerformancePolicy policy, long epoch, bool allowFull, CancellationToken token)
    {
        if (epoch != Volatile.Read(ref _cacheEpoch)) return false;
        await _speculativeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Use one open stream for both the bounded header probe and decode, exactly as the
            // foreground path does. This removes a redundant open/read from the highest-value
            // neighbour-preparation path.
            var (stream, _) = await OpenDecodeStreamAsync(path, sequential: true, token).ConfigureAwait(false);
            using (stream)
            {
                var source = await ProbeDimensionsAsync(path, stream, token).ConfigureAwait(false);
                if (!source.IsValid || IsDecompressionBomb(source)) return false;
                var wantFull = allowFull && EstimatedDecodedBytes(source.Width, source.Height) <= policy.DecodedCacheMegabytes * 1024L * 1024L / 2;
                Bitmap bitmap;
                var isFull = false;
                if (wantFull || !policy.DecoderScaledFirstFrame || !RequiresPreview(source, policy))
                {
                    bitmap = await _backend.DecodeFullAsync(stream, path, token).ConfigureAwait(false);
                    isFull = true;
                }
                else
                {
                    bitmap = await _backend.DecodePreviewAsync(stream, source, RequiredPreviewLongestSide(source, policy), policy.PreviewInterpolation, path, token).ConfigureAwait(false);
                }
                if (epoch != Volatile.Read(ref _cacheEpoch)) { bitmap.Dispose(); return false; }
                source = NormalizeSourceOrientation(source, bitmap);
                AddPrepared(path, bitmap, isFull, source, epoch);
                return wantFull && isFull;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally { _speculativeGate.Release(); }
    }

    public async Task WarmAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var epoch = Volatile.Read(ref _cacheEpoch);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            await WarmOneAsync(path, epoch, cancellationToken).ConfigureAwait(false);
    }

    private async Task WarmOneAsync(string path, long epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TryGetCompressed(path, out _)) return;
        await _compressedWarmGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!TryIdentity(path, out var before) || before.Length <= 0) return;
            var policy = Policy;
            var readLimit = ImagePerformanceGovernor.GetMaxSpeculativeCompressedFileBytes(policy);
            if (before.Length > readLimit)
            {
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("compressed_cache_skip_large", $"bytes={before.Length};limit={readLimit}");
                return;
            }
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("compressed_cache_read_start", $"bytes={before.Length};limit={readLimit}");
            
            // Allocate native unmanaged buffer to completely avoid LOH GC pressure
            var buffer = new NativeCompressedBuffer(before.Length);
            try
            {
                await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var unmanagedStream = buffer.CreateWriteStream())
                {
                    await fs.CopyToAsync(unmanagedStream, token).ConfigureAwait(false);
                }

                if (!TryIdentity(path, out var after) || after != before || buffer.Length != before.Length)
                {
                    buffer.Dispose();
                    return;
                }
                if (epoch != Volatile.Read(ref _cacheEpoch))
                {
                    buffer.Dispose();
                    return;
                }
                if (AddCompressed(path, buffer, before, epoch))
                {
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("compressed_cache_admitted", $"bytes={buffer.Length}");
                }
                else
                {
                    buffer.Dispose();
                }
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally { _compressedWarmGate.Release(); }
    }

    private (Stream Stream, bool CacheHit) OpenDecodeStream(string path, bool sequential)
    {
        if (TryGetCompressed(path, out var buffer) && buffer is not null)
            return (buffer.CreateReadStream(), true);

        var options = FileOptions.Asynchronous | (sequential ? FileOptions.SequentialScan : FileOptions.None);
        return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, options), false);
    }

    private Task<(Stream Stream, bool CacheHit)> OpenDecodeStreamAsync(string path, bool sequential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Thread.CurrentThread.IsThreadPoolThread)
            return Task.FromResult(OpenDecodeStream(path, sequential));

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return OpenDecodeStream(path, sequential);
        }, token);
    }

    private ImageDimensions ProbeDimensionsAsyncDirect(string path)
    {
        // Dedicated built-in decoders know their own container. Probe them before the generic content
        // sniffer, which is unreliable for header-less formats (an uncompressed true-colour TGA, for
        // example, starts 00 00 02 00 and can otherwise resemble an ICO/CUR directory).
        if (SvgDecoder.TryProbeDimensions(path, out var svg) && svg.IsValid) return svg;
        if (BuiltInRasterDecoder.TryProbeDimensions(path, out var raster) && raster.IsValid) return raster;
        if (ImageHeaderProbe.TryProbe(path, out var dimensions)) return dimensions;
        if (NativeImageProbe.TryProbe(path, out var native) && native.Width > 0 && native.Height > 0)
            return new ImageDimensions((int)Math.Min(native.Width, (uint)int.MaxValue), (int)Math.Min(native.Height, (uint)int.MaxValue));
        var provider = _backend.Probe(path);
        if (provider is { Supported: true, Dimensions.IsValid: true }) return provider.Value.Dimensions;
        return default;
    }

    private static bool RequiresPreview(ImageDimensions source, ImagePerformancePolicy policy) =>
        source.IsValid && (source.Width > policy.PreviewMaxWidth || source.Height > policy.PreviewMaxHeight);

    /// <summary>
    /// Converts the 2-D viewport box to the one-dimensional DecodeToWidth/Height API used by generic
    /// managed codecs. This preserves source aspect ratio and therefore avoids the former portrait-in-
    /// landscape overdecode even for formats that do not expose a native bounded decode interface.
    /// </summary>
    private static int RequiredPreviewLongestSide(ImageDimensions source, ImagePerformancePolicy policy)
    {
        if (!source.IsValid) return policy.PreviewLongestSide;
        var scale = Math.Min(1.0, Math.Min(policy.PreviewMaxWidth / (double)source.Width,
                                          policy.PreviewMaxHeight / (double)source.Height));
        return Math.Max(1, (int)Math.Ceiling(source.LongestSide * scale));
    }

    private async Task<ImageDimensions> ProbeDimensionsAsync(string path, Stream alreadyOpenStream, CancellationToken cancellationToken)
    {
        // Dedicated built-in decoders first (see ProbeDimensionsAsyncDirect for why).
        if (SvgDecoder.TryProbeDimensions(path, out var svg) && svg.IsValid) return svg;

        if (BuiltInRasterDecoder.TryProbeDimensions(path, out var raster) && raster.IsValid) return raster;

        // Otherwise probe from the exact stream that will be decoded, restoring its position
        // afterwards. This removes Glide 2.1's normal second file-open/header-read pass.
        var header = await ImageHeaderProbe.TryProbeAsync(alreadyOpenStream, cancellationToken).ConfigureAwait(false);
        if (header is { IsValid: true }) return header.Value;

        if (NativeImageProbe.TryProbe(path, out var native) && native.Width > 0 && native.Height > 0)
            return new ImageDimensions((int)Math.Min(native.Width, (uint)int.MaxValue), (int)Math.Min(native.Height, (uint)int.MaxValue));

        var provider = await _backend.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        return provider is { Supported: true, Dimensions.IsValid: true } ? provider.Value.Dimensions : default;
    }

    public void PurgeWarmCache() => PurgeCaches();
    public void PurgeCaches()
    {
        lock (_cacheGate)
        {
            _cacheEpoch++;
            _prefetchCts.Cancel();
            foreach (var entry in _compressed.Values) entry.Buffer.Dispose();
            _compressed.Clear();
            _compressedLru.Clear();
            _compressedBytes = 0;
            foreach (var entry in _prepared.Values)
            {
                if (!string.Equals(entry.Node.Value, _currentActivePath, StringComparison.OrdinalIgnoreCase) &&
                    IsBitmapInActiveUse?.Invoke(entry.Bitmap) != true)
                    entry.Bitmap.Dispose();
            }
            _prepared.Clear();
            _preparedLru.Clear();
            _preparedBytes = 0;
        }
    }

    public void InvalidatePending() => Interlocked.Increment(ref _generation);

    /// <summary>Evicts cached compressed/decoded data for one source path so an explicit user refresh
    /// re-reads bytes from disk even when file metadata granularity would otherwise make a cache entry look current.</summary>
    public void InvalidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        InvalidateCachedPath(Path.GetFullPath(path));
    }

    public ImageCacheSnapshot GetCacheSnapshot()
    {
        lock (_cacheGate) return new(_compressed.Count, _compressedBytes, _prepared.Count, _preparedBytes, Volatile.Read(ref _cacheEpoch));
    }

    private bool TryGetCompressed(string path, out NativeCompressedBuffer? buffer)
    {
        buffer = null;
        if (!TryIdentity(path, out var identity)) { InvalidateCachedPath(path); return false; }
        lock (_cacheGate)
        {
            if (!_compressed.TryGetValue(path, out var entry)) return false;
            if (entry.Identity != identity) { RemoveCompressedLocked(path, entry); return false; }
            _compressedLru.Remove(entry.Node); _compressedLru.AddFirst(entry.Node);
            buffer = entry.Buffer;
            return true;
        }
    }

    private bool AddCompressed(string path, NativeCompressedBuffer buffer, FileIdentity identity, long epoch)
    {
        var budget = Policy.CompressedCacheMegabytes * 1024L * 1024L;
        if (buffer.Length > ImagePerformanceGovernor.GetMaxSpeculativeCompressedFileBytes(Policy) || buffer.Length > budget || buffer.Length != identity.Length) return false;
        lock (_cacheGate)
        {
            if (epoch != _cacheEpoch) return false;
            if (_compressed.TryGetValue(path, out var old)) RemoveCompressedLocked(path, old);
            var node = _compressedLru.AddFirst(path);
            _compressed[path] = new CompressedEntry(buffer, identity, node);
            _compressedBytes += buffer.Length;
            TrimCompressedLocked();
            return true;
        }
    }

    private bool TryGetPrepared(string path, out PreparedFrame frame)
    {
        frame = default;
        if (!TryIdentity(path, out var identity)) { InvalidateCachedPath(path); return false; }
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(path, out var entry)) return false;
            if (entry.Identity != identity) { RemovePreparedLocked(path, entry, dispose: true); return false; }
            _preparedLru.Remove(entry.Node);
            _preparedLru.AddFirst(entry.Node);
            frame = new PreparedFrame(entry.Bitmap, entry.IsFull, entry.Source);
            return true;
        }
    }

    /// <summary>
    /// Removes a cached preview without disposing it when the viewport is still displaying that
    /// exact bitmap. An authoritative full load may replace the cache entry while the UI is
    /// awaiting the decoder; transferring the preview out of the cache prevents that replacement
    /// from disposing a bitmap still owned by the viewport.
    /// </summary>
    public bool DetachPreparedPreview(string path, Bitmap displayed)
    {
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(path, out var entry) || entry.IsFull ||
                !ReferenceEquals(entry.Bitmap, displayed))
                return false;

            RemovePreparedLocked(path, entry, dispose: false);
            return true;
        }
    }

    internal void AddPrepared(string path, Bitmap bitmap, bool isFull, ImageDimensions source, long epoch = -1)
    {
        var estimate = EstimatedDecodedBytes(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var budget = Policy.DecodedCacheMegabytes * 1024L * 1024L;
        if (estimate <= 0 || estimate > budget) { bitmap.Dispose(); return; }
        if (!TryIdentity(path, out var identity)) { bitmap.Dispose(); return; }
        lock (_cacheGate)
        {
            if (epoch >= 0 && epoch != _cacheEpoch) { bitmap.Dispose(); return; }
            if (_prepared.TryGetValue(path, out var old))
            {
                if (old.IsFull && !isFull) { bitmap.Dispose(); return; }
                RemovePreparedLocked(path, old, dispose: true);
            }
            var node = _preparedLru.AddFirst(path);
            _prepared[path] = new PreparedEntry(bitmap, isFull, source, identity, estimate, node);
            _preparedBytes += estimate;
            TrimPreparedLocked();
        }
    }

    private string? _currentActivePath;
    public void SetActivePath(string? path)
    {
        lock (_cacheGate) _currentActivePath = path;
    }

    public bool IsBitmapCached(Bitmap? bitmap)
    {
        if (bitmap is null) return false;
        lock (_cacheGate)
        {
            foreach (var entry in _prepared.Values)
            {
                if (ReferenceEquals(entry.Bitmap, bitmap)) return true;
            }
            return false;
        }
    }

    private void InvalidateCachedPath(string path)
    {
        lock (_cacheGate)
        {
            if (_compressed.TryGetValue(path, out var c)) RemoveCompressedLocked(path, c);
            if (_prepared.TryGetValue(path, out var p)) RemovePreparedLocked(path, p, dispose: true);
        }
    }

    private void RemoveCompressedLocked(string path, CompressedEntry entry)
    {
        _compressedBytes -= entry.Buffer.Length;
        _compressedLru.Remove(entry.Node);
        _compressed.Remove(path);
        entry.Buffer.Dispose();
    }

    private void RemovePreparedLocked(string path, PreparedEntry entry, bool dispose)
    {
        _preparedBytes -= entry.EstimatedBytes;
        _preparedLru.Remove(entry.Node);
        _prepared.Remove(path);
        if (dispose)
        {
            if (IsBitmapInActiveUse?.Invoke(entry.Bitmap) == true)
            {
                // Active in viewport or mid-transition; skip disposal so we don't cause black flashes.
                // The viewport will release and dispose via PriorBitmapReleased when done.
            }
            else
            {
                entry.Bitmap.Dispose();
            }
        }
    }

    private void TrimCaches()
    {
        lock (_cacheGate) { TrimCompressedLocked(); TrimPreparedLocked(); }
    }
    private void TrimCompressedLocked()
    {
        var itemLimit = Policy.CompressedCacheItems;
        var byteLimit = Policy.CompressedCacheMegabytes * 1024L * 1024L;
        while ((_compressed.Count > itemLimit || _compressedBytes > byteLimit) && _compressedLru.Last is { } last)
        {
            if (_compressed.TryGetValue(last.Value, out var entry)) RemoveCompressedLocked(last.Value, entry);
            else _compressedLru.RemoveLast();
        }
    }
    private void TrimPreparedLocked()
    {
        var byteLimit = Policy.DecodedCacheMegabytes * 1024L * 1024L;
        var node = _preparedLru.Last;
        while (_preparedBytes > byteLimit && node is not null)
        {
            var prev = node.Previous;
            var path = node.Value;
            if (string.Equals(path, _currentActivePath, StringComparison.OrdinalIgnoreCase))
            {
                node = prev;
                continue;
            }
            if (_prepared.TryGetValue(path, out var entry))
                RemovePreparedLocked(path, entry, dispose: true);
            else
                _preparedLru.Remove(node);
            node = prev;
        }
    }

    private static bool TryIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            identity = new FileIdentity(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch { return false; }
    }
    public static bool IsDecompressionBomb(ImageDimensions dimensions)
    {
        if (!dimensions.IsValid) return false;
        if (dimensions.Width > ImageHeaderProbe.MaxDimension || dimensions.Height > ImageHeaderProbe.MaxDimension)
            return true;
        var pixels = (long)dimensions.Width * dimensions.Height;
        return pixels > ImageHeaderProbe.MaxTotalPixels;
    }

    /// <summary>
    /// The lightweight header probe reports the raw stored pixel dimensions for JPEG/TIFF/WebP eXIf
    /// files, while the codec may apply the EXIF orientation and return a 90°-rotated bitmap. The
    /// viewport sizes its destination rectangle from the reported source size and maps the bitmap
    /// onto it, so a transposed source stretches the picture. Align the source orientation with the
    /// bitmap the decoder actually produced by comparing the bitmap aspect ratio against both the
    /// direct and transposed source aspect ratios. Square sources are never ambiguous.
    /// </summary>
    internal static ImageDimensions NormalizeSourceOrientation(ImageDimensions source, Bitmap bitmap)
        => NormalizeSourceOrientation(source, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

    internal static ImageDimensions NormalizeSourceOrientation(ImageDimensions source, int bitmapWidth, int bitmapHeight)
    {
        if (!source.IsValid || bitmapWidth <= 0 || bitmapHeight <= 0) return source;
        if (source.Width == source.Height) return source;

        var sourceAspect = (double)source.Width / source.Height;
        var bitmapAspect = (double)bitmapWidth / bitmapHeight;
        var directError = Math.Abs(Math.Log(bitmapAspect / sourceAspect));
        var transposedAspect = (double)source.Height / source.Width;
        var transposeError = Math.Abs(Math.Log(bitmapAspect / transposedAspect));
        return transposeError < directError ? new ImageDimensions(source.Height, source.Width) : source;
    }

    private static long EstimatedDecodedBytes(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        try { return checked((long)width * height * 4L); } catch { return long.MaxValue; }
    }

    public void Dispose()
    {
        PurgeCaches();
        _prefetchCts.Dispose();
        _activitySettleCts.Cancel(); _activitySettleCts.Dispose();
        _foregroundGate.Dispose(); _refinementGate.Dispose(); _speculativeGate.Dispose(); _compressedWarmGate.Dispose();
    }

    private readonly record struct PreparedFrame(Bitmap Bitmap, bool IsFull, ImageDimensions Source);
}

public sealed record ImageLoadResult(
    string Path,
    Bitmap Bitmap,
    TimeSpan DecodeTime,
    bool CacheHit = false,
    bool IsPreview = false,
    int SourceWidth = 0,
    int SourceHeight = 0,
    long Generation = 0,
    bool PreparedFrameHit = false,
    string DecodeRoute = "stream");

public readonly record struct ImageCacheSnapshot(int CompressedItems, long CompressedBytes, int DecodedItems, long DecodedBytes, long Epoch);
