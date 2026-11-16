using System.Text;
using Glide.Imaging;
using SkiaSharp;
using static Glide.Core.Tests.ManagedDecoderTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// Paint.NET full-resolution decoding. Real .pdn files cannot be produced here, so <see cref="PdnBuilder"/>
/// emits a spec-conformant MS-NRBF stream mirroring Paint.NET's object graph (Document, LayerList : ArrayList,
/// BitmapLayer with Layer+properties / BitmapLayerProperties, Surface, MemoryBlock) followed by the deferred,
/// chunked, gzip-compressed BGRA layer data, inside the PDN3 container with a PNG thumbnail.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class PdnDecoderTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() => Cleanup(_files);

    // ============================================================================================
    // Fixture generator
    // ============================================================================================

    private sealed record PdnLayer(int Width, int Height, Func<int, int, SKColor> Color)
    {
        public bool Visible { get; init; } = true;
        public byte Opacity { get; init; } = 255;
        /// <summary>3.x style: class name of the BitmapLayerProperties.blendOp instance (null = no blendOp member).</summary>
        public string? BlendOp { get; init; } = "PaintDotNet.UserBlendOps+NormalBlendOp";
        /// <summary>4.x+ style: LayerProperties.blendMode enum value (boxed as value__).</summary>
        public int? BlendMode { get; init; }

        public byte[] Bgra()
        {
            var bytes = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var c = Color(x, y);
                    var o = (y * Width + x) * 4;
                    bytes[o] = c.Blue; bytes[o + 1] = c.Green; bytes[o + 2] = c.Red; bytes[o + 3] = c.Alpha;
                }
            return bytes;
        }
    }

    private enum Container { Deferred, GzipGraph, Pdn20 }

    private sealed class PdnBuilder
    {
        public int Width, Height;
        public List<PdnLayer> Layers = new();
        public Container Container = Container.Deferred;
        public int ChunkSize = 20;           // deliberately not a multiple of 4: chunks split pixels
        public int FormatVersion;            // 0 = gzip chunks, 1 = raw chunks
        public bool ShuffleChunks = true;
        public byte[]? Thumbnail = Png(2, 2, (_, _) => SKColors.Magenta);
        /// <summary>Hook to corrupt the graph: writes extra records/values instead of the normal Document.</summary>
        public Action<Nrbf>? CustomGraph;
        public Func<byte[], byte[]>? MutateDeferred;

        public byte[] Build()
        {
            var graph = new Nrbf();
            if (CustomGraph is not null) CustomGraph(graph);
            else WriteGraph(graph);
            var nrbf = graph.ToArray();

            var body = new Writer();
            switch (Container)
            {
                case Container.Deferred:
                    var deferred = Deferred();
                    if (MutateDeferred is not null) deferred = MutateDeferred(deferred);
                    body.U8(0).U8(1).Bytes(nrbf).Bytes(deferred);
                    break;
                default:
                    body.Bytes(Gzip(nrbf));
                    break;
            }
            if (Container == Container.Pdn20) return body.ToArray();

            var thumb = Thumbnail is null ? "" : $"<custom><thumb png=\"{Convert.ToBase64String(Thumbnail)}\" /></custom>";
            var xml = Encoding.UTF8.GetBytes($"<pdnImage width=\"{Width}\" height=\"{Height}\" layerCount=\"{Layers.Count}\" savedWithVersion=\"3.36.7079.4\">{thumb}</pdnImage>");
            var w = new Writer();
            w.Ascii("PDN3").U8(xml.Length & 0xFF).U8((xml.Length >> 8) & 0xFF).U8((xml.Length >> 16) & 0xFF).Bytes(xml).Bytes(body.ToArray());
            return w.ToArray();
        }

        private void WriteGraph(Nrbf g)
        {
            var inline = Container != Container.Deferred;
            g.Header(1);
            g.Library(2, "PaintDotNet.Data, Version=3.36.7079.4, Culture=neutral, PublicKeyToken=null");
            g.Class(1, "PaintDotNet.Document",
                Nrbf.Ref("layers", "PaintDotNet.LayerList", 3), Nrbf.Int("width", Width), Nrbf.Int("height", Height),
                Nrbf.SystemRef("userMetaData", "System.Collections.Specialized.NameValueCollection", 4),
                Nrbf.SystemRef("savedWith", "System.Version", 5));
            g.Class(3, "PaintDotNet.LayerList",
                Nrbf.Ref("parent", "PaintDotNet.Document", 1), Nrbf.ObjectArray("ArrayList+_items", 6),
                Nrbf.Int("ArrayList+_size", Layers.Count), Nrbf.Int("ArrayList+_version", Layers.Count));
            g.SystemClass(4, "System.Collections.Specialized.NameValueCollection",
                Nrbf.Bool("ReadOnly", false), Nrbf.Int("Count", 1), Nrbf.StringArray("Keys", 7), Nrbf.ObjectArray("Values", 8));
            g.SystemClass(5, "System.Version", Nrbf.Int("_Major", 3), Nrbf.Int("_Minor", 36), Nrbf.Int("_Build", 7079), Nrbf.Int("_Revision", 4));
            // ArrayList capacity padding is written as an ObjectNullMultiple256 run.
            g.ArraySingleObject(6, Layers.Count + 3, Layers.Select((_, i) => 10 + i * 10).ToArray());
            g.ArraySingleString(7, "$exif.tag0");
            g.ArraySingleObject(8, 1, Array.Empty<int>());

            for (var i = 0; i < Layers.Count; i++)
            {
                var layer = Layers[i];
                var b = 10 + i * 10;
                var layerMembers = new[]
                {
                    Nrbf.Ref("properties", "PaintDotNet.BitmapLayer+BitmapLayerProperties", b + 2),
                    Nrbf.Ref("surface", "PaintDotNet.Surface", b + 3),
                    Nrbf.Bool("disposed", false),
                    Nrbf.Int("Layer+width", layer.Width),
                    Nrbf.Int("Layer+height", layer.Height),
                    Nrbf.Ref("Layer+properties", "PaintDotNet.Layer+LayerProperties", b + 1),
                };
                // Later layers reuse the first BitmapLayer's metadata (ClassWithId), as BinaryFormatter does.
                if (i == 0) g.Class(b, "PaintDotNet.BitmapLayer", layerMembers);
                else g.ClassWithId(b, 10, layerMembers);

                var props = new List<Nrbf.Member>
                {
                    Nrbf.String("name", b + 6, i == 0 ? "Background" : $"Layer {i + 1}"),
                    Nrbf.NullMember("userMetaData"),
                    Nrbf.Bool("visible", layer.Visible),
                    Nrbf.Bool("isBackground", i == 0),
                    Nrbf.Byte("opacity", layer.Opacity),
                };
                if (layer.BlendMode is { } mode)
                    props.Add(Nrbf.Inline("blendMode", "PaintDotNet.LayerBlendMode", w => w.Class(b + 7, "PaintDotNet.LayerBlendMode", Nrbf.Int("value__", mode))));
                g.Class(b + 1, "PaintDotNet.Layer+LayerProperties", props.ToArray());

                if (layer.BlendOp is { } op)
                {
                    g.Class(b + 2, "PaintDotNet.BitmapLayer+BitmapLayerProperties", Nrbf.Ref("blendOp", op, b + 5));
                    g.Class(b + 5, op);
                }
                else
                {
                    g.Class(b + 2, "PaintDotNet.BitmapLayer+BitmapLayerProperties");
                }
                g.Class(b + 3, "PaintDotNet.Surface",
                    Nrbf.Int("width", layer.Width), Nrbf.Int("height", layer.Height), Nrbf.Int("stride", layer.Width * 4),
                    Nrbf.Ref("scan0", "PaintDotNet.MemoryBlock", b + 4), Nrbf.Bool("disposed", false));
                var length = (long)layer.Width * layer.Height * 4;
                if (inline)
                {
                    g.Class(b + 4, "PaintDotNet.MemoryBlock",
                        Nrbf.Long("length64", length), Nrbf.Bool("hasParent", false), Nrbf.Bool("deferred", false),
                        Nrbf.ByteArray("pointerData", b + 8));
                    g.ArraySinglePrimitiveBytes(b + 8, layer.Bgra());
                }
                else
                {
                    g.Class(b + 4, "PaintDotNet.MemoryBlock",
                        Nrbf.Long("length64", length), Nrbf.Int("bitmapWidth", layer.Width), Nrbf.Int("bitmapHeight", layer.Height),
                        Nrbf.Bool("hasParent", false), Nrbf.Bool("deferred", true));
                }
            }
            g.End();
        }

        private byte[] Deferred()
        {
            var w = new Writer { BigEndian = true };
            var random = new Random(1234);
            foreach (var layer in Layers)
            {
                var pixels = layer.Bgra();
                w.U8(FormatVersion).U32(ChunkSize);
                var count = (pixels.Length + ChunkSize - 1) / ChunkSize;
                var order = Enumerable.Range(0, count).ToArray();
                if (ShuffleChunks) random.Shuffle(order);
                foreach (var n in order)
                {
                    var chunk = pixels.Skip(n * ChunkSize).Take(ChunkSize).ToArray();
                    var data = FormatVersion == 0 ? Gzip(chunk) : chunk;
                    w.U32(n).U32(data.Length).Bytes(data);
                }
            }
            return w.ToArray();
        }
    }

    /// <summary>Minimal MS-NRBF writer (record layouts per [MS-NRBF] 2.1-2.7).</summary>
    private sealed class Nrbf
    {
        private readonly Writer _w = new();
        private readonly Dictionary<int, Member[]> _layouts = new();

        public sealed record Member(string Name, byte BinaryType, Action<Writer> Additional, Action<Nrbf> Value);

        public static Member Int(string name, int v) => new(name, 0, w => w.U8(8), n => n._w.U32(v));
        public static Member Long(string name, long v) => new(name, 0, w => w.U8(9), n => n._w.U64(v));
        public static Member Bool(string name, bool v) => new(name, 0, w => w.U8(1), n => n._w.U8(v ? 1 : 0));
        public static Member Byte(string name, byte v) => new(name, 0, w => w.U8(2), n => n._w.U8(v));
        public static Member String(string name, int id, string v) => new(name, 1, _ => { }, n => n.ObjectString(id, v));
        public static Member NullMember(string name) => new(name, 2, _ => { }, n => n._w.U8(10));
        public static Member Ref(string name, string type, int id) => new(name, 4, w => { Lps(w, type); w.U32(2); }, n => n.Reference(id));
        public static Member SystemRef(string name, string type, int id) => new(name, 3, w => Lps(w, type), n => n.Reference(id));
        public static Member ObjectArray(string name, int id) => new(name, 5, _ => { }, n => n.Reference(id));
        public static Member StringArray(string name, int id) => new(name, 6, _ => { }, n => n.Reference(id));
        public static Member ByteArray(string name, int id) => new(name, 7, w => w.U8(2), n => n.Reference(id));
        public static Member Inline(string name, string type, Action<Nrbf> record) => new(name, 4, w => { Lps(w, type); w.U32(2); }, record);
        public static Member Raw(string name, byte binaryType, Action<Writer> additional, Action<Writer> value) => new(name, binaryType, additional, n => value(n._w));

        public static void Lps(Writer w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            Length7(w, bytes.Length);
            w.Bytes(bytes);
        }

        public static void Length7(Writer w, long length)
        {
            var v = (uint)length;
            while (v >= 0x80) { w.U8((int)(v | 0x80)); v >>= 7; }
            w.U8((int)v);
        }

        public Writer Raw() => _w;
        public void Header(int root) => _w.U8(0).U32(root).U32(-1).U32(1).U32(0);
        public void Library(int id, string name) { _w.U8(12).U32(id); Lps(_w, name); }
        public void Reference(int id) => _w.U8(9).U32(id);
        public void ObjectString(int id, string v) { _w.U8(6).U32(id); Lps(_w, v); }
        public void End() => _w.U8(11);

        private void ClassRecord(byte type, int id, string name, Member[] members, bool library)
        {
            _w.U8(type).U32(id);
            Lps(_w, name);
            _w.U32(members.Length);
            foreach (var m in members) Lps(_w, m.Name);
            foreach (var m in members) _w.U8(m.BinaryType);
            foreach (var m in members) m.Additional(_w);
            if (library) _w.U32(2);
            _layouts[id] = members;
            foreach (var m in members) m.Value(this);
        }

        public void Class(int id, string name, params Member[] members) => ClassRecord(5, id, name, members, library: true);
        public void SystemClass(int id, string name, params Member[] members) => ClassRecord(4, id, name, members, library: false);

        public void ClassWithId(int id, int metadataId, params Member[] members)
        {
            _w.U8(1).U32(id).U32(metadataId);
            foreach (var m in members) m.Value(this);
        }

        public void ArraySingleObject(int id, int length, int[] references)
        {
            _w.U8(16).U32(id).U32(length);
            foreach (var r in references) Reference(r);
            var pad = length - references.Length;
            if (pad > 0) _w.U8(13).U8(pad);
        }

        public void ArraySingleString(int id, params string[] values)
        {
            _w.U8(17).U32(id).U32(values.Length);
            for (var i = 0; i < values.Length; i++) ObjectString(id * 100 + i, values[i]);
        }

        public void ArraySinglePrimitiveBytes(int id, byte[] data) => _w.U8(15).U32(id).U32(data.Length).U8(2).Bytes(data);

        public byte[] ToArray() => _w.ToArray();
    }

    private static PdnBuilder Doc(int width, int height, params PdnLayer[] layers) => new() { Width = width, Height = height, Layers = layers.ToList() };

    private static SKColor Gradient(int x, int y) => new((byte)(x * 40 + 10), (byte)(y * 60 + 5), (byte)(200 - x * 10 - y), (byte)(255 - x * y));

    private void AssertThumbnailFallback(byte[] file, string label)
    {
        var pixels = Decode(Temp(_files, file, ".pdn"), checkProbe: false);
        Assert.True((pixels.Width, pixels.Height) == (2, 2), $"{label}: expected the 2x2 thumbnail, got {pixels.Width}x{pixels.Height}");
        AssertColor(pixels.At(1, 1), 255, 0, 255);
    }

    // ============================================================================================
    // Decoding
    // ============================================================================================

    [Theory]
    [InlineData(20, 0, true)]
    [InlineData(4096, 0, false)]
    [InlineData(7, 1, true)]
    public void Single_layer_decodes_at_full_resolution(int chunkSize, int formatVersion, bool shuffle)
    {
        var doc = Doc(5, 3, new PdnLayer(5, 3, Gradient));
        doc.ChunkSize = chunkSize;
        doc.FormatVersion = formatVersion;
        doc.ShuffleChunks = shuffle;
        var pixels = Decode(Temp(_files, doc.Build(), ".pdn"));
        Assert.Equal((5, 3), (pixels.Width, pixels.Height));
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 5; x++)
            {
                var c = Gradient(x, y);
                AssertColor(pixels.At(x, y), c.Red, c.Green, c.Blue, a: c.Alpha);
            }
    }

    [Fact]
    public void Three_layers_honour_visibility_opacity_and_order()
    {
        var doc = Doc(4, 3,
            new PdnLayer(4, 3, (_, _) => SKColors.Red),
            new PdnLayer(4, 3, (_, _) => SKColors.Lime) { Visible = false },
            // 4.x style blendMode enum (0 = Normal), half opacity, transparent left column.
            new PdnLayer(4, 3, (x, _) => x == 0 ? SKColors.Transparent : SKColors.Blue) { Opacity = 128, BlendOp = null, BlendMode = 0 });
        var pixels = Decode(Temp(_files, doc.Build(), ".pdn"));
        Assert.Equal((4, 3), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(0, 0), 255, 0, 0);
        AssertColor(pixels.At(2, 1), 127, 0, 128, 1);
        AssertColor(pixels.At(3, 2), 127, 0, 128, 1);
    }

    [Fact]
    public void Faint_pixel_over_an_opaque_backdrop_is_blended()
    {
        // Regression: LayerBlend.Over computed da * (65025 - sa) in 32 bits, which overflowed for low source
        // alpha over an opaque backdrop and silently dropped the pixel (antialiased edges vanished).
        var doc = Doc(1, 1, new PdnLayer(1, 1, (_, _) => SKColors.White), new PdnLayer(1, 1, (_, _) => new SKColor(255, 0, 0, 9)));
        var pixels = Decode(Temp(_files, doc.Build(), ".pdn"));
        AssertColor(pixels.At(0, 0), 255, 246, 246);
    }

    /// <summary>
    /// Real files written by Paint.NET 4.0.21 (Normal + Additive at opacity 161) and 3.5.10 (two Normal
    /// layers), compared with Paint.NET's own flattened exports. Fixtures: pypdn test data, MIT
    /// (Fixtures/Pdn/LICENSE-pypdn.txt).
    /// </summary>
    [Theory]
    [InlineData("pdn4-additive-opacity")]
    [InlineData("pdn3510-two-layers")]
    public void Real_paint_net_files_match_their_flattened_exports(string name)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pdn");
        var pixels = Decode(Path.Combine(folder, name + ".pdn"));
        using var reference = SKBitmap.Decode(Path.Combine(folder, name + ".png"));
        Assert.Equal((reference.Width, reference.Height), (pixels.Width, pixels.Height));
        Assert.Equal((800, 600), (pixels.Width, pixels.Height));
        var worst = 0;
        for (var y = 0; y < reference.Height; y++)
            for (var x = 0; x < reference.Width; x++)
            {
                var expected = reference.GetPixel(x, y);
                var actual = pixels.At(x, y);
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(actual.R - expected.Red), Math.Abs(actual.G - expected.Green)),
                    Math.Max(Math.Abs(actual.B - expected.Blue), Math.Abs(actual.A - expected.Alpha))));
            }
        Assert.True(worst <= 1, $"{name}: worst channel difference {worst}");

        // A browse-size preview comes from the embedded 256 px thumbnail; the viewer-size one is a full decode.
        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(Path.Combine(folder, name + ".pdn"), 200, out var tw, out _, out _));
        Assert.Equal(256, tw);
        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(Path.Combine(folder, name + ".pdn"), 400, out var fw, out _, out _));
        Assert.Equal(800, fw);
    }

    [Fact]
    public void Blend_modes_map_from_blendOp_and_blendMode()
    {
        static PdnBuilder Two(PdnLayer top) => Doc(2, 1, new PdnLayer(2, 1, (_, _) => new SKColor(200, 100, 50)), top);

        // 3.x: BitmapLayerProperties.blendOp = UserBlendOps+MultiplyBlendOp.
        var multiply = Decode(Temp(_files, Two(new PdnLayer(2, 1, (_, _) => new SKColor(128, 255, 0)) { BlendOp = "PaintDotNet.UserBlendOps+MultiplyBlendOp" }).Build(), ".pdn"));
        AssertColor(multiply.At(0, 0), 100, 100, 0, 1);

        // 4.x: LayerProperties.blendMode = 12 (Screen).
        var screen = Decode(Temp(_files, Two(new PdnLayer(2, 1, (_, _) => new SKColor(128, 0, 255)) { BlendOp = null, BlendMode = 12 }).Build(), ".pdn"));
        AssertColor(screen.At(1, 0), 228, 100, 255, 1);

        // Difference at 50% opacity over an opaque base: halfway between base and |base - top|.
        var difference = Decode(Temp(_files, Two(new PdnLayer(2, 1, (_, _) => new SKColor(100, 100, 100)) { BlendOp = null, BlendMode = 8, Opacity = 128 }).Build(), ".pdn"));
        AssertColor(difference.At(0, 0), 150, 50, 50, 1);

        // Unknown blend op / future enum values composite as Normal.
        var unknown = Decode(Temp(_files, Two(new PdnLayer(2, 1, (_, _) => SKColors.Blue) { BlendOp = "PaintDotNet.UserBlendOps+FancyBlendOp" }).Build(), ".pdn"));
        AssertColor(unknown.At(0, 0), 0, 0, 255);
        var future = Decode(Temp(_files, Two(new PdnLayer(2, 1, (_, _) => SKColors.Blue) { BlendOp = null, BlendMode = 99 }).Build(), ".pdn"));
        AssertColor(future.At(0, 0), 0, 0, 255);
    }

    [Fact]
    public void Layer_size_must_match_the_document()
    {
        // Paint.NET layers always match the canvas; a mismatched surface means a damaged file.
        var doc = Doc(3, 2, new PdnLayer(3, 2, (_, _) => SKColors.White), new PdnLayer(1, 1, (_, _) => SKColors.Black));
        AssertThumbnailFallback(doc.Build(), "layer size");
    }

    [Fact]
    public void Tiny_file_claiming_a_huge_canvas_is_refused_before_allocation()
    {
        // 16000x16000 (~1 GB canvas) claimed by one chunk of a few bytes: deflate cannot expand that far.
        var doc = Doc(4, 3, new PdnLayer(4, 3, (_, _) => SKColors.Red));
        doc.CustomGraph = g =>
        {
            g.Header(1);
            g.Class(1, "PaintDotNet.Document", Nrbf.Ref("layers", "PaintDotNet.LayerList", 3), Nrbf.Int("width", 16000), Nrbf.Int("height", 16000));
            g.Class(3, "PaintDotNet.LayerList", Nrbf.ObjectArray("ArrayList+_items", 6), Nrbf.Int("ArrayList+_size", 1));
            g.ArraySingleObject(6, 1, new[] { 10 });
            g.Class(10, "PaintDotNet.BitmapLayer", Nrbf.Ref("surface", "PaintDotNet.Surface", 13));
            g.Class(13, "PaintDotNet.Surface", Nrbf.Int("width", 16000), Nrbf.Int("height", 16000), Nrbf.Int("stride", 64000), Nrbf.Ref("scan0", "PaintDotNet.MemoryBlock", 14));
            g.Class(14, "PaintDotNet.MemoryBlock", Nrbf.Long("length64", 16000L * 64000), Nrbf.Bool("hasParent", false), Nrbf.Bool("deferred", true));
            g.End();
        };
        doc.MutateDeferred = _ =>
        {
            var w = new Writer { BigEndian = true };
            var chunk = Gzip(new byte[4096]);
            w.U8(0).U32(-1).U32(0).U32(chunk.Length).Bytes(chunk);
            return w.ToArray();
        };
        var file = doc.Build();
        // Pretend memory is plentiful so only the data-size plausibility check can refuse it.
        DecodeMemoryGuard.SnapshotOverrideForTests = () => new DecodeMemoryGuard.MemorySnapshot(1UL << 42, 1UL << 42);
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            AssertThumbnailFallback(file, "huge canvas, tiny data");
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256L * 1024 * 1024, "the canvas must not be allocated");
        }
        finally
        {
            DecodeMemoryGuard.SnapshotOverrideForTests = null;
        }
    }

    [Fact]
    public void Gzip_wrapped_graph_and_paint_net_2_0_files_decode()
    {
        var legacy = Doc(3, 2, new PdnLayer(3, 2, Gradient), new PdnLayer(3, 2, (x, _) => x == 1 ? SKColors.Black : SKColors.Transparent));
        legacy.Container = Container.GzipGraph;
        var pixels = Decode(Temp(_files, legacy.Build(), ".pdn"));
        Assert.Equal((3, 2), (pixels.Width, pixels.Height));
        AssertColor(pixels.At(1, 1), 0, 0, 0);
        var c = Gradient(2, 1);
        AssertColor(pixels.At(2, 1), c.Red, c.Green, c.Blue, a: c.Alpha);

        legacy.Container = Container.Pdn20;
        var bare = Decode(Temp(_files, legacy.Build(), ".pdn"));
        Assert.Equal((3, 2), (bare.Width, bare.Height));
        AssertColor(bare.At(1, 0), 0, 0, 0);
    }

    [Fact]
    public void Bounded_preview_uses_the_thumbnail_only_when_it_is_large_enough()
    {
        var doc = Doc(8, 6, new PdnLayer(8, 6, (_, _) => SKColors.Red));
        doc.Thumbnail = Png(4, 3, (_, _) => SKColors.Cyan);
        var path = Temp(_files, doc.Build(), ".pdn");

        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(path, 4, out var tw, out var th, out var thumb));
        Assert.Equal((4, 3), (tw, th));
        AssertColor(new Pixels(tw, th, thumb).At(0, 0), 0, 255, 255);

        Assert.True(BuiltInRasterDecoder.TryDecodePixelsForTests(path, 6, out var fw, out var fh, out var full));
        Assert.Equal((8, 6), (fw, fh));
        AssertColor(new Pixels(fw, fh, full).At(7, 5), 255, 0, 0);

        Assert.True(BuiltInRasterDecoder.TryDecodeBounded(path, 4, out var bitmap));
        Assert.Equal(4, bitmap!.PixelSize.Width);
        bitmap.Dispose();

        // Full decode and probe report the document, not the thumbnail.
        var pixels = Decode(path);
        Assert.Equal((8, 6), (pixels.Width, pixels.Height));
    }

    // ============================================================================================
    // Malformed / hostile input: never throws, falls back to the thumbnail
    // ============================================================================================

    private static PdnBuilder Valid() => Doc(4, 3, new PdnLayer(4, 3, (_, _) => SKColors.Red), new PdnLayer(4, 3, (_, _) => SKColors.Blue) { Opacity = 100 });

    [Fact]
    public void Corrupt_pixel_data_falls_back_to_the_thumbnail()
    {
        // Bad gzip in a chunk.
        var badGzip = Valid();
        badGzip.MutateDeferred = d => { var copy = (byte[])d.Clone(); for (var i = 20; i < 40 && i < copy.Length; i++) copy[i] ^= 0x5A; return copy; };
        AssertThumbnailFallback(badGzip.Build(), "bad gzip");

        // Truncated in the deferred data.
        var good = Valid().Build();
        AssertThumbnailFallback(good.Take(good.Length - 30).ToArray(), "truncated");

        // Out-of-range chunk number, then a duplicate one (block header = version byte + chunk size; each
        // chunk = big-endian number + size + data).
        var outOfRange = Valid();
        outOfRange.ShuffleChunks = false;
        outOfRange.MutateDeferred = d => { var copy = (byte[])d.Clone(); copy[5] = 0x7F; return copy; };
        AssertThumbnailFallback(outOfRange.Build(), "chunk number out of range");
        var duplicate = Valid();
        duplicate.ShuffleChunks = false;
        duplicate.MutateDeferred = d =>
        {
            var copy = (byte[])d.Clone();
            var second = 5 + 8 + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(9));
            Assert.Equal(1, copy[second + 3]);
            copy[second + 3] = 0;
            return copy;
        };
        AssertThumbnailFallback(duplicate.Build(), "duplicate chunk number");

        // Zero chunk size, and a chunk size so small the stream cannot hold the implied chunk count.
        foreach (var size in new[] { 0, 1 })
        {
            var tiny = Valid();
            tiny.MutateDeferred = d => { var copy = (byte[])d.Clone(); copy[1] = 0; copy[2] = 0; copy[3] = 0; copy[4] = (byte)size; return copy; };
            AssertThumbnailFallback(tiny.Build(), $"chunk size {size}");
        }

        // Unknown deferred format version.
        var version = Valid();
        version.MutateDeferred = d => { var copy = (byte[])d.Clone(); copy[0] = 7; return copy; };
        AssertThumbnailFallback(version.Build(), "format version");
    }

    [Fact]
    public void Hostile_graphs_fall_back_to_the_thumbnail()
    {
        // Huge object array count.
        AssertThumbnailFallback(Graph(g =>
        {
            g.Header(1);
            g.Class(1, "PaintDotNet.Document", Nrbf.ObjectArray("layers", 2), Nrbf.Int("width", 4), Nrbf.Int("height", 3));
            g.Raw().U8(16).U32(2).U32(int.MaxValue);
            g.End();
        }), "huge array");

        // Huge primitive array and huge member count.
        AssertThumbnailFallback(Graph(g => { g.Header(1); g.Raw().U8(15).U32(1).U32(0x7FFFFFF0).U8(9); g.End(); }), "huge primitive array");
        AssertThumbnailFallback(Graph(g => { g.Header(1); g.Raw().U8(5).U32(1); Nrbf.Lps(g.Raw(), "X"); g.Raw().U32(int.MaxValue); g.End(); }), "huge member count");

        // Hostile string length (7-bit length of ~2 GiB).
        AssertThumbnailFallback(Graph(g => { g.Header(1); g.Raw().U8(6).U32(1).U8(0xFF).U8(0xFF).U8(0xFF).U8(0xFF).U8(0x07); g.End(); }), "string length");

        // Deep nesting: 500 inline struct records.
        AssertThumbnailFallback(Graph(g =>
        {
            g.Header(1);
            void Nest(Nrbf n, int depth)
            {
                if (depth == 0) { n.Class(100000, "Leaf", Nrbf.Int("v", 1)); return; }
                n.Class(1000 + depth, "Node", Nrbf.Inline("child", "Node", inner => Nest(inner, depth - 1)));
            }
            g.Class(1, "PaintDotNet.Document", Nrbf.Int("width", 4), Nrbf.Int("height", 3), Nrbf.Inline("layers", "Node", n => Nest(n, 500)));
            g.End();
        }), "deep nesting");

        // Cycles: layers -> Document itself; LayerList items -> LayerList; a dangling reference.
        AssertThumbnailFallback(Graph(g =>
        {
            g.Header(1);
            g.Class(1, "PaintDotNet.Document", Nrbf.Ref("layers", "PaintDotNet.Document", 1), Nrbf.Int("width", 4), Nrbf.Int("height", 3));
            g.End();
        }), "self cycle");
        AssertThumbnailFallback(Graph(g =>
        {
            g.Header(1);
            g.Class(1, "PaintDotNet.Document", Nrbf.Ref("layers", "PaintDotNet.LayerList", 3), Nrbf.Int("width", 4), Nrbf.Int("height", 3));
            g.Class(3, "PaintDotNet.LayerList", Nrbf.ObjectArray("ArrayList+_items", 6), Nrbf.Int("ArrayList+_size", 2));
            g.ArraySingleObject(6, 2, new[] { 3, 1 });
            g.End();
        }), "list cycle");
        AssertThumbnailFallback(Graph(g =>
        {
            g.Header(1);
            g.Class(1, "PaintDotNet.Document", Nrbf.Ref("layers", "PaintDotNet.LayerList", 777), Nrbf.Int("width", 4), Nrbf.Int("height", 3));
            g.End();
        }), "dangling reference");

        // Duplicate object id, unknown record type, wrong header version.
        AssertThumbnailFallback(Graph(g => { g.Header(1); g.ObjectString(1, "a"); g.ObjectString(1, "b"); g.End(); }), "duplicate id");
        AssertThumbnailFallback(Graph(g => { g.Header(1); g.Raw().U8(21); g.End(); }), "method call record");
        AssertThumbnailFallback(Graph(g => { g.Raw().U8(0).U32(1).U32(-1).U32(2).U32(0); g.End(); }), "header version");

        // Document claims a canvas that does not match its layer data / would not fit.
        var huge = Valid();
        huge.Width = 60000;
        huge.Height = 60000;
        AssertThumbnailFallback(huge.Build(), "huge canvas");
        var mismatch = Doc(4, 3, new PdnLayer(4, 3, (_, _) => SKColors.Red));
        mismatch.CustomGraph = g =>
        {
            // MemoryBlock length that disagrees with stride * height.
            g.Header(1);
            g.Class(1, "PaintDotNet.Document", Nrbf.Ref("layers", "PaintDotNet.LayerList", 3), Nrbf.Int("width", 4), Nrbf.Int("height", 3));
            g.Class(3, "PaintDotNet.LayerList", Nrbf.ObjectArray("ArrayList+_items", 6), Nrbf.Int("ArrayList+_size", 1));
            g.ArraySingleObject(6, 1, new[] { 10 });
            g.Class(10, "PaintDotNet.BitmapLayer", Nrbf.Ref("surface", "PaintDotNet.Surface", 13));
            g.Class(13, "PaintDotNet.Surface", Nrbf.Int("width", 4), Nrbf.Int("height", 3), Nrbf.Int("stride", 16), Nrbf.Ref("scan0", "PaintDotNet.MemoryBlock", 14));
            g.Class(14, "PaintDotNet.MemoryBlock", Nrbf.Long("length64", 1L << 40), Nrbf.Bool("hasParent", false), Nrbf.Bool("deferred", true));
            g.End();
        };
        AssertThumbnailFallback(mismatch.Build(), "length mismatch");
    }

    private static byte[] Graph(Action<Nrbf> write)
    {
        var doc = Valid();
        doc.CustomGraph = write;
        return doc.Build();
    }

    [Fact]
    public void Broken_file_without_thumbnail_fails_cleanly()
    {
        var doc = Valid();
        doc.Thumbnail = null;
        var good = doc.Build();
        Assert.Equal((4, 3), (Decode(Temp(_files, good, ".pdn")).Width, Decode(Temp(_files, good, ".pdn")).Height));
        AssertFails(Temp(_files, good.Take(good.Length - 10).ToArray(), ".pdn"));
        doc.Container = Container.Pdn20;
        var bare = doc.Build();
        AssertFails(Temp(_files, bare.Take(bare.Length / 2).ToArray(), ".pdn"));
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var original = Doc(6, 4, new PdnLayer(6, 4, Gradient), new PdnLayer(6, 4, (_, _) => SKColors.Blue) { Opacity = 77, BlendMode = 1, BlendOp = null }).Build();
        var random = new Random(4242);
        var path = Temp(_files, original, ".pdn");
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var copy = (byte[])original.Clone();
            var edits = random.Next(1, 6);
            for (var e = 0; e < edits; e++)
            {
                // Bias toward the graph/deferred part (after the XML header) where the new parser runs.
                var at = random.Next(copy.Length / 3, copy.Length);
                copy[at] = (byte)random.Next(256);
            }
            if (random.Next(5) == 0) copy = copy.Take(random.Next(8, copy.Length)).ToArray();
            File.WriteAllBytes(path, copy);
            var ok = BuiltInRasterDecoder.TryDecodePixelsForTests(path, 0, out var w, out var h, out var bgra);
            if (ok) Assert.True(bgra.Length >= (long)w * h * 4);
            BuiltInRasterDecoder.TryProbeDimensions(path, out _);
        }
    }
}
