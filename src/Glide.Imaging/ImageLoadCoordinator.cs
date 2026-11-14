using System.Buffers;
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
    // The cache owns one reference; every read stream holds another. Cache eviction (trim, purge on
    // minimize, invalidation) used to free the memory while a decoder was still reading it.
    private int _refCount = 1;

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
        ObjectDisposedException.ThrowIf(_disposed != 0 || !TryAddRef(), this);
        return new LeasedReadStream(this, (byte*)_pointer, _length);
    }

    private bool TryAddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current == 0) return false;
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current) return true;
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _refCount) != 0) return;
        var ptr = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (ptr != IntPtr.Zero)
        {
            unsafe { NativeMemory.Free((void*)ptr); }
        }
    }

    private sealed unsafe class LeasedReadStream(NativeCompressedBuffer owner, byte* pointer, long length)
        : UnmanagedMemoryStream(pointer, length, length, FileAccess.Read)
    {
        private int _released;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }

    public unsafe UnmanagedMemoryStream CreateWriteStream()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return new UnmanagedMemoryStream((byte*)_pointer, _length, _length, FileAccess.Write);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Release();
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
    private readonly SemaphoreSlim _speculativeGate = new(ImagePerformanceGovernor.SpeculativeDecodeLanes, ImagePerformanceGovernor.SpeculativeDecodeLanes);
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

    // Deterministic bitmap lifetime (replaces the old "skip disposal and leave it to the GC" path).
    // Every cache-owned bitmap handed to a caller in an ImageLoadResult carries one presentation lease
    // until the caller calls ReleaseBitmap. Cache eviction, purge or promotion never disposes a leased
    // (or viewport-active) bitmap: it is moved to _retired instead and disposed by the final
    // ReleaseBitmap, after the caller's presentation fence. Guarded by _cacheGate.
    private readonly Dictionary<Bitmap, int> _leases = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Bitmap> _retired = new(ReferenceEqualityComparer.Instance);
    // Neighbour preparations currently decoding, so a foreground request for the same file joins
    // the in-flight decode instead of duplicating it. Guarded by _cacheGate.
    private readonly Dictionary<string, Task> _inflightPrepares = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Concurrent neighbour decodes per prefetch run (hardware tier: 1 constrained, 2 mainstream, 3-6 high-end).</summary>
    private static int PrefetchDecodeLanes => ImagePerformanceGovernor.SpeculativeDecodeLanes;

    /// <summary>Test seam: observes every bitmap disposal performed by the coordinator.</summary>
    internal Action<Bitmap>? BitmapDisposedForTests { get; set; }

    public ImageLoadCoordinator(IImageDecoderBackend? backend = null) => _backend = backend ?? new AvaloniaImageDecoderBackend();

    /// <summary>
    /// Extra safety net consulted before the cache disposes a bitmap: true while the viewport still
    /// shows it. Ownership itself is tracked with presentation leases (see <see cref="ReleaseBitmap"/>);
    /// a bitmap that is in active use is retired, never leaked to the GC.
    /// </summary>
    public Func<Bitmap?, bool>? IsBitmapInActiveUse { get; set; }

    /// <summary>
    /// The caller no longer presents <paramref name="bitmap"/> (call it after the compositor fence for
    /// a replaced frame, or immediately for a stale/rejected result). Releases one presentation lease;
    /// when none remain the bitmap is disposed unless the cache still owns it. Bitmaps that were never
    /// cached (rapid previews, cacheResult:false loads) are disposed here as well, so the caller has a
    /// single release rule for every <see cref="ImageLoadResult"/> it received.
    /// </summary>
    /// <returns>True when the bitmap was disposed by this call.</returns>
    public bool ReleaseBitmap(Bitmap? bitmap)
    {
        if (bitmap is null) return false;
        lock (_cacheGate)
        {
            if (_leases.TryGetValue(bitmap, out var count))
            {
                if (count > 1) { _leases[bitmap] = count - 1; return false; }
                _leases.Remove(bitmap);
            }
            if (IsCachedLocked(bitmap)) return false;
            _retired.Remove(bitmap);
        }
        return DisposeBitmap(bitmap);
    }

    /// <summary>Number of presentation leases currently held on <paramref name="bitmap"/> (diagnostics/tests).</summary>
    public int GetLeaseCount(Bitmap? bitmap)
    {
        if (bitmap is null) return 0;
        lock (_cacheGate) return _leases.TryGetValue(bitmap, out var count) ? count : 0;
    }

    /// <summary>Bitmaps evicted from the cache while still presented, awaiting their final release.</summary>
    public int RetiredBitmapCount { get { lock (_cacheGate) return _retired.Count; } }

    private void LeaseLocked(Bitmap bitmap) => _leases[bitmap] = _leases.TryGetValue(bitmap, out var count) ? count + 1 : 1;

    private bool IsCachedLocked(Bitmap bitmap)
    {
        foreach (var entry in _prepared.Values)
            if (ReferenceEquals(entry.Bitmap, bitmap)) return true;
        return false;
    }

    /// <summary>Cache gave up ownership of <paramref name="bitmap"/>: dispose now unless someone still presents it.</summary>
    private void DisposeOrRetireLocked(Bitmap bitmap)
    {
        if (_leases.ContainsKey(bitmap) || IsBitmapInActiveUse?.Invoke(bitmap) == true)
        {
            _retired.Add(bitmap);
            return;
        }
        DisposeBitmap(bitmap);
    }

    /// <summary>Disposes once; a repeated release of the same bitmap is a no-op. Returns true when disposed now.</summary>
    private bool DisposeBitmap(Bitmap bitmap)
    {
        lock (_disposedGate)
        {
            if (_disposedByLoader.TryGetValue(bitmap, out _)) return false;
            _disposedByLoader.Add(bitmap, DisposedMarker);
        }
        BitmapDisposedForTests?.Invoke(bitmap);
        bitmap.Dispose();
        return true;
    }

    private static readonly object DisposedMarker = new();
    private readonly object _disposedGate = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, object> _disposedByLoader = new();

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

        if (!requireFull && TryGetPrepared(path, out var prepared, lease: true))
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_prepared_cache_hit", path);
            return new ImageLoadResult(path, prepared.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                CacheHit: true, IsPreview: !prepared.IsFull, SourceWidth: prepared.Source.Width,
                SourceHeight: prepared.Source.Height, Generation: generation, PreparedFrameHit: true,
                DecodeRoute: "prepared-cache");
        }

        // Fix (perf): await the preload with WaitAsync(timeout) instead of blocking task.Wait(500).
        var preloadedResult = await StartupImagePreloader.TryConsumeAsync(path, cancellationToken).ConfigureAwait(false);
        if (preloadedResult is not null)
        {
            using (preloadedResult)
            {
                Bitmap? preloadedBitmap = null;
                var route = "startup-preloaded";
                if (preloadedResult.Bitmap is not null)
                {
                    preloadedBitmap = preloadedResult.Bitmap;
                    preloadedResult.IsConsumed = true;
                }
                else if (preloadedResult.NativeImage.Data != IntPtr.Zero && preloadedResult.NativeStatus == 1)
                {
                    // TryCreateBitmapFromNative always frees the native buffer (success or failure).
                    // The copy into the bitmap (tens of MB for a full-resolution preload) must not run on
                    // the UI thread, which is the caller here.
                    preloadedResult.IsConsumed = true;
                    var native = preloadedResult.NativeImage;
                    var nativeStatus = preloadedResult.NativeStatus;
                    preloadedBitmap = await Task.Run(() =>
                        NativeImageDecoder.TryCreateBitmapFromNative(native, nativeStatus, out var nativeBitmap) ? nativeBitmap : null,
                        CancellationToken.None).ConfigureAwait(false);
                    if (preloadedBitmap is not null) route = "startup-native-preloaded";
                }
                else if (preloadedResult.PreloadedBytes is not null && preloadedResult.PreloadedBytes.Length > 0 &&
                         preloadedResult.Dimensions.IsValid && DecodeMemoryGuard.AllowsFullDecode(preloadedResult.Dimensions))
                {
                    // Full decode without a scaled path: only for a frame of known size within the pixel
                    // cap and the memory guard (the preloader enforces the same rule; this is the backstop).
                    using var ms = new MemoryStream(preloadedResult.PreloadedBytes, writable: false);
                    preloadedBitmap = _backend.DecodeFull(ms, path);
                    preloadedResult.IsConsumed = true;
                    route = "startup-memory-preloaded";
                }

                if (preloadedBitmap is not null)
                {
                    var preloadedSource = preloadedResult.Dimensions.IsValid
                        ? NormalizeSourceOrientation(preloadedResult.Dimensions, preloadedBitmap)
                        : new ImageDimensions(preloadedBitmap.PixelSize.Width, preloadedBitmap.PixelSize.Height);
                    // The preloader decodes large JPEGs at the screen-sized preview box (see
                    // StartupImagePreloader), so report a reduced frame as a preview: refinement and the
                    // quality-recovery path then fetch full resolution exactly as for any other preview.
                    var preloadedIsPreview = preloadedBitmap.PixelSize.Width < preloadedSource.Width ||
                                             preloadedBitmap.PixelSize.Height < preloadedSource.Height;
                    if (cacheResult) TryAddPrepared(path, preloadedBitmap, isFull: !preloadedIsPreview, preloadedSource, epoch: -1, lease: true);
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_startup_preload_hit", $"{route};preview={preloadedIsPreview}");
                    return new ImageLoadResult(path, preloadedBitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                        CacheHit: true, IsPreview: preloadedIsPreview, SourceWidth: preloadedSource.Width,
                        SourceHeight: preloadedSource.Height, Generation: generation, PreparedFrameHit: false,
                        DecodeRoute: route);
                }
            }
        }

        // Foreground miss. If a neighbour preparation is already decoding this exact file, join it
        // instead of starting a second decode of the same pixels (the prepared re-check below then
        // hits). Otherwise stop the stale prefetch run from starting more decodes: the current image
        // beats speculation, and the neighbourhood is re-planned once this frame is presented.
        if (!requireFull)
        {
            Task? inflight;
            lock (_cacheGate) _inflightPrepares.TryGetValue(path, out inflight);
            if (inflight is not null)
            {
                if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_join_inflight_prefetch", ImageFormatRegistry.GetLongestExtension(path));
                try { await inflight.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            lock (_cacheGate) _prefetchCts.Cancel();
        }

        return await Task.Run(async () =>
        {
            await _foregroundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _generation)) return null;

                if (!requireFull && TryGetPrepared(path, out var preparedWhileWaiting, lease: true))
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

                    // Formats whose codecs cannot decode at reduced size (PNG/WebP/TIFF/BMP/GIF/ICO) pay a
                    // full decode for a "preview" anyway; the Fit refinement then decoded them twice more
                    // (bounded + full). Decode once at source resolution when it fits the budget.
                    // Memory guard (see DecodeMemoryGuard): a frame within the pixel cap that would not fit
                    // the memory available right now is shown through its preview, never decoded full.
                    var overMemory = source.IsValid && !IsDecompressionBomb(source) && !DecodeMemoryGuard.FitsAvailableMemory(source);
                    var fullFirst = !requireFull && !overMemory && policy.DecoderScaledFirstFrame && source.IsValid &&
                        RequiresPreview(source, policy) && PrefersFullFirstFrame(path, source, policy);
                    if (fullFirst && GlidePerformanceTrace.Enabled)
                        GlidePerformanceTrace.Mark("foreground_full_first", $"source={source.Width}x{source.Height}");

                    if (!fullFirst && !requireFull && policy.DecoderScaledFirstFrame && _backend is IPathOptimizedImageDecoderBackend pathBackend &&
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
                                DisposeBitmap(pathPreview.Bitmap);
                                return null;
                            }
                            if (GlidePerformanceTrace.Enabled)
                                GlidePerformanceTrace.Mark("foreground_path_preview_ready",
                                    $"route={pathPreview.Route};output={pathPreview.Bitmap.PixelSize.Width}x{pathPreview.Bitmap.PixelSize.Height};elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms");
                            source = NormalizeSourceOrientation(source, pathPreview.Bitmap);
                            if (cacheResult && (!pathPreview.IsPreview || cachePreview))
                                TryAddPrepared(path, pathPreview.Bitmap, isFull: !pathPreview.IsPreview, source, epoch: -1, lease: true);
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
                        // A source beyond the safe full-decode ceiling (e.g. 20000 x 20000) can still be
                        // shown through its decoder-scaled frame. A full-resolution request (quality
                        // recovery, rapid-browse settle) keeps that frame instead of failing the open.
                        if (TryGetPrepared(path, out var bounded, lease: true))
                        {
                            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_full_refused_kept_preview", $"{source.Width}x{source.Height}");
                            return new ImageLoadResult(path, bounded.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                                CacheHit: true, IsPreview: !bounded.IsFull, SourceWidth: bounded.Source.Width,
                                SourceHeight: bounded.Source.Height, Generation: generation, PreparedFrameHit: true,
                                DecodeRoute: "prepared-cache");
                        }
                        throw new InvalidDataException(
                            $"Image dimensions ({source.Width}x{source.Height}) exceed the safe decompression bomb limit of {ImageHeaderProbe.MaxTotalPixels:N0} pixels.");
                    }

                    var scaledFirstFrame = !fullFirst && !requireFull && policy.DecoderScaledFirstFrame && source.IsValid && RequiresPreview(source, policy);
                    if (overMemory && !scaledFirstFrame)
                    {
                        // A full frame was requested (or the image has no scaled first frame) but it does
                        // not fit the free memory: keep the cached preview, else decode a bounded one.
                        if (cacheResult && TryGetPrepared(path, out var kept, lease: true))
                        {
                            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_full_refused_memory_kept_preview", $"{source.Width}x{source.Height}");
                            return new ImageLoadResult(path, kept.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                                CacheHit: true, IsPreview: !kept.IsFull, SourceWidth: kept.Source.Width,
                                SourceHeight: kept.Source.Height, Generation: generation, PreparedFrameHit: true,
                                DecodeRoute: "prepared-cache");
                        }
                        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_full_refused_memory", $"{source.Width}x{source.Height}");
                        (bitmap, isPreview) = await DecodeBoundedFallbackAsync(stream, path, source, policy, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidDataException(
                                $"There is not enough free memory to open this {source.Width}x{source.Height} image at full size.");
                    }
                    else if (scaledFirstFrame)
                    {
                        var targetLongest = RequiredPreviewLongestSide(source, policy);
                        bitmap = await _backend.DecodePreviewAsync(stream, source, targetLongest, policy.PreviewInterpolation, path, cancellationToken).ConfigureAwait(false);
                        isPreview = bitmap.PixelSize.Width < source.Width || bitmap.PixelSize.Height < source.Height;
                    }
                    else
                    {
                        try
                        {
                            bitmap = await _backend.DecodeFullAsync(stream, path, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (DecodeMemoryGuard.IsAllocationFailure(ex) && source.IsValid && RequiresPreview(source, policy))
                        {
                            // The full frame could not be allocated (memory changed since the check, or the
                            // codec needed more than the estimate): fall back to the preview instead of failing.
                            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_full_allocation_failed", $"{source.Width}x{source.Height}");
                            if (cacheResult && TryGetPrepared(path, out var kept, lease: true))
                                return new ImageLoadResult(path, kept.Bitmap, System.Diagnostics.Stopwatch.GetElapsedTime(started),
                                    CacheHit: true, IsPreview: !kept.IsFull, SourceWidth: kept.Source.Width,
                                    SourceHeight: kept.Source.Height, Generation: generation, PreparedFrameHit: true,
                                    DecodeRoute: "prepared-cache");
                            (bitmap, isPreview) = await DecodeBoundedFallbackAsync(stream, path, source, policy, cancellationToken).ConfigureAwait(false)
                                ?? throw new InvalidDataException(
                                    $"There is not enough free memory to open this {source.Width}x{source.Height} image at full size.", ex);
                        }
                        if (!source.IsValid) source = new ImageDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    }

                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("foreground_decode_ready", $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height};preview={isPreview}");
                    if (generation != Volatile.Read(ref _generation))
                    {
                        DisposeBitmap(bitmap);
                        return null;
                    }
                    if (!source.IsValid) source = new ImageDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    source = NormalizeSourceOrientation(source, bitmap);
                    // A rejected admission leaves the bitmap caller-owned (never disposed under the caller).
                    if (cacheResult && (!isPreview || cachePreview))
                        TryAddPrepared(path, bitmap, isFull: !isPreview, source, epoch: -1, lease: true);
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
        // Beyond the full-decode ceiling the caller can never get the full frame; a bounded refinement
        // would be decoded only to be discarded by MainWindow (it then asks for the full frame).
        if (IsDecompressionBomb(source)) return null;
        // Same for a frame the memory guard refuses: the caller would follow a bounded refinement with a
        // full-resolution request, which would only return the preview again.
        if (!DecodeMemoryGuard.FitsAvailableMemory(source))
        {
            if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("refinement_skipped_memory_guard", $"{source.Width}x{source.Height}");
            return null;
        }

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
            // The caller will present this cached frame: lease it (only if it is still cached).
            if (!TryLeaseCached(firstFrame.Path, prepared.Bitmap)) return null;
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
                if (!full)
                {
                    var firstLongest = Math.Max(firstFrame.Bitmap.PixelSize.Width, firstFrame.Bitmap.PixelSize.Height);
                    // A plain Fit demand only needs the image's longest side at the fitted scale: a
                    // portrait photo in a landscape window is limited by the window height, not width.
                    if (demand.DisplayPixelWidth > 0 && demand.DisplayPixelHeight > 0 && !demand.SelectionZoom && !demand.ExactPixelDemand)
                    {
                        var zoom = double.IsFinite(demand.Zoom) && demand.Zoom > 0 ? demand.Zoom : 1;
                        var fit = Math.Min(demand.DisplayPixelWidth * zoom / source.Width, demand.DisplayPixelHeight * zoom / source.Height);
                        var fitLongest = (int)Math.Ceiling(source.LongestSide * Math.Min(1.0, fit));
                        boundedTarget = Math.Min(boundedTarget, Math.Max(1, fitLongest));
                    }
                    if (firstLongest >= boundedTarget)
                    {
                        // The first frame already carries at least the pixels this demand asks for; a
                        // bounded decode could only reproduce it (JPEG picks the same DCT scale again).
                        // Measured: 24 MP JPEG wasted a whole ~38 ms decode here on every image.
                        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("refinement_skipped_first_frame_sufficient", $"first={firstLongest};target={boundedTarget}");
                        return null;
                    }
                    if (budgetAllowsFull && sourceBytes <= MaxDirectFullRefinementBytes)
                    {
                        // MainWindow discards a bounded refinement and immediately decodes the full frame
                        // (RefinePresentedAsync), and codecs without reduced-size decoding (PNG/WebP/TIFF/
                        // BMP/GIF) pay a full decode for a bounded frame anyway. Decode full once.
                        full = true;
                        if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("refinement_direct_full", $"source={sourceBytes}");
                    }
                }
                var isPreview = !full && boundedTarget < Math.Max(firstFrame.SourceWidth, firstFrame.SourceHeight);
                var bitmap = isPreview
                    ? await _backend.DecodePreviewAsync(stream, new ImageDimensions(firstFrame.SourceWidth, firstFrame.SourceHeight), boundedTarget, policy.PreviewInterpolation, firstFrame.Path, cancellationToken).ConfigureAwait(false)
                    : await _backend.DecodeFullAsync(stream, firstFrame.Path, cancellationToken).ConfigureAwait(false);
                if (firstFrame.Generation != Volatile.Read(ref _generation)) { DisposeBitmap(bitmap); return null; }
                if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height <=
                    (long)firstFrame.Bitmap.PixelSize.Width * firstFrame.Bitmap.PixelSize.Height)
                {
                    DisposeBitmap(bitmap);
                    return null;
                }
                var outputBytes = EstimatedDecodedBytes(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                if (outputBytes > Math.Max(0, budgetBytes - firstFrameBytes))
                {
                    if (GlidePerformanceTrace.Enabled) GlidePerformanceTrace.Mark("refinement_bounded_output_rejected", $"output={outputBytes};budget={budgetBytes};firstFrame={firstFrameBytes}");
                    DisposeBitmap(bitmap); return null;
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
            // The caller now presents the promoted bitmap: it holds a lease like any cached result.
            LeaseLocked(refined.Bitmap);
            // The old frame is detached by the caller before this call; if it is still leased (e.g. kept
            // as the interaction preview) it is retired and disposed by its final ReleaseBitmap.
            DisposeOrRetireLocked(old.Bitmap);
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

        // A jump outside the previous neighbourhood (random access, Home/End, a far thumbnail click)
        // makes most of the old speculation useless and the next move unpredictable: prepare the new
        // neighbourhood one decode at a time as before. Sequential browsing gets every lane.
        var lanes = PrefetchDecodeLanes;
        lock (_cacheGate)
        {
            var sameList = ReferenceEquals(paths, _lastPrefetchList) || paths.Count == _lastPrefetchCount;
            var jumped = sameList && _lastPrefetchIndex >= 0 && Math.Abs(currentIndex - _lastPrefetchIndex) > 2;
            _lastPrefetchList = paths;
            _lastPrefetchCount = paths.Count;
            _lastPrefetchIndex = currentIndex;
            if (jumped) lanes = 1;
            // Keep the nearest neighbours resident: far speculation must never evict the image the
            // user is most likely to open next (LRU order alone did, once every neighbour was touched).
            _protectedNeighbours.Clear();
            foreach (var path in plan.Take(Math.Max(2, PrefetchDecodeLanes))) _protectedNeighbours.Add(path);
        }
        _ = Task.Run(() => RunPrefetchAsync(plan, policy, epoch, lanes, cts.Token), CancellationToken.None);
    }

    private IReadOnlyList<string>? _lastPrefetchList;
    private int _lastPrefetchCount;
    private int _lastPrefetchIndex = -1;
    private readonly HashSet<string> _protectedNeighbours = new(StringComparer.OrdinalIgnoreCase);

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

    private async Task RunPrefetchAsync(IReadOnlyList<string> plan, ImagePerformancePolicy policy, long epoch, int maxLanes, CancellationToken token)
    {
        try
        {
            if (plan.Count == 0) return;

            // Glide 2.2 priority order is kept (nearest presentation-ready frames, then compressed
            // warm-up of farther files, then their frames) but each phase now uses the machine's
            // speculative lanes (1 on constrained, 2 mainstream, 3+ high-end) instead of one decode at
            // a time. Frames already prepared are skipped (they used to be re-decoded on every step).
            var lanes = Math.Clamp(maxLanes, 1, plan.Count);
            var fullRemaining = policy.FullNeighbourPredecodeCount;
            var nearest = new Task<bool>[lanes];
            for (var i = 0; i < lanes; i++)
                nearest[i] = PrepareOneAsync(plan[i], policy, epoch, allowFull: i < fullRemaining, token);
            await Task.WhenAll(nearest).ConfigureAwait(false);
            if (lanes >= plan.Count) return;

            token.ThrowIfCancellationRequested();
            var warm = new List<Task>(plan.Count - lanes);
            for (var i = lanes; i < plan.Count; i++) warm.Add(WarmOneAsync(plan[i], epoch, token));
            await Task.WhenAll(warm).ConfigureAwait(false);

            var next = lanes - 1;
            var workers = new Task[lanes];
            for (var w = 0; w < lanes; w++)
            {
                workers[w] = Task.Run(async () =>
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var index = Interlocked.Increment(ref next);
                        if (index >= plan.Count) return;
                        await PrepareOneAsync(plan[index], policy, epoch, allowFull: false, token).ConfigureAwait(false);
                    }
                }, token);
            }
            await Task.WhenAll(workers).ConfigureAwait(false);
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
        // Already presentation-ready: every navigation step re-plans the same neighbourhood, and the
        // old loop re-decoded all of it each time (up to 30 decodes per step at depth 15).
        if (HasPrepared(path, out var preparedIsFull) && (preparedIsFull || !allowFull)) return preparedIsFull;
        await _speculativeGate.WaitAsync(token).ConfigureAwait(false);
        var inflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate) _inflightPrepares[path] = inflight.Task;
        try
        {
            if (HasPrepared(path, out preparedIsFull) && (preparedIsFull || !allowFull)) return preparedIsFull;
            // Use one open stream for both the bounded header probe and decode, exactly as the
            // foreground path does. This removes a redundant open/read from the highest-value
            // neighbour-preparation path.
            var (stream, _) = await OpenDecodeStreamAsync(path, sequential: true, token).ConfigureAwait(false);
            using (stream)
            {
                var source = await ProbeDimensionsAsync(path, stream, token).ConfigureAwait(false);
                if (!source.IsValid || IsDecompressionBomb(source)) return false;
                var wantFull = allowFull && EstimatedDecodedBytes(source.Width, source.Height) <= policy.DecodedCacheMegabytes * 1024L * 1024L / 2 &&
                    DecodeMemoryGuard.FitsAvailableMemory(source);
                Bitmap bitmap;
                var isFull = false;
                if (wantFull || ((!policy.DecoderScaledFirstFrame || !RequiresPreview(source, policy)) && DecodeMemoryGuard.FitsAvailableMemory(source)))
                {
                    bitmap = await _backend.DecodeFullAsync(stream, path, token).ConfigureAwait(false);
                    isFull = true;
                }
                else
                {
                    bitmap = await _backend.DecodePreviewAsync(stream, source, RequiredPreviewLongestSide(source, policy), policy.PreviewInterpolation, path, token).ConfigureAwait(false);
                }
                if (epoch != Volatile.Read(ref _cacheEpoch)) { DisposeBitmap(bitmap); return false; }
                source = NormalizeSourceOrientation(source, bitmap);
                // Some WIC codecs (PNG) answer a bounded request at source size; record what was
                // actually produced so a source-resolution frame is never treated as a preview.
                isFull |= bitmap.PixelSize.Width >= source.Width && bitmap.PixelSize.Height >= source.Height;
                AddPrepared(path, bitmap, isFull, source, epoch);
                return wantFull && isFull;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally
        {
            lock (_cacheGate)
            {
                if (_inflightPrepares.TryGetValue(path, out var current) && ReferenceEquals(current, inflight.Task))
                    _inflightPrepares.Remove(path);
            }
            inflight.TrySetResult();
            _speculativeGate.Release();
        }
    }

    public async Task WarmAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var epoch = Volatile.Read(ref _cacheEpoch);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            await WarmOneAsync(path, epoch, cancellationToken).ConfigureAwait(false);
    }

    // Fix (perf): cache the speculative-limit computation (which queries
    // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) for 2s to avoid a per-warm syscall.
    private static long _specLimitCacheTicks;
    private static long _specLimitCacheValue;
    private static int _specLimitCacheBudgetMb;

    private static long GetCachedMaxSpeculativeCompressedFileBytes(ImagePerformancePolicy policy)
    {
        var now = Environment.TickCount64;
        var budgetMb = policy.CompressedCacheMegabytes;
        if (now - _specLimitCacheTicks < 2000 && budgetMb == _specLimitCacheBudgetMb)
            return _specLimitCacheValue;
        var value = ImagePerformanceGovernor.GetMaxSpeculativeCompressedFileBytes(policy);
        _specLimitCacheValue = value;
        _specLimitCacheBudgetMb = budgetMb;
        _specLimitCacheTicks = now;
        return value;
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
            var readLimit = GetCachedMaxSpeculativeCompressedFileBytes(policy);
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
        {
            // A concurrent trim/purge may evict the entry between lookup and lease; read from disk then.
            try { return (buffer.CreateReadStream(), true); }
            catch (ObjectDisposedException) { }
        }

        var options = FileOptions.Asynchronous | (sequential ? FileOptions.SequentialScan : FileOptions.None);
        return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, options), false);
    }

    // Fix (perf): FileStream construction performs no I/O; open directly instead of hopping
    // through Task.Run, avoiding a thread-pool scheduling delay on warm paths.
    private Task<(Stream Stream, bool CacheHit)> OpenDecodeStreamAsync(string path, bool sequential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(OpenDecodeStream(path, sequential));
    }

    private static bool RequiresPreview(ImageDimensions source, ImagePerformancePolicy policy) =>
        source.IsValid && (source.Width > policy.PreviewMaxWidth || source.Height > policy.PreviewMaxHeight);

    /// <summary>Largest frame RefineForegroundAsync decodes directly at source resolution (the decompression-bomb ceiling).</summary>
    private const long MaxDirectFullRefinementBytes = ImageHeaderProbe.MaxTotalPixels * 4L;

    /// <summary>
    /// True when the first frame should be decoded at source resolution: the codec has no reduced-size
    /// decode (so a "preview" costs a full decode plus a resample, and the Fit refinement then decoded
    /// the file twice more) and the frame fits half the decoded-cache budget - the same admission rule
    /// neighbour preparation uses for full frames, and a frame the refinement would have decoded and
    /// cached anyway. Output pixels are exactly the frame the refinement chain used to settle on.
    /// Measured (Maximum quality, 16 threads): LZW TIFF 24 MP settles in 178 ms instead of 585 ms, 100 MP
    /// in 0.76 s instead of 2.3 s, BMP 24 MP in 28 ms instead of 123 ms; first paint is no slower.
    /// </summary>
    internal static bool PrefersFullFirstFrame(string path, ImageDimensions source, ImagePerformancePolicy policy)
    {
        if (!source.IsValid || !NativeImageDecoder.IsFullDecodeOnlyFormat(path)) return false;
        // WebP is the exception: its scaled first frame is cheaper than the full frame (measured 24 MP:
        // 86 vs 90 ms, 50 MP: 156 vs 183 ms; on PCs without the WIC WebP extension both go through Skia),
        // so its first paint stays on the scaled frame and the refinement then decodes full once.
        if (NativeImageDecoder.IsWebpPath(path)) return false;
        var bytes = EstimatedDecodedBytes(source.Width, source.Height);
        var limit = Math.Min(policy.DecodedCacheMegabytes * 1024L * 1024L / 2, MaxDirectFullRefinementBytes);
        return bytes > 0 && bytes <= limit;
    }

    /// <summary>Leases <paramref name="bitmap"/> if it is still the cached frame for <paramref name="path"/>.</summary>
    private bool TryLeaseCached(string path, Bitmap bitmap)
    {
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(path, out var entry) || !ReferenceEquals(entry.Bitmap, bitmap)) return false;
            LeaseLocked(bitmap);
            return true;
        }
    }

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

    /// <summary>
    /// Decodes a preview-box frame when the full frame was refused by the memory guard or failed to
    /// allocate. Null when the image is small enough that no reduced frame exists.
    /// </summary>
    private async Task<(Bitmap Bitmap, bool IsPreview)?> DecodeBoundedFallbackAsync(Stream stream, string path, ImageDimensions source,
        ImagePerformancePolicy policy, CancellationToken cancellationToken)
    {
        if (!source.IsValid || !RequiresPreview(source, policy)) return null;
        if (stream.CanSeek) stream.Position = 0;
        var bitmap = await _backend.DecodePreviewAsync(stream, source, RequiredPreviewLongestSide(source, policy),
            policy.PreviewInterpolation, path, cancellationToken).ConfigureAwait(false);
        return (bitmap, bitmap.PixelSize.Width < source.Width || bitmap.PixelSize.Height < source.Height);
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
                // The active path's frame may be on screen without a lease (e.g. attached before
                // leases existed for it); retire it rather than dispose. Retired frames are disposed
                // when the window releases them (ReleaseBitmap) or when the coordinator is disposed.
                if (string.Equals(entry.Node.Value, _currentActivePath, StringComparison.OrdinalIgnoreCase))
                    _retired.Add(entry.Bitmap);
                else
                    DisposeOrRetireLocked(entry.Bitmap);
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
        if (buffer.Length > GetCachedMaxSpeculativeCompressedFileBytes(Policy) || buffer.Length > budget || buffer.Length != identity.Length) return false;
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

    private bool TryGetPrepared(string path, out PreparedFrame frame, bool lease = false)
    {
        frame = default;
        if (!TryIdentity(path, out var identity)) { InvalidateCachedPath(path); return false; }
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(path, out var entry)) return false;
            if (entry.Identity != identity) { RemovePreparedLocked(path, entry, dispose: true); return false; }
            _preparedLru.Remove(entry.Node);
            _preparedLru.AddFirst(entry.Node);
            // Lease inside the lock: a concurrent trim between this return and the caller attaching
            // the bitmap to the viewport can no longer dispose it.
            if (lease) LeaseLocked(entry.Bitmap);
            frame = new PreparedFrame(entry.Bitmap, entry.IsFull, entry.Source);
            return true;
        }
    }

    /// <summary>True when a prepared frame exists for <paramref name="path"/> (full or preview), without leasing it.</summary>
    private bool HasPrepared(string path, out bool isFull)
    {
        isFull = false;
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(path, out var entry)) return false;
            isFull = entry.IsFull;
            // Keep a still-relevant neighbour warm in the LRU.
            _preparedLru.Remove(entry.Node);
            _preparedLru.AddFirst(entry.Node);
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

    /// <summary>
    /// Admits <paramref name="bitmap"/> to the prepared cache. Never disposes the bitmap: on rejection
    /// (over budget, stale epoch, file changed, a full frame already cached) it stays owned by the
    /// caller. The foreground path used to return a bitmap this method had just disposed whenever the
    /// cache rejected it (e.g. a 100 MP full frame over a 256 MB budget), i.e. a use-after-dispose.
    /// When <paramref name="lease"/> is true an admitted bitmap also receives one presentation lease.
    /// </summary>
    internal bool TryAddPrepared(string path, Bitmap bitmap, bool isFull, ImageDimensions source, long epoch = -1, bool lease = false)
    {
        var estimate = EstimatedDecodedBytes(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var budget = Policy.DecodedCacheMegabytes * 1024L * 1024L;
        if (estimate <= 0 || estimate > budget) return false;
        if (!TryIdentity(path, out var identity)) return false;
        lock (_cacheGate)
        {
            if (epoch >= 0 && epoch != _cacheEpoch) return false;
            if (_prepared.TryGetValue(path, out var old))
            {
                if (ReferenceEquals(old.Bitmap, bitmap)) { if (lease) LeaseLocked(bitmap); return true; }
                if (old.IsFull && !isFull) return false;
                RemovePreparedLocked(path, old, dispose: true);
            }
            var node = _preparedLru.AddFirst(path);
            _prepared[path] = new PreparedEntry(bitmap, isFull, source, identity, estimate, node);
            _preparedBytes += estimate;
            if (lease) LeaseLocked(bitmap);
            TrimPreparedLocked();
            // Speculative frame that only fits by evicting the protected nearest neighbours: refuse it
            // (the caller disposes it) so the decoded cache stays within budget.
            if (epoch >= 0 && _preparedBytes > budget && !_protectedNeighbours.Contains(path) &&
                _prepared.TryGetValue(path, out var added) && ReferenceEquals(added.Bitmap, bitmap))
            {
                RemovePreparedLocked(path, added, dispose: false);
                return false;
            }
            return true;
        }
    }

    /// <summary>Compatibility wrapper for speculative callers: a rejected bitmap is disposed.</summary>
    internal void AddPrepared(string path, Bitmap bitmap, bool isFull, ImageDimensions source, long epoch = -1)
    {
        if (!TryAddPrepared(path, bitmap, isFull, source, epoch)) DisposeBitmap(bitmap);
    }

    private string? _currentActivePath;
    public void SetActivePath(string? path)
    {
        lock (_cacheGate) _currentActivePath = path;
    }

    public bool IsBitmapCached(Bitmap? bitmap)
    {
        if (bitmap is null) return false;
        lock (_cacheGate) return IsCachedLocked(bitmap);
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
        // Leased / viewport-active bitmaps are retired and disposed by their final ReleaseBitmap.
        if (dispose) DisposeOrRetireLocked(entry.Bitmap);
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
            if (string.Equals(path, _currentActivePath, StringComparison.OrdinalIgnoreCase) || _protectedNeighbours.Contains(path))
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
    /// files, while the codec may apply the EXIF orientation and return a 90Â°-rotated bitmap. The
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
        // End of life: nothing can present these any more (the owning window is closing).
        Bitmap[] retired;
        lock (_cacheGate)
        {
            retired = _retired.ToArray();
            _retired.Clear();
            _leases.Clear();
        }
        foreach (var bitmap in retired) DisposeBitmap(bitmap);
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
