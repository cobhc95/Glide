using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;
using FileSource = Glide.Imaging.ManagedCodecSupport.FileSource;

namespace Glide.Imaging;

/// <summary>
/// Kodak's private TIFF compression 65000, as used for the rendered previews of Kodak DCR files (DCS Pro 14n,
/// SLR/n, SLR/c, …: a 1125x750 and a 375x250 preview, YCbCr or RGB). Without it those files had no usable
/// preview at all. Port of dcraw's kodak_65000_decode / kodak_ycbcr_load_raw / kodak_rgb_load_raw (public
/// domain algorithm description by Dave Coffin). Only the preview strip is read; the sensor data is never touched.
/// Samples are 12-bit and display-referred; they are stretched by their own largest value like the other
/// uncompressed RAW previews.
/// </summary>
internal static class Kodak65000Decoder
{
    private const int MaxStripBytes = 64 << 20;

    public static bool TryDecode(FileSource source, long offset, long length, int width, int height, bool ycbcr, out DecodedImage image)
    {
        image = default;
        if (length <= 0 || length > MaxStripBytes || !ManagedCodecSupport.IsValidDimensions(width, height)) return false;
        var data = source.ReadBytes(offset, length);
        if (data is null || data.Length < length) return false;
        var samples = new ushort[(long)width * height * 3];
        var reader = new Reader(data);
        try
        {
            if (ycbcr) DecodeYCbCr(ref reader, samples, width, height);
            else DecodeRgb(ref reader, samples, width, height);
        }
        catch (IndexOutOfRangeException)
        {
            return false; // truncated or corrupt strip
        }

        if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var bgra)) return false;
        var max = 255;
        foreach (var s in samples) if (s > max) max = s;
        var pixels = (long)width * height;
        for (long p = 0; p < pixels; p++)
        {
            bgra[p * 4] = (byte)(samples[p * 3 + 2] * 255 / max);
            bgra[p * 4 + 1] = (byte)(samples[p * 3 + 1] * 255 / max);
            bgra[p * 4 + 2] = (byte)(samples[p * 3] * 255 / max);
            bgra[p * 4 + 3] = 255;
        }
        image = new DecodedImage(width, height, bgra);
        return true;
    }

    private static void DecodeYCbCr(ref Reader reader, ushort[] samples, int width, int height)
    {
        var buf = new short[384];
        Span<int> rgb = stackalloc int[3];
        var y = new int[2, 2];
        // 2x2 chroma blocks: the stream always covers an even width (a declared 1125x750 preview holds 1126 columns).
        var coded = (width + 1) & ~1;
        for (var row = 0; row < height; row += 2)
            for (var col = 0; col < coded; col += 128)
            {
                var len = Math.Min(128, coded - col);
                reader.Decode(buf, len * 3);
                y[0, 1] = y[1, 1] = 0;
                int cb = 0, cr = 0, bp = 0;
                for (var i = 0; i < len; i += 2, bp += 2)
                {
                    cb += buf[bp + 4];
                    cr += buf[bp + 5];
                    rgb[1] = -((cb + cr + 2) >> 2);
                    rgb[2] = rgb[1] + cb;
                    rgb[0] = rgb[1] + cr;
                    for (var j = 0; j < 2; j++)
                        for (var k = 0; k < 2; k++)
                        {
                            y[j, k] = y[j, k ^ 1] + buf[bp++]; // out-of-range values are clamped, as dcraw does
                            int r = row + j, c = col + i + k;
                            if (r >= height || c >= width) continue; // odd preview sizes (375x250)
                            var at = ((long)r * width + c) * 3;
                            for (var ch = 0; ch < 3; ch++) samples[at + ch] = (ushort)Math.Clamp(y[j, k] + rgb[ch], 0, 0xFFF);
                        }
                }
            }
    }

    private static void DecodeRgb(ref Reader reader, ushort[] samples, int width, int height)
    {
        var buf = new short[768];
        Span<int> rgb = stackalloc int[3];
        long at = 0;
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col += 256)
            {
                var len = Math.Min(256, width - col);
                var absolute = reader.Decode(buf, len * 3);
                rgb.Clear();
                var bp = 0;
                for (var i = 0; i < len; i++, at += 3)
                    for (var ch = 0; ch < 3; ch++)
                    {
                        rgb[ch] = absolute ? buf[bp++] : rgb[ch] + buf[bp++];
                        samples[at + ch] = (ushort)Math.Clamp(rgb[ch], 0, 0xFFF);
                    }
            }
    }

    private ref struct Reader
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly byte[] _blen = new byte[768];

        public Reader(byte[] data) { _data = data; _pos = 0; }

        private int Next() => _data[_pos++];

        /// <summary>
        /// One block of <paramref name="count"/> values (kodak_65000_decode): differences, or - when the block is stored
        /// raw (returns true) - absolute 12-bit values.
        /// </summary>
        public bool Decode(short[] output, int count)
        {
            var save = _pos;
            var size = (count + 3) & ~3;
            for (var i = 0; i < size; i += 2)
            {
                var c = Next();
                _blen[i] = (byte)(c & 15);
                _blen[i + 1] = (byte)(c >> 4);
                if (_blen[i] > 12 || _blen[i + 1] > 12)
                {
                    // Escape: the block is stored as packed 12-bit values (big-endian words, as in DCR files).
                    _pos = save;
                    Span<int> raw = stackalloc int[6];
                    for (var k = 0; k < size; k += 8)
                    {
                        for (var j = 0; j < 6; j++) raw[j] = Next() << 8 | Next();
                        output[k] = (short)(raw[0] >> 12 << 8 | raw[2] >> 12 << 4 | raw[4] >> 12);
                        output[k + 1] = (short)(raw[1] >> 12 << 8 | raw[3] >> 12 << 4 | raw[5] >> 12);
                        for (var j = 0; j < 6; j++) output[k + 2 + j] = (short)(raw[j] & 0xFFF);
                    }
                    return true;
                }
            }
            long bitbuf = 0;
            var bits = 0;
            if ((size & 7) == 4)
            {
                bitbuf = (long)Next() << 8;
                bitbuf += Next();
                bits = 16;
            }
            for (var i = 0; i < size; i++)
            {
                int len = _blen[i];
                if (bits < len)
                {
                    for (var j = 0; j < 32; j += 8) bitbuf += (long)Next() << (bits + (j ^ 8));
                    bits += 32;
                }
                var diff = (int)(bitbuf & (0xFFFF >> (16 - len)));
                bitbuf >>= len;
                bits -= len;
                if (len > 0 && (diff & (1 << (len - 1))) == 0) diff -= (1 << len) - 1;
                if (i < output.Length) output[i] = (short)diff;
            }
            return false;
        }
    }
}
