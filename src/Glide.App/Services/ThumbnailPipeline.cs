using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using Glide.Diagnostics.Runtime;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>What the I/O stage of the thumbnail pipeline does with a file before a decode lane sees it (5.2.0).</summary>
internal enum ThumbnailIoPlan
{
    /// <summary>Nothing: the decoder opens the file itself (formats that read only part of it, or very large files).</summary>
    None,
    /// <summary>Read the whole file and hand the bytes to the decode lane (JPEG, PNG, GIF, ICO: decoded from memory, so the lane never opens the file).</summary>
    Bytes,
}

/// <summary>Where a finished thumbnail came from.</summary>
internal enum ThumbnailSource
{
    Decoded,
    DiskCache,
    WindowsCache,
    /// <summary>5.2.0: the in-memory thumbnail cache (<see cref="ThumbnailMemoryCache"/>), filled by the whole-folder preparation.</summary>
    Memory,
}

/// <summary>
/// The per-tile record of one thumbnail job: when it was queued, what each stage cost and which lane ran it. Filled by the
/// scheduler and the pipeline; written to the diagnostics log by <see cref="ThumbnailTrace"/> when that is switched on.
/// </summary>
internal sealed class ThumbnailTiming
{
    private static long s_nextId;

    public ThumbnailTiming(in ThumbnailRequest request, ThumbnailPriority priority)
    {
        Id = Interlocked.Increment(ref s_nextId);
        Path = request.Path;
        Width = request.Width;
        FileBytes = request.SizeBytes;
        Order = request.Order;
        Priority = priority;
        QueuedAt = Stopwatch.GetTimestamp();
    }

    public long Id { get; }
    public string Path { get; }
    public int Width { get; }
    public long FileBytes { get; }
    public long Order { get; }
    public ThumbnailPriority Priority { get; }
    public ThumbnailIoPlan Plan { get; set; }
    public ThumbnailSource Source { get; set; }
    /// <summary>decoded, disk-cache, windows-cache, failed, timeout, io-timeout or cancelled.</summary>
    public string Outcome { get; set; } = "";

    public long QueuedAt { get; }
    public long IoStartedAt { get; set; }
    public long IoEndedAt { get; set; }
    public long DecodeQueuedAt { get; set; }
    public long DecodeStartedAt { get; set; }
    public long DecodeEndedAt { get; set; }
    public long EndedAt { get; set; }

    /// <summary>Disk-cache stamp and lookup.</summary>
    public double CacheMs { get; set; }
    /// <summary>Windows thumbnail cache probe (cache-only).</summary>
    public double WindowsCacheMs { get; set; }
    /// <summary>Opening the file in the I/O stage (this is where on-access scanners and virtual disks add their delay).</summary>
    public double OpenMs { get; set; }
    /// <summary>Reading it.</summary>
    public double ReadMs { get; set; }
    /// <summary>The decode itself, on a decode lane.</summary>
    public double DecodeMs { get; set; }
    /// <summary>Copying the picture into the disk cache.</summary>
    public double StoreMs { get; set; }

    public int IoLane { get; set; } = -1;
    public int DecodeLane { get; set; } = -1;
    public int IoInFlight { get; set; }
    public int DecodeInFlight { get; set; }

    internal static double Ms(long from, long to) => from == 0 || to == 0 ? 0 : (to - from) * 1000.0 / Stopwatch.Frequency;
    public double IoWaitMs => Ms(QueuedAt, IoStartedAt);
    public double IoMs => Ms(IoStartedAt, IoEndedAt);
    public double DecodeWaitMs => Ms(DecodeQueuedAt, DecodeStartedAt);
    public double TotalMs => Ms(QueuedAt, EndedAt);
}

/// <summary>What the I/O stage produced for a request: a finished picture (cache hit), the file's bytes, or just a warmed file.</summary>
internal sealed class ThumbnailPrepared : IDisposable
{
    private byte[]? _buffer;
    private int _length;

    /// <summary>A thumbnail that needs no decode (disk cache or Windows thumbnail cache). The consumer owns it.</summary>
    public Bitmap? Ready { get; set; }
    public ThumbnailSource Source { get; set; }
    /// <summary>The disk-cache identity taken before the read, so the decoded picture is stored under the version that was read.</summary>
    public ThumbnailDiskCache.Stamp Stamp { get; set; }
    public byte[]? Buffer => _buffer;
    public int Length => _length;
    public bool HasBytes => _buffer is not null;

