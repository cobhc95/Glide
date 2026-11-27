using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Glide.App.Services;
using Glide.Core.Settings;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.2.0, the 45 MP progressive JPEG folder: Glide's own DC-only reader (GlideJpegDc.cpp, no libjpeg coefficient buffer) is the
/// route of a progressive thumbnail from the path and from memory, gives libjpeg's DC picture, declines what it does not handle;
/// "Thumbnails loaded at once" resolves and is clamped by memory and applies to a running scheduler; the in-memory thumbnail cache and
/// a request joining the whole-folder preparation's job decode a file once.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ProgressiveDcReaderTests
{
    public ProgressiveDcReaderTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        ThumbnailDiskCache.Configure(false, null, 0);
    }

    private const int RouteOwnDc = 1;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jpeg", name);

    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    [Theory]
    [InlineData("progressive45-5464x8192.jpg", 683, 1024)]
    [InlineData("big-progressive420-4480x6720.jpg", 560, 840)]
    [InlineData("progressive-dri-2000x3000.jpg", 250, 375)]
    [InlineData("progressive-gray-2000x3000.jpg", 250, 375)]
    public void The_own_dc_reader_gives_libjpegs_one_eighth_picture(string name, int width, int height)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Fixture(name);
        Assert.True(NativeImageDecoder.TryDecodeJpegDcOnlyForTests(File.ReadAllBytes(path), out var own), name + " declined");
        Assert.True(NativeImageDecoder.TryDecodeProgressiveDcForTests(path, out var reference));
        using (own)
        using (reference)
        {
            Assert.Equal(new PixelSize(width, height), own!.PixelSize);
            Assert.Equal(reference!.PixelSize, own.PixelSize);
            var a = Pixels(own);
            var b = Pixels(reference);
            var worst = 0;
            long total = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var d = Math.Abs(a[i] - b[i]);
                total += d;
                worst = Math.Max(worst, d);
            }
            Assert.True(worst <= 2, $"{name}: a pixel differs from libjpeg's DC decode by {worst} levels");
            Assert.True(total / (double)a.Length < 0.5, $"{name}: mean difference {total / (double)a.Length:F2}");
        }
    }

    [Fact]
    public void A_45_mp_progressive_thumbnail_takes_the_own_dc_route_from_memory_and_from_the_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Fixture("progressive45-5464x8192.jpg");
        Assert.True(NativeImageDecoder.TryDecodeJpegThumbnailFromMemory(File.ReadAllBytes(path), 236, out var fromMemory));
        Assert.Equal(RouteOwnDc, NativeImageDecoder.LastJpegThumbnailRouteForTests());
        Assert.True(NativeImageDecoder.TryDecodeJpegThumbnail(path, 152, out var fromPath));
        Assert.Equal(RouteOwnDc, NativeImageDecoder.LastJpegThumbnailRouteForTests());
        using (fromMemory)
        using (fromPath)
        {
            Assert.Equal(236, fromMemory!.PixelSize.Width);
            Assert.InRange(fromMemory.PixelSize.Height, 353, 355);
            Assert.Equal(152, fromPath!.PixelSize.Width);
        }

        // The Explorer tab's whole decode (the pipeline reads the bytes, the decode lane decodes them) takes the same route.
        var bytes = File.ReadAllBytes(path);
        using var tile = ThumbnailDecode.DecodeUncached(path, 236, bytes, bytes.Length);
        Assert.NotNull(tile);
        Assert.Equal(RouteOwnDc, NativeImageDecoder.LastJpegThumbnailRouteForTests());
    }

    [Fact]
    public void The_own_dc_reader_declines_baseline_cmyk_and_cut_files()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(NativeImageDecoder.TryDecodeJpegDcOnlyForTests(File.ReadAllBytes(Fixture("big-base420-4480x6720.jpg")), out _));
        Assert.False(NativeImageDecoder.TryDecodeJpegDcOnlyForTests(File.ReadAllBytes(Fixture("cmyk-ycck-progressive-1001x667.jpg")), out _));
        var bytes = File.ReadAllBytes(Fixture("progressive45-5464x8192.jpg"));
        Assert.False(NativeImageDecoder.TryDecodeJpegDcOnlyForTests(bytes.AsSpan(0, 3000), out _));
        Assert.False(NativeImageDecoder.TryDecodeJpegDcOnlyForTests(bytes.AsSpan(0, bytes.Length / 40), out _)); // inside the DC scan
        // Every prefix length of the header and first scan either declines or decodes; none crashes or reads past the end.
        for (var cut = 2; cut < 4000; cut += 97)
            if (NativeImageDecoder.TryDecodeJpegDcOnlyForTests(bytes.AsSpan(0, cut), out var partial)) partial?.Dispose();
    }

    // ---- "Thumbnails loaded at once" -------------------------------------------------------------------------------------------

    [Fact]
    public void The_lane_setting_resolves_automatic_and_fixed_values_and_is_clamped_by_memory()
    {
        const long GB = 1024L * 1024 * 1024;
        Assert.Equal(12, ThumbnailLanesSetting.Resolve("Automatic", highEnd: true, 16, 48 * GB));
        Assert.Equal(8, ThumbnailLanesSetting.Resolve("Automatic", highEnd: false, 16, 48 * GB));
        Assert.Equal(24, ThumbnailLanesSetting.Resolve("24", true, 16, 48 * GB));
        Assert.Equal(32, ThumbnailLanesSetting.Resolve("32", true, 16, 48 * GB));
        Assert.Equal(12, ThumbnailLanesSetting.Resolve("nonsense", true, 16, 48 * GB)); // unknown value = Automatic
        // 4 GB free: a quarter is 1 GB = 6 lanes of 160 MB, whatever was asked for; never below 2.
        Assert.Equal(6, ThumbnailLanesSetting.Resolve("32", true, 16, 4 * GB));
        Assert.Equal(6, ThumbnailLanesSetting.Resolve("Automatic", true, 16, 4 * GB));
        Assert.Equal(2, ThumbnailLanesSetting.Resolve("16", true, 16, GB / 4));
        Assert.Equal(ThumbnailLanesSetting.Choices, new[] { "Automatic", "4", "8", "12", "16", "24", "32" });
        Assert.Equal(ThumbnailLanesSetting.Automatic, new GlideSettingsState().ThumbnailLanes);
        Assert.True(new GlideSettingsState().ThumbnailPrepareFolder);
    }

    [Fact]
    public async Task Changing_the_lanes_of_a_running_scheduler_applies_at_once()
    {
        using var release = new ManualResetEventSlim(false);
        var inFlight = 0;
        var peak = 0;
        var scheduler = new ThumbnailScheduler(2, (_, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (release) peak = Math.Max(peak, now);
            release.Wait(TimeSpan.FromSeconds(20));
            Interlocked.Decrement(ref inFlight);
            return null;
        }, TimeSpan.FromSeconds(30));
        var jobs = Enumerable.Range(0, 12).Select(i =>
            scheduler.RunAsync(new ThumbnailRequest($@"C:\p\{i:D2}.png", 1000, 1, 236), ThumbnailPriority.Visible, CancellationToken.None)).ToArray();
        await WaitUntil(() => Volatile.Read(ref inFlight) == 2);
        Assert.Equal(2, scheduler.DecodeRunningCount);
        scheduler.SetLanes(8, 16);
        await WaitUntil(() => Volatile.Read(ref inFlight) == 8);
        Assert.Equal(8, scheduler.DecodeLanes);
        release.Set();
        await Task.WhenAll(jobs);
        Assert.Equal(8, peak);
        scheduler.SetLanes(500, 500);
        Assert.Equal(ThumbnailScheduler.MaxDecodeLanes, scheduler.DecodeLanes);
    }

    // ---- memory cache and the whole-folder preparation ------------------------------------------------------------------------

    private static Bitmap SolidBitmap() => new WriteableBitmap(new PixelSize(20, 30), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);

    [Fact]
    public async Task A_tile_asked_for_after_the_folder_was_prepared_comes_from_memory_without_a_decode()
    {
        var decodes = 0;
        var scheduler = new ThumbnailScheduler(4, (_, _) => { Interlocked.Increment(ref decodes); return SolidBitmap(); })
        {
            MemoryCache = new ThumbnailMemoryCache(16L * 1024 * 1024),
        };
        var request = new ThumbnailRequest(@"C:\p\a.jpg", 1000, 1, 236);
        (await scheduler.RunAsync(request, ThumbnailPriority.Prefetch, CancellationToken.None))?.Dispose();
        Assert.Equal(1, decodes);
        using var shown = await scheduler.RunAsync(request, ThumbnailPriority.Visible, CancellationToken.None, out var timing);
        Assert.NotNull(shown);
        Assert.Equal(new PixelSize(20, 30), shown!.PixelSize);
        Assert.Equal(1, decodes);
        Assert.Equal(ThumbnailSource.Memory, timing.Source);
        // A different file version is a different picture.
        (await scheduler.RunAsync(request with { ModifiedTicks = 2 }, ThumbnailPriority.Visible, CancellationToken.None))?.Dispose();
        Assert.Equal(2, decodes);
    }

    [Fact]
    public async Task A_visible_tile_joins_the_queued_preparation_job_of_its_file_and_the_file_is_decoded_once()
    {
        var decodes = new List<string>();
        using var release = new ManualResetEventSlim(false);
        var scheduler = new ThumbnailScheduler(1, (path, _) =>
        {
            lock (decodes) decodes.Add(Path.GetFileName(path));
            if (Path.GetFileName(path) == "blocker.jpg") release.Wait(TimeSpan.FromSeconds(20));
            return SolidBitmap();
        }) { MemoryCache = new ThumbnailMemoryCache(16L * 1024 * 1024) };
        var blocker = scheduler.RunAsync(new ThumbnailRequest(@"C:\p\blocker.jpg", 1000, 1, 236), ThumbnailPriority.Visible, CancellationToken.None);
        await WaitUntil(() => scheduler.RunningCount == 1);
        // The whole folder is queued for preparation, in folder order.
        var prepared = Enumerable.Range(0, 6).Select(i =>
            scheduler.RunAsync(new ThumbnailRequest($@"C:\p\{i:D2}.jpg", 1000, 1, 236, i), ThumbnailPriority.Prefetch, CancellationToken.None)).ToArray();
        // The user scrolls to the last file: its tile joins the queued job, which moves ahead of the rest.
        var visible = scheduler.RunAsync(new ThumbnailRequest(@"C:\p\05.jpg", 1000, 1, 236, 0), ThumbnailPriority.Visible, CancellationToken.None);
        release.Set();
        using (var shown = await visible) Assert.NotNull(shown);
        foreach (var b in await Task.WhenAll(prepared)) b?.Dispose();
        (await blocker)?.Dispose();
        lock (decodes)
        {
            Assert.Equal(1, decodes.Count(d => d == "05.jpg"));
            Assert.Equal("05.jpg", decodes[1]); // right after the blocker, ahead of 00 - 04
        }
    }

    [Fact]
    public void The_memory_cache_drops_the_least_recently_used_pictures_beyond_its_budget()
    {
        using var bitmap = SolidBitmap(); // 20 x 30 x 4 = 2400 bytes
        var cache = new ThumbnailMemoryCache(5000);
        cache.Put("a", bitmap);
        cache.Put("b", bitmap);
        using (cache.TryCreate("a")) { } // a is now the most recent
        cache.Put("c", bitmap);
        Assert.True(cache.Contains("a"));
        Assert.False(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
        Assert.True(cache.Bytes <= 5000);
        cache.SetMaxBytes(0);
        Assert.Equal(0, cache.Count);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) Assert.Fail("timed out");
            await Task.Delay(5);
        }
    }
}
