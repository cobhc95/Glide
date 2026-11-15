using System.Buffers.Binary;
using System.Text;
using Glide.App.Services;
using Glide.Imaging;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.1 audit item 2: Glide writes one frame, so Save must never overwrite a multipage TIFF / animated / multi-image
/// original. Covers the page/frame probe (crafted fixtures, no external files) and the save guard.
/// </summary>
[Collection("ImageFrameProbe")]
public sealed class MultiFrameSaveGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glide-frames-" + Guid.NewGuid().ToString("N"));

    public MultiFrameSaveGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ImageFrameProbe.NativeCountOverrideForTests = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A valid TIFF with <paramref name="pages"/> 1x1 8-bit grey pages (uncompressed), chained IFDs.</summary>
    internal static byte[] Tiff(int pages, bool little = true, byte grey = 0x80)
    {
        const int entries = 9;
        const int ifdBytes = 2 + entries * 12 + 4;
        const int pageBytes = 2 + ifdBytes; // pixel (+1 pad byte) then the IFD
        var data = new byte[8 + pages * pageBytes];
        void U16(int at, int value) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)value); else BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)value); }
        void U32(int at, uint value) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value); else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), value); }

        data[0] = data[1] = (byte)(little ? 'I' : 'M');
        U16(2, 42);
        U32(4, 8 + 2);
        for (var page = 0; page < pages; page++)
        {
            var start = 8 + page * pageBytes;
            data[start] = grey;
            var ifd = start + 2;
            U16(ifd, entries);
            var e = ifd + 2;
            void Short(int tag, int value) { U16(e, tag); U16(e + 2, 3); U32(e + 4, 1); U16(e + 8, value); e += 12; }
            void Long(int tag, uint value) { U16(e, tag); U16(e + 2, 4); U32(e + 4, 1); U32(e + 8, value); e += 12; }
            Short(256, 1); Short(257, 1); Short(258, 8); Short(259, 1); Short(262, 1);
            Long(273, (uint)start); Short(277, 1); Short(278, 1); Long(279, 1);
            U32(e, page + 1 < pages ? (uint)(8 + (page + 1) * pageBytes + 2) : 0);
        }
        return data;
    }

    internal static byte[] AnimatedGif(int frames)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF });
        for (var i = 0; i < frames; i++)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00 });
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0x02, 0x02, 0x44, 0x01, 0x00 });
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static void Chunk(List<byte> png, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        png.AddRange(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
        png.AddRange(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.AddRange(crc);
    }

    private static byte[] Png(int? animationFrames)
    {
        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        if (animationFrames is { } frames)
        {
            var actl = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(actl, frames);
            Chunk(png, "acTL", actl);
        }
        Chunk(png, "IDAT", new byte[] { 0x78, 0x9C, 0x63, 0x60, 0x00, 0x02, 0x00, 0x00, 0x05, 0x00, 0x01 });
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] Ico(int images)
    {
        var ico = new byte[6 + images * 16];
        ico[2] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), (ushort)images);
        for (var i = 0; i < images; i++) { ico[6 + i * 16] = (byte)(16 << i); ico[6 + i * 16 + 1] = (byte)(16 << i); }
        return ico;
    }

    private static byte[] AnimatedWebp(int frames)
    {
        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());
        void Riff(string id, byte[] payload)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes(payload.Length));
            body.AddRange(payload);
            if ((payload.Length & 1) != 0) body.Add(0);
        }
        Riff("VP8X", new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        Riff("ANIM", new byte[6]);
        for (var i = 0; i < frames; i++) Riff("ANMF", new byte[16]);
        var riff = new List<byte>();
        riff.AddRange("RIFF"u8.ToArray());
        riff.AddRange(BitConverter.GetBytes(body.Count));
        riff.AddRange(body);
        return riff.ToArray();
    }

    // ------------------------------------------------------------------ probe

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    [InlineData(12, true)]
    public void Tiff_pages_are_counted_in_both_byte_orders(int pages, bool little)
    {
        var path = Write($"p{pages}-{little}.tif", Tiff(pages, little));
        Assert.Equal(pages, ImageFrameProbe.CountFramesManaged(path));
        Assert.Equal(pages, ImageFrameProbe.CountFrames(path));

        // When the native bridge is present, WIC (which the decoder uses) sees the same pages.
        if (NativeImageProbe.TryProbe(path, out var native)) Assert.Equal((uint)pages, native.FrameCount);
    }

    [Fact]
    public void Animated_and_multi_image_formats_are_counted()
    {
        Assert.Equal(4, ImageFrameProbe.CountFrames(Write("anim.gif", AnimatedGif(4))));
        Assert.Equal(1, ImageFrameProbe.CountFrames(Write("still.gif", AnimatedGif(1))));
        Assert.Equal(5, ImageFrameProbe.CountFrames(Write("anim.png", Png(5))));
        Assert.Equal(1, ImageFrameProbe.CountFrames(Write("still.png", Png(null))));
        Assert.Equal(3, ImageFrameProbe.CountFrames(Write("multi.ico", Ico(3))));
        Assert.Equal(2, ImageFrameProbe.CountFrames(Write("anim.webp", AnimatedWebp(2))));
    }

    [Fact]
    public void Single_frame_unknown_and_hostile_files_count_as_one()
    {
        Assert.Equal(1, ImageFrameProbe.CountFrames(Write("x.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9 })));
        Assert.Equal(0, ImageFrameProbe.CountFramesManaged(Write("x.bin", new byte[] { 1, 2, 3 })));
        Assert.Equal(1, ImageFrameProbe.CountFrames(Write("x.bin2", new byte[] { 1, 2, 3 })));
        Assert.Equal(1, ImageFrameProbe.CountFrames(Path.Combine(_dir, "missing.tif")));

        // An IFD chain that loops back on itself is counted once and the walk stops.
        var loop = Tiff(1);
        BinaryPrimitives.WriteUInt32LittleEndian(loop.AsSpan(loop.Length - 4), 10);
        Assert.Equal(1, ImageFrameProbe.CountFramesManaged(Write("loop.tif", loop)));

        // A truncated chain counts the pages whose directory is really there.
        var cut = Tiff(3);
        Assert.Equal(2, ImageFrameProbe.CountFramesManaged(Write("cut.tif", cut.AsSpan(0, cut.Length - 120).ToArray())));
    }

    [Fact]
    public void The_larger_of_the_native_and_managed_answers_wins_and_a_missing_bridge_falls_back()
    {
        var gif = Write("two.gif", AnimatedGif(2));
        ImageFrameProbe.NativeCountOverrideForTests = _ => null; // older/missing DLL without an answer
        Assert.Equal(2, ImageFrameProbe.CountFrames(gif));
        ImageFrameProbe.NativeCountOverrideForTests = _ => 7;    // e.g. a HEIF sequence only WIC understands
        Assert.Equal(7, ImageFrameProbe.CountFrames(Write("seq.heic", new byte[] { 0, 0, 0, 0x18 })));
        ImageFrameProbe.NativeCountOverrideForTests = _ => throw new IOException("bridge failed");
        Assert.Equal(2, ImageFrameProbe.CountFrames(gif));
    }

    // ------------------------------------------------------------------ policy and guard

    [Fact]
    public void Messages_name_pages_for_tiff_and_frames_otherwise()
    {
        Assert.False(ImageSavePolicy.BlocksInPlaceSave(1));
        Assert.True(ImageSavePolicy.BlocksInPlaceSave(2));
        Assert.Equal("This file has 3 pages. Saving would keep only the first. Use Save as to write a copy instead.",
            ImageSavePolicy.MultiFrameInPlaceMessage(@"C:\x\scan.tif", 3));
        Assert.Equal("This file has 12 frames. Saving would keep only the first. Use Save as to write a copy instead.",
            ImageSavePolicy.MultiFrameInPlaceMessage(@"C:\x\anim.png", 12));
        Assert.Contains("Only the first page will be written to the new file", ImageSavePolicy.MultiFrameCopyWarning("a.TIFF", 2));
        Assert.Contains("Choose a different file name", ImageSavePolicy.MultiFrameOverwriteMessage("a.gif", 2));
        Assert.True(ImageSavePolicy.IsSameFile(@"C:\Pics\A.TIF", @"c:\pics\.\a.tif"));
        Assert.False(ImageSavePolicy.IsSameFile(@"C:\Pics\a.tif", @"C:\Pics\a - Copy.tif"));
    }

    [Fact]
    public void In_place_save_of_a_multipage_tiff_is_refused_and_the_file_is_untouched()
    {
        var path = Write("scan.tif", Tiff(3));
        var before = File.ReadAllBytes(path);
        var px = new byte[2 * 2 * 4];

        var refused = Assert.Throws<MultiFrameSaveRefusedException>(() =>
            ImageSaveService.EncodeAndReplace(px, 2, 2, new Glide.Core.ImageEditTransform(1, false, false), path, 0.9f, sourcePath: path));

        Assert.Equal(3, refused.FrameCount);
        Assert.Equal("This file has 3 pages. Saving would keep only the first. Use Save as to write a copy instead.", refused.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Single_page_save_still_works_atomically_and_a_copy_of_a_multipage_file_is_allowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var single = Write("single.tif", Tiff(1));
        var px = new byte[4 * 3 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 20; px[i + 2] = 30; px[i + 3] = 255; }

        ImageSaveService.EncodeAndReplace(px, 4, 3, new Glide.Core.ImageEditTransform(1, false, false), single, 0.9f, sourcePath: single);
        Assert.True(ImageHeaderProbe.TryProbe(single, out var dims));
        Assert.Equal((3, 4), (dims.Width, dims.Height));
        Assert.Equal(1, ImageFrameProbe.CountFrames(single));

        var multi = Write("multi.tif", Tiff(3));
        var copy = Path.Combine(_dir, "multi-first-page.png");
        ImageSaveService.EncodeAndReplace(px, 4, 3, default, copy, 0.9f, sourcePath: multi);
        Assert.Equal(0x89, File.ReadAllBytes(copy)[0]);
        Assert.Equal(3, ImageFrameProbe.CountFrames(multi)); // the original keeps all of its pages
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }
}
