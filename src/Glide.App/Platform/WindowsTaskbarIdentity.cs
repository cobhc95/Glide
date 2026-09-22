using System.Runtime.InteropServices;

namespace Glide.App.Platform;

/// <summary>
/// Gives an individual top-level window its own AppUserModelID. Windows groups taskbar buttons by
/// AppUserModelID, so a distinct id per window makes each Glide window appear as a separate taskbar
/// button instead of being combined into one. The first (primary) window keeps the process default so
/// taskbar pinning still groups with the pinned Glide shortcut.
/// </summary>
internal static class WindowsTaskbarIdentity
{
    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private const ushort VtLpwstr = 31;

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>Reads the window's AppUserModelID (used by diagnostics and the round-trip test).</summary>
    public static string? TryGetWindowAppUserModelId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        var iid = IidPropertyStore;
        IPropertyStore? store = null;
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is null) return null;
            var key = AppUserModelIdKey;
            store.GetValue(ref key, out var value);
            try
            {
                return value.VariantType == VtLpwstr && value.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Pointer)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Sets the window's AppUserModelID. Best-effort: any failure (including a shell that refuses the
    /// property) is swallowed so taskbar identity can never destabilise a window.
    /// </summary>
    public static bool TrySetWindowAppUserModelId(IntPtr hwnd, string appUserModelId)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(appUserModelId)) return false;

        var iid = IidPropertyStore;
        IPropertyStore? store = null;
        var pointer = IntPtr.Zero;
        try
        {
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is null) return false;
            var key = AppUserModelIdKey;
            pointer = Marshal.StringToCoTaskMemUni(appUserModelId);
            var value = new PropVariant { VariantType = VtLpwstr, Pointer = pointer };
            store.SetValue(ref key, ref value);
            store.Commit();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    // PROPVARIANT layout: 8-byte header (VARTYPE + 3 reserved words) followed by the union, which
    // holds the LPWSTR at offset 8 on both x86 and x64.
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
