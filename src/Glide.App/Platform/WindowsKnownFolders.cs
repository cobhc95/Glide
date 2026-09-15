using System.Runtime.InteropServices;

namespace Glide.App.Platform;

internal static class WindowsKnownFolders
{
    // FOLDERID_Downloads
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

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
            catch { }
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
