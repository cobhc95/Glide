using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

/// <summary>
/// DICOM (.dcm/.dicom/.ima) first frame. Uncompressed implicit/explicit little- and big-endian and
/// deflated transfer syntaxes, RLE Lossless, and encapsulated 8-bit JPEG (baseline/extended/progressive,
/// decoded with the bundled JPEG codec). Monochrome 8/16/32-bit with rescale and VOI window (or a
/// min/max stretch when no window is stored), MONOCHROME1 inversion, RGB and YBR_FULL colour.
/// Not handled: JPEG Lossless/JPEG-LS/JPEG 2000 transfer syntaxes, PALETTE COLOR, YBR_*_422 native.
/// </summary>
internal static class DicomDecoder
{
    private const uint Undefined = 0xFFFFFFFF;

    private enum Syntax { ImplicitLittle, ExplicitLittle, ExplicitBig }

    private sealed class State
    {
        public string TransferSyntax = "1.2.840.10008.1.2";
        public int Rows, Columns, BitsAllocated, BitsStored, HighBit = -1, PixelRepresentation, Samples = 1, Planar, Frames = 1;
        public string Photometric = "MONOCHROME2";
        public double Slope = 1, Intercept, WindowCenter = double.NaN, WindowWidth = double.NaN;
        public int PixelOffset = -1;
        public long PixelLength;
        public bool Encapsulated;
        public Syntax Syntax;
        public byte[]? Dataset; // inflated dataset for the deflated transfer syntax
    }

    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            if (!TryParse(data, stopAtPixelData: true, out var state)) return false;
            if (!ManagedCodecSupport.IsValidDimensions(state.Columns, state.Rows)) return false;
            dimensions = new ImageDimensions(state.Columns, state.Rows);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        try
        {
            if (!TryParse(data, stopAtPixelData: false, out var state)) return false;
            if (!ManagedCodecSupport.FitsMemory(state.Columns, state.Rows)) return false;
            ReadOnlySpan<byte> dataset = state.Dataset is null ? data : state.Dataset;
            if (state.PixelOffset < 0) return false;

            if (state.Encapsulated)
            {
                if (!TryGetFirstFrame(dataset, state, out var frame)) return false;
                if (state.TransferSyntax == "1.2.840.10008.1.2.5")
                {
                    if (!TryDecodeRle(frame, state, out var native)) return false;
                    state.Planar = 1;
                    return TryDecodeNative(native, state, Syntax.ExplicitLittle, out image);
                }
                if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != 0xD8) return false;
                var jpegBytes = state.Photometric == "RGB" ? MarkJpegAsRgb(frame) : null;
                if (!ManagedCodecSupport.TryDecodeEncoded(jpegBytes ?? frame, maxLongestSide, out var jpeg, out _)) return false;
                if (state.Photometric == "MONOCHROME1") Invert(jpeg.Bgra);
                image = jpeg;
                return true;
            }

