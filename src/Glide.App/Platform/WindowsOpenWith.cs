using System.Runtime.InteropServices;

namespace Glide.App.Platform;

internal static class WindowsOpenWith
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO { [MarshalAs(UnmanagedType.LPWStr)] public string pcszFile; [MarshalAs(UnmanagedType.LPWStr)] public string? pcszClass; public uint oaifInFlags; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);
    private const uint OAIF_EXEC = 0x00000004;
    public static void Show(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try { var info = new OPENASINFO { pcszFile = path, pcszClass = null, oaifInFlags = OAIF_EXEC }; _ = SHOpenWithDialog(IntPtr.Zero, ref info); } catch { }
    }
}
