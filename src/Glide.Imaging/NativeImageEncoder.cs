using System.Runtime.InteropServices;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Windows WIC encoder bridge (Glide.Native): PNG, JPEG, BMP, TIFF, GIF and JPEG XR on every Windows, plus HEIC,
/// AVIF and JPEG XL when a Windows codec extension registers an encoder (probed lazily, see <see cref="IsFormatAvailable"/>).
/// </summary>
public static partial class NativeImageEncoder
{
    /// <summary>GlideEncodeImage2W flags.</summary>
    [Flags]
    public enum EncodeFlags : uint
    {
        None = 0,
        CopyIcc = 1,
        CopyExifXmp = 2,
        Lossless = 4,
    }

    /// <summary>Maps an extension to the original 4.x format id (PNG/JPEG/BMP/TIFF), or -1 when unsupported.</summary>
    public static int FormatId(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => 0,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => 1,
        ".bmp" or ".dib" => 2,
        ".tif" or ".tiff" => 3,
        _ => -1
    };

    /// <summary>The original PNG/JPEG/BMP/TIFF set; the full writable list is <see cref="Glide.Imaging.Encoders.SaveFormatAvailability"/>.</summary>
    public static bool CanEncode(string extension) => FormatId(extension) >= 0;

    public static bool TryEncode(string path, uint width, uint height, uint stride, IntPtr pixels, string extension)
        => TryEncode(path, width, height, stride, pixels, extension, 0.95f);

    /// <param name="quality">JPEG quality 0..1 (ignored for other formats).</param>
    public static bool TryEncode(string path, uint width, uint height, uint stride, IntPtr pixels, string extension, float quality)
    {
        var format = FormatId(extension);
        return format >= 0 && TryEncodeFile(path, width, height, stride, pixels, format, quality, EncodeFlags.None, null);
    }

    /// <summary>Writes premultiplied BGRA to <paramref name="path"/> as WIC format <paramref name="wicFormat"/> (SaveFormatCatalog.Wic*).</summary>
    public static bool TryEncodeFile(string path, uint width, uint height, uint stride, IntPtr pixels, int wicFormat, float quality,
        EncodeFlags flags, string? metadataSource)
    {
        if (!OperatingSystem.IsWindows() || pixels == IntPtr.Zero || wicFormat < 0) return false;
        try
        {
            return GlideEncodeImage2W(path, width, height, stride, pixels, (uint)wicFormat, quality, (uint)flags, metadataSource, out _, out _) == 1;
        }
        catch (EntryPointNotFoundException)
        {
            // Older native bridge (before 5.0.4): PNG/JPEG/BMP/TIFF without metadata.
            if (wicFormat > 3) return false;
            try { return GlideEncodeImageExW(path, width, height, stride, pixels, (uint)wicFormat, quality) == 1; }
            catch (EntryPointNotFoundException)
            {
                if (wicFormat > 2) return false;
                try { return GlideEncodeImageW(path, width, height, stride, pixels, (uint)wicFormat) == 1; }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException) { return false; }
        }
        catch (DllNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>Encodes premultiplied BGRA (tightly packed) into memory, e.g. the PNG inside an ICO or OpenRaster file. Null on failure.</summary>
    public static byte[]? TryEncodeToMemory(byte[] premultipliedBgra, int width, int height, int wicFormat, float quality = 0.95f)
    {
        if (!OperatingSystem.IsWindows() || width <= 0 || height <= 0 || premultipliedBgra.Length < (long)width * height * 4) return null;
        var handle = GCHandle.Alloc(premultipliedBgra, GCHandleType.Pinned);
        nint data = 0;
        try
        {
            if (GlideEncodeImage2W(null, (uint)width, (uint)height, (uint)(width * 4), handle.AddrOfPinnedObject(), (uint)wicFormat, quality,
                    0, null, out data, out var size) != 1 || data == 0 || size == 0 || size > int.MaxValue)
                return null;
            var bytes = new byte[(int)size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
        finally
        {
            handle.Free();
            if (data != 0) GlideFreeImageBuffer(data);
        }
    }

    private static readonly Lazy<bool>[] Availability = Enumerable.Range(0, SaveFormatCatalog.WicJxl + 1)
        .Select(id => new Lazy<bool>(() => Probe(id), LazyThreadSafetyMode.ExecutionAndPublication)).ToArray();

    /// <summary>
    /// True when this PC can write WIC format <paramref name="wicFormat"/>. Probed once per format on first use (never at
    /// startup): WIC component enumeration, plus a small trial encode for HEIC/AVIF/JPEG XL, whose encoders come
    /// from optional Windows Store extensions.
    /// </summary>
    public static bool IsFormatAvailable(int wicFormat)
        => wicFormat >= 0 && wicFormat < Availability.Length && Availability[wicFormat].Value;

    private static bool Probe(int wicFormat)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return GlideProbeEncoderW((uint)wicFormat) == 1; }
        catch (EntryPointNotFoundException) { return wicFormat <= 3; } // pre-5.0.4 bridge: the original four
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException) { return false; }
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideEncodeImageW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideEncodeImageW(string path, uint width, uint height, uint stride, IntPtr pixels, uint format);

    [LibraryImport("Glide.Native", EntryPoint = "GlideEncodeImageExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideEncodeImageExW(string path, uint width, uint height, uint stride, IntPtr pixels, uint format, float quality);

    [LibraryImport("Glide.Native", EntryPoint = "GlideEncodeImage2W", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideEncodeImage2W(string? path, uint width, uint height, uint stride, IntPtr pixels, uint format, float quality,
        uint flags, string? metadataSource, out nint data, out ulong size);

    [LibraryImport("Glide.Native", EntryPoint = "GlideProbeEncoderW")]
    [return: MarshalAs(UnmanagedType.I4)]
    private static partial int GlideProbeEncoderW(uint format);

    [LibraryImport("Glide.Native", EntryPoint = "GlideFreeImageBuffer")]
    private static partial void GlideFreeImageBuffer(nint data);
}
