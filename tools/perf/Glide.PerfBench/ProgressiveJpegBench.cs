using System.Diagnostics;
using System.Text.Json.Nodes;
using Glide.Imaging;

namespace Glide.PerfBench;

/// <summary>
/// Per-file cost of a progressive JPEG thumbnail (5.2.0 45 MP case). Timings only: no file name or pixel is printed or kept.
///   thumb-progressive --dir &lt;folder&gt; [--files 20] [--width 236] [--threads 1,12]
/// For each thread count: the DC-only decode from the path (GlideJpegProgressiveDcW), the Explorer route from memory
/// (GlideDecodeJpegThumbnailMemory, what the pipeline's decode lane calls) and from the path (GlideDecodeJpegThumbnailW),
/// median ms per file and the process's peak private bytes above the start of the run.
/// </summary>
internal static class ProgressiveJpegBench
{
    public static int Run(string[] args)
    {
        var dir = Path.GetFullPath(Args.Get(args, "--dir") ?? throw new ArgumentException("--dir required"));
        var count = Args.GetInt(args, "--files", 20);
        var width = Args.GetInt(args, "--width", 236);
        var threads = (Args.Get(args, "--threads") ?? "1,12").Split(',').Select(int.Parse).ToArray();
        var files = Directory.GetFiles(dir).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(count).ToArray();
        if (files.Length == 0) { Console.Error.WriteLine("no JPEG in folder"); return 2; }
        var bytes = files.Select(File.ReadAllBytes).ToArray(); // the reads are not timed
        var routes = new (string Name, Func<int, IDisposable?> Decode)[]
        {
            ("libjpegDcFromPath", i => NativeImageDecoder.TryDecodeProgressiveDcForTests(files[i], out var b) ? b : null),
            ("ownDcFromMemory", i => NativeImageDecoder.TryDecodeJpegDcOnlyForTests(bytes[i], out var b) ? b : null),
            ("thumbFromMemory", i => NativeImageDecoder.TryDecodeJpegThumbnailFromMemory(bytes[i], width, out var b) ? b : null),
            ("thumbFromPath", i => NativeImageDecoder.TryDecodeJpegThumbnail(files[i], width, out var b) ? b : null),
        };
        using (routes[1].Decode(0)) { } // JIT / library warm-up, not timed
        var result = new JsonObject { ["files"] = files.Length, ["width"] = width, ["logicalCpus"] = Environment.ProcessorCount };
        foreach (var t in threads)
        {
            var perThreads = new JsonObject();
            foreach (var (name, decode) in routes)
            {
                GC.Collect();
                using var self = Process.GetCurrentProcess();
                var basePrivate = self.PrivateMemorySize64;
                long peak = basePrivate;
                using var stop = new CancellationTokenSource();
                var sampler = Task.Run(() =>
                {
                    using var p = Process.GetCurrentProcess();
                    while (!stop.IsCancellationRequested) { p.Refresh(); peak = Math.Max(peak, p.PrivateMemorySize64); Thread.Sleep(2); }
                });
                var times = new double[files.Length];
                var failed = 0;
                var started = Stopwatch.GetTimestamp();
                Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = t }, i =>
                {
                    var s = Stopwatch.GetTimestamp();
                    using var bitmap = decode(i);
                    times[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
                    if (bitmap is null) Interlocked.Increment(ref failed);
                });
                var wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                stop.Cancel();
                sampler.Wait();
                perThreads[name] = new JsonObject
                {
                    ["medianMs"] = Math.Round(Stats.Median(times), 1), ["p95Ms"] = Math.Round(Stats.Percentile(times, 0.95), 1),
                    ["wallMs"] = Math.Round(wall, 1), ["failed"] = failed,
                    ["peakPrivateMbAboveBase"] = Math.Round((peak - basePrivate) / 1048576d, 1),
                };
            }
            result["threads" + t] = perThreads;
        }
        Console.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
