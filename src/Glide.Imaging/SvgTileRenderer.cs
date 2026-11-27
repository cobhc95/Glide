using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace Glide.Imaging;

/// <summary>
/// SVG at thumbnail size (5.2.0). Path-heavy vector files go to Windows' built-in Direct2D SVG engine through
/// <c>Glide.Native.dll</c> (about 8x faster than Svg.Skia for a 5000-element document: the cost of Svg.Skia is parsing and
/// recording the document, not drawing it); everything else, and any file Direct2D declines, goes to the Svg.Skia
/// rasteriser at tile size, which is also what the viewer uses, so the tile looks like the opened picture.
/// <para>
/// Direct2D draws a subset of SVG and differs from Svg.Skia in the details, so it is used only for documents made of plain
/// shapes, paths, gradients, groups and transforms (<see cref="IsDirect2DSafe"/>): no text, CSS, filters, masks, patterns,
/// markers, DOCTYPE / entities, and no <c>image</c> element of any kind (the SVG security rule against remote and UNC image
/// references stays with Svg.Skia, whose loader strips them). The root needs a size in pixels (width / height, or a viewBox).
/// </para>
/// </summary>
public static partial class SvgTileRenderer
{
    /// <summary>Largest document sent to Direct2D (larger or compressed documents take the Svg.Skia route).</summary>
    private const int MaxDirect2DBytes = 4 * 1024 * 1024;

    /// <summary>Switch for tests and diagnostics (environment <c>GLIDE_SVG_D2D=0</c> turns the Direct2D route off for the process).</summary>
    public static bool Direct2DEnabled { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("GLIDE_SVG_D2D"), "0", StringComparison.Ordinal);

    /// <summary>Which route produced the last bitmap on this thread ("direct2d", "svg-skia", or "" when none); for tests and the bench.</summary>
    [ThreadStatic] private static string? s_lastRoute;
    public static string LastRoute => s_lastRoute ?? "";

    private static int s_warmed;

    /// <summary>
    /// Initialises Svg.Skia (assembly load, JIT, parser tables, SkiaSharp surface) and the Direct2D route on the calling
    /// thread with a tiny document (5.2.0). The first SVG of a process used to cost about 100 ms more than the second, even for
    /// a trivial file; the app calls this from a low-priority thread once it is idle. Runs once per process; never throws.
    /// </summary>
    public static void WarmUp()
    {
        if (Interlocked.Exchange(ref s_warmed, 1) != 0) return;
        try
        {
            var tiny = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 16 16\"><defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#08f\"/></linearGradient></defs><rect width=\"16\" height=\"16\" fill=\"url(#g)\"/><circle cx=\"8\" cy=\"8\" r=\"5\" fill=\"#f80\" stroke=\"#000\" stroke-width=\"1\"/><path d=\"M2 2 L14 14\" stroke=\"#000\"/></svg>"u8.ToArray();
            SvgDecoder.WarmUpParser(tiny);
            if (Direct2DEnabled && OperatingSystem.IsWindows())
            {
                unsafe
                {
                    fixed (byte* data = tiny)
                    {
                        var status = GlideRenderSvgBuffer(data, (uint)tiny.Length, 16, 16, 16, out var decoded);
                        if (NativeImageDecoder.TryCreateBitmapFromNative(decoded, status, out var warmed)) warmed.Dispose();
                    }
                }
            }
        }
        catch { /* a warm-up that fails changes nothing: the first real SVG just pays for itself */ }
    }

    public static bool TryRender(string path, int maxLongestSide, out Bitmap? bitmap)
    {
        bitmap = null;
        s_lastRoute = "";
        if (maxLongestSide <= 0) return false;
        if (Direct2DEnabled && OperatingSystem.IsWindows() && TryRenderDirect2D(path, maxLongestSide, out bitmap) && bitmap is not null)
        {
            s_lastRoute = "direct2d";
            return true;
        }
        if (!SvgDecoder.TryDecode(path, maxLongestSide, out bitmap) || bitmap is null) return false;
        s_lastRoute = "svg-skia";
        return true;
    }

