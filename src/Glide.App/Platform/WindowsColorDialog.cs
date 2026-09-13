using System.Runtime.InteropServices;

namespace Glide.App.Platform;

internal static class WindowsColorDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct CHOOSECOLOR
    {
        public int lStructSize; public IntPtr hwndOwner; public IntPtr hInstance; public uint rgbResult;
        public IntPtr lpCustColors; public uint Flags; public IntPtr lCustData; public IntPtr lpfnHook; public string? lpTemplateName;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool ChooseColor(ref CHOOSECOLOR cc);
    private const uint CC_RGBINIT = 0x1, CC_FULLOPEN = 0x2, CC_ANYCOLOR = 0x100;
    public static bool TryChoose(string initialHex, out string hex, IntPtr hwndOwner = default)
    {
        hex = initialHex;
        if (!OperatingSystem.IsWindows()) return false;
        var custom = Marshal.AllocHGlobal(16 * sizeof(uint));
        try
        {
            for (var i = 0; i < 16; i++) Marshal.WriteInt32(custom, i * 4, unchecked((int)0x00FFFFFF));
            var rgb = Parse(initialHex);
            var cc = new CHOOSECOLOR { lStructSize = Marshal.SizeOf<CHOOSECOLOR>(), hwndOwner = hwndOwner, rgbResult = rgb, lpCustColors = custom, Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR };
            if (!ChooseColor(ref cc)) return false;
            var r = cc.rgbResult & 0xff; var g = (cc.rgbResult >> 8) & 0xff; var b = (cc.rgbResult >> 16) & 0xff;
            hex = $"#{r:X2}{g:X2}{b:X2}"; return true;
        }
        finally { Marshal.FreeHGlobal(custom); }
    }
    private static uint Parse(string? hex)
    {
        try { var h=(hex??"#38A9F5").TrimStart('#'); var r=Convert.ToUInt32(h.Substring(0,2),16); var g=Convert.ToUInt32(h.Substring(2,2),16); var b=Convert.ToUInt32(h.Substring(4,2),16); return r | (g<<8) | (b<<16); }
        catch { return 0x00F5A938; }
    }
}
