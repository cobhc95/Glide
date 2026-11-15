using System.Runtime.InteropServices;

namespace Glide.App.Platform;

internal static class WindowsKnownFolders
{
    // FOLDERID_Downloads
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The Downloads folder if it exists; never creates it (used by the Explorer navigation pane).</summary>
    public static string? DownloadsIfExists()
    {
        if (OperatingSystem.IsWindows())
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(in DownloadsId, 0, IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero)
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
                }
            }
            catch { /* shell lookup failed: fall back to the next candidate folder */ }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static string Downloads()
    {
        if (OperatingSystem.IsWindows())
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(in DownloadsId, 0, IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero)
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        Directory.CreateDirectory(path);
                        return path;
                    }
                }
            }
            catch { /* folder cannot be created: fall back to the next candidate folder */ }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            }
        }

        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(string.IsNullOrWhiteSpace(user) ? AppContext.BaseDirectory : user, "Downloads");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
