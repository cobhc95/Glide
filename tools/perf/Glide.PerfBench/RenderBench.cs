using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.Imaging;

namespace Glide.PerfBench;

/// <summary>
/// Zoom / pan frame cost of the real ImageViewport on the headless CPU Skia renderer (1920x1080 window).
/// Each frame = view change + InvalidateVisual + forced render + framebuffer capture (~1 ms of the cost).
/// GPU (ANGLE/D3D) texture upload and DWM present are NOT represented; numbers are relative.
/// </summary>
internal static class RenderBench
{
    public static int Run(string[] args)
    {
        var data = Args.Get(args, "--data") ?? throw new ArgumentException("--data required");
        var outDir = Args.Get(args, "--out") ?? Path.Combine(data, "..", "perf-results");
        var label = Args.Get(args, "--label") ?? "run";
        var frames = Args.GetInt(args, "--frames", 40);
        var session = HeadlessUnitTestSession.StartNew(typeof(BenchApp));
        var report = new Report("render");
        var md = report.Markdown;
        md.AppendLine($"## Zoom/pan frame cost ({label}) - headless CPU Skia, 1920x1080 viewport, {frames} frames");
        md.AppendLine();
        md.AppendLine("| Image (bitmap kind) | Scenario | Median frame ms | p95 frame ms |");
        md.AppendLine("|---|---|---:|---:|");
        var items = new JsonArray();
        session.Dispatch<object?>(async () =>
        {
            foreach (var file in new[] { "jpeg-base-24mp.jpg", "jpeg-base-100mp.jpg" })
            {
                var path = Path.Combine(data, "single", file);
                if (!File.Exists(path)) continue;
                using var loader = new ImageLoadCoordinator();
                var full = loader.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("decode failed");
                foreach (var immutable in new[] { false, true })
                {
                var bitmap = immutable ? ToImmutable(full.Bitmap) : full.Bitmap;
                var kind = bitmap is WriteableBitmap ? "WriteableBitmap" : "immutable Bitmap";
                var viewport = new ImageViewport { Width = 1920, Height = 1080 };
                var window = new Window { Width = 1920, Height = 1080, Content = viewport };
                window.Show();
                viewport.SwapPresentedFrame(bitmap, bitmap.PixelSize.Width, bitmap.PixelSize.Height, 1, path);
                foreach (var (scenario, quality, setup, step) in Scenarios(viewport))
                {
                    viewport.SetImageQuality(quality);
                    setup();
                    Render(window);
                    var times = new List<double>();
                    for (var i = 0; i < frames; i++)
                    {
                        step(i);
                        var t = Stopwatch.GetTimestamp();
                        Render(window);
                        times.Add(Timing.Ms(t));
                    }
                    var median = Stats.Median(times);
                    var p95 = Stats.Percentile(times, 0.95);
                    items.Add(new JsonObject { ["file"] = file, ["kind"] = kind, ["scenario"] = scenario, ["medianMs"] = median, ["p95Ms"] = p95 });
                    md.AppendLine($"| {file} ({kind}) | {scenario} | {Stats.Ms(median)} | {Stats.Ms(p95)} |");
                }
                window.Close();
                Dispatcher.UIThread.RunJobs();
                if (immutable) bitmap.Dispose();
                }
                full.Bitmap.Dispose();
            }
            await Task.CompletedTask;
            return null;
        }, CancellationToken.None).GetAwaiter().GetResult();
        report.Root["items"] = items;
        report.Save(outDir, $"render-{label}");
        return 0;
    }

    private static unsafe Bitmap ToImmutable(Bitmap source)
    {
        if (source is not WriteableBitmap writeable) return source;
        using var locked = writeable.Lock();
        return new Bitmap(locked.Format, Avalonia.Platform.AlphaFormat.Premul, locked.Address, locked.Size, new Vector(96, 96), locked.RowBytes);
    }

    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
    }

    private static IEnumerable<(string Name, string Quality, Action Setup, Action<int> Step)> Scenarios(ImageViewport viewport)
    {
        yield return ("fit, zoom wobble (Maximum quality filter)", "Maximum quality", viewport.Fit, i => viewport.ZoomBy(i % 2 == 0 ? 1.02 : 1 / 1.02));
        yield return ("fit, zoom wobble (Maximum speed filter)", "Maximum speed", viewport.Fit, i => viewport.ZoomBy(i % 2 == 0 ? 1.02 : 1 / 1.02));
        yield return ("100 % pan (Maximum quality filter)", "Maximum quality", viewport.ActualSize, i =>
        {
            var s = viewport.GetScrollState();
            viewport.SetHorizontalScrollOffset((s.HorizontalValue + 37) % Math.Max(1, s.HorizontalMaximum));
        });
        yield return ("400 % pan (Maximum quality filter)", "Maximum quality", () => viewport.ZoomTo(4), i =>
        {
            var s = viewport.GetScrollState();
            viewport.SetVerticalScrollOffset((s.VerticalValue + 23) % Math.Max(1, s.VerticalMaximum));
        });
    }
}
