using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.App.Services;
using Glide.Imaging;
using Glide.Tests.Support;
using SkiaSharp;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 Explorer thumbnail pipeline (a slow first open of a file on a virtual disk or behind an on-access scanner made the tab
/// load one tile at a time): the I/O stage reads many files at once on its own lanes while the decode lanes work on files already
/// read; a slow read never counts against the decode timeout; reads start in on-screen reading order; the disk cache and Windows'
/// own thumbnail cache are asked before the file is read; the first scan of a large cache does not hold up the writers.
/// Latency is injected with <see cref="ThumbnailPipeline.BeforeOpen"/> on real JPEG files, so the real reader and decoder run.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailPipelineTests : IDisposable
{
    private readonly TempDir _dir = new("glide-thumb-pipeline");

    public ThumbnailPipelineTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        ThumbnailDiskCache.Configure(false, null, 0);
    }

    public void Dispose()
    {
        ThumbnailDiskCache.Configure(false, null, 0);
        ThumbnailDecode.DecodingForTests = null;
        ThumbnailTrace.SinkForTests = null;
        _dir.Dispose();
    }

    private string[] MakeJpegs(int count, string folder = "pics", int width = 960, int height = 1440)
    {
        var bytes = MakeJpegBytes(width, height);
        var root = _dir.Combine(folder);
        Directory.CreateDirectory(root);
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            paths[i] = Path.Combine(root, $"IMG-{i + 1:0000}.jpg");
            File.WriteAllBytes(paths[i], bytes);
        }
        return paths;
    }

    internal static byte[] MakeJpegBytes(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.DarkSlateBlue, SKColors.Goldenrod], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, paint);
            using var dot = new SKPaint { Color = SKColors.Crimson, IsAntialias = true };
            canvas.DrawCircle(width / 3f, height / 3f, width / 5f, dot);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static ThumbnailRequest RequestFor(string path, long order = 0, int width = 236)
    {
        var info = new FileInfo(path);
        return new ThumbnailRequest(path, info.Length, info.LastWriteTimeUtc.Ticks, width, order);
    }

    // ---- concurrency under injected latency -------------------------------------------------------------------------------------

    [Fact]
    public async Task Slow_opens_overlap_so_a_folder_takes_about_one_latency_per_lane_batch_not_one_latency_per_file()
    {
        const int Files = 48;
        const int LatencyMs = 250;
        var paths = MakeJpegs(Files);
        var pipeline = new ThumbnailPipeline { BeforeOpen = _ => Thread.Sleep(LatencyMs), UseWindowsCache = false };
        var scheduler = new ThumbnailScheduler(8, pipeline, ioLanes: 16, timeout: TimeSpan.FromSeconds(30));

        var clock = Stopwatch.StartNew();
        var jobs = paths.Select((p, i) => scheduler.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, CancellationToken.None)).ToArray();
        var bitmaps = await Task.WhenAll(jobs);
        var elapsed = clock.Elapsed;
        foreach (var bitmap in bitmaps) Assert.NotNull(bitmap);
        foreach (var bitmap in bitmaps) bitmap!.Dispose();

        // Serial opens would take Files * LatencyMs = 12 s; 16 lanes need 3 rounds (0.75 s) plus the decodes.
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"48 files with a {LatencyMs} ms open took {elapsed.TotalMilliseconds:F0} ms; the opens are being serialised");
        Assert.InRange(scheduler.PeakIoInFlight, 12, 16);
        Assert.InRange(scheduler.PeakDecodeInFlight, 1, 8);
        Assert.Equal(0, scheduler.TimedOutCount);
    }

    [Fact]
    public async Task A_slow_open_never_counts_against_the_decode_timeout_and_never_takes_a_decode_lane()
    {
        // 0.45 s per open against a 0.3 s decode timeout: before the I/O stage the open was inside the timeout, so every tile timed out
        // (the tile keeps its glyph, the lane is lost until the decode ends).
        var paths = MakeJpegs(12);
        var slowOpen = new Action<string>(_ => Thread.Sleep(450));

        var legacy = new ThumbnailScheduler(4, (path, width) => { slowOpen(path); return ThumbnailDecode.DecodeToWidth(path, width); }, TimeSpan.FromMilliseconds(300));
        var legacyResults = await Task.WhenAll(paths.Select((p, i) => legacy.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, CancellationToken.None)));
        Assert.All(legacyResults, r => Assert.Null(r));
        Assert.Equal(paths.Length, legacy.TimedOutCount);

        var scheduler = new ThumbnailScheduler(4, new ThumbnailPipeline { BeforeOpen = slowOpen, UseWindowsCache = false }, ioLanes: 12,
            timeout: TimeSpan.FromMilliseconds(300), ioTimeout: TimeSpan.FromSeconds(30));
        var results = await Task.WhenAll(paths.Select((p, i) => scheduler.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, CancellationToken.None)));
        Assert.All(results, r => Assert.NotNull(r));
        foreach (var bitmap in results) bitmap!.Dispose();
        Assert.Equal(0, scheduler.TimedOutCount);
        Assert.Equal(0, scheduler.Abandoned);
        Assert.Equal(4, scheduler.EffectiveLanes);
    }

    [Fact]
    public async Task A_read_that_never_ends_is_given_up_on_its_own_lane_pool_and_the_rest_continue()
    {
        var paths = MakeJpegs(6);
        using var hang = new ManualResetEventSlim(false);
        var pipeline = new ThumbnailPipeline
        {
            UseWindowsCache = false,
            BeforeOpen = path => { if (path.EndsWith("IMG-0001.jpg", StringComparison.Ordinal)) hang.Wait(TimeSpan.FromSeconds(30)); },
        };
        var scheduler = new ThumbnailScheduler(2, pipeline, ioLanes: 2, timeout: TimeSpan.FromSeconds(10), ioTimeout: TimeSpan.FromMilliseconds(300));
        var stuck = scheduler.RunAsync(RequestFor(paths[0], 0), ThumbnailPriority.Visible, CancellationToken.None, out var stuckTiming);
        var others = paths.Skip(1).Select((p, i) => scheduler.RunAsync(RequestFor(p, i + 1), ThumbnailPriority.Visible, CancellationToken.None)).ToArray();

        Assert.Null(await stuck.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("io-timeout", stuckTiming.Outcome);
        Assert.Equal(1, scheduler.IoAbandoned);
        var rest = await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(rest, r => Assert.NotNull(r));
        foreach (var bitmap in rest) bitmap!.Dispose();

        hang.Set();
        await WaitUntil(() => scheduler.IoAbandoned == 0);
        Assert.Equal(2, scheduler.EffectiveIoLanes);
    }

    [Fact]
    public async Task The_read_ahead_is_bounded_so_the_io_stage_cannot_run_away_from_the_decoders()
    {
        var paths = MakeJpegs(24);
        var fileBytes = new FileInfo(paths[0]).Length;
        var budget = fileBytes * 3;
        ThumbnailScheduler? scheduler = null;
        long peak = 0;
        // The decode is the slow stage here; every open samples how many bytes are waiting for a lane.
        var pipeline = new ThumbnailPipeline
        {
            UseWindowsCache = false,
            BeforeOpen = _ => { var held = scheduler!.BufferedBytes; long seen; while (held > (seen = Interlocked.Read(ref peak)) && Interlocked.CompareExchange(ref peak, held, seen) != seen) { } },
        };
        scheduler = new ThumbnailScheduler(1, new SlowDecode(pipeline, TimeSpan.FromMilliseconds(30)), ioLanes: 8, timeout: TimeSpan.FromSeconds(30), maxBufferedBytes: budget);
        var results = await Task.WhenAll(paths.Select((p, i) => scheduler.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, CancellationToken.None)));
        foreach (var bitmap in results) { Assert.NotNull(bitmap); bitmap!.Dispose(); }
        Assert.True(Interlocked.Read(ref peak) <= budget, $"{peak} bytes were buffered against a budget of {budget}");
        Assert.True(Interlocked.Read(ref peak) > 0);
        await WaitUntil(() => scheduler!.BufferedBytes == 0);
    }

    // ---- on-screen order ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Files_are_opened_in_on_screen_reading_order_visible_first_then_nearest_then_look_ahead_whatever_their_names_or_formats()
    {
        var paths = MakeJpegs(12);
        var opened = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var pipeline = new ThumbnailPipeline
        {
            UseWindowsCache = false,
            BeforeOpen = path =>
            {
                lock (opened) opened.Add(Path.GetFileName(path));
                if (path.EndsWith("IMG-0012.jpg", StringComparison.Ordinal)) release.Wait(TimeSpan.FromSeconds(20)); // the first job holds the only I/O lane
            },
        };
        var scheduler = new ThumbnailScheduler(2, pipeline, ioLanes: 1, timeout: TimeSpan.FromSeconds(30));
        var holder = scheduler.RunAsync(RequestFor(paths[11], 0), ThumbnailPriority.Visible, CancellationToken.None);
        await WaitUntil(() => scheduler.IoRunningCount == 1 && opened.Count == 1);

        // Asked for in a scrambled order (a layout pass does not realise tiles in name order); names say nothing about position.
        var jobs = new[]
        {
            scheduler.RunAsync(RequestFor(paths[7], 1_000_003), ThumbnailPriority.Near, CancellationToken.None),   // buffer row, far below
            scheduler.RunAsync(RequestFor(paths[0], 2), ThumbnailPriority.Visible, CancellationToken.None),         // second visible tile
            scheduler.RunAsync(RequestFor(paths[9], 50), ThumbnailPriority.Prefetch, CancellationToken.None),       // look-ahead
            scheduler.RunAsync(RequestFor(paths[5], 0), ThumbnailPriority.Visible, CancellationToken.None),         // first visible tile
            scheduler.RunAsync(RequestFor(paths[3], 500_001), ThumbnailPriority.Near, CancellationToken.None),      // buffer row, just below
            scheduler.RunAsync(RequestFor(paths[1], 11), ThumbnailPriority.Visible, CancellationToken.None),        // last visible tile
        };
        release.Set();
        foreach (var bitmap in await Task.WhenAll(jobs.Append(holder))) bitmap?.Dispose();

        Assert.Equal(new[] { "IMG-0012.jpg", "IMG-0006.jpg", "IMG-0001.jpg", "IMG-0002.jpg", "IMG-0004.jpg", "IMG-0008.jpg", "IMG-0010.jpg" }, opened);
    }

    [Fact]
    public async Task Decode_lanes_take_equal_cost_files_in_reading_order_not_in_the_order_they_were_asked_for()
    {
        var started = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var scheduler = new ThumbnailScheduler(1, (path, _) =>
        {
            lock (started) started.Add(Path.GetFileName(path));
            if (Path.GetFileName(path) == "00-blocker.jpg") release.Wait(TimeSpan.FromSeconds(20));
            return null;
        });
        var blocker = scheduler.RunAsync(new ThumbnailRequest(@"C:\p\00-blocker.jpg", 2_000_000, 1, 236), ThumbnailPriority.Visible, CancellationToken.None);
        await WaitUntil(() => scheduler.RunningCount == 1);
        var jobs = new[] { 3, 1, 2 }.Select(rank => scheduler.RunAsync(new ThumbnailRequest($@"C:\p\{rank}-tile.jpg", 2_000_000, 1, 236, rank), ThumbnailPriority.Visible, CancellationToken.None)).ToArray();
        release.Set();
        await Task.WhenAll(jobs.Append(blocker));
        Assert.Equal(new[] { "00-blocker.jpg", "1-tile.jpg", "2-tile.jpg", "3-tile.jpg" }, started);
    }

    [Fact]
    public async Task A_job_cancelled_while_waiting_for_an_io_lane_is_never_opened()
    {
        var paths = MakeJpegs(3);
        var opened = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var pipeline = new ThumbnailPipeline
        {
            UseWindowsCache = false,
            BeforeOpen = path => { lock (opened) opened.Add(Path.GetFileName(path)); if (path.EndsWith("IMG-0001.jpg", StringComparison.Ordinal)) release.Wait(TimeSpan.FromSeconds(20)); },
        };
        var scheduler = new ThumbnailScheduler(1, pipeline, ioLanes: 1, timeout: TimeSpan.FromSeconds(30));
        var first = scheduler.RunAsync(RequestFor(paths[0], 0), ThumbnailPriority.Visible, CancellationToken.None);
        await WaitUntil(() => opened.Count == 1);
        using var cts = new CancellationTokenSource();
        var scrolledAway = scheduler.RunAsync(RequestFor(paths[1], 1), ThumbnailPriority.Visible, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scrolledAway);
        release.Set();
        (await first)?.Dispose();
        Assert.Equal(new[] { "IMG-0001.jpg" }, opened);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task A_tile_realised_again_while_its_file_is_being_read_waits_for_that_read_instead_of_opening_the_file_twice()
    {
        // A layout change or a refresh realises the same tiles again: the first request is cancelled, but a read that has started cannot be
        // stopped. The second request must adopt its bytes (on a slow open, a second read is another whole latency).
        var paths = MakeJpegs(4);
        var opens = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var pipeline = new ThumbnailPipeline
        {
            UseWindowsCache = false,
            BeforeOpen = path => { lock (opens) opens.Add(Path.GetFileName(path)); release.Wait(TimeSpan.FromSeconds(20)); },
        };
        var scheduler = new ThumbnailScheduler(2, pipeline, ioLanes: 4, timeout: TimeSpan.FromSeconds(30));
        using var firstWave = new CancellationTokenSource();
        var first = paths.Select((p, i) => scheduler.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, firstWave.Token)).ToArray();
        await WaitUntil(() => { lock (opens) return opens.Count == 4; });

        firstWave.Cancel(); // the tiles were thrown away ...
        var second = paths.Select((p, i) => scheduler.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, CancellationToken.None, out _)).ToArray(); // ... and realised again
        await Task.Delay(100);
        Assert.Equal(4, scheduler.PendingCount); // waiting for the reads that are under way, not queued for new ones
        release.Set();

        var results = await Task.WhenAll(second).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(results, r => Assert.NotNull(r));
        foreach (var bitmap in results) bitmap!.Dispose();
        foreach (var job in first) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Equal(4, opens.Count); // each file was opened once
        await WaitUntil(() => scheduler.BufferedBytes == 0 && scheduler.PendingCount == 0);
    }

    // ---- cache fast paths ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_disk_cache_hit_is_served_by_the_io_stage_without_opening_the_picture()
    {
        ThumbnailDiskCache.Configure(true, _dir.Combine("cache"), 64L * 1024 * 1024);
        var path = MakeJpegs(1)[0];
        var opens = 0;
        var decodes = 0;
        ThumbnailDecode.DecodingForTests = _ => Interlocked.Increment(ref decodes);
        var pipeline = new ThumbnailPipeline { BeforeOpen = _ => Interlocked.Increment(ref opens), UseWindowsCache = false };
        var scheduler = new ThumbnailScheduler(2, pipeline, ioLanes: 4);

        (await scheduler.RunAsync(RequestFor(path), ThumbnailPriority.Visible, CancellationToken.None))?.Dispose();
        Assert.True(ThumbnailDiskCache.Current!.WaitForWrites(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, opens);
        Assert.Equal(1, decodes);

        using var hit = await scheduler.RunAsync(RequestFor(path), ThumbnailPriority.Visible, CancellationToken.None, out var timing);
        Assert.NotNull(hit);
        Assert.Equal("disk-cache", timing.Outcome);
        Assert.Equal(ThumbnailSource.DiskCache, timing.Source);
        Assert.Equal(1, opens);   // the picture was not opened again
        Assert.Equal(1, decodes); // and not decoded
    }

    [Fact]
    public async Task A_thumbnail_windows_already_cached_appears_without_reading_or_decoding_the_file_and_a_miss_falls_back_to_our_decode()
    {
        // PNG: a JPEG read into memory skips the probe (5.2.0, see the next test).
        var paths = MakeJpegs(2).Select(p =>
        {
            var png = Path.ChangeExtension(p, ".png");
            using (var bitmap = SKBitmap.Decode(p)) using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(png, data.ToArray());
            File.Delete(p);
            return png;
        }).ToArray();
        var opens = 0;
        var decodes = 0;
        ThumbnailDecode.DecodingForTests = _ => Interlocked.Increment(ref decodes);
        var probes = new List<string>();
        var pipeline = new ThumbnailPipeline
        {
            BeforeOpen = _ => Interlocked.Increment(ref opens),
            UseWindowsCache = true,
            WindowsCacheProbe = (path, width) =>
            {
                lock (probes) probes.Add(Path.GetFileName(path));
                return path.EndsWith("IMG-0001.png", StringComparison.Ordinal) ? new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96)) : null;
            },
        };
        var scheduler = new ThumbnailScheduler(2, pipeline, ioLanes: 4);

        using var cached = await scheduler.RunAsync(RequestFor(paths[0], 0), ThumbnailPriority.Visible, CancellationToken.None, out var hitTiming);
        Assert.NotNull(cached);
        Assert.Equal("windows-cache", hitTiming.Outcome);
        Assert.Equal(ThumbnailSource.WindowsCache, hitTiming.Source);
        Assert.Equal(0, opens);
        Assert.Equal(0, decodes);

        using var decoded = await scheduler.RunAsync(RequestFor(paths[1], 1), ThumbnailPriority.Visible, CancellationToken.None, out var missTiming);
        Assert.NotNull(decoded);
        Assert.Equal("decoded", missTiming.Outcome);
        Assert.Equal(1, opens);
        Assert.Equal(1, decodes);
        Assert.Equal(new[] { "IMG-0001.png", "IMG-0002.png" }, probes);
    }

    [Fact]
    public async Task A_jpeg_read_into_memory_is_decoded_by_glide_without_asking_the_windows_cache()
    {
        // 5.2.0: the shell probe cost 35 - 60 ms per file in a real 45 MP folder, Glide's own decode of the bytes 10 - 20 ms.
        var paths = MakeJpegs(1, "nowin");
        var probes = 0;
        var pipeline = new ThumbnailPipeline
        {
            UseWindowsCache = true,
            WindowsCacheProbe = (_, width) => { Interlocked.Increment(ref probes); return new WriteableBitmap(new PixelSize(width, width), new Vector(96, 96)); },
        };
        var scheduler = new ThumbnailScheduler(2, pipeline, ioLanes: 4);
        using var tile = await scheduler.RunAsync(RequestFor(paths[0], 0), ThumbnailPriority.Visible, CancellationToken.None, out var timing);
        Assert.NotNull(tile);
        Assert.Equal("decoded", timing.Outcome);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void The_real_windows_thumbnail_cache_answers_from_cache_only_after_it_was_warmed_and_a_cold_file_is_a_cheap_miss()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = MakeJpegs(1, "win")[0];
        // Not in Windows' cache yet: the cache-only probe must say so without extracting anything.
        Assert.Null(ThumbnailPipeline.ProbeWindowsCache(path, 236));
        // Ask the shell for the thumbnail once (this makes Windows store it); if this PC has no thumbnail handler for JPEG, there is nothing to test.
        if (!NativeImageDecoder.TryDecodeShellPreview(path, 236, out var warmed)) return;
        warmed.Dispose();
        var sw = Stopwatch.StartNew();
        using var hit = ThumbnailPipeline.ProbeWindowsCache(path, 236);
        var ms = sw.Elapsed.TotalMilliseconds;
        // Windows may write its cache entry a moment later; a hit is the point of the test, but a PC that does not cache it is not a failure.
        if (hit is null) return;
        Assert.InRange(hit.PixelSize.Width, 1, 236);
        Assert.InRange(hit.PixelSize.Height, 1, 236);
        Assert.True(ms < 250, $"a cached thumbnail took {ms:F0} ms to fetch");
    }

    [Fact]
    public void A_jpeg_decoded_from_memory_matches_the_decode_from_its_path()
    {
        var path = MakeJpegs(1, "mem", 1200, 1800)[0];
        var bytes = File.ReadAllBytes(path);
        Assert.True(NativeImageDecoder.TryDecodeJpegThumbnail(path, 236, out var fromPath));
        if (!NativeImageDecoder.TryDecodeJpegThumbnailFromMemory(bytes, 236, out var fromMemory))
        {
            fromPath!.Dispose();
            Assert.Fail("Glide.Native.dll has no GlideDecodeJpegThumbnailMemory export (rebuild native/Glide.Native)");
            return;
        }
        using (fromPath)
        using (fromMemory)
        {
            Assert.Equal(fromPath!.PixelSize, fromMemory!.PixelSize);
            Assert.Equal(PixelsOf(fromPath), PixelsOf(fromMemory));
        }
        Assert.False(NativeImageDecoder.TryDecodeJpegThumbnailFromMemory(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 236, out var junk));
        Assert.Null(junk);
    }

    private static byte[] PixelsOf(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    [Fact]
    public async Task The_first_scan_of_a_large_thumbnail_cache_does_not_hold_up_the_writers()
    {
        // The scan used to run under a lock on the first writer's thread: every other write of that folder queued behind it, each on its
        // own thread-pool thread, and the decode lanes starved of threads.
        var cache = new ThumbnailDiskCache(_dir.Combine("slow-cache"), 64L * 1024 * 1024) { ScanDelayForTests = TimeSpan.FromSeconds(3) };
        var pixels = new byte[32 * 32 * 4];
        var stamps = new List<ThumbnailDiskCache.Stamp>();
        var paths = MakeJpegs(40, "src", 64, 64);
        var clock = Stopwatch.StartNew();
        foreach (var path in paths)
        {
            var stamp = cache.StampFor(path, 32);
            stamps.Add(stamp);
            cache.Put(stamp, 32, 32, pixels, 32 * 4);
        }
        await WaitUntil(() => stamps.All(s => File.Exists(cache.FilePathForTests(s))));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"40 writes took {clock.Elapsed.TotalMilliseconds:F0} ms while the scan slept for 3 s");
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(15)));
        Assert.All(stamps, s => Assert.NotNull(cache.TryGet(s)));
        var (files, bytes) = cache.Measure();
        Assert.Equal(40, files);
        Assert.True(bytes > 0);
    }

    // ---- diagnostics trace ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_trace_logs_one_line_per_tile_with_the_stage_timings_and_lanes_and_a_burst_summary_without_file_names()
    {
        var lines = new List<(string Name, string Payload)>();
        ThumbnailTrace.SinkForTests = (name, payload) => { lock (lines) lines.Add((name, payload)); };
        var paths = MakeJpegs(10);
        var scheduler = new ThumbnailScheduler(3, new ThumbnailPipeline { BeforeOpen = _ => Thread.Sleep(20), UseWindowsCache = false }, ioLanes: 4);
        var results = await Task.WhenAll(paths.Select((p, i) => scheduler.RunAsync(RequestFor(p, i), ThumbnailPriority.Visible, CancellationToken.None)));
        foreach (var bitmap in results) bitmap?.Dispose();
        await WaitUntil(() => { lock (lines) return lines.Any(l => l.Name == "thumbnail.burst"); });

        string[] tiles;
        lock (lines) tiles = lines.Where(l => l.Name == "thumbnail.tile").Select(l => l.Payload).ToArray();
        Assert.Equal(10, tiles.Length);
        foreach (var field in new[] { "id=", "file=", "ext=.jpg", "kb=", "prio=Visible", "order=", "plan=Bytes", "outcome=decoded", "io_wait_ms=", "cache_ms=", "win_cache_ms=", "open_ms=",
                     "read_ms=", "decode_wait_ms=", "decode_ms=", "store_ms=", "total_ms=", "io_lane=", "decode_lane=", "io_in_flight=", "decode_in_flight=" })
            Assert.All(tiles, line => Assert.Contains(field, line));
        Assert.All(tiles, line => Assert.DoesNotContain("IMG-", line));          // names appear as a hash only
        Assert.All(tiles, line => Assert.DoesNotContain(_dir.Path, line));
        Assert.All(tiles, line => Assert.True(OpenMsOf(line) >= 19, "the injected 20 ms open shows in open_ms: " + line));
        string burst;
        lock (lines) burst = lines.Single(l => l.Name == "thumbnail.burst").Payload;
        Assert.Contains("tiles=10", burst);
        Assert.Contains("io_lanes=4", burst);
        Assert.Contains("decode_lanes=3", burst);
    }

    [Fact]
    public void A_name_hash_is_stable_case_insensitive_and_not_the_name()
    {
        Assert.Equal(ThumbnailTrace.NameHash(@"C:\a\IMG-0001.JPG"), ThumbnailTrace.NameHash(@"D:\other\img-0001.jpg"));
        Assert.NotEqual(ThumbnailTrace.NameHash(@"C:\a\IMG-0001.jpg"), ThumbnailTrace.NameHash(@"C:\a\IMG-0002.jpg"));
        Assert.Equal(6, ThumbnailTrace.NameHash("x.jpg").Length);
    }

    [Fact]
    public void The_explorer_tab_hands_each_tile_s_screen_position_to_the_scheduler()
    {
        var source = SourceText.Read("src/Glide.App/MainWindow.axaml.cs");
        Assert.Contains("ExplorerThumbnailRank(entry)", source);
        Assert.Contains("new ThumbnailRequest(path, entry.Size, entry.Modified.Ticks, Math.Max(16, width), order)", source);
        Assert.Contains("priority, token, out var timing)", source);
        Assert.Contains("ThumbnailTrace.Applied(timing", source);
        Assert.Contains("new ThumbnailPipeline()", source);
    }

    private static double OpenMsOf(string line)
    {
        var start = line.IndexOf(" open_ms=", StringComparison.Ordinal) + " open_ms=".Length;
        var end = line.IndexOf(' ', start);
        return double.Parse(line[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }

    /// <summary>A pipeline whose decode stage is slowed down (the read-ahead then has to wait for it).</summary>
    private sealed class SlowDecode : IThumbnailPipeline
    {
        private readonly IThumbnailPipeline _inner;
        private readonly TimeSpan _delay;
        public SlowDecode(IThumbnailPipeline inner, TimeSpan delay) { _inner = inner; _delay = delay; }
        public bool HasIoStage => _inner.HasIoStage;
        public ThumbnailIoPlan PlanIo(in ThumbnailRequest request) => _inner.PlanIo(request);
        public ThumbnailPrepared Prepare(in ThumbnailRequest request, ThumbnailIoPlan plan, ThumbnailTiming timing) => _inner.Prepare(request, plan, timing);
        public Bitmap? Decode(in ThumbnailRequest request, ThumbnailPrepared prepared, ThumbnailTiming timing)
        {
            Thread.Sleep(_delay);
            return _inner.Decode(request, prepared, timing);
        }
    }
}
