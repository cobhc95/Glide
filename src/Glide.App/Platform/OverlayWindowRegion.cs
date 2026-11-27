using System.Runtime.InteropServices;
using Glide.App.Controls;

namespace Glide.App.Platform;

/// <summary>SetWindowRgn for Overlay mode's window: cuts the transparent HWND to its visible frames, or removes the region.</summary>
internal static class OverlayWindowRegion
{
    private const int RgnOr = 2;

    /// <summary>
    /// Sets the window region to the union of <paramref name="rects"/> (window pixels), or clears it for null / empty.
    /// Returns false when the region could not be set (the previous region stays in force).
    /// </summary>
    public static bool Apply(IntPtr hwnd, IReadOnlyList<RegionRect>? rects)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;
        if (rects is null || rects.Count == 0) return SetWindowRgn(hwnd, IntPtr.Zero, true) != 0;
        var region = IntPtr.Zero;
        try
        {
            foreach (var r in rects)
            {
                var piece = CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
                if (piece == IntPtr.Zero) continue;
                if (region == IntPtr.Zero) { region = piece; continue; }
                CombineRgn(region, region, piece, RgnOr);
                DeleteObject(piece);
            }
            if (region == IntPtr.Zero) return false;
            // On success the system owns the region handle.
            if (SetWindowRgn(hwnd, region, true) == 0) return false;
            region = IntPtr.Zero;
            return true;
        }
        finally { if (region != IntPtr.Zero) DeleteObject(region); }
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
