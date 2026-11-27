using Glide.App.Settings;
using Glide.Core.Settings;
using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>
/// Connects the Explorer thumbnail disk cache (<see cref="ThumbnailDiskCache"/>) to Settings: the on/off switch
/// (explorer.thumbnailCache), the size limit in megabytes (explorer.thumbnailCacheMaxMb) and the folder. The live settings
/// publisher calls <see cref="Apply"/> on every publish (startup, preview, apply), so the cache always follows the settings.
/// </summary>
internal static class ThumbnailCacheSettings
{
    public const int MinMegabytes = 32;
    public const int MaxMegabytes = 8192;
    public const int DefaultMegabytes = 512;

    public static int ClampMegabytes(int megabytes) => Math.Clamp(megabytes, MinMegabytes, MaxMegabytes);

    /// <summary>
    /// The cache folder: <c>%LOCALAPPDATA%\Glide\thumbnails</c>, or <c>thumbnails</c> inside the settings folder when that folder
    /// is redirected (GLIDE_SETTINGS_DIRECTORY, an isolated test or diagnostic profile), so those never touch the real cache.
    /// </summary>
    public static string Directory => SettingsStore.GetThumbnailCacheDirectory();

    /// <summary>Starts, resizes or stops the cache for these settings.</summary>
    public static void Apply(GlideSettingsState settings)
    {
        try
        {
            var options = new ExplorerOptions(settings);
            ThumbnailDiskCache.Configure(options.ThumbnailCache, Directory, ClampMegabytes(options.ThumbnailCacheMaxMb) * 1024L * 1024L);
        }
        catch { /* a cache that cannot start only means thumbnails decode every time */ }
    }

    /// <summary>Files and bytes in the cache folder right now.</summary>
    public static (int Files, long Bytes) Usage()
    {
        var cache = ThumbnailDiskCache.Current ?? new ThumbnailDiskCache(Directory, ThumbnailDiskCache.DefaultMaxBytes);
        return cache.Measure();
    }

    /// <summary>Deletes every cached thumbnail (also while the cache is switched off). Returns the number of files removed.</summary>
    public static int Clear()
    {
        if (ThumbnailDiskCache.Current is { } live) return live.Clear();
        return ThumbnailDiskCache.ClearDirectory(Directory);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024d:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:0.##} GB"
    };
}
