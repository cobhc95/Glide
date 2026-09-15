using System.Runtime.InteropServices;

namespace Glide.Imaging;

public static partial class NativeImageProbe
{
    /// <summary>
    /// Native probing is optional in Phase 1. Missing DLL/entrypoint returns false rather than
    /// taking down startup; this keeps the native boundary observable and independently replaceable.
    /// </summary>
    public static bool TryProbe(string path, out NativeImageInfo info)
    {
        info = default;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return GlideProbeImageW(path, out info) == 1;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideProbeImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GlideProbeImageW(string path, out NativeImageInfo info);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeImageInfo
{
    public uint Width;
    public uint Height;
    public uint FrameCount;
    public uint Reserved;
}