            var bytesPerSample = state.BitsAllocated / 8;
            var frameBytes = (long)state.Rows * state.Columns * state.Samples * bytesPerSample;
            if (frameBytes <= 0 || state.PixelOffset + frameBytes > dataset.Length) return false;
            return TryDecodeNative(dataset.Slice(state.PixelOffset, (int)frameBytes), state, state.Syntax, out image);
        }
        catch
        {
            image = default;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Parsing
    // ---------------------------------------------------------------------------------------------

    private static bool TryParse(ReadOnlySpan<byte> data, bool stopAtPixelData, out State state)
    {
        state = new State();
        var position = 0;
        var hasMeta = false;
        if (data.Length >= 132 && data.Slice(128, 4).SequenceEqual("DICM"u8))
        {
            position = 132;
            hasMeta = true;
        }
        else if (data.Length < 8)
        {
            return false;
        }

        // File meta information (group 0002) is always explicit VR little endian.
        while (position + 8 <= data.Length && BinaryPrimitives.ReadUInt16LittleEndian(data[position..]) == 0x0002)
        {
            if (!ReadHeader(data, ref position, Syntax.ExplicitLittle, out var group, out var element, out _, out var length)) return false;
            if (length == Undefined || position + length > data.Length) return false;
            if (group == 0x0002 && element == 0x0010)
                state.TransferSyntax = Encoding.ASCII.GetString(data.Slice(position, (int)length)).Trim('\0', ' ');
            position += (int)length;
            hasMeta = true;
        }

        if (!hasMeta)
        {
            // A raw dataset without preamble: accept only when the first tag is plausible.
            var group = BinaryPrimitives.ReadUInt16LittleEndian(data);
            if (group is not (0x0008 or 0x0010 or 0x0018 or 0x0020 or 0x0028)) return false;
            state.TransferSyntax = IsUpper(data[4]) && IsUpper(data[5]) ? "1.2.840.10008.1.2.1" : "1.2.840.10008.1.2";
        }

        var ts = state.TransferSyntax;
        state.Syntax = ts switch
        {
            "1.2.840.10008.1.2" => Syntax.ImplicitLittle,
            "1.2.840.10008.1.2.2" => Syntax.ExplicitBig,
            _ => Syntax.ExplicitLittle,
        };
        var encapsulatedSyntax = ts.StartsWith("1.2.840.10008.1.2.4", StringComparison.Ordinal) || ts == "1.2.840.10008.1.2.5";

        ReadOnlySpan<byte> dataset = data;
        if (ts == "1.2.840.10008.1.2.1.99")
        {
            if (stopAtPixelData)
            {
                // Probe: inflate a bounded prefix only.
                var prefix = new byte[Math.Min(1 << 20, (data.Length - position) * 16L + 4096)];
                var produced = ManagedCodecSupport.Inflate(data[position..], prefix, ManagedCodecSupport.Wrapper.Deflate);
                if (produced <= 0) return false;
                state.Dataset = prefix[..produced];
            }
            else
            {
                if (!InflateAll(data[position..], out var inflated)) return false;
                state.Dataset = inflated;
            }
            dataset = state.Dataset;
            position = 0;
        }

        // Some writers declare explicit VR but write implicit: detect from the first element.
        if (state.Syntax == Syntax.ExplicitLittle && position + 6 <= dataset.Length &&
            !(IsUpper(dataset[position + 4]) && IsUpper(dataset[position + 5])))
            state.Syntax = Syntax.ImplicitLittle;

        var syntax = state.Syntax;
        for (var n = 0; n < 100_000 && position + 8 <= dataset.Length; n++)
        {
            if (!ReadHeader(dataset, ref position, syntax, out var group, out var element, out var vr, out var length)) return false;
            if (group == 0x7FE0 && element == 0x0010)
            {
                state.PixelOffset = position;
                state.Encapsulated = length == Undefined;
                state.PixelLength = length == Undefined ? dataset.Length - position : length;
                if (!state.Encapsulated && encapsulatedSyntax) return false;
                break;
            }
            if (length == Undefined)
            {
                if (!SkipUndefined(dataset, ref position, vr == "UN" ? Syntax.ImplicitLittle : syntax, 0)) return false;
                continue;
            }
            if (position + length > dataset.Length)
            {
                if (stopAtPixelData) break;
                return false;
            }
            var value = dataset.Slice(position, (int)length);
            if (group == 0x0028) ReadImageTag(state, element, value, syntax);
            position += (int)length;
        }

        if (state.Rows <= 0 || state.Columns <= 0) return false;
        if (state.BitsAllocated == 0) state.BitsAllocated = 16;
        if (state.BitsStored == 0 || state.BitsStored > state.BitsAllocated) state.BitsStored = state.BitsAllocated;
        if (state.HighBit < 0 || state.HighBit >= state.BitsAllocated) state.HighBit = state.BitsStored - 1;
        if (state.BitsAllocated is not (8 or 16 or 32)) return false;
        if (state.Samples is not (1 or 3)) return false;
        return stopAtPixelData || state.PixelOffset >= 0;
    }

    private static bool IsUpper(byte b) => b is >= (byte)'A' and <= (byte)'Z';

    private static bool InflateAll(ReadOnlySpan<byte> source, out byte[] result)
    {
        result = Array.Empty<byte>();
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            unsafe
            {
                fixed (byte* pointer = source)
                {
                    using var input = new UnmanagedMemoryStream(pointer, source.Length);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > ManagedCodecSupport.MaxDecompressedBytes) return false;
                        output.Write(buffer, 0, read);
                    }
                }
            }
            result = output.ToArray();
            return result.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadHeader(ReadOnlySpan<byte> data, ref int position, Syntax syntax, out int group, out int element, out string vr, out uint length)
    {
        group = element = 0;
        vr = string.Empty;
        length = 0;
        if (position + 8 > data.Length) return false;
        var big = syntax == Syntax.ExplicitBig;
        group = big ? BinaryPrimitives.ReadUInt16BigEndian(data[position..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
        element = big ? BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 2)..]);
        // Item and delimitation tags never carry a VR.
        if (group == 0xFFFE || syntax == Syntax.ImplicitLittle)
        {
            length = big ? BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            position += 8;
            return true;
        }
        vr = Encoding.ASCII.GetString(data.Slice(position + 4, 2));
        if (vr is "OB" or "OD" or "OF" or "OL" or "OV" or "OW" or "SQ" or "SV" or "UC" or "UN" or "UR" or "UT" or "UV")
        {
            if (position + 12 > data.Length) return false;
            length = big ? BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 8)..]);
            position += 12;
        }
        else
        {
            length = big ? BinaryPrimitives.ReadUInt16BigEndian(data[(position + 6)..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 6)..]);
            position += 8;
        }
        return true;
    }

    /// <summary>Skips a sequence (or UN element) of undefined length, including nested items.</summary>
    private static bool SkipUndefined(ReadOnlySpan<byte> data, ref int position, Syntax syntax, int depth)
    {
        if (depth > 16) return false;
        for (var n = 0; n < 1_000_000; n++)
        {
            if (!ReadHeader(data, ref position, syntax, out var group, out var element, out _, out var length)) return false;
            if (group != 0xFFFE) return false;
            if (element == 0xE0DD) return true; // sequence delimitation
            if (element != 0xE000) return false;
            if (length != Undefined)
            {
                if (position + length > data.Length) return false;
                position += (int)length;
                continue;
            }
            // Item of undefined length: elements until the item delimitation tag.
            for (var m = 0; ; m++)
            {
                if (m > 1_000_000) return false;
                if (!ReadHeader(data, ref position, syntax, out var g, out var e, out var vr, out var l)) return false;
                if (g == 0xFFFE && e == 0xE00D) break;
                if (l == Undefined)
                {
                    if (!SkipUndefined(data, ref position, vr == "UN" ? Syntax.ImplicitLittle : syntax, depth + 1)) return false;
                    continue;
                }
                if (position + l > data.Length) return false;
                position += (int)l;
            }
        }
        return false;
    }

    private static void ReadImageTag(State state, int element, ReadOnlySpan<byte> value, Syntax syntax)
    {
        var us = value.Length >= 2
            ? syntax == Syntax.ExplicitBig ? BinaryPrimitives.ReadUInt16BigEndian(value) : BinaryPrimitives.ReadUInt16LittleEndian(value)
            : 0;
        switch (element)
        {
            case 0x0002: state.Samples = us; break;
            case 0x0004: state.Photometric = Encoding.ASCII.GetString(value).Trim('\0', ' ').ToUpperInvariant(); break;
            case 0x0006: state.Planar = us; break;
            case 0x0008: state.Frames = (int)Math.Max(1, ParseNumber(value, 1)); break;
            case 0x0010: state.Rows = us; break;
            case 0x0011: state.Columns = us; break;
            case 0x0100: state.BitsAllocated = us; break;
            case 0x0101: state.BitsStored = us; break;
            case 0x0102: state.HighBit = us; break;
            case 0x0103: state.PixelRepresentation = us; break;
            case 0x1050: state.WindowCenter = ParseNumber(value, double.NaN); break;
            case 0x1051: state.WindowWidth = ParseNumber(value, double.NaN); break;
            case 0x1052: state.Intercept = ParseNumber(value, 0); break;
            case 0x1053: state.Slope = ParseNumber(value, 1); break;
        }
    }

    private static double ParseNumber(ReadOnlySpan<byte> value, double fallback)
    {
        var text = Encoding.ASCII.GetString(value).Trim('\0', ' ');
        var separator = text.IndexOf('\\');
        if (separator >= 0) text = text[..separator];
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;
    }

    // ---------------------------------------------------------------------------------------------
    // Pixel data
    // ---------------------------------------------------------------------------------------------

    private static bool TryGetFirstFrame(ReadOnlySpan<byte> data, State state, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        var position = state.PixelOffset;
        var syntax = state.Syntax == Syntax.ExplicitBig ? Syntax.ExplicitBig : Syntax.ImplicitLittle;
        // Basic offset table item.
        if (!ReadHeader(data, ref position, syntax, out var group, out var element, out _, out var tableLength)) return false;
        if (group != 0xFFFE || element != 0xE000 || tableLength == Undefined || position + tableLength > data.Length) return false;
        long secondFrameOffset = -1;
        if (tableLength >= 8 && state.Frames > 1)
            secondFrameOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
        position += (int)tableLength;
        var firstFragment = position;

        using var output = new MemoryStream();
        for (var n = 0; n < 100_000; n++)
        {
            var itemStart = position;
            if (!ReadHeader(data, ref position, syntax, out group, out element, out _, out var length)) break;
            if (group != 0xFFFE || element == 0xE0DD) break;
            if (element != 0xE000 || length == Undefined || position + length > data.Length) return false;
            if (secondFrameOffset >= 0 && itemStart - firstFragment >= secondFrameOffset) break;
            output.Write(data.Slice(position, (int)length));
            position += (int)length;
            if (output.Length > ManagedCodecSupport.MaxDecompressedBytes) return false;
            if (state.TransferSyntax == "1.2.840.10008.1.2.5") break; // RLE: one fragment per frame
            if (state.Frames > 1 && secondFrameOffset < 0 && length >= 2 &&
                data[position - 2] == 0xFF && data[position - 1] == 0xD9) break; // JPEG frame ended
        }
        frame = output.ToArray();
        return frame.Length > 0;
    }

    private static bool TryDecodeRle(ReadOnlySpan<byte> frame, State state, out byte[] native)
    {
        native = Array.Empty<byte>();
        if (frame.Length < 64) return false;
        var segments = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame);
        var bytesPerSample = state.BitsAllocated / 8;
        if (segments != state.Samples * bytesPerSample || segments is < 1 or > 15) return false;
        var pixels = state.Rows * state.Columns;
        native = new byte[(long)pixels * segments];
        var plane = new byte[pixels];
        for (var s = 0; s < segments; s++)
        {
            var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame[(4 + s * 4)..]);
            var end = s + 1 < segments ? (int)BinaryPrimitives.ReadUInt32LittleEndian(frame[(8 + s * 4)..]) : frame.Length;
            if (start < 64 || end > frame.Length || end < start) return false;
            if (!PackBits(frame[start..end], plane)) return false;
            // Segment order is per sample, most significant byte first; native output is planar LE.
            var sample = s / bytesPerSample;
            var byteIndex = bytesPerSample - 1 - s % bytesPerSample;
            for (var i = 0; i < pixels; i++)
                native[((long)sample * pixels + i) * bytesPerSample + byteIndex] = plane[i];
        }
        return true;
    }

    private static bool PackBits(ReadOnlySpan<byte> source, byte[] destination)
    {
        int input = 0, output = 0;
        while (output < destination.Length && input < source.Length)
        {
            int n = (sbyte)source[input++];
            if (n >= 0)
            {
                var count = n + 1;
                if (input + count > source.Length || output + count > destination.Length) return false;
                source.Slice(input, count).CopyTo(destination.AsSpan(output));
                input += count;
                output += count;
            }
            else if (n != -128)
            {
                var count = 1 - n;
                if (input >= source.Length || output + count > destination.Length) return false;
                destination.AsSpan(output, count).Fill(source[input++]);
                output += count;
            }
        }
        return output == destination.Length;
    }

    private static bool TryDecodeNative(ReadOnlySpan<byte> frame, State state, Syntax syntax, out DecodedImage image)
    {
        image = default;
        int width = state.Columns, height = state.Rows;
        var pixels = (long)width * height;
        var bytesPerSample = state.BitsAllocated / 8;
        if (frame.Length < pixels * state.Samples * bytesPerSample) return false;
        if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
        var big = syntax == Syntax.ExplicitBig;

        if (state.Samples == 3)
        {
            if (state.Photometric is not ("RGB" or "YBR_FULL")) return false;
            for (long i = 0; i < pixels; i++)
            {
                int c0, c1, c2;
                if (state.Planar == 1)
                {
                    c0 = Sample8(frame, i, bytesPerSample, big);
                    c1 = Sample8(frame, pixels + i, bytesPerSample, big);
                    c2 = Sample8(frame, 2 * pixels + i, bytesPerSample, big);
                }
                else
                {
                    c0 = Sample8(frame, i * 3, bytesPerSample, big);
                    c1 = Sample8(frame, i * 3 + 1, bytesPerSample, big);
                    c2 = Sample8(frame, i * 3 + 2, bytesPerSample, big);
                }
                int r = c0, g = c1, b = c2;
                if (state.Photometric == "YBR_FULL")
                {
                    r = Math.Clamp((int)Math.Round(c0 + 1.402 * (c2 - 128)), 0, 255);
                    g = Math.Clamp((int)Math.Round(c0 - 0.344136 * (c1 - 128) - 0.714136 * (c2 - 128)), 0, 255);
                    b = Math.Clamp((int)Math.Round(c0 + 1.772 * (c1 - 128)), 0, 255);
                }
                var o = i * 4;
                output[o] = (byte)b; output[o + 1] = (byte)g; output[o + 2] = (byte)r; output[o + 3] = 255;
            }
            image = new DecodedImage(width, height, output);
            return true;
        }

        if (!state.Photometric.StartsWith("MONOCHROME", StringComparison.Ordinal)) return false;
        var values = new float[pixels];
        var shift = Math.Max(0, state.HighBit + 1 - state.BitsStored);
        var mask = state.BitsStored >= 32 ? 0xFFFFFFFFu : (1u << state.BitsStored) - 1;
        var signBit = 1u << (state.BitsStored - 1);
        float min = float.MaxValue, max = float.MinValue;
        for (long i = 0; i < pixels; i++)
        {
            uint raw = bytesPerSample switch
            {
                1 => frame[(int)i],
                2 => big ? BinaryPrimitives.ReadUInt16BigEndian(frame[(int)(i * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(frame[(int)(i * 2)..]),
                _ => big ? BinaryPrimitives.ReadUInt32BigEndian(frame[(int)(i * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(frame[(int)(i * 4)..]),
            };
            var stored = (raw >> shift) & mask;
            double v = state.PixelRepresentation == 1 && (stored & signBit) != 0
                ? (long)stored - (state.BitsStored >= 32 ? 0x1_0000_0000L : 1L << state.BitsStored)
                : stored;
            var modality = (float)(v * state.Slope + state.Intercept);
            values[i] = modality;
            if (modality < min) min = modality;
            if (modality > max) max = modality;
        }

        var invert = state.Photometric == "MONOCHROME1";
        var useWindow = double.IsFinite(state.WindowCenter) && double.IsFinite(state.WindowWidth) && state.WindowWidth >= 1;
        var center = state.WindowCenter;
        var widthWindow = state.WindowWidth;
        var range = max - min;
        for (long i = 0; i < pixels; i++)
        {
            double t;
            if (useWindow)
            {
                // DICOM PS3.3 C.11.2.1.2 linear VOI function.
                var x = values[i];
                var lower = center - 0.5 - (widthWindow - 1) / 2;
                var upper = center - 0.5 + (widthWindow - 1) / 2;
                t = x <= lower ? 0 : x > upper ? 1 : widthWindow <= 1 ? 1 : ((x - (center - 0.5)) / (widthWindow - 1) + 0.5);
            }
            else
            {
                t = range > 0 ? (values[i] - min) / range : 0.5;
            }
            var g = (byte)Math.Clamp((int)(t * 255 + 0.5), 0, 255);
            if (invert) g = (byte)(255 - g);
            var o = i * 4;
            output[o] = g; output[o + 1] = g; output[o + 2] = g; output[o + 3] = 255;
        }
        image = new DecodedImage(width, height, output);
        return true;
    }

    private static int Sample8(ReadOnlySpan<byte> frame, long index, int bytesPerSample, bool big)
    {
        if (bytesPerSample == 1) return frame[(int)index];
        var o = (int)(index * bytesPerSample);
        // Use the most significant byte of wider samples.
        return big ? frame[o] : frame[o + bytesPerSample - 1];
    }

    /// <summary>
    /// DICOM Photometric Interpretation RGB means the three JPEG components already are R, G and B. A JPEG
    /// that carries no JFIF/Adobe marker and no R/G/B component ids is otherwise assumed to be YCbCr by
    /// libjpeg, so an Adobe APP14 "transform 0" marker is inserted to select plain RGB. Returns null when the
    /// stream needs no change (it already says what it is, or is not a 3-component DCT JPEG).
    /// </summary>
    private static byte[]? MarkJpegAsRgb(ReadOnlySpan<byte> jpeg)
    {
        var position = 2;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF) return null;
            var marker = jpeg[position + 1];
            if (marker == 0xFF) { position++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { position += 2; continue; }
            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (length < 2 || position + 2 + length > jpeg.Length) return null;
            var body = jpeg.Slice(position + 4, length - 2);
            if (marker == 0xE0 && body.Length >= 5 && body[..5].SequenceEqual("JFIF\0"u8)) return null;
            if (marker == 0xEE && body.Length >= 5 && body[..5].SequenceEqual("Adobe"u8)) return null;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                // precision(1) height(2) width(2) components(1) then id/sampling/table per component.
                if (body.Length < 6 + 9 || body[5] != 3) return null;
                if (body[6] == (byte)'R' && body[9] == (byte)'G' && body[12] == (byte)'B') return null;
                var result = new byte[jpeg.Length + 16];
                result[0] = 0xFF; result[1] = 0xD8;
                ReadOnlySpan<byte> adobe = new byte[] { 0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0x00, 0x64, 0, 0, 0, 0, 0 };
                adobe.CopyTo(result.AsSpan(2));
                jpeg[2..].CopyTo(result.AsSpan(18));
                return result;
            }
            if (marker == 0xDA) return null;
            position += 2 + length;
        }
        return null;
    }

    private static void Invert(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(255 - bgra[i]);
            bgra[i + 1] = (byte)(255 - bgra[i + 1]);
            bgra[i + 2] = (byte)(255 - bgra[i + 2]);
        }
    }
}

/// <summary>
/// NIfTI-1/NIfTI-2 single-file volumes (.nii, gzip-compressed .nii.gz): the middle axial slice of the
/// first volume, min/max normalised to grey (RGB24/RGBA32 volumes shown in colour), displayed with +Y up.
/// The file is streamed: only the header and the one slice are read (decompressed) from disk.
/// </summary>
internal static class NiftiDecoder
{
    private sealed class Header
    {
        public bool Big;
        public long Nx, Ny, Nz;
        public int DataType;
        public long VoxOffset;
        public double Slope = 1, Intercept;
        public int HeaderSize;
    }

    public static bool TryProbeFile(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            using var stream = Open(path);
            if (stream is null || !TryReadHeader(stream, out var header, out _)) return false;
            if (!ManagedCodecSupport.IsValidDimensions(header.Nx, header.Ny)) return false;
            dimensions = new ImageDimensions((int)header.Nx, (int)header.Ny);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDecodeFile(string path, out DecodedImage image)
    {
        image = default;
        try
        {
            using var stream = Open(path);
            if (stream is null || !TryReadHeader(stream, out var header, out var consumed)) return false;
            if (!ManagedCodecSupport.FitsMemory(header.Nx, header.Ny)) return false;
            var bpv = BytesPerVoxel(header.DataType);
            if (bpv == 0) return false;
            var sliceBytes = header.Nx * header.Ny * bpv;
            if (sliceBytes > int.MaxValue) return false;
            var z = header.Nz / 2;
            var target = header.VoxOffset + z * sliceBytes;
            if (target < consumed) return false;
            if (stream.CanSeek && target + sliceBytes > stream.Length) return false; // truncated volume
            if (!Skip(stream, target - consumed)) return false;
            var slice = new byte[sliceBytes];
            if (ManagedCodecSupport.ReadFully(stream, slice) != slice.Length) return false;
            return TryConvert(slice, header, out image);
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static Stream? Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        try
        {
            Span<byte> magic = stackalloc byte[2];
            var read = file.Read(magic);
            file.Position = 0;
            if (read == 2 && magic[0] == 0x1F && magic[1] == 0x8B)
                return new BufferedStream(new GZipStream(file, CompressionMode.Decompress), 65536);
            return file;
        }
        catch
        {
            file.Dispose();
            return null;
        }
    }

    private static bool Skip(Stream stream, long count)
    {
        if (count == 0) return true;
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length) return false;
            stream.Seek(count, SeekOrigin.Current);
            return true;
        }
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) return false;
            count -= read;
        }
        return true;
    }

    private static bool TryReadHeader(Stream stream, out Header header, out long consumed)
    {
        header = new Header();
        consumed = 0;
        var buffer = new byte[540];
        var read = ManagedCodecSupport.ReadFully(stream, buffer.AsSpan(0, 348));
        if (read < 348) return false;
        consumed = 348;
        ReadOnlySpan<byte> b = buffer;
        var sizeLe = BinaryPrimitives.ReadInt32LittleEndian(b);
        var sizeBe = BinaryPrimitives.ReadInt32BigEndian(b);
        if (sizeLe == 348 || sizeBe == 348)
        {
            header.Big = sizeBe == 348;
            if (!(b.Slice(344, 4).SequenceEqual("n+1\0"u8))) return false; // "ni1" = separate .img file
            var big1 = header.Big;
            short I16(int o) => big1 ? BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(o)) : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(o));
            float F32(int o) => big1 ? BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(o)) : BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(o));
            var rank = I16(40);
            header.Nx = I16(42);
            header.Ny = I16(44);
            header.Nz = rank >= 3 ? Math.Max((short)1, I16(46)) : 1;
            header.DataType = I16(70);
            var vox = F32(108);
            if (!float.IsFinite(vox) || vox < 348) return false;
            header.VoxOffset = (long)vox;
            var slope = F32(112);
            var inter = F32(116);
            header.Slope = float.IsFinite(slope) && slope != 0 ? slope : 1;
            header.Intercept = float.IsFinite(inter) && slope != 0 ? inter : 0;
            header.HeaderSize = 348;
            return header.Nx > 0 && header.Ny > 0 && header.Nz > 0;
        }
        if (sizeLe == 540 || sizeBe == 540)
        {
            read = ManagedCodecSupport.ReadFully(stream, buffer.AsSpan(348, 540 - 348));
            if (read < 540 - 348) return false;
            consumed = 540;
            b = buffer;
            header.Big = sizeBe == 540;
            if (!b.Slice(4, 3).SequenceEqual("n+2"u8)) return false;
            var big2 = header.Big;
            short I16(int o) => big2 ? BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(o)) : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(o));
            long I64(int o) => big2 ? BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(o)) : BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(o));
            double F64(int o) => big2 ? BinaryPrimitives.ReadDoubleBigEndian(buffer.AsSpan(o)) : BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(o));
            header.DataType = I16(12);
            var rank = I64(16);
            header.Nx = I64(24);
            header.Ny = I64(32);
            header.Nz = rank >= 3 ? Math.Max(1, I64(40)) : 1;
            header.VoxOffset = I64(168);
            var slope = F64(176);
            var inter = F64(184);
            header.Slope = double.IsFinite(slope) && slope != 0 ? slope : 1;
            header.Intercept = double.IsFinite(inter) && slope != 0 ? inter : 0;
            header.HeaderSize = 540;
            return header.VoxOffset >= 540 && header.Nx > 0 && header.Ny > 0 && header.Nz > 0 && header.Nz < int.MaxValue;
        }
        return false;
    }

    private static int BytesPerVoxel(int dataType) => dataType switch
    {
        2 or 256 => 1,       // uint8, int8
        4 or 512 => 2,       // int16, uint16
        8 or 16 or 768 => 4, // int32, float32, uint32
        64 or 1024 or 1280 => 8, // float64, int64, uint64
        128 => 3,            // RGB24
        2304 => 4,           // RGBA32
        _ => 0,
    };

    private static bool TryConvert(byte[] slice, Header header, out DecodedImage image)
    {
        image = default;
        int width = (int)header.Nx, height = (int)header.Ny;
        if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
        var count = (long)width * height;
        if (header.DataType is 128 or 2304)
        {
            var stride = header.DataType == 128 ? 3 : 4;
            for (long i = 0; i < count; i++)
            {
                var o = i * 4;
                var s = i * stride;
                output[o] = slice[s + 2]; output[o + 1] = slice[s + 1]; output[o + 2] = slice[s];
                output[o + 3] = stride == 4 ? slice[s + 3] : (byte)255;
            }
        }
        else
        {
            var values = new double[count];
            double min = double.MaxValue, max = double.MinValue;
            ReadOnlySpan<byte> s = slice;
            var big = header.Big;
            for (long i = 0; i < count; i++)
            {
                var o = (int)i;
                double v = header.DataType switch
                {
                    2 => s[o],
                    256 => (sbyte)s[o],
                    4 => big ? BinaryPrimitives.ReadInt16BigEndian(s[(o * 2)..]) : BinaryPrimitives.ReadInt16LittleEndian(s[(o * 2)..]),
                    512 => big ? BinaryPrimitives.ReadUInt16BigEndian(s[(o * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(s[(o * 2)..]),
                    8 => big ? BinaryPrimitives.ReadInt32BigEndian(s[(o * 4)..]) : BinaryPrimitives.ReadInt32LittleEndian(s[(o * 4)..]),
                    768 => big ? BinaryPrimitives.ReadUInt32BigEndian(s[(o * 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[(o * 4)..]),
                    16 => big ? BinaryPrimitives.ReadSingleBigEndian(s[(o * 4)..]) : BinaryPrimitives.ReadSingleLittleEndian(s[(o * 4)..]),
                    64 => big ? BinaryPrimitives.ReadDoubleBigEndian(s[(o * 8)..]) : BinaryPrimitives.ReadDoubleLittleEndian(s[(o * 8)..]),
                    1024 => big ? BinaryPrimitives.ReadInt64BigEndian(s[(o * 8)..]) : BinaryPrimitives.ReadInt64LittleEndian(s[(o * 8)..]),
                    _ => big ? BinaryPrimitives.ReadUInt64BigEndian(s[(o * 8)..]) : BinaryPrimitives.ReadUInt64LittleEndian(s[(o * 8)..]),
                };
                v = v * header.Slope + header.Intercept;
                values[i] = v;
                if (double.IsFinite(v))
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            var range = max - min;
            for (long i = 0; i < count; i++)
            {
                var v = values[i];
                var g = !double.IsFinite(v) ? (byte)0 : range > 0 ? (byte)Math.Clamp((int)((v - min) / range * 255 + 0.5), 0, 255) : (byte)128;
                var o = i * 4;
                output[o] = g; output[o + 1] = g; output[o + 2] = g; output[o + 3] = 255;
            }
        }
        // NIfTI stores +Y (anterior) upwards; flip so the first stored row ends up at the bottom.
        ManagedCodecSupport.FlipVertical(output, width, height);
        image = new DecodedImage(width, height, output);
        return true;
    }
}

/// <summary>
/// FITS (.fits/.fit/.fts, gzip .fts.gz): the first 2-D image HDU (primary or IMAGE extension; the first
/// plane of a cube). BITPIX 8/16/32/64/-32/-64 with BZERO/BSCALE/BLANK, displayed with a 0.5–99.5
/// percentile linear stretch and FITS' bottom-up row order.
/// </summary>
internal static class FitsDecoder
{
    private const int Block = 2880;

    private sealed class Hdu
    {
        public int Bitpix;
        public long[] Axes = Array.Empty<long>();
        public double Bzero, Bscale = 1;
        public long? Blank;
        public long DataStart;
        public long DataBytes;
        public long Pcount, Gcount = 1;
        public bool IsImage;
    }

    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            if (ManagedCodecSupport.IsGzip(data))
            {
                var prefix = new byte[Block * 64];
                var produced = ManagedCodecSupport.Inflate(data, prefix, ManagedCodecSupport.Wrapper.Gzip);
                if (produced <= 0) return false;
                return TryProbe(prefix.AsSpan(0, produced), out dimensions);
            }
            if (!TryFindImage(data, probeOnly: true, out var hdu)) return false;
            dimensions = new ImageDimensions((int)hdu.Axes[0], (int)hdu.Axes[1]);
            return dimensions.IsValid;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        try
        {
            if (ManagedCodecSupport.IsGzip(data))
            {
                if (!ManagedCodecSupport.TryGunzip(data, ManagedCodecSupport.MaxDecompressedBytes, out var inflated)) return false;
                return TryDecode(inflated, out image);
            }
            if (!TryFindImage(data, probeOnly: false, out var hdu)) return false;
            int width = (int)hdu.Axes[0], height = (int)hdu.Axes[1];
            if (!ManagedCodecSupport.FitsMemory(width, height)) return false;
            var count = (long)width * height;
            var bytes = Math.Abs(hdu.Bitpix) / 8;
            if (hdu.DataStart + count * bytes > data.Length) return false;
            var plane = data.Slice((int)hdu.DataStart, (int)(count * bytes));

            var values = new float[count];
            for (long i = 0; i < count; i++)
            {
                var o = (int)(i * bytes);
                double raw;
                long? integer = null;
                switch (hdu.Bitpix)
                {
                    case 8: integer = plane[o]; raw = plane[o]; break;
                    case 16: integer = BinaryPrimitives.ReadInt16BigEndian(plane[o..]); raw = integer.Value; break;
                    case 32: integer = BinaryPrimitives.ReadInt32BigEndian(plane[o..]); raw = integer.Value; break;
                    case 64: integer = BinaryPrimitives.ReadInt64BigEndian(plane[o..]); raw = integer.Value; break;
                    case -32: raw = BinaryPrimitives.ReadSingleBigEndian(plane[o..]); break;
                    default: raw = BinaryPrimitives.ReadDoubleBigEndian(plane[o..]); break;
                }
                values[i] = integer is not null && hdu.Blank == integer ? float.NaN : (float)(hdu.Bzero + hdu.Bscale * raw);
            }

            if (!TryStretchBounds(values, out var lo, out var hi)) { lo = 0; hi = 1; }
            var range = hi - lo;
            if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
            for (long i = 0; i < count; i++)
            {
                var v = values[i];
                var g = float.IsFinite(v) ? (byte)Math.Clamp((int)((v - lo) / range * 255f + 0.5f), 0, 255) : (byte)0;
                var o = i * 4;
                output[o] = g; output[o + 1] = g; output[o + 2] = g; output[o + 3] = 255;
            }
            ManagedCodecSupport.FlipVertical(output, width, height);
            image = new DecodedImage(width, height, output);
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static bool TryStretchBounds(float[] values, out float lo, out float hi)
    {
        lo = hi = 0;
        const int MaxSamples = 1 << 20;
        var stride = Math.Max(1, values.Length / MaxSamples);
        var samples = new float[(values.Length + stride - 1) / stride];
        var count = 0;
        float min = float.MaxValue, max = float.MinValue;
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (!float.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
            if (i % stride == 0) samples[count++] = v;
        }
        if (count == 0) return false;
        // Only two order statistics are needed: select them (O(n)) instead of sorting all samples.
        var loIndex = (int)(0.005 * (count - 1));
        var hiIndex = (int)Math.Ceiling(0.995 * (count - 1));
        lo = Select(samples, 0, count - 1, loIndex);
        hi = Select(samples, loIndex, count - 1, hiIndex);
        if (!(hi > lo)) { lo = min; hi = max; }
        if (!(hi > lo)) { lo = min - 0.5f; hi = min + 0.5f; }
        return true;
    }

    /// <summary>
    /// The k-th smallest of a[left..right] (inclusive) — the value a full sort would put at index k —
    /// reordering the range in place (Hoare quickselect, median-of-three). Values must be finite. A
    /// pathological input that keeps the partitions lopsided falls back to sorting the remaining range,
    /// so the worst case stays O(n log n).
    /// </summary>
    internal static float Select(float[] a, int left, int right, int k)
    {
        var budget = 2 * (BitOperations.Log2((uint)Math.Max(1, right - left + 1)) + 1);
        while (right > left)
        {
            if (budget-- <= 0)
            {
                Array.Sort(a, left, right - left + 1);
                return a[k];
            }
            var mid = left + (right - left) / 2;
            if (a[mid] < a[left]) (a[mid], a[left]) = (a[left], a[mid]);
            if (a[right] < a[left]) (a[right], a[left]) = (a[left], a[right]);
            if (a[right] < a[mid]) (a[right], a[mid]) = (a[mid], a[right]);
            var pivot = a[mid];
            int i = left, j = right;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j)
                {
                    (a[i], a[j]) = (a[j], a[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) right = j;
            else if (k >= i) left = i;
            else return a[k];
        }
        return a[k];
    }

    private static bool TryFindImage(ReadOnlySpan<byte> data, bool probeOnly, out Hdu image)
    {
        image = new Hdu();
        long position = 0;
        for (var index = 0; index < 64 && position + Block <= data.Length; index++)
        {
            if (!TryParseHdu(data, position, index == 0, out var hdu)) return false;
            if (hdu.IsImage && hdu.Axes.Length >= 2 && hdu.Axes[0] > 0 && hdu.Axes[1] > 0 &&
                hdu.Bitpix is 8 or 16 or 32 or 64 or -32 or -64)
            {
                if (!ManagedCodecSupport.IsValidDimensions(hdu.Axes[0], hdu.Axes[1])) return false;
                image = hdu;
                return true;
            }
            var next = hdu.DataStart + (hdu.DataBytes + Block - 1) / Block * Block;
            if (next <= position) return false;
            position = next;
            if (probeOnly && position >= data.Length) return false;
        }
        return false;
    }

    private static bool TryParseHdu(ReadOnlySpan<byte> data, long start, bool primary, out Hdu hdu)
    {
        hdu = new Hdu();
        var naxis = 0;
        var axes = new long[999];
        var xtension = string.Empty;
        for (long card = start; card + 80 <= data.Length && card < start + Block * 2000L; card += 80)
        {
            var text = Encoding.ASCII.GetString(data.Slice((int)card, 80));
            var key = text[..8].TrimEnd();
            if (card == start)
            {
                if (primary && (key != "SIMPLE" || !Value(text).StartsWith('T'))) return false;
                if (!primary && key != "XTENSION") return false;
            }
            if (key == "END")
            {
                hdu.DataStart = (card + 80 + Block - 1) / Block * Block;
                hdu.Axes = axes[..naxis];
                long product = naxis == 0 ? 0 : 1;
                foreach (var axis in hdu.Axes)
                {
                    if (axis < 0) return false;
                    product = axis == 0 ? 0 : checked(product * axis);
                }
                hdu.DataBytes = checked(Math.Abs(hdu.Bitpix) / 8 * hdu.Gcount * (hdu.Pcount + product));
                hdu.IsImage = primary || xtension == "IMAGE";
                return hdu.Bitpix != 0;
            }
            if (text.Length < 10 || text[8] != '=') continue;
            var value = Value(text);
            switch (key)
            {
                case "XTENSION": xtension = value.Trim('\'', ' '); break;
                case "BITPIX": hdu.Bitpix = (int)ParseInt(value); break;
                case "NAXIS": naxis = (int)Math.Clamp(ParseInt(value), 0, 999); break;
                case "BZERO": hdu.Bzero = ParseDouble(value, 0); break;
                case "BSCALE": hdu.Bscale = ParseDouble(value, 1); break;
                case "BLANK": hdu.Blank = ParseInt(value); break;
                case "PCOUNT": hdu.Pcount = Math.Max(0, ParseInt(value)); break;
                case "GCOUNT": hdu.Gcount = Math.Max(1, ParseInt(value)); break;
                default:
                    if (key.StartsWith("NAXIS", StringComparison.Ordinal) &&
                        int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= 999)
                        axes[n - 1] = ParseInt(value);
                    break;
            }
        }
        return false;
    }

    private static string Value(string card)
    {
        var text = card.Length > 10 ? card[10..] : string.Empty;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('\''))
        {
            var end = trimmed.IndexOf('\'', 1);
            return end > 0 ? trimmed[..(end + 1)] : trimmed;
        }
        var slash = text.IndexOf('/');
        return (slash >= 0 ? text[..slash] : text).Trim();
    }

    private static long ParseInt(string value)
        => long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value.Trim().Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : fallback;
}

/// <summary>Portable FloatMap (.pfm): "PF" colour / "Pf" grey, scale sign selects endianness, bottom-up rows.</summary>
internal static class PfmDecoder
{
    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryParseHeader(data, out var channels, out var width, out var height, out _, out _)) return false;
        _ = channels;
        dimensions = new ImageDimensions(width, height);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        try
        {
            if (!TryParseHeader(data, out var channels, out var width, out var height, out var little, out var start)) return false;
            if (!ManagedCodecSupport.FitsMemory(width, height)) return false;
            var count = (long)width * height;
            if (start + count * channels * 4 > data.Length) return false;
            if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
            for (var y = 0; y < height; y++)
            {
                var sourceRow = height - 1 - y; // stored bottom-to-top
                for (var x = 0; x < width; x++)
                {
                    var s = start + ((long)sourceRow * width + x) * channels * 4;
                    float r = ReadFloat(data, s, little), g = channels == 3 ? ReadFloat(data, s + 4, little) : r, b = channels == 3 ? ReadFloat(data, s + 8, little) : r;
                    ManagedCodecSupport.WriteLinearPixel(output, (long)y * width + x, r, g, b, 1f, premultiplied: false);
                }
            }
            image = new DecodedImage(width, height, output);
            return true;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    private static float ReadFloat(ReadOnlySpan<byte> data, long offset, bool little)
        => little ? BinaryPrimitives.ReadSingleLittleEndian(data[(int)offset..]) : BinaryPrimitives.ReadSingleBigEndian(data[(int)offset..]);

    private static bool TryParseHeader(ReadOnlySpan<byte> data, out int channels, out int width, out int height, out bool little, out int start)
    {
        channels = width = height = start = 0;
        little = false;
        if (data.Length < 8 || data[0] != 'P' || (data[1] != 'F' && data[1] != 'f')) return false;
        channels = data[1] == 'F' ? 3 : 1;
        var position = 2;
        var tokens = new string[3];
        for (var t = 0; t < 3; t++)
        {
            while (position < data.Length && data[position] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') position++;
            var begin = position;
            while (position < data.Length && data[position] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) position++;
            if (position == begin || position - begin > 32) return false;
            tokens[t] = Encoding.ASCII.GetString(data[begin..position]);
        }
        if (position >= data.Length) return false;
        position++; // single whitespace before the raster
        if (!int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
            !int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out height) ||
            !double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale == 0 || !double.IsFinite(scale))
            return false;
        little = scale < 0;
        start = position;
        return ManagedCodecSupport.IsValidDimensions(width, height);
    }
}
