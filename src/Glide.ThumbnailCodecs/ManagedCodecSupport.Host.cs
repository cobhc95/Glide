using System.Runtime.InteropServices;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

// The thumbnail library's implementation of the encoded sub-image seam (the viewer compiles
// ManagedCodecSupport.Skia.cs instead). Embedded JPEG/PNG bytes are handed to the native host
// (Glide.ShellThumbnail), which decodes them with WIC and returns straight BGRA, so this library
// carries no image codec of its own. Same contract as the Skia version: orientation is reported,
// never applied; with maxLongestSide > 0 the result may be reduced but stays at least that large
// (or the full image when it is smaller). Without a registered host every call simply fails.
internal static unsafe partial class ManagedCodecSupport
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct HostImage
    {
        public uint Width;
        public uint Height;
        public uint Orientation;
        public uint Reserved;
        public void* Data;   // straight BGRA, stride = Width * 4, released with the host's free callback
        public nuint Size;
    }

    private static delegate* unmanaged[Cdecl]<byte*, nuint, uint, int, HostImage*, int> s_hostDecode;
    private static delegate* unmanaged[Cdecl]<void*, void> s_hostFree;

    internal static void SetHost(delegate* unmanaged[Cdecl]<byte*, nuint, uint, int, HostImage*, int> decode,
                                 delegate* unmanaged[Cdecl]<void*, void> free)
    {
        s_hostFree = free;
        s_hostDecode = decode;
    }

    public static bool TryProbeEncoded(ReadOnlySpan<byte> encoded, out int width, out int height, out int orientation)
    {
        width = height = 0;
        orientation = 1;
        if (!Call(encoded, 0, probeOnly: true, out var result)) return false;
        width = (int)Math.Min(result.Width, int.MaxValue);
        height = (int)Math.Min(result.Height, int.MaxValue);
        orientation = result.Orientation is >= 1 and <= 8 ? (int)result.Orientation : 1;
        return IsValidDimensions(width, height);
    }

    public static bool TryDecodeEncoded(ReadOnlySpan<byte> encoded, int maxLongestSide, out DecodedImage image, out int orientation)
    {
        image = default;
        orientation = 1;
        if (!Call(encoded, maxLongestSide, probeOnly: false, out var result)) return false;
        try
        {
            if (result.Data is null || result.Width > int.MaxValue || result.Height > int.MaxValue) return false;
            var width = (int)result.Width;
            var height = (int)result.Height;
            if (!IsValidDimensions(width, height) || !FitsMemory(width, height)) return false;
            var bytes = (long)width * height * 4;
            if ((ulong)result.Size < (ulong)bytes || bytes > int.MaxValue || !TryAllocatePixels(width, height, out var pixels)) return false;
            new ReadOnlySpan<byte>(result.Data, (int)bytes).CopyTo(pixels);
            orientation = result.Orientation is >= 1 and <= 8 ? (int)result.Orientation : 1;
            image = new DecodedImage(width, height, pixels);
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
        finally
        {
            if (result.Data is not null && s_hostFree is not null) s_hostFree(result.Data);
        }
    }

    private static bool Call(ReadOnlySpan<byte> encoded, int maxLongestSide, bool probeOnly, out HostImage result)
    {
        result = default;
        var decode = s_hostDecode;
        if (decode is null || encoded.IsEmpty) return false;
        try
        {
            HostImage image = default;
            int ok;
            fixed (byte* data = encoded)
                ok = decode(data, (nuint)encoded.Length, (uint)Math.Max(0, maxLongestSide), probeOnly ? 1 : 0, &image);
            if (ok == 0)
            {
                if (image.Data is not null && s_hostFree is not null) s_hostFree(image.Data);
                return false;
            }
            if (probeOnly && image.Data is not null && s_hostFree is not null)
            {
                s_hostFree(image.Data);
                image.Data = null;
            }
            result = image;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
