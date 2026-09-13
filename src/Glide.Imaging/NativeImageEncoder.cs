using System.Runtime.InteropServices;

namespace Glide.Imaging;

/// <summary>Small optional Windows WIC encoder for real PNG/JPEG/BMP output.</summary>
public static partial class NativeImageEncoder
{
    public static bool TryEncode(string path, uint width, uint height, uint stride, IntPtr pixels, string extension)
    {
        if (!OperatingSystem.IsWindows() || pixels == IntPtr.Zero) return false;
        var format = extension.ToLowerInvariant() switch { ".png" => 0u, ".jpg" or ".jpeg" => 1u, ".bmp" => 2u, _ => 99u };
        if (format > 2) return false;
        try { return GlideEncodeImageW(path, width, height, stride, pixels, format) == 1; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideEncodeImageW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideEncodeImageW(string path, uint width, uint height, uint stride, IntPtr pixels, uint format);
}
