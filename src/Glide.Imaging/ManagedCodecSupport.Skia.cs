using SkiaSharp;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

// The viewer's implementation of the encoded sub-image seam: embedded JPEG/PNG (camera RAW previews,
// ORA/KRA layers, PDN thumbnails, DICOM JPEG frames) decoded with the bundled Skia codecs. The Explorer
// thumbnail codec library compiles ManagedCodecSupport.Host.cs (same signatures, WIC via the native
// host) instead of this file, so it carries no Skia dependency.
internal static partial class ManagedCodecSupport
{
    // ---------------------------------------------------------------------------------------------
    // Encoded (JPEG/PNG) sub-images, decoded with the bundled Skia codecs
    // ---------------------------------------------------------------------------------------------

    /// <summary>Reads the stored size and EXIF origin of an embedded JPEG/PNG without decoding pixels.</summary>
    public static bool TryProbeEncoded(ReadOnlySpan<byte> encoded, out int width, out int height, out int orientation)
    {
        width = height = 0;
        orientation = 1;
        try
        {
            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data);
            if (codec is null) return false;
            width = codec.Info.Width;
            height = codec.Info.Height;
            orientation = (int)codec.EncodedOrigin;
            if (orientation is < 1 or > 8) orientation = 1;
            return IsValidDimensions(width, height);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes an embedded JPEG/PNG to straight BGRA. When <paramref name="maxLongestSide"/> is positive the
    /// codec's native downscale (JPEG DCT scaling) is used as long as the result stays at least that large.
    /// The image's own EXIF orientation is reported, not applied.
    /// </summary>
    public static unsafe bool TryDecodeEncoded(ReadOnlySpan<byte> encoded, int maxLongestSide, out DecodedImage image, out int orientation)
    {
        image = default;
        orientation = 1;
        try
        {
            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data);
            if (codec is null) return false;
            var info = codec.Info;
            if (!IsValidDimensions(info.Width, info.Height)) return false;
            orientation = (int)codec.EncodedOrigin;
            if (orientation is < 1 or > 8) orientation = 1;

            var size = new SKSizeI(info.Width, info.Height);
            var longest = Math.Max(info.Width, info.Height);
            if (maxLongestSide > 0 && longest > maxLongestSide)
            {
                // JPEG DCT scaling only offers n/8 steps, and the codec rounds a requested scale down to
                // one of them. Asking for the exact ratio can therefore land just under the target and
                // fall back to a full decode; walk the steps and take the smallest that still covers it.
                for (var n = 1; n < 8; n++)
                {
                    if ((long)longest * n / 8 < maxLongestSide) continue;
                    var scaled = codec.GetScaledDimensions(n / 8f);
                    if (scaled.Width > 0 && scaled.Height > 0 && Math.Max(scaled.Width, scaled.Height) >= maxLongestSide &&
                        scaled.Width <= info.Width && scaled.Height <= info.Height)
                    {
                        size = scaled;
                        break;
                    }
                }
            }
            if (!FitsMemory(size.Width, size.Height)) return false;

            var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            if (!TryAllocatePixels(size.Width, size.Height, out var pixels)) return false;
            SKCodecResult result;
            fixed (byte* pointer = pixels)
                result = codec.GetPixels(decodeInfo, (IntPtr)pointer);
            if (result != SKCodecResult.Success) return false;
            image = new DecodedImage(size.Width, size.Height, pixels);
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }
}
