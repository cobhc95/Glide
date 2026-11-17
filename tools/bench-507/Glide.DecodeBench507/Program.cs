using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Glide.DecodeBench507;

// Glide 5.0.7 decode/open-speed benchmark. Plan: docs\perf\benchmark-plan-5.0.7.md.
//   probe [--engines a,b]                                     load every engine, print library/version/errors (no decoding)
//   plan  --corpus <spec>[;<spec>] [options]                  case matrix + estimated run time (no decoding)
//   run   --corpus <spec>[;<spec>] --confirm [options]        MEASURE (serial; refuses to start without --confirm)
//   one   --engine e --file f --mode preview|full [--json o]  one decode in a fresh process (cold sample; spawned by run)
// corpus spec: synthetic:<dir> | real:<dir> | <dir>   (each needs manifest.csv or is scanned)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0) { Console.Error.WriteLine("usage: Glide.DecodeBench507 probe|plan|run|one ... (see Program.cs header)"); return 2; }
ThreadPool.SetMinThreads(Math.Max(8, Environment.ProcessorCount), 8); // same as Glide's Program.Main
var cmd = args[0].ToLowerInvariant();
var cfg = EngineConfig.Load(Args.Get(args, "--engines-config"));
try
{
    var code = cmd switch
    {
        "probe" => Bench.Probe(args, cfg),
        "plan" => Bench.Plan(args, cfg, run: false),
        "run" => Bench.Plan(args, cfg, run: true),
        "one" => Bench.One(args, cfg),
        _ => 2
    };
    Console.Out.Flush();
    HardExit.Now(code); // headless Avalonia/WIC/vips shutdown can hang; results are already flushed
    return code;
}
catch (Exception ex)
{
    Console.Error.WriteLine("BENCH FAILED: " + ex);
    HardExit.Now(1);
    return 1;
}

internal static class HardExit
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr p, uint code);
    public static void Now(int code) { Console.Out.Flush(); Console.Error.Flush(); TerminateProcess(GetCurrentProcess(), (uint)code); }
}

internal sealed record Case(CorpusFile File, string Engine, Mode Mode);

internal sealed class Sample
{
    public double Ms, ConvertMs, PeakDeltaMb, Busy;
    public Outcome Outcome = new();
}

