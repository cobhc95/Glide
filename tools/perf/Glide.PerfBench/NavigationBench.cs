using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.PerfBench;

/// <summary>
/// Next/previous navigation through a folder, modelled on MainWindow.NavigateCoreAsync / PresentCurrentAsync:
///  - normal step (interval &gt; 150 ms): foreground load with the default policy, then SchedulePrefetch and the
///    Fit-mode refinement chain in the background (and the 650 ms preview-quality recovery full decode);
///  - rapid step (interval &lt;= 150 ms): rapid-browse policy, previous request cancelled, no prefetch, previews
///    not cached; 220 ms after the last press the rapid-settle full decode runs.
/// Latency = key press to the bitmap being ready for the viewport.
/// </summary>
internal static class NavigationBench
{
    private sealed class Viewer : IDisposable
    {
        public readonly ImageLoadCoordinator Loader = new();
        private Bitmap? _displayed;
        private CancellationTokenSource _request = new();
        private CancellationTokenSource _background = new();
        private readonly List<Task> _backgroundTasks = new();
        public int FramesShown;
        public int PreparedHits;

        public Viewer()
        {
            // Mirrors MainWindow: the loader asks whether the viewport still shows a bitmap.
            Loader.IsBitmapInActiveUse = bitmap => bitmap is not null && ReferenceEquals(Volatile.Read(ref _displayed), bitmap);
        }

        public CancellationToken BeginRequest()
        {
            _request.Cancel(); _request.Dispose();
            _background.Cancel(); _background.Dispose();
            _request = new CancellationTokenSource();
            _background = new CancellationTokenSource();
            return _request.Token;
        }

        public void Show(ImageLoadResult result)
        {
            var old = _displayed;
            _displayed = result.Bitmap;
            Loader.SetActivePath(result.Path);
            FramesShown++;
            if (result.PreparedFrameHit) PreparedHits++;
            if (old is not null && !ReferenceEquals(old, result.Bitmap)) Retire(old);
            else if (old is not null && Ownership.Leases(Loader, old) > 1) Ownership.Release(Loader, old);
        }

        private void Retire(Bitmap bitmap)
        {
            // Same rule as MainWindow's post-fence retire: give back the presentation lease.
            Ownership.Release(Loader, bitmap);
        }

