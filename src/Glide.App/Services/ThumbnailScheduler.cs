using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Glide.Core;

namespace Glide.App.Services;

/// <summary>What a thumbnail job is for: tiles on screen first, then tiles realised just off screen, then the look-ahead that fills the disk cache.</summary>
internal enum ThumbnailPriority
{
    Visible = 0,
    /// <summary>A realised tile that is not (yet) on screen: after every visible tile, nearest first.</summary>
    Near = 1,
    Prefetch = 2,
}

/// <summary>
/// Rough warm cost, in milliseconds, of the Explorer thumbnail of a file, from its suffix and size (5.2.0). Measured by the
/// 5.2.0 format audit (docs/audits/format-speed-audit-5.2.0.md): an icon is 1 ms, a camera JPEG 13 - 26 ms, a RAW preview 4 -
/// 34 ms, a PSD 64 - 122 ms, a HEIC / AVIF / JPEG XL 46 - 114 ms, a path-heavy SVG about 200 ms. Only the order matters: the
/// scheduler starts the cheap tiles first so the screen fills sooner.
/// </summary>
internal static class ThumbnailCostModel
{
    public static double EstimateMs(string path, long sizeBytes)
    {
        var megabytes = Math.Max(0, sizeBytes) / (1024d * 1024d);
        var extension = ImageFormatRegistry.GetLongestExtension(path);
        var cost = extension switch
        {
            ".ico" or ".cur" or ".icns" or ".wbmp" or ".xbm" or ".xpm" => 1 + 2 * megabytes,
            ".jxr" or ".wdp" or ".hdp" => 3 + 0.5 * megabytes,
            ".bmp" or ".dib" => 4 + 0.3 * megabytes,
            ".dds" or ".ktx" or ".ktx2" or ".pvr" or ".astc" => 8 + 0.4 * megabytes,
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".jif" or ".jfi" or ".pjpeg" or ".pjpg" or ".mpo" => 8 + 1.2 * megabytes,
            ".gif" or ".apng" => 5 + 3 * megabytes,
            ".png" => 8 + 5 * megabytes,
            ".jp2" or ".j2k" or ".j2c" or ".jpc" or ".jpx" or ".jpf" or ".jpm" or ".mj2" => 12 + 2 * megabytes,
            ".tif" or ".tiff" or ".btf" => 10 + 3 * megabytes,
            ".webp" => 20 + 8 * megabytes,
            ".heic" or ".heif" or ".heics" or ".heifs" or ".hif" or ".avif" or ".avifs" or ".jxl" => 50 + 30 * megabytes,
            ".psd" or ".psb" or ".pdd" => 25 + 1.2 * megabytes,
            ".exr" or ".hdr" or ".rgbe" or ".pfm" => 30 + 3 * megabytes,
            ".svg" or ".svgz" => 20 + 350 * megabytes,
            _ when CodecCapabilityRegistry.CameraRawPreviewExtensions.Contains(extension) => 4 + 1.1 * megabytes,
            _ => 20 + 5 * megabytes,
        };
        return Math.Min(cost, 2000);
    }

    /// <summary>
    /// The cost class the scheduler orders by: 0 under 5 ms, 1 under 20 ms, 2 under 60 ms, 3 under 150 ms, 4 above. Coarse on
    /// purpose, so tiles of similar cost keep the order the grid asked for them in (reading order).
    /// </summary>
    public static int Bucket(double estimatedMs) => estimatedMs < 5 ? 0 : estimatedMs < 20 ? 1 : estimatedMs < 60 ? 2 : estimatedMs < 150 ? 3 : 4;
}

/// <summary>
/// One thumbnail to produce: the file, its stamp (size, last write), and the tile width. <paramref name="Order"/> ranks requests of
/// the same <see cref="ThumbnailPriority"/> for the I/O stage (smaller first): the grid passes the tile's reading position
/// (top-left to bottom-right) for visible tiles and the distance from the viewport for the rest, so files are opened in the order
/// the user's eye follows, not by name across the whole folder.
/// </summary>
internal readonly record struct ThumbnailRequest(string Path, long SizeBytes, long ModifiedTicks, int Width, long Order = 0)
{
    /// <summary>Identifies this version of the file (a changed or replaced file is a new key, so a remembered failure ends).</summary>
    public string Key => $"{Path}|{SizeBytes}|{ModifiedTicks}";
}