    public void SetBytes(byte[] pooled, int length)
    {
        _buffer = pooled;
        _length = length;
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        _length = 0;
        // Ready is handed on, never disposed here (the scheduler disposes a picture nobody wanted).
    }
}

/// <summary>The two halves of one Explorer thumbnail (5.2.0): an I/O stage (any number of lanes, tolerant of slow opens) and a decode stage (CPU lanes).</summary>
internal interface IThumbnailPipeline
{
    /// <summary>False for a plain decode function: the scheduler then runs only the decode stage.</summary>
    bool HasIoStage { get; }

    /// <summary>What the I/O stage will do for this request. Pure (no file access): the scheduler reserves memory from it.</summary>
    ThumbnailIoPlan PlanIo(in ThumbnailRequest request);

    /// <summary>The I/O stage: caches first (no source read), otherwise open and read the file as planned.</summary>
    ThumbnailPrepared Prepare(in ThumbnailRequest request, ThumbnailIoPlan plan, ThumbnailTiming timing);

    /// <summary>The decode stage. Null = the file cannot be decoded (the tile keeps its glyph).</summary>
    Bitmap? Decode(in ThumbnailRequest request, ThumbnailPrepared prepared, ThumbnailTiming timing);
}

/// <summary>A plain <c>(path, width) -> bitmap</c> decode function as a pipeline with no I/O stage (the 5.2.0 behaviour; tests).</summary>
internal sealed class DecodeOnlyThumbnailPipeline : IThumbnailPipeline
{
    private readonly Func<string, int, Bitmap?> _decode;

    public DecodeOnlyThumbnailPipeline(Func<string, int, Bitmap?> decode) => _decode = decode;

    public bool HasIoStage => false;
    public ThumbnailIoPlan PlanIo(in ThumbnailRequest request) => ThumbnailIoPlan.None;
    public ThumbnailPrepared Prepare(in ThumbnailRequest request, ThumbnailIoPlan plan, ThumbnailTiming timing) => new();
    public Bitmap? Decode(in ThumbnailRequest request, ThumbnailPrepared prepared, ThumbnailTiming timing) => _decode(request.Path, Math.Max(16, request.Width));
}

/// <summary>
/// The production pipeline (5.2.0). I/O stage, in this order: the disk cache (a hit never reads the picture), then Windows' own
/// thumbnail cache (<c>IShellItemImageFactory</c> with <c>SIIGBF_INCACHEONLY</c>: a tile File Explorer already made appears without
/// reading the file), then the file itself, read whole by an I/O lane so its first open (on-access antivirus, a virtual or network
/// disk) overlaps the decoding of other tiles. The bytes go to the decode lane (JPEG through the native in-memory export, PNG / GIF /
/// ICO through Skia on a memory stream), so each file is opened exactly once: a scanner that rescans on every open is not paid twice.
/// Formats that read part of a file (RAW previews, pyramid TIFFs, PSD, ...) or have their own path-based decoder (WebP, TIFF, BMP, WIC,
/// codec plug-ins) and very large files are left to the decode lane.
/// </summary>
internal sealed class ThumbnailPipeline : IThumbnailPipeline
{
    /// <summary>A JPEG larger than this is decoded from its path (the bytes would hold too much memory while they wait for a lane).</summary>
    public const long MaxBytesFileLength = 48L * 1024 * 1024;
    /// <summary>The same limit for PNG / GIF / ICO (their decode needs the whole file and a full frame besides).</summary>
    public const long MaxSkiaBytesFileLength = 32L * 1024 * 1024;

    /// <summary>
    /// Set to 0 to skip the Windows thumbnail cache probe (<c>GLIDE_THUMB_WINCACHE=0</c>).
    /// </summary>
    public static readonly bool WindowsCacheDefault = !string.Equals(Environment.GetEnvironmentVariable("GLIDE_THUMB_WINCACHE"), "0", StringComparison.Ordinal);

    public bool UseWindowsCache { get; init; } = WindowsCacheDefault;

    /// <summary>
    /// Called with the path just before each file open of the I/O stage. Tests and the benchmark delay here to model a slow
    /// first open (an on-access scanner, a virtual disk); it is not set in production.
    /// </summary>
    public Action<string>? BeforeOpen { get; init; }

    /// <summary>Test seam for the Windows thumbnail cache probe (the shell itself is used when null).</summary>
    public Func<string, int, Bitmap?>? WindowsCacheProbe { get; init; }

