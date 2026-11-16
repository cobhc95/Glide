using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

/// <summary>
/// OpenRaster (.ora) and Krita (.kra) documents are ZIP archives that carry a flattened
/// <c>mergedimage.png</c>; that composite is decoded with the bundled PNG codec. ORA files without it
/// are composited from <c>stack.xml</c> (PNG layers, normal blending, opacity/visibility/offsets);
/// KRA files without it fall back to the embedded <c>preview.png</c> thumbnail.
/// </summary>
internal static class ZipLayeredDecoder
{
    private const long MaxEntryBytes = 512L * 1024 * 1024;

    public static bool TryProbeFile(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            using var archive = OpenArchive(path);
            var entry = archive.GetEntry("mergedimage.png");
            if (entry is not null && TryReadPngSize(entry, out dimensions)) return true;
            if (archive.GetEntry("stack.xml") is { } stack && TryReadStack(stack, out var w, out var h, out _))
            {
                dimensions = new ImageDimensions(w, h);
                return ManagedCodecSupport.IsValidDimensions(w, h);
            }
            if (archive.GetEntry("preview.png") is { } preview && TryReadPngSize(preview, out dimensions)) return true;
            return false;
        }
        catch
        {
            dimensions = default;
            return false;
        }
    }

    public static bool TryDecodeFile(string path, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        try
        {
            using var archive = OpenArchive(path);
            if (archive.GetEntry("mergedimage.png") is { } merged && TryReadEntry(merged, out var png) &&
                ManagedCodecSupport.TryDecodeEncoded(png, maxLongestSide, out image, out _))
                return true;
            if (archive.GetEntry("stack.xml") is { } stack && TryComposite(archive, stack, out image)) return true;
            if (archive.GetEntry("preview.png") is { } preview && TryReadEntry(preview, out var thumb) &&
                ManagedCodecSupport.TryDecodeEncoded(thumb, 0, out image, out _))
                return true;
            return false;
        }
        catch
        {
            image = default;
            return false;
        }
    }

    /// <summary>Opens the document with delete/rename sharing so Explorer can still move the file.</summary>
    private static ZipArchive OpenArchive(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool TryReadEntry(ZipArchiveEntry entry, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (entry.Length <= 0 || entry.Length > MaxEntryBytes) return false;
        using var stream = entry.Open();
        bytes = new byte[entry.Length];
        return ManagedCodecSupport.ReadFully(stream, bytes) == bytes.Length;
    }

    private static bool TryReadPngSize(ZipArchiveEntry entry, out ImageDimensions dimensions)
    {
        dimensions = default;
        using var stream = entry.Open();
        Span<byte> header = stackalloc byte[24];
        if (ManagedCodecSupport.ReadFully(stream, header) != 24 || header[12] != 'I' || header[13] != 'H') return false;
        var w = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        var h = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        dimensions = new ImageDimensions(w, h);
        return ManagedCodecSupport.IsValidDimensions(w, h);
    }

    private sealed record LayerInfo(string Source, int X, int Y, float Opacity);

    private static XmlReaderSettings XmlSettings => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 16 * 1024 * 1024,
        IgnoreComments = true,
    };

    private static bool TryReadStack(ZipArchiveEntry stack, out int width, out int height, out List<LayerInfo> layers)
    {
        width = height = 0;
        layers = new List<LayerInfo>();
        if (stack.Length > 16 * 1024 * 1024) return false;
        using var stream = stack.Open();
        using var reader = XmlReader.Create(stream, XmlSettings);
        // Stack frames: (visible, opacity) multiplied down the tree; isolated group blending is approximated.
        var frames = new Stack<(bool Visible, float Opacity, int X, int Y)>();
        frames.Push((true, 1f, 0, 0));
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "stack")
            {
                if (frames.Count > 1) frames.Pop();
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            var parent = frames.Peek();
            switch (reader.Name)
            {
                case "image":
                    int.TryParse(reader.GetAttribute("w"), out width);
                    int.TryParse(reader.GetAttribute("h"), out height);
                    break;
                case "stack":
                    if (reader.IsEmptyElement) break;
                    frames.Push((parent.Visible && !IsHidden(reader), parent.Opacity * ReadOpacity(reader),
                        parent.X + ReadInt(reader, "x"), parent.Y + ReadInt(reader, "y")));
                    break;
                case "layer":
                    var src = reader.GetAttribute("src");
                    if (string.IsNullOrEmpty(src) || !parent.Visible || IsHidden(reader)) break;
                    layers.Add(new LayerInfo(src, parent.X + ReadInt(reader, "x"), parent.Y + ReadInt(reader, "y"),
                        parent.Opacity * ReadOpacity(reader)));
                    if (layers.Count > 4096) return false;
                    break;
            }
        }
        return width > 0 && height > 0;
    }

    private static bool IsHidden(XmlReader reader) => string.Equals(reader.GetAttribute("visibility"), "hidden", StringComparison.OrdinalIgnoreCase);

    private static float ReadOpacity(XmlReader reader)
        => float.TryParse(reader.GetAttribute("opacity"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o)
            ? Math.Clamp(o, 0f, 1f)
            : 1f;

    private static int ReadInt(XmlReader reader, string name)
        => int.TryParse(reader.GetAttribute(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static bool TryComposite(ZipArchive archive, ZipArchiveEntry stack, out DecodedImage image)
    {
        image = default;
        if (!TryReadStack(stack, out var width, out var height, out var layers)) return false;
        if (!ManagedCodecSupport.FitsMemory(width, height) || !ManagedCodecSupport.TryAllocatePixels(width, height, out var canvas)) return false;
        // stack.xml lists the top-most layer first.
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (layer.Opacity <= 0f) continue;
            if (archive.GetEntry(layer.Source) is not { } entry || !TryReadEntry(entry, out var png)) continue;
            if (!ManagedCodecSupport.TryDecodeEncoded(png, 0, out var pixels, out _)) continue;
            LayerBlend.Over(canvas, width, height, pixels.Bgra, pixels.Width, pixels.Height, layer.X, layer.Y, layer.Opacity, null);
        }
        image = new DecodedImage(width, height, canvas);
        return true;
    }
}

/// <summary>
/// Separable layer blend modes (Paint.NET's set; values match Paint.NET's LayerBlendMode enum). Names are
/// also matched against 3.x <c>UserBlendOps+XxxBlendOp</c> class names.
/// </summary>
internal enum LayerBlendMode
{
    Normal = 0, Multiply = 1, Additive = 2, ColorBurn = 3, ColorDodge = 4, Reflect = 5, Glow = 6,
    Overlay = 7, Difference = 8, Negation = 9, Lighten = 10, Darken = 11, Screen = 12, Xor = 13,
}

/// <summary>Straight-alpha "normal" (source-over) compositing shared by the layered decoders.</summary>
internal static class LayerBlend
{
    /// <summary>
    /// Composites with a separable blend mode: the result colour is the usual "source-atop/over" mix where
    /// the overlapping coverage takes <c>F(backdrop, source)</c>. <see cref="LayerBlendMode.Normal"/> is
    /// exactly <see cref="Over"/>.
    /// </summary>
    public static void Blend(byte[] canvas, int canvasWidth, int canvasHeight, byte[] layer, int layerWidth, int layerHeight,
        int offsetX, int offsetY, float opacity, LayerBlendMode mode)
    {
        if (mode == LayerBlendMode.Normal || !Enum.IsDefined(mode))
        {
            Over(canvas, canvasWidth, canvasHeight, layer, layerWidth, layerHeight, offsetX, offsetY, opacity, null);
            return;
        }
        var x0 = Math.Max(0, offsetX);
        var y0 = Math.Max(0, offsetY);
        var x1 = Math.Min(canvasWidth, (long)offsetX + layerWidth);
        var y1 = Math.Min(canvasHeight, (long)offsetY + layerHeight);
        var opacity255 = (int)Math.Round(Math.Clamp(opacity, 0f, 1f) * 255);
        if (y1 <= y0 || x1 <= x0) return;
        ManagedCodecSupport.ForBands((int)(y1 - y0), x1 - x0, (start, end) =>
        {
            for (var y = y0 + start; y < y0 + end; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var s = ((long)(y - offsetY) * layerWidth + (x - offsetX)) * 4;
                    long sa = layer[s + 3] * opacity255; // 0..65025
                    if (sa <= 0) continue;
                    var d = ((long)y * canvasWidth + x) * 4;
                    long da = canvas[d + 3] * 255;
                    var both = sa * da / 65025;          // source over backdrop: blended colour
                    var backdropOnly = da - both;
                    var sourceOnly = sa - both;
                    var total = both + backdropOnly + sourceOnly;
                    if (total <= 0) continue;
                    for (var c = 0; c < 3; c++)
                    {
                        int b = canvas[d + c], a = layer[s + c];
                        var f = Separable(mode, b, a);
                        var value = (b * backdropOnly + a * sourceOnly + f * both + total / 2) / total;
                        canvas[d + c] = (byte)Math.Clamp(value, 0, 255);
                    }
                    canvas[d + 3] = (byte)Math.Clamp((total + 127) / 255, 0, 255);
                }
            }
        });
    }

    /// <summary>Per-channel blend functions (backdrop <paramref name="b"/>, source <paramref name="a"/>), as defined by Paint.NET.</summary>
    private static int Separable(LayerBlendMode mode, int b, int a) => mode switch
    {
        LayerBlendMode.Multiply => b * a / 255,
        LayerBlendMode.Additive => Math.Min(255, b + a),
        LayerBlendMode.ColorBurn => a == 0 ? 0 : Math.Max(0, 255 - (255 - b) * 255 / a),
        LayerBlendMode.ColorDodge => a == 255 ? 255 : Math.Min(255, b * 255 / (255 - a)),
        LayerBlendMode.Reflect => a == 255 ? 255 : Math.Min(255, b * b / (255 - a)),
        LayerBlendMode.Glow => b == 255 ? 255 : Math.Min(255, a * a / (255 - b)),
        LayerBlendMode.Overlay => b < 128 ? 2 * b * a / 255 : 255 - 2 * (255 - b) * (255 - a) / 255,
        LayerBlendMode.Difference => Math.Abs(b - a),
        LayerBlendMode.Negation => 255 - Math.Abs(255 - b - a),
        LayerBlendMode.Lighten => Math.Max(b, a),
        LayerBlendMode.Darken => Math.Min(b, a),
        LayerBlendMode.Screen => b + a - b * a / 255,
        LayerBlendMode.Xor => b ^ a,
        _ => a,
    };

    public static void Over(byte[] canvas, int canvasWidth, int canvasHeight, byte[] layer, int layerWidth, int layerHeight,
        int offsetX, int offsetY, float opacity, byte[]? mask)
    {
        var x0 = Math.Max(0, offsetX);
        var y0 = Math.Max(0, offsetY);
        var x1 = Math.Min(canvasWidth, (long)offsetX + layerWidth);
        var y1 = Math.Min(canvasHeight, (long)offsetY + layerHeight);
        var opacity255 = (int)Math.Round(Math.Clamp(opacity, 0f, 1f) * 255);
        if (y1 <= y0 || x1 <= x0) return;
        // Rows are independent: blend row bands in parallel (identical to the serial loop).
        ManagedCodecSupport.ForBands((int)(y1 - y0), x1 - x0, (start, end) =>
        {
            for (var y = y0 + start; y < y0 + end; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var li = (long)(y - offsetY) * layerWidth + (x - offsetX);
                    var s = li * 4;
                    var sa = layer[s + 3] * opacity255;
                    if (mask is not null) sa = sa * mask[li] / 255;
                    if (sa <= 0) continue;
                    var d = ((long)y * canvasWidth + x) * 4;
                    // Work in 0..65025 (255*255) units for alpha.
                    var da = canvas[d + 3] * 255;
                    if (sa == 65025 || da == 0)
                    {
                        // Exact shortcuts of the general formula below: an opaque source (outA = 65025) or an
                        // empty destination (outA = sa) both reduce every colour channel to the source value.
                        canvas[d] = layer[s]; canvas[d + 1] = layer[s + 1]; canvas[d + 2] = layer[s + 2];
                        canvas[d + 3] = (byte)((sa + 127) / 255);
                        continue;
                    }
                    // 64-bit: da * (65025 - sa) reaches ~4.2e9 (a faint pixel over an opaque backdrop) and
                    // overflowed int, which dropped such pixels.
                    var outA = sa + (long)da * (65025 - sa) / 65025;
                    if (outA <= 0) continue;
                    for (var c = 0; c < 3; c++)
                    {
                        var value = (layer[s + c] * (long)sa + canvas[d + c] * (long)da * (65025 - sa) / 65025) / outA;
                        canvas[d + c] = (byte)Math.Clamp(value, 0, 255);
                    }
                    canvas[d + 3] = (byte)Math.Clamp((outA + 127) / 255, 0, 255);
                }
            }
        });
    }
}

