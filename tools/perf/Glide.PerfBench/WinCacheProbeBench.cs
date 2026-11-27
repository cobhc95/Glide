using System.Diagnostics;
using Glide.App.Services;
using Glide.Imaging;

namespace Glide.PerfBench;

/// <summary>
/// thumb-wincache --dir &lt;folder&gt; [--files 5] [--width 236]: whether Windows' thumbnail cache answers for the folder's files:
/// the cache-only probe cold, one warming call to the shell (which makes Windows store the thumbnail), the probe again.
/// Only for the benchmark's own synthetic folder (the warming call writes into the user's Windows thumbnail cache).
/// </summary>
internal static class WinCacheProbeBench
{
    public static int Run(string[] args)
    {
        var dir = Args.Get(args, "--dir") ?? throw new ArgumentException("--dir required");
        var count = Args.GetInt(args, "--files", 5);
        var width = Args.GetInt(args, "--width", 236);
        foreach (var file in Directory.GetFiles(Path.GetFullPath(dir), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(count))
        {
            var t0 = Stopwatch.GetTimestamp();
            var cold = ThumbnailPipeline.ProbeWindowsCache(file, width);
            var coldMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            t0 = Stopwatch.GetTimestamp();
            var warmOk = NativeImageDecoder.TryDecodeShellPreview(file, width, out var warm);
            var warmMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var warmSize = warmOk ? warm.PixelSize.ToString() : "-";
            Thread.Sleep(300);
            t0 = Stopwatch.GetTimestamp();
            var after = ThumbnailPipeline.ProbeWindowsCache(file, width);
            var afterMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Console.WriteLine($"cold probe: {(cold is null ? "miss" : "HIT")} {coldMs:F1} ms | warm call: {(warmOk ? "ok" : "failed")} {warmSize} {warmMs:F1} ms | probe after: {(after is null ? "miss" : "HIT " + after.PixelSize)} {afterMs:F1} ms");
            cold?.Dispose();
            if (warmOk) warm.Dispose();
            after?.Dispose();
        }
        return 0;
    }
}
