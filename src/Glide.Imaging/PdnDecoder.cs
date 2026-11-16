using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DecodedImage = Glide.Imaging.BuiltInRasterDecoder.DecodedImage;

namespace Glide.Imaging;

/// <summary>
/// Paint.NET (.pdn), all versions that write the classic format (2.x, 3.x, 4.x and 5.x up to at least 5.2):
/// <c>"PDN3"</c>, a 24-bit little-endian length, a UTF-8 XML header (with a PNG thumbnail), then either
/// <c>00 01</c> + an MS-NRBF (.NET BinaryFormatter) object graph followed by the "deferred" layer pixel data,
/// or a gzip member wrapping the whole graph (2.1 beta and earlier; Paint.NET 2.0 files have no PDN3 header).
/// <para>
/// The object graph is never deserialized: <see cref="NrbfGraph"/> walks the record stream as plain data
/// (no type loading, no reflection, no object construction) under hard limits, and only width/height, the
/// layer list (visibility, opacity, blend mode) and each layer's Surface/MemoryBlock are read from it. Visible
/// layers are composited bottom-up with <see cref="LayerBlend"/>. Any failure falls back to the embedded
/// thumbnail (≤ 256 px), and a bounded preview no larger than that thumbnail is served from it directly.
/// </para>
/// </summary>
internal static class PdnDecoder
{
    private const int MaxLayers = 4096;
    private const long MaxGzipGraphBytes = 1L << 30;
    private const int ProbeGzipBytes = 4 * 1024 * 1024;
    private const long MaxDeflateRatio = 1032;

