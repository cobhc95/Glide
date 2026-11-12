namespace Glide.Imaging;

/// <summary>One fully composited animation frame: premultiplied BGRA8 at the GIF logical screen size.</summary>
public sealed record GifAnimationFrame(byte[] Bgra, int DelayMs);

/// <summary>A decoded animated GIF: logical canvas size and every composited frame in display order.</summary>
public sealed record GifAnimation(int Width, int Height, IReadOnlyList<GifAnimationFrame> Frames);

/// <summary>
/// Managed, dependency-free GIF87a/89a animation decoder. The main viewer pipeline still decodes and
/// presents the first frame through the normal codec path; this decoder only supplies the composited
/// frames needed to play the animation. It runs off the UI thread, honours frame disposal modes,
/// transparency, interlacing and local/global palettes, and refuses inputs that would exceed a memory
/// budget so a huge animation can never exhaust the process (it then simply stays static).
/// </summary>
public static class GifAnimationDecoder
{
    /// <summary>Upper bound for all composited frames together (bytes).</summary>
    public const long DefaultMemoryBudgetBytes = 384L * 1024 * 1024;
    private const int MaxDimension = 16384;

    /// <summary>
    /// Decodes every frame. Returns null when the file is not a GIF, has fewer than two frames, is
    /// malformed before the second frame, or would exceed <paramref name="memoryBudgetBytes"/>.
    /// </summary>
    public static GifAnimation? Decode(ReadOnlySpan<byte> data, long memoryBudgetBytes = DefaultMemoryBudgetBytes, CancellationToken cancellationToken = default)
    {
        if (data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F') return null;
        var width = data[6] | (data[7] << 8);
        var height = data[8] | (data[9] << 8);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension) return null;
        var frameBytes = (long)width * height * 4;
        if (frameBytes * 2 > memoryBudgetBytes) return null;

        var packed = data[10];
        var pos = 13;
        uint[]? globalPalette = null;
        if ((packed & 0x80) != 0)
        {
            var size = 1 << ((packed & 7) + 1);
            if (pos + size * 3 > data.Length) return null;
            globalPalette = ReadPalette(data.Slice(pos, size * 3), size);
            pos += size * 3;
        }

        var canvas = new uint[width * height];
        uint[]? restore = null;
        var frames = new List<GifAnimationFrame>();
        long totalBytes = 0;

        // Graphic Control Extension state applies to the next image only.
        var disposal = 0;
        var delayCs = 0;
        var transparentIndex = -1;

        try
        {
            while (pos < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = data[pos++];
                if (block == 0x3B) break; // trailer
                if (block == 0x21)
                {
                    if (pos >= data.Length) break;
                    var label = data[pos++];
                    if (label == 0xF9 && pos + 5 <= data.Length && data[pos] >= 4)
                    {
                        var gcePacked = data[pos + 1];
                        disposal = (gcePacked >> 2) & 7;
                        delayCs = data[pos + 2] | (data[pos + 3] << 8);
                        transparentIndex = (gcePacked & 1) != 0 ? data[pos + 4] : -1;
                    }
                    if (!SkipSubBlocks(data, ref pos)) break;
                    continue;
                }
                if (block != 0x2C) break; // unknown block: stop at what we have

                if (pos + 9 > data.Length) break;
                var fx = data[pos] | (data[pos + 1] << 8);
                var fy = data[pos + 2] | (data[pos + 3] << 8);
                var fw = data[pos + 4] | (data[pos + 5] << 8);
                var fh = data[pos + 6] | (data[pos + 7] << 8);
                var fPacked = data[pos + 8];
                pos += 9;
                var palette = globalPalette;
                if ((fPacked & 0x80) != 0)
                {
                    var size = 1 << ((fPacked & 7) + 1);
                    if (pos + size * 3 > data.Length) break;
                    palette = ReadPalette(data.Slice(pos, size * 3), size);
                    pos += size * 3;
                }
                var interlaced = (fPacked & 0x40) != 0;
                if (pos >= data.Length) break;
                var minCodeSize = data[pos++];

                // Gather the LZW sub-blocks into one contiguous buffer.
                var start = pos;
                var compressedLength = 0;
                while (pos < data.Length && data[pos] != 0)
                {
                    var len = data[pos];
                    compressedLength += len;
                    pos += len + 1;
                }
                if (pos >= data.Length) break;
                pos++; // block terminator
                var compressed = new byte[compressedLength];
                for (int p = start, o = 0; p < pos - 1;)
                {
                    var len = data[p++];
                    data.Slice(p, len).CopyTo(compressed.AsSpan(o, len));
                    o += len;
                    p += len;
                }

                if (palette is null || fw <= 0 || fh <= 0) { ResetControl(); continue; }
                var indices = new byte[fw * fh];
                DecodeLzw(compressed, minCodeSize, indices);

                if (disposal == 3) restore = (uint[])canvas.Clone();
                DrawFrame(canvas, width, height, indices, palette, fx, fy, fw, fh, interlaced, transparentIndex);

                totalBytes += frameBytes;
                if (totalBytes > memoryBudgetBytes) return null;
                frames.Add(new GifAnimationFrame(ToBytes(canvas), NormalizeDelay(delayCs)));

                // Apply this frame's disposal before the next frame is drawn.
                if (disposal == 2) ClearRect(canvas, width, height, fx, fy, fw, fh);
                else if (disposal == 3 && restore is not null) Array.Copy(restore, canvas, canvas.Length);
                ResetControl();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (frames.Count >= 2)
        {
            // Truncated/corrupt tail: play the frames decoded so far, like browsers do.
        }
        catch (Exception)
        {
            return null;
        }

        return frames.Count >= 2 ? new GifAnimation(width, height, frames) : null;

        void ResetControl()
        {
            disposal = 0;
            delayCs = 0;
            transparentIndex = -1;
        }
    }

    /// <summary>Browsers treat 0–1 cs delays as "as fast as allowed" and clamp them to 100 ms.</summary>
    internal static int NormalizeDelay(int centiseconds) => centiseconds <= 1 ? 100 : centiseconds * 10;

    private static uint[] ReadPalette(ReadOnlySpan<byte> rgb, int size)
    {
        var palette = new uint[256];
        for (var i = 0; i < size && i < 256; i++)
            palette[i] = 0xFF000000u | ((uint)rgb[i * 3] << 16) | ((uint)rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];
        return palette;
    }

    private static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            var len = data[pos++];
            if (len == 0) return true;
            pos += len;
        }
        return false;
    }

