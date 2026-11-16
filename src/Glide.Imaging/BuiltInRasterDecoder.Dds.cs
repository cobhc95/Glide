using System.Buffers.Binary;
using Glide.Core;

namespace Glide.Imaging;

/// <summary>
/// Uncompressed DirectDraw Surface (5.0.4). Windows' WIC DDS codec decodes only the block-compressed
/// BC1-BC3 formats, so the uncompressed textures Glide itself writes on Save as (B8G8R8A8, legacy header)
/// and other plain 32/24-bit RGB(A) DDS files are read here. Anything else (BC/DXTn, float, luminance)
/// returns false and continues to WIC exactly as before.
/// </summary>
public static partial class BuiltInRasterDecoder
{
    private const uint DdsFlagAlphaPixels = 0x1, DdsFlagFourCC = 0x4, DdsFlagRgb = 0x40;

    private readonly record struct DdsLayout(int Width, int Height, int DataOffset, int BytesPerPixel, int Pitch,
        int ShiftR, int ShiftG, int ShiftB, int ShiftA);

    private static bool TryParseUncompressedDds(ReadOnlySpan<byte> data, out DdsLayout layout)
    {
        layout = default;
        if (data.Length < 128 || !data[..4].SequenceEqual("DDS "u8) || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 124) return false;
        var height = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[12..]), int.MaxValue);
        var width = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[16..]), int.MaxValue);
        if (!IsValidDimensions(width, height)) return false;
        var pf = data[76..108];
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(pf[4..]);
        uint r, g, b, a;
        int bytesPerPixel, offset;
        if ((flags & DdsFlagFourCC) != 0)
        {
            if (!pf.Slice(8, 4).SequenceEqual("DX10"u8) || data.Length < 148) return false; // array textures: first slice
            var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(data[128..]);
            switch (dxgi)
            {
                case 87 or 91: r = 0x00FF0000; g = 0x0000FF00; b = 0x000000FF; a = 0xFF000000; break; // B8G8R8A8(_SRGB)
                case 88 or 93: r = 0x00FF0000; g = 0x0000FF00; b = 0x000000FF; a = 0; break;          // B8G8R8X8(_SRGB)
                case 28 or 29: r = 0x000000FF; g = 0x0000FF00; b = 0x00FF0000; a = 0xFF000000; break; // R8G8B8A8(_SRGB)
                default: return false;
            }
            bytesPerPixel = 4;
            offset = 148;
        }
        else
        {
            if ((flags & DdsFlagRgb) == 0) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(pf[12..]);
            if (bits is not (24 or 32)) return false;
            r = BinaryPrimitives.ReadUInt32LittleEndian(pf[16..]);
            g = BinaryPrimitives.ReadUInt32LittleEndian(pf[20..]);
            b = BinaryPrimitives.ReadUInt32LittleEndian(pf[24..]);
            a = (flags & DdsFlagAlphaPixels) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(pf[28..]) : 0;
            bytesPerPixel = (int)bits / 8;
            offset = 128;
        }
        if (!ByteShift(r, out var sr) || !ByteShift(g, out var sg) || !ByteShift(b, out var sb)) return false;
        var sa = -1;
        if (a != 0 && !ByteShift(a, out sa)) return false;
        if (Math.Max(Math.Max(sr, sg), Math.Max(sb, sa)) >= bytesPerPixel * 8) return false;
        var pitch = width * bytesPerPixel;
        if ((long)offset + (long)pitch * height > data.Length) return false;
        layout = new DdsLayout(width, height, offset, bytesPerPixel, pitch, sr, sg, sb, sa);
        return true;
    }

    /// <summary>Only whole-byte 8-bit masks (0xFF shifted by a multiple of 8).</summary>
    private static bool ByteShift(uint mask, out int shift)
    {
        shift = -1;
        for (var s = 0; s <= 24; s += 8)
            if (mask == 0xFFu << s) { shift = s; return true; }
        return false;
    }

    private static bool TryDecodeDds(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (!TryParseUncompressedDds(data, out var l)) return false;
        var output = new byte[checked(l.Width * l.Height * 4)];
        for (var y = 0; y < l.Height; y++)
        {
            var row = data.Slice(l.DataOffset + y * l.Pitch, l.Pitch);
            for (var x = 0; x < l.Width; x++)
            {
                var p = row.Slice(x * l.BytesPerPixel, l.BytesPerPixel);
                uint value = l.BytesPerPixel == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(p) : (uint)(p[0] | (p[1] << 8) | (p[2] << 16));
                var o = (y * l.Width + x) * 4;
                output[o] = (byte)(value >> l.ShiftB);
                output[o + 1] = (byte)(value >> l.ShiftG);
                output[o + 2] = (byte)(value >> l.ShiftR);
                output[o + 3] = l.ShiftA < 0 ? (byte)255 : (byte)(value >> l.ShiftA);
            }
        }
        image = new DecodedImage(l.Width, l.Height, output);
        return true;
    }

    private static bool TryProbeDds(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        // Header only: the probe buffer may be shorter than the pixel data.
        if (data.Length < 128 || !data[..4].SequenceEqual("DDS "u8)) return false;
        var height = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[12..]), int.MaxValue);
        var width = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[16..]), int.MaxValue);
        if (!IsValidDimensions(width, height)) return false;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[80..]);
        var uncompressed = (flags & DdsFlagRgb) != 0 && (flags & DdsFlagFourCC) == 0 ||
                           (flags & DdsFlagFourCC) != 0 && data.Slice(84, 4).SequenceEqual("DX10"u8);
        return uncompressed && SetDimensions(width, height, out dimensions);
    }
}