    private static readonly Regex ThumbRegex = new("<thumb\\s+png=\"([A-Za-z0-9+/=\\s]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex ImageTagRegex = new("<pdnImage\\b[^>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex WidthRegex = new("\\swidth=\"(\\d{1,9})\"", RegexOptions.CultureInvariant);
    private static readonly Regex HeightRegex = new("\\sheight=\"(\\d{1,9})\"", RegexOptions.CultureInvariant);
    private static readonly Regex LayerCountRegex = new("\\slayerCount=\"(\\d{1,9})\"", RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------------------------------------
    // Entry points
    // ---------------------------------------------------------------------------------------------

    public static bool TryProbe(ReadOnlySpan<byte> data, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            if (TryProbeDocument(data, out var width, out var height))
            {
                dimensions = new ImageDimensions(width, height);
                return true;
            }
        }
        catch
        {
            // Fall through to the thumbnail.
        }
        if (!TryGetThumbnail(data, out var png)) return false;
        if (!ManagedCodecSupport.TryProbeEncoded(png, out var w, out var h, out _)) return false;
        dimensions = new ImageDimensions(w, h);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        try
        {
            if (maxLongestSide > 0 && TryThumbnailForPreview(data, maxLongestSide, out image)) return true;
            if (TryDecodeDocument(data, out image)) return true;
        }
        catch
        {
            // Any parse failure (including allocation failure) falls back to the thumbnail.
        }
        image = default;
        return TryGetThumbnail(data, out var png) && ManagedCodecSupport.TryDecodeEncoded(png, 0, out image, out _);
    }

    // ---------------------------------------------------------------------------------------------
    // Container
    // ---------------------------------------------------------------------------------------------

    /// <summary>Header fields of the PDN3 container (all optional; the graph is authoritative).</summary>
    private readonly record struct Header(bool HasHeader, int Width, int Height, int LayerCount, int BodyOffset);

    private static bool TryReadHeader(ReadOnlySpan<byte> data, out Header header, out string xml)
    {
        header = default;
        xml = string.Empty;
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            header = new Header(false, 0, 0, 0, 0); // Paint.NET 2.0: the whole file is gzip'd BinaryFormatter output.
            return true;
        }
        if (data.Length < 8 || !data[..4].SequenceEqual("PDN3"u8)) return false;
        var length = data[4] | (data[5] << 8) | (data[6] << 16);
        if (length <= 0 || 7L + length > data.Length) return false;
        xml = Encoding.UTF8.GetString(data.Slice(7, length));
        int width = 0, height = 0, layers = 0;
        var tag = ImageTagRegex.Match(xml);
        if (tag.Success)
        {
            var w = WidthRegex.Match(tag.Value);
            var h = HeightRegex.Match(tag.Value);
            var l = LayerCountRegex.Match(tag.Value);
            if (!w.Success || !int.TryParse(w.Groups[1].Value, out width)) width = 0;
            if (!h.Success || !int.TryParse(h.Groups[1].Value, out height)) height = 0;
            if (!l.Success || !int.TryParse(l.Groups[1].Value, out layers)) layers = 0;
        }
        header = new Header(true, width, height, layers, 7 + length);
        return true;
    }

    private static bool TryGetThumbnail(ReadOnlySpan<byte> data, out byte[] png)
    {
        png = Array.Empty<byte>();
        try
        {
            if (!TryReadHeader(data, out var header, out var xml) || !header.HasHeader) return false;
            var match = ThumbRegex.Match(xml);
            if (!match.Success) return false;
            png = Convert.FromBase64String(match.Groups[1].Value);
            return png.Length > 8;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A bounded preview no larger than the stored thumbnail is served from it (the caller box-downsamples),
    /// which avoids inflating every layer of a large document for a small browse thumbnail.
    /// </summary>
    private static bool TryThumbnailForPreview(ReadOnlySpan<byte> data, int maxLongestSide, out DecodedImage image)
    {
        image = default;
        if (!TryReadHeader(data, out var header, out _) || !header.HasHeader) return false;
        if (!ManagedCodecSupport.IsValidDimensions(header.Width, header.Height)) return false;
        if (!TryGetThumbnail(data, out var png) || !ManagedCodecSupport.TryProbeEncoded(png, out var tw, out var th, out _)) return false;
        if (Math.Max(tw, th) < maxLongestSide || tw > header.Width || th > header.Height) return false;
        // Same aspect ratio (within a pixel of rounding), so the preview matches the full decode's shape.
        if (Math.Abs((long)tw * header.Height - (long)th * header.Width) > Math.Max(header.Width, header.Height)) return false;
        return ManagedCodecSupport.TryDecodeEncoded(png, 0, out image, out _);
    }

    /// <summary>The NRBF graph bytes (raw, or gunzipped) and where the deferred pixel data starts.</summary>
    private static bool TryOpenGraph(ReadOnlySpan<byte> data, in Header header, bool probe, out byte[]? inflated, out int graphStart, out bool deferredAllowed)
    {
        inflated = null;
        graphStart = 0;
        deferredAllowed = false;
        var body = header.BodyOffset;
        if (body + 2 > data.Length) return false;
        if (header.HasHeader && data[body] == 0x00 && data[body + 1] == 0x01)
        {
            graphStart = body + 2;
            deferredAllowed = true;
            return true;
        }
        if (data[body] != 0x1F || data[body + 1] != 0x8B) return false;
        // Gzip'd graph (2.x): the layer pixels are inline byte[] arrays inside it. Bound the output by the
        // header's declared size when there is one.
        long cap = MaxGzipGraphBytes;
        if (probe) cap = ProbeGzipBytes;
        else if (ManagedCodecSupport.IsValidDimensions(header.Width, header.Height) && header.LayerCount is > 0 and <= MaxLayers)
            cap = Math.Min(cap, (long)header.Width * header.Height * 4 * header.LayerCount + (16L << 20));
        if (!probe && !DecodeMemoryGuard.FitsAvailableMemory(cap, DecodeMemoryGuard.GetSnapshot())) return false;
        if (!TryGunzipPrefix(data[body..], cap, allowPartial: probe, out var bytes)) return false;
        inflated = bytes;
        return true;
    }

    /// <summary>Gunzips up to <paramref name="cap"/> bytes; with <paramref name="allowPartial"/> a truncated or over-long stream yields its prefix.</summary>
    private static unsafe bool TryGunzipPrefix(ReadOnlySpan<byte> source, long cap, bool allowPartial, out byte[] result)
    {
        result = Array.Empty<byte>();
        fixed (byte* pointer = source)
        {
            using var input = new UnmanagedMemoryStream(pointer, source.Length);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            try
            {
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (total + read > cap)
                    {
                        if (!allowPartial) return false;
                        output.Write(buffer, 0, (int)(cap - total));
                        total = cap;
                        break;
                    }
                    output.Write(buffer, 0, read);
                    total += read;
                }
            }
            catch
            {
                if (!allowPartial) return false;
            }
            if (total == 0) return false;
            result = output.ToArray();
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Document model (read from the graph as plain data)
    // ---------------------------------------------------------------------------------------------

    private sealed class LayerInfo
    {
        public int Width, Height;
        public bool Visible = true;
        public int Opacity = 255;
        public LayerBlendMode Mode;
        public int BlockId;           // MemoryBlock object id
        public long Length;           // bytes (== width * height * 4)
        public bool Deferred;
        public int InlineOffset = -1; // non-deferred: byte[] offset in the graph buffer
    }

    private static bool TryProbeDocument(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (!TryReadHeader(data, out var header, out _)) return false;
        if (!TryOpenGraph(data, header, probe: true, out var inflated, out var start, out _)) return false;
        var graphBytes = inflated is null ? data[start..] : inflated;
        var graph = NrbfGraph.Parse(graphBytes, allowTruncation: true);
        if (graph is null) return false;
        if (graph.Complete)
            return TryReadLayers(graph, out width, out height, out _);
        // The probe only saw the start of the file: the root Document record comes first.
        return TryReadDocumentSize(graph, out width, out height);
    }

    private static bool TryReadDocumentSize(NrbfGraph graph, out int width, out int height)
    {
        width = height = 0;
        if (graph.GetClass(graph.RootId) is not { } root || !root.Name.EndsWith("Document", StringComparison.Ordinal)) return false;
        if (!root.TryGetInt("width", out var w) || !root.TryGetInt("height", out var h)) return false;
        if (!ManagedCodecSupport.IsValidDimensions(w, h)) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    private static bool TryReadLayers(NrbfGraph graph, out int width, out int height, out List<LayerInfo> layers)
    {
        layers = new List<LayerInfo>();
        if (!TryReadDocumentSize(graph, out width, out height)) return false;
        var root = graph.GetClass(graph.RootId)!;

        // Document.layers is a LayerList: an ArrayList subclass ("ArrayList+_items" / "ArrayList+_size").
        if (!root.TryGetRef("layers", out var layersId)) return false;
        NrbfArray? items;
        long count;
        if (graph.GetArray(layersId) is { } direct)
        {
            items = direct;
            count = direct.Items?.Length ?? 0;
        }
        else if (graph.GetClass(layersId) is { } list && list.TryGetRefBySuffix("_items", out var itemsId) && graph.GetArray(itemsId) is { } array)
        {
            items = array;
            count = list.TryGetIntBySuffix("_size", out var size) ? size : array.Items?.Length ?? 0;
        }
        else return false;
        if (items.Items is null || count < 0 || count > items.Items.Length || count > MaxLayers) return false;

        for (var i = 0; i < count; i++)
        {
            var item = items.Items[i];
            if (item.Kind != NrbfKind.Ref || graph.GetClass(item.RefId) is not { } layer) return false;
            // Paint.NET layers are always the document's size; anything else is a damaged file.
            if (!TryReadLayer(graph, layer, out var info) || info.Width != width || info.Height != height) return false;
            layers.Add(info);
        }
        return layers.Count > 0;
    }

    private static bool TryReadLayer(NrbfGraph graph, NrbfClass layer, out LayerInfo info)
    {
        info = new LayerInfo();
        // Base-class members are serialized as "Layer+name"; BitmapLayer's own as plain names.
        var common = layer.TryGetRef("Layer+properties", out var propsId) ? graph.GetClass(propsId) : null;
        var hasMode = false;
        if (common is not null)
        {
            if (common.TryGetBool("visible", out var visible)) info.Visible = visible;
            if (common.TryGetInt("opacity", out var opacity)) info.Opacity = (int)Math.Clamp(opacity, 0, 255);
            if (TryReadBlendMode(graph, common, out var mode))
            {
                info.Mode = mode;
                hasMode = true;
            }
        }
        // 4.x keeps the mode in LayerProperties.blendMode; 3.x in BitmapLayerProperties.blendOp.
        if (!hasMode && layer.TryGetRef("properties", out var bitmapPropsId) && graph.GetClass(bitmapPropsId) is { } bitmapProps &&
            TryReadBlendMode(graph, bitmapProps, out var bitmapMode))
            info.Mode = bitmapMode;

        if (!layer.TryGetRef("surface", out var surfaceId) || graph.GetClass(surfaceId) is not { } surface) return false;
        if (!surface.TryGetInt("width", out var w) || !surface.TryGetInt("height", out var h) || !surface.TryGetInt("stride", out var stride)) return false;
        if (!ManagedCodecSupport.IsValidDimensions(w, h) || stride != w * 4) return false;
        info.Width = (int)w;
        info.Height = (int)h;
        if (!surface.TryGetRef("scan0", out var blockId) || graph.GetClass(blockId) is not { } block) return false;
        info.BlockId = blockId;
        if (!block.TryGetInt("length64", out var length) && !block.TryGetInt("length", out length)) return false;
        if (length != stride * h || length > Array.MaxLength) return false;
        info.Length = length;
        if (block.TryGetBool("hasParent", out var hasParent) && hasParent) return false;
        info.Deferred = block.TryGetBool("deferred", out var deferred) && deferred;
        if (!info.Deferred)
        {
            if (!block.TryGetRef("pointerData", out var dataId) || graph.GetArray(dataId) is not { } bytes ||
                bytes.PrimitiveType != NrbfGraph.PrimitiveByte || bytes.Length != length)
                return false;
            info.InlineOffset = bytes.DataOffset;
        }
        return true;
    }

    /// <summary>
    /// 4.x+: <c>blendMode</c> (a LayerBlendMode enum, serialized as a boxed <c>value__</c>);
    /// 3.x: <c>blendOp</c>, an instance of <c>PaintDotNet.UserBlendOps+XxxBlendOp</c>.
    /// </summary>
    private static bool TryReadBlendMode(NrbfGraph graph, NrbfClass properties, out LayerBlendMode mode)
    {
        mode = LayerBlendMode.Normal;
        if (properties.TryGetValue("blendMode", out var value))
        {
            long raw;
            if (value.Kind == NrbfKind.Int) raw = value.Bits;
            else if (value.Kind == NrbfKind.Ref && graph.GetClass(value.RefId) is { } boxed && boxed.TryGetInt("value__", out var inner)) raw = inner;
            else return false;
            mode = raw is >= 0 and <= (long)LayerBlendMode.Xor ? (LayerBlendMode)raw : LayerBlendMode.Normal;
            return true;
        }
        if (properties.TryGetRef("blendOp", out var opId) && graph.GetClass(opId) is { } op)
        {
            var name = op.Name;
            var plus = name.LastIndexOf('+');
            if (plus >= 0) name = name[(plus + 1)..];
            if (name.EndsWith("BlendOp", StringComparison.Ordinal)) name = name[..^"BlendOp".Length];
            mode = Enum.TryParse<LayerBlendMode>(name, ignoreCase: false, out var parsed) ? parsed : LayerBlendMode.Normal;
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Pixels
    // ---------------------------------------------------------------------------------------------

    private readonly record struct Chunk(int Offset, int Size, long Target, int TargetSize);

    private sealed class Block
    {
        public int FormatVersion;
        public List<Chunk> Chunks = new();
    }

    private static bool TryDecodeDocument(ReadOnlySpan<byte> data, out DecodedImage image)
    {
        image = default;
        if (!TryReadHeader(data, out var header, out _)) return false;
        if (!TryOpenGraph(data, header, probe: false, out var inflated, out var start, out var deferredAllowed)) return false;
        ReadOnlySpan<byte> source = inflated is null ? data : inflated;
        var graphBytes = inflated is null ? data[start..] : inflated;
        var graph = NrbfGraph.Parse(graphBytes, allowTruncation: false);
        if (graph is null || !graph.Complete) return false;
        if (!TryReadLayers(graph, out var width, out var height, out var layers)) return false;
        var baseOffset = inflated is null ? start : 0;

        // Canvas, one layer buffer and the bitmap copy the caller makes.
        if (!ManagedCodecSupport.IsValidDimensions(width, height) ||
            !DecodeMemoryGuard.FitsAvailableMemory((long)width * height * 4 * 3, DecodeMemoryGuard.GetSnapshot()))
            return false;

        // Deferred MemoryBlock data follows the graph, in the order the blocks were serialized.
        Dictionary<int, Block>? blocks = null;
        if (layers.Any(l => l.Deferred))
        {
            if (!deferredAllowed) return false;
            if (!TryReadDeferredBlocks(data, start + graph.EndPosition, graph, out blocks)) return false;
            // Refuse before allocating when the stored data cannot possibly inflate to the claimed size
            // (deflate never exceeds ~1032:1), so a tiny file cannot make us allocate a huge canvas.
            foreach (var layer in layers)
            {
                if (!layer.Deferred || !blocks.TryGetValue(layer.BlockId, out var block)) continue;
                long stored = 0;
                foreach (var chunk in block.Chunks) stored += chunk.Size;
                if (stored * (block.FormatVersion == 0 ? MaxDeflateRatio : 1) < layer.Length) return false;
            }
        }

        if (!ManagedCodecSupport.TryAllocatePixels(width, height, out var canvas)) return false;
        var canvasEmpty = true;
        byte[]? buffer = null;
        // Index 0 is the bottom (background) layer.
        foreach (var layer in layers)
        {
            if (!layer.Visible || layer.Opacity == 0) continue;
            byte[] pixels;
            var direct = canvasEmpty && layer.Mode == LayerBlendMode.Normal && layer.Opacity == 255 &&
                         layer.Width == width && layer.Height == height;
            if (direct) pixels = canvas;
            else
            {
                if (buffer is null || buffer.Length != layer.Length) buffer = new byte[layer.Length];
                pixels = buffer;
            }
            if (layer.Deferred)
            {
                if (blocks is null || !blocks.TryGetValue(layer.BlockId, out var block) || !TryInflateBlock(source, block, pixels, layer.Length)) return false;
            }
            else
            {
                var offset = baseOffset + layer.InlineOffset;
                if (layer.InlineOffset < 0 || offset + layer.Length > source.Length) return false;
                source.Slice(offset, (int)layer.Length).CopyTo(pixels);
            }
            if (!direct)
                LayerBlend.Blend(canvas, width, height, pixels, layer.Width, layer.Height, 0, 0, layer.Opacity / 255f, layer.Mode);
            canvasEmpty = false;
        }
        image = new DecodedImage(width, height, canvas);
        return true;
    }

    /// <summary>
    /// Each deferred block: format version (0 = gzip chunks, 1 = raw), big-endian chunk size, then
    /// ceil(length / chunkSize) chunks in any order, each a big-endian chunk number and data size.
    /// Only the headers are walked here; data is inflated on demand for visible layers.
    /// </summary>
    private static bool TryReadDeferredBlocks(ReadOnlySpan<byte> data, int position, NrbfGraph graph, out Dictionary<int, Block> blocks)
    {
        blocks = new Dictionary<int, Block>();
        foreach (var id in graph.DeferredBlockOrder)
        {
            if (graph.GetClass(id) is not { } memoryBlock) return false;
            if (!memoryBlock.TryGetInt("length64", out var length) && !memoryBlock.TryGetInt("length", out length)) return false;
            if (length <= 0 || length > Array.MaxLength) return false;
            if (position + 5 > data.Length) return false;
            var block = new Block { FormatVersion = data[position] };
            if (block.FormatVersion is not (0 or 1)) return false;
            var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 1)..]);
            position += 5;
            if (chunkSize == 0) return false;
            var chunkCount = (length + chunkSize - 1) / chunkSize;
            // Every chunk costs at least its 8-byte header plus one byte: refuse counts the file cannot hold.
            if (chunkCount * 9 > data.Length - position) return false;
            var seen = new bool[chunkCount];
            for (long i = 0; i < chunkCount; i++)
            {
                if (position + 8 > data.Length) return false;
                var number = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
                var size = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
                position += 8;
                if (number >= chunkCount || seen[number] || size == 0 || size > data.Length - position) return false;
                seen[number] = true;
                var target = (long)number * chunkSize;
                var targetSize = (int)Math.Min(chunkSize, length - target);
                if (block.FormatVersion == 1 && size < targetSize) return false;
                block.Chunks.Add(new Chunk(position, (int)size, target, targetSize));
                position += (int)size;
            }
            blocks[id] = block;
        }
        return true;
    }

    /// <summary>Inflates every chunk of a block into <paramref name="destination"/>; chunks are independent, so this runs in parallel.</summary>
    private static unsafe bool TryInflateBlock(ReadOnlySpan<byte> data, Block block, byte[] destination, long length)
    {
        if (destination.Length < length) return false;
        var chunks = block.Chunks;
        var raw = block.FormatVersion == 1;
        var failed = 0;
        fixed (byte* pointer = data)
        {
            var address = (nint)pointer;
            var dataLength = data.Length;
            var average = chunks.Count == 0 ? 0 : length / chunks.Count;
            ManagedCodecSupport.ForBands(chunks.Count, average, (first, last) =>
            {
                var file = new ReadOnlySpan<byte>((void*)address, dataLength);
                for (var i = first; i < last && Volatile.Read(ref failed) == 0; i++)
                {
                    var chunk = chunks[i];
                    var input = file.Slice(chunk.Offset, chunk.Size);
                    var output = destination.AsSpan((int)chunk.Target, chunk.TargetSize);
                    if (raw) input[..chunk.TargetSize].CopyTo(output);
                    else if (ManagedCodecSupport.Inflate(input, output, ManagedCodecSupport.Wrapper.Gzip) != chunk.TargetSize)
                        Interlocked.Exchange(ref failed, 1);
                }
            });
        }
        return failed == 0;
    }
}

// =================================================================================================
// MS-NRBF (.NET Remoting Binary Format) record walker
// =================================================================================================

internal enum NrbfKind : byte { Null, Bool, Int, Float, String, Ref, Other }

/// <summary>One member or array element: a primitive, a string, null, or a reference to another record.</summary>
internal readonly struct NrbfValue
{
    public readonly NrbfKind Kind;
    public readonly long Bits;
    public readonly string? Text;

    public NrbfValue(NrbfKind kind, long bits = 0, string? text = null)
    {
        Kind = kind;
        Bits = bits;
        Text = text;
    }

    public int RefId => (int)Bits;
    public static readonly NrbfValue Null = new(NrbfKind.Null);
    public static readonly NrbfValue Other = new(NrbfKind.Other);
}

internal sealed class NrbfClass
{
    public required string Name;
    public required string[] Members;
    public required NrbfValue[] Values;

    public bool TryGetValue(string name, out NrbfValue value)
    {
        for (var i = 0; i < Members.Length; i++)
        {
            if (Members[i] == name)
            {
                value = Values[i];
                return true;
            }
        }
        value = default;
        return false;
    }

    private bool TryGetValueBySuffix(string suffix, out NrbfValue value)
    {
        for (var i = 0; i < Members.Length; i++)
        {
            if (Members[i].EndsWith(suffix, StringComparison.Ordinal))
            {
                value = Values[i];
                return true;
            }
        }
        value = default;
        return false;
    }

    public bool TryGetInt(string name, out long value)
    {
        value = 0;
        if (!TryGetValue(name, out var v) || v.Kind != NrbfKind.Int) return false;
        value = v.Bits;
        return true;
    }

    public bool TryGetIntBySuffix(string suffix, out long value)
    {
        value = 0;
        if (!TryGetValueBySuffix(suffix, out var v) || v.Kind != NrbfKind.Int) return false;
        value = v.Bits;
        return true;
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        if (!TryGetValue(name, out var v) || v.Kind is not (NrbfKind.Bool or NrbfKind.Int)) return false;
        value = v.Bits != 0;
        return true;
    }

    public bool TryGetRef(string name, out int id)
    {
        id = 0;
        if (!TryGetValue(name, out var v) || v.Kind != NrbfKind.Ref) return false;
        id = v.RefId;
        return true;
    }

    public bool TryGetRefBySuffix(string suffix, out int id)
    {
        id = 0;
        if (!TryGetValueBySuffix(suffix, out var v) || v.Kind != NrbfKind.Ref) return false;
        id = v.RefId;
        return true;
    }
}

/// <summary>An array record: object/string elements are kept; primitive data is only located (offset into the stream).</summary>
internal sealed class NrbfArray
{
    public NrbfValue[]? Items;
    public int PrimitiveType;
    public int DataOffset = -1;
    public long Length;
}

/// <summary>
/// A read-only, bounded walk of an MS-NRBF record stream (as written by BinaryFormatter) into plain data.
/// No type is ever resolved or loaded and no object is constructed: class records become name/member/value
/// tables, references stay as ids (so cycles are harmless), and primitive arrays are only located. Every
/// count and length is checked against the bytes remaining and against fixed caps; nesting is depth-limited.
/// Malformed input yields <c>null</c>, never an exception.
/// </summary>
internal sealed class NrbfGraph
{
    public const int PrimitiveByte = 2;

    private const int MaxRecords = 1_000_000;
    private const int MaxObjects = 500_000;
    private const int MaxDepth = 48;
    private const int MaxMembers = 4096;
    private const int MaxNameBytes = 64 * 1024;
    private const int MaxKeptStringBytes = 4096;
    private const int MaxObjectArrayLength = 1 << 20;
    private const long MaxObjectElements = 1 << 22;
    private const int MaxRank = 32;

    private readonly Dictionary<int, object> _objects = new();
    private readonly Dictionary<int, ClassMeta> _metadata = new();
    private readonly List<int> _deferredBlocks = new();
    private int _records;
    private long _objectElements;

    public int RootId { get; private set; }
    /// <summary>True when the stream ended with a MessageEnd record (and every reference resolved).</summary>
    public bool Complete { get; private set; }
    /// <summary>Offset just past the MessageEnd record.</summary>
    public int EndPosition { get; private set; }
    /// <summary>Object ids of MemoryBlock-like records that announced deferred data, in stream order.</summary>
    public IReadOnlyList<int> DeferredBlockOrder => _deferredBlocks;

    public NrbfClass? GetClass(int id) => _objects.TryGetValue(id, out var o) ? o as NrbfClass : null;
    public NrbfArray? GetArray(int id) => _objects.TryGetValue(id, out var o) ? o as NrbfArray : null;

    private sealed class ClassMeta
    {
        public required string Name;
        public required string[] Members;
        public required byte[] BinaryTypes;
        public required byte[] PrimitiveTypes;
    }

    /// <summary>
    /// Parses the stream. With <paramref name="allowTruncation"/>, running out of data returns the partial graph
    /// (with <see cref="Complete"/> false) instead of failing; structural errors always fail.
    /// </summary>
    public static NrbfGraph? Parse(ReadOnlySpan<byte> data, bool allowTruncation)
    {
        var graph = new NrbfGraph();
        var reader = new Reader(data, graph);
        try
        {
            if (reader.ReadStream()) return graph;
            return allowTruncation && reader.Truncated && graph.RootId != 0 ? graph : null;
        }
        catch
        {
            return null;
        }
    }

    private bool Register(int id, object value)
    {
        if (id == 0 || _objects.Count >= MaxObjects) return false;
        return _objects.TryAdd(id, value);
    }

    private bool ReferencesResolve()
    {
        foreach (var value in _objects.Values)
        {
            var items = value switch
            {
                NrbfClass c => c.Values,
                NrbfArray a => a.Items,
                _ => null,
            };
            if (items is null) continue;
            foreach (var item in items)
                if (item.Kind == NrbfKind.Ref && !_objects.ContainsKey(item.RefId)) return false;
        }
        return _objects.ContainsKey(RootId);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly NrbfGraph _graph;
        private int _position;
        public bool Truncated;

        public Reader(ReadOnlySpan<byte> data, NrbfGraph graph)
        {
            _data = data;
            _graph = graph;
            _position = 0;
            Truncated = false;
        }

        private int Remaining => _data.Length - _position;

        private bool Need(long count)
        {
            if (count < 0) return false;
            if (count <= Remaining) return true;
            Truncated = true;
            return false;
        }

        private bool U8(out byte value)
        {
            value = 0;
            if (!Need(1)) return false;
            value = _data[_position++];
            return true;
        }

        private bool I32(out int value)
        {
            value = 0;
            if (!Need(4)) return false;
            value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
            _position += 4;
            return true;
        }

        private bool Count(out int value) => I32(out value) && value >= 0;

        /// <summary>LengthPrefixedString: 7-bit encoded length (max 5 bytes) + UTF-8.</summary>
        private bool String(int maxBytes, bool keep, out string? text)
        {
            text = null;
            var length = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (shift > 28 || !U8(out var b)) return false;
                if (shift == 28 && b > 0x07) return false;
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
            }
            if (length < 0 || length > maxBytes || !Need(length)) return false;
            if (keep) text = Encoding.UTF8.GetString(_data.Slice(_position, length));
            _position += length;
            return true;
        }

        private bool Name(out string text)
        {
            text = string.Empty;
            if (!String(MaxNameBytes, keep: true, out var s)) return false;
            text = s!;
            return true;
        }

        private bool CountRecord() => ++_graph._records <= MaxRecords;

        public bool ReadStream()
        {
            // SerializedStreamHeader: RootId, HeaderId, MajorVersion (1), MinorVersion (0).
            if (!U8(out var type) || type != 0) return false;
            if (!I32(out var root) || !I32(out _) || !I32(out var major) || !I32(out var minor)) return false;
            if (major != 1 || minor != 0 || root == 0) return false;
            _graph.RootId = root;
            while (true)
            {
                if (!Need(1)) return false;
                if (_data[_position] == 11) // MessageEnd
                {
                    _position++;
                    _graph.EndPosition = _position;
                    if (!_graph.ReferencesResolve()) return false;
                    _graph.Complete = true;
                    return true;
                }
                if (!ReadRecord(0, out var value, out var nulls) || nulls != 0) return false;
                // Top-level records are objects, arrays, strings or libraries; values are meaningless here.
                if (value.Kind is NrbfKind.Bool or NrbfKind.Int or NrbfKind.Float) return false;
            }
        }

        /// <summary>
        /// Reads one record in value position. <paramref name="nulls"/> is set for ObjectNullMultiple
        /// records (only legal inside arrays; the caller checks).
        /// </summary>
        private bool ReadRecord(int depth, out NrbfValue value, out int nulls)
        {
            value = NrbfValue.Null;
            nulls = 0;
            if (depth > MaxDepth) return false;
            while (true)
            {
                if (!CountRecord() || !U8(out var type)) return false;
                switch (type)
                {
                    case 12: // BinaryLibrary: prefix record, the value follows.
                        if (!I32(out _) || !String(MaxNameBytes, keep: false, out _)) return false;
                        continue;
                    case 6: // BinaryObjectString
                    {
                        if (!I32(out var id)) return false;
                        var start = _position;
                        if (!String(int.MaxValue, keep: false, out _)) return false;
                        var length = _position - start;
                        _position = start;
                        if (!String(int.MaxValue, keep: length <= MaxKeptStringBytes + 5, out var s)) return false;
                        s ??= string.Empty;
                        if (!_graph.Register(id, s)) return false;
                        value = new NrbfValue(NrbfKind.String, 0, s);
                        return true;
                    }
                    case 9: // MemberReference
                        if (!I32(out var refId) || refId == 0) return false;
                        value = new NrbfValue(NrbfKind.Ref, refId);
                        return true;
                    case 10: // ObjectNull
                        value = NrbfValue.Null;
                        return true;
                    case 13: // ObjectNullMultiple256
                        if (!U8(out var n8)) return false;
                        nulls = n8;
                        return nulls > 0;
                    case 14: // ObjectNullMultiple
                        if (!Count(out nulls)) return false;
                        return nulls > 0;
                    case 8: // MemberPrimitiveTyped
                        return U8(out var primitive) && Primitive(primitive, out value);
                    case 1: // ClassWithId
                    {
                        if (!I32(out var id) || !I32(out var metadataId)) return false;
                        if (!_graph._metadata.TryGetValue(metadataId, out var meta)) return false;
                        return ClassValues(id, meta, depth, out value);
                    }
                    case 2: // SystemClassWithMembers (untyped)
                    case 3: // ClassWithMembers (untyped)
                    case 4: // SystemClassWithMembersAndTypes
                    case 5: // ClassWithMembersAndTypes
                    {
                        if (!ClassInfo(out var id, out var name, out var members)) return false;
                        var binaryTypes = new byte[members.Length];
                        var primitiveTypes = new byte[members.Length];
                        if (type is 4 or 5)
                        {
                            if (!MemberTypes(binaryTypes, primitiveTypes)) return false;
                        }
                        else
                        {
                            Array.Fill(binaryTypes, (byte)2); // Object: every value is a record.
                        }
                        if (type is 3 or 5 && !I32(out _)) return false; // LibraryId
                        var meta = new ClassMeta { Name = name, Members = members, BinaryTypes = binaryTypes, PrimitiveTypes = primitiveTypes };
                        if (!_graph._metadata.TryAdd(id, meta)) return false;
                        return ClassValues(id, meta, depth, out value);
                    }
                    case 7: // BinaryArray
                        return BinaryArray(depth, out value);
                    case 15: // ArraySinglePrimitive
                    {
                        if (!I32(out var id) || !Count(out var length) || !U8(out var elementType)) return false;
                        return PrimitiveArray(id, length, elementType, out value);
                    }
                    case 16: // ArraySingleObject
                    case 17: // ArraySingleString
                    {
                        if (!I32(out var id) || !Count(out var length)) return false;
                        return ObjectArray(id, length, depth, out value);
                    }
                    default: // MessageEnd in value position, method call/return records, unknown types.
                        return false;
                }
            }
        }

        private bool ClassInfo(out int id, out string name, out string[] members)
        {
            members = Array.Empty<string>();
            name = string.Empty;
            if (!I32(out id) || !Name(out name) || !Count(out var count)) return false;
            if (count > MaxMembers || count > Remaining) return false;
            members = new string[count];
            for (var i = 0; i < count; i++)
                if (!Name(out members[i])) return false;
            return true;
        }

        private bool MemberTypes(byte[] binaryTypes, byte[] primitiveTypes)
        {
            if (!Need(binaryTypes.Length)) return false;
            for (var i = 0; i < binaryTypes.Length; i++)
            {
                binaryTypes[i] = _data[_position++];
                if (binaryTypes[i] > 7) return false;
            }
            for (var i = 0; i < binaryTypes.Length; i++)
                if (!AdditionalTypeInfo(binaryTypes[i], out primitiveTypes[i])) return false;
            return true;
        }

        private bool AdditionalTypeInfo(byte binaryType, out byte primitiveType)
        {
            primitiveType = 0;
            switch (binaryType)
            {
                case 0: // Primitive
                case 7: // PrimitiveArray
                    return U8(out primitiveType) && PrimitiveSize(primitiveType) != 0 && primitiveType != 17;
                case 3: // SystemClass: type name
                    return String(MaxNameBytes, keep: false, out _);
                case 4: // Class: type name + library id
                    return String(MaxNameBytes, keep: false, out _) && I32(out _);
                default: // String, Object, ObjectArray, StringArray
                    return true;
            }
        }

        private bool ClassValues(int id, ClassMeta meta, int depth, out NrbfValue value)
        {
            value = new NrbfValue(NrbfKind.Ref, id);
            var values = new NrbfValue[meta.Members.Length];
            var cls = new NrbfClass { Name = meta.Name, Members = meta.Members, Values = values };
            // Register before reading members so a nested record cannot reuse this id.
            if (!_graph.Register(id, cls)) return false;
            for (var i = 0; i < values.Length; i++)
            {
                if (meta.BinaryTypes[i] == 0)
                {
                    if (!Primitive(meta.PrimitiveTypes[i], out values[i])) return false;
                }
                else
                {
                    if (!ReadRecord(depth + 1, out values[i], out var nulls) || nulls != 0) return false;
                }
            }
            // MemoryBlock (any version): announces its pixel data after the graph when "deferred" is true.
            if (cls.TryGetBool("deferred", out var deferred) && deferred &&
                !(cls.TryGetBool("hasParent", out var hasParent) && hasParent))
                _graph._deferredBlocks.Add(id);
            return true;
        }

        private bool BinaryArray(int depth, out NrbfValue value)
        {
            value = NrbfValue.Null;
            if (!I32(out var id) || !U8(out var arrayType) || arrayType > 5 || !Count(out var rank)) return false;
            if (rank < 1 || rank > MaxRank) return false;
            long total = 1;
            for (var i = 0; i < rank; i++)
            {
                if (!Count(out var length)) return false;
                total *= length;
                if (total > int.MaxValue) return false;
            }
            if (arrayType is 3 or 4 or 5)
                for (var i = 0; i < rank; i++)
                    if (!I32(out _)) return false;
            if (!U8(out var elementType) || elementType > 7 || !AdditionalTypeInfo(elementType, out var primitive)) return false;
            return elementType == 0
                ? PrimitiveArray(id, (int)total, primitive, out value)
                : ObjectArray(id, (int)total, depth, out value);
        }

        private bool PrimitiveArray(int id, int length, byte primitive, out NrbfValue value)
        {
            value = new NrbfValue(NrbfKind.Ref, id);
            var size = PrimitiveSize(primitive);
            if (size == 0 || primitive == 17) return false;
            var array = new NrbfArray { PrimitiveType = primitive, Length = length, DataOffset = _position };
            if (size > 0)
            {
                if (!Need((long)length * size)) return false;
                _position += length * size;
            }
            else
            {
                // Variable-size elements (Char, Decimal, String): each is at least one byte.
                if (!Need(length)) return false;
                for (var i = 0; i < length; i++)
                    if (!Primitive(primitive, out _)) return false;
            }
            return _graph.Register(id, array);
        }

        private bool ObjectArray(int id, int length, int depth, out NrbfValue value)
        {
            value = new NrbfValue(NrbfKind.Ref, id);
            if (length > MaxObjectArrayLength) return false;
            _graph._objectElements += length;
            if (_graph._objectElements > MaxObjectElements) return false;
            var items = new NrbfValue[length];
            var array = new NrbfArray { Items = items, Length = length };
            if (!_graph.Register(id, array)) return false;
            for (var i = 0; i < length;)
            {
                if (!ReadRecord(depth + 1, out var item, out var nulls)) return false;
                if (nulls > 0)
                {
                    if (nulls > length - i) return false;
                    i += nulls; // already Null (default) entries
                    continue;
                }
                items[i++] = item;
            }
            return true;
        }

        /// <summary>Byte size of a primitive (-1 = variable length, 0 = invalid).</summary>
        private static int PrimitiveSize(byte type) => type switch
        {
            1 or 2 or 10 => 1,
            7 or 14 => 2,
            8 or 11 or 15 => 4,
            6 or 9 or 12 or 13 or 16 => 8,
            3 or 5 or 18 => -1,
            17 => -1,
            _ => 0,
        };

        private bool Primitive(byte type, out NrbfValue value)
        {
            value = NrbfValue.Other;
            switch (type)
            {
                case 1: // Boolean
                    if (!U8(out var b)) return false;
                    value = new NrbfValue(NrbfKind.Bool, b != 0 ? 1 : 0);
                    return true;
                case 2: // Byte
                    if (!U8(out var u8)) return false;
                    value = new NrbfValue(NrbfKind.Int, u8);
                    return true;
                case 10: // SByte
                    if (!U8(out var s8)) return false;
                    value = new NrbfValue(NrbfKind.Int, (sbyte)s8);
                    return true;
                case 7: // Int16
                    if (!Need(2)) return false;
                    value = new NrbfValue(NrbfKind.Int, BinaryPrimitives.ReadInt16LittleEndian(_data[_position..]));
                    _position += 2;
                    return true;
                case 14: // UInt16
                    if (!Need(2)) return false;
                    value = new NrbfValue(NrbfKind.Int, BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]));
                    _position += 2;
                    return true;
                case 8: // Int32
                    if (!I32(out var i32)) return false;
                    value = new NrbfValue(NrbfKind.Int, i32);
                    return true;
                case 15: // UInt32
                    if (!Need(4)) return false;
                    value = new NrbfValue(NrbfKind.Int, BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]));
                    _position += 4;
                    return true;
                case 9: // Int64
                case 16: // UInt64 (values above long.MaxValue are never a valid size here)
                    if (!Need(8)) return false;
                    value = new NrbfValue(NrbfKind.Int, BinaryPrimitives.ReadInt64LittleEndian(_data[_position..]));
                    _position += 8;
                    return true;
                case 12: // TimeSpan
                case 13: // DateTime
                    if (!Need(8)) return false;
                    _position += 8;
                    return true;
                case 11: // Single
                    if (!Need(4)) return false;
                    value = new NrbfValue(NrbfKind.Float, BitConverter.DoubleToInt64Bits(BinaryPrimitives.ReadSingleLittleEndian(_data[_position..])));
                    _position += 4;
                    return true;
                case 6: // Double
                    if (!Need(8)) return false;
                    value = new NrbfValue(NrbfKind.Float, BinaryPrimitives.ReadInt64LittleEndian(_data[_position..]));
                    _position += 8;
                    return true;
                case 3: // Char: one UTF-8 encoded character (1-4 bytes)
                {
                    if (!U8(out var lead)) return false;
                    var extra = lead < 0x80 ? 0 : lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : -1;
                    if (extra < 0 || !Need(extra)) return false;
                    _position += extra;
                    return true;
                }
                case 5: // Decimal (as a string)
                case 18: // String
                    return String(MaxNameBytes, keep: false, out _);
                case 17: // Null
                    value = NrbfValue.Null;
                    return true;
                default:
                    return false;
            }
        }
    }
}
