using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Imaging;
using Glide.Tests.Support;
using SkiaSharp;

namespace Glide.Core.Tests;

/// <summary>
/// The Explorer thumbnail decode with the persistent cache switched on: the second request is served from the cache without
/// decoding, a changed picture is decoded again, and the Settings switch/limit start, resize and stop the shared cache.
/// These touch the process-wide <see cref="ThumbnailDiskCache.Current"/>, so they run in the headless collection (serialised
/// with the tests that build windows and publish settings).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Thumbnails")]
public sealed class ThumbnailDiskCacheDecodeTests : IDisposable
{
    private readonly TempDir _dir = new("glide-thumbcache-decode");
    private readonly List<string> _decoded = new();

    public ThumbnailDiskCacheDecodeTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
        ThumbnailDiskCache.Configure(false, null, 0);
        ThumbnailDecode.DecodingForTests = path => { lock (_decoded) _decoded.Add(path); };
    }

    public void Dispose()
    {
        ThumbnailDiskCache.Configure(false, null, 0);
        ThumbnailDecode.DecodingForTests = null;
        _dir.Dispose();
    }

    private string Png(string name, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = _dir.Combine(name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    private ThumbnailDiskCache UseCache()
    {
        ThumbnailDiskCache.Configure(true, _dir.Combine("cache"), 64L * 1024 * 1024);
        return ThumbnailDiskCache.Current!;
    }

    [Fact]
    public void The_second_request_for_a_thumbnail_is_served_from_the_disk_cache_without_decoding()
    {
        var cache = UseCache();
        var path = Png("photo.png", 800, 600, SKColors.SteelBlue);

        using (var first = ThumbnailDecode.DecodeToWidth(path, 200))
        {
            Assert.NotNull(first);
            Assert.Equal(200, first!.PixelSize.Width);
        }
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));
        Assert.Single(_decoded);

        using var second = ThumbnailDecode.DecodeToWidth(path, 200);
        Assert.NotNull(second);
        Assert.Equal(new PixelSize(200, 150), second!.PixelSize);
        Assert.Single(_decoded); // the cache answered: nothing was decoded
        var (b, g, r, a) = Pixel(second, 100, 75);
        Assert.Equal((SKColors.SteelBlue.Blue, SKColors.SteelBlue.Green, SKColors.SteelBlue.Red, (byte)255), (b, g, r, a));
        Assert.True(cache.Hits >= 1);
    }

    [Fact]
    public void A_picture_that_changed_since_it_was_cached_is_decoded_again()
    {
        var cache = UseCache();
        var path = Png("edit.png", 640, 480, SKColors.Red);
        using (ThumbnailDecode.DecodeToWidth(path, 160)) { }
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));

        File.Delete(path);
        Png("edit.png", 640, 480, SKColors.Lime); // same name, new content and modified time
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(3));
        using var after = ThumbnailDecode.DecodeToWidth(path, 160);

        Assert.Equal(2, _decoded.Count);
        var (_, g, r, _) = Pixel(after!, 80, 60);
        Assert.True(g > 200 && r < 60, $"the new picture's green must show, got r={r} g={g}");
    }

    [Fact]
    public void A_request_for_another_width_is_a_separate_cache_entry()
    {
        var cache = UseCache();
        var path = Png("sizes.png", 800, 600, SKColors.Orange);
        using (ThumbnailDecode.DecodeToWidth(path, 200)) { }
        using (ThumbnailDecode.DecodeToWidth(path, 100)) { }
        Assert.True(cache.WaitForWrites(TimeSpan.FromSeconds(10)));
        using (var small = ThumbnailDecode.DecodeToWidth(path, 100)) Assert.Equal(100, small!.PixelSize.Width);
        using (var large = ThumbnailDecode.DecodeToWidth(path, 200)) Assert.Equal(200, large!.PixelSize.Width);
        Assert.Equal(2, _decoded.Count);
        Assert.Equal(2, cache.Measure().Files);
    }

    [Fact]
    public void With_the_cache_off_every_request_decodes_and_nothing_is_written()
    {
        var path = Png("off.png", 800, 600, SKColors.Teal);
        using (ThumbnailDecode.DecodeToWidth(path, 200)) { }
        using (ThumbnailDecode.DecodeToWidth(path, 200)) { }
        Assert.Equal(2, _decoded.Count);
        Assert.False(Directory.Exists(_dir.Combine("cache")));
    }

    [Fact]
    public void Settings_start_resize_and_stop_the_shared_cache()
    {
        var folder = Path.GetFullPath(_dir.Combine("settings-cache"));
        var settings = new GlideSettingsState();
        Assert.True(settings.ExplorerThumbnailCache);                    // on by default
        Assert.Equal(512, settings.ExplorerThumbnailCacheMaxMb);

        ThumbnailDiskCache.Configure(settings.ExplorerThumbnailCache, folder, ThumbnailCacheSettings.ClampMegabytes(settings.ExplorerThumbnailCacheMaxMb) * 1024L * 1024L);
        Assert.NotNull(ThumbnailDiskCache.Current);
        Assert.Equal(folder, ThumbnailDiskCache.Current!.Directory);
        Assert.Equal(512L * 1024 * 1024, ThumbnailDiskCache.Current.MaxBytes);

        var same = ThumbnailDiskCache.Current;
        ThumbnailDiskCache.Configure(true, folder, 128L * 1024 * 1024);      // a new limit keeps the same cache
        Assert.Same(same, ThumbnailDiskCache.Current);
        Assert.Equal(128L * 1024 * 1024, ThumbnailDiskCache.Current!.MaxBytes);

        ThumbnailDiskCache.Configure(false, folder, 128L * 1024 * 1024);
        Assert.Null(ThumbnailDiskCache.Current);

        Assert.Equal(32, ThumbnailCacheSettings.ClampMegabytes(0));
        Assert.Equal(8192, ThumbnailCacheSettings.ClampMegabytes(int.MaxValue));
    }

    [Fact]
    public void Publishing_settings_applies_the_thumbnail_cache_switch_and_limit()
    {
        var previous = SettingsService.Current;
        SettingsStore.SettingsDirectoryOverrideForTests.Value = _dir.Combine("profile");
        try
        {
            var on = new GlideSettingsState { ExplorerThumbnailCache = true, ExplorerThumbnailCacheMaxMb = 200 };
            SettingsService.Publish(on);
            Assert.NotNull(ThumbnailDiskCache.Current);
            Assert.Equal(200L * 1024 * 1024, ThumbnailDiskCache.Current!.MaxBytes);
            // An isolated settings folder keeps its cache next to its settings, never in the real %LOCALAPPDATA%.
            Assert.StartsWith(Path.GetFullPath(_dir.Combine("profile")), ThumbnailDiskCache.Current.Directory, StringComparison.OrdinalIgnoreCase);

            SettingsService.Publish(new GlideSettingsState { ExplorerThumbnailCache = false });
            Assert.Null(ThumbnailDiskCache.Current);
        }
        finally
        {
            SettingsStore.SettingsDirectoryOverrideForTests.Value = null;
            if (previous is not null) SettingsService.Publish(previous);
            ThumbnailDiskCache.Configure(false, null, 0);
        }
    }

    [Fact]
    public void The_thumbnail_cache_settings_are_catalogued_with_their_defaults_and_in_the_clear_caches_reset()
    {
        var cache = SettingsCatalog.All.Single(s => s.Id == "explorer.thumbnailCache");
        Assert.Equal(SettingKind.Toggle, cache.Kind);
        Assert.Equal(true, cache.DefaultValue);
        var limit = SettingsCatalog.All.Single(s => s.Id == "explorer.thumbnailCacheMaxMb");
        Assert.Equal(512, limit.DefaultValue);
        Assert.Equal(SettingKind.Action, SettingsCatalog.All.Single(s => s.Id == "explorer.clearThumbnailCache").Kind);

        var environment = new Glide.Core.Reset.ResetEnvironment(
            SettingsDirectory: _dir.Combine("settings"), LocalGlideDirectory: _dir.Combine("local"), TempDirectory: _dir.Combine("temp"),
            InstallDirectory: _dir.Combine("install"), ImageExtensions: [".jpg"], SettingsSchemaVersion: 1, DefaultSettingsJson: "{}");
        var targets = Glide.Core.Reset.ResetPlanner.TargetsFor(environment, Glide.Core.Data.ResetItemId.Caches);
        Assert.Contains(targets, t => t is Glide.Core.Reset.DirectoryTarget d && d.Path == Path.Combine(environment.LocalGlideDirectory, "thumbnails"));
        Assert.Contains(targets, t => t is Glide.Core.Reset.DirectoryTarget d && d.Path == Path.Combine(environment.SettingsDirectory, "thumbnails"));
    }
}
