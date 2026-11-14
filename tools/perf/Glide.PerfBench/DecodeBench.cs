using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.PerfBench;

/// <summary>
/// Single-image open costs through the production ImageLoadCoordinator + AvaloniaImageDecoderBackend:
///   first frame  = LoadForegroundAsync with the factory-default policy (what reaches the viewport first),
///   settled      = first frame + the Fit-mode refinement chain MainWindow.RefinePresentedAsync runs,
///   full decode  = LoadFullForegroundAsync (what 100 % zoom / save / print need),
///   cached       = re-open hitting the prepared-frame cache.
/// Warm runs use a fresh coordinator per run (empty Glide caches, OS file cache warm, codecs loaded).
/// Cold = the first decode in a brand-new process (decode-one mode, spawned per file by the runner).
/// </summary>
internal static class DecodeBench
{
    public sealed record OpenSample(double FirstMs, double SettledMs, double FullMs, double CachedMs,
        string FirstSize, string SettledSize, string FullSize, string Route, double PeakMb, double PeakDeltaMb, string? Error);

    public static async Task<OpenSample> MeasureOnceAsync(string path, ImagePerformancePolicy policy, bool includeFull = true)
    {
        string? error = null;
        double first = double.NaN, settled = double.NaN, full = double.NaN, cached = double.NaN;
        string firstSize = "", settledSize = "", fullSize = "", route = "";
        using var memory = new MemorySampler();
        try
        {
            using (var loader = new ImageLoadCoordinator())
            {
                loader.Policy = policy;
                loader.SetViewportDemand(path, new ImageViewportDemand(BenchPolicies.ViewportWidth, BenchPolicies.ViewportHeight));
                var started = Stopwatch.GetTimestamp();
                var result = await loader.LoadForegroundAsync(path, policy).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("foreground returned null");
                first = Timing.Ms(started);
                firstSize = $"{result.Bitmap.PixelSize.Width}x{result.Bitmap.PixelSize.Height}";
                route = result.DecodeRoute;
                settledSize = firstSize;
                settled = first;
                loader.SetActivePath(path);
                if (result.IsPreview && policy.BackgroundRefinement)
                {
                    var refined = await loader.RefineForegroundAsync(result).ConfigureAwait(false);
                    if (refined is not null)
                    {
                        if (refined.IsPreview)
                        {
                            Ownership.Release(loader, refined.Bitmap);
                            var fullFrame = await loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false).ConfigureAwait(false);
                            if (fullFrame is not null)
                            {
                                settledSize = $"{fullFrame.Bitmap.PixelSize.Width}x{fullFrame.Bitmap.PixelSize.Height}";
                                Ownership.Release(loader, fullFrame.Bitmap);
                            }
                        }
                        else
                        {
                            settledSize = $"{refined.Bitmap.PixelSize.Width}x{refined.Bitmap.PixelSize.Height}";
                            loader.PromoteRefinement(result, refined);
                            Ownership.Release(loader, refined.Bitmap);
                        }
                    }
                    settled = Timing.Ms(started);
                }
                var cachedStart = Stopwatch.GetTimestamp();
                var again = await loader.LoadForegroundAsync(path, policy).ConfigureAwait(false);
                cached = Timing.Ms(cachedStart);
                if (again is not null) Ownership.Release(loader, again.Bitmap);
                Ownership.Release(loader, result.Bitmap);
                loader.SetActivePath(null);
            }

            if (includeFull)
            {
                using var loader = new ImageLoadCoordinator();
                loader.Policy = policy;
                var started = Stopwatch.GetTimestamp();
                var fullResult = await loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false).ConfigureAwait(false);
                full = Timing.Ms(started);
                if (fullResult is not null)
                {
                    fullSize = $"{fullResult.Bitmap.PixelSize.Width}x{fullResult.Bitmap.PixelSize.Height}";
                    Ownership.Release(loader, fullResult.Bitmap);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        return new OpenSample(first, settled, full, cached, firstSize, settledSize, fullSize, route, memory.PeakMb, memory.PeakDeltaMb, error);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public static async Task<int> RunOneAsync(string path, string? jsonOut)
    {
        var sample = await MeasureOnceAsync(path, BenchPolicies.AppDefault()).ConfigureAwait(false);
        var node = new JsonObject
        {
            ["file"] = Path.GetFileName(path),
            ["coldFirstMs"] = sample.FirstMs,
            ["coldSettledMs"] = sample.SettledMs,
            ["coldFullMs"] = sample.FullMs,
            ["peakMb"] = sample.PeakMb,
            ["error"] = sample.Error
        };
        if (jsonOut is not null) File.WriteAllText(jsonOut, node.ToJsonString(JsonOptions));
        Console.WriteLine(node.ToJsonString(JsonOptions));
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? Path.Combine(data, "..", "perf-results");
        var label = Args.Get(args, "--label") ?? "run";
        var runs = Args.GetInt(args, "--runs", 5);
        var filter = Args.Get(args, "--filter");
        var coldJsonDir = Args.Get(args, "--cold-dir");
        var files = Directory.GetFiles(Path.Combine(data, "single"))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Where(f => filter is null || Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => new FileInfo(f).Length).ToArray();
        var report = new Report("decode");
        var md = report.Markdown;
        md.AppendLine($"## Single-image open ({label}) - median of {runs} warm runs, logical CPUs {Environment.ProcessorCount}");
        md.AppendLine();
        md.AppendLine("| File | Cold first frame ms | Warm first frame ms | Settled ms | Full decode ms | Cached reopen ms | First / settled / full px | Route | Peak private MB (delta) |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---|---|---:|");
        var items = new JsonArray();
        var policy = BenchPolicies.AppDefault();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var samples = new List<OpenSample>();
            var pixels = ProbePixels(file);
            var effectiveRuns = pixels >= 90_000_000 ? Math.Max(3, runs / 2) : runs;
            // One untimed warm-up primes WIC/Skia codec state for this format.
            await MeasureOnceAsync(file, policy, includeFull: false).ConfigureAwait(false);
            for (var i = 0; i < effectiveRuns; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                samples.Add(await MeasureOnceAsync(file, policy).ConfigureAwait(false));
            }
            var ok = samples.Where(s => s.Error is null).ToList();
            double cold = double.NaN;
            if (coldJsonDir is not null)
            {
                var coldFile = Path.Combine(coldJsonDir, name + ".json");
                if (File.Exists(coldFile))
                {
                    var node = JsonNode.Parse(File.ReadAllText(coldFile));
                    cold = node?["coldFirstMs"] is JsonValue cv && cv.TryGetValue<double>(out var cd) ? cd : double.NaN;
                }
            }
            var errors = samples.Where(s => s.Error is not null).Select(s => s.Error).Distinct().ToArray();
            var last = ok.LastOrDefault() ?? samples.Last();
            var entry = new JsonObject
            {
                ["file"] = name,
                ["bytes"] = new FileInfo(file).Length,
                ["runs"] = samples.Count,
                ["coldFirstMs"] = cold,
                ["firstMs"] = Stats.Median(ok.Select(s => s.FirstMs).ToArray()),
                ["settledMs"] = Stats.Median(ok.Select(s => s.SettledMs).ToArray()),
                ["fullMs"] = Stats.Median(ok.Where(s => !double.IsNaN(s.FullMs)).Select(s => s.FullMs).ToArray()),
                ["cachedMs"] = Stats.Median(ok.Select(s => s.CachedMs).ToArray()),
                ["peakDeltaMb"] = ok.Count > 0 ? ok.Max(s => s.PeakDeltaMb) : double.NaN,
                ["firstSize"] = last.FirstSize,
                ["settledSize"] = last.SettledSize,
                ["fullSize"] = last.FullSize,
                ["route"] = last.Route,
                ["errors"] = errors.Length == 0 ? null : string.Join(" | ", errors)
            };
            items.Add(entry);
            md.AppendLine($"| {name} | {Stats.Ms(cold)} | {Stats.Ms((double)entry["firstMs"]!)} | {Stats.Ms((double)entry["settledMs"]!)} | {Stats.Ms((double)entry["fullMs"]!)} | {Stats.Ms((double)entry["cachedMs"]!)} | {last.FirstSize} / {last.SettledSize} / {(last.FullSize.Length == 0 ? "-" : last.FullSize)} | {last.Route} | {(ok.Count > 0 ? ok.Max(s => s.PeakDeltaMb).ToString("0") : "n/a")}{(errors.Length > 0 ? " ERR: " + string.Join(";", errors) : "")} |");
            Console.Error.WriteLine($"decode {name}: first {Stats.Median(ok.Select(s => s.FirstMs).ToArray()):0.0} full {Stats.Median(ok.Where(s => !double.IsNaN(s.FullMs)).Select(s => s.FullMs).ToArray()):0.0} errors {errors.Length}");
        }
        report.Root["items"] = items;
        report.Save(outDir, $"decode-{label}");
        return 0;
    }

    private static long ProbePixels(string file)
    {
        try { return ImageHeaderProbe.TryProbe(file, out var d) && d.IsValid ? (long)d.Width * d.Height : 0; }
        catch { return 0; }
    }
}
