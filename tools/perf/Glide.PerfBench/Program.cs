using Avalonia;
using Avalonia.Headless;
using Glide.PerfBench;

// Glide headless performance harness.
//   decode     --data <corpus> --out <dir> --label <name> [--runs 5] [--filter text] [--cold-dir <dir>]
//   decode-one <file> [--json <out.json>]          (cold: first decode in a fresh process)
//   navigate   --data <corpus> --out <dir> --label <name> [--repeats 3]
//   render     --data <corpus> --out <dir> --label <name> [--frames 40]
//   ui         --data <corpus> --out <dir> --label <name> [--repeats 7]
//   thumb-pipeline / thumb-read-scaling  --dir <folder> ...   (Explorer thumbnail I/O + decode pipeline, see ThumbPipelineBench.cs)
// tools\perf\run-perf.ps1 orchestrates all modes (one process per mode) and the app first-frame launches.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Glide.PerfBench <decode|decode-one|navigate|render|ui> ...");
    return 2;
}

ThreadPool.SetMinThreads(Math.Max(8, Environment.ProcessorCount), 8); // same as Glide's Program.Main
var mode = args[0].ToLowerInvariant();
if (mode is "decode" or "decode-one" or "navigate" or "direct-probe" or "thumb" or "thumb-one" or "webp-stress" or "png-probe" or "grid" or "thumb-pipeline" or "thumb-read-scaling" or "thumb-wincache" or "thumb-progressive")
{
    // Decode-only modes need Skia bitmaps but no UI thread: set Avalonia up exactly like the
    // Core.Tests headless fixture does.
    AppBuilder.Configure<BenchApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
    // Setup installs Avalonia's SynchronizationContext on this thread, but no dispatcher loop runs:
    // top-level awaits must not try to resume on it.
    SynchronizationContext.SetSynchronizationContext(null);
}

try
{
    var code = mode switch
    {
        "decode" => await DecodeBench.RunAsync(args).ConfigureAwait(false),
        "decode-one" => await DecodeBench.RunOneAsync(args[1], Args.Get(args, "--json")).ConfigureAwait(false),
        "navigate" => await NavigationBench.RunAsync(args).ConfigureAwait(false),
        "render" => RenderBench.Run(args),
        "ui" => UiBench.Run(args),
        "direct-probe" => DirectProbe.Run(args[1]),
        "thumb" => ThumbBench.Run(args),
        "grid" => ThumbBench.Grid(args),
        "thumb-pipeline" => ThumbPipelineBench.Run(args),
        "thumb-read-scaling" => ThumbPipelineBench.ReadScaling(args),
        "thumb-wincache" => WinCacheProbeBench.Run(args),
        "thumb-progressive" => ProgressiveJpegBench.Run(args),
        "png-probe" => ThumbBench.PngProbe(args[1]),
        "webp-stress" => ThumbBench.Stress(args[1], Args.GetInt(args, "--width", 2283), Args.GetInt(args, "--threads", 4), Args.GetInt(args, "--each", 25)),
        "thumb-one" => ThumbBench.RunOne(args[1], Args.GetInt(args, "--width", 236), Args.Get(args, "--json"), args.Contains("--preload")),
        _ => 2
    };
    Console.Out.Flush();
    HardExit.Now(code); // managed shutdown of the headless Avalonia/WIC state can hang; results are already flushed
    return code;
}
catch (Exception ex)
{
    // Never leave a crashed bench waiting on Windows Error Reporting.
    Console.Error.WriteLine("BENCH FAILED: " + ex);
    Console.Error.Flush();
    HardExit.Now(1);
    return 1;
}

internal static class HardExit
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    public static void Now(int code)
    {
        Console.Out.Flush();
        Console.Error.Flush();
        TerminateProcess(GetCurrentProcess(), (uint)code);
    }
}
/// <summary>Reports whether a file takes the direct (decode-into-bitmap) full-resolution path.</summary>
internal static class DirectProbe
{
    public static int Run(string path)
    {
        var type = typeof(Glide.Imaging.ImageLoadCoordinator).Assembly.GetType("Glide.Imaging.NativeImageDecoder")!;
        var direct = type.GetMethod("TryDecodeFullDirectForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var buffered = type.GetMethod("TryDecodeFullBufferedForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var (name, method) in new[] { ("direct", direct), ("buffered", buffered) })
        {
            var times = new List<double>();
            var ok = false;
            for (var i = 0; i < 7; i++)
            {
                var a = new object?[] { path, null };
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                ok = (bool)method.Invoke(null, a)!;
                times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                (a[1] as IDisposable)?.Dispose();
            }
            Console.WriteLine($"{Path.GetFileName(path)} {name}: ok={ok} median={Stats.Median(times):0.0} ms");
        }
        return 0;
    }
}