/// <summary>
/// Runs the Explorer tab's thumbnails (5.2.0) as two stages.
/// <list type="number">
/// <item><b>I/O stage</b> (<see cref="IoLanes"/> dedicated threads, more than there are CPU cores to decode with): the disk cache,
/// Windows' own thumbnail cache, then reading the file. A slow first open (an on-access antivirus scan, a virtual or network
/// disk) holds only one I/O lane, never a decode lane, and many opens are in flight at once. Waiting files are started visible
/// tiles first, then nearest the viewport, then look-ahead, each in on-screen reading order (<see cref="ThumbnailRequest.Order"/>).
/// Bytes read ahead are bounded (<c>maxBufferedBytes</c>), so the I/O stage cannot run away from the decoders.</item>
/// <item><b>Decode stage</b> (a bounded number of lanes): files whose bytes are ready, visible first, then cheapest cost class
/// first (<see cref="ThumbnailCostModel"/>), then in reading order. A decode that outlives <see cref="Timeout"/> releases its lane at
/// once and its tile keeps the file-type glyph; the decode itself cannot be interrupted, so it counts as abandoned (that many
/// lanes fewer until it ends) and its picture is discarded. The timeout starts when the decode starts: waiting for a disk never
/// counts against it. A read that outlives <see cref="IoTimeout"/> is given up the same way on its own lane pool.</item>
/// </list>
/// A file that failed or timed out is remembered (by path, size and last-write time) and is not retried. Without a pipeline (a plain
/// decode function) only the decode stage runs.
/// </summary>
internal sealed class ThumbnailScheduler
{
    internal const int MaxRememberedFailures = 8192;
    /// <summary>Most decode lanes a scheduler can be set to (the "Thumbnails loaded at once" setting's largest choice).</summary>
    internal const int MaxDecodeLanes = 32;
    /// <summary>Most I/O lanes a scheduler can be set to.</summary>
    internal const int MaxIoLanes = 64;
    /// <summary>Default I/O lanes for a machine with <paramref name="decodeLanes"/> decode lanes: a slow disk is latency bound, not CPU bound.</summary>
    internal static int DefaultIoLanes(int decodeLanes) => Math.Clamp(decodeLanes * 2, 4, 16);

    private static readonly TimeSpan IoWorkerIdleExit = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly List<Job> _ioPending = new();
    private readonly List<Job> _decodePending = new();
    // The same file asked for twice (a tile realised again after a layout change, a refresh): the second request waits for the read already
    // under way and takes its result instead of reading the file again (a cancelled read cannot be stopped, so without this each re-realised
    // tile cost a second open, on the disks where an open is the slow part).
    private readonly Dictionary<string, Job> _ioReading = new();
    private readonly Dictionary<string, List<Job>> _ioWaiters = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly IThumbnailPipeline _pipeline;
    private readonly bool _ioStage;
    private int _lanes;
    private int _ioLanes;
    // 5.2.0: requests attached to an identical look-ahead job already queued or running (the whole-folder preparation got there
    // first): they complete with a copy of its picture instead of reading and decoding the file a second time.
    private readonly Dictionary<string, Job> _prefetchByKey = new();
    private readonly long _maxBufferedBytes;
    private readonly bool[] _decodeLaneBusy;
    private readonly bool[] _ioLaneBusy;
    private int _running;
    private int _ioRunning;
    private int _abandoned;
    private int _ioAbandoned;
    private int _ioWorkers;
    private int _ioIdle;
    private long _bufferedBytes;
    private long _sequence;
    private long _timedOut;
    private int _peakIo;
    private int _peakDecode;
    // One burst = from the queue becoming busy to it draining; logged as a single summary line when the trace is on.
    private int _burstTiles;
    private int _burstTimedOut;
    private long _burstStart;
    private long _burstFirstTile;

    /// <summary>A decode-only scheduler: <paramref name="decode"/> (the production decode when null) runs on <paramref name="lanes"/> lanes.</summary>
    public ThumbnailScheduler(int lanes, Func<string, int, Bitmap?>? decode = null, TimeSpan? timeout = null)
        : this(lanes, new DecodeOnlyThumbnailPipeline(decode ?? ThumbnailDecode.DecodeToWidth), 1, timeout, null, 0)
    {
    }

    /// <summary>A two-stage scheduler: <paramref name="ioLanes"/> I/O lanes in front of <paramref name="lanes"/> decode lanes.</summary>
    public ThumbnailScheduler(int lanes, IThumbnailPipeline pipeline, int ioLanes, TimeSpan? timeout = null, TimeSpan? ioTimeout = null,
        long maxBufferedBytes = 192L * 1024 * 1024)
    {
        _lanes = Math.Clamp(lanes, 1, MaxDecodeLanes);
        _ioLanes = Math.Clamp(ioLanes, 1, MaxIoLanes);
        _pipeline = pipeline;
        _ioStage = pipeline.HasIoStage;
        _maxBufferedBytes = Math.Max(1, maxBufferedBytes);
        _decodeLaneBusy = new bool[MaxDecodeLanes];
        _ioLaneBusy = new bool[MaxIoLanes];
        Timeout = timeout ?? DefaultTimeout;
        IoTimeout = ioTimeout ?? DefaultIoTimeout;
    }