    private static void DecodeLzw(byte[] input, int minCodeSize, byte[] output)
    {
        if (minCodeSize is < 2 or > 11) minCodeSize = Math.Clamp(minCodeSize, 2, 11);
        const int MaxCodes = 4096;
        var prefix = new short[MaxCodes];
        var suffix = new byte[MaxCodes];
        var stack = new byte[MaxCodes + 1];
        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var codeSize = minCodeSize + 1;
        var codeMask = (1 << codeSize) - 1;
        var next = clear + 2;
        var old = -1;
        byte first = 0;
        for (var i = 0; i < clear; i++) { prefix[i] = -1; suffix[i] = (byte)i; }

        int bits = 0, datum = 0, inPos = 0, outPos = 0;
        while (outPos < output.Length)
        {
            while (bits < codeSize)
            {
                if (inPos >= input.Length) return; // ran out of data: leave the rest as index 0
                datum |= input[inPos++] << bits;
                bits += 8;
            }
            var code = datum & codeMask;
            datum >>= codeSize;
            bits -= codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                codeMask = (1 << codeSize) - 1;
                next = clear + 2;
                old = -1;
                continue;
            }
            if (code == end) return;

            if (old == -1)
            {
                if (code >= clear) return; // invalid first code
                output[outPos++] = suffix[code];
                old = code;
                first = suffix[code];
                continue;
            }

            var inCode = code;
            var sp = 0;
            if (code >= next)
            {
                if (code > next) return; // corrupt stream
                stack[sp++] = first;
                code = old;
            }
            while (code >= clear)
            {
                if (sp >= stack.Length - 1) return;
                stack[sp++] = suffix[code];
                code = prefix[code];
                if (code < 0) return;
            }
            first = suffix[code];
            stack[sp++] = first;

            if (next < MaxCodes)
            {
                prefix[next] = (short)old;
                suffix[next] = first;
                next++;
                if ((next & codeMask) == 0 && next < MaxCodes)
                {
                    codeSize++;
                    codeMask = (1 << codeSize) - 1;
                }
            }
            old = inCode;

            while (sp > 0 && outPos < output.Length) output[outPos++] = stack[--sp];
        }
    }

    private static void DrawFrame(uint[] canvas, int width, int height, byte[] indices, uint[] palette,
        int fx, int fy, int fw, int fh, bool interlaced, int transparentIndex)
    {
        for (var row = 0; row < fh; row++)
        {
            var destRow = interlaced ? InterlacedRow(row, fh) : row;
            var y = fy + destRow;
            if (y < 0 || y >= height) continue;
            var src = row * fw;
            var dst = y * width;
            for (var col = 0; col < fw; col++)
            {
                var x = fx + col;
                if (x < 0 || x >= width) continue;
                var index = indices[src + col];
                if (index == transparentIndex) continue;
                canvas[dst + x] = palette[index];
            }
        }
    }

    /// <summary>Maps the n-th stored row of an interlaced image to its display row.</summary>
    internal static int InterlacedRow(int n, int height)
    {
        var pass1 = (height + 7) / 8;
        if (n < pass1) return n * 8;
        n -= pass1;
        var pass2 = (height + 3) / 8;
        if (n < pass2) return n * 8 + 4;
        n -= pass2;
        var pass3 = (height + 1) / 4;
        if (n < pass3) return n * 4 + 2;
        n -= pass3;
        return n * 2 + 1;
    }

    private static void ClearRect(uint[] canvas, int width, int height, int fx, int fy, int fw, int fh)
    {
        var x0 = Math.Max(0, fx); var x1 = Math.Min(width, fx + fw);
        var y0 = Math.Max(0, fy); var y1 = Math.Min(height, fy + fh);
        for (var y = y0; y < y1; y++)
            Array.Clear(canvas, y * width + x0, Math.Max(0, x1 - x0));
    }

    /// <summary>ARGB (opaque or fully transparent) to premultiplied BGRA8 bytes.</summary>
    private static byte[] ToBytes(uint[] canvas)
    {
        var bytes = new byte[canvas.Length * 4];
        for (int i = 0, o = 0; i < canvas.Length; i++, o += 4)
        {
            var argb = canvas[i];
            if ((argb >> 24) == 0) continue; // transparent stays 0,0,0,0 (premultiplied)
            bytes[o] = (byte)argb;
            bytes[o + 1] = (byte)(argb >> 8);
            bytes[o + 2] = (byte)(argb >> 16);
            bytes[o + 3] = 0xFF;
        }
        return bytes;
    }
}
