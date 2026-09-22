using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Dependency-free decoder for the long tail of simple raster formats that neither Avalonia/Skia
/// nor the Windows WIC bridge can be relied on to handle. Every entry point is total: malformed,
/// truncated or hostile input returns <c>false</c> (with a null bitmap) and never throws. Output is
/// straight (non-premultiplied) BGRA8 with alpha 255 unless the source carries its own alpha.
/// </summary>
public static class BuiltInRasterDecoder
{
    private const int MaxDimension = 65_535;
    private const long MaxTotalPixels = 256_000_000L; // ~1.0 GB uncompressed at 32bpp
    private const int MinBoundedSide = 1;
    private const int MaxBoundedSide = 32_768;
    private const long MaxSourceBytes = 1L << 30;      // 1 GiB on-disk safety cap
    private const long MaxDecodedBytes = 1L << 30;     // 1 GiB decompressed safety cap
    private const int MaxProbeBytes = 256 * 1024;

    private static readonly Regex XbmWidthRegex = new(@"_width\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex XbmHeightRegex = new(@"_height\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------------------------

    /// <summary>True when the longest declared suffix belongs to a format this decoder implements.</summary>
    public static bool CanDecode(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsSupportedExtension(ImageFormatRegistry.GetLongestExtension(path));

    /// <summary>Decodes the first frame/page at full resolution.</summary>
    public static bool TryDecode(string path, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!TryDecodeToBuffer(path, out var image)) return false;
        return TryCreateBitmap(image, out bitmap);
    }

    /// <summary>
    /// Decodes and, when the source is larger than <paramref name="maxLongestSide"/>, box-downsamples
    /// to fit. The side is clamped to [1, 32768].
    /// </summary>
    public static bool TryDecodeBounded(string path, int maxLongestSide, out Bitmap? bitmap)
    {
        bitmap = null;
        maxLongestSide = Math.Clamp(maxLongestSide, MinBoundedSide, MaxBoundedSide);
        if (!TryDecodeToBuffer(path, out var image)) return false;

        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxLongestSide) return TryCreateBitmap(image, out bitmap);

        var scale = (double)maxLongestSide / longest;
        var targetWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        var scaled = BoxDownsample(image, targetWidth, targetHeight);
        return TryCreateBitmap(scaled, out bitmap);
    }