    /// <summary>A decode that takes longer than this is given up on (the tile keeps its glyph).</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A file read (open plus read) that takes longer than this is given up on. Much longer than <see cref="DefaultTimeout"/>: a
    /// first read of an unscanned file on a busy virtual disk legitimately takes seconds, and waiting costs no CPU lane.
    /// </summary>
    public static TimeSpan DefaultIoTimeout { get; } = TimeSpan.FromSeconds(45);

    public TimeSpan Timeout { get; set; }
    public TimeSpan IoTimeout { get; set; }

    /// <summary>Decode lanes not currently held by decodes that were given up on (never below one).</summary>
    public int EffectiveLanes => Math.Max(1, Volatile.Read(ref _lanes) - Volatile.Read(ref _abandoned));

    /// <summary>I/O lanes not currently held by reads that were given up on (never below one).</summary>
    public int EffectiveIoLanes => Math.Max(1, Volatile.Read(ref _ioLanes) - Volatile.Read(ref _ioAbandoned));

    public int IoLanes => Volatile.Read(ref _ioLanes);
    public int DecodeLanes => Volatile.Read(ref _lanes);

    /// <summary>
    /// 5.2.0: decoded thumbnails kept in memory (null: none). A request found there completes at once; every picture the scheduler
    /// produces is copied in, so the whole-folder preparation makes later tiles instant.
    /// </summary>
    public ThumbnailMemoryCache? MemoryCache { get; set; }

    /// <summary>
    /// 5.2.0 ("Thumbnails loaded at once"): changes the number of decode lanes (and I/O lanes) while running. Fewer lanes take effect as
    /// running decodes end; more start waiting work at once.
    /// </summary>
    public void SetLanes(int decodeLanes, int ioLanes)
    {
        lock (_gate)
        {
            _lanes = Math.Clamp(decodeLanes, 1, MaxDecodeLanes);
            _ioLanes = Math.Clamp(ioLanes, 1, MaxIoLanes);
        }
        Pump();
    }
    public int Abandoned => Volatile.Read(ref _abandoned);
    public int IoAbandoned => Volatile.Read(ref _ioAbandoned);
    public long TimedOutCount => Interlocked.Read(ref _timedOut);
    public int RememberedFailures => _failed.Count;

    /// <summary>Jobs waiting for an I/O lane or a decode lane.</summary>
    public int PendingCount { get { lock (_gate) return _ioPending.Count + _decodePending.Count + WaiterCount(); } }
    /// <summary>Jobs running on an I/O lane or a decode lane.</summary>
    public int RunningCount { get { lock (_gate) return _running + _ioRunning; } }
    public int IoRunningCount { get { lock (_gate) return _ioRunning; } }
    public int DecodeRunningCount { get { lock (_gate) return _running; } }
    /// <summary>File bytes held for decode lanes (read, not yet decoded), and the budget for them.</summary>
    public long BufferedBytes { get { lock (_gate) return _bufferedBytes; } }

    /// <summary>The most files in flight in each stage since construction (or <see cref="ResetPeaks"/>): the evidence that work overlaps.</summary>
    public int PeakIoInFlight { get { lock (_gate) return _peakIo; } }
    public int PeakDecodeInFlight { get { lock (_gate) return _peakDecode; } }
    public void ResetPeaks() { lock (_gate) { _peakIo = _ioRunning; _peakDecode = _running; } }

    public bool IsKnownFailure(in ThumbnailRequest request) => _failed.ContainsKey(request.Key);

    /// <summary>Forgets remembered failures (a refresh of the folder, or the settings changing what can be decoded).</summary>
    public void ForgetFailures() => _failed.Clear();

    /// <summary>
    /// Queues a thumbnail and completes with it, or null (failure, timeout, known failure). A cancelled job that has not started its
    /// decode is dropped from the queue; one that is running finishes (or times out) and its picture is discarded.
    /// The caller owns the returned bitmap.
    /// </summary>
    public Task<Bitmap?> RunAsync(ThumbnailRequest request, ThumbnailPriority priority, CancellationToken cancellationToken)
        => RunAsync(request, priority, cancellationToken, out _);

