using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Glide.PerfBench;

/// <summary>
/// 5.2.0 queue order (audit item 9): how soon the first screen of an Explorer folder is filled. A mixed folder (every audit fixture
/// in a fixed shuffled order, so a 1 ms icon sits next to a 150 ms PSD) is decoded by the lane pool two ways:
///   fifo      the pre-5.2.0 gate: SemaphoreSlim(lanes), tiles start in the order the grid asked for them;
///   scheduler the 5.2.0 <c>ThumbnailScheduler</c>: cheapest cost class first, arrival order inside a class.
/// Both use the production decode (<c>ThumbnailDecode.DecodeToWidth</c>, disk cache off, warmed by one untimed pass) and the same
/// lanes. "screen" = the first N tiles of the folder (what is visible). Reported per variant (median of the runs): mean and median
/// time at which a visible tile gets its thumbnail, the time of the 10th, and the time all visible tiles are done.
/// </summary>
internal static class QueueBench
{
    private static readonly Assembly App = Assembly.Load("Glide");
    private static readonly Type DecodeType = App.GetType("Glide.App.Services.ThumbnailDecode", true)!;
    private static readonly MethodInfo Decode = DecodeType.GetMethod("DecodeToWidth", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly Type SchedulerType = App.GetType("Glide.App.Services.ThumbnailScheduler", true)!;
    private static readonly Type RequestType = App.GetType("Glide.App.Services.ThumbnailRequest", true)!;
    private static readonly Type PriorityType = App.GetType("Glide.App.Services.ThumbnailPriority", true)!;

    private static Bitmap? DecodeOne(string path, int width) => (Bitmap?)Decode.Invoke(null, new object[] { path, width });

    public static async Task<int> RunAsync(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? ".";
        var screen = Args.GetInt(args, "--screen", 40);
        var runs = Args.GetInt(args, "--runs", 7);
        var width = Args.GetInt(args, "--width", 152);
        var lanes = Args.GetInt(args, "--lanes", Math.Clamp(Environment.ProcessorCount * 3 / 4, 2, 12));
        var seed = Args.GetInt(args, "--seed", 5);
        var files = FileSet.List(data, Args.Get(args, "--filter")).Where(f => !f.Contains("bigtiff", StringComparison.OrdinalIgnoreCase)).ToList();
        var random = new Random(seed);
        files = files.OrderBy(_ => random.Next()).ToList();
        var visible = files.Take(screen).ToList();
        Console.Error.WriteLine($"queue: {files.Count} files, screen {visible.Count}, lanes {lanes}, {width} px");

        // Untimed pass: OS file cache, JIT, codec start-up.
        foreach (var file in files) DecodeOne(file, width)?.Dispose();

        var fifo = new List<double[]>();
        var sched = new List<double[]>();
        for (var run = 0; run < runs; run++)
        {
            fifo.Add(await Fifo(visible, lanes, width).ConfigureAwait(false));
            sched.Add(await Scheduled(visible, lanes, width).ConfigureAwait(false));
        }

        JsonNode Summary(List<double[]> samples)
        {
            double Med(Func<double[], double> f) => Stats.Median(samples.Select(f).ToArray());
            return new JsonObject
            {
                ["meanMs"] = Med(s => s.Average()),
                ["medianMs"] = Med(s => s.OrderBy(x => x).ElementAt(s.Length / 2)),
                ["tenthMs"] = Med(s => s.OrderBy(x => x).ElementAt(Math.Min(9, s.Length - 1))),
                ["lastMs"] = Med(s => s.Max()),
            };
        }
        var node = new JsonObject
        {
            ["files"] = files.Count, ["screen"] = visible.Count, ["lanes"] = lanes, ["width"] = width, ["runs"] = runs,
            ["fifo"] = Summary(fifo), ["scheduler"] = Summary(sched),
            ["visible"] = new JsonArray(visible.Select(v => (JsonNode)Path.GetFileName(v)).ToArray()),
        };
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "queue.json"), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>Completion time (ms since the start) of each visible tile under the old first-come lane gate.</summary>
    private static async Task<double[]> Fifo(List<string> visible, int lanes, int width)
    {
        using var gate = new SemaphoreSlim(lanes, lanes);
        var started = Stopwatch.GetTimestamp();
        var tasks = visible.Select(async path =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { using var bitmap = await Task.Run(() => DecodeOne(path, width)).ConfigureAwait(false); }
            finally { gate.Release(); }
            return Timing.Ms(started);
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>The same under the 5.2.0 scheduler (reflection: its types are internal to the app assembly).</summary>
    private static async Task<double[]> Scheduled(List<string> visible, int lanes, int width)
    {
        var scheduler = Activator.CreateInstance(SchedulerType, lanes, null, null)!;
        var run = SchedulerType.GetMethod("RunAsync")!;
        var started = Stopwatch.GetTimestamp();
        var tasks = visible.Select(async path =>
        {
            var info = new FileInfo(path);
            var request = Activator.CreateInstance(RequestType, path, info.Length, info.LastWriteTimeUtc.Ticks, width)!;
            var task = (Task<Bitmap?>)run.Invoke(scheduler, new[] { request, Enum.ToObject(PriorityType, 0), CancellationToken.None })!;
            using var bitmap = await task.ConfigureAwait(false);
            return Timing.Ms(started);
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