    /// <summary>Reads just enough of the file to report the first frame's stored dimensions.</summary>
    public static bool TryProbeDimensions(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var extension = ImageFormatRegistry.GetLongestExtension(path);
            if (!IsSupportedExtension(extension)) return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.SequentialScan);
            var wanted = (int)Math.Min(stream.Length, MaxProbeBytes);
            if (wanted <= 0) return false;
            var data = new byte[wanted];
            var read = 0;
            while (read < wanted)
            {
                var chunk = stream.Read(data, read, wanted - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            if (read != wanted) Array.Resize(ref data, read);

            return TryProbeBuffer(data, extension, out dimensions);
        }
        catch
        {
            dimensions = default;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------------------------------

    private static bool IsSupportedExtension(string extension) => extension switch
    {
        ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => true,
        ".pcx" => true,
        ".pnm" or ".ppm" or ".pgm" or ".pbm" or ".pam" => true,
        ".qoi" => true,
        ".hdr" or ".rgbe" => true,
        ".wbmp" => true,
        ".xbm" => true,
        ".xpm" => true,
        ".sgi" or ".rgb" or ".rgba" or ".bw" => true,
        _ => false,
    };

    private static bool TryDecodeToBuffer(string path, out DecodedImage image)
    {
        image = default;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxSourceBytes) return false;
            var data = File.ReadAllBytes(path);
            var extension = ImageFormatRegistry.GetLongestExtension(path);
            if (!IsSupportedExtension(extension)) return false;
            return TryDecodeBuffer(data, extension, out image);
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static bool TryDecodeBuffer(ReadOnlySpan<byte> data, string extension, out DecodedImage image)
    {
        image = default;
        try
        {
            return extension switch
            {
                ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => TryDecodeTga(data, out image),
                ".pcx" => TryDecodePcx(data, out image),
                ".pnm" or ".ppm" or ".pgm" or ".pbm" or ".pam" => TryDecodePnm(data, out image),
                ".qoi" => TryDecodeQoi(data, out image),
                ".hdr" or ".rgbe" => TryDecodeHdr(data, out image),
                ".wbmp" => TryDecodeWbmp(data, out image),
                ".xbm" => TryDecodeXbm(data, out image),
                ".xpm" => TryDecodeXpm(data, out image),
                ".sgi" or ".rgb" or ".rgba" or ".bw" => TryDecodeSgi(data, out image),
                _ => false,
            };
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static bool TryProbeBuffer(ReadOnlySpan<byte> data, string extension, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            return extension switch
            {
                ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => TryProbeTga(data, out dimensions),
                ".pcx" => TryProbePcx(data, out dimensions),
                ".pnm" or ".ppm" or ".pgm" or ".pbm" or ".pam" => TryProbePnm(data, out dimensions),
                ".qoi" => TryProbeQoi(data, out dimensions),
                ".hdr" or ".rgbe" => TryProbeHdr(data, out dimensions),
                ".wbmp" => TryProbeWbmp(data, out dimensions),
                ".xbm" => TryProbeXbm(data, out dimensions),
                ".xpm" => TryProbeXpm(data, out dimensions),
                ".sgi" or ".rgb" or ".rgba" or ".bw" => TryProbeSgi(data, out dimensions),
                _ => false,
            };
        }
        catch
        {
            dimensions = default;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Bitmap / buffer plumbing
    // ---------------------------------------------------------------------------------------------

    private readonly struct DecodedImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Bgra;

        public DecodedImage(int width, int height, byte[] bgra)
        {
            Width = width;
            Height = height;
            Bgra = bgra;
        }
    }

    private static unsafe bool TryCreateBitmap(DecodedImage image, out Bitmap? bitmap)
    {
        bitmap = null;
        if (image.Bgra is null || image.Width <= 0 || image.Height <= 0) return false;
        int rowBytes;
        try { rowBytes = checked(image.Width * 4); }
        catch { return false; }
        if (image.Bgra.Length < (long)rowBytes * image.Height) return false;

        WriteableBitmap? candidate = null;
        try
        {
            candidate = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var frame = candidate.Lock())
            {
                if (frame.RowBytes < rowBytes) return false;
                fixed (byte* source = image.Bgra)
                {
                    for (var y = 0; y < image.Height; y++)
                    {
                        var src = source + (long)y * rowBytes;
                        var dst = (byte*)frame.Address + (long)y * frame.RowBytes;
                        Buffer.MemoryCopy(src, dst, frame.RowBytes, rowBytes);
                    }
                }
            }
            bitmap = candidate;
            candidate = null;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private static DecodedImage BoxDownsample(DecodedImage source, int targetWidth, int targetHeight)
    {
        var output = new byte[checked(targetWidth * targetHeight * 4)];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = (int)((long)ty * source.Height / targetHeight);
            var y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * source.Height / targetHeight));
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = (int)((long)tx * source.Width / targetWidth);
                var x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * source.Width / targetWidth));

                long b = 0, g = 0, r = 0, a = 0;
                var count = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * source.Width;
                    for (var x = x0; x < x1; x++)
                    {
                        var i = (row + x) * 4;
                        b += source.Bgra[i];
                        g += source.Bgra[i + 1];
                        r += source.Bgra[i + 2];
                        a += source.Bgra[i + 3];
                        count++;
                    }
                }

                var o = (ty * targetWidth + tx) * 4;
                output[o] = (byte)(b / count);
                output[o + 1] = (byte)(g / count);
                output[o + 2] = (byte)(r / count);
                output[o + 3] = (byte)(a / count);
            }
        }
        return new DecodedImage(targetWidth, targetHeight, output);
    }

    // ---------------------------------------------------------------------------------------------
    // Truevision TGA / Targa
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeTga(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (data.Length < 18) return false;

        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var colorMapFirst = ReadU16(data, 3);
        var colorMapLength = ReadU16(data, 5);
        var colorMapBits = data[7];
        var width = ReadU16(data, 12);
        var height = ReadU16(data, 14);
        var bpp = data[16];
        var descriptor = data[17];

        if (!IsValidDimensions(width, height)) return false;

        var rle = imageType >= 9;
        var baseType = rle ? imageType - 8 : imageType;
        if (baseType is < 1 or > 3) return false;
        if (rle && imageType > 11) return false;

        var offset = 18 + idLength;
        if (offset < 18 || offset > data.Length) return false;

        uint[]? palette = null;
        if (baseType == 1)
        {
            if (colorMapType != 1) return false;
            if (!IsValidTgaDepth(colorMapBits) || colorMapLength <= 0) return false;
            var entryBytes = (colorMapBits + 7) / 8;
            var paletteBytes = (long)colorMapLength * entryBytes;
            if (offset + paletteBytes > data.Length) return false;
            palette = new uint[colorMapLength];
            for (var i = 0; i < colorMapLength; i++)
                palette[i] = ReadTgaPaletteEntry(data.Slice(offset + i * entryBytes, entryBytes), colorMapBits);
            offset += (int)paletteBytes;
        }
        else if (colorMapType == 1)
        {
            // A color map may legally be present even when unused; skip it.
            if (!IsValidTgaDepth(colorMapBits)) return false;
            var entryBytes = (colorMapBits + 7) / 8;
            var paletteBytes = (long)colorMapLength * entryBytes;
            if (offset + paletteBytes > data.Length) return false;
            offset += (int)paletteBytes;
        }

        if (bpp is not (8 or 15 or 16 or 24 or 32)) return false;
        if (baseType == 1 && bpp is not (8 or 16)) return false;

        var total = width * height;
        var pixels = new byte[checked(total * 4)];
        var alphaBits = descriptor & 0x0F;
        var produced = 0;

        if (!rle)
        {
            for (var i = 0; i < total; i++)
            {
                if (!ReadTgaPixel(data, ref offset, baseType, bpp, alphaBits, palette, colorMapFirst, out var p)) return false;
                WritePacked(pixels, produced++, p);
            }
        }
        else
        {
            while (produced < total)
            {
                if (offset >= data.Length) return false;
                var header = data[offset++];
                var count = (header & 0x7F) + 1;
                if (produced + count > total) count = total - produced;
                if ((header & 0x80) != 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if (!ReadTgaPixel(data, ref offset, baseType, bpp, alphaBits, palette, colorMapFirst, out var p)) return false;
                        WritePacked(pixels, produced++, p);
                    }
                }
                else
                {
                    if (!ReadTgaPixel(data, ref offset, baseType, bpp, alphaBits, palette, colorMapFirst, out var p)) return false;
                    for (var i = 0; i < count; i++) WritePacked(pixels, produced++, p);
                }
            }
        }

        var rightToLeft = (descriptor & 0x10) != 0;
        var topLeft = (descriptor & 0x20) != 0;
        if (rightToLeft) FlipHorizontal(pixels, width, height);
        if (!topLeft) FlipVertical(pixels, width, height);

        image = new DecodedImage(width, height, pixels);
        return true;
    }

    private static bool TryProbeTga(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 18) return false;
        var imageType = data[2];
        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11)) return false;
        var width = ReadU16(data, 12);
        var height = ReadU16(data, 14);
        return SetDimensions(width, height, out dimensions);
    }

    private static bool IsValidTgaDepth(int bits) => bits is 8 or 15 or 16 or 24 or 32;

    private static uint ReadTgaPaletteEntry(ReadOnlySpan<byte> entry, int bits)
    {
        switch (bits)
        {
            case 8:
            {
                var v = entry[0];
                return Pack(v, v, v, 255);
            }
            case 15:
            case 16:
            {
                var value = entry[0] | (entry[1] << 8);
                return Pack(Scale5(value & 0x1F), Scale5((value >> 5) & 0x1F), Scale5((value >> 10) & 0x1F), 255);
            }
            case 24:
                return Pack(entry[0], entry[1], entry[2], 255);
            default:
                return Pack(entry[0], entry[1], entry[2], entry[3]);
        }
    }

    private static bool ReadTgaPixel(ReadOnlySpan<byte> data, ref int offset, int baseType, int bpp, int alphaBits,
        uint[]? palette, int colorMapFirst, out uint packed)
    {
        packed = 0;
        switch (baseType)
        {
            case 1:
            {
                int index;
                if (bpp == 8)
                {
                    if (offset + 1 > data.Length) return false;
                    index = data[offset++];
                }
                else
                {
                    if (offset + 2 > data.Length) return false;
                    index = data[offset] | (data[offset + 1] << 8);
                    offset += 2;
                }
                var entry = index - colorMapFirst;
                if (palette is null || entry < 0 || entry >= palette.Length) return false;
                packed = palette[entry];
                return true;
            }
            case 2:
            {
                if (bpp is 15 or 16)
                {
                    if (offset + 2 > data.Length) return false;
                    var value = data[offset] | (data[offset + 1] << 8);
                    offset += 2;
                    var alpha = (byte)255;
                    if (bpp == 16 && alphaBits > 0) alpha = ((value >> 15) & 1) != 0 ? (byte)255 : (byte)0;
                    packed = Pack(Scale5(value & 0x1F), Scale5((value >> 5) & 0x1F), Scale5((value >> 10) & 0x1F), alpha);
                    return true;
                }
                if (bpp == 24)
                {
                    if (offset + 3 > data.Length) return false;
                    packed = Pack(data[offset], data[offset + 1], data[offset + 2], 255);
                    offset += 3;
                    return true;
                }
                if (offset + 4 > data.Length) return false;
                packed = Pack(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]);
                offset += 4;
                return true;
            }
            default:
            {
                if (bpp == 8)
                {
                    if (offset + 1 > data.Length) return false;
                    var v = data[offset++];
                    packed = Pack(v, v, v, 255);
                    return true;
                }
                if (offset + 2 > data.Length) return false;
                packed = Pack(data[offset], data[offset], data[offset], data[offset + 1]);
                offset += 2;
                return true;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // ZSoft PCX
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodePcx(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (data.Length < 128 || data[0] != 0x0A) return false;

        var encoding = data[2];
        var bpp = data[3];
        var xmin = ReadU16(data, 4);
        var ymin = ReadU16(data, 6);
        var xmax = ReadU16(data, 8);
        var ymax = ReadU16(data, 10);
        var numPlanes = data[65];
        var bytesPerLine = ReadU16(data, 66);

        var width = xmax - xmin + 1;
        var height = ymax - ymin + 1;
        if (!IsValidDimensions(width, height)) return false;
        if (encoding is not (0 or 1)) return false;
        if (bytesPerLine <= 0) return false;

        var planarChannels = bpp == 8 && numPlanes is 3 or 4;
        var singlePlaneBytes = bpp is 24 or 32 && numPlanes == 1;
        int samplesPerRow;
        int requiredBytesPerLine;

        if (bpp is 1 or 2 or 4)
        {
            if (numPlanes != 1) return false;
            samplesPerRow = bytesPerLine;
            requiredBytesPerLine = (width * bpp + 7) / 8;
        }
        else if (bpp == 8 && numPlanes == 1)
        {
            samplesPerRow = bytesPerLine;
            requiredBytesPerLine = width;
        }
        else if (planarChannels)
        {
            samplesPerRow = checked(bytesPerLine * numPlanes);
            requiredBytesPerLine = width;
        }
        else if (singlePlaneBytes)
        {
            samplesPerRow = bytesPerLine;
            requiredBytesPerLine = width * (bpp / 8);
        }
        else
        {
            return false;
        }

        if (bytesPerLine < requiredBytesPerLine) return false;

        var decodedSize = (long)height * samplesPerRow;
        if (decodedSize <= 0 || decodedSize > MaxDecodedBytes) return false;
        var raw = new byte[(int)decodedSize];

        if (encoding == 1)
        {
            var position = 128;
            if (!PcxRleDecode(data, ref position, raw)) return false;
        }
        else
        {
            if (data.Length - 128 < raw.Length) return false;
            data.Slice(128, raw.Length).CopyTo(raw);
        }

        var palette16 = new uint[16];
        for (var i = 0; i < 16; i++)
            palette16[i] = Pack(data[16 + i * 3], data[17 + i * 3], data[18 + i * 3], 255);

        uint[]? palette256 = null;
        if (bpp == 8 && numPlanes == 1 && data.Length >= 769 && data[data.Length - 769] == 0x0C)
        {
            palette256 = new uint[256];
            var start = data.Length - 768;
            for (var i = 0; i < 256; i++)
                palette256[i] = Pack(data[start + i * 3], data[start + i * 3 + 1], data[start + i * 3 + 2], 255);
        }

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var rowBase = y * samplesPerRow;
            if (bpp is 1 or 2 or 4)
            {
                var mask = (1 << bpp) - 1;
                for (var x = 0; x < width; x++)
                {
                    var bitIndex = x * bpp;
                    var shift = 8 - bpp - (bitIndex & 7);
                    var index = (raw[rowBase + (bitIndex >> 3)] >> shift) & mask;
                    WritePacked(output, y * width + x, palette16[index]);
                }
            }
            else if (bpp == 8 && numPlanes == 1)
            {
                for (var x = 0; x < width; x++)
                {
                    var index = raw[rowBase + x];
                    var color = palette256 is not null ? palette256[index] : Pack((byte)index, (byte)index, (byte)index, 255);
                    WritePacked(output, y * width + x, color);
                }
            }
            else if (planarChannels)
            {
                var plane1 = rowBase + bytesPerLine;
                var plane2 = rowBase + bytesPerLine * 2;
                var plane3 = rowBase + bytesPerLine * 3;
                for (var x = 0; x < width; x++)
                {
                    var r = raw[rowBase + x];
                    var g = raw[plane1 + x];
                    var b = raw[plane2 + x];
                    var a = numPlanes >= 4 ? raw[plane3 + x] : (byte)255;
                    WritePacked(output, y * width + x, Pack(b, g, r, a));
                }
            }
            else
            {
                var stride = bpp / 8;
                for (var x = 0; x < width; x++)
                {
                    var o = rowBase + x * stride;
                    if (bpp == 24) WritePacked(output, y * width + x, Pack(raw[o], raw[o + 1], raw[o + 2], 255));
                    else WritePacked(output, y * width + x, Pack(raw[o], raw[o + 1], raw[o + 2], raw[o + 3]));
                }
            }
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbePcx(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 128 || data[0] != 0x0A) return false;
        var width = ReadU16(data, 8) - ReadU16(data, 4) + 1;
        var height = ReadU16(data, 10) - ReadU16(data, 6) + 1;
        return SetDimensions(width, height, out dimensions);
    }

    private static bool PcxRleDecode(ReadOnlySpan<byte> data, ref int position, byte[] destination)
    {
        var index = 0;
        while (index < destination.Length)
        {
            if (position >= data.Length) return false;
            var value = data[position++];
            if ((value & 0xC0) == 0xC0)
            {
                var count = value & 0x3F;
                if (count == 0)
                {
                    destination[index++] = value;
                    continue;
                }
                if (position >= data.Length) return false;
                var repeated = data[position++];
                if (index + count > destination.Length) return false;
                for (var i = 0; i < count; i++) destination[index++] = repeated;
            }
            else
            {
                destination[index++] = value;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Netpbm (PBM / PGM / PPM / PAM)
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodePnm(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (data.Length < 2 || data[0] != (byte)'P') return false;
        var kind = data[1] - (byte)'0';
        if (kind is < 1 or > 7) return false;
        var position = 2;

        if (kind == 7) return TryDecodePam(data, ref position, out image);

        if (!ReadPnmToken(data, ref position, out var widthValue) ||
            !ReadPnmToken(data, ref position, out var heightValue)) return false;
        var width = (int)widthValue;
        var height = (int)heightValue;
        if (!IsValidDimensions(width, height)) return false;

        if (kind == 1) return DecodePbmAscii(data, ref position, width, height, out image);
        if (kind == 4)
        {
            if (!SkipSingleWhitespace(data, ref position)) return false;
            return DecodePbmBinary(data, ref position, width, height, out image);
        }

        if (!ReadPnmToken(data, ref position, out var maxValue) || maxValue <= 0 || maxValue > 65535) return false;
        if (!SkipSingleWhitespace(data, ref position)) return false;
        var maxval = (int)maxValue;

        return kind switch
        {
            2 => DecodePgmAscii(data, ref position, width, height, maxval, out image),
            3 => DecodePpmAscii(data, ref position, width, height, maxval, out image),
            5 => DecodePgmBinary(data, ref position, width, height, maxval, out image),
            6 => DecodePpmBinary(data, ref position, width, height, maxval, out image),
            _ => false,
        };
    }

    private static bool TryProbePnm(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 2 || data[0] != (byte)'P') return false;
        var kind = data[1] - (byte)'0';
        if (kind is < 1 or > 7) return false;
        var position = 2;

        if (kind == 7)
        {
            if (!TryParsePamHeader(data, ref position, out var w, out var h, out _, out _)) return false;
            return SetDimensions(w, h, out dimensions);
        }

        if (!ReadPnmToken(data, ref position, out var widthValue) ||
            !ReadPnmToken(data, ref position, out var heightValue)) return false;
        return SetDimensions((int)widthValue, (int)heightValue, out dimensions);
    }

    private static bool TryDecodePam(ReadOnlySpan<byte> data, ref int position, out DecodedImage image)
    {
        image = default;
        if (!TryParsePamHeader(data, ref position, out var width, out var height, out var depth, out var maxval)) return false;
        if (depth is < 1 or > 4) return false;

        var bytesPerSample = maxval < 256 ? 1 : 2;
        var samples = (long)width * height * depth;
        var needed = samples * bytesPerSample;
        if (position + needed > data.Length) return false;

        var output = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            if (depth == 1)
            {
                var v = ReadSample(data, ref position, bytesPerSample, maxval);
                output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
            }
            else if (depth == 2)
            {
                var v = ReadSample(data, ref position, bytesPerSample, maxval);
                var a = ReadSample(data, ref position, bytesPerSample, maxval);
                output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = a;
            }
            else if (depth == 3)
            {
                var r = ReadSample(data, ref position, bytesPerSample, maxval);
                var g = ReadSample(data, ref position, bytesPerSample, maxval);
                var b = ReadSample(data, ref position, bytesPerSample, maxval);
                output[o] = b; output[o + 1] = g; output[o + 2] = r; output[o + 3] = 255;
            }
            else
            {
                var r = ReadSample(data, ref position, bytesPerSample, maxval);
                var g = ReadSample(data, ref position, bytesPerSample, maxval);
                var b = ReadSample(data, ref position, bytesPerSample, maxval);
                var a = ReadSample(data, ref position, bytesPerSample, maxval);
                output[o] = b; output[o + 1] = g; output[o + 2] = r; output[o + 3] = a;
            }
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryParsePamHeader(ReadOnlySpan<byte> data, ref int position, out int width, out int height, out int depth, out int maxval)
    {
        width = 0; height = 0; depth = 0; maxval = 0;
        var foundEnd = false;
        while (position < data.Length)
        {
            var lineStart = position;
            while (position < data.Length && data[position] != (byte)'\n') position++;
            var line = data.Slice(lineStart, position - lineStart);
            if (position < data.Length) position++;
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];

            var i = 0;
            while (i < line.Length && IsWhitespace(line[i])) i++;
            if (i >= line.Length || line[i] == (byte)'#') continue;

            var keyStart = i;
            while (i < line.Length && !IsWhitespace(line[i])) i++;
            var key = line.Slice(keyStart, i - keyStart);
            if (key.SequenceEqual("ENDHDR"u8))
            {
                foundEnd = true;
                break;
            }

            while (i < line.Length && IsWhitespace(line[i])) i++;
            var valueStart = i;
            while (i < line.Length && !IsWhitespace(line[i])) i++;
            var value = line.Slice(valueStart, i - valueStart);

            if (key.SequenceEqual("WIDTH"u8)) width = ParseInt(value);
            else if (key.SequenceEqual("HEIGHT"u8)) height = ParseInt(value);
            else if (key.SequenceEqual("DEPTH"u8)) depth = ParseInt(value);
            else if (key.SequenceEqual("MAXVAL"u8)) maxval = ParseInt(value);
        }

        if (!foundEnd) return false;
        if (!IsValidDimensions(width, height)) return false;
        if (maxval <= 0 || maxval > 65535) return false;
        return true;
    }

    private static bool DecodePbmAscii(ReadOnlySpan<byte> data, ref int position, int width, int height, out DecodedImage image)
    {
        image = default;
        var output = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            if (!ReadPnmToken(data, ref position, out var value)) return false;
            var v = value != 0 ? (byte)0 : (byte)255;
            var o = i * 4;
            output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool DecodePbmBinary(ReadOnlySpan<byte> data, ref int position, int width, int height, out DecodedImage image)
    {
        image = default;
        var rowBytes = (width + 7) / 8;
        if ((long)position + (long)rowBytes * height > data.Length) return false;
        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = data[position + y * rowBytes + (x >> 3)];
                var bit = (value >> (7 - (x & 7))) & 1;
                var v = bit == 1 ? (byte)0 : (byte)255;
                var o = (y * width + x) * 4;
                output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
            }
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool DecodePgmAscii(ReadOnlySpan<byte> data, ref int position, int width, int height, int maxval, out DecodedImage image)
    {
        image = default;
        var output = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            if (!ReadPnmToken(data, ref position, out var value)) return false;
            var v = ScaleSample((int)value, maxval);
            var o = i * 4;
            output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool DecodePpmAscii(ReadOnlySpan<byte> data, ref int position, int width, int height, int maxval, out DecodedImage image)
    {
        image = default;
        var output = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            if (!ReadPnmToken(data, ref position, out var r) ||
                !ReadPnmToken(data, ref position, out var g) ||
                !ReadPnmToken(data, ref position, out var b)) return false;
            var o = i * 4;
            output[o] = ScaleSample((int)b, maxval);
            output[o + 1] = ScaleSample((int)g, maxval);
            output[o + 2] = ScaleSample((int)r, maxval);
            output[o + 3] = 255;
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool DecodePgmBinary(ReadOnlySpan<byte> data, ref int position, int width, int height, int maxval, out DecodedImage image)
    {
        image = default;
        var bytesPerSample = maxval < 256 ? 1 : 2;
        if ((long)position + (long)width * height * bytesPerSample > data.Length) return false;
        var output = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var v = ReadSample(data, ref position, bytesPerSample, maxval);
            var o = i * 4;
            output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool DecodePpmBinary(ReadOnlySpan<byte> data, ref int position, int width, int height, int maxval, out DecodedImage image)
    {
        image = default;
        var bytesPerSample = maxval < 256 ? 1 : 2;
        if ((long)position + (long)width * height * 3 * bytesPerSample > data.Length) return false;
        var output = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var r = ReadSample(data, ref position, bytesPerSample, maxval);
            var g = ReadSample(data, ref position, bytesPerSample, maxval);
            var b = ReadSample(data, ref position, bytesPerSample, maxval);
            var o = i * 4;
            output[o] = b; output[o + 1] = g; output[o + 2] = r; output[o + 3] = 255;
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static byte ReadSample(ReadOnlySpan<byte> data, ref int position, int bytesPerSample, int maxval)
    {
        int value;
        if (bytesPerSample == 1)
        {
            value = data[position++];
        }
        else
        {
            value = (data[position] << 8) | data[position + 1];
            position += 2;
        }
        return ScaleSample(value, maxval);
    }

    private static byte ScaleSample(int value, int maxval)
    {
        if (maxval <= 0) return 0;
        if (maxval == 255) return (byte)Math.Clamp(value, 0, 255);
        if (maxval == 65535) return (byte)(Math.Clamp(value, 0, 65535) >> 8);
        return (byte)Math.Clamp((value * 255 + maxval / 2) / maxval, 0, 255);
    }

    private static bool ReadPnmToken(ReadOnlySpan<byte> data, ref int position, out long value)
    {
        value = 0;
        while (position < data.Length)
        {
            var c = data[position];
            if (c == (byte)'#')
            {
                while (position < data.Length && data[position] != (byte)'\n') position++;
            }
            else if (IsWhitespace(c))
            {
                position++;
            }
            else
            {
                break;
            }
        }

        if (position >= data.Length) return false;
        if (data[position] < (byte)'0' || data[position] > (byte)'9') return false;

        long result = 0;
        while (position < data.Length && data[position] >= (byte)'0' && data[position] <= (byte)'9')
        {
            result = result * 10 + (data[position] - (byte)'0');
            if (result > int.MaxValue) return false;
            position++;
        }
        value = result;
        return true;
    }

    private static bool SkipSingleWhitespace(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length || !IsWhitespace(data[position])) return false;
        var c = data[position++];
        if (c == (byte)'\r' && position < data.Length && data[position] == (byte)'\n') position++;
        return true;
    }

    private static int ParseInt(ReadOnlySpan<byte> value)
    {
        var result = 0;
        foreach (var c in value)
        {
            if (c < (byte)'0' || c > (byte)'9')
            {
                if (result == 0) continue;
                break;
            }
            result = result * 10 + (c - (byte)'0');
            if (result > int.MaxValue) return int.MaxValue;
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Quite OK Image (QOI)
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeQoi(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (data.Length < 14 || !data[..4].SequenceEqual("qoif"u8)) return false;
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        var channels = data[12];
        if (!IsValidDimensions(width, height)) return false;
        if (channels is not (3 or 4)) return false;

        var total = width * height;
        var output = new byte[checked(total * 4)];
        var index = new uint[64];
        byte r = 0, g = 0, b = 0, a = 255;
        var position = 14;
        var run = 0;

        for (var i = 0; i < total; i++)
        {
            if (run > 0)
            {
                run--;
            }
            else
            {
                if (position >= data.Length) return false;
                var b1 = data[position++];
                if (b1 == 0xFE)
                {
                    if (position + 3 > data.Length) return false;
                    r = data[position];
                    g = data[position + 1];
                    b = data[position + 2];
                    position += 3;
                }
                else if (b1 == 0xFF)
                {
                    if (position + 4 > data.Length) return false;
                    r = data[position];
                    g = data[position + 1];
                    b = data[position + 2];
                    a = data[position + 3];
                    position += 4;
                }
                else
                {
                    switch (b1 & 0xC0)
                    {
                        case 0x00:
                        {
                            var packed = index[b1 & 0x3F];
                            r = (byte)(packed >> 24);
                            g = (byte)(packed >> 16);
                            b = (byte)(packed >> 8);
                            a = (byte)packed;
                            break;
                        }
                        case 0x40:
                            r = (byte)(r + (((b1 >> 4) & 3) - 2));
                            g = (byte)(g + (((b1 >> 2) & 3) - 2));
                            b = (byte)(b + ((b1 & 3) - 2));
                            break;
                        case 0x80:
                        {
                            if (position >= data.Length) return false;
                            var b2 = data[position++];
                            var dg = (b1 & 0x3F) - 32;
                            r = (byte)(r + dg + ((b2 >> 4) & 0x0F) - 8);
                            g = (byte)(g + dg);
                            b = (byte)(b + dg + (b2 & 0x0F) - 8);
                            break;
                        }
                        default:
                            run = b1 & 0x3F;
                            break;
                    }
                }
            }

            var o = i * 4;
            output[o] = b;
            output[o + 1] = g;
            output[o + 2] = r;
            output[o + 3] = a;
            index[(r * 3 + g * 5 + b * 7 + a * 11) & 63] = (uint)((r << 24) | (g << 16) | (b << 8) | a);
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbeQoi(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 14 || !data[..4].SequenceEqual("qoif"u8)) return false;
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        return SetDimensions(width, height, out dimensions);
    }

    // ---------------------------------------------------------------------------------------------
    // Radiance HDR / RGBE
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeHdr(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (!TryParseHdrHeader(data, out var dataStart, out var width, out var height, out var flipX, out var flipY)) return false;
        if (!IsValidDimensions(width, height)) return false;

        var total = width * height;
        var output = new byte[checked(total * 4)];
        var channels = new byte[4 * width];
        var position = dataStart;

        for (var y = 0; y < height; y++)
        {
            if (position + 4 <= data.Length && width >= 8 && width <= 32767 &&
                data[position] == 2 && data[position + 1] == 2 &&
                ((data[position + 2] << 8) | data[position + 3]) == width)
            {
                position += 4;
                for (var c = 0; c < 4; c++)
                {
                    var x = 0;
                    while (x < width)
                    {
                        if (position >= data.Length) return false;
                        var code = data[position++];
                        if (code > 128)
                        {
                            var count = code - 128;
                            if (position >= data.Length || x + count > width) return false;
                            var value = data[position++];
                            for (var i = 0; i < count; i++) channels[c * width + x++] = value;
                        }
                        else if (code > 0)
                        {
                            var count = code;
                            if (position + count > data.Length || x + count > width) return false;
                            for (var i = 0; i < count; i++) channels[c * width + x++] = data[position++];
                        }
                        else
                        {
                            return false;
                        }
                    }
                }

                for (var x = 0; x < width; x++)
                {
                    WriteRgbe(output, y * width + x, channels[x], channels[width + x], channels[2 * width + x], channels[3 * width + x]);
                }
            }
            else
            {
                if ((long)position + (long)width * 4 > data.Length) return false;
                for (var x = 0; x < width; x++)
                {
                    WriteRgbe(output, y * width + x, data[position], data[position + 1], data[position + 2], data[position + 3]);
                    position += 4;
                }
            }
        }

        if (flipX) FlipHorizontal(output, width, height);
        if (flipY) FlipVertical(output, width, height);

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbeHdr(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryParseHdrHeader(data, out _, out var width, out var height, out _, out _)) return false;
        return SetDimensions(width, height, out dimensions);
    }

    private static bool TryParseHdrHeader(ReadOnlySpan<byte> data, out int dataStart, out int width, out int height,
        out bool flipX, out bool flipY)
    {
        dataStart = 0; width = 0; height = 0; flipX = false; flipY = false;
        if (data.Length < 4 || data[0] != (byte)'#' || data[1] != (byte)'?') return false;

        var position = 0;
        var headerEnded = false;
        while (position < data.Length)
        {
            var lineStart = position;
            while (position < data.Length && data[position] != (byte)'\n') position++;
            var line = data.Slice(lineStart, position - lineStart);
            if (position < data.Length) position++;
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (line.IsEmpty)
            {
                headerEnded = true;
                break;
            }
        }
        if (!headerEnded) return false;

        while (position < data.Length && (data[position] == (byte)'\n' || data[position] == (byte)'\r')) position++;
        var resolutionStart = position;
        while (position < data.Length && data[position] != (byte)'\n') position++;
        var resolution = data.Slice(resolutionStart, position - resolutionStart);
        if (!resolution.IsEmpty && resolution[^1] == (byte)'\r') resolution = resolution[..^1];
        if (position < data.Length) position++;

        var text = Encoding.ASCII.GetString(resolution);
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;

        for (var i = 0; i < 4; i += 2)
        {
            var spec = parts[i];
            if (spec.Length != 2) return false;
            var sign = spec[0];
            var axis = char.ToUpperInvariant(spec[1]);
            if (!int.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0)
                return false;
            if (axis == 'X') { width = count; flipX = sign == '-'; }
            else if (axis == 'Y') { height = count; flipY = sign == '+'; }
            else return false;
        }

        if (width <= 0 || height <= 0) return false;
        dataStart = position;
        return true;
    }

    private static void WriteRgbe(byte[] output, int pixel, byte er, byte eg, byte eb, byte ee)
    {
        var o = pixel * 4;
        if (ee == 0)
        {
            output[o] = 0; output[o + 1] = 0; output[o + 2] = 0;
        }
        else
        {
            var scale = (float)Math.ScaleB(1.0, ee - 136);
            output[o] = ClampByte(eb * scale * 255f);
            output[o + 1] = ClampByte(eg * scale * 255f);
            output[o + 2] = ClampByte(er * scale * 255f);
        }
        output[o + 3] = 255;
    }

    private static byte ClampByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)(value + 0.5f);
    }

    // ---------------------------------------------------------------------------------------------
    // WAP WBMP (type 0)
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeWbmp(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (data.Length < 4 || data[0] != 0 || data[1] != 0) return false;
        var position = 2;
        if (!ReadMultibyteUInt(data, ref position, out var widthValue) ||
            !ReadMultibyteUInt(data, ref position, out var heightValue)) return false;
        var width = (int)widthValue;
        var height = (int)heightValue;
        if (!IsValidDimensions(width, height)) return false;

        var rowBytes = (width + 7) / 8;
        if ((long)position + (long)rowBytes * height > data.Length) return false;

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = data[position + y * rowBytes + (x >> 3)];
                var bit = (value >> (7 - (x & 7))) & 1;
                var v = bit == 1 ? (byte)0 : (byte)255;
                var o = (y * width + x) * 4;
                output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
            }
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbeWbmp(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 4 || data[0] != 0 || data[1] != 0) return false;
        var position = 2;
        if (!ReadMultibyteUInt(data, ref position, out var width) ||
            !ReadMultibyteUInt(data, ref position, out var height)) return false;
        return SetDimensions((int)width, (int)height, out dimensions);
    }

    private static bool ReadMultibyteUInt(ReadOnlySpan<byte> data, ref int position, out uint value)
    {
        value = 0;
        var count = 0;
        while (true)
        {
            if (position >= data.Length || count > 5) return false;
            var b = data[position++];
            value = (value << 7) | (uint)(b & 0x7F);
            count++;
            if ((b & 0x80) == 0) return true;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // X11 bitmap (XBM)
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeXbm(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        var text = Encoding.ASCII.GetString(data);
        var width = GetDefine(text, XbmWidthRegex);
        var height = GetDefine(text, XbmHeightRegex);
        if (!IsValidDimensions(width, height)) return false;

        var braceStart = text.IndexOf('{');
        var braceEnd = braceStart >= 0 ? text.IndexOf('}', braceStart + 1) : -1;
        if (braceStart < 0 || braceEnd < 0) return false;

        var bytes = new List<byte>();
        for (var i = braceStart + 1; i < braceEnd; i++)
        {
            if (text[i] != '0' || i + 2 >= braceEnd || (text[i + 1] != 'x' && text[i + 1] != 'X')) continue;
            var j = i + 2;
            var value = 0;
            var digits = 0;
            while (j < braceEnd && digits < 2 && Uri.IsHexDigit(text[j]))
            {
                value = value * 16 + HexValue(text[j]);
                j++;
                digits++;
            }
            if (digits == 0) continue;
            bytes.Add((byte)value);
            i = j - 1;
        }

        var rowBytes = (width + 7) / 8;
        if ((long)rowBytes * height > bytes.Count) return false;

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = bytes[y * rowBytes + (x >> 3)];
                var bit = (value >> (x & 7)) & 1; // XBM pixels are LSB-first.
                var v = bit == 1 ? (byte)0 : (byte)255;
                var o = (y * width + x) * 4;
                output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
            }
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbeXbm(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        var text = Encoding.ASCII.GetString(data);
        var width = GetDefine(text, XbmWidthRegex);
        var height = GetDefine(text, XbmHeightRegex);
        return SetDimensions(width, height, out dimensions);
    }

    private static int GetDefine(string text, Regex regex)
    {
        var match = regex.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    // ---------------------------------------------------------------------------------------------
    // X11 pixmap (XPM)
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeXpm(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        var text = Encoding.Latin1.GetString(data);
        var strings = ExtractCStrings(text);
        if (strings.Count < 1) return false;

        var header = strings[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (header.Length < 4) return false;
        if (!int.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colors) ||
            !int.TryParse(header[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var charsPerPixel)) return false;
        if (!IsValidDimensions(width, height)) return false;
        if (charsPerPixel <= 0 || charsPerPixel > 8 || colors <= 0 || colors > 65536) return false;
        if (strings.Count < 1 + colors + height) return false;

        var map = new Dictionary<string, uint>(colors, StringComparer.Ordinal);
        for (var i = 0; i < colors; i++)
        {
            var line = strings[1 + i];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;
            var key = tokens[0];
            var colorIndex = Array.FindIndex(tokens, t => t == "c");
            if (colorIndex < 0 || colorIndex + 1 >= tokens.Length) return false;
            map[key] = ParseXpmColor(tokens[colorIndex + 1]);
        }

        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = strings[1 + colors + y];
            if (row.Length < width * charsPerPixel) return false;
            for (var x = 0; x < width; x++)
            {
                var key = row.Substring(x * charsPerPixel, charsPerPixel);
                if (!map.TryGetValue(key, out var packed)) return false;
                WritePacked(output, y * width + x, packed);
            }
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbeXpm(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        var text = Encoding.Latin1.GetString(data);
        var strings = ExtractCStrings(text);
        if (strings.Count < 1) return false;
        var header = strings[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (header.Length < 2) return false;
        if (!int.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)) return false;
        return SetDimensions(width, height, out dimensions);
    }

    private static List<string> ExtractCStrings(string text)
    {
        var result = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '"')
            {
                i++;
                continue;
            }
            i++;
            var builder = new StringBuilder();
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    // Keep the escaped character literal; XPM rarely uses escapes in keys/colors.
                    builder.Append(text[i + 1]);
                    i += 2;
                }
                else
                {
                    builder.Append(text[i]);
                    i++;
                }
            }
            if (i < text.Length) i++; // closing quote
            result.Add(builder.ToString());
        }
        return result;
    }

    private static uint ParseXpmColor(string color)
    {
        if (color.Equals("None", StringComparison.OrdinalIgnoreCase)) return 0x00000000u;
        if (color.Length > 0 && color[0] == '#')
        {
            var hex = color.AsSpan(1);
            switch (hex.Length)
            {
                case 3:
                    return Pack((byte)(HexValue(hex[2]) * 17), (byte)(HexValue(hex[1]) * 17), (byte)(HexValue(hex[0]) * 17), 255);
                case 6:
                    return Pack((byte)((HexValue(hex[4]) << 4) | HexValue(hex[5])),
                        (byte)((HexValue(hex[2]) << 4) | HexValue(hex[3])),
                        (byte)((HexValue(hex[0]) << 4) | HexValue(hex[1])), 255);
                case 9:
                    return Pack((byte)(ParseHex(hex.Slice(6, 3)) * 255 / 4095),
                        (byte)(ParseHex(hex.Slice(3, 3)) * 255 / 4095),
                        (byte)(ParseHex(hex.Slice(0, 3)) * 255 / 4095), 255);
                case 12:
                    return Pack((byte)(ParseHex(hex.Slice(8, 4)) * 255 / 65535),
                        (byte)(ParseHex(hex.Slice(4, 4)) * 255 / 65535),
                        (byte)(ParseHex(hex.Slice(0, 4)) * 255 / 65535), 255);
            }
        }
        return Pack(0, 0, 0, 255);
    }

    private static int ParseHex(ReadOnlySpan<char> value)
    {
        var result = 0;
        foreach (var c in value) result = (result << 4) | HexValue(c);
        return result;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };

    // ---------------------------------------------------------------------------------------------
    // SGI RGB
    // ---------------------------------------------------------------------------------------------

    private static bool TryDecodeSgi(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (data.Length < 512 || ReadU16BigEndian(data, 0) != 0x01DA) return false;
        var storage = data[2];
        var bpc = data[3];
        var dimension = ReadU16BigEndian(data, 4);
        var width = ReadU16BigEndian(data, 6);
        var height = ReadU16BigEndian(data, 8);
        var zsize = ReadU16BigEndian(data, 10);

        if (storage is not (0 or 1)) return false;
        if (bpc is not (1 or 2)) return false;
        if (zsize is < 1 or > 4) return false;
        if (dimension is < 1 or > 3) return false;
        if (!IsValidDimensions(width, height)) return false;

        var output = new byte[checked(width * height * 4)];
        var rows = new byte[zsize][];
        for (var z = 0; z < zsize; z++) rows[z] = new byte[width * bpc];

        for (var y = 0; y < height; y++)
        {
            for (var z = 0; z < zsize; z++)
            {
                if (storage == 0)
                {
                    var offset = 512L + (long)z * width * height * bpc + (long)y * width * bpc;
                    if (offset + (long)width * bpc > data.Length) return false;
                    data.Slice((int)offset, width * bpc).CopyTo(rows[z]);
                }
                else
                {
                    if (!DecodeSgiRleRow(data, y, z, width, height, zsize, bpc, rows[z])) return false;
                }
            }

            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                if (zsize == 1)
                {
                    var v = SampleAt(rows[0], x, bpc);
                    output[o] = v; output[o + 1] = v; output[o + 2] = v; output[o + 3] = 255;
                }
                else if (zsize == 2)
                {
                    var v = SampleAt(rows[0], x, bpc);
                    output[o] = v; output[o + 1] = v; output[o + 2] = v;
                    output[o + 3] = SampleAt(rows[1], x, bpc);
                }
                else
                {
                    output[o] = SampleAt(rows[2], x, bpc);
                    output[o + 1] = SampleAt(rows[1], x, bpc);
                    output[o + 2] = SampleAt(rows[0], x, bpc);
                    output[o + 3] = zsize >= 4 ? SampleAt(rows[3], x, bpc) : (byte)255;
                }
            }
        }

        image = new DecodedImage(width, height, output);
        return true;
    }

    private static bool TryProbeSgi(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (data.Length < 512 || ReadU16BigEndian(data, 0) != 0x01DA) return false;
        if (data[3] is not (1 or 2)) return false;
        var width = ReadU16BigEndian(data, 6);
        var height = ReadU16BigEndian(data, 8);
        var zsize = ReadU16BigEndian(data, 10);
        if (zsize is < 1 or > 4) return false;
        return SetDimensions(width, height, out dimensions);
    }

    private static bool DecodeSgiRleRow(ReadOnlySpan<byte> data, int y, int z, int width, int height, int zsize, int bpc, byte[] destination)
    {
        var tableCount = (long)zsize * height;
        var offsetsStart = 512L;
        var lengthsStart = 512L + tableCount * 4;
        if (lengthsStart + tableCount * 4 > data.Length) return false;

        var index = z * height + y;
        var offsetPosition = offsetsStart + index * 4L;
        var lengthPosition = lengthsStart + index * 4L;
        if (offsetPosition + 4 > data.Length || lengthPosition + 4 > data.Length) return false;

        var offset = ReadU32BigEndian(data, (int)offsetPosition);
        var length = ReadU32BigEndian(data, (int)lengthPosition);
        if (offset == 0 || length == 0) return false;
        if ((long)offset + length > data.Length) return false;

        var source = data.Slice((int)offset, (int)length);
        var x = 0;
        var position = 0;
        while (x < width)
        {
            if (position >= source.Length) return false;
            var control = source[position++];
            var count = control & 0x7F;
            if (count == 0) break;
            if ((control & 0x80) != 0)
            {
                if (position + count * bpc > source.Length || x + count > width) return false;
                source.Slice(position, count * bpc).CopyTo(destination.AsSpan(x * bpc));
                position += count * bpc;
                x += count;
            }
            else
            {
                if (position + bpc > source.Length || x + count > width) return false;
                for (var i = 0; i < count; i++)
                    source.Slice(position, bpc).CopyTo(destination.AsSpan((x + i) * bpc));
                position += bpc;
                x += count;
            }
        }

        if (x < width) destination.AsSpan(x * bpc, (width - x) * bpc).Clear();
        return true;
    }

    private static byte SampleAt(byte[] row, int x, int bpc) => bpc == 1 ? row[x] : row[x * 2];

    // ---------------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------------

    private static bool IsValidDimensions(int width, int height)
        => width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension &&
           (long)width * height <= MaxTotalPixels;

    private static bool SetDimensions(int width, int height, out ImageDimensions dimensions)
    {
        dimensions = new ImageDimensions(width, height);
        return IsValidDimensions(width, height);
    }

    private static int ReadU16(ReadOnlySpan<byte> data, int offset) => data[offset] | (data[offset + 1] << 8);

    private static int ReadU16BigEndian(ReadOnlySpan<byte> data, int offset) => (data[offset] << 8) | data[offset + 1];

    private static uint ReadU32BigEndian(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static uint Pack(byte b, byte g, byte r, byte a) => (uint)(b | (g << 8) | (r << 16) | (a << 24));

    private static byte Scale5(int value) => (byte)((value << 3) | (value >> 2));

    private static void WritePacked(byte[] buffer, int pixel, uint packed)
    {
        var o = pixel * 4;
        buffer[o] = (byte)packed;
        buffer[o + 1] = (byte)(packed >> 8);
        buffer[o + 2] = (byte)(packed >> 16);
        buffer[o + 3] = (byte)(packed >> 24);
    }

    private static bool IsWhitespace(byte value)
        => value is 0x20 or 0x09 or 0x0A or 0x0D or 0x0B or 0x0C;

    private static void FlipHorizontal(byte[] buffer, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width / 2; x++)
            {
                var left = (row + x) * 4;
                var right = (row + width - 1 - x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    (buffer[left + c], buffer[right + c]) = (buffer[right + c], buffer[left + c]);
                }
            }
        }
    }

    private static void FlipVertical(byte[] buffer, int width, int height)
    {
        var rowBytes = width * 4;
        var temp = new byte[rowBytes];
        for (var y = 0; y < height / 2; y++)
        {
            var top = y * rowBytes;
            var bottom = (height - 1 - y) * rowBytes;
            Buffer.BlockCopy(buffer, top, temp, 0, rowBytes);
            Buffer.BlockCopy(buffer, bottom, buffer, top, rowBytes);
            Buffer.BlockCopy(temp, 0, buffer, bottom, rowBytes);
        }
    }
}
