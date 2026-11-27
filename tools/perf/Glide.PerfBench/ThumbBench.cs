using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;

namespace Glide.PerfBench;

/// <summary>
/// Explorer-tab thumbnail cost through the production entry point <c>Glide.App.Services.ThumbnailDecode.DecodeToWidth</c>
/// (reached by reflection so one bench source runs against both the baseline and the optimised build):
///   warm  = median of N decodes in one process after one untimed warm-up (codecs loaded, OS file cache warm),
///   cold  = the first decode in a brand-new process (thumb-one mode, spawned per file by run-thumb-bench.ps1),
///   miss / hit = with the persistent thumbnail disk cache configured on an empty folder: the decode that stores the
///                entry, then the median of N reads of it (columns are n/a on a build without the cache).
/// </summary>
internal static class ThumbBench
{
    private static readonly MethodInfo DecodeMethod = FindDecode();
    private static readonly MethodInfo? CacheConfigure = FindCacheConfigure();

    private static MethodInfo FindDecode()
    {
        var app = System.Reflection.Assembly.Load("Glide");
        var type = app.GetType("Glide.App.Services.ThumbnailDecode", throwOnError: true)!;
        return type.GetMethod("DecodeToWidth", BindingFlags.Public | BindingFlags.Static)!;
    }

