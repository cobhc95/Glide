using System.Runtime.InteropServices;

namespace Glide.App.Platform;

/// <summary>
/// Clears this user's Windows thumbnail cache and asks the shell to rebuild thumbnails. Shared by
/// Settings > Explorer thumbnails > Refresh thumbnails and the installer (--refresh-thumbnails).
/// </summary>
internal static class ThumbnailCacheRefresher
{
    /// <summary>Deletes the per-user thumbcache databases (best-effort; Explorer may hold some open)
    /// and sends SHCNE_ASSOCCHANGED. Returns the number of cache files removed.</summary>
    public static int Refresh()
    {
        var removed = 0;
        try
        {
            var cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                              "Microsoft", "Windows", "Explorer");
            if (Directory.Exists(cacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(cacheDirectory, "thumbcache_*.db"))
                {
                    try { File.Delete(file); removed++; } catch { /* a locked cache file is skipped and not counted as removed */ }
                }
            }
        }
        catch (Exception loggedEx) { Glide.Diagnostics.Runtime.LiveDiagnosticTrace.WriteException("thumbnails", "cache_clear_failed", loggedEx); }
        try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { /* shell refresh notification is cosmetic */ }
        return removed;
    }

    /// <summary>
    /// Installer step run as the installing (non-elevated) user: records the installer's Explorer
    /// thumbnails choice in this user's thumbnail settings (so the Settings toggle matches), removes this
    /// user's own provider entries when turned off, then refreshes the thumbnail cache.
    /// </summary>
    public static int RunInstallerStep(bool enabled, out string message)
    {
        var settings = ThumbnailSettingsStore.Load() with { Enabled = enabled };
        ThumbnailSettingsStore.Save(settings);
        if (!enabled)
            WindowsThumbnailRegistration.Unregister(perUser: true, Glide.Core.ImageFormatRegistry.Extensions, out _);
        var removed = Refresh();
        message = $"Explorer thumbnails {(enabled ? "on" : "off")}; thumbnail cache refresh requested ({removed} cache files removed).";
        return 0;
    }

    /// <summary>Parses "--refresh-thumbnails [--disabled]". Null when the switch is absent.</summary>
    internal static bool? ParseRefreshSwitch(string[] args)
    {
        if (args.Length < 1 || !args[0].Equals("--refresh-thumbnails", StringComparison.OrdinalIgnoreCase)) return null;
        return !args.Skip(1).Any(a => a.Equals("--disabled", StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
