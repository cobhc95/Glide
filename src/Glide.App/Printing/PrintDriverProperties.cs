using System.Drawing.Printing;
using System.Runtime.InteropServices;

#pragma warning disable CA1416 // PrintDriverProperties is Windows-only by design; Show() requires Windows first.

namespace Glide.App.Printing;

/// <summary>
/// Opens the printer driver's native Properties/Preferences sheet (the same sheet every
/// mature viewer exposes) and writes the result back into the live PrintDocument settings.
/// P/Invoke only; no WinForms dependency. Used exclusively from the Print dialog.
/// </summary>
public static class PrintDriverProperties
{
    private const int DM_IN_PROMPT = 0x0004;
    private const int DM_OUT_BUFFER = 0x0002;
    private const int IDOK = 1;

    public static bool Show(nint ownerHwnd, PrintDocument document)
    {
        if (!OperatingSystem.IsWindows() || document is null) return false;
        var printerName = document.PrinterSettings.PrinterName;
        if (string.IsNullOrWhiteSpace(printerName)) return false;
        try
        {
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero)) return false;
            try
            {
                // Query required DEVMODE size, then show the driver modal.
                var needed = DocumentProperties(ownerHwnd, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
                if (needed <= 0) return false;
                var devMode = Marshal.AllocHGlobal(needed);
                try
                {
                    // Seed with the current settings so the sheet opens on the live values.
                    var seed = document.PrinterSettings.GetHdevmode();
                    try { CopyDevMode(seed, devMode, needed); }
                    finally { document.PrinterSettings.SetHdevmode(seed); }
                    var result = DocumentProperties(ownerHwnd, hPrinter, printerName, devMode, devMode,
                        DM_IN_PROMPT | DM_OUT_BUFFER);
                    if (result != IDOK) return false;
                    // Adopt the driver's result back into the live settings.
                    var target = document.PrinterSettings.GetHdevmode();
                    try { CopyDevMode(devMode, target, needed); }
                    finally { document.PrinterSettings.SetHdevmode(target); }
                    return true;
                }
                finally { Marshal.FreeHGlobal(devMode); }
            }
            finally { ClosePrinter(hPrinter); }
        }
        catch { return false; }
    }

    private static void CopyDevMode(nint source, nint destination, int capacity)
    {
        var extra = Marshal.ReadInt16(source, 70); // dmDriverExtra
        var total = Math.Min(capacity, 72 + Math.Max(0, (int)extra));
        if (total <= 0) return;
        var bytes = new byte[total];
        Marshal.Copy(source, bytes, 0, total);
        Marshal.Copy(bytes, 0, destination, total);
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
