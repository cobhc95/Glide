using Glide.Imaging;
using Glide.Tests.Support;

namespace Glide.Core.Tests;

/// <summary>
/// The persistent Explorer thumbnail cache (<see cref="ThumbnailDiskCache"/>): hits return exactly the stored pixels, a changed
/// picture is a miss (and its old entry is removed), trimming drops the least recently used entries first, concurrent readers
/// and writers never see a partial file, and damaged files are misses. Instance-level only: no shared state, so these run in
/// parallel with everything else (the shared on/off state is covered in <see cref="ThumbnailDiskCacheDecodeTests"/>).
/// </summary>
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailDiskCacheTests : IDisposable
{
    private readonly TempDir _dir = new("glide-thumbcache");

    public void Dispose() => _dir.Dispose();

    private string Source(string name, int bytes = 100)
    {
        var path = _dir.Combine("src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private ThumbnailDiskCache NewCache(long maxBytes = 64L * 1024 * 1024, bool clamp = true) =>
        new(_dir.Combine("cache"), maxBytes, clamp) { TouchInterval = TimeSpan.Zero };

    private static byte[] Fill(int width, int height, byte value, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = value; pixels[i + 1] = (byte)(value + 1); pixels[i + 2] = (byte)(value + 2); pixels[i + 3] = alpha; }
        return pixels;
    }

    private static void Put(ThumbnailDiskCache cache, ThumbnailDiskCache.Stamp stamp, int w, int h, byte value, byte alpha = 255)
    {
        cache.Put(stamp, w, h, Fill(w, h, value, alpha), w * 4);
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));
    }

    private static string[] CacheFiles(ThumbnailDiskCache cache) =>
        Directory.Exists(cache.Directory) ? Directory.GetFiles(cache.Directory, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public void A_stored_thumbnail_is_returned_unchanged_opaque_and_transparent()
    {
        var cache = NewCache();
        var opaque = cache.StampFor(Source("opaque.jpg"), 236);
        var transparent = cache.StampFor(Source("alpha.png"), 236);
        var pixelsOpaque = Fill(40, 30, 10);
        var pixelsAlpha = Fill(40, 30, 20, alpha: 77);
        cache.Put(opaque, 40, 30, pixelsOpaque, 160);
        cache.Put(transparent, 40, 30, pixelsAlpha, 160);
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));

        using (var hit = cache.TryGet(opaque))
        {
            Assert.NotNull(hit);
            Assert.Equal((40, 30), (hit!.Width, hit.Height));
            Assert.True(hit.Pixels.SequenceEqual(pixelsOpaque));
        }
        using (var hit = cache.TryGet(transparent))
        {
            Assert.NotNull(hit);
            Assert.True(hit!.Pixels.SequenceEqual(pixelsAlpha));
        }
        // An opaque thumbnail is stored as 24-bit BGR (3 of 4 bytes), a transparent one keeps alpha.
        var sizes = CacheFiles(cache).Select(f => new FileInfo(f).Length).OrderBy(x => x).ToArray();
        Assert.Equal([64 + 40 * 30 * 3L, 64 + 40 * 30 * 4L], sizes);
        Assert.Equal(2, cache.Hits);
    }

    [Fact]
    public void A_row_stride_wider_than_the_pixels_is_stored_without_the_padding()
    {
        var cache = NewCache();
        var stamp = cache.StampFor(Source("stride.jpg"), 100);
        var padded = new byte[(8 + 4) * 4 * 6];
        for (var y = 0; y < 6; y++) for (var x = 0; x < 8; x++) { var o = (y * 12 + x) * 4; padded[o] = (byte)y; padded[o + 1] = (byte)x; padded[o + 2] = 9; padded[o + 3] = 255; }
        cache.Put(stamp, 8, 6, padded, 12 * 4);
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));
        using var hit = cache.TryGet(stamp)!;
        Assert.Equal((8, 6), (hit.Width, hit.Height));
        Assert.Equal(new byte[] { 3, 5, 9, 255 }, hit.Pixels.Slice((3 * 8 + 5) * 4, 4).ToArray());
    }

    [Fact]
    public void Unknown_picture_other_width_and_missing_source_are_misses()
    {
        var cache = NewCache();
        var source = Source("a.jpg");
        Put(cache, cache.StampFor(source, 236), 10, 10, 1);

        Assert.NotNull(cache.TryGet(cache.StampFor(source, 236)));
        Assert.Null(cache.TryGet(cache.StampFor(source, 152)));                       // another requested size
        Assert.Null(cache.TryGet(cache.StampFor(Source("b.jpg"), 236)));              // another picture
        Assert.False(cache.StampFor(_dir.Combine("src", "missing.jpg"), 236).IsValid); // nothing to key on
        Assert.Null(cache.TryGet(default));
    }

    [Fact]
    public void A_changed_picture_is_a_miss_and_its_old_thumbnail_is_removed_when_the_new_one_is_stored()
    {
        var cache = NewCache();
        var source = Source("photo.jpg", 100);
        Put(cache, cache.StampFor(source, 236), 10, 10, 1);
        Assert.Single(CacheFiles(cache));

        // Same size, later modified time (an in-place edit): a miss.
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(5));
        var afterTouch = cache.StampFor(source, 236);
        Assert.Null(cache.TryGet(afterTouch));
        // Different size (a replaced file): a miss as well.
        File.WriteAllBytes(source, new byte[250]);
        var afterRewrite = cache.StampFor(source, 236);
        Assert.Null(cache.TryGet(afterRewrite));

        Put(cache, afterRewrite, 10, 10, 2);
        var files = CacheFiles(cache);
        Assert.Single(files); // the stale version of the same picture and width is gone
        using var hit = cache.TryGet(afterRewrite)!;
        Assert.Equal(2, hit.Pixels[0]);
    }

    [Fact]
    public void Trimming_removes_the_least_recently_used_thumbnails_first()
    {
        var entry = 64 + 16 * 16 * 4; // a transparent 16x16 thumbnail
        var cache = NewCache(entry * 100L, clamp: false);
        var stamps = Enumerable.Range(0, 10).Select(i => cache.StampFor(Source($"p{i}.png"), 100)).ToArray();
        for (var i = 0; i < stamps.Length; i++) Put(cache, stamps[i], 16, 16, (byte)i, alpha: 200);
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 0; i < stamps.Length; i++) File.SetLastWriteTimeUtc(cache.FilePathForTests(stamps[i]), baseTime.AddMinutes(i));

        cache.SetMaxBytes(entry * 6L); // the limit drops below what is stored
        var left = cache.Trim();

        Assert.True(left <= entry * 6L * 9 / 10, $"{left} bytes left of a {entry * 6L} byte limit");
        for (var i = 0; i < 5; i++) Assert.Null(cache.TryGet(stamps[i]));          // the five oldest are gone
        for (var i = 5; i < 10; i++) Assert.NotNull(cache.TryGet(stamps[i]));      // the five newest stay
        Assert.Equal(5, CacheFiles(cache).Length);
    }

    [Fact]
    public void Reading_a_thumbnail_refreshes_its_place_in_the_least_recently_used_order()
    {
        var entry = 64 + 16 * 16 * 4;
        var cache = NewCache(entry * 100L, clamp: false);
        var stamps = Enumerable.Range(0, 10).Select(i => cache.StampFor(Source($"q{i}.png"), 100)).ToArray();
        for (var i = 0; i < stamps.Length; i++) Put(cache, stamps[i], 16, 16, (byte)i, alpha: 200);
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 0; i < stamps.Length; i++) File.SetLastWriteTimeUtc(cache.FilePathForTests(stamps[i]), baseTime.AddMinutes(i));

        using (cache.TryGet(stamps[0])) { } // the oldest, read just now

        cache.SetMaxBytes(entry * 6L);
        cache.Trim();

        Assert.NotNull(cache.TryGet(stamps[0]));
        Assert.Null(cache.TryGet(stamps[1]));
    }

    [Fact]
    public void Storing_past_the_limit_trims_in_the_background_to_90_percent()
    {
        var entry = 64 + 16 * 16 * 4;
        var cache = NewCache(entry * 10L, clamp: false);
        for (var i = 0; i < 40; i++)
        {
            var stamp = cache.StampFor(Source($"r{i}.png"), 100);
            cache.Put(stamp, 16, 16, Fill(16, 16, (byte)i, 200), 64);
            cache.WaitForWrites(TimeSpan.FromSeconds(10));
        }
        cache.Trim(); // a background trim may still be running: this one waits for it and settles the total
        var (files, bytes) = cache.Measure();
        Assert.True(bytes <= entry * 10L, $"{bytes} bytes in {files} files, limit {entry * 10L}");
        Assert.True(files >= 5, "trimming must not empty the cache");
    }

    [Fact]
    public void Concurrent_readers_and_writers_never_see_a_partial_thumbnail()
    {
        var cache = NewCache();
        var sources = Enumerable.Range(0, 4).Select(i => Source($"c{i}.jpg")).ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            for (var iteration = 0; iteration < 150; iteration++)
            {
                var index = (worker + iteration) % sources.Length;
                var stamp = cache.StampFor(sources[index], 236);
                var expected = (byte)(index * 10);
                using var hit = cache.TryGet(stamp);
                if (hit is null) { cache.Put(stamp, 24, 16, Fill(24, 16, expected), 96); continue; }
                if (hit.Width != 24 || hit.Height != 16) { failures.Add($"size {hit.Width}x{hit.Height}"); continue; }
                var pixels = hit.Pixels;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] != expected || pixels[i + 1] != expected + 1 || pixels[i + 2] != expected + 2 || pixels[i + 3] != 255) { failures.Add($"pixel {i}"); break; }
            }
        });
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(20)));
        Assert.Empty(failures);
        Assert.DoesNotContain(CacheFiles(cache), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.All(sources, s => { using var hit = cache.TryGet(cache.StampFor(s, 236)); Assert.NotNull(hit); });
    }

    [Fact]
    public void Truncated_foreign_and_inconsistent_files_are_misses_and_are_removed()
    {
        var cache = NewCache();
        var stamp = cache.StampFor(Source("d.jpg"), 236);
        Put(cache, stamp, 12, 12, 5, alpha: 100);
        var file = cache.FilePathForTests(stamp);
        var good = File.ReadAllBytes(file);

        File.WriteAllBytes(file, good[..40]); // cut inside the header
        Assert.Null(cache.TryGet(stamp));
        Assert.False(File.Exists(file));

        File.WriteAllBytes(file, good[..^10]); // pixels cut short
        Assert.Null(cache.TryGet(stamp));
        Assert.False(File.Exists(file));

        var wrongMagic = (byte[])good.Clone();
        wrongMagic[0] ^= 0xFF;
        File.WriteAllBytes(file, wrongMagic);
        Assert.Null(cache.TryGet(stamp));

        var wrongPicture = (byte[])good.Clone();
        wrongPicture[24] ^= 0x01; // the recorded source size no longer matches
        File.WriteAllBytes(file, wrongPicture);
        Assert.Null(cache.TryGet(stamp));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Clear_removes_only_cache_files_and_works_without_an_instance()
    {
        var cache = NewCache();
        for (var i = 0; i < 3; i++) Put(cache, cache.StampFor(Source($"k{i}.jpg"), 100), 8, 8, (byte)i);
        var foreign = Path.Combine(cache.Directory, "notes.txt");
        File.WriteAllText(foreign, "keep me");

        Assert.Equal(3, cache.Clear());
        Assert.Equal((0, 0L), cache.Measure());
        Assert.True(File.Exists(foreign));

        Put(cache, cache.StampFor(Source("k9.jpg"), 100), 8, 8, 9);
        Assert.Equal(1, ThumbnailDiskCache.ClearDirectory(cache.Directory));
        Assert.Equal(0, ThumbnailDiskCache.ClearDirectory(_dir.Combine("never-created")));
    }

    [Fact]
    public void Oversized_empty_and_unstamped_thumbnails_are_ignored()
    {
        var cache = NewCache();
        var stamp = cache.StampFor(Source("e.jpg"), 100);
        cache.Put(stamp, 4096, 4096, new byte[4096 * 4096 * 4], 4096 * 4);
        cache.Put(stamp, 0, 0, [], 0);
        cache.Put(stamp, 8, 8, new byte[10], 32);              // buffer shorter than width x height
        cache.Put(default, 8, 8, Fill(8, 8, 1), 32);
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(5)));
        Assert.Empty(CacheFiles(cache));
    }

    [Fact]
    public void The_size_limit_is_kept_within_sane_bounds()
    {
        Assert.Equal(ThumbnailDiskCache.MinMaxBytes, ThumbnailDiskCache.ClampMaxBytes(1));
        Assert.Equal(ThumbnailDiskCache.MaxMaxBytes, ThumbnailDiskCache.ClampMaxBytes(long.MaxValue));
        Assert.Equal(512L * 1024 * 1024, ThumbnailDiskCache.DefaultMaxBytes);
        Assert.Equal("thumbnails", Glide.Core.Reset.ResetCatalog.ThumbnailCacheFolderName);
        Assert.Equal(ThumbnailDiskCache.FolderName, Glide.Core.Reset.ResetCatalog.ThumbnailCacheFolderName);
    }
}
