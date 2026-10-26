using System.Runtime.InteropServices;

namespace Glide.Imaging;

/// <summary>Small optional Windows WIC encoder for real PNG/JPEG/BMP/TIFF output.</summary>
public static partial class NativeImageEncoder
{
    /// <summary>Extensions the WIC bridge can write (WIC ships no WebP/GIF-animation encoder we use).</summary>
    public static IReadOnlyList<string> EncodableExtensions { get; } = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    /// <summary>Maps an extension to the native format id, or -1 when unsupported.</summary>
    public static int FormatId(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => 0,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => 1,
        ".bmp" or ".dib" => 2,
        ".tif" or ".tiff" => 3,
        _ => -1
    };

    public static bool CanEncode(string extension) => FormatId(extension) >= 0;

    public static bool TryEncode(string path, uint width, uint height, uint stride, IntPtr pixels, string extension)
        => TryEncode(path, width, height, stride, pixels, extension, 0.95f);

    /// <param name="quality">JPEG quality 0..1 (ignored for other formats).</param>
    public static bool TryEncode(string path, uint width, uint height, uint stride, IntPtr pixels, string extension, float quality)
    {
        if (!OperatingSystem.IsWindows() || pixels == IntPtr.Zero) return false;
        var format = FormatId(extension);
        if (format < 0) return false;
        try { return GlideEncodeImageExW(path, width, height, stride, pixels, (uint)format, quality) == 1; }
        catch (EntryPointNotFoundException)
        {
            // Older native bridge: only PNG/JPEG/BMP at fixed quality.
            if (format > 2) return false;
            try { return GlideEncodeImageW(path, width, height, stride, pixels, (uint)format) == 1; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
        }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideEncodeImageW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideEncodeImageW(string path, uint width, uint height, uint stride, IntPtr pixels, uint format);

    [LibraryImport("Glide.Native", EntryPoint = "GlideEncodeImageExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideEncodeImageExW(string path, uint width, uint height, uint stride, IntPtr pixels, uint format, float quality);
}
