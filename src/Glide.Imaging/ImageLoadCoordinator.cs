using Avalonia.Media.Imaging;
using Glide.Core;

namespace Glide.Imaging;

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

    private sealed record CompressedEntry(byte[] Bytes, FileIdentity Identity, LinkedListNode<string> Node);
    private sealed record PreparedEntry(Bitmap Bitmap, bool IsFull, ImageDimensions Source, FileIdentity Identity, long EstimatedBytes, LinkedListNode<string> Node);
    private readonly record struct FileIdentity(long Length, long LastWriteTicksUtc);

    public ImageLoadCoordinator(IImageDecoderBackend? backend = null) => _backend = backend ?? new AvaloniaImageDecoderBackend();

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

    public async Task<ImageLoadResult?> LoadForegroundAsync(string path, ImagePerformancePolicy policy, CancellationToken cancellationToken = default)
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

        if (TryTakePrepared(path, out var prepared))
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_prepared_cache_hit", path);
            return new ImageLoadResult(path, prepared.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                CacheHit: true, IsPreview: !prepared.IsFull, SourceWidth: prepared.Source.Width,
                SourceHeight: prepared.Source.Height, Generation: generation, PreparedFrameHit: true,
                DecodeRoute: "prepared-cache");
        }

        await _foregroundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Windows first-paint fast path. Probe only the compact image header off-thread, then let
            // a path-aware backend consume either an already-cached Shell thumbnail or a codec-native
            // reduced frame. On a hit this avoids opening a second managed FileStream, avoids reading
            // the compressed file into managed memory, and (for large JPEG/JPEG-XR) avoids materialising
            // the full source raster before the user sees anything.
            ImageDimensions source = default;
            if (policy.DecoderScaledFirstFrame && _backend is IPathOptimizedImageDecoderBackend pathBackend &&
                pathBackend.SupportsPathPreview(path))
            {
                source = await Task.Run(() => ProbeDimensions(path), cancellationToken).ConfigureAwait(false);
                if (source.IsValid && RequiresPreview(source, policy))
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
                        return new ImageLoadResult(path, pathPreview.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                            CacheHit: pathPreview.Route == "shell-cache", IsPreview: pathPreview.IsPreview,
                            SourceWidth: source.Width, SourceHeight: source.Height, Generation: generation, PreparedFrameHit: false,
                            DecodeRoute: pathPreview.Route);
                    }
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_path_preview_miss", path);
                }
            }

            var (stream, compressedHit) = await OpenDecodeStreamAsync(path, policy.SequentialForegroundReads, cancellationToken).ConfigureAwait(false);
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_stream_ready", compressedHit ? "compressed_cache" : "file");
            using (stream)
            {
                if (!source.IsValid)
                    source = await ProbeDimensionsAsync(path, stream, cancellationToken).ConfigureAwait(false);
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_probe_ready", source.IsValid ? $"{source.Width}x{source.Height}" : "unknown");
                Bitmap bitmap;
                var isPreview = false;
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_decode_start", source.IsValid ? $"{source.Width}x{source.Height}" : "unknown");
                if (policy.DecoderScaledFirstFrame && source.IsValid && RequiresPreview(source, policy))
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
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_load_end", $"elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms");
                return new ImageLoadResult(path, bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                    compressedHit, isPreview, source.Width, source.Height, generation, false,
                    compressedHit ? "compressed-stream" : "stream");
            }
        }
        finally { _foregroundGate.Release(); }
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

        if (TryTakePrepared(firstFrame.Path, out var prepared) && prepared.IsFull)
        {
            if (firstFrame.Generation != Volatile.Read(ref _generation)) { prepared.Bitmap.Dispose(); return null; }
            if ((long)prepared.Bitmap.PixelSize.Width * prepared.Bitmap.PixelSize.Height <=
                (long)firstFrame.Bitmap.PixelSize.Width * firstFrame.Bitmap.PixelSize.Height)
            { prepared.Bitmap.Dispose(); return null; }
            var preparedBudget = Policy.DecodedCacheMegabytes * 1024L * 1024L;
            var preparedBytes = EstimatedDecodedBytes(prepared.Bitmap.PixelSize.Width, prepared.Bitmap.PixelSize.Height);
            var firstBytes = EstimatedDecodedBytes(firstFrame.Bitmap.PixelSize.Width, firstFrame.Bitmap.PixelSize.Height);
            if (preparedBytes > Math.Max(0, preparedBudget - firstBytes))
            { prepared.Bitmap.Dispose(); return null; }
            return new ImageLoadResult(firstFrame.Path, prepared.Bitmap, TimeSpan.Zero, true, false,
                prepared.Source.Width, prepared.Source.Height, firstFrame.Generation, true);
        }
        if (prepared.Bitmap is not null) prepared.Bitmap.Dispose();

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
                if (!source.IsValid) return false;
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
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (!TryIdentity(path, out var after) || after != before || bytes.LongLength != before.Length) return;
            if (epoch != Volatile.Read(ref _cacheEpoch)) return;
            if (AddCompressed(path, bytes, before, epoch) && GlidePerformanceTrace.Enabled)
                GlidePerformanceTrace.Mark("compressed_cache_admitted", $"bytes={bytes.LongLength}");
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally { _compressedWarmGate.Release(); }
    }

    private async Task<(Stream Stream, bool CacheHit)> OpenDecodeStreamAsync(string path, bool sequential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TryGetCompressed(path, out var bytes) && bytes is not null)
            return (new MemoryStream(bytes, writable: false), true);

        // Opening a cold/network/slow-storage file can itself stall. Keep even handle acquisition off
        // the UI continuation; foreground decode retains priority but never owns the UI thread.
        var stream = await Task.Run<Stream>(() =>
        {
            token.ThrowIfCancellationRequested();
            var options = FileOptions.Asynchronous | (sequential ? FileOptions.SequentialScan : FileOptions.None);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, options);
        }, token).ConfigureAwait(false);
        return (stream, false);
    }

    private ImageDimensions ProbeDimensions(string path)
    {
        // Cheap signature/header probe first. This guarantees common formats never wake the optional
        // provider runtime merely to learn dimensions. WIC is next; lazy providers are last.
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
        // First probe from the exact stream that will be decoded, restoring its position afterwards.
        // This removes Glide 2.1's normal second file-open/header-read pass.
        var header = await ImageHeaderProbe.TryProbeAsync(alreadyOpenStream, cancellationToken).ConfigureAwait(false);
        if (header is { IsValid: true }) return header.Value;

        var nativeDimensions = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeImageProbe.TryProbe(path, out var native) && native.Width > 0 && native.Height > 0)
                return new ImageDimensions((int)Math.Min(native.Width, (uint)int.MaxValue), (int)Math.Min(native.Height, (uint)int.MaxValue));
            return default;
        }, cancellationToken).ConfigureAwait(false);
        if (nativeDimensions.IsValid) return nativeDimensions;

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
            _compressed.Clear();
            _compressedLru.Clear();
            _compressedBytes = 0;
            foreach (var entry in _prepared.Values) entry.Bitmap.Dispose();
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

    private bool TryGetCompressed(string path, out byte[]? bytes)
    {
        bytes = null;
        if (!TryIdentity(path, out var identity)) { InvalidateCachedPath(path); return false; }
        lock (_cacheGate)
        {
            if (!_compressed.TryGetValue(path, out var entry)) return false;
            if (entry.Identity != identity) { RemoveCompressedLocked(path, entry); return false; }
            _compressedLru.Remove(entry.Node); _compressedLru.AddFirst(entry.Node);
            bytes = entry.Bytes;
            return true;
        }
    }

    private bool AddCompressed(string path, byte[] bytes, FileIdentity identity, long epoch)
    {
        var budget = Policy.CompressedCacheMegabytes * 1024L * 1024L;
        if (bytes.LongLength > ImagePerformanceGovernor.GetMaxSpeculativeCompressedFileBytes(Policy) || bytes.LongLength > budget || bytes.LongLength != identity.Length) return false;
        lock (_cacheGate)
        {
            if (epoch != _cacheEpoch) return false;
            if (_compressed.TryGetValue(path, out var old)) RemoveCompressedLocked(path, old);
            var node = _compressedLru.AddFirst(path);
            _compressed[path] = new CompressedEntry(bytes, identity, node);
            _compressedBytes += bytes.LongLength;
            TrimCompressedLocked();
            return true;
        }
    }

    private bool TryTakePrepared(string path, out PreparedFrame frame)
    {
        frame = default;
        if (!TryIdentity(path, out var identity)) { InvalidateCachedPath(path); return false; }
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(path, out var entry)) return false;
            if (entry.Identity != identity) { RemovePreparedLocked(path, entry, dispose: true); return false; }
            RemovePreparedLocked(path, entry, dispose: false);
            frame = new PreparedFrame(entry.Bitmap, entry.IsFull, entry.Source);
            return true;
        }
    }

    private void AddPrepared(string path, Bitmap bitmap, bool isFull, ImageDimensions source, long epoch)
    {
        var estimate = EstimatedDecodedBytes(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var budget = Policy.DecodedCacheMegabytes * 1024L * 1024L;
        if (estimate <= 0 || estimate > budget) { bitmap.Dispose(); return; }
        if (!TryIdentity(path, out var identity)) { bitmap.Dispose(); return; }
        lock (_cacheGate)
        {
            if (epoch != _cacheEpoch) { bitmap.Dispose(); return; }
            if (_prepared.TryGetValue(path, out var old)) RemovePreparedLocked(path, old, dispose: true);
            var node = _preparedLru.AddFirst(path);
            _prepared[path] = new PreparedEntry(bitmap, isFull, source, identity, estimate, node);
            _preparedBytes += estimate;
            TrimPreparedLocked();
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
        _compressedBytes -= entry.Bytes.LongLength;
        _compressedLru.Remove(entry.Node);
        _compressed.Remove(path);
    }

    private void RemovePreparedLocked(string path, PreparedEntry entry, bool dispose)
    {
        _preparedBytes -= entry.EstimatedBytes;
        _preparedLru.Remove(entry.Node);
        _prepared.Remove(path);
        if (dispose) entry.Bitmap.Dispose();
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
        while (_preparedBytes > byteLimit && _preparedLru.Last is { } last)
        {
            if (_prepared.TryGetValue(last.Value, out var entry)) RemovePreparedLocked(last.Value, entry, dispose: true);
            else _preparedLru.RemoveLast();
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
