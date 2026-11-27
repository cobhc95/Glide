using System.Diagnostics;
using System.Text.Json.Nodes;
using Glide.App.Services;
using Glide.Imaging;
using SkiaSharp;

namespace Glide.PerfBench;

/// <summary>
/// Headless benchmark of the Explorer-tab thumbnail pipeline (5.2.0 I/O stage + decode lanes) on a folder of JPEGs, with optional
/// injected per-open latency. Never starts Glide.exe and never writes to the folder it measures; the thumbnail disk cache stays off.
///   thumb-pipeline --dir &lt;folder&gt; [--generate N] [--mode legacy|pipeline] [--latency-ms L] [--device-parallel K]
///                  [--lanes N] [--io-lanes N] [--width 236] [--win-cache 0|1] [--label text] [--json out.json]
///   thumb-read-scaling --dir &lt;folder&gt; [--threads 1,4,12]       (whole-file reads only, no decode)
/// legacy = the 5.2.0 scheduler as released (decode lanes open the files themselves); pipeline = I/O lanes read ahead, decode lanes
/// decode from memory. --generate fills an empty (or new) folder with N copies of one synthetic 3744 x 5616 JPEG of about 2.3 MB.
/// Only timings are recorded: no file name, no pixel is printed or kept (names appear as a 6-digit hash in the per-tile trace).
/// </summary>
internal static class ThumbPipelineBench
{
    public static int Run(string[] args)
    {
        var dir = Path.GetFullPath(Args.Get(args, "--dir") ?? throw new ArgumentException("--dir required"));
        var generate = Args.GetInt(args, "--generate", 0);
        if (generate > 0) Generate(dir, generate);
        var mode = (Args.Get(args, "--mode") ?? "pipeline").ToLowerInvariant();
        var latency = Args.GetInt(args, "--latency-ms", 0);
        var devicePar = Args.GetInt(args, "--device-parallel", 0);
        var lanes = Args.GetInt(args, "--lanes", ImagePerformanceGovernor.ThumbnailDecodeLanes);
        var ioLanes = Args.GetInt(args, "--io-lanes", ThumbnailScheduler.DefaultIoLanes(lanes));
        var width = Args.GetInt(args, "--width", 236);
        var winCache = Args.GetInt(args, "--win-cache", 0) != 0;
        var visible = Args.GetInt(args, "--visible", 0);
        var label = Args.Get(args, "--label") ?? mode;
        s_allFormats = Args.GetInt(args, "--all-formats", 0) != 0;

        var files = Directory.GetFiles(dir).Where(IsPicture).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) { Console.Error.WriteLine("no pictures in " + dir); return 2; }
        ThumbnailDiskCache.Configure(false, null, 0);

        using var device = devicePar > 0 ? new SemaphoreSlim(devicePar) : null;
        void Latency()
        {
            if (latency <= 0) return;
            device?.Wait();
            try { Thread.Sleep(latency); }
            finally { device?.Release(); }
        }

        ThumbnailScheduler scheduler;
        if (mode == "legacy")
            scheduler = new ThumbnailScheduler(lanes, (path, w) => { Latency(); return ThumbnailDecode.DecodeToWidth(path, w); }, TimeSpan.FromSeconds(Args.GetInt(args, "--timeout-s", 300)));
        else
            scheduler = new ThumbnailScheduler(lanes, new ThumbnailPipeline { BeforeOpen = _ => Latency(), UseWindowsCache = winCache }, ioLanes,
                TimeSpan.FromSeconds(Args.GetInt(args, "--timeout-s", 300)), TimeSpan.FromMinutes(5));

        var tiles = new List<ThumbnailTiming>();
        // The per-tile records come back from RunAsync (out parameter) in both modes.
        var infos = files.Select(f => new FileInfo(f)).ToArray();
        // Untimed warm-up of the decoder and the JIT on the first file (not part of the measurement), through the same entry the scheduler uses.
        using (var warm = ThumbnailDecode.DecodeUncached(files[0], width)) { }

        // --warm-win-cache 1: ask the shell for each (synthetic) file once, which makes Windows store its thumbnail; the probe cost before
        // and after is reported. Never used on a folder that is not the benchmark's own.
        var missMs = new List<double>();
        var warmMs = new List<double>();
        if (Args.GetInt(args, "--warm-win-cache", 0) != 0)
        {
            foreach (var file in files.Take(5)) missMs.Add(TimeIt(() => ThumbnailPipeline.ProbeWindowsCache(file, width)));
            foreach (var file in files) warmMs.Add(TimeIt(() => { NativeImageDecoder.TryDecodeShellPreview(file, width, out var bitmap); return bitmap; }));
        }

