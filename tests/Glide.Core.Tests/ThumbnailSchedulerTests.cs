using System.Diagnostics;
using Avalonia.Media.Imaging;
using Glide.App.Services;
using Glide.Imaging;
using Glide.Tests.Support;
using SkiaSharp;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0 Explorer thumbnail scheduling: visible tiles first, then cheapest format and size first, look-ahead last; a
/// per-file timeout that gives the lane back and leaves the tile on its glyph; a remembered failure per file version; and the
/// look-ahead filling the disk cache so scrolling onto it is a hit. Decodes are injected, so no picture is needed for the
/// ordering and timeout tests.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailSchedulerTests : IDisposable
{
    private readonly TempDir _dir = new("glide-thumb-scheduler");

    public ThumbnailSchedulerTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        ThumbnailDiskCache.Configure(false, null, 0);
    }

    public void Dispose()
    {
        ThumbnailDiskCache.Configure(false, null, 0);
        ThumbnailDecode.DecodingForTests = null;
        _dir.Dispose();
    }

    private static ThumbnailRequest Request(string name, long size = 1_000_000, long modified = 1) => new(@"C:\pictures\" + name, size, modified, 236);

    // ---- cost model ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_cost_model_orders_formats_the_way_the_audit_measured_them()
    {
        const long MB = 1024 * 1024;
        var ico = ThumbnailCostModel.EstimateMs("a.ico", 30_000);
        var raw = ThumbnailCostModel.EstimateMs("a.dng", 24 * MB);
        var jpeg = ThumbnailCostModel.EstimateMs("a.jpg", 4 * MB);
        var psd = ThumbnailCostModel.EstimateMs("a.psd", 75 * MB);
        var heic = ThumbnailCostModel.EstimateMs("a.heic", 3 * MB);
        var svg = ThumbnailCostModel.EstimateMs("a.svg", MB / 2);
        Assert.True(ico < jpeg, "an icon is cheaper than a JPEG");
        Assert.True(jpeg < psd, "a JPEG is cheaper than a PSD");
        Assert.True(raw < psd, "a RAW preview is cheaper than a PSD");
        Assert.True(psd < svg || svg > 150, "a path-heavy SVG is among the slowest");
        Assert.True(ThumbnailCostModel.Bucket(ico) < ThumbnailCostModel.Bucket(heic));
        Assert.True(ThumbnailCostModel.EstimateMs("a.jpg", 40 * MB) > ThumbnailCostModel.EstimateMs("a.jpg", MB), "a bigger file costs more");
        Assert.InRange(ThumbnailCostModel.Bucket(0), 0, 0);
        Assert.InRange(ThumbnailCostModel.Bucket(1e9), 4, 4);
    }

    // ---- queue order ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Waiting_jobs_start_visible_first_then_cheapest_first_then_in_arrival_order_and_look_ahead_last()
    {
        var started = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var scheduler = new ThumbnailScheduler(1, (path, _) =>
        {
            lock (started) started.Add(Path.GetFileName(path));
            if (Path.GetFileName(path) == "00-blocker.png") release.Wait(TimeSpan.FromSeconds(20));
            return null;
        });

        // The first job takes the only lane; everything else queues behind it.
        var blocker = scheduler.RunAsync(Request("00-blocker.png"), ThumbnailPriority.Visible, CancellationToken.None);
        await WaitUntil(() => scheduler.RunningCount == 1);
        var jobs = new[]
        {
            scheduler.RunAsync(Request("10-heic.heic", 3_000_000), ThumbnailPriority.Visible, CancellationToken.None),   // expensive
            scheduler.RunAsync(Request("11-ahead-ico.ico", 20_000), ThumbnailPriority.Prefetch, CancellationToken.None), // look-ahead: after every visible tile
            scheduler.RunAsync(Request("12-ico.ico", 20_000), ThumbnailPriority.Visible, CancellationToken.None),         // cheapest
            scheduler.RunAsync(Request("13-jpg.jpg", 4_000_000), ThumbnailPriority.Visible, CancellationToken.None),
            scheduler.RunAsync(Request("14-ico.ico", 20_000), ThumbnailPriority.Visible, CancellationToken.None),         // same cost class as 12: arrival order
            scheduler.RunAsync(Request("15-jpg.jpg", 4_000_000), ThumbnailPriority.Visible, CancellationToken.None),
        };
        Assert.Equal(6, scheduler.PendingCount);
        release.Set();
        await blocker;
        await Task.WhenAll(jobs);

        Assert.Equal(new[] { "00-blocker.png", "12-ico.ico", "14-ico.ico", "13-jpg.jpg", "15-jpg.jpg", "10-heic.heic", "11-ahead-ico.ico" }, started);
    }

    [Fact]
    public async Task A_job_cancelled_before_it_starts_never_decodes()
    {
        var started = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var scheduler = new ThumbnailScheduler(1, (path, _) =>
        {
            lock (started) started.Add(Path.GetFileName(path));
            release.Wait(TimeSpan.FromSeconds(20));
            return null;
        });
        var first = scheduler.RunAsync(Request("first.png"), ThumbnailPriority.Visible, CancellationToken.None);
        await WaitUntil(() => scheduler.RunningCount == 1);
        using var cts = new CancellationTokenSource();
        var cancelled = scheduler.RunAsync(Request("scrolled-away.png"), ThumbnailPriority.Visible, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, scheduler.PendingCount);
        release.Set();
        await first;
        Assert.Equal(new[] { "first.png" }, started);
    }

    // ---- timeout and failure memory ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_decode_that_takes_too_long_gives_up_the_lane_and_the_tile_keeps_its_glyph()
    {
        using var hang = new ManualResetEventSlim(false);
        var order = new List<string>();
        var scheduler = new ThumbnailScheduler(1, (path, _) =>
        {
            lock (order) order.Add(Path.GetFileName(path));
            if (Path.GetFileName(path) == "hang.psd") hang.Wait(TimeSpan.FromSeconds(30));
            return null;
        }, timeout: TimeSpan.FromMilliseconds(250));

        var clock = Stopwatch.StartNew();
        var stuck = await scheduler.RunAsync(Request("hang.psd"), ThumbnailPriority.Visible, CancellationToken.None);
        Assert.Null(stuck);
        Assert.InRange(clock.ElapsedMilliseconds, 200, 5000);
        Assert.Equal(1, scheduler.TimedOutCount);
        Assert.Equal(1, scheduler.Abandoned);

        // The single lane is not stalled by the file that hangs: the next picture is decoded straight away.
        var next = await scheduler.RunAsync(Request("next.png"), ThumbnailPriority.Visible, CancellationToken.None);
        Assert.Null(next); // the injected decode returns no picture; what matters is that it ran
        Assert.Contains("next.png", order);

        hang.Set();
        await WaitUntil(() => scheduler.Abandoned == 0);
    }

    [Fact]
    public async Task A_decode_that_was_only_slow_is_forgiven_once_it_finishes()
    {
        using var finish = new ManualResetEventSlim(false);
        var scheduler = new ThumbnailScheduler(1, (_, _) =>
        {
            finish.Wait(TimeSpan.FromSeconds(30));
            return new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(2, 2), new Avalonia.Vector(96, 96));
        }, timeout: TimeSpan.FromMilliseconds(150));
        var request = Request("slow.psd");
        Assert.Null(await scheduler.RunAsync(request, ThumbnailPriority.Visible, CancellationToken.None));
        Assert.True(scheduler.IsKnownFailure(request));
        finish.Set();
        await WaitUntil(() => scheduler.Abandoned == 0);
        Assert.False(scheduler.IsKnownFailure(request)); // its thumbnail is in the disk cache by now: asking again is cheap
    }

    [Fact]
    public async Task A_file_that_failed_or_timed_out_is_not_retried_until_it_changes()
    {
        var calls = 0;
        var scheduler = new ThumbnailScheduler(2, (_, _) => { Interlocked.Increment(ref calls); return null; });
        var request = Request("broken.tga", 5000, 77);
        Assert.Null(await scheduler.RunAsync(request, ThumbnailPriority.Visible, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.True(scheduler.IsKnownFailure(request));
        Assert.Null(await scheduler.RunAsync(request, ThumbnailPriority.Visible, CancellationToken.None));
        Assert.Equal(1, calls); // remembered: no second decode
        Assert.Null(await scheduler.RunAsync(Request("broken.tga", 5000, 78), ThumbnailPriority.Visible, CancellationToken.None));
        Assert.Equal(2, calls); // a new last-write time is a new file version
        scheduler.ForgetFailures();
        Assert.False(scheduler.IsKnownFailure(request));
    }

    [Fact]
    public async Task An_exception_in_a_decoder_is_a_glyph_not_an_error()
    {
        var scheduler = new ThumbnailScheduler(2, (_, _) => throw new InvalidOperationException("corrupt"));
        Assert.Null(await scheduler.RunAsync(Request("corrupt.psd"), ThumbnailPriority.Visible, CancellationToken.None));
        Assert.Equal(0, scheduler.RunningCount);
    }

    [Fact]
    public async Task Slow_files_that_hold_lanes_reduce_the_lanes_instead_of_blocking_the_queue_for_good()
    {
        using var hang = new ManualResetEventSlim(false);
        var scheduler = new ThumbnailScheduler(3, (path, _) =>
        {
            if (Path.GetFileName(path).StartsWith("hang")) hang.Wait(TimeSpan.FromSeconds(30));
            return null;
        }, timeout: TimeSpan.FromMilliseconds(150));
        await Task.WhenAll(
            scheduler.RunAsync(Request("hang1.psd"), ThumbnailPriority.Visible, CancellationToken.None),
            scheduler.RunAsync(Request("hang2.psd"), ThumbnailPriority.Visible, CancellationToken.None),
            scheduler.RunAsync(Request("hang3.psd"), ThumbnailPriority.Visible, CancellationToken.None));
        Assert.Equal(3, scheduler.Abandoned);
        Assert.Equal(1, scheduler.EffectiveLanes); // never zero
        Assert.Null(await scheduler.RunAsync(Request("fine.png"), ThumbnailPriority.Visible, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        hang.Set();
        await WaitUntil(() => scheduler.Abandoned == 0);
        Assert.Equal(3, scheduler.EffectiveLanes);
    }

    // ---- look-ahead fills the cache --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_look_ahead_job_leaves_the_thumbnail_in_the_disk_cache_so_the_tile_is_a_hit()
    {
        ThumbnailDiskCache.Configure(true, _dir.Combine("cache"), 64L * 1024 * 1024);
        var cache = ThumbnailDiskCache.Current!;
        var path = _dir.Combine("ahead.png");
        using (var bitmap = new SKBitmap(640, 480)) { bitmap.Erase(SKColors.SteelBlue); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(path, data.ToArray()); }
        var decodes = 0;
        ThumbnailDecode.DecodingForTests = _ => Interlocked.Increment(ref decodes);

        var scheduler = new ThumbnailScheduler(2);
        var info = new FileInfo(path);
        var ahead = await scheduler.RunAsync(new ThumbnailRequest(path, info.Length, info.LastWriteTimeUtc.Ticks, 200), ThumbnailPriority.Prefetch, CancellationToken.None);
        ahead?.Dispose();
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, decodes);

        using var tile = ThumbnailDecode.DecodeToWidth(path, 200);
        Assert.NotNull(tile);
        Assert.Equal(1, decodes); // the visible tile found the look-ahead's thumbnail in the cache
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
