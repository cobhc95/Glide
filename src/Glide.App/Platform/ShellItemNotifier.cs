using System.Runtime.InteropServices;

namespace Glide.App.Platform;

/// <summary>
/// Tells Explorer that a file Glide wrote has new content, so open folder windows and the thumbnail
/// cache refresh it. Without this a rotated/edited image keeps its old thumbnail: Save writes a temp
/// file and swaps it in with File.Replace, which Explorer does not treat as a content change.
/// </summary>
internal static class ShellItemNotifier
{
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlushNoWait = 0x3000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, [MarshalAs(UnmanagedType.LPWStr)] string dwItem1, IntPtr dwItem2);

    public static void FileContentChanged(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return;
        try { SHChangeNotify(ShcneUpdateItem, ShcnfPathW | ShcnfFlushNoWait, Path.GetFullPath(path), IntPtr.Zero); }
        catch { /* the refresh is cosmetic; the save itself already succeeded */ }
    }
}
