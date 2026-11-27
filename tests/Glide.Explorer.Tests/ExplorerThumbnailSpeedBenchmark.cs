using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Xunit.Abstractions;

namespace Glide.Explorer.Tests;

/// <summary>
/// Opt-in benchmark (GLIDE_THUMB_BENCH_DIR=&lt;folder of camera JPEGs&gt;, e.g. the corpus tools/thumbnail-speed-benchmark.py
/// generates): opens the real Explorer tab on that folder headlessly and reports time to the first thumbnail and to every
/// realized (visible) tile's thumbnail. Read-only on the folder. Without the variable it returns at once.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerThumbnailSpeedBenchmark(ITestOutputHelper output)
{
    [Fact]
    public async Task CameraFolderThumbnailTiming()
    {
        var folder = Environment.GetEnvironmentVariable("GLIDE_THUMB_BENCH_DIR");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var lines = new List<string>();
        await ExplorerHarness.Run(async h =>
        {
            for (var round = 0; round < 1; round++)
            {

                var sw = Stopwatch.StartNew();
                await h.OpenExplorerAsync(folder);
                var opened = sw.Elapsed.TotalMilliseconds;
                List<Image> Images() => h.BrowserView.GetVisualDescendants().OfType<Image>()
                    .Where(i => i.Tag is string p && p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToList();
                double first = -1;
                while (sw.ElapsedMilliseconds < 60_000)
                {
                    h.Pump();
                    var images = Images();
                    var done = images.Count(i => i.Source is not null);
                    if (first < 0 && done > 0) first = sw.Elapsed.TotalMilliseconds;
                    if (images.Count > 0 && done == images.Count) break;
                    await Task.Delay(2);
                }
                var all = sw.Elapsed.TotalMilliseconds;
                lines.Add($"mode {h.Get<int>("_browserViewMode")}: tiles {Images().Count}, folder open+settle {opened:F0} ms, first thumbnail {first:F0} ms, all visible {all:F0} ms");
                // Second open of the same folder (revisit): cache behaviour.
                sw.Restart();
                await h.OpenExplorerAsync(folder);
                while (sw.ElapsedMilliseconds < 60_000)
                {
                    h.Pump();
                    var images = Images();
                    if (images.Count > 0 && images.All(i => i.Source is not null)) break;
                    await Task.Delay(2);
                }
                lines.Add($"revisit: all visible {sw.Elapsed.TotalMilliseconds:F0} ms");
            }
        }, startExplorer: false);
        foreach (var l in lines) output.WriteLine(l);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "glide-thumb-grid-bench.txt"), lines);
    }
}