    /// <summary><see cref="RunAsync(ThumbnailRequest, ThumbnailPriority, CancellationToken)"/> that also hands back the job's timing record (filled in as it runs).</summary>
    public Task<Bitmap?> RunAsync(ThumbnailRequest request, ThumbnailPriority priority, CancellationToken cancellationToken, out ThumbnailTiming timing)
    {
        timing = new ThumbnailTiming(request, priority);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<Bitmap?>(cancellationToken);
        if (_failed.ContainsKey(request.Key)) return Task.FromResult<Bitmap?>(null);
        if (MemoryCache?.TryCreate(ThumbnailMemoryCache.KeyFor(request)) is { } remembered)
        {
            timing.Source = ThumbnailSource.Memory;
            timing.Outcome = "memory";
            return Task.FromResult<Bitmap?>(remembered);
        }
        var cost = ThumbnailCostModel.Bucket(ThumbnailCostModel.EstimateMs(request.Path, request.SizeBytes));
        var job = new Job(request, priority, cost, Interlocked.Increment(ref _sequence), cancellationToken, timing);
        if (_ioStage)
        {
            job.Plan = _pipeline.PlanIo(request);
            timing.Plan = job.Plan;
        }
        job.Registration = cancellationToken.Register(() => Cancel(job)); // before the job is visible to a lane
        lock (_gate)
        {
            if (_burstTiles == 0 && _ioPending.Count + _decodePending.Count + _running + _ioRunning + WaiterCount() == 0) _burstStart = Stopwatch.GetTimestamp();
            if (_prefetchByKey.TryGetValue(job.DupKey, out var leader) && !leader.Token.IsCancellationRequested)
            {
                // The look-ahead already has this file: raise it to this request's rank and take a copy of its picture.
                if (priority < leader.Priority) leader.Priority = priority;
                if (job.Request.Order < leader.Request.Order) leader.Request = leader.Request with { Order = job.Request.Order };
                job.Leader = leader;
                leader.Followers.Add(job);
            }
            else if (_ioStage && _ioReading.ContainsKey(job.DupKey))
            {
                // The same file is already being read: wait for that read (see _ioReading).
                if (!_ioWaiters.TryGetValue(job.DupKey, out var waiting)) _ioWaiters[job.DupKey] = waiting = new List<Job>();
                waiting.Add(job);
            }
            else if (_ioStage) _ioPending.Add(job);
            else { timing.DecodeQueuedAt = Stopwatch.GetTimestamp(); _decodePending.Add(job); }
            if (job.Leader is null && priority == ThumbnailPriority.Prefetch) _prefetchByKey[job.DupKey] = job;
        }
        if (cancellationToken.IsCancellationRequested) Cancel(job);
        Pump();
        return job.Completion.Task;
    }

    private void Cancel(Job job)
    {
        bool removed;
        lock (_gate)
        {
            removed = _ioPending.Remove(job) || _decodePending.Remove(job) || RemoveWaiter(job) || (job.Leader?.Followers.Remove(job) ?? false);
            if (removed) ReleaseReservation(job);
        }
        if (!removed) return;
        job.Prepared?.Dispose();
        job.Prepared = null;
        job.Timing.Outcome = "cancelled";
        job.Completion.TrySetCanceled(job.Token);
        Pump();
        EmitBurstIfIdle();
    }

    private int WaiterCount()
    {
        var count = 0;
        foreach (var list in _ioWaiters.Values) count += list.Count;
        return count;
    }

    private bool RemoveWaiter(Job job)
    {
        if (!_ioWaiters.TryGetValue(job.DupKey, out var list) || !list.Remove(job)) return false;
        if (list.Count == 0) _ioWaiters.Remove(job.DupKey);
        return true;
    }

    // ---- scheduling -------------------------------------------------------------------------------------------------------

    private void Pump()
    {
        while (true)
        {
            Job? next = null;
            var lane = -1;
            lock (_gate)
            {
                if (_decodePending.Count == 0 || _running >= EffectiveLanes) break;
                var best = 0;
                for (var i = 1; i < _decodePending.Count; i++)
                    if (CompareDecode(_decodePending[i], _decodePending[best]) < 0) best = i;
                next = _decodePending[best];
                _decodePending.RemoveAt(best);
                _running++;
                _peakDecode = Math.Max(_peakDecode, _running);
                lane = TakeLane(_decodeLaneBusy);
                next.Timing.DecodeInFlight = _running;
            }
            _ = Execute(next, lane);
        }
        if (_ioStage) SignalIo();
    }

