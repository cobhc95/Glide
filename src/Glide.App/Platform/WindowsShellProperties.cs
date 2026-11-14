using System.Runtime.InteropServices;

namespace Glide.App.Platform;

/// <summary>Shows the Windows shell "Properties" dialog for a file or folder.</summary>
internal static partial class WindowsShellProperties
{
    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const int SwShowNormal = 1;

    /// <summary>Test seam: when set, <see cref="Show"/> calls this instead of the shell.</summary>
    public static Func<string, bool>? Launcher { get; set; }

    /// <summary>
    /// Opens the Properties dialog. The dialog is owned by a shell thread, so this returns promptly
    /// (SEE_MASK_NOASYNC is deliberately not set); the process must simply stay alive.
    /// Call from the UI (STA) thread. Returns false when unsupported, the path is missing, or the shell fails.
    /// </summary>
    public static bool Show(string path)
    {
        if (Launcher is { } launcher) return launcher(path);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!File.Exists(full) && !Directory.Exists(full)) return false;

        var verb = IntPtr.Zero;
        var file = IntPtr.Zero;
        try
        {
            verb = Marshal.StringToHGlobalUni("properties");
            file = Marshal.StringToHGlobalUni(full);
            var info = new ShellExecuteInfo
            {
                cbSize = (uint)Marshal.SizeOf<ShellExecuteInfo>(),
                fMask = SeeMaskInvokeIdList | SeeMaskFlagNoUi,
                lpVerb = verb,
                lpFile = file,
                nShow = SwShowNormal,
            };
            return ShellExecuteExW(ref info);
        }
        catch (Exception) { return false; }
        finally
        {
            if (verb != IntPtr.Zero) Marshal.FreeHGlobal(verb);
            if (file != IntPtr.Zero) Marshal.FreeHGlobal(file);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpFile;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public IntPtr lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ref ShellExecuteInfo info);
}