    public bool HasIoStage => true;

    public ThumbnailIoPlan PlanIo(in ThumbnailRequest request)
    {
        var route = ThumbnailRoutes.RouteFor(request.Path);
        var size = request.SizeBytes;
        if (route == ThumbnailRoute.None || size <= 0) return ThumbnailIoPlan.None;
        if (route == ThumbnailRoute.Jpeg && ThumbnailRoutes.IsNativeJpegSuffix(request.Path))
            return size <= MaxBytesFileLength ? ThumbnailIoPlan.Bytes : ThumbnailIoPlan.None;
        if (route == ThumbnailRoute.Skia && size <= MaxSkiaBytesFileLength) return ThumbnailIoPlan.Bytes;
        return ThumbnailIoPlan.None;
    }

    public ThumbnailPrepared Prepare(in ThumbnailRequest request, ThumbnailIoPlan plan, ThumbnailTiming timing)
    {
        var prepared = new ThumbnailPrepared();
        var width = Math.Max(16, request.Width);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var hit = ThumbnailDecode.TryDiskCache(request.Path, width, out var stamp);
            timing.CacheMs = ThumbnailTiming.Ms(started, Stopwatch.GetTimestamp());
            prepared.Stamp = stamp;
            if (hit is not null)
            {
                prepared.Ready = hit;
                prepared.Source = ThumbnailSource.DiskCache;
                return prepared;
            }
            // 5.2.0: not for a JPEG read into memory: the shell probe costs 35 - 60 ms per file in a real folder, Glide's own decode of
            // the bytes 10 - 20 ms (a 45 MP progressive file through the DC-only reader), so the probe only delayed those tiles.
            if (UseWindowsCache && ThumbnailRoutes.CanThumbnail(request.Path) && !(plan == ThumbnailIoPlan.Bytes && ThumbnailRoutes.RouteFor(request.Path) == ThumbnailRoute.Jpeg))
            {
                started = Stopwatch.GetTimestamp();
                Bitmap? shell = null;
                try { shell = WindowsCacheProbe is { } probe ? probe(request.Path, width) : ProbeWindowsCache(request.Path, width); }
                catch { /* a shell that cannot answer is a miss */ }
                timing.WindowsCacheMs = ThumbnailTiming.Ms(started, Stopwatch.GetTimestamp());
                if (shell is not null)
                {
                    prepared.Ready = shell;
                    prepared.Source = ThumbnailSource.WindowsCache;
                    return prepared;
                }
            }
            if (plan == ThumbnailIoPlan.Bytes) ReadWhole(request.Path, request.SizeBytes, prepared, timing);
        }
        catch
        {
            // Any I/O problem (a locked or vanished file): the decode lane opens the path itself and reports the real failure.
            prepared.Dispose();
            prepared = new ThumbnailPrepared();
        }
        return prepared;
    }

    /// <summary>Windows' thumbnail cache only (never extracts): the cached picture scaled into a square box of the tile width.</summary>
    internal static Bitmap? ProbeWindowsCache(string path, int width)
        => NativeImageDecoder.TryDecodeShellCachedBounded(path.Replace('/', '\\'), width, width, out var bitmap) ? bitmap : null; // the shell wants backslashes

    private void ReadWhole(string path, long expectedLength, ThumbnailPrepared prepared, ThumbnailTiming timing)
    {
        var started = Stopwatch.GetTimestamp();
        BeforeOpen?.Invoke(path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        var opened = Stopwatch.GetTimestamp();
        timing.OpenMs = ThumbnailTiming.Ms(started, opened);
        var length = RandomAccess.GetLength(handle);
        if (length <= 0 || length > MaxBytesFileLength || length > Math.Max(expectedLength, 0) * 2 + 1024 * 1024) return; // not the file the listing described
        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                var got = RandomAccess.Read(handle, buffer.AsSpan((int)offset, (int)(length - offset)), offset);
                if (got <= 0) break;
                offset += got;
            }
            timing.ReadMs = ThumbnailTiming.Ms(opened, Stopwatch.GetTimestamp());
            if (offset == length)
            {
                prepared.SetBytes(buffer, (int)length);
                buffer = null;
            }
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Bitmap? Decode(in ThumbnailRequest request, ThumbnailPrepared prepared, ThumbnailTiming timing)
    {
        var bitmap = ThumbnailDecode.DecodePrepared(request.Path, Math.Max(16, request.Width), prepared.Buffer, prepared.Length, prepared.Stamp, out var storeMs);
        timing.StoreMs = storeMs;
        return bitmap;
    }
}

