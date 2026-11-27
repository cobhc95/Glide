using Avalonia.Controls;
using Avalonia.VisualTree;
using Glide.App.Services;

namespace Glide.Explorer.Tests;

/// <summary>
/// 5.2.0, the real Explorer tab (a headless MainWindow, real tiles, real realisation order) against the thumbnail pipeline with a slow
/// first open injected: the tab must start many reads at once, in on-screen reading order, and must not wait for one tile before
/// asking for the next (a slow disk made the tab load one tile at a time).
/// </summary>
[Collection(ExplorerHeadlessCollection.Name)]
[Trait("Area", "Explorer")]
public sealed class ExplorerThumbnailConcurrencyTests
{
    private const int Files = 60;
    private const int LatencyMs = 250;

    private static void Populate(string root)
    {
        var png = ExplorerHarness.Png(48, 32, 220, 40, 40);
        for (var i = 0; i < Files; i++) File.WriteAllBytes(Path.Combine(root, $"photo-{i:D3}.png"), png);
    }

    [Fact]
    public async Task The_tab_opens_the_visible_files_in_parallel_and_in_reading_order_and_every_visible_tile_gets_its_picture()
    {
        await ExplorerHarness.Run(async h =>
        {
            var opened = new List<string>();
            var inFlight = 0;
            var peak = 0;
            var pipeline = new ThumbnailPipeline
            {
                UseWindowsCache = false,
                BeforeOpen = path =>
                {
                    lock (opened) opened.Add(Path.GetFileName(path));
                    var now = Interlocked.Increment(ref inFlight);
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                    Thread.Sleep(LatencyMs);
                    Interlocked.Decrement(ref inFlight);
                },
            };
            var scheduler = new ThumbnailScheduler(4, pipeline, ioLanes: 12, timeout: TimeSpan.FromSeconds(30));
            h.Set("_browserThumbnails", scheduler);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            await h.OpenExplorerAsync(h.Root);
            var firstScreen = h.Names.Take(12).ToArray();
            foreach (var name in firstScreen)
            {
                var tile = h.Tile(name);
                Assert.NotNull(tile);
                await h.WaitUntil(() => tile!.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), name + " thumbnail", timeoutMs: 15000);
            }
            var elapsed = clock.Elapsed;

            Assert.True(peak >= 8, $"only {peak} files were being opened at once; the tab is opening them one by one");
            // 12 visible tiles through 12 lanes is one latency; serial opens would be 12 x 250 ms = 3 s before the last picture.
            Assert.True(elapsed < TimeSpan.FromSeconds(2.8), $"the first 12 tiles took {elapsed.TotalMilliseconds:F0} ms");
            string[] firstOpened;
            lock (opened) firstOpened = opened.Take(firstScreen.Length).ToArray();
            // The first reads are the first screen's tiles (top-left first), not whichever files the repeater realised last. Parallel lanes
            // pick up their jobs within a few ms of each other, so the order inside that first batch is not asserted here (the scheduler's
            // own test asserts the exact order with one lane).
            Assert.Equal(firstScreen.OrderBy(n => n, StringComparer.Ordinal), firstOpened.OrderBy(n => n, StringComparer.Ordinal));
        }, Populate, startExplorer: false);
    }
}
