using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glide.Imaging;
using Glide.PerfBench;

/// <summary>
/// The viewer's open cost through the same loader the app builds (MainWindow: <c>new ImageLoadCoordinator(new CodecProviderDecoderBackend(lazyResolver))</c>),
/// so the bundled codec plugins (AVIF, HEIC, JPEG XL, JPEG 2000) are on the route; PerfBench's decode mode uses the plain Skia/WIC backend and cannot open them.
/// Same measurements as PerfBench decode: first frame, settled (first frame + the Fit refinement), full decode.
/// </summary>
internal static class ViewerOpen
{
    public sealed record Sample(double FirstMs, double SettledMs, double FullMs, string FirstSize, string FullSize, string Route, double PeakDeltaMb, string? Error);

    private static ImageLoadCoordinator NewLoader(ImagePerformancePolicy policy)
    {
        var backend = new CodecProviderDecoderBackend(lazyResolver: path => CodecProviderRuntime.Shared.ResolveProvider(path));
        return new ImageLoadCoordinator(backend) { Policy = policy };
    }

    public static async Task<Sample> MeasureAsync(string path, bool includeFull = true)
    {
        var policy = BenchPolicies.AppDefault();
        double first = double.NaN, settled = double.NaN, full = double.NaN;
        string firstSize = "", fullSize = "", route = "";
        string? error = null;
        using var memory = new MemorySampler();
        try
        {
            using (var loader = NewLoader(policy))
            {
                loader.SetViewportDemand(path, new ImageViewportDemand(BenchPolicies.ViewportWidth, BenchPolicies.ViewportHeight));
                var started = Stopwatch.GetTimestamp();
                var result = await loader.LoadForegroundAsync(path, policy).ConfigureAwait(false) ?? throw new InvalidOperationException("foreground returned null");
                first = Timing.Ms(started);
                firstSize = $"{result.Bitmap.PixelSize.Width}x{result.Bitmap.PixelSize.Height}";
                route = result.DecodeRoute;
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
                            if (fullFrame is not null) Ownership.Release(loader, fullFrame.Bitmap);
                        }
                        else { loader.PromoteRefinement(result, refined); Ownership.Release(loader, refined.Bitmap); }
                    }
                    settled = Timing.Ms(started);
                }
                Ownership.Release(loader, result.Bitmap);
                loader.SetActivePath(null);
            }
            if (includeFull)
            {
                using var loader = NewLoader(policy);
                var started = Stopwatch.GetTimestamp();
                var fullResult = await loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false).ConfigureAwait(false);
                full = Timing.Ms(started);
                if (fullResult is not null)
                {
                    fullSize = $"{fullResult.Bitmap.PixelSize.Width}x{fullResult.Bitmap.PixelSize.Height}";
                    Ownership.Release(loader, fullResult.Bitmap);
                }
                else error = "full decode returned null";
            }
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }
        return new Sample(first, settled, full, firstSize, fullSize, route, memory.PeakDeltaMb, error);
    }

    private static double? F(double v) => double.IsFinite(v) ? v : null;

    /// <summary>
    /// 5.2.0 --preload: what the app has done by the time the user opens the file after browsing its folder: the codec plug-in
    /// warmed by the folder listing (<see cref="CodecPreloader"/>) and Svg.Skia / Direct2D initialised by the idle warm-up.
    /// Not timed. A harness for a build without these classes simply does not pass the flag.
    /// </summary>
    private static void Preload(string path)
    {
        if (CodecPreloader.NeedsProvider(path))
        {
            CodecPreloader.NotifyListing(new[] { path });
            var clock = Stopwatch.StartNew();
            while (CodecPreloader.ResolvedCount < 1 && clock.ElapsedMilliseconds < 10_000) Thread.Sleep(5);
        }
        if (SvgDecoder.IsSvgPath(path)) SvgTileRenderer.WarmUp();
    }

    public static async Task<int> RunOneAsync(string path, string? jsonOut, bool preload = false)
    {
        if (preload) Preload(path);
        var s = await MeasureAsync(path).ConfigureAwait(false);
        var node = new JsonObject
        {
            ["file"] = Path.GetFileName(path), ["coldFirstMs"] = F(s.FirstMs), ["coldSettledMs"] = F(s.SettledMs), ["coldFullMs"] = F(s.FullMs),
            ["route"] = s.Route, ["peakDeltaMb"] = s.PeakDeltaMb, ["error"] = s.Error
        };
        if (jsonOut is not null) File.WriteAllText(jsonOut, node.ToJsonString());
        Console.WriteLine(node.ToJsonString());
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? ".";
        var label = Args.Get(args, "--label") ?? "run";
        var runs = Args.GetInt(args, "--runs", 7);
        var items = new JsonArray();
        foreach (var file in FileSet.List(data, Args.Get(args, "--filter")))
        {
            await MeasureAsync(file, includeFull: false).ConfigureAwait(false); // untimed warm-up (codec state for the format)
            var samples = new List<Sample>();
            for (var i = 0; i < runs; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                samples.Add(await MeasureAsync(file).ConfigureAwait(false));
            }
            var ok = samples.Where(s => s.Error is null).ToList();
            var last = ok.LastOrDefault() ?? samples.Last();
            double Med(Func<Sample, double> f) => Stats.Median(ok.Select(f).Where(double.IsFinite).ToArray());
            items.Add(new JsonObject
            {
                ["file"] = Path.GetFileName(file), ["firstMs"] = F(Med(s => s.FirstMs)), ["settledMs"] = F(Med(s => s.SettledMs)), ["fullMs"] = F(Med(s => s.FullMs)),
                ["firstMin"] = ok.Count > 0 ? ok.Min(s => s.FirstMs) : null, ["firstMax"] = ok.Count > 0 ? ok.Max(s => s.FirstMs) : null,
                ["fullMin"] = ok.Count > 0 ? ok.Min(s => s.FullMs) : null, ["fullMax"] = ok.Count > 0 ? ok.Max(s => s.FullMs) : null,
                ["firstSize"] = last.FirstSize, ["fullSize"] = last.FullSize, ["route"] = last.Route,
                ["peakDeltaMb"] = ok.Count > 0 ? ok.Max(s => s.PeakDeltaMb) : null,
                ["error"] = samples.FirstOrDefault(s => s.Error is not null)?.Error
            });
            Console.Error.WriteLine($"view {Path.GetFileName(file)}: first {Stats.Ms(Med(s => s.FirstMs))} full {Stats.Ms(Med(s => s.FullMs))} {last.Route} {samples.FirstOrDefault(s => s.Error is not null)?.Error}");
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"view-{label}.json"), new JsonObject { ["items"] = items }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