        // Peak private bytes of the process during the run (decode buffers, file bytes waiting for a lane, bitmaps).
        GC.Collect();
        long basePrivate, peakPrivate;
        using (var self = Process.GetCurrentProcess()) basePrivate = peakPrivate = self.PrivateMemorySize64;
        using var stopSampling = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            using var p = Process.GetCurrentProcess();
            while (!stopSampling.IsCancellationRequested) { p.Refresh(); peakPrivate = Math.Max(peakPrivate, p.PrivateMemorySize64); Thread.Sleep(3); }
        });
        var started = Stopwatch.GetTimestamp();
        var tasks = new Task<Avalonia.Media.Imaging.Bitmap?>[files.Length];
        var completed = new double[files.Length];
        var ok = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var index = i;
            var priority = visible > 0 && i >= visible ? ThumbnailPriority.Near : ThumbnailPriority.Visible;
            var request = new ThumbnailRequest(files[i], infos[i].Length, infos[i].LastWriteTimeUtc.Ticks, width, i);
            tasks[i] = scheduler.RunAsync(request, priority, CancellationToken.None, out var timing);
            lock (tiles) tiles.Add(timing);
            _ = tasks[i].ContinueWith(t =>
            {
                completed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (t.IsCompletedSuccessfully && t.Result is { } bitmap) { Interlocked.Increment(ref ok); bitmap.Dispose(); }
            }, TaskScheduler.Default);
        }
        Task.WaitAll(tasks);
        Thread.Sleep(20); // let the continuations store their completion times
        var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        stopSampling.Cancel();
        sampler.Wait();

        var done = completed.Where(v => v > 0).OrderBy(v => v).ToArray();
        var node = new JsonObject
        {
            ["label"] = label, ["mode"] = mode, ["files"] = files.Length, ["latencyMs"] = latency, ["devicePar"] = devicePar,
            ["lanes"] = lanes, ["ioLanes"] = mode == "legacy" ? 0 : ioLanes, ["winCache"] = winCache,
            ["tilesOk"] = ok, ["totalMs"] = Math.Round(total, 1), ["firstMs"] = Math.Round(done.FirstOrDefault(), 1),
            ["p50Ms"] = Math.Round(Stats.Percentile(done, 0.5), 1), ["p90Ms"] = Math.Round(Stats.Percentile(done, 0.9), 1),
            ["tilesPerSecond"] = Math.Round(files.Length / (total / 1000.0), 1),
            ["peakIoInFlight"] = scheduler.PeakIoInFlight, ["peakDecodeInFlight"] = scheduler.PeakDecodeInFlight,
            ["timedOut"] = scheduler.TimedOutCount,
            ["peakPrivateMbAboveStart"] = Math.Round((peakPrivate - basePrivate) / 1048576d, 1),
        };
        if (warmMs.Count > 0)
        {
            node["winCacheMissProbeMs"] = Math.Round(Stats.Median(missMs), 2);
            node["winCacheWarmCallMs"] = Math.Round(Stats.Median(warmMs), 2);
        }
        AddStage(node, "open", tiles.Select(t => t.OpenMs).Where(v => v > 0));
        AddStage(node, "winProbe", tiles.Select(t => t.WindowsCacheMs).Where(v => v > 0));
        AddStage(node, "read", tiles.Select(t => t.ReadMs).Where(v => v > 0));
        AddStage(node, "decode", tiles.Select(t => t.DecodeMs).Where(v => v > 0));
        AddStage(node, "ioWait", tiles.Select(t => t.IoWaitMs));
        AddStage(node, "decodeWait", tiles.Select(t => t.DecodeWaitMs));
        node["outcomes"] = new JsonObject(tiles.GroupBy(t => t.Outcome.Length == 0 ? "none" : t.Outcome).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));
        node["completionOrderIsReadingOrder"] = IsMostlyAscending(completed);
        var json = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (Args.Get(args, "--json") is { } path) File.WriteAllText(path, json);
        return 0;
    }

    private static double TimeIt(Func<IDisposable?> action)
    {
        var started = Stopwatch.GetTimestamp();
        using var result = action();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static bool IsPicture(string path) => s_allFormats
        ? ThumbnailRoutes.CanThumbnail(path)
        : path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static bool s_allFormats;

    private static void AddStage(JsonObject node, string name, IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return;
        node[name + "Ms"] = new JsonObject
        {
            ["n"] = list.Count, ["min"] = Math.Round(list.Min(), 1), ["median"] = Math.Round(Stats.Median(list), 1),
            ["p95"] = Math.Round(Stats.Percentile(list, 0.95), 1), ["max"] = Math.Round(list.Max(), 1),
        };
    }

    private static bool IsMostlyAscending(double[] completedAt)
    {
        var inversions = 0;
        for (var i = 1; i < completedAt.Length; i++)
            if (completedAt[i] + 1 < completedAt[i - 1]) inversions++;
        return inversions < completedAt.Length / 4;
    }

    /// <summary>
    /// Concurrency scaling of plain whole-file reads (open + read, no decode), per thread count; timings only. --unbuffered 1 opens with
    /// FILE_FLAG_NO_BUFFERING so the read bypasses the operating system's file cache (the device and any filter driver in front of it
    /// are measured, not memory); it only reads, so it changes nothing on the disk or in the cache.
    /// </summary>
    public static unsafe int ReadScaling(string[] args)
    {
        var dir = Path.GetFullPath(Args.Get(args, "--dir") ?? throw new ArgumentException("--dir required"));
        var counts = (Args.Get(args, "--threads") ?? "1,4,12").Split(',').Select(int.Parse).ToArray();
        var unbuffered = Args.GetInt(args, "--unbuffered", 0) != 0;
        var files = Directory.GetFiles(dir).Where(IsPicture).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
        var limit = Args.GetInt(args, "--files", files.Length);
        // An unbuffered pass reads a different slice per thread count (nothing to warm); a buffered one repeats the same files.
        var results = new JsonArray();
        var slice = 0;
        foreach (var threads in counts)
        {
            var batch = unbuffered ? files.Skip(slice * limit).Take(limit).ToArray() : files.Take(limit).ToArray();
            slice++;
            if (batch.Length == 0) batch = files.Take(limit).ToArray();
            var opens = new double[batch.Length];
            var reads = new double[batch.Length];
            long bytes = 0;
            var next = -1;
            var started = Stopwatch.GetTimestamp();
            var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() =>
            {
                const int Chunk = 1 << 20;
                var aligned = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)Chunk, 4096);
                try
                {
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < batch.Length)
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        using var handle = File.OpenHandle(batch[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                            unbuffered ? (FileOptions)0x20000000 : FileOptions.SequentialScan);
                        var t1 = Stopwatch.GetTimestamp();
                        var length = RandomAccess.GetLength(handle);
                        long offset = 0;
                        while (offset < length)
                        {
                            var got = RandomAccess.Read(handle, new Span<byte>(aligned, Chunk), offset);
                            if (got <= 0) break;
                            offset += got;
                        }
                        var t2 = Stopwatch.GetTimestamp();
                        opens[i] = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                        reads[i] = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                        Interlocked.Add(ref bytes, Math.Min(offset, length));
                    }
                }
                finally { System.Runtime.InteropServices.NativeMemory.AlignedFree(aligned); }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(workers);
            var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var row = new JsonObject
            {
                ["threads"] = threads, ["files"] = batch.Length, ["unbuffered"] = unbuffered, ["totalMs"] = Math.Round(total, 1),
                ["mbPerSecond"] = Math.Round(bytes / 1048576.0 / (total / 1000), 1), ["filesPerSecond"] = Math.Round(batch.Length / (total / 1000), 1),
            };
            AddStage(row, "open", opens);
            AddStage(row, "read", reads);
            results.Add(row);
        }
        var json = results.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (Args.Get(args, "--json") is { } path) File.WriteAllText(path, json);
        return 0;
    }

    /// <summary>Creates <paramref name="count"/> copies of one synthetic 3744 x 5616 JPEG (about 2.3 MB, no EXIF thumbnail) in <paramref name="dir"/>.</summary>
    internal static void Generate(string dir, int count)
    {
        Directory.CreateDirectory(dir);
        if (Directory.EnumerateFileSystemEntries(dir).Any()) throw new InvalidOperationException("--generate needs an empty folder: " + dir);
        var bytes = MakeJpeg(3744, 5616, 2_300_000);
        for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(dir, $"IMG-{i + 1:0000}.jpg"), bytes);
    }

    /// <summary>A photo-like JPEG (smooth gradients, shapes and fine noise) near <paramref name="targetBytes"/>.</summary>
    internal static unsafe byte[] MakeJpeg(int width, int height, int targetBytes)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [new SKColor(30, 60, 120), new SKColor(220, 170, 90)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, paint);
            var random = new Random(7);
            using var shape = new SKPaint { IsAntialias = true };
            for (var i = 0; i < 400; i++)
            {
                shape.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(40, 160));
                canvas.DrawOval(random.Next(width), random.Next(height), random.Next(40, 600), random.Next(40, 600), shape);
            }
        }
        var pixels = new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
        var noise = new Random(11);
        var chunk = new byte[1 << 20];
        for (var offset = 0; offset < pixels.Length; offset += chunk.Length)
        {
            var n = Math.Min(chunk.Length, pixels.Length - offset);
            noise.NextBytes(chunk.AsSpan(0, n));
            for (var i = 0; i < n; i++)
                if ((i & 3) != 3) pixels[offset + i] = (byte)Math.Clamp(pixels[offset + i] + ((chunk[i] & 15) - 8), 0, 255);
        }
        byte[]? best = null;
        foreach (var quality in new[] { 90, 85, 80, 75, 70, 62, 55, 45 })
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            best = data.ToArray();
            if (best.Length <= targetBytes * 1.15) break;
        }
        return best!;
    }
}