    private static MethodInfo? FindCacheConfigure()
    {
        var type = typeof(Glide.Imaging.ImageLoadCoordinator).Assembly.GetType("Glide.Imaging.ThumbnailDiskCache");
        return type?.GetMethod("ConfigureForBench", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    public static bool HasCache => CacheConfigure is not null;

    public static void ConfigureCache(string? directory, long maxBytes) =>
        CacheConfigure?.Invoke(null, new object?[] { directory, maxBytes });

    /// <summary>Experiment switch (--variant native): the viewer's WIC bridge instead of the Explorer entry point.</summary>
    public static string Variant = "";

    public static (double Ms, string Size) DecodeOnce(string path, int width)
    {
        var started = Stopwatch.GetTimestamp();
        Bitmap? bitmap = null;
        try
        {
            if (Variant == "native") bitmap = Glide.Imaging.NativeImageDecoder.TryDecode(path, width, out var native) ? native : null;
            else bitmap = (Bitmap?)DecodeMethod.Invoke(null, new object[] { path, width });
        }
        catch (TargetInvocationException ex) { return (double.NaN, "error: " + ex.InnerException?.GetType().Name + " " + ex.InnerException?.Message.Split((char)10)[0]); }
        var ms = Timing.Ms(started);
        var size = bitmap is null ? "none" : $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}";
        bitmap?.Dispose();
        return (ms, size);
    }

    /// <summary>Concurrency probe (webp-stress): N threads x M native bounded decodes of one file; prints how many failed.</summary>
    public static int Stress(string path, int width, int threads, int each)
    {
        var failed = 0; var ok = 0;
        var started = Stopwatch.GetTimestamp();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            for (var i = 0; i < each; i++)
            {
                if (Glide.Imaging.NativeImageDecoder.TryDecode(path, width, out var bitmap)) { Interlocked.Increment(ref ok); bitmap.Dispose(); }
                else Interlocked.Increment(ref failed);
            }
        });
        Console.WriteLine($"stress {Path.GetFileName(path)}: ok={ok} failed={failed} total {Timing.Ms(started):0} ms");
        return 0;
    }

    /// <summary>PNG feasibility probe (png-probe): time of the zlib inflate of a PNG's IDAT stream alone (System.IO.Compression, zlib-ng).</summary>
    public static int PngProbe(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var idat = new MemoryStream();
        var pos = 8;
        while (pos + 12 <= bytes.Length)
        {
            var len = (bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (type == "IDAT") idat.Write(bytes, pos + 8, len);
            pos += 12 + len;
        }
        var compressed = idat.ToArray();
        var scratch = new byte[1 << 20];
        var times = new List<double>();
        long raw = 0;
        for (var run = 0; run < 7; run++)
        {
            var started = Stopwatch.GetTimestamp();
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress);
            raw = 0;
            int n;
            while ((n = z.Read(scratch, 0, scratch.Length)) > 0) raw += n;
            times.Add(Timing.Ms(started));
        }
        Console.WriteLine($"{Path.GetFileName(path)}: IDAT {compressed.Length / 1e6:0.0} MB -> {raw / 1e6:0.0} MB, inflate alone median {Stats.Median(times):0.0} ms (min {times.Min():0.0})");
        return 0;
    }

    /// <summary>
    /// Whole-folder time (grid mode): every file of --dir decoded to --width with N decode lanes (workers pulling from one list, the way
    /// the Explorer tab's thumbnail gate does), median of --runs after one untimed pass. Reports the time until all thumbnails exist.
    /// --cache on: the first pass stores the thumbnails in the disk cache and the timed passes are all hits.
    /// </summary>
    public static int Grid(string[] args)
    {
        var dir = Args.Get(args, "--dir") ?? throw new ArgumentException("--dir required");
        var width = Args.GetInt(args, "--width", 152);
        var runs = Args.GetInt(args, "--runs", 3);
        var lanesList = (Args.Get(args, "--lanes") ?? "1,2,4,8,12,16").Split(',').Select(int.Parse).ToArray();
        var cache = (Args.Get(args, "--cache") ?? "off") == "on";
        var files = Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var cacheDir = Path.Combine(Path.GetTempPath(), "glide-grid-cache-" + Guid.NewGuid().ToString("N"));
        ConfigureCache(cache ? cacheDir : null, 2048L * 1024 * 1024);
        int failed = 0;
        double Pass(int lanes)
        {
            var next = -1;
            failed = 0;
            var started = Stopwatch.GetTimestamp();
            var workers = Enumerable.Range(0, lanes).Select(_ => Task.Run(() =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < files.Length)
                {
                    var (ms, size) = DecodeOnce(files[i], width);
                    if (double.IsNaN(ms) || size == "none") Interlocked.Increment(ref failed);
                }
            })).ToArray();
            Task.WaitAll(workers);
            return Timing.Ms(started);
        }
        Pass(Environment.ProcessorCount); // warm: OS file cache, codecs, and (cache on) the stored thumbnails
        ThumbnailWaitForWrites();
        Console.WriteLine($"grid {files.Length} files from {dir}, {width} px, cache {(cache ? "on (all hits)" : "off")}, {Environment.ProcessorCount} logical CPUs");
        foreach (var lanes in lanesList)
        {
            var times = new List<double>();
            for (var r = 0; r < runs; r++) times.Add(Pass(lanes));
            Console.WriteLine($"lanes {lanes,2}: median {Stats.Median(times),8:0.0} ms  (min {times.Min():0.0}, max {times.Max():0.0})  per image {Stats.Median(times) / files.Length:0.00} ms, no thumbnail for {failed} file(s)");
        }
        ConfigureCache(null, 0);
        try { Directory.Delete(cacheDir, true); } catch { /* temp */ }
        return 0;
    }

    private static void ThumbnailWaitForWrites()
    {
        var type = typeof(Glide.Imaging.ImageLoadCoordinator).Assembly.GetType("Glide.Imaging.ThumbnailDiskCache");
        var current = type?.GetProperty("Current")?.GetValue(null);
        current?.GetType().GetMethod("WaitForWrites")?.Invoke(current, new object[] { TimeSpan.FromSeconds(60) });
    }

    public static int RunOne(string path, int width, string? jsonOut, bool preload = false)
    {
        if (preload)
        {
            // 5.2.0 --preload: the folder listing has warmed the codec plug-in (CodecPreloader) before the first tile is decoded; not timed.
            if (Glide.Imaging.CodecPreloader.NeedsProvider(path))
            {
                Glide.Imaging.CodecPreloader.NotifyListing(new[] { path });
                var wait = Stopwatch.StartNew();
                while (Glide.Imaging.CodecPreloader.ResolvedCount < 1 && wait.ElapsedMilliseconds < 10_000) Thread.Sleep(5);
            }
            if (Glide.Imaging.SvgDecoder.IsSvgPath(path)) Glide.Imaging.SvgTileRenderer.WarmUp();
        }
        var (ms, size) = DecodeOnce(path, width);
        var node = new JsonObject { ["file"] = Path.GetFileName(path), ["width"] = width, ["coldMs"] = double.IsFinite(ms) ? (double?)ms : null, ["size"] = size };
        if (jsonOut is not null) File.WriteAllText(jsonOut, node.ToJsonString());
        Console.WriteLine(node.ToJsonString());
        return 0;
    }

    public static int Run(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? Path.Combine(data, "..", "perf-results");
        var label = Args.Get(args, "--label") ?? "run";
        var runs = Args.GetInt(args, "--runs", 7);
        var filter = Args.Get(args, "--filter");
        Variant = Args.Get(args, "--variant") ?? "";
        var widths = (Args.Get(args, "--widths") ?? "152,236").Split(',').Select(int.Parse).ToArray();
        var cacheRoot = Path.Combine(Path.GetTempPath(), "glide-thumb-bench-cache-" + Guid.NewGuid().ToString("N"));
        var files = Directory.GetFiles(Path.Combine(data, "single"))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Where(f => filter is null || Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
        var report = new Report("thumb");
        var md = report.Markdown;
        md.AppendLine($"## Explorer thumbnails ({label}) - median of {runs} warm runs, logical CPUs {Environment.ProcessorCount}, disk cache {(HasCache ? "present" : "absent")}");
        md.AppendLine();
        md.AppendLine("| File | Width | Warm ms (median) | min-max | Noisy | Output px | Cache miss+store ms | Cache hit ms (median) |");
        md.AppendLine("|---|---:|---:|---|---|---|---:|---:|");
        var items = new JsonArray();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            foreach (var width in widths)
            {
                ConfigureCache(null, 0);
                DecodeOnce(file, width); // untimed warm-up
                var samples = new List<double>();
                string size = "";
                for (var i = 0; i < runs; i++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    var (ms, s) = DecodeOnce(file, width);
                    if (!double.IsNaN(ms)) samples.Add(ms);
                    size = s;
                }
                var median = Stats.Median(samples);
                var min = samples.Count > 0 ? samples.Min() : double.NaN;
                var max = samples.Count > 0 ? samples.Max() : double.NaN;
                var noisy = samples.Count > 0 && (max - min) / Math.Max(median, 0.01) > 0.6 && max - min > 2.0;
                double miss = double.NaN, hit = double.NaN;
                if (HasCache && size != "none" && !size.StartsWith("error", StringComparison.Ordinal))
                {
                    var dir = Path.Combine(cacheRoot, Path.GetFileNameWithoutExtension(name) + "-" + width);
                    ConfigureCache(dir, 512L * 1024 * 1024);
                    miss = DecodeOnce(file, width).Ms;
                    var hits = new List<double>();
                    for (var i = 0; i < runs; i++) hits.Add(DecodeOnce(file, width).Ms);
                    hit = Stats.Median(hits);
                    ConfigureCache(null, 0);
                }
                items.Add(new JsonObject
                {
                    ["file"] = name, ["width"] = width, ["warmMs"] = median, ["minMs"] = min, ["maxMs"] = max, ["noisy"] = noisy,
                    ["size"] = size, ["missMs"] = miss, ["hitMs"] = hit, ["samples"] = new JsonArray(samples.Select(s => (JsonNode)s).ToArray())
                });
                md.AppendLine($"| {name} | {width} | {Stats.Ms(median)} | {Stats.Ms(min)}-{Stats.Ms(max)} | {(noisy ? "NOISY" : "")} | {size} | {Stats.Ms(miss)} | {Stats.Ms(hit)} |");
                Console.Error.WriteLine($"thumb {name}@{width}: {Stats.Ms(median)} ms {size}{(noisy ? " NOISY" : "")}");
            }
        }
        try { Directory.Delete(cacheRoot, true); } catch { /* temp */ }
        report.Root["items"] = items;
        report.Save(outDir, $"thumb-{label}");
        return 0;
    }
}