        public void StartBackground(ImageLoadResult first, bool recovery)
        {
            var token = _background.Token;
            _backgroundTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var stillPreview = first.IsPreview;
                    if (first.IsPreview)
                    {
                        var refined = await Loader.RefineForegroundAsync(first, token).ConfigureAwait(false);
                        if (refined is not null && !token.IsCancellationRequested)
                        {
                            if (refined.IsPreview)
                            {
                                Ownership.Release(Loader, refined.Bitmap);
                                var full = await Loader.LoadFullForegroundAsync(first.Path, token, cacheResult: false).ConfigureAwait(false);
                                if (full is not null) { lock (this) { if (!token.IsCancellationRequested) { ShowRefined(first, full); stillPreview = false; } else Ownership.Release(Loader, full.Bitmap); } }
                            }
                            else
                            {
                                lock (this) { ShowRefined(first, refined); stillPreview = false; }
                            }
                        }
                    }
                    if (recovery && stillPreview)
                    {
                        await Task.Delay(650, token).ConfigureAwait(false);
                        var full = await Loader.LoadFullForegroundAsync(first.Path, token).ConfigureAwait(false);
                        if (full is not null) lock (this) { if (!token.IsCancellationRequested) Show(full); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine("background: " + ex.Message); }
            }));
        }

        private void ShowRefined(ImageLoadResult first, ImageLoadResult refined)
        {
            var old = _displayed;
            _displayed = refined.Bitmap;
            var promoted = Loader.PromoteRefinement(first, refined);
            // Baseline build: PromoteRefinement disposed the old cached frame itself.
            if (old is not null && !ReferenceEquals(old, refined.Bitmap) && (!promoted || Ownership.HasLeases)) Retire(old);
        }

        public async Task DrainAsync()
        {
            try { await Task.WhenAll(_backgroundTasks).ConfigureAwait(false); } catch { }
            _backgroundTasks.Clear();
        }

        public void Dispose()
        {
            _request.Cancel(); _background.Cancel();
            DrainAsync().GetAwaiter().GetResult();
            var displayed = _displayed;
            _displayed = null;
            Loader.SetActivePath(null);
            Ownership.Release(Loader, displayed);
            Loader.Dispose();
        }
    }

    public sealed record ScenarioResult(string Name, List<double> Latencies, double SettleMs, double PeakDeltaMb, int Frames, int PreparedHits, ImageCacheSnapshot Cache, double CpuMs = 0);

    private static TimeSpan Cpu() => System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

    private static async Task<ScenarioResult> SteadyAsync(string name, IReadOnlyList<string> paths, int steps, int dwellMs, bool prefetch, IReadOnlyList<string>? folderOrder = null)
    {
        var policy = BenchPolicies.AppDefault(prefetch);
        var cpuStart = Cpu();
        using var memory = new MemorySampler();
        var latencies = new List<double>();
        ImageCacheSnapshot snapshot;
        int frames, hits;
        using (var viewer = new Viewer())
        {
            viewer.Loader.Policy = policy;
            for (var i = 0; i <= steps && i < paths.Count; i++)
            {
                var token = viewer.BeginRequest();
                viewer.Loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
                viewer.Loader.SetViewportDemand(paths[i], new ImageViewportDemand(BenchPolicies.ViewportWidth, BenchPolicies.ViewportHeight));
                var started = Stopwatch.GetTimestamp();
                var result = await viewer.Loader.LoadForegroundAsync(paths[i], policy, token).ConfigureAwait(false);
                var ms = Timing.Ms(started);
                if (result is null) continue;
                lock (viewer) viewer.Show(result);
                if (i > 0) latencies.Add(ms); // step 0 is the folder open, not a navigation
                if (prefetch)
                {
                    // Prefetch always follows folder order; folderOrder lets a scenario jump around it.
                    if (folderOrder is null) viewer.Loader.SchedulePrefetch(paths, i, 1);
                    else viewer.Loader.SchedulePrefetch(folderOrder, IndexOf(folderOrder, paths[i]), 1);
                }
                viewer.StartBackground(result, recovery: true);
                await Task.Delay(dwellMs).ConfigureAwait(false);
            }
            snapshot = viewer.Loader.GetCacheSnapshot();
            frames = viewer.FramesShown;
            hits = viewer.PreparedHits;
        }
        return new ScenarioResult(name, latencies, double.NaN, memory.PeakDeltaMb, frames, hits, snapshot, (Cpu() - cpuStart).TotalMilliseconds);
    }

    private static async Task<ScenarioResult> BurstAsync(string name, IReadOnlyList<string> paths, int presses, int intervalMs)
    {
        var normal = BenchPolicies.AppDefault();
        var rapid = BenchPolicies.AppRapid();
        var cpuStart = Cpu();
        using var memory = new MemorySampler();
        var latencies = new List<double>();
        double settle;
        ImageCacheSnapshot snapshot;
        int frames, hits;
        using (var viewer = new Viewer())
        {
            viewer.Loader.Policy = normal;
            // Open the first image normally and let its neighbours start preparing (a real user lands
            // on an image, then holds the arrow key).
            var openToken = viewer.BeginRequest();
            var opened = await viewer.Loader.LoadForegroundAsync(paths[0], normal, openToken).ConfigureAwait(false);
            if (opened is not null) { lock (viewer) viewer.Show(opened); viewer.Loader.SchedulePrefetch(paths, 0, 1); viewer.StartBackground(opened, recovery: true); }
            await Task.Delay(600).ConfigureAwait(false);

            var inflight = new List<Task>();
            long lastPress = 0;
            var index = 0;
            for (var p = 0; p < presses && index + 1 < paths.Count; p++)
            {
                index++;
                var path = paths[index];
                var token = viewer.BeginRequest();
                var rapidStep = p > 0;
                viewer.Loader.ReportNavigationActivity(rapidStep ? ImageNavigationActivity.RapidBrowse : ImageNavigationActivity.NormalBrowse);
                var started = Stopwatch.GetTimestamp();
                lastPress = started;
                var stepIndex = index;
                inflight.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await viewer.Loader.LoadForegroundAsync(path, rapidStep ? rapid : normal, token,
                            cachePreview: !rapidStep).ConfigureAwait(false);
                        if (result is null) return;
                        if (token.IsCancellationRequested)
                        {
                            Ownership.Release(viewer.Loader, result.Bitmap);
                            return;
                        }
                        lock (viewer) { viewer.Show(result); latencies.Add(Timing.Ms(started)); }
                        if (!rapidStep) viewer.Loader.SchedulePrefetch(paths, stepIndex, 1);
                    }
                    catch (OperationCanceledException) { }
                }));
                await Task.Delay(intervalMs).ConfigureAwait(false);
            }
            await Task.WhenAll(inflight).ConfigureAwait(false);
            // Rapid settle: 220 ms after the last press MainWindow forces the full frame of the final image.
            var sinceLast = Stopwatch.GetElapsedTime(lastPress).TotalMilliseconds;
            if (sinceLast < 220) await Task.Delay(TimeSpan.FromMilliseconds(220 - sinceLast)).ConfigureAwait(false);
            var settleToken = viewer.BeginRequest();
            viewer.Loader.ReportNavigationActivity(ImageNavigationActivity.NormalBrowse);
            var full = await viewer.Loader.LoadFullForegroundAsync(paths[index], settleToken).ConfigureAwait(false);
            settle = Timing.Ms(lastPress);
            if (full is not null) lock (viewer) viewer.Show(full);
            snapshot = viewer.Loader.GetCacheSnapshot();
            frames = viewer.FramesShown;
            hits = viewer.PreparedHits;
        }
        return new ScenarioResult(name, latencies, settle, memory.PeakDeltaMb, frames, hits, snapshot, (Cpu() - cpuStart).TotalMilliseconds);
    }

    private static int IndexOf(IReadOnlyList<string> list, string path)
    {
        for (var i = 0; i < list.Count; i++) if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Deterministic far-jump order: every step lands outside the previous prefetch window.</summary>
    private static IReadOnlyList<string> Shuffle(IReadOnlyList<string> paths)
    {
        var random = new Random(4242);
        return paths.OrderBy(_ => random.Next()).ToArray();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? Path.Combine(data, "..", "perf-results");
        var label = Args.Get(args, "--label") ?? "run";
        var repeats = Args.GetInt(args, "--repeats", 3);
        var report = new Report("navigate");
        var md = report.Markdown;
        md.AppendLine($"## Navigation ({label}) - logical CPUs {Environment.ProcessorCount}, hardware tier {ImagePerformanceGovernor.Current.Tier}");
        md.AppendLine();
        md.AppendLine("| Folder | Scenario | Median step ms | p95 step ms | Max step ms | Frames | Prepared hits | Settle ms | CPU ms per run | Peak private delta MB | Cache at end (decoded MB / compressed MB) |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        var items = new JsonArray();
        foreach (var folderName in new[] { "folder-50-24mp", "folder-500-4k", "folder-5000-small" })
        {
            var folder = Path.Combine(data, "folders", folderName);
            if (!Directory.Exists(folder)) continue;
            var paths = Directory.GetFiles(folder, "*.jpg").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            // Warm OS cache + codecs once so every scenario starts from the same state.
            foreach (var p in paths.Take(40)) { try { File.ReadAllBytes(p); } catch { } }
            var scenarios = new List<Func<Task<ScenarioResult>>>
            {
                () => SteadyAsync("next, 500 ms dwell, prefetch", paths, 20, 500, prefetch: true),
                () => SteadyAsync("next, 500 ms dwell, no prefetch", paths, 20, 500, prefetch: false),
                () => SteadyAsync("next, 1500 ms dwell, prefetch", paths, 8, 1500, prefetch: true),
                () => BurstAsync("burst 20 x 40 ms (held key)", paths, 20, 40),
                () => SteadyAsync("random jumps, 300 ms dwell, prefetch", Shuffle(paths), 15, 300, prefetch: true, folderOrder: paths),
            };
            foreach (var scenario in scenarios)
            {
                var runs = new List<ScenarioResult>();
                for (var r = 0; r < repeats; r++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    runs.Add(await scenario().ConfigureAwait(false));
                    await Task.Delay(300).ConfigureAwait(false);
                }
                var all = runs.SelectMany(x => x.Latencies).ToArray();
                var name = runs[0].Name;
                var settle = Stats.Median(runs.Select(x => x.SettleMs).Where(x => !double.IsNaN(x)).ToArray());
                var peak = runs.Max(x => x.PeakDeltaMb);
                var cache = runs[^1].Cache;
                var entry = new JsonObject
                {
                    ["folder"] = folderName,
                    ["scenario"] = name,
                    ["medianMs"] = Stats.Median(all),
                    ["p95Ms"] = Stats.Percentile(all, 0.95),
                    ["maxMs"] = all.Length > 0 ? all.Max() : double.NaN,
                    ["frames"] = runs.Sum(x => x.Frames),
                    ["preparedHits"] = runs.Sum(x => x.PreparedHits),
                    ["settleMs"] = settle,
                    ["peakDeltaMb"] = peak,
                    ["cpuMsPerRun"] = Stats.Median(runs.Select(x => x.CpuMs).ToArray()),
                    ["decodedCacheMb"] = cache.DecodedBytes / 1048576d,
                    ["compressedCacheMb"] = cache.CompressedBytes / 1048576d
                };
                items.Add(entry);
                md.AppendLine($"| {folderName} | {name} | {Stats.Ms(Stats.Median(all))} | {Stats.Ms(Stats.Percentile(all, 0.95))} | {Stats.Ms(all.Length > 0 ? all.Max() : double.NaN)} | {entry["frames"]} | {entry["preparedHits"]} | {Stats.Ms(settle)} | {Stats.Median(runs.Select(x => x.CpuMs).ToArray()):0} | {peak:0} | {cache.DecodedBytes / 1048576d:0} / {cache.CompressedBytes / 1048576d:0} |");
                Console.Error.WriteLine($"navigate {folderName} {name}: median {Stats.Median(all):0.0} p95 {Stats.Percentile(all, 0.95):0.0}");
            }
        }
        report.Root["items"] = items;
        report.Save(outDir, $"navigate-{label}");
        return 0;
    }
}