internal static class Bench
{
    // Rough single-image costs on a 16-thread desktop (ms per megapixel, full decode) used ONLY for the
    // run-time estimate. Seeded from the 5.0.0 perf table; refined after the quick tier has run.
    private static readonly Dictionary<string, double> FullMsPerMp = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = 4.0, ["jpeg"] = 4.0, ["png"] = 5.5, ["webp"] = 4.5, ["tif"] = 7.5, ["tiff"] = 7.5, ["bmp"] = 1.0, ["gif"] = 6.0,
        ["avif"] = 9.0, ["heic"] = 12.0, ["heif"] = 12.0, ["jxl"] = 6.0, ["jp2"] = 25.0, ["j2k"] = 25.0, ["jpf"] = 25.0,
        ["qoi"] = 3.0, ["tga"] = 2.0, ["exr"] = 15.0, ["hdr"] = 10.0, ["psd"] = 6.0
    };

    public static int Probe(string[] a, EngineConfig cfg)
    {
        var threads = Args.Int(a, "--threads", Environment.ProcessorCount);
        foreach (var name in Args.List(a, "--engines", string.Join(',', EngineFactory.All.Concat(cfg.Cli.Select(c => c.Name)))))
        {
            using var e = EngineFactory.Create(name, cfg, threads);
            Console.WriteLine($"{e.Name,-16} {(e.LoadError is null ? "OK  " : "FAIL")} {e.Library}{(e.LoadError is null ? "" : "  -> " + e.LoadError)}");
        }
        return 0;
    }

    private static (int W, int H) Box(string[] a)
    {
        var b = (Args.Get(a, "--box") ?? "2560x1369").Split('x');
        return (int.Parse(b[0]), int.Parse(b[1]));
    }

    public static int Plan(string[] a, EngineConfig cfg, bool run)
    {
        var corpora = (Args.Get(a, "--corpus") ?? throw new ArgumentException("--corpus required")).Split(';', StringSplitOptions.RemoveEmptyEntries);
        var engineNames = Args.List(a, "--engines", "glide,wic,skia,turbojpeg,turbojpeg-fast,turbojpeg-bands,vips,libraw");
        var modes = Args.List(a, "--modes", "preview,full").Select(m => m.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? Mode.Preview : Mode.Full).Distinct().ToArray();
        var warmup = Args.Int(a, "--warmup", 1);
        var reps = Args.Int(a, "--reps", 7);
        var repsLarge = Args.Int(a, "--reps-large", 5);
        var coldReps = Args.Int(a, "--cold-reps", 0);
        var coldMaxMp = Args.Dbl(a, "--cold-max-mp", 30);
        var threads = Args.Int(a, "--threads", Environment.ProcessorCount);
        var (boxW, boxH) = Box(a);
        var idleMode = (Args.Get(a, "--idle-check") ?? "case").ToLowerInvariant();
        var idleThreshold = Args.Dbl(a, "--idle-threshold", 15);
        var idleWindow = Args.Int(a, "--idle-window", 750);
        var idleTimeout = Args.Int(a, "--idle-timeout", 60_000);
        // e.g. --idle-block dotnet,MSBuild,VBCSCompiler,ISCC,testhost: pause (up to --idle-timeout) while any runs.
        IdleGate.BlockingProcesses = Args.List(a, "--idle-block", "");
        var files = corpora.SelectMany(c => Corpus.Load(c, Args.Int(a, "--max-per-format", 0), Args.Get(a, "--format"), Args.Get(a, "--variant"))).ToList();
        if (Args.Get(a, "--mp") is { } mpList) // e.g. "24" or "12,24": keep files whose megapixels round to one of these (synthetic sizes)
        {
            var wanted = mpList.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            files = files.Where(f => !double.IsNaN(f.Megapixels) && wanted.Any(w => Math.Abs(f.Megapixels - w) <= w * 0.1)).ToList();
        }

        var engines = engineNames.Select(n => EngineFactory.Create(n, cfg, threads)).ToList();
        var cases = new List<Case>();
        foreach (var f in files)
            foreach (var e in engines.Where(e => e.LoadError is null && e.Supports(f.Ext)))
                foreach (var m in modes)
                    cases.Add(new Case(f, e.Name, m));

        // ---- estimate ----
        double est = 0;
        foreach (var c in cases)
        {
            var mp = double.IsNaN(c.File.Megapixels) ? 12 : c.File.Megapixels;
            var perMp = FullMsPerMp.GetValueOrDefault(c.File.Format, Ext.Raw.Contains(c.File.Ext) ? 1.5 : 6.0);
            var ms = mp * perMp * (c.Mode == Mode.Preview && Ext.Jpeg.Contains(c.File.Ext) ? 0.35 : 1.0) + 5;
            var n = warmup + (mp >= 90 ? repsLarge : reps);
            est += n * (ms + 25 /* GC + sampler */) + (idleMode == "case" ? idleWindow + 150 : idleMode == "rep" ? n * (idleWindow + 150) : 0);
            if (coldReps > 0 && mp <= coldMaxMp) est += coldReps * (ms + 450 /* process start + runtime/codec init */);
        }
        Console.WriteLine($"files {files.Count}, engines {string.Join(',', engines.Select(e => e.Name + (e.LoadError is null ? "" : "(unavailable)")))}");
        Console.WriteLine($"cases {cases.Count} (file x engine x mode), warm-up {warmup}, reps {reps} ({repsLarge} at >= 90 MP), cold reps {coldReps} (<= {coldMaxMp} MP)");
        Console.WriteLine($"estimated measuring time: {TimeSpan.FromMilliseconds(est):hh\\:mm\\:ss} (idle waits not included; machine busy => longer)");
        foreach (var g in cases.GroupBy(c => c.File.Format).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-6} files {g.Select(c => c.File.Path).Distinct().Count(),3}  cases {g.Count(),4}  engines {string.Join(',', g.Select(c => c.Engine).Distinct())}");
        foreach (var e in engines.Where(e => e.LoadError is not null)) Console.WriteLine($"  unavailable: {e.Name}: {e.LoadError}");
        if (!run) { engines.ForEach(e => e.Dispose()); return 0; }

        if (!Args.Has(a, "--confirm"))
        {
            Console.Error.WriteLine("Refusing to measure without --confirm (the user must confirm first: this uses the CPU heavily).");
            return 3;
        }

        // ---- run ----
        var outDir = Path.GetFullPath(Args.Get(a, "--out") ?? Path.Combine(Environment.CurrentDirectory, "bench-results"));
        var label = Args.Get(a, "--label") ?? DateTime.Now.ToString("yyyyMMdd-HHmm");
        var resultDir = Path.Combine(outDir, label);
        Directory.CreateDirectory(resultDir);
        var tuning = ProcessTuning.Apply(Args.Get(a, "--priority") ?? "high", Args.Get(a, "--affinity"));
        var machine = MachineInfo.Describe();
        var settings = $"box {boxW}x{boxH}; threads {threads}; warm-up {warmup}; reps {reps}/{repsLarge}; cold reps {coldReps}; idle {idleMode} <= {idleThreshold}% over {idleWindow} ms (timeout {idleTimeout} ms); {tuning}";
        File.WriteAllText(Path.Combine(resultDir, "run-info.txt"), machine + Environment.NewLine + settings + Environment.NewLine +
            string.Join(Environment.NewLine, engines.Select(e => $"{e.Name}: {e.Library}{(e.LoadError is null ? "" : " UNAVAILABLE " + e.LoadError)}")) + Environment.NewLine);
        var rawPath = Path.Combine(resultDir, "raw.csv");
        using var raw = new StreamWriter(rawPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        raw.WriteLine(Csv.Line("set", "file", "format", "variant", "mp", "bytes", "engine", "mode", "phase", "rep", "ms", "convert_ms", "peak_delta_mb", "out_w", "out_h", "route", "busy_pct", "checksum", "error"));

        var byName = engines.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var results = new List<(Case Case, List<Sample> Warm, List<double> Cold, string? Error)>();
        var seed = Args.Int(a, "--shuffle", 0);
        var rng = seed != 0 ? new Random(seed) : null;
        var sw = Stopwatch.StartNew();
        var index = 0;
        // Interleave engines per file (and optionally shuffle their order) so slow drift (thermal, background
        // activity) spreads over all engines instead of biasing whichever ran last.
        foreach (var fileGroup in cases.GroupBy(c => c.File))
        {
            var group = fileGroup.ToList();
            if (rng is not null) group = group.OrderBy(_ => rng.Next()).ToList();
            foreach (var c in group)
            {
                index++;
                var engine = byName[c.Engine];
                var mp = double.IsNaN(c.File.Megapixels) ? 0 : c.File.Megapixels;
                var n = mp >= 90 ? repsLarge : reps;
                var req = new Request(c.File.Path, c.Mode, boxW, boxH, threads);
                var (busy, waited, timedOut) = idleMode == "off" ? (double.NaN, 0d, false) : IdleGate.WaitForIdle(idleThreshold, idleWindow, idleTimeout);
                Console.Error.Write($"[{index}/{cases.Count} {sw.Elapsed:hh\\:mm\\:ss}] {c.Engine} {c.Mode} {Path.GetFileName(c.File.Path)} busy {busy:0}%{(timedOut ? " (NOISY)" : "")} ");

                string? error = null;
                for (var w = 0; w < warmup && error is null; w++)
                {
                    var s = Measure(engine, req, busy);
                    if (!s.Outcome.Ok) error = s.Outcome.Error ?? "failed";
                }
                var warm = new List<Sample>();
                for (var i = 0; i < n && error is null; i++)
                {
                    if (idleMode == "rep" && i > 0) busy = IdleGate.WaitForIdle(idleThreshold, idleWindow, idleTimeout).Busy;
                    var s = Measure(engine, req, busy);
                    warm.Add(s);
                    raw.WriteLine(Row(c, "warm", i + 1, s));
                    if (!s.Outcome.Ok) error = s.Outcome.Error;
                }
                if (error is not null && warm.Count == 0)
                    raw.WriteLine(Row(c, "warm", 0, new Sample { Ms = double.NaN, Busy = busy, Outcome = Outcome.Fail(error) }));

                var cold = new List<double>();
                if (error is null && coldReps > 0 && mp <= coldMaxMp)
                {
                    for (var i = 0; i < coldReps; i++)
                    {
                        var cs = RunCold(c, boxW, boxH, threads, Args.Get(a, "--engines-config"));
                        raw.WriteLine(Row(c, "cold", i + 1, cs));
                        if (cs.Outcome.Ok) cold.Add(cs.Ms);
                    }
                }
                results.Add((c, warm, cold, error));
                var ok = warm.Where(s => s.Outcome.Ok).Select(s => s.Ms).ToList();
                Console.Error.WriteLine(error is null ? $"median {Stats.F(Stats.Median(ok))} ms" : "ERROR " + error);
            }
        }

        WriteSummary(resultDir, label, machine, settings, engines, results, Args.Get(a, "--baseline") ?? "glide");
        Console.WriteLine("results: " + resultDir);
        engines.ForEach(e => e.Dispose());
        return 0;
    }

    private static Sample Measure(IEngine engine, Request req, double busy)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var state = engine.Prepare(req);
        var sample = new Sample { Busy = busy };
        try
        {
            using var mem = new MemorySampler();
            var t = Stopwatch.GetTimestamp();
            Outcome o;
            try { o = engine.Run(req, state); }
            catch (Exception ex) { o = Outcome.Fail(ex.GetType().Name + ": " + ex.Message); }
            sample.Ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            sample.Outcome = o;
            sample.ConvertMs = o.ConvertMs;
            mem.Dispose();
            sample.PeakDeltaMb = mem.PeakDeltaMb;
        }
        finally { try { engine.Cleanup(state); } catch { } }
        return sample;
    }

    private static string Row(Case c, string phase, int rep, Sample s) => Csv.Line(c.File.Set, c.File.Path, c.File.Format, c.File.Variant,
        c.File.Megapixels, c.File.Bytes, c.Engine, c.Mode.ToString().ToLowerInvariant(), phase, rep, s.Ms, s.ConvertMs, s.PeakDeltaMb,
        s.Outcome.W, s.Outcome.H, s.Outcome.Route, s.Busy, s.Outcome.Checksum.ToString("X16"), s.Outcome.Error);

    // ---- cold: one decode in a brand-new process (process-cold, OS-file-cache warm) ----
    private static Sample RunCold(Case c, int boxW, int boxH, int threads, string? enginesConfig)
    {
        var json = Path.Combine(Path.GetTempPath(), "glide-bench-cold-" + Guid.NewGuid().ToString("N") + ".json");
        var exe = Environment.ProcessPath!;
        var argList = new List<string> { "one", "--engine", c.Engine, "--file", c.File.Path, "--mode", c.Mode.ToString().ToLowerInvariant(), "--box", $"{boxW}x{boxH}", "--threads", threads.ToString(), "--json", json };
        if (enginesConfig is not null) { argList.Add("--engines-config"); argList.Add(enginesConfig); }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in argList) psi.ArgumentList.Add(x);
        var wall = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
        _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } return new Sample { Ms = double.NaN, Outcome = Outcome.Fail("cold timeout") }; }
        wall.Stop();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var r = doc.RootElement;
            var o = new Outcome { Ok = r.GetProperty("ok").GetBoolean(), W = r.GetProperty("w").GetInt32(), H = r.GetProperty("h").GetInt32(), Route = r.GetProperty("route").GetString() + $"|wall={wall.Elapsed.TotalMilliseconds:0}", Error = r.GetProperty("error").GetString() };
            return new Sample { Ms = r.GetProperty("ms").GetDouble(), PeakDeltaMb = r.GetProperty("peakWsMb").GetDouble(), Outcome = o, Busy = double.NaN };
        }
        catch (Exception ex) { return new Sample { Ms = double.NaN, Outcome = Outcome.Fail("cold result unreadable: " + ex.Message) }; }
        finally { try { File.Delete(json); } catch { } }
    }

    public static int One(string[] a, EngineConfig cfg)
    {
        ProcessTuning.Apply("high", Args.Get(a, "--affinity"));
        var (boxW, boxH) = Box(a);
        var threads = Args.Int(a, "--threads", Environment.ProcessorCount);
        var mode = (Args.Get(a, "--mode") ?? "preview").StartsWith("p", StringComparison.OrdinalIgnoreCase) ? Mode.Preview : Mode.Full;
        var file = Args.Get(a, "--file") ?? throw new ArgumentException("--file required");
        // Engine construction (DLL load, runtime init) is outside the timer; codec first-use init is inside, as in a
        // freshly launched Glide that has already shown its window.
        using var engine = EngineFactory.Create(Args.Get(a, "--engine") ?? "glide", cfg, threads);
        var req = new Request(file, mode, boxW, boxH, threads);
        var state = engine.Prepare(req);
        var t = Stopwatch.GetTimestamp();
        Outcome o;
        try { o = engine.Run(req, state); } catch (Exception ex) { o = Outcome.Fail(ex.GetType().Name + ": " + ex.Message); }
        var ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ms"] = ms, ["ok"] = o.Ok, ["w"] = o.W, ["h"] = o.H, ["route"] = o.Route, ["checksum"] = o.Checksum.ToString("X16"), ["error"] = o.Error, ["peakWsMb"] = MemorySampler.PeakWorkingSetMb()
        });
        if (Args.Get(a, "--json") is { } path) File.WriteAllText(path, json);
        Console.WriteLine(json);
        return o.Ok ? 0 : 1;
    }

    // ---- summary ----
    private static void WriteSummary(string dir, string label, string machine, string settings, List<IEngine> engines,
        List<(Case Case, List<Sample> Warm, List<double> Cold, string? Error)> results, string baseline)
    {
        var sum = new StringBuilder();
        sum.AppendLine(Csv.Line("set", "file", "format", "variant", "mp", "bytes", "engine", "mode", "n", "median_ms", "p95_ms", "min_ms", "max_ms", "stdev_ms", "cv_pct",
            "convert_median_ms", "peak_delta_mb_max", "cold_median_ms", "cold_n", "busy_max_pct", "out_w", "out_h", "route", "error"));
        var rows = new Dictionary<(string File, string Engine, Mode Mode), (double Median, double P95, double Cold, string Route)>();
        foreach (var (c, warm, cold, error) in results)
        {
            var ok = warm.Where(s => s.Outcome.Ok).ToList();
            var ms = ok.Select(s => s.Ms).ToList();
            var med = Stats.Median(ms);
            var p95 = Stats.Percentile(ms, 0.95);
            var sd = Stats.StdDev(ms);
            var last = ok.LastOrDefault()?.Outcome ?? new Outcome();
            var coldMed = Stats.Median(cold);
            rows[(c.File.Path, c.Engine, c.Mode)] = (med, p95, coldMed, last.Route);
            sum.AppendLine(Csv.Line(c.File.Set, c.File.Path, c.File.Format, c.File.Variant, c.File.Megapixels, c.File.Bytes, c.Engine, c.Mode.ToString().ToLowerInvariant(),
                ms.Count, med, p95, ms.Count > 0 ? ms.Min() : double.NaN, ms.Count > 0 ? ms.Max() : double.NaN, sd, ms.Count > 0 && med > 0 ? 100 * sd / med : double.NaN,
                Stats.Median(ok.Select(s => s.ConvertMs).ToList()), ok.Count > 0 ? ok.Max(s => s.PeakDeltaMb) : double.NaN, coldMed, cold.Count,
                ok.Count > 0 ? ok.Max(s => s.Busy) : double.NaN, last.W, last.H, last.Route, error));
        }
        File.WriteAllText(Path.Combine(dir, "summary.csv"), sum.ToString(), new UTF8Encoding(false));

        var md = new StringBuilder();
        md.AppendLine($"# Decode benchmark {label}");
        md.AppendLine();
        md.AppendLine("- " + machine);
        md.AppendLine("- " + settings);
        md.AppendLine("- Cells: warm median ms (p95). Ratio row/column `x` = engine time / " + baseline + " time (< 1 is faster). Cold = first decode in a fresh process.");
        md.AppendLine();
        md.AppendLine("| Engine | Library |");
        md.AppendLine("|---|---|");
        foreach (var e in engines) md.AppendLine($"| {e.Name} | {e.Library}{(e.LoadError is null ? "" : " **unavailable:** " + e.LoadError)} |");
        var engineNames = results.Select(r => r.Case.Engine).Distinct().ToList();
        foreach (var set in results.Select(r => r.Case.File.Set).Distinct())
        foreach (var mode in new[] { Mode.Preview, Mode.Full })
        {
            var inSet = results.Where(r => r.Case.File.Set == set && r.Case.Mode == mode).ToList();
            if (inSet.Count == 0) continue;
            var names = engineNames.Where(n => inSet.Any(r => r.Case.Engine == n)).ToList();
            md.AppendLine();
            md.AppendLine($"## {set}: {(mode == Mode.Preview ? "time to first pixel (viewport-bounded first frame)" : "full-resolution decode")}");
            md.AppendLine();
            md.AppendLine("| File | MP | " + string.Join(" | ", names) + " |");
            md.AppendLine("|---|---:|" + string.Concat(names.Select(_ => "---:|")));
            foreach (var f in inSet.Select(r => r.Case.File).Distinct().OrderBy(f => f.Format).ThenBy(f => f.Megapixels))
            {
                rows.TryGetValue((f.Path, baseline, mode), out var b);
                var cells = names.Select(n =>
                {
                    var r = inSet.FirstOrDefault(x => x.Case.File == f && x.Case.Engine == n);
                    if (r.Case is null) return "";
                    if (r.Error is not null) return "err";
                    var v = rows[(f.Path, n, mode)];
                    var ratio = n != baseline && b.Median > 0 ? $" {v.Median / b.Median:0.00}x" : "";
                    return $"{Stats.F(v.Median)} ({Stats.F(v.P95)}){ratio}";
                });
                md.AppendLine($"| {f.Format} {f.Variant} {Path.GetFileName(f.Path)} | {f.Megapixels:0.#} | {string.Join(" | ", cells)} |");
            }
            // Geometric-mean ratio per format: the number the decision report quotes.
            md.AppendLine();
            md.AppendLine($"Geometric mean of time ratio vs {baseline} per format ({set}, {mode.ToString().ToLowerInvariant()}):");
            md.AppendLine();
            md.AppendLine("| Format | " + string.Join(" | ", names) + " |");
            md.AppendLine("|---|" + string.Concat(names.Select(_ => "---:|")));
            foreach (var fmt in inSet.Select(r => r.Case.File.Format).Distinct().OrderBy(x => x))
            {
                var cells = names.Select(n =>
                {
                    var ratios = inSet.Where(r => r.Case.File.Format == fmt && r.Case.Engine == n && r.Error is null)
                        .Select(r => (v: rows[(r.Case.File.Path, n, mode)].Median, b: rows.TryGetValue((r.Case.File.Path, baseline, mode), out var bb) ? bb.Median : double.NaN))
                        .Where(x => x.v > 0 && x.b > 0).Select(x => Math.Log(x.v / x.b)).ToList();
                    return ratios.Count == 0 ? "" : $"{Math.Exp(ratios.Average()):0.00}x (n={ratios.Count})";
                });
                md.AppendLine($"| {fmt} | {string.Join(" | ", cells)} |");
            }
        }
        md.AppendLine();
        md.AppendLine("Errors:");
        foreach (var r in results.Where(r => r.Error is not null)) md.AppendLine($"- {r.Case.Engine} {r.Case.Mode} {Path.GetFileName(r.Case.File.Path)}: {r.Error}");
        File.WriteAllText(Path.Combine(dir, "summary.md"), md.ToString(), new UTF8Encoding(false));
    }
}
