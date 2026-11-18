using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// Deterministic bitmap lifetime (presentation leases) and the decode-pipeline changes of the perf pass:
/// no cached frame is disposed while presented, every released frame is disposed exactly once, rejected
/// cache admissions never hand out a disposed bitmap, prefetch never re-decodes prepared neighbours, and
/// full-decode-only formats settle in one decode with pixels identical to the full-resolution decode.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Decode")]
public sealed class ImageLoadCoordinatorLifetimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glide-lifetime-" + Guid.NewGuid().ToString("N"));

    public ImageLoadCoordinatorLifetimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        TempDir.TryDelete(_dir);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private string WriteImage(string name, int width, int height, int seed = 0, byte[]? exifApp1 = null)
    {
        var path = Path.Combine(_dir, name);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                pixels[i] = (byte)((x * 255 / width + seed * 37) & 0xFF);
                pixels[i + 1] = (byte)(y * 255 / height);
                pixels[i + 2] = (byte)((x / 16 + y / 16 + seed) * 23 & 0xFF);
                pixels[i + 3] = 255;
            }
        }
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Assert.True(NativeImageEncoder.TryEncode(path, (uint)width, (uint)height, (uint)stride, handle.AddrOfPinnedObject(),
                Path.GetExtension(name), 0.92f), "native WIC encoder unavailable for " + name);
        }
        finally { handle.Free(); }
        if (exifApp1 is not null)
        {
            var jpeg = File.ReadAllBytes(path);
            Assert.True(jpeg[0] == 0xFF && jpeg[1] == 0xD8);
            File.WriteAllBytes(path, jpeg.Take(2).Concat(exifApp1).Concat(jpeg.Skip(2)).ToArray());
        }
        return path;
    }

    /// <summary>APP1 Exif segment carrying only IFD0 Orientation.</summary>
    private static byte[] ExifOrientation(ushort orientation)
    {
        var tiff = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0 };
        tiff.AddRange(new byte[] { 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0 });
        tiff.AddRange(new byte[] { 0, 0, 0, 0 });
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        return new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) }.Concat(payload).ToArray();
    }

    private static ImagePerformancePolicy Policy(int decodedMb = 256, int previewBox = 4096, bool prefetch = false) =>
        (ImagePerformancePolicy.ForProfile("Balanced") with
        {
            DecodedCacheMegabytes = decodedMb,
            PreviewLongestSide = previewBox,
            PreviewMaxWidth = previewBox,
            PreviewMaxHeight = previewBox,
            PredictivePrefetch = prefetch,
            RefinementDelayMs = 5
        }).Normalize();

    private sealed class DisposalLog
    {
        private readonly List<Bitmap> _disposed = new();
        public void Attach(ImageLoadCoordinator loader) => loader.BitmapDisposedForTests = b => { lock (_disposed) _disposed.Add(b); };
        public int Count(Bitmap bitmap) { lock (_disposed) return _disposed.Count(d => ReferenceEquals(d, bitmap)); }
        public int Total { get { lock (_disposed) return _disposed.Count; } }
        public bool AnyDuplicates { get { lock (_disposed) return _disposed.GroupBy(b => b, ReferenceEqualityComparer.Instance).Any(g => g.Count() > 1); } }
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        // Copy through the public API so the comparison is independent of the bitmap implementation.
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new Avalonia.PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride); }
        finally { handle.Free(); }
        return buffer;
    }

    // ---------------------------------------------------------------------------------------------
    // Presentation leases
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvictedWhilePresented_IsRetired_ThenDisposedExactlyOnceOnRelease()
    {
        var a = WriteImage("a.png", 2048, 2048, 1); // 16 MB decoded
        var b = WriteImage("b.png", 2048, 2048, 2);
        var c = WriteImage("c.png", 2048, 2048, 3);
        var log = new DisposalLog();
        using var loader = new ImageLoadCoordinator { Policy = Policy(decodedMb: 32) };
        log.Attach(loader);

        var ra = (await loader.LoadForegroundAsync(a, loader.Policy))!;
        var rb = (await loader.LoadForegroundAsync(b, loader.Policy))!;
        var rc = (await loader.LoadForegroundAsync(c, loader.Policy))!;

        // A was evicted (32 MB budget holds two 16 MB frames) but is still presented: retired, alive.
        Assert.False(loader.IsBitmapCached(ra.Bitmap));
        Assert.Equal(1, loader.GetLeaseCount(ra.Bitmap));
        Assert.Equal(0, log.Count(ra.Bitmap));
        Assert.True(loader.RetiredBitmapCount >= 1);
        Assert.Equal(2048, ra.Bitmap.PixelSize.Width);

        Assert.True(loader.ReleaseBitmap(ra.Bitmap));
        Assert.Equal(1, log.Count(ra.Bitmap));
        Assert.False(loader.ReleaseBitmap(ra.Bitmap)); // idempotent: never disposed twice
        Assert.Equal(1, log.Count(ra.Bitmap));

        // B and C are still cached: releasing their leases keeps them alive (cache-owned).
        Assert.False(loader.ReleaseBitmap(rb.Bitmap));
        Assert.False(loader.ReleaseBitmap(rc.Bitmap));
        Assert.Equal(0, log.Count(rb.Bitmap) + log.Count(rc.Bitmap));
        loader.PurgeCaches();
        Assert.Equal(1, log.Count(rb.Bitmap));
        Assert.Equal(1, log.Count(rc.Bitmap));
        Assert.False(log.AnyDuplicates);
    }

    [Fact]
    public async Task PurgeWhilePresented_RetiresFrame_ReleaseDisposesIt()
    {
        var a = WriteImage("purge.png", 1024, 768);
        var log = new DisposalLog();
        using var loader = new ImageLoadCoordinator { Policy = Policy() };
        log.Attach(loader);
        var result = (await loader.LoadForegroundAsync(a, loader.Policy))!;
        loader.SetActivePath(a);

        loader.PurgeCaches(); // e.g. minimize with "purge cache on minimize"
        Assert.Equal(0, log.Count(result.Bitmap));
        Assert.False(loader.IsBitmapCached(result.Bitmap));

        Assert.True(loader.ReleaseBitmap(result.Bitmap));
        Assert.Equal(1, log.Count(result.Bitmap));
    }

    [Fact]
    public async Task PreparedHitIsLeasedPerResult_AndBalancedReleasesKeepItCached()
    {
        var a = WriteImage("hit.png", 800, 600);
        var log = new DisposalLog();
        using var loader = new ImageLoadCoordinator { Policy = Policy() };
        log.Attach(loader);
        var first = (await loader.LoadForegroundAsync(a, loader.Policy))!;
        var again = (await loader.LoadForegroundAsync(a, loader.Policy))!;
        Assert.Same(first.Bitmap, again.Bitmap);
        Assert.True(again.PreparedFrameHit);
        Assert.Equal(2, loader.GetLeaseCount(first.Bitmap));

        loader.ReleaseBitmap(again.Bitmap);
        loader.ReleaseBitmap(first.Bitmap);
        Assert.Equal(0, loader.GetLeaseCount(first.Bitmap));
        Assert.True(loader.IsBitmapCached(first.Bitmap));
        Assert.Equal(0, log.Total);
    }

    [Fact]
    public async Task UncachedResult_IsCallerOwned_AndReleaseDisposesIt()
    {
        var a = WriteImage("uncached.png", 640, 480);
        var log = new DisposalLog();
        using var loader = new ImageLoadCoordinator { Policy = Policy() };
        log.Attach(loader);
        var result = (await loader.LoadFullForegroundAsync(a, CancellationToken.None, cacheResult: false))!;
        Assert.False(loader.IsBitmapCached(result.Bitmap));
        Assert.Equal(0, loader.GetLeaseCount(result.Bitmap));
        Assert.True(loader.ReleaseBitmap(result.Bitmap));
        Assert.Equal(1, log.Count(result.Bitmap));
    }

    [Fact]
    public async Task RejectedCacheAdmission_NeverReturnsADisposedBitmap()
    {
        // 3000 x 3000 x 4 = 36 MB > the 32 MB decoded budget: the cache rejects it. The old AddPrepared
        // disposed the bitmap and the foreground path then returned it to the viewer.
        var big = WriteImage("big.bmp", 3000, 3000);
        var log = new DisposalLog();
        using var loader = new ImageLoadCoordinator { Policy = Policy(decodedMb: 32) };
        log.Attach(loader);
        var result = (await loader.LoadFullForegroundAsync(big))!;
        Assert.Equal(0, log.Total);
        Assert.False(loader.IsBitmapCached(result.Bitmap));
        Assert.Equal(3000, result.Bitmap.PixelSize.Width);
        Assert.Equal(3000 * 3000 * 4, Pixels(result.Bitmap).Length); // still readable
        Assert.True(loader.ReleaseBitmap(result.Bitmap));
    }

    [Fact]
    public async Task PromoteRefinement_LeasesPromotedFrame_AndRetiresTheLeasedPreview()
    {
        var jpeg = WriteImage("promote.jpg", 4000, 3000);
        var log = new DisposalLog();
        using var loader = new ImageLoadCoordinator { Policy = Policy(previewBox: 1000) };
        log.Attach(loader);
        var first = (await loader.LoadForegroundAsync(jpeg, loader.Policy))!;
        Assert.True(first.IsPreview);
        Assert.True(loader.IsBitmapCached(first.Bitmap));

        var full = (await loader.LoadFullForegroundAsync(jpeg, CancellationToken.None, cacheResult: false))!;
        Assert.True(loader.PromoteRefinement(first, full));
        Assert.True(loader.IsBitmapCached(full.Bitmap));
        Assert.Equal(1, loader.GetLeaseCount(full.Bitmap));
        Assert.Equal(0, log.Count(first.Bitmap)); // still leased by its presentation
        Assert.True(loader.ReleaseBitmap(first.Bitmap));
        Assert.Equal(1, log.Count(first.Bitmap));
        Assert.False(loader.ReleaseBitmap(full.Bitmap)); // cached: survives
        Assert.Equal(0, log.Count(full.Bitmap));
    }

    [Fact]
    public async Task ConcurrentPrefetchEvictionAndNavigation_NeverDisposePresentedFrames_AndLeakNothing()
    {
        var paths = Enumerable.Range(0, 14).Select(i => WriteImage($"nav_{i:00}.png", 1024, 1024, i)).ToArray(); // 4 MB each
        var log = new DisposalLog();
        var loader = new ImageLoadCoordinator { Policy = Policy(decodedMb: 32, prefetch: true) with { PrefetchDepth = 4 } };
        log.Attach(loader);
        Bitmap? presented = null;
        loader.IsBitmapInActiveUse = b => b is not null && ReferenceEquals(Volatile.Read(ref presented), b);
        var seen = new HashSet<Bitmap>(ReferenceEqualityComparer.Instance);
        var random = new Random(1234);
        var index = 0;
        for (var step = 0; step < 80; step++)
        {
            index = Math.Clamp(index + (random.Next(4) == 0 ? -1 : 1) * (random.Next(3) == 0 ? 2 : 1), 0, paths.Length - 1);
            var result = await loader.LoadForegroundAsync(paths[index], loader.Policy);
            Assert.NotNull(result);
            Assert.Equal(0, log.Count(result!.Bitmap)); // never handed out disposed
            var previous = presented;
            Volatile.Write(ref presented, result.Bitmap);
            seen.Add(result.Bitmap);
            loader.SchedulePrefetch(paths, index, 1);
            if (random.Next(5) == 0) loader.PurgeCaches();
            if (step % 3 == 0) await Task.Delay(random.Next(1, 15));
            // The presented frame must still be alive while presented.
            Assert.Equal(0, log.Count(result.Bitmap));
            if (previous is not null && !ReferenceEquals(previous, result.Bitmap)) loader.ReleaseBitmap(previous);
            else if (previous is not null) loader.ReleaseBitmap(previous); // balance the extra lease of a re-presentation
        }
        // Let in-flight neighbour decodes finish, then close like MainWindow does.
        await Task.Delay(300);
        var last = presented!;
        Volatile.Write(ref presented, null);
        loader.ReleaseBitmap(last);
        loader.Dispose();
        Assert.False(log.AnyDuplicates);
        foreach (var bitmap in seen) Assert.Equal(1, log.Count(bitmap)); // nothing left to the GC
        Assert.Equal(0, loader.RetiredBitmapCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Decode pipeline
    // ---------------------------------------------------------------------------------------------

    private sealed class CountingBackend : IImageDecoderBackend
    {
        private readonly AvaloniaImageDecoderBackend _inner = new();
        public int FullDecodes;
        public int PreviewDecodes;
        public Bitmap DecodeFull(Stream stream) { Interlocked.Increment(ref FullDecodes); return _inner.DecodeFull(stream); }
        public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode)
        { Interlocked.Increment(ref PreviewDecodes); return _inner.DecodePreview(stream, source, longestSide, interpolationMode); }
        public Bitmap DecodeFull(Stream stream, string? sourcePath) { Interlocked.Increment(ref FullDecodes); return _inner.DecodeFull(stream, sourcePath); }
        public Bitmap DecodePreview(Stream stream, ImageDimensions source, int longestSide, BitmapInterpolationMode interpolationMode, string? sourcePath)
        { Interlocked.Increment(ref PreviewDecodes); return _inner.DecodePreview(stream, source, longestSide, interpolationMode, sourcePath); }
    }

    [Fact]
    public async Task Prefetch_DoesNotRedecodeAlreadyPreparedNeighbours()
    {
        var paths = Enumerable.Range(0, 6).Select(i => WriteImage($"pf_{i}.png", 512, 384, i)).ToArray();
        var backend = new CountingBackend();
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy(prefetch: true) with { PrefetchDepth = 5 } };
        loader.SchedulePrefetch(paths, 0, 1);
        await Wait.UntilAsync(() => loader.GetCacheSnapshot().DecodedItems >= 5, "5 neighbours decoded");
        Assert.Equal(5, loader.GetCacheSnapshot().DecodedItems);
        await Task.Delay(100); // negative wait: let stray work settle before taking the baseline count
        var decodes = backend.FullDecodes + backend.PreviewDecodes;

        loader.SchedulePrefetch(paths, 0, 1); // the same neighbourhood again (e.g. a refresh)
        await Task.Delay(300); // negative wait: proves no re-decode happens
        Assert.Equal(decodes, backend.FullDecodes + backend.PreviewDecodes);
    }

    [Fact]
    public async Task ForegroundJoinsAnInflightNeighbourDecode()
    {
        var paths = Enumerable.Range(0, 2).Select(i => WriteImage($"join_{i}.png", 3000, 2000, i)).ToArray();
        var backend = new CountingBackend();
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy(prefetch: true) with { PrefetchDepth = 1 } };
        loader.SchedulePrefetch(paths, 0, 1); // starts decoding paths[1]
        await Wait.TryUntilAsync(() => backend.FullDecodes + backend.PreviewDecodes > 0, 100, pollMs: 1);
        var result = await loader.LoadForegroundAsync(paths[1], loader.Policy);
        Assert.NotNull(result);
        Assert.Equal(1, backend.FullDecodes + backend.PreviewDecodes); // one decode served both
        loader.ReleaseBitmap(result!.Bitmap);
    }

    [Theory]
    [InlineData("unscalable.bmp")]
    [InlineData("unscalable.png")]
    [InlineData("unscalable.tif")]
    public async Task FullDecodeOnlyFormats_FirstFrameIsSourceResolution_PixelIdenticalToFullDecode(string name)
    {
        var path = WriteImage(name, 3000, 2000);
        using var loader = new ImageLoadCoordinator { Policy = Policy(previewBox: 1200) };
        var first = (await loader.LoadForegroundAsync(path, loader.Policy))!;
        Assert.False(first.IsPreview);
        Assert.Equal(3000, first.Bitmap.PixelSize.Width);
        Assert.Equal(2000, first.Bitmap.PixelSize.Height);
        Assert.Null(await loader.RefineForegroundAsync(first)); // nothing left to refine

        using var reference = new ImageLoadCoordinator { Policy = Policy(previewBox: 1200) };
        var full = (await reference.LoadFullForegroundAsync(path, CancellationToken.None, cacheResult: false))!;
        Assert.Equal(Pixels(full.Bitmap), Pixels(first.Bitmap));
        full.Bitmap.Dispose();
    }

    [Theory]
    [InlineData("direct.png", 1500, 1000)]
    [InlineData("direct.jpg", 1500, 1000)]
    [InlineData("direct.tif", 1500, 1000)]
    [InlineData("direct.bmp", 1500, 1000)]
    // 5.0.7: TIFF and BMP of 4 MP and more decode in parallel bands (one WIC decoder per band).
    [InlineData("direct-bands.tif", 3000, 2000)]
    [InlineData("direct-bands.bmp", 3000, 2000)]
    [InlineData("direct-bands-odd.tif", 2999, 2003)]
    public void DirectFullDecode_IsPixelIdenticalToTheBufferedPath(string name, int width, int height)
    {
        var path = WriteImage(name, width, height, seed: 5);
        Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(path, out var buffered));
        using (buffered)
        {
            if (!NativeImageDecoder.TryDecodeFullDirectForTests(path, out var direct))
            {
                // Older Glide.Native.dll without GlideDecodeImageIntoW: the buffered path is used everywhere.
                // (Set GLIDE_EXPECT_DIRECT_DECODE=1 when testing against a freshly built bridge.)
                Assert.NotEqual("1", Environment.GetEnvironmentVariable("GLIDE_EXPECT_DIRECT_DECODE"));
                Assert.False(NativeImageDecoder.DirectFullDecodeAvailable);
                return;
            }
            using (direct)
            {
                Assert.Equal(buffered.PixelSize, direct.PixelSize);
                Assert.Equal(Pixels(buffered), Pixels(direct));
            }
        }
    }

    [Fact]
    public void MissingNativeCodec_IsSkippedOnlyAfterThreeManagedFallbacks()
    {
        var ext = ".glidetest" + Guid.NewGuid().ToString("N")[..6];
        NativeImageDecoder.MarkNativeCodecUnavailable("a" + ext);
        NativeImageDecoder.MarkNativeCodecUnavailable("b" + ext);
        Assert.False(NativeImageDecoder.IsNativeCodecKnownUnavailable("c" + ext)); // one odd file is not enough
        NativeImageDecoder.MarkNativeCodecUnavailable("c" + ext);
        Assert.True(NativeImageDecoder.IsNativeCodecKnownUnavailable("d" + ext));
        Assert.False(NativeImageDecoder.IsNativeCodecKnownUnavailable("photo.jpg"));
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void DirectFullDecode_OfAnExifOrientedJpeg_IsPixelIdenticalToTheBufferedOrientationPath(ushort orientation)
    {
        // 2400 x 1700 = 4.08 MP: large enough for the multi-threaded transform (5.0.7).
        var jpeg = WriteImage($"direct-rotated-{orientation}.jpg", 2400, 1700, seed: orientation, exifApp1: ExifOrientation(orientation));
        var swaps = orientation >= 5;
        Assert.True(NativeImageDecoder.TryDecode(jpeg, 0, out var oriented));
        using (oriented)
        {
            Assert.Equal(swaps ? 1700 : 2400, oriented.PixelSize.Width);
            Assert.Equal(swaps ? 2400 : 1700, oriented.PixelSize.Height);
            Assert.True(NativeImageDecoder.TryDecodeFullBufferedForTests(jpeg, out var buffered));
            using (buffered)
            {
                if (!NativeImageDecoder.TryDecodeFullDirectForTests(jpeg, out var direct))
                {
                    // Bridges before 5.0.7 hand oriented frames to the buffered path (status 2).
                    Assert.NotEqual("1", Environment.GetEnvironmentVariable("GLIDE_EXPECT_DIRECT_DECODE"));
                    return;
                }
                using (direct)
                {
                    Assert.Equal(buffered.PixelSize, direct.PixelSize);
                    Assert.Equal(Pixels(buffered), Pixels(direct));
                    Assert.Equal(Pixels(oriented), Pixels(direct));
                }
            }
        }
    }

    [Fact]
    public async Task FullFirstFrame_RespectsTheBudgetCap()
    {
        // 3000 x 2000 x 4 = 24 MB > 32 MB / 2: stays on the bounded first frame + refinement path.
        var path = WriteImage("capped.bmp", 3000, 2000);
        Assert.False(ImageLoadCoordinator.PrefersFullFirstFrame(path, new ImageDimensions(3000, 2000), Policy(decodedMb: 32)));
        Assert.True(ImageLoadCoordinator.PrefersFullFirstFrame(path, new ImageDimensions(3000, 2000), Policy(decodedMb: 256)));
        Assert.False(ImageLoadCoordinator.PrefersFullFirstFrame("photo.jpg", new ImageDimensions(3000, 2000), Policy(decodedMb: 256)));
        using var loader = new ImageLoadCoordinator { Policy = Policy(decodedMb: 32, previewBox: 1200) };
        var first = (await loader.LoadForegroundAsync(path, loader.Policy))!;
        Assert.True(first.IsPreview);
        Assert.True(first.Bitmap.PixelSize.Width <= 1200);
        loader.ReleaseBitmap(first.Bitmap);
    }

    [Fact]
    public async Task Jpeg_RefinementIsSkippedWhenTheFirstFrameCoversTheViewport_AndGoesStraightToFullOtherwise()
    {
        var jpeg = WriteImage("refine.jpg", 4000, 3000);
        var backend = new CountingBackend();
        using var loader = new ImageLoadCoordinator(backend) { Policy = Policy(previewBox: 1900) with { PreviewLongestSide = 2400 } };
        loader.SetViewportDemand(jpeg, new ImageViewportDemand(1900, 1000));
        var first = (await loader.LoadForegroundAsync(jpeg, loader.Policy))!;
        Assert.True(first.IsPreview);
        Assert.Equal(2000, first.Bitmap.PixelSize.Width); // DCT 1/2
        var decodes = backend.FullDecodes + backend.PreviewDecodes;
        Assert.Null(await loader.RefineForegroundAsync(first)); // 2000 px already covers a 1900 px viewport
        Assert.Equal(decodes, backend.FullDecodes + backend.PreviewDecodes);

        loader.SetViewportDemand(jpeg, new ImageViewportDemand(3800, 2000));
        var refined = await loader.RefineForegroundAsync(first);
        Assert.NotNull(refined);
        Assert.False(refined!.IsPreview); // direct full decode, no discarded bounded intermediate
        Assert.Equal(4000, refined.Bitmap.PixelSize.Width);
        Assert.Equal(1, backend.FullDecodes);
        refined.Bitmap.Dispose();
    }

    [Fact]
    public async Task ExifOrientation_IsAppliedOnPreviewFullAndStartupPreloadRoutes()
    {
        var jpeg = WriteImage("rotated.jpg", 4000, 3000, exifApp1: ExifOrientation(6));
        using var loader = new ImageLoadCoordinator { Policy = Policy(previewBox: 1000) };
        var preview = (await loader.LoadForegroundAsync(jpeg, loader.Policy))!;
        Assert.True(preview.IsPreview);
        Assert.True(preview.Bitmap.PixelSize.Height > preview.Bitmap.PixelSize.Width);
        Assert.Equal(3000, preview.SourceWidth);
        Assert.Equal(4000, preview.SourceHeight);

        var full = (await loader.LoadFullForegroundAsync(jpeg, CancellationToken.None, cacheResult: false))!;
        Assert.Equal(3000, full.Bitmap.PixelSize.Width);
        Assert.Equal(4000, full.Bitmap.PixelSize.Height);
        full.Bitmap.Dispose();

        StartupImagePreloader.Start(jpeg, 1000, 1000);
        using var cold = new ImageLoadCoordinator { Policy = Policy(previewBox: 1000) };
        var preloaded = (await cold.LoadForegroundAsync(jpeg, cold.Policy))!;
        Assert.StartsWith("startup", preloaded.DecodeRoute);
        Assert.True(preloaded.IsPreview); // bounded preload is reported as a preview, so refinement still runs
        Assert.True(preloaded.Bitmap.PixelSize.Height > preloaded.Bitmap.PixelSize.Width);
        Assert.True(preloaded.Bitmap.PixelSize.Height <= 1500);
        Assert.Equal(3000, preloaded.SourceWidth);
        Assert.Equal(4000, preloaded.SourceHeight);
    }

    [Fact]
    public async Task StartupPreload_SmallImageIsFullResolution()
    {
        var png = WriteImage("startup-small.png", 640, 480);
        StartupImagePreloader.Start(png, 2400, 1300);
        using var loader = new ImageLoadCoordinator { Policy = Policy() };
        var result = (await loader.LoadForegroundAsync(png, loader.Policy))!;
        Assert.StartsWith("startup", result.DecodeRoute);
        Assert.False(result.IsPreview);
        Assert.Equal(640, result.Bitmap.PixelSize.Width);
    }

    [Fact]
    public void HardwareLanesAreBoundedAndScaleWithProcessors()
    {
        var profile = ImagePerformanceGovernor.Current;
        Assert.InRange(ImagePerformanceGovernor.SpeculativeDecodeLanes, 1, 6);
        Assert.InRange(ImagePerformanceGovernor.ThumbnailDecodeLanes, 2, 8);
        Assert.True(ImagePerformanceGovernor.ThumbnailDecodeLanes <= Math.Max(2, profile.LogicalProcessors));
        Assert.Equal(1, new ImageHardwareProfile(GlideHardwareTier.Constrained, 4, 4L << 30).SpeculativeDecodeLanes);
        Assert.Equal(2, new ImageHardwareProfile(GlideHardwareTier.Mainstream, 8, 16L << 30).SpeculativeDecodeLanes);
        Assert.Equal(4, new ImageHardwareProfile(GlideHardwareTier.HighEnd, 16, 32L << 30).SpeculativeDecodeLanes);
        Assert.Equal(6, new ImageHardwareProfile(GlideHardwareTier.HighEnd, 64, 128L << 30).SpeculativeDecodeLanes);
    }
}