    private static int CompareDecode(Job a, Job b)
    {
        var byPriority = a.Priority.CompareTo(b.Priority);
        if (byPriority != 0) return byPriority;
        var byCost = a.CostBucket.CompareTo(b.CostBucket);
        if (byCost != 0) return byCost;
        var byOrder = a.Request.Order.CompareTo(b.Request.Order);
        return byOrder != 0 ? byOrder : a.Sequence.CompareTo(b.Sequence);
    }

    /// <summary>The I/O queue is ordered by on-screen position only (visible, nearest, look-ahead; then reading order): a cheap format never overtakes a tile above it.</summary>
    private static int CompareIo(Job a, Job b)
    {
        var byPriority = a.Priority.CompareTo(b.Priority);
        if (byPriority != 0) return byPriority;
        var byOrder = a.Request.Order.CompareTo(b.Request.Order);
        return byOrder != 0 ? byOrder : a.Sequence.CompareTo(b.Sequence);
    }

    private static int TakeLane(bool[] busy)
    {
        for (var i = 0; i < busy.Length; i++)
            if (!busy[i]) { busy[i] = true; return i; }
        return -1;
    }

    private static void FreeLane(bool[] busy, int lane)
    {
        if ((uint)lane < (uint)busy.Length) busy[lane] = false;
    }

    private void ReleaseReservation(Job job)
    {
        if (job.Reserved == 0) return;
        _bufferedBytes -= job.Reserved;
        job.Reserved = 0;
    }

    // ---- I/O stage --------------------------------------------------------------------------------------------------------

    /// <summary>Wakes an idle I/O worker, or starts one (up to twice the lane count: some may be stuck in a read that timed out).</summary>
    private void SignalIo()
    {
        lock (_gate)
        {
            if (_ioPending.Count == 0 || _ioRunning >= EffectiveIoLanes) return;
            if (_ioIdle > 0) { Monitor.PulseAll(_gate); return; }
            if (_ioWorkers >= _ioLanes * 2 + 2) return;
            _ioWorkers++;
            var worker = new Thread(IoWorkerLoop, 256 * 1024) { IsBackground = true, Name = "Glide thumbnail I/O" };
            worker.Start();
        }
    }

    private void IoWorkerLoop()
    {
        while (true)
        {
            Job? job;
            var lane = -1;
            lock (_gate)
            {
                while (!TryTakeIoJob(out job, out lane))
                {
                    _ioIdle++;
                    var signalled = Monitor.Wait(_gate, IoWorkerIdleExit);
                    _ioIdle--;
                    if (!signalled && _ioPending.Count == 0)
                    {
                        _ioWorkers--;
                        return;
                    }
                }
            }
            try { RunIo(job!, lane); }
            catch { /* a failure inside the stage ends only that job (RunIo completes it) */ }
        }
    }

    /// <summary>Under the gate: the best waiting file, if an I/O lane is free and the read-ahead budget allows it.</summary>
    private bool TryTakeIoJob(out Job? job, out int lane)
    {
        job = null;
        lane = -1;
        int best;
        Job candidate;
        while (true)
        {
            if (_ioPending.Count == 0 || _ioRunning >= EffectiveIoLanes) return false;
            best = 0;
            for (var i = 1; i < _ioPending.Count; i++)
                if (CompareIo(_ioPending[i], _ioPending[best]) < 0) best = i;
            candidate = _ioPending[best];
            if (!_ioReading.ContainsKey(candidate.DupKey)) break;
            // This exact file is being read right now: wait for that read instead of opening it a second time.
            _ioPending.RemoveAt(best);
            if (!_ioWaiters.TryGetValue(candidate.DupKey, out var waiting)) _ioWaiters[candidate.DupKey] = waiting = new List<Job>();
            waiting.Add(candidate);
        }
        // Bytes that wait for a decode lane are bounded: when the budget is spent the I/O stage waits for the decoders (a file bigger than
        // the whole budget still goes when nothing else is buffered).
        if (candidate.Plan == ThumbnailIoPlan.Bytes && _bufferedBytes > 0 && _bufferedBytes + candidate.Request.SizeBytes > _maxBufferedBytes) return false;
        _ioPending.RemoveAt(best);
        _ioReading[candidate.DupKey] = candidate;
        _ioRunning++;
        _peakIo = Math.Max(_peakIo, _ioRunning);
        lane = TakeLane(_ioLaneBusy);
        if (candidate.Plan == ThumbnailIoPlan.Bytes) { candidate.Reserved = candidate.Request.SizeBytes; _bufferedBytes += candidate.Reserved; }
        candidate.IoLane = lane;
        candidate.Timing.IoLane = lane;
        candidate.Timing.IoInFlight = _ioRunning;
        candidate.Timing.IoStartedAt = Stopwatch.GetTimestamp();
        job = candidate;
        return true;
    }

