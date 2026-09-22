using System.Runtime.InteropServices;

namespace Glide.App.Printing;

/// <summary>
/// Opens the printer driver's native Properties/Preferences sheet (the same sheet every
/// mature viewer exposes) and reports the resulting orientation so Glide's own radio +
/// preview stay in sync. P/Invoke only; no WinForms dependency. Used exclusively from the
/// Print dialog.
///
/// The DEVMODE is seeded by the driver with DM_OUT_BUFFER, edited with DM_IN_PROMPT, then
/// read back directly — the previous GetHdevmode/SetHdevmode copy was size-fragile and could
/// corrupt the heap (observed as a crash when the sheet changed orientation).
/// </summary>
public static class PrintDriverProperties
{
    private const int DM_IN_PROMPT = 0x0004;
    private const int DM_OUT_BUFFER = 0x0002;
    private const int IDOK = 1;
    // DEVMODE field offsets (Unicode): dmDeviceName[32] then 4 WORDs then dmFields DWORD.
    private const int OffsetDmOrientation = 76; // union begins after dmFields
    private const short DmOrientationLandscape = 2;

    public static bool Show(nint ownerHwnd, string printerName, out bool landscape)
    {
        landscape = false;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(printerName)) return false;
        nint hPrinter = IntPtr.Zero;
        nint devMode = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero) || hPrinter == IntPtr.Zero) return false;
            var needed = DocumentProperties(ownerHwnd, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (needed <= 0) return false;
            devMode = Marshal.AllocHGlobal(needed);
            // Seed with the driver's current settings so the sheet opens on live values.
            if (DocumentProperties(ownerHwnd, hPrinter, printerName, devMode, IntPtr.Zero, DM_OUT_BUFFER) < 0)
                return false;
            // Show the modal sheet and write the accepted values back into devMode.
            var result = DocumentProperties(ownerHwnd, hPrinter, printerName, devMode, devMode, DM_IN_PROMPT | DM_OUT_BUFFER);
            if (result != IDOK) return false;
            var orientation = Marshal.ReadInt16(devMode, OffsetDmOrientation);
            landscape = orientation == DmOrientationLandscape;
            return true;
        }
        catch { return false; }
        finally
        {
            if (devMode != IntPtr.Zero) Marshal.FreeHGlobal(devMode);
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out nint phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(nint hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int DocumentProperties(
        nint hwnd, nint hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);
}
