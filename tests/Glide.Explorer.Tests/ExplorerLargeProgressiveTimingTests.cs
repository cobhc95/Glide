using Avalonia.Controls;
using Avalonia.VisualTree;
using Glide.App.Explorer;
using Glide.App.Services;
using Glide.Imaging;
using Xunit.Abstractions;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.2.0 measurement (opt-in, not a regular test): the real Explorer tab (headless MainWindow, production scheduler and pipeline)
/// opened on the folder named by <c>GLIDE_THUMB_TIMING_DIR</c> (for example the synthetic 45 MP progressive folder from
/// tools/perf/generate_progressive_45mp.py). The disk cache is off unless <c>GLIDE_THUMB_TIMING_CACHE</c> names a fresh folder.
/// Prints, without scrolling: the time until every visible tile shows its picture and the time until every picture of the folder is
/// ready (in the memory cache, from the whole-folder preparation); then it scrolls to the end and prints how long the last screen
/// took to show. Optional <c>GLIDE_THUMB_TIMING_LANES</c> sets "Thumbnails loaded at once". Read-only on the folder; without the
/// variable it returns at once.
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerLargeProgressiveTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Time_to_all_thumbnails_of_a_folder_in_the_real_tab()
    {
        var dir = Environment.GetEnvironmentVariable("GLIDE_THUMB_TIMING_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        await ExplorerHarness.Run(async h =>
        {
            var cacheDir = Environment.GetEnvironmentVariable("GLIDE_THUMB_TIMING_CACHE");
            ThumbnailDiskCache.Configure(!string.IsNullOrEmpty(cacheDir), cacheDir, 512L * 1024 * 1024);
            if (Environment.GetEnvironmentVariable("GLIDE_THUMB_TIMING_LANES") is { Length: > 0 } lanes) h.Settings.ThumbnailLanes = lanes;
            h.Call("ApplyThumbnailLoadSettings");
            var scheduler = h.Get<ThumbnailScheduler>("_browserThumbnails");
            scheduler.MemoryCache?.Clear();
            scheduler.ResetPeaks();
            bool Shown(Border tile) => tile.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null);
            bool AllRealisedShown() => h.Surface.Repeater.Children.OfType<Border>().Where(t => t.Tag is ExplorerEntry { IsDirectory: false }).All(Shown);

            var peakPrivate = 0L;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                while (!stop.IsCancellationRequested) { self.Refresh(); peakPrivate = Math.Max(peakPrivate, self.PrivateMemorySize64); await Task.Delay(5); }
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await h.OpenExplorerAsync(dir);
            var total = h.Entries.Count(e => !e.IsDirectory);
            await h.WaitUntil(AllRealisedShown, "first screen", 120_000);
            var firstScreenMs = clock.Elapsed.TotalMilliseconds;
            double readyMs = -1;
            if (scheduler.MemoryCache is { } memory)
            {
                await h.WaitUntil(() => memory.Count >= total || scheduler.PendingCount + scheduler.RunningCount == 0, "folder prepared", 120_000);
                readyMs = clock.Elapsed.TotalMilliseconds;
            }
            var prepared = scheduler.MemoryCache?.Count ?? 0;
            var scroll = h.Surface.ScrollViewer;
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
            h.Window.UpdateLayout();
            var scrolled = System.Diagnostics.Stopwatch.StartNew();
            await h.WaitUntil(AllRealisedShown, "last screen", 120_000);
            var lastScreenMs = scrolled.Elapsed.TotalMilliseconds;
            stop.Cancel();
            await sampler;
            output.WriteLine($"files={total} lanes={scheduler.DecodeLanes} firstScreenMs={firstScreenMs:F0} allPreparedMs={readyMs:F0} prepared={prepared} " +
                             $"lastScreenAfterScrollMs={lastScreenMs:F0} peakDecode={scheduler.PeakDecodeInFlight} peakPrivateMb={peakPrivate / 1048576}");
        }, startExplorer: false);
    }
}
