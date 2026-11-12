using System.Runtime.InteropServices;

namespace Glide.App.Platform;

/// <summary>
/// Gives an individual top-level window its own AppUserModelID. Windows groups taskbar buttons by
/// AppUserModelID, so a distinct id per window makes each Glide window appear as a separate taskbar
/// button instead of being combined into one. The first (primary) window keeps the process default so
/// taskbar pinning still groups with the pinned Glide shortcut.
/// </summary>
internal static unsafe class WindowsTaskbarIdentity
{
    // IPropertyStore is driven through its raw vtable. The previous source-generated COM wrapper
    // (ComObject + manual FinalRelease) threw a NullReferenceException from ComObject.Finalize() on
    // the finalizer thread, which NativeAOT turns into a process-wide fast-fail (0xc0000409) some
    // time after a second window was created. No managed COM wrapper means nothing to finalize.
    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private const ushort VtLpwstr = 31;

    // IUnknown: 0 QueryInterface, 1 AddRef, 2 Release. IPropertyStore: 3 GetCount, 4 GetAt,
    // 5 GetValue, 6 SetValue, 7 Commit.
    private const int SlotRelease = 2, SlotGetValue = 5, SlotSetValue = 6, SlotCommit = 7;

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IntPtr propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private static IntPtr Slot(IntPtr store, int index) => ((IntPtr*)*(IntPtr*)store)[index];

    private static void ReleaseStore(IntPtr store)
    {
        if (store != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(store, SlotRelease))(store);
    }

    private static IntPtr TryGetStore(IntPtr hwnd)
    {
        var iid = IidPropertyStore;
        return SHGetPropertyStoreForWindow(hwnd, ref iid, out var pointer) == 0 ? pointer : IntPtr.Zero;
    }

    /// <summary>Reads the window's AppUserModelID (used by diagnostics and the round-trip test).</summary>
    public static string? TryGetWindowAppUserModelId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var store = IntPtr.Zero;
        try
        {
            store = TryGetStore(hwnd);
            if (store == IntPtr.Zero) return null;
            var key = AppUserModelIdKey;
            PropVariant value = default;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)Slot(store, SlotGetValue))(store, &key, &value);
            if (hr < 0) return null;
            try
            {
                return value.VariantType == VtLpwstr && value.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Pointer)
                    : null;
            }
            finally { PropVariantClear(ref value); }
        }
        catch { return null; }
        finally { ReleaseStore(store); }
    }

    /// <summary>
    /// Sets the window's AppUserModelID. Best-effort: any failure (including a shell that refuses the
    /// property) is swallowed so taskbar identity can never destabilise a window.
    /// </summary>
    public static bool TrySetWindowAppUserModelId(IntPtr hwnd, string appUserModelId)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(appUserModelId)) return false;
        var store = IntPtr.Zero;
        var pointer = IntPtr.Zero;
        try
        {
            store = TryGetStore(hwnd);
            if (store == IntPtr.Zero) return false;
            var key = AppUserModelIdKey;
            pointer = Marshal.StringToCoTaskMemUni(appUserModelId);
            var value = new PropVariant { VariantType = VtLpwstr, Pointer = pointer };
            if (((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)Slot(store, SlotSetValue))(store, &key, &value) < 0) return false;
            return ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(store, SlotCommit))(store) >= 0;
        }
        catch { return false; }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            ReleaseStore(store);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    // PROPVARIANT layout: 8-byte header (VARTYPE + 3 reserved words) followed by the union, which
    // holds the LPWSTR at offset 8 on both x86 and x64.
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