/// <summary>
/// Per-tile thumbnail timing in the diagnostics log. Off unless the diagnostic trace is on (<c>--diagnostic-trace</c> or
/// <c>GLIDE_DIAGNOSTIC_TRACE=1</c>, which writes <c>Glide-Diagnostic-Latest.txt</c> in the Downloads folder): then every tile logs one
/// <c>thumbnail.tile</c> line (queue wait, cache probes, open, read, decode wait, decode, store, lanes, how many files were in
/// flight), one <c>thumbnail.applied</c> line when the UI shows it, and one <c>thumbnail.burst</c> summary when the queue drains.
/// File names are written as a short hash, never as text, so the log can be shared.
/// </summary>
internal static class ThumbnailTrace
{
    /// <summary>Test seam: every would-be log line (subsystem-qualified name, payload) goes here and the live trace is not used.</summary>
    internal static Action<string, string>? SinkForTests { get; set; }

    public static bool Enabled => SinkForTests is not null || LiveDiagnosticTrace.IsEnabled;

    public static void Tile(ThumbnailTiming t)
    {
        if (!Enabled) return;
        var sb = new StringBuilder(280);
        sb.Append("id=").Append(t.Id).Append(" file=").Append(NameHash(t.Path)).Append(" ext=").Append(System.IO.Path.GetExtension(t.Path).ToLowerInvariant())
          .Append(" kb=").Append(t.FileBytes / 1024).Append(" prio=").Append(t.Priority).Append(" order=").Append(t.Order)
          .Append(" plan=").Append(t.Plan).Append(" outcome=").Append(t.Outcome).Append(" source=").Append(t.Source);
        Field(sb, "io_wait_ms", t.IoWaitMs); Field(sb, "cache_ms", t.CacheMs); Field(sb, "win_cache_ms", t.WindowsCacheMs);
        Field(sb, "open_ms", t.OpenMs); Field(sb, "read_ms", t.ReadMs); Field(sb, "io_ms", t.IoMs);
        Field(sb, "decode_wait_ms", t.DecodeWaitMs); Field(sb, "decode_ms", t.DecodeMs); Field(sb, "store_ms", t.StoreMs); Field(sb, "total_ms", t.TotalMs);
        sb.Append(" io_lane=").Append(t.IoLane).Append(" decode_lane=").Append(t.DecodeLane)
          .Append(" io_in_flight=").Append(t.IoInFlight).Append(" decode_in_flight=").Append(t.DecodeInFlight);
        Write("thumbnail.tile", sb.ToString());
    }

    /// <summary>The UI thread showed the picture: <paramref name="uiMs"/> is the time from the picture being ready to it being set on the tile.</summary>
    public static void Applied(ThumbnailTiming t, double uiMs)
    {
        if (!Enabled) return;
        Write("thumbnail.applied", $"id={t.Id} ui_apply_ms={uiMs.ToString("0.0", CultureInfo.InvariantCulture)}");
    }

    public static void Burst(int tiles, double totalMs, double firstTileMs, int peakIo, int peakDecode, int ioLanes, int decodeLanes, int timedOut)
    {
        if (!Enabled) return;
        Write("thumbnail.burst", string.Create(CultureInfo.InvariantCulture,
            $"tiles={tiles} total_ms={totalMs:0.0} first_tile_ms={firstTileMs:0.0} peak_io={peakIo} peak_decode={peakDecode} io_lanes={ioLanes} decode_lanes={decodeLanes} timed_out={timedOut}"));
    }

    private static void Field(StringBuilder sb, string name, double value) => sb.Append(' ').Append(name).Append('=').Append(value.ToString("0.0", CultureInfo.InvariantCulture));

    /// <summary>Stable 6-hex-digit tag of the file name (case-insensitive): enough to match lines, nothing to read.</summary>
    internal static string NameHash(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        uint hash = 2166136261;
        foreach (var c in name) hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
        return (hash & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);
    }

    private static void Write(string name, string payload)
    {
        if (SinkForTests is { } sink) { sink(name, payload); return; }
        var dot = name.IndexOf('.');
        LiveDiagnosticTrace.Write(name[..dot], name[(dot + 1)..], payload);
    }
}
