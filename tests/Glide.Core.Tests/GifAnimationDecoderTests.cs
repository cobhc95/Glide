using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// Hand-built GIF fixtures for the managed animation decoder: frame compositing, disposal
/// (restore-to-background), transparency, delay normalisation and interlaced row order.
/// </summary>
public sealed class GifAnimationDecoderTests
{
    // Palette: 0 red, 1 green, 2 blue, 3 white.
    private static readonly byte[] Palette = { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

    [Fact]
    public void Composites_frames_with_disposal_transparency_and_delays()
    {
        var gif = BuildGif(2, 2,
            new Frame(0, 0, 2, 2, new byte[] { 0, 1, 2, 3 }, DelayCs: 5, Disposal: 2, Transparent: -1),
            new Frame(0, 0, 2, 2, new byte[] { 0, 1, 0, 1 }, DelayCs: 0, Disposal: 0, Transparent: 0));

        var animation = GifAnimationDecoder.Decode(gif);

        Assert.NotNull(animation);
        Assert.Equal(2, animation!.Width);
        Assert.Equal(2, animation.Height);
        Assert.Equal(2, animation.Frames.Count);
        Assert.Equal(50, animation.Frames[0].DelayMs);
        Assert.Equal(100, animation.Frames[1].DelayMs); // 0 cs is clamped like browsers do

        // Frame 1: red, green / blue, white (BGRA).
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0xFF }, Pixel(animation.Frames[0], 0));
        Assert.Equal(new byte[] { 0, 0xFF, 0, 0xFF }, Pixel(animation.Frames[0], 1));
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF }, Pixel(animation.Frames[0], 2));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Pixel(animation.Frames[0], 3));

        // Frame 1 disposal 2 clears to transparent; frame 2's index 0 is transparent.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(animation.Frames[1], 0));
        Assert.Equal(new byte[] { 0, 0xFF, 0, 0xFF }, Pixel(animation.Frames[1], 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(animation.Frames[1], 2));
        Assert.Equal(new byte[] { 0, 0xFF, 0, 0xFF }, Pixel(animation.Frames[1], 3));
    }

    [Fact]
    public void Transparent_pixels_keep_the_previous_frame_when_not_disposed()
    {
        var gif = BuildGif(2, 1,
            new Frame(0, 0, 2, 1, new byte[] { 2, 2 }, DelayCs: 10, Disposal: 1, Transparent: -1),
            new Frame(0, 0, 2, 1, new byte[] { 0, 3 }, DelayCs: 10, Disposal: 0, Transparent: 0));

        var animation = GifAnimationDecoder.Decode(gif)!;

        Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF }, Pixel(animation.Frames[1], 0)); // blue kept
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Pixel(animation.Frames[1], 1));
    }

    [Fact]
    public void Interlaced_frames_are_written_in_display_row_order()
    {
        var gif = BuildGif(1, 8,
            new Frame(0, 0, 1, 8, new byte[] { 0, 1, 2, 3, 0, 1, 2, 3 }, DelayCs: 10, Disposal: 0, Transparent: -1, Interlaced: true),
            new Frame(0, 0, 1, 1, new byte[] { 3 }, DelayCs: 10, Disposal: 0, Transparent: -1));

        var first = GifAnimationDecoder.Decode(gif)!.Frames[0];
        // Stored rows 0..7 map to display rows 0,4,2,6,1,3,5,7.
        var expectedIndexByRow = new[] { 0, 0, 2, 1, 1, 2, 3, 3 };
        for (var row = 0; row < 8; row++)
            Assert.Equal(Bgra(expectedIndexByRow[row]), Pixel(first, row));
    }

    [Fact]
    public void Single_frame_and_non_gif_inputs_are_not_animations()
    {
        Assert.Null(GifAnimationDecoder.Decode(BuildGif(1, 1, new Frame(0, 0, 1, 1, new byte[] { 1 }, 10, 0, -1))));
        Assert.Null(GifAnimationDecoder.Decode(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    private sealed record Frame(int X, int Y, int W, int H, byte[] Indices, int DelayCs, int Disposal, int Transparent, bool Interlaced = false);

    private static byte[] Pixel(GifAnimationFrame frame, int index) => frame.Bgra.AsSpan(index * 4, 4).ToArray();

    private static byte[] Bgra(int paletteIndex) =>
        new[] { Palette[paletteIndex * 3 + 2], Palette[paletteIndex * 3 + 1], Palette[paletteIndex * 3], (byte)0xFF };

    private static byte[] BuildGif(int width, int height, params Frame[] frames)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange(new[] { (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8) });
        bytes.AddRange(new byte[] { 0x81, 0, 0 }); // global table, 4 entries
        bytes.AddRange(Palette);
        foreach (var frame in frames)
        {
            var packed = (byte)((frame.Disposal << 2) | (frame.Transparent >= 0 ? 1 : 0));
            bytes.AddRange(new byte[] { 0x21, 0xF9, 4, packed, (byte)frame.DelayCs, (byte)(frame.DelayCs >> 8), (byte)Math.Max(0, frame.Transparent), 0 });
            bytes.Add(0x2C);
            bytes.AddRange(new[] { (byte)frame.X, (byte)(frame.X >> 8), (byte)frame.Y, (byte)(frame.Y >> 8),
                (byte)frame.W, (byte)(frame.W >> 8), (byte)frame.H, (byte)(frame.H >> 8), (byte)(frame.Interlaced ? 0x40 : 0) });
            bytes.Add(2); // LZW minimum code size
            var data = EncodeLiteralLzw(frame.Indices);
            for (var i = 0; i < data.Length; i += 255)
            {
                var len = Math.Min(255, data.Length - i);
                bytes.Add((byte)len);
                bytes.AddRange(data.AsSpan(i, len).ToArray());
            }
            bytes.Add(0);
        }
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    /// <summary>Valid but trivially simple LZW: a clear code before every literal keeps codes at 3 bits.</summary>
    private static byte[] EncodeLiteralLzw(byte[] indices)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        void Emit(int code)
        {
            buffer |= code << bits;
            bits += 3;
            while (bits >= 8) { output.Add((byte)buffer); buffer >>= 8; bits -= 8; }
        }
        foreach (var index in indices) { Emit(4); Emit(index); }
        Emit(5);
        if (bits > 0) output.Add((byte)buffer);
        return output.ToArray();
    }
}