    private static unsafe bool TryRenderDirect2D(string path, int maxLongestSide, out Bitmap? bitmap)
    {
        bitmap = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxDirect2DBytes) return false;
            byte[] document;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
            {
                document = new byte[info.Length];
                file.ReadExactly(document);
            }
            if (document.Length >= 2 && document[0] == 0x1F && document[1] == 0x8B)
            {
                // SVGZ: inflated within the same budget; a document that inflates beyond it is left to Svg.Skia (which has its own).
                using var gzip = new GZipStream(new MemoryStream(document, writable: false), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    int read;
                    while ((read = gzip.Read(rented, 0, rented.Length)) > 0)
                    {
                        if (inflated.Length + read > MaxDirect2DBytes) return false;
                        inflated.Write(rented, 0, read);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(rented); }
                document = inflated.ToArray();
            }
            if (!TryGetDirect2DSize(document, out var width, out var height)) return false;
            fixed (byte* data = document)
            {
                var status = GlideRenderSvgBuffer(data, (uint)document.Length, width, height, (uint)Math.Min(maxLongestSide, 4096), out var decoded);
                if (!NativeImageDecoder.TryCreateBitmapFromNative(decoded, status, out var created)) return false;
                bitmap = created;
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                      or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// True (with the document's size in pixels) when Direct2D is expected to draw <paramref name="document"/> like Svg.Skia:
    /// <see cref="IsDirect2DSafe"/> and a root size from width / height or the viewBox.
    /// </summary>
    internal static bool TryGetDirect2DSize(ReadOnlySpan<byte> document, out float width, out float height)
    {
        width = height = 0;
        return IsDirect2DSafe(document) && TryReadRootSize(document, out width, out height);
    }

    private static readonly byte[][] Forbidden =
    {
        "<text"u8.ToArray(), "<style"u8.ToArray(), "<filter"u8.ToArray(), "<image"u8.ToArray(), "<feimage"u8.ToArray(),
        "<foreignobject"u8.ToArray(), "<mask"u8.ToArray(), "<pattern"u8.ToArray(), "<marker"u8.ToArray(), "<switch"u8.ToArray(),
        "<!doctype"u8.ToArray(), "<!entity"u8.ToArray(), "<script"u8.ToArray(), "<animate"u8.ToArray(), "<set "u8.ToArray(),
        "<font"u8.ToArray(), "<tref"u8.ToArray(), "<a "u8.ToArray(), "<view"u8.ToArray(), "<cursor"u8.ToArray(), "<symbol"u8.ToArray(),
        "@import"u8.ToArray(), "url(http"u8.ToArray(), "url(\\\\"u8.ToArray(), "url(file"u8.ToArray(),
    };

    /// <summary>
    /// The document only uses features Direct2D renders like Svg.Skia. Case-insensitive on the element names; an href that is
    /// not a same-document <c>#fragment</c> disqualifies the document (no file or network reference ever reaches Direct2D).
    /// </summary>
    internal static bool IsDirect2DSafe(ReadOnlySpan<byte> document)
    {
        if (document.Length == 0 || document.Length > MaxDirect2DBytes) return false;
        // UTF-16 documents are not recognised here (their bytes never match the needles): leave them to Svg.Skia.
        if (document.Length >= 2 && ((document[0] == 0xFF && document[1] == 0xFE) || (document[0] == 0xFE && document[1] == 0xFF))) return false;
        foreach (var needle in Forbidden)
            if (document.IndexOfIgnoreCaseAscii(needle) >= 0) return false;
        // href / xlink:href must point inside the document.
        var rest = document;
        while (true)
        {
            var at = rest.IndexOfIgnoreCaseAscii("href"u8);
            if (at < 0) break;
            var after = rest[(at + 4)..];
            var i = 0;
            while (i < after.Length && (after[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
            if (i < after.Length && after[i] == (byte)'=')
            {
                i++;
                while (i < after.Length && (after[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
                if (i + 1 < after.Length && (after[i] is (byte)'"' or (byte)'\'') && after[i + 1] != (byte)'#') return false;
            }
            rest = after;
        }
        return true;
    }

    /// <summary>The root's size in pixels: width / height (unitless or px), completed from the viewBox; false when neither gives one.</summary>
    internal static bool TryReadRootSize(ReadOnlySpan<byte> document, out float width, out float height)
    {
        width = height = 0;
        var start = document.IndexOfIgnoreCaseAscii("<svg"u8);
        if (start < 0) return false;
        var tag = document[start..];
        var end = tag.IndexOf((byte)'>');
        if (end < 0 || end > 8192) return false;
        tag = tag[..end];

        var hasWidth = TryReadLength(tag, "width"u8, out var w, out var widthOk);
        var hasHeight = TryReadLength(tag, "height"u8, out var h, out var heightOk);
        if ((hasWidth && !widthOk) || (hasHeight && !heightOk)) return false; // a unit or percentage this reader does not convert
        var hasBox = TryReadViewBox(tag, out var boxWidth, out var boxHeight);
        if (hasWidth && hasHeight) { width = w; height = h; }
        else if (hasBox && hasWidth) { width = w; height = w * boxHeight / boxWidth; }
        else if (hasBox && hasHeight) { height = h; width = h * boxWidth / boxHeight; }
        else if (hasBox) { width = boxWidth; height = boxHeight; }
        else return false;
        return width > 0 && height > 0 && float.IsFinite(width) && float.IsFinite(height) && width <= 1_000_000 && height <= 1_000_000;
    }

    private static bool TryReadAttribute(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        value = default;
        var from = 0;
        while (from < tag.Length)
        {
            var at = tag[from..].IndexOfIgnoreCaseAscii(name);
            if (at < 0) return false;
            at += from;
            from = at + name.Length;
            // A whole attribute name: whitespace before it, '=' (and a quote) after it.
            if (at == 0 || !(tag[at - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) continue;
            var i = from;
            while (i < tag.Length && tag[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
            if (i >= tag.Length || tag[i] != (byte)'=') continue;
            i++;
            while (i < tag.Length && tag[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
            if (i >= tag.Length || tag[i] is not ((byte)'"' or (byte)'\'')) continue;
            var quote = tag[i];
            var close = tag[(i + 1)..].IndexOf(quote);
            if (close < 0) return false;
            value = tag.Slice(i + 1, close);
            return true;
        }
        return false;
    }

    /// <summary>True when the attribute exists; <paramref name="ok"/> says it is a positive plain number or a px value.</summary>
    private static bool TryReadLength(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> name, out float value, out bool ok)
    {
        value = 0;
        ok = false;
        if (!TryReadAttribute(tag, name, out var raw)) return false;
        var text = System.Text.Encoding.ASCII.GetString(raw).Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2].TrimEnd();
        ok = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && float.IsFinite(value);
        return true;
    }

    private static bool TryReadViewBox(ReadOnlySpan<byte> tag, out float width, out float height)
    {
        width = height = 0;
        if (!TryReadAttribute(tag, "viewbox"u8, out var raw)) return false;
        var parts = System.Text.Encoding.ASCII.GetString(raw).Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 &&
               float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
               float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height) &&
               width > 0 && height > 0;
    }

    private static int IndexOfIgnoreCaseAscii(this ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerNeedle)
    {
        if (lowerNeedle.Length == 0 || haystack.Length < lowerNeedle.Length) return -1;
        var first = lowerNeedle[0];
        var upperFirst = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first - 32) : first;
        var last = haystack.Length - lowerNeedle.Length;
        var i = 0;
        while (i <= last)
        {
            var hit = upperFirst == first ? haystack[i..(last + 1)].IndexOf(first) : haystack[i..(last + 1)].IndexOfAny(first, upperFirst);
            if (hit < 0) return -1;
            i += hit;
            var match = true;
            for (var k = 1; k < lowerNeedle.Length; k++)
            {
                var c = haystack[i + k];
                if (c is >= (byte)'A' and <= (byte)'Z') c = (byte)(c + 32);
                if (c != lowerNeedle[k]) { match = false; break; }
            }
            if (match) return i;
            i++;
        }
        return -1;
    }

    [LibraryImport("Glide.Native", EntryPoint = "GlideRenderSvgBuffer")]
    private static unsafe partial int GlideRenderSvgBuffer(byte* data, uint size, float intrinsicWidth, float intrinsicHeight,
        uint maxLongestSide, out NativeImageDecoder.NativeDecodedImage image);
}
