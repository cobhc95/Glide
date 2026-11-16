using System.IO.Compression;
using System.Text;
using Glide.Imaging;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>PSD ZIP composites, OpenRaster, Krita, Paint.NET and GIMP XCF fixtures built in-test.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class LayeredDocumentDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    // ============================================================================================
    // PSD ZIP / ZIP with prediction
    // ============================================================================================

    private static byte[] Psd(int width, int height, int depth, int compression, byte[] imageData)
    {
        var w = new Writer { BigEndian = true };
        w.Ascii("8BPS").U16(1).Zeros(6).U16(3).U32(height).U32(width).U16(depth).U16(3);
        w.U32(0).U32(0).U32(0);
        w.U16(compression).Bytes(imageData);
        return w.ToArray();
    }

    // 3x2 RGB planes.
    private static readonly byte[][] Planes8 =
    {
        new byte[] { 10, 20, 30, 40, 50, 60 },
        new byte[] { 200, 100, 0, 255, 128, 1 },
        new byte[] { 5, 5, 5, 250, 250, 250 },
    };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Psd_zip_8bit(int compression)
    {
        var rows = new List<byte[]>();
        foreach (var plane in Planes8)
            for (var y = 0; y < 2; y++)
                rows.Add(plane.Skip(y * 3).Take(3).ToArray());
        if (compression == 3)
            rows = rows.Select(r => r.Select((v, i) => i == 0 ? v : (byte)(v - r[i - 1])).ToArray()).ToList();
        var pixels = Decode(Temp(_files, Psd(3, 2, 8, compression, Zlib(rows.SelectMany(r => r).ToArray())), ".psd"));
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 10, 200, 5);
        AssertColor(pixels.At(2, 1), 60, 1, 250);
    }

    [Fact]
    public void Psd_zip_prediction_16_and_32_bit()
    {
        // 16-bit: values v*257 so the decoder's high byte equals v.
        var w16 = new Writer { BigEndian = true };
        foreach (var plane in Planes8)
        {
            for (var y = 0; y < 2; y++)
            {
                ushort previous = 0;
                for (var x = 0; x < 3; x++)
                {
                    var value = (ushort)(plane[y * 3 + x] * 257);
                    w16.U16(x == 0 ? value : (ushort)(value - previous));
                    previous = value;
                }
            }
        }
        var p16 = Decode(Temp(_files, Psd(3, 2, 16, 3, Zlib(w16.ToArray())), ".psd"));
        AssertColor(p16.At(0, 0), 10, 200, 5);
        AssertColor(p16.At(2, 1), 60, 1, 250);

        // 32-bit float: rows byte-planed then byte-delta coded.
        float[] values = { 0f, 1f, 0.5f, 1f, 0.5f, 0f };
        var w32 = new List<byte>();
        for (var plane = 0; plane < 3; plane++)
        {
            for (var y = 0; y < 2; y++)
            {
                var row = new byte[12];
                for (var x = 0; x < 3; x++)
                {
                    var bytes = BitConverter.GetBytes(values[y * 3 + x]).Reverse().ToArray(); // big-endian
                    for (var b = 0; b < 4; b++) row[b * 3 + x] = bytes[b];
                }
                var delta = row.Select((v, i) => i == 0 ? v : (byte)(v - row[i - 1])).ToArray();
                w32.AddRange(delta);
            }
        }
        var p32 = Decode(Temp(_files, Psd(3, 2, 32, 3, Zlib(w32.ToArray())), ".psd"));
        AssertColor(p32.At(0, 0), 0, 0, 0);
        AssertColor(p32.At(1, 0), 255, 255, 255);
        AssertColor(p32.At(2, 0), 186, 186, 186, 1);
    }

    [Fact]
    public void Psd_zip_malformed_fails_gracefully()
    {
        var data = Zlib(new byte[18]);
        AssertFails(Temp(_files, Psd(3, 2, 8, 2, data.Take(data.Length / 2).ToArray()), ".psd"));
        // Tiny stream claiming a 30000x30000 canvas is refused before allocation.
        AssertFails(Temp(_files, Psd(30000, 30000, 8, 2, Zlib(new byte[64])), ".psd"));
        AssertFails(Temp(_files, Psd(3, 2, 8, 4, new byte[18]), ".psd"));
    }

    // ============================================================================================
    // ORA / KRA
    // ============================================================================================

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return output.ToArray();
    }

    private static byte[] Solid(int w, int h, SKColor color) => Png(w, h, (_, _) => color);

    [Fact]
    public void Ora_and_kra_use_mergedimage()
    {
        var merged = Png(3, 2, (x, y) => x == 0 ? new SKColor(255, 0, 0) : new SKColor(0, 255, 0, 128));
        var ora = Temp(_files, Zip(("mimetype", Encoding.ASCII.GetBytes("image/openraster")),
            ("stack.xml", Encoding.UTF8.GetBytes("<image w=\"3\" h=\"2\"><stack><layer src=\"data/a.png\"/></stack></image>")),
            ("data/a.png", Solid(3, 2, SKColors.Blue)), ("mergedimage.png", merged)), ".ora");
        var pixels = Decode(ora);
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(2, 1), 0, 255, 0, 1, a: 128);

        var kra = Temp(_files, Zip(("mimetype", Encoding.ASCII.GetBytes("application/x-krita")), ("maindoc.xml", Encoding.UTF8.GetBytes("<DOC/>")),
            ("mergedimage.png", merged), ("preview.png", Solid(1, 1, SKColors.White))), ".kra");
        var k = Decode(kra);
        Assert.Equal((3, 2), (k.Width, k.Height));
        AssertColor(k.At(0, 1), 255, 0, 0);

        var previewOnly = Temp(_files, Zip(("mimetype", Encoding.ASCII.GetBytes("application/x-krita")), ("preview.png", Solid(2, 2, SKColors.White))), ".kra");
        Assert.Equal((2, 2), (Decode(previewOnly).Width, Decode(previewOnly).Height));
    }

    [Fact]
    public void Ora_without_mergedimage_composites_the_stack()
    {
        var stack = "<?xml version=\"1.0\"?><image w=\"4\" h=\"2\"><stack>" +
                    "<stack opacity=\"0.5\"><layer src=\"data/white.png\" x=\"0\" y=\"1\"/></stack>" +
                    "<layer src=\"data/hidden.png\" visibility=\"hidden\"/>" +
                    "<layer src=\"data/top.png\" x=\"2\" y=\"0\" opacity=\"1.0\"/>" +
                    "<layer src=\"data/bottom.png\" x=\"0\" y=\"0\"/>" +
                    "</stack></image>";
        var ora = Temp(_files, Zip(("mimetype", Encoding.ASCII.GetBytes("image/openraster")), ("stack.xml", Encoding.UTF8.GetBytes(stack)),
            ("data/white.png", Solid(1, 1, SKColors.White)), ("data/hidden.png", Solid(4, 2, SKColors.Lime)),
            ("data/top.png", Solid(2, 2, SKColors.Blue)), ("data/bottom.png", Solid(4, 2, SKColors.Red))), ".ora");
        var pixels = Decode(ora);
        Assert.Equal((4, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(3, 0), 0, 0, 255);
        AssertColor(pixels.At(0, 1), 255, 128, 128, 1);
    }

    [Fact]
    public void Zip_documents_malformed_fail_gracefully()
    {
        AssertFails(Temp(_files, Zip(("mimetype", Encoding.ASCII.GetBytes("image/openraster"))), ".ora"));
        AssertFails(Temp(_files, Zip(("mergedimage.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 })), ".kra"));
        var good = Zip(("mergedimage.png", Solid(2, 2, SKColors.Red)));
        AssertFails(Temp(_files, good.Take(good.Length / 2).ToArray(), ".ora"));
        AssertFails(Temp(_files, Encoding.ASCII.GetBytes("not a zip"), ".kra"));
    }

    // ============================================================================================
    // PDN
    // ============================================================================================

    [Fact]
    public void Pdn_shows_its_embedded_thumbnail()
    {
        var thumb = Convert.ToBase64String(Png(4, 3, (x, _) => x < 2 ? SKColors.Red : SKColors.Blue));
        var xml = Encoding.UTF8.GetBytes($"<pdnImage width=\"400\" height=\"300\" layerCount=\"2\"><custom><thumb png=\"{thumb}\" /></custom></pdnImage>");
        var w = new Writer();
        w.Ascii("PDN3").U8(xml.Length & 0xFF).U8((xml.Length >> 8) & 0xFF).U8((xml.Length >> 16) & 0xFF).Bytes(xml).Bytes(new byte[] { 0, 1, 2, 3, 4 });
        var pixels = Decode(Temp(_files, w.ToArray(), ".pdn"));
        Assert.Equal((4, 3), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(3, 2), 0, 0, 255);

        AssertFails(Temp(_files, Encoding.ASCII.GetBytes("PDN3\x10\0\0<pdnImage/>"), ".pdn"));
        AssertFails(Temp(_files, Encoding.ASCII.GetBytes("PDN3\xFF\xFF\x00<pdnImage>"), ".pdn"));
    }

    // ============================================================================================
    // XCF
    // ============================================================================================

    private sealed record XcfLayer(int Width, int Height, int Type, int X, int Y, byte[] Pixels, int Opacity = 255, bool Visible = true, byte[]? Mask = null, bool NoTiles = false);

    private sealed class XcfBuilder
    {
        private readonly Writer _w = new() { BigEndian = true };
        private readonly int _version;
        private readonly int _compression;
        private int PointerSize => _version >= 11 ? 8 : 4;

        public XcfBuilder(int version, int compression)
        {
            _version = version;
            _compression = compression;
        }

        private long Pointer(long value = 0)
        {
            var at = _w.Position;
            if (PointerSize == 8) _w.U64(value); else _w.U32(value);
            return at;
        }

        private void Patch(long at, long value)
        {
            if (PointerSize == 8) _w.PatchU64(at, value); else _w.PatchU32(at, value);
        }

        private void Name(string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name + "\0");
            _w.U32(bytes.Length).Bytes(bytes);
        }

        public byte[] Build(int width, int height, int baseType, XcfLayer[] layers, byte[]? colormap = null, int precision = -1)
        {
            _w.Ascii(_version == 0 ? "gimp xcf file" : $"gimp xcf v{_version:D3}").U8(0);
            _w.U32(width).U32(height).U32(baseType);
            if (_version >= 4) _w.U32(precision);
            if (colormap is not null) _w.U32(1).U32(4 + colormap.Length).U32(colormap.Length / 3).Bytes(colormap);
            _w.U32(17).U32(1).U8(_compression);
            _w.U32(0).U32(0);
            var layerPointers = layers.Select(_ => Pointer()).ToArray();
            Pointer(0);
            Pointer(0); // no channels
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                Patch(layerPointers[i], _w.Position);
                _w.U32(layer.Width).U32(layer.Height).U32(layer.Type);
                Name("layer" + i);
                _w.U32(6).U32(4).U32(layer.Opacity);
                _w.U32(8).U32(4).U32(layer.Visible ? 1 : 0);
                _w.U32(15).U32(8).U32(layer.X).U32(layer.Y);
                if (layer.Mask is not null) _w.U32(11).U32(4).U32(1);
                _w.U32(0).U32(0);
                var hierarchy = Pointer();
                var mask = Pointer();
                Patch(hierarchy, _w.Position);
                WriteHierarchy(layer.Width, layer.Height, BytesPerPixel(layer.Type), layer.Pixels, layer.NoTiles);
                if (layer.Mask is not null)
                {
                    Patch(mask, _w.Position);
                    _w.U32(layer.Width).U32(layer.Height);
                    Name("mask");
                    _w.U32(0).U32(0);
                    var maskHierarchy = Pointer();
                    Patch(maskHierarchy, _w.Position);
                    WriteHierarchy(layer.Width, layer.Height, 1, layer.Mask);
                }
            }
            return _w.ToArray();
        }

        private static int BytesPerPixel(int type) => type switch { 0 => 3, 1 => 4, 2 => 1, 3 => 2, 4 => 1, _ => 2 };

        private void WriteHierarchy(int width, int height, int bpp, byte[] pixels, bool noTiles = false)
        {
            _w.U32(width).U32(height).U32(bpp);
            var level = Pointer();
            Pointer(0);
            Patch(level, _w.Position);
            _w.U32(width).U32(height);
            if (noTiles) { Pointer(0); return; } // a layer that was never painted has an empty tile list
            var tilesX = (width + 63) / 64;
            var tilesY = (height + 63) / 64;
            var tilePointers = new long[tilesX * tilesY];
            for (var t = 0; t < tilePointers.Length; t++) tilePointers[t] = Pointer();
            Pointer(0);
            for (var t = 0; t < tilePointers.Length; t++)
            {
                Patch(tilePointers[t], _w.Position);
                int tx = t % tilesX, ty = t / tilesX;
                int tw = Math.Min(64, width - tx * 64), th = Math.Min(64, height - ty * 64);
                var tile = new byte[tw * th * bpp];
                for (var y = 0; y < th; y++)
                    Array.Copy(pixels, ((ty * 64 + y) * width + tx * 64) * bpp, tile, y * tw * bpp, tw * bpp);
                _w.Bytes(_compression switch { 0 => tile, 1 => Rle(tile, bpp), _ => Zlib(tile) });
            }
        }

        private static byte[] Rle(byte[] tile, int bpp)
        {
            var output = new List<byte>();
            var count = tile.Length / bpp;
            for (var c = 0; c < bpp; c++)
            {
                var channel = Enumerable.Range(0, count).Select(i => tile[i * bpp + c]).ToArray();
                var i = 0;
                while (i < channel.Length)
                {
                    var run = 1;
                    while (i + run < channel.Length && channel[i + run] == channel[i] && run < 127) run++;
                    if (run >= 3)
                    {
                        output.Add((byte)(run - 1));
                        output.Add(channel[i]);
                        i += run;
                        continue;
                    }
                    var n = Math.Min(127, channel.Length - i);
                    var literal = 0;
                    while (literal < n && !(i + literal + 2 < channel.Length && channel[i + literal] == channel[i + literal + 1] && channel[i + literal] == channel[i + literal + 2])) literal++;
                    if (literal == 0) literal = 1;
                    output.Add((byte)(256 - literal));
                    for (var k = 0; k < literal; k++) output.Add(channel[i + k]);
                    i += literal;
                }
            }
            return output.ToArray();
        }
    }

    private static byte[] Fill(int count, params byte[] pixel) => Enumerable.Range(0, count).SelectMany(_ => pixel).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Xcf_flattens_visible_layers(int compression)
    {
        var layers = new[]
        {
            new XcfLayer(3, 2, 1, 0, 0, Fill(6, 0, 255, 0, 255), Visible: false),        // hidden green
            new XcfLayer(2, 1, 1, 1, 0, Fill(2, 0, 0, 255, 255)),                         // blue at (1,0)
            new XcfLayer(3, 2, 0, 0, 0, Fill(6, 255, 0, 0)),                              // red background
        };
        var pixels = Decode(Temp(_files, new XcfBuilder(0, compression).Build(3, 2, 0, layers), ".xcf"));
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(1, 0), 0, 0, 255);
        AssertColor(pixels.At(2, 0), 0, 0, 255);
        AssertColor(pixels.At(2, 1), 255, 0, 0);
    }

    [Fact]
    public void Xcf_gray_indexed_opacity_mask_and_64bit_pointers()
    {
        var gray = Decode(Temp(_files, new XcfBuilder(0, 0).Build(2, 1, 1, new[] { new XcfLayer(2, 1, 3, 0, 0, new byte[] { 100, 255, 200, 0 }) }), ".xcf"));
        AssertColor(gray.At(0, 0), 100, 100, 100);
        Assert.Equal(0, gray.At(1, 0).A);

        var colormap = new byte[] { 255, 0, 0, 0, 255, 0 };
        var indexed = Decode(Temp(_files, new XcfBuilder(0, 2).Build(2, 1, 2, new[] { new XcfLayer(2, 1, 4, 0, 0, new byte[] { 1, 0 }) }, colormap), ".xcf"));
        AssertColor(indexed.At(0, 0), 0, 255, 0);
        AssertColor(indexed.At(1, 0), 255, 0, 0);

        var half = Decode(Temp(_files, new XcfBuilder(0, 1).Build(1, 1, 0, new[]
        {
            new XcfLayer(1, 1, 0, 0, 0, new byte[] { 255, 255, 255 }, Opacity: 128),
            new XcfLayer(1, 1, 0, 0, 0, new byte[] { 0, 0, 0 }),
        }), ".xcf"));
        AssertColor(half.At(0, 0), 128, 128, 128, 1);

        var masked = Decode(Temp(_files, new XcfBuilder(0, 1).Build(2, 1, 0, new[]
        {
            new XcfLayer(2, 1, 1, 0, 0, Fill(2, 255, 0, 0, 255), Mask: new byte[] { 255, 0 }),
        }), ".xcf"));
        AssertColor(masked.At(0, 0), 255, 0, 0);
        Assert.Equal(0, masked.At(1, 0).A);

        // GIMP 2.10+ style: v011, 64-bit pointers, 8-bit perceptual precision, two tiles wide.
        var wide = Enumerable.Range(0, 70 * 2).SelectMany(i => new[] { (byte)(i % 70 * 3), (byte)(i / 70 * 200), (byte)7 }).ToArray();
        var modern = Decode(Temp(_files, new XcfBuilder(11, 1).Build(70, 2, 0, new[] { new XcfLayer(70, 2, 0, 0, 0, wide) }, precision: 150), ".xcf"));
        Assert.Equal((70, 2), (modern.Width, modern.Height));
        AssertColor(modern.At(65, 1), 195, 200, 7);
    }

    [Fact]
    public void Xcf_layer_without_tile_data_is_empty_not_an_error()
    {
        // GIMP writes never-painted layers with an empty tile list (its own test files do).
        var layers = new[]
        {
            new XcfLayer(2, 1, 1, 0, 0, Fill(2, 0, 0, 0, 0), NoTiles: true),
            new XcfLayer(2, 1, 0, 0, 0, Fill(2, 255, 0, 0)),
        };
        var pixels = Decode(Temp(_files, new XcfBuilder(0, 1).Build(2, 1, 0, layers), ".xcf"));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(1, 0), 255, 0, 0);
    }

    [Fact]
    public void Xcf_malformed_and_unsupported_fail_gracefully()
    {
        var good = new XcfBuilder(0, 1).Build(3, 2, 0, new[] { new XcfLayer(3, 2, 0, 0, 0, Fill(6, 255, 0, 0)) });
        AssertFails(Temp(_files, good.Take(good.Length - 6).ToArray(), ".xcf"));
        AssertFails(Temp(_files, Encoding.ASCII.GetBytes("gimp xcf filX\0").Concat(new byte[64]).ToArray(), ".xcf"));
        // 16-bit linear precision (v7+: 200) is not decoded.
        AssertFails(Temp(_files, new XcfBuilder(7, 0).Build(1, 1, 0, new[] { new XcfLayer(1, 1, 0, 0, 0, new byte[] { 1, 2, 3 }) }, precision: 200), ".xcf"));
    }
}