    private void RunIo(Job job, int lane)
    {
        var timing = job.Timing;
        job.Registration.Dispose(); // the job can no longer be dropped from the queue; a cancel is honoured after the read
        ThumbnailPrepared? prepared = null;
        Timer? watchdog = null;
        try
        {
            if (!job.Token.IsCancellationRequested)
            {
                if (IoTimeout > TimeSpan.Zero && IoTimeout != System.Threading.Timeout.InfiniteTimeSpan)
                    watchdog = new Timer(_ => IoTimedOut(job), null, IoTimeout, System.Threading.Timeout.InfiniteTimeSpan);
                if (MemoryCache?.TryCreate(ThumbnailMemoryCache.KeyFor(job.Request)) is { } remembered)
                    prepared = new ThumbnailPrepared { Ready = remembered, Source = ThumbnailSource.Memory };
                else
                    try { prepared = _pipeline.Prepare(job.Request, job.Plan, timing); }
                catch { prepared = new ThumbnailPrepared(); } // the decode lane opens the path itself and reports the real failure
            }
        }
        finally { watchdog?.Dispose(); }
        timing.IoEndedAt = Stopwatch.GetTimestamp();

        if (Interlocked.CompareExchange(ref job.IoState, 1, 0) != 0)
        {
            // Given up on while it was reading: its job already completed; the lane was released then. Drop what it produced.
            prepared?.Ready?.Dispose();
            prepared?.Dispose();
            lock (_gate) _ioAbandoned--;
            Pump();
            return;
        }

        List<Job>? followers;
        lock (_gate)
        {
            _ioRunning--;
            FreeLane(_ioLaneBusy, lane);
            followers = EndReading(job);
        }
        CompleteIo(job, prepared, followers);
        Pump();
        EmitBurstIfIdle();
    }

    /// <summary>Under the gate: this job's read is over; returns the identical requests that were waiting for it.</summary>
    private List<Job>? EndReading(Job job)
    {
        if (_ioReading.TryGetValue(job.DupKey, out var reading) && ReferenceEquals(reading, job)) _ioReading.Remove(job.DupKey);
        return _ioWaiters.Remove(job.DupKey, out var waiting) ? waiting : null;
    }

    /// <summary>
    /// Hands the read's result to the first request that still wants it (the one that read it, or an identical request that waited for
    /// it); requests that were cancelled in the meantime complete as cancelled, and a second live request for the same file reads it again.
    /// </summary>
    private void CompleteIo(Job job, ThumbnailPrepared? prepared, List<Job>? followers)
    {
        // No result at all (the request was cancelled before its read started): nothing to hand on; live waiters read the file themselves.
        var owner = prepared is not null && !job.Token.IsCancellationRequested ? job
            : prepared is not null ? followers?.FirstOrDefault(f => !f.Token.IsCancellationRequested) : null;
        if (followers is not null)
        {
            foreach (var follower in followers)
            {
                if (ReferenceEquals(follower, owner)) continue;
                if (follower.Token.IsCancellationRequested)
                {
                    follower.Registration.Dispose();
                    Finish(follower, null, "cancelled", cancelledJob: true);
                }
                else
                {
                    lock (_gate) _ioPending.Add(follower); // two live requests for one file: the second reads it itself
                }
            }
        }
        if (owner is null || prepared is null)
        {
            lock (_gate) ReleaseReservation(job);
            prepared?.Ready?.Dispose();
            prepared?.Dispose();
            Finish(job, null, "cancelled", cancelledJob: true);
            return;
        }
        if (!ReferenceEquals(owner, job))
        {
            // The request that read the file was cancelled (its tile was re-realised); the identical waiting one takes the bytes and the budget.
            lock (_gate) { owner.Reserved = job.Reserved; job.Reserved = 0; }
            owner.Registration.Dispose();
            var from = job.Timing;
            var to = owner.Timing;
            to.CacheMs = from.CacheMs; to.WindowsCacheMs = from.WindowsCacheMs; to.OpenMs = from.OpenMs; to.ReadMs = from.ReadMs;
            to.IoLane = from.IoLane; to.IoInFlight = from.IoInFlight; to.IoStartedAt = from.IoStartedAt; to.IoEndedAt = from.IoEndedAt;
            Finish(job, null, "cancelled", cancelledJob: true);
        }
        var timing = owner.Timing;
        if (prepared.Ready is { } ready)
        {
            lock (_gate) ReleaseReservation(owner);
            timing.Source = prepared.Source;
            prepared.Dispose();
            Finish(owner, ready, prepared.Source switch { ThumbnailSource.DiskCache => "disk-cache", ThumbnailSource.Memory => "memory", _ => "windows-cache" });
        }
        else
        {
            owner.Prepared = prepared;
            lock (_gate)
            {
                timing.DecodeQueuedAt = Stopwatch.GetTimestamp();
                _decodePending.Add(owner);
            }
        }
    }