/// <summary>
/// GIMP XCF (all file versions up to 64-bit pointers): flattens the visible layers with normal blending,
/// layer opacity, offsets, layer masks and group visibility/opacity. Tiles may be uncompressed, RLE or
/// zlib. 8-bit integer precision only (RGB/GRAY/INDEXED with or without alpha); higher-precision
/// images and non-normal blend modes (composited as normal) are limitations.
/// </summary>
internal static class XcfDecoder
{
    private const int TileSize = 64;

    private sealed class Image
    {
        public int Version, Width, Height, BaseType, Precision;
        public int Compression;
        public byte[]? Colormap;
        public int PointerSize => Version >= 11 ? 8 : 4;
        public bool Linear;
    }

    private sealed class Layer
    {
        public int Width, Height, Type;
        public bool Visible = true, IsGroup, ApplyMask = true;
        public float Opacity = 1f;
        public int X, Y;
        public long Hierarchy, Mask;
        public int[] Path = Array.Empty<int>();
    }

    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        if (!TryReadHeader(data, out var image, out _)) return false;
        dimensions = new ImageDimensions(image.Width, image.Height);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out DecodedImage result)
    {
        result = default;
        try
        {
            if (!TryReadHeader(data, out var image, out var position)) return false;
            if (!ManagedCodecSupport.FitsMemory(image.Width, image.Height)) return false;
            if (!ReadProperties(data, ref position, image, null)) return false;
            if (image.Compression is not (0 or 1 or 2)) return false;

            var layerPointers = new List<long>();
            for (var n = 0; n < 65536; n++)
            {
                if (!ReadPointer(data, ref position, image, out var pointer)) return false;
                if (pointer == 0) break;
                layerPointers.Add(pointer);
            }

            var layers = new List<Layer>();
            foreach (var pointer in layerPointers)
            {
                if (TryReadLayer(data, pointer, image, out var layer)) layers.Add(layer);
            }

            // Group visibility/opacity, keyed by item path.
            var groups = new Dictionary<string, (bool Visible, float Opacity)>();
            foreach (var layer in layers.Where(l => l.IsGroup))
                groups[string.Join('/', layer.Path)] = (layer.Visible, layer.Opacity);

            if (!ManagedCodecSupport.TryAllocatePixels(image.Width, image.Height, out var canvas)) return false;
            // The first layer in the file is the top of the stack.
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                var layer = layers[i];
                if (layer.IsGroup || !layer.Visible) continue;
                var opacity = layer.Opacity;
                var hidden = false;
                for (var depth = layer.Path.Length - 1; depth >= 1; depth--)
                {
                    if (groups.TryGetValue(string.Join('/', layer.Path[..depth]), out var group))
                    {
                        if (!group.Visible) { hidden = true; break; }
                        opacity *= group.Opacity;
                    }
                }
                if (hidden || opacity <= 0f) continue;
                if (!ManagedCodecSupport.FitsMemory(layer.Width, layer.Height)) continue;
                if (!TryReadPixels(data, layer.Hierarchy, image, layer.Type, layer.Width, layer.Height, out var pixels)) return false;
                byte[]? mask = null;
                if (layer.Mask != 0 && layer.ApplyMask && TryReadChannel(data, layer.Mask, image, layer.Width, layer.Height, out var maskPixels))
                    mask = maskPixels;
                LayerBlend.Over(canvas, image.Width, image.Height, pixels, layer.Width, layer.Height, layer.X, layer.Y, opacity, mask);
            }
            result = new DecodedImage(image.Width, image.Height, canvas);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> data, out Image image, out int position)
    {
        image = new Image();
        position = 0;
        if (data.Length < 26 || !data[..9].SequenceEqual("gimp xcf "u8) || data[13] != 0) return false;
        var tag = data.Slice(9, 4);
        if (tag.SequenceEqual("file"u8)) image.Version = 0;
        else if (tag[0] == 'v' && int.TryParse(Encoding.ASCII.GetString(tag[1..]), out var version)) image.Version = version;
        else return false;
        position = 14;
        image.Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[position..]), int.MaxValue);
        image.Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]), int.MaxValue);
        image.BaseType = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8)..]);
        position += 12;
        if (image.Version >= 4)
        {
            if (position + 4 > data.Length) return false;
            image.Precision = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            position += 4;
            // 8-bit integer only: v4-v6 encode it as 0 (gamma); v7+ as 100 (linear) / 150 (perceptual).
            var eightBit = image.Version < 7 ? image.Precision == 0 : image.Precision is 100 or 150;
            if (!eightBit) return false;
            image.Linear = image.Version >= 7 && image.Precision == 100;
        }
        return image.BaseType is 0 or 1 or 2 && ManagedCodecSupport.IsValidDimensions(image.Width, image.Height);
    }

    private static bool ReadPointer(ReadOnlySpan<byte> data, ref int position, Image image, out long pointer)
    {
        pointer = 0;
        if (position + image.PointerSize > data.Length) return false;
        pointer = image.PointerSize == 8 ? (long)BinaryPrimitives.ReadUInt64BigEndian(data[position..]) : BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += image.PointerSize;
        return pointer >= 0 && pointer < data.Length;
    }

    /// <summary>Reads a property list (image-level when <paramref name="layer"/> is null).</summary>
    private static bool ReadProperties(ReadOnlySpan<byte> data, ref int position, Image image, Layer? layer)
    {
        for (var n = 0; n < 4096; n++)
        {
            if (position + 8 > data.Length) return false;
            var type = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            position += 8;
            if (type == 0) return true; // PROP_END
            if (length > data.Length - position) return false;
            var payload = data.Slice(position, (int)length);
            position += (int)length;
            if (layer is null)
            {
                switch (type)
                {
                    case 1 when payload.Length >= 4: // PROP_COLORMAP
                        var colors = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(payload), 256);
                        if (payload.Length >= 4 + colors * 3) image.Colormap = payload.Slice(4, colors * 3).ToArray();
                        break;
                    case 17 when payload.Length >= 1: // PROP_COMPRESSION
                        image.Compression = payload[0];
                        break;
                }
                continue;
            }
            switch (type)
            {
                case 6 when payload.Length >= 4: layer.Opacity = Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(payload), 0u, 255u) / 255f; break;
                case 33 when payload.Length >= 4:
                    var opacity = BinaryPrimitives.ReadSingleBigEndian(payload);
                    if (float.IsFinite(opacity)) layer.Opacity = Math.Clamp(opacity, 0f, 1f);
                    break;
                case 8 when payload.Length >= 4: layer.Visible = BinaryPrimitives.ReadUInt32BigEndian(payload) != 0; break;
                case 11 when payload.Length >= 4: layer.ApplyMask = BinaryPrimitives.ReadUInt32BigEndian(payload) != 0; break;
                case 15 when payload.Length >= 8:
                    layer.X = BinaryPrimitives.ReadInt32BigEndian(payload);
                    layer.Y = BinaryPrimitives.ReadInt32BigEndian(payload[4..]);
                    break;
                case 29: layer.IsGroup = true; break; // PROP_GROUP_ITEM
                case 30: // PROP_ITEM_PATH
                    var path = new int[payload.Length / 4];
                    for (var i = 0; i < path.Length; i++) path[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(payload[(i * 4)..]);
                    layer.Path = path;
                    break;
            }
        }
        return false;
    }

    private static bool SkipString(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 4 > data.Length) return false;
        var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += 4;
        if (length > data.Length - position) return false;
        position += (int)length;
        return true;
    }

    private static bool TryReadLayer(ReadOnlySpan<byte> data, long pointer, Image image, out Layer layer)
    {
        layer = new Layer();
        var position = (int)pointer;
        if (position + 12 > data.Length) return false;
        layer.Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[position..]), int.MaxValue);
        layer.Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]), int.MaxValue);
        layer.Type = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8)..]);
        position += 12;
        if (!SkipString(data, ref position)) return false;
        if (!ReadProperties(data, ref position, image, layer)) return false;
        if (!ReadPointer(data, ref position, image, out layer.Hierarchy)) return false;
        if (!ReadPointer(data, ref position, image, out layer.Mask)) return false;
        return layer.Type is >= 0 and <= 5;
    }

    private static bool TryReadChannel(ReadOnlySpan<byte> data, long pointer, Image image, int width, int height, out byte[] mask)
    {
        mask = Array.Empty<byte>();
        var position = (int)pointer;
        if (position + 8 > data.Length) return false;
        var w = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
        if (w != width || h != height) return false;
        position += 8;
        if (!SkipString(data, ref position)) return false;
        var ignored = new Layer();
        if (!ReadProperties(data, ref position, image, ignored)) return false;
        if (!ReadPointer(data, ref position, image, out var hierarchy)) return false;
        if (!TryReadLevel(data, hierarchy, image, 1, width, height, out var raw)) return false;
        mask = raw;
        return true;
    }

    private static int BytesPerPixel(int type) => type switch
    {
        0 => 3, 1 => 4, 2 => 1, 3 => 2, 4 => 1, 5 => 2, _ => 0,
    };

    private static bool TryReadPixels(ReadOnlySpan<byte> data, long hierarchy, Image image, int type, int width, int height, out byte[] bgra)
    {
        bgra = Array.Empty<byte>();
        var bpp = BytesPerPixel(type);
        if (bpp == 0 || !TryReadLevel(data, hierarchy, image, bpp, width, height, out var raw)) return false;
        if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var output)) return false;
        bgra = output;
        var colormap = image.Colormap;
        var linear = image.Linear;
        // Pixels are independent: convert row bands in parallel (identical to the serial loop).
        ManagedCodecSupport.ForBands(height, width, (startRow, endRow) => ConvertPixels(raw, output, (long)startRow * width, (long)endRow * width, bpp, type, colormap, linear));
        return true;
    }

    private static void ConvertPixels(byte[] raw, byte[] bgra, long first, long last, int bpp, int type, byte[]? colormap, bool linear)
    {
        for (long i = first; i < last; i++)
        {
            var s = i * bpp;
            byte r, g, b, a = 255;
            switch (type)
            {
                case 0: r = raw[s]; g = raw[s + 1]; b = raw[s + 2]; break;
                case 1: r = raw[s]; g = raw[s + 1]; b = raw[s + 2]; a = raw[s + 3]; break;
                case 2: r = g = b = raw[s]; break;
                case 3: r = g = b = raw[s]; a = raw[s + 1]; break;
                default:
                    var index = raw[s];
                    if (colormap is not null && index * 3 + 2 < colormap.Length)
                    {
                        r = colormap[index * 3]; g = colormap[index * 3 + 1]; b = colormap[index * 3 + 2];
                    }
                    else
                    {
                        r = g = b = index;
                    }
                    if (type == 5) a = raw[s + 1];
                    break;
            }
            if (linear)
            {
                r = ManagedCodecSupport.LinearToSrgb8(r / 255f);
                g = ManagedCodecSupport.LinearToSrgb8(g / 255f);
                b = ManagedCodecSupport.LinearToSrgb8(b / 255f);
            }
            var o = i * 4;
            bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = a;
        }
    }

    /// <summary>Reads the full-resolution level of a hierarchy into an interleaved bpp buffer.</summary>
    private static bool TryReadLevel(ReadOnlySpan<byte> data, long hierarchy, Image image, int bpp, int width, int height, out byte[] raw)
    {
        raw = Array.Empty<byte>();
        var position = (int)hierarchy;
        if (position + 12 > data.Length) return false;
        var hw = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        var hh = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
        var hbpp = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8)..]);
        if (hw != width || hh != height || hbpp != bpp) return false;
        position += 12;
        if (!ReadPointer(data, ref position, image, out var level) || level == 0) return false;
        position = (int)level;
        if (position + 8 > data.Length) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[position..]) != width || BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]) != height) return false;
        position += 8;

        var tilesX = (width + TileSize - 1) / TileSize;
        var tilesY = (height + TileSize - 1) / TileSize;
        var tileCount = (long)tilesX * tilesY;
        // Every tile needs at least its pointer plus a few bytes of data; uncompressed tiles need all of it.
        if (tileCount * (image.PointerSize + 2) > data.Length - position) return false;
        if (image.Compression == 0 && (long)width * height * bpp > data.Length) return false;
        var pointers = new long[tileCount + 1];
        // A zero pointer ends the list early: GIMP writes layers that were never painted without tile data
        // (its own test files do), and those tiles are transparent/black rather than an error.
        var tilesPresent = tileCount;
        for (long t = 0; t < tileCount; t++)
        {
            if (!ReadPointer(data, ref position, image, out pointers[t])) return false;
            if (pointers[t] == 0) { tilesPresent = t; break; }
        }
        // The list is zero-terminated; the terminator also bounds the last zlib tile.
        pointers[tileCount] = data.Length;

        byte[] output;
        try { output = new byte[checked((long)width * height * bpp)]; }
        catch { return false; }
        // Tiles are independent and cover disjoint regions: decode them in parallel bands (identical result).
        var failed = 0;
        var compression = image.Compression;
        unsafe
        {
            fixed (byte* pointer = data)
            {
                var address = (nint)pointer;
                var length = data.Length;
                ManagedCodecSupport.ForBands((int)tileCount, TileSize * TileSize, (first, last) =>
                {
                    var file = new ReadOnlySpan<byte>((void*)address, length);
                    var tile = new byte[TileSize * TileSize * bpp];
                    for (long t = first; t < last && Volatile.Read(ref failed) == 0; t++)
                    {
                        if (t >= tilesPresent) break;
                        var tx = (int)(t % tilesX);
                        var ty = (int)(t / tilesX);
                        var tw = Math.Min(TileSize, width - tx * TileSize);
                        var th = Math.Min(TileSize, height - ty * TileSize);
                        var bytes = tw * th * bpp;
                        var start = pointers[t];
                        var end = Math.Max(start, Math.Min(file.Length, pointers[t + 1] > start ? pointers[t + 1] : file.Length));
                        var source = file[(int)start..(int)end];
                        var span = tile.AsSpan(0, bytes);
                        var ok = compression switch
                        {
                            0 => source.Length >= bytes && CopyTile(source, span, bytes),
                            1 => DecodeRleTile(file[(int)start..], span, bpp, tw * th),
                            _ => ManagedCodecSupport.Inflate(file[(int)start..], span, ManagedCodecSupport.Wrapper.Zlib) == bytes,
                        };
                        if (!ok) { Interlocked.Exchange(ref failed, 1); return; }
                        for (var y = 0; y < th; y++)
                        {
                            var destination = ((long)(ty * TileSize + y) * width + tx * TileSize) * bpp;
                            span.Slice(y * tw * bpp, tw * bpp).CopyTo(output.AsSpan((int)destination, tw * bpp));
                        }
                    }
                });
            }
        }
        if (failed != 0) return false;
        raw = output;
        return true;
    }

    private static bool CopyTile(ReadOnlySpan<byte> source, Span<byte> tile, int bytes)
    {
        source[..bytes].CopyTo(tile);
        return true;
    }

    private static bool DecodeRleTile(ReadOnlySpan<byte> source, Span<byte> tile, int bpp, int pixels)
    {
        var input = 0;
        for (var channel = 0; channel < bpp; channel++)
        {
            var output = channel;
            var remaining = pixels;
            while (remaining > 0)
            {
                if (input >= source.Length) return false;
                int value = source[input++];
                int length;
                if (value >= 128)
                {
                    length = 256 - value;
                    if (length == 128)
                    {
                        if (input + 2 > source.Length) return false;
                        length = (source[input] << 8) | source[input + 1];
                        input += 2;
                    }
                    if (length == 0 || length > remaining || input + length > source.Length) return false;
                    for (var i = 0; i < length; i++, output += bpp) tile[output] = source[input++];
                }
                else
                {
                    length = value + 1;
                    if (length == 128)
                    {
                        if (input + 2 > source.Length) return false;
                        length = (source[input] << 8) | source[input + 1];
                        input += 2;
                    }
                    if (length == 0 || length > remaining || input >= source.Length) return false;
                    var b = source[input++];
                    for (var i = 0; i < length; i++, output += bpp) tile[output] = b;
                }
                remaining -= length;
            }
        }
        return true;
    }
}
