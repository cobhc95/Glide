using System.Runtime.InteropServices;

namespace Glide.App.Platform;

/// <summary>
/// DWM cloaking for a top-level HWND (Windows 8+). A cloaked window is still rendered and composed by DWM but not
/// shown, and the change takes effect on DWM's next composition without waiting for the app's own render loop.
/// Glide uses it to hide the one or two frames in which a fullscreen enter/exit would otherwise show the previous
/// frame stretched or clipped to the new window rectangle. Unlike <c>Window.Opacity = 0</c> it needs no extra
/// render pass to hide and none to reveal, and it never touches the user's window-transparency value.
/// </summary>
internal static class WindowCloak
{
    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    public static bool TrySet(IntPtr hwnd, bool cloaked)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;
        try
        {
            var value = cloaked ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // no DWM API: the transition simply runs unfenced
        }
    }

    /// <summary>Blocks until DWM's next composition pass (call off the UI thread).</summary>
    public static void Flush()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _ = DwmFlush(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // no DWM API: nothing to wait for
        }
    }
}