    private void IoTimedOut(Job job)
    {
        if (Interlocked.CompareExchange(ref job.IoState, 2, 0) != 0) return;
        List<Job>? followers;
        lock (_gate)
        {
            _ioRunning--;
            FreeLane(_ioLaneBusy, job.IoLane);
            _ioAbandoned++;
            ReleaseReservation(job);
            followers = EndReading(job);
        }
        Interlocked.Increment(ref _timedOut);
        Remember(job.Request.Key);
        Finish(job, null, "io-timeout", timedOut: true);
        if (followers is not null)
            foreach (var follower in followers)
            {
                if (follower.Token.IsCancellationRequested) { follower.Registration.Dispose(); Finish(follower, null, "cancelled", cancelledJob: true); }
                else { follower.Timing.Outcome = "io-timeout"; Finish(follower, null, "io-timeout", timedOut: true); }
            }
        Pump();
        EmitBurstIfIdle();
    }

    // ---- decode stage -----------------------------------------------------------------------------------------------------

    private async Task Execute(Job job, int lane)
    {
        Bitmap? result = null;
        var finished = false;
        var prepared = job.Prepared ?? new ThumbnailPrepared();
        var timing = job.Timing;
        var preparedOwnedByWork = false;
        try
        {
            job.Registration.Dispose();
            timing.DecodeLane = lane;
            timing.DecodeStartedAt = Stopwatch.GetTimestamp();
            if (job.Token.IsCancellationRequested) { Finish(job, null, "cancelled", cancelledJob: true); return; }
            var request = job.Request;
            var work = Task.Run(() =>
            {
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var bitmap = _pipeline.Decode(request, prepared, timing);
                    timing.DecodeMs = ThumbnailTiming.Ms(started, Stopwatch.GetTimestamp()) - timing.StoreMs;
                    timing.DecodeEndedAt = Stopwatch.GetTimestamp();
                    return bitmap;
                }
                catch { return null; } // an undecodable file is a glyph, never an error
            });
            using var timerCts = new CancellationTokenSource();
            var timer = Task.Delay(Timeout, timerCts.Token);
            var winner = await Task.WhenAny(work, timer).ConfigureAwait(false);
            if (winner == work)
            {
                timerCts.Cancel(); // no timer left running for a decode that finished
                result = await work.ConfigureAwait(false);
                finished = true;
                if (result is null) Remember(request.Key);
                if (job.Token.IsCancellationRequested) { result?.Dispose(); result = null; Finish(job, null, "cancelled", cancelledJob: true); }
                else Finish(job, result, result is null ? "failed" : "decoded");
                return;
            }
            // Timed out: give the lane back, count the decode as abandoned and discard its picture when it ends.
            Interlocked.Increment(ref _timedOut);
            Interlocked.Increment(ref _abandoned);
            Remember(request.Key);
            var timedOutKey = request.Key;
            preparedOwnedByWork = true;
            _ = work.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } late)
                {
                    // It was only slow: the decode stored its thumbnail in the disk cache, so the file is worth another request.
                    _failed.TryRemove(timedOutKey, out _);
                    late.Dispose();
                }
                prepared.Dispose();
                lock (_gate) ReleaseReservation(job);
                Interlocked.Decrement(ref _abandoned);
                Pump();
            }, TaskScheduler.Default);
            Finish(job, null, "timeout", timedOut: true);
        }
        catch (Exception ex)
        {
            if (!finished) Remember(job.Request.Key);
            timing.Outcome = "error";
            job.Completion.TrySetException(ex);
        }
        finally
        {
            if (!preparedOwnedByWork)
            {
                prepared.Dispose();
                lock (_gate) ReleaseReservation(job);
            }
            lock (_gate)
            {
                _running--;
                FreeLane(_decodeLaneBusy, lane);
            }
            Pump();
            EmitBurstIfIdle();
        }
    }

    // ---- completion -------------------------------------------------------------------------------------------------------

    private void Finish(Job job, Bitmap? result, string outcome, bool cancelledJob = false, bool timedOut = false)
    {
        var timing = job.Timing;
        timing.EndedAt = Stopwatch.GetTimestamp();
        timing.Outcome = outcome;
        if (outcome == "decoded") timing.Source = ThumbnailSource.Decoded;
        if (result is not null && !cancelledJob && outcome != "memory") MemoryCache?.Put(ThumbnailMemoryCache.KeyFor(job.Request), result);
        List<Job>? followers = null;
        lock (_gate)
        {
            if (_prefetchByKey.TryGetValue(job.DupKey, out var registered) && ReferenceEquals(registered, job)) _prefetchByKey.Remove(job.DupKey);
            if (job.Followers.Count > 0) { followers = new List<Job>(job.Followers); job.Followers.Clear(); }
        }
        if (followers is not null)
            foreach (var follower in followers)
            {
                follower.Registration.Dispose();
                var copy = result is null ? null : ThumbnailMemoryCache.Copy(result);
                if (follower.Token.IsCancellationRequested) { copy?.Dispose(); follower.Timing.Outcome = "cancelled"; follower.Completion.TrySetCanceled(follower.Token); continue; }
                if (cancelledJob)
                {
                    // The look-ahead was cancelled (a new folder): the request that joined it runs on its own.
                    follower.Leader = null;
                    lock (_gate) { if (_ioStage) _ioPending.Add(follower); else _decodePending.Add(follower); }
                    Pump();
                    continue;
                }
                follower.Timing.Source = timing.Source;
                Finish(follower, copy, outcome, timedOut: timedOut);
            }
        if (cancelledJob) job.Completion.TrySetCanceled(job.Token);
        else job.Completion.TrySetResult(result);
        if (cancelledJob) return;
        ThumbnailTrace.Tile(timing);
        if (!ThumbnailTrace.Enabled) return;
        lock (_gate)
        {
            _burstTiles++;
            if (timedOut) _burstTimedOut++;
            if (_burstFirstTile == 0) _burstFirstTile = timing.EndedAt;
        }
    }

    /// <summary>Logs the burst summary once the queue has drained (called after a lane is released, so "idle" is true for the last tile).</summary>
    private void EmitBurstIfIdle()
    {
        if (!ThumbnailTrace.Enabled) return;
        double total, first;
        int tiles, timedOutCount;
        lock (_gate)
        {
            if (_burstTiles == 0 || _ioPending.Count + _decodePending.Count + _running + _ioRunning + WaiterCount() != 0) return;
            tiles = _burstTiles;
            timedOutCount = _burstTimedOut;
            total = ThumbnailTiming.Ms(_burstStart, Stopwatch.GetTimestamp());
            first = ThumbnailTiming.Ms(_burstStart, _burstFirstTile);
            _burstTiles = 0;
            _burstTimedOut = 0;
            _burstFirstTile = 0;
        }
        ThumbnailTrace.Burst(tiles, total, first, PeakIoInFlight, PeakDecodeInFlight, _ioLanes, _lanes, timedOutCount);
    }

    private void Remember(string key)
    {
        if (_failed.Count >= MaxRememberedFailures) _failed.Clear();
        _failed[key] = 0;
    }

    private sealed class Job
    {
        public Job(ThumbnailRequest request, ThumbnailPriority priority, int costBucket, long sequence, CancellationToken token, ThumbnailTiming timing)
        {
            Request = request;
            Priority = priority;
            CostBucket = costBucket;
            Sequence = sequence;
            Token = token;
            Timing = timing;
        }

        public ThumbnailRequest Request { get; set; }
        public ThumbnailPriority Priority { get; set; }
        /// <summary>The look-ahead job this request joined (see _prefetchByKey), and the requests that joined this one.</summary>
        public Job? Leader { get; set; }
        public List<Job> Followers { get; } = new();
        public int CostBucket { get; }
        public long Sequence { get; }
        public CancellationToken Token { get; }
        public ThumbnailTiming Timing { get; }
        public ThumbnailIoPlan Plan { get; set; }
        /// <summary>Identifies the exact file version and tile width: two jobs with the same key would read and decode the same thing.</summary>
        public string DupKey => _dupKey ??= Request.Key + "|" + Request.Width;
        private string? _dupKey;
        public long Reserved { get; set; }
        public int IoLane { get; set; } = -1;
        /// <summary>0 reading (or not started), 1 finished normally, 2 given up on by the watchdog.</summary>
        public int IoState;
        public ThumbnailPrepared? Prepared { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
        public TaskCompletionSource<Bitmap?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
