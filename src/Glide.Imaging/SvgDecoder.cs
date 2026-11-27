using System.Buffers;
using System.IO.Compression;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace Glide.Imaging;

/// <summary>
/// Vector SVG/SVGZ rasteriser. SVG is resolution independent, so "decoding" means rendering the
/// document at the resolution the caller needs: a bounded longest side for previews, or the
/// document's intrinsic size capped to a safe maximum for full frames. Uses Svg.Skia (the same
/// SkiaSharp stack Avalonia already ships).
/// </summary>
public static class SvgDecoder
{
    /// <summary>Cap for full-resolution vector renders so a huge viewBox cannot exhaust memory.</summary>
    public const int DefaultFullLongestSide = 4096;

    /// <summary>Largest encoded SVG/SVGZ document read into memory (keeps loading bounded and lock-free).</summary>
    private const long MaxDocumentBytes = 128L * 1024L * 1024L;

    public static bool IsSvgPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase));

    public static bool TryDecode(string path, int maxLongestSide, out Bitmap? bitmap)
    {
        bitmap = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            using var svg = new SKSvg();
            var picture = LoadPicture(svg, path);
            if (picture is null) return false;
            var cull = picture.CullRect;
            var intrinsicW = cull.Width > 0 ? cull.Width : 1000f;
            var intrinsicH = cull.Height > 0 ? cull.Height : 1000f;
            var longest = maxLongestSide > 0 ? Math.Min(maxLongestSide, 32768) : DefaultFullLongestSide;
            var scale = longest / Math.Max(intrinsicW, intrinsicH);
            // A bounded request is a preview and may upscale (vectors stay crisp). An unbounded
            // "full" request is the print/1:1 source, so only ever downscale to the safety cap.
            if (maxLongestSide <= 0) scale = Math.Min(1.0f, scale);
            var targetW = Math.Max(1, (int)Math.Round(intrinsicW * scale));
            var targetH = Math.Max(1, (int)Math.Round(intrinsicH * scale));
            if (targetW > 32768 || targetH > 32768) return false;
            bitmap = Render(picture, targetW, targetH, intrinsicW, intrinsicH);
            return bitmap is not null;
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    /// <summary>Runs the parse -> picture -> raster pipeline once on a tiny in-memory document (idle warm-up, <see cref="SvgTileRenderer.WarmUp"/>).</summary>
    internal static void WarmUpParser(byte[] document)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(document, writable: false);
        var picture = LoadWithoutExternalImages(svg, stream);
        if (picture is null) return;
        using var surface = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface?.Canvas.DrawPicture(picture);
        surface?.Canvas.Flush();
    }

    public static bool TryProbeDimensions(string path, out ImageDimensions dimensions)
    {
        dimensions = default;
        try
        {
            if (!IsSvgPath(path) || !File.Exists(path)) return false;
            using var svg = new SKSvg();
            var picture = LoadPicture(svg, path);
            if (picture is null) return false;
            var cull = picture.CullRect;
            if (cull.Width <= 0 || cull.Height <= 0) return false;
            dimensions = new ImageDimensions((int)Math.Ceiling(cull.Width), (int)Math.Ceiling(cull.Height));
            return dimensions.IsValid;
        }
        catch { return false; }
    }

    private static SKPicture? LoadPicture(SKSvg svg, string path)
    {
        // Read the encoded document with full delete sharing and hand Svg.Skia an in-memory stream.
        // Passing a path lets the third-party loader retain a file handle; that keeps the SVG
        // undeletable in Explorer ("in use in Glide") after the viewer has closed it. The read is
        // bounded so a hostile/huge document cannot pin unbounded managed memory.
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaxDocumentBytes) return null;

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            // Fix (SVGZ bomb): budgeted gzip inflation rejects output over MaxDocumentBytes (128MB)
            // instead of unbounded CopyTo. Keeps FileShare.ReadWrite|Delete and the 4096 copy cap.
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                long total = 0;
                int read;
                while ((read = gzip.Read(rented, 0, rented.Length)) > 0)
                {
                    total += read;
                    if (total > MaxDocumentBytes) return null;
                    decompressed.Write(rented, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            decompressed.Position = 0;
            return LoadWithoutExternalImages(svg, decompressed);
        }

        using var buffer = new MemoryStream(checked((int)info.Length));
        file.CopyTo(buffer);
        buffer.Position = 0;
        return LoadWithoutExternalImages(svg, buffer);
    }

    /// <summary>
    /// Security: Svg.Model fetches every non-data: <c>&lt;image&gt;</c>/<c>&lt;feImage&gt;</c> href with
    /// WebRequest, so merely viewing a hostile SVG made Glide contact remote hosts (http/https/ftp) or
    /// UNC shares (\\host\share, which also sends the user's NTLM credentials). Same parser as
    /// <c>SKSvg.Load(Stream)</c>; image references that are not embedded data: URIs, internal #fragments
    /// or local drive paths are dropped before the picture is built (the rest of the document renders).
    /// </summary>
    private static SKPicture? LoadWithoutExternalImages(SKSvg svg, Stream stream)
    {
        var document = Svg.Model.SvgExtensions.Open(stream, null);
        if (document is null) return null;
        RemoveExternalImageReferences(document);
        return svg.FromSvgDocument(document);
    }

    /// <summary>Removes image/feImage elements whose href could reach the network. Returns how many were removed.</summary>
    internal static int RemoveExternalImageReferences(Svg.SvgElement root) => RemoveExternalImageReferences(root, 0, remove: true);

    private static int RemoveExternalImageReferences(Svg.SvgElement root, int depth, bool remove)
    {
        var unsafeElements = new List<Svg.SvgElement>();
        var pending = new Stack<Svg.SvgElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            var href = element switch
            {
                Svg.SvgImage image => image.Href,
                Svg.FilterEffects.SvgImage filterImage => filterImage.Href,
                _ => null,
            };
            if (href is not null && !IsSafeImageReference(href, depth)) unsafeElements.Add(element);
            foreach (var child in element.Children) pending.Push(child);
        }
        if (remove)
            foreach (var element in unsafeElements) element.Parent?.Children.Remove(element);
        return unsafeElements.Count;
    }

    /// <summary>Nested SVG images (data:image/svg+xml) are checked this many levels deep; deeper nesting is refused.</summary>
    private const int MaxNestedSvgDepth = 3;

    /// <summary>
    /// True for references that cannot leave the machine: embedded data: URIs, same-document #fragments
    /// and fully qualified local drive paths (C:\x.png or file:///C:/x.png). Remote schemes, UNC/device
    /// paths, host-qualified file URIs and relative references (resolved against xml:base) are refused.
    /// An embedded SVG image (data:image/svg+xml) is itself rendered by Svg.Model, so it is safe only
    /// when its own image references are.
    /// </summary>
    internal static bool IsSafeImageReference(string? href) => IsSafeImageReference(href, 0);

    private static bool IsSafeImageReference(string? href, int depth)
    {
        if (string.IsNullOrWhiteSpace(href)) return true;
        var value = href.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return IsSafeDataUri(value, depth);
        if (value.StartsWith('#')) return true;
        if (IsLocalDrivePath(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc &&
               string.IsNullOrEmpty(uri.Host) && IsLocalDrivePath(uri.LocalPath);
    }

    private static bool IsSafeDataUri(string value, int depth)
    {
        var comma = value.IndexOf(',');
        if (comma < 0) return true; // nothing decodable, so nothing is loaded
        var header = value[5..comma];
        if (header.IndexOf("svg", StringComparison.OrdinalIgnoreCase) < 0) return true; // raster: decoded in memory only
        if (depth >= MaxNestedSvgDepth) return false;
        try
        {
            var payload = value[(comma + 1)..];
            var bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            using var nestedStream = new MemoryStream(bytes, writable: false);
            var nested = Svg.Model.SvgExtensions.Open(nestedStream, null);
            return nested is null || RemoveExternalImageReferences(nested, depth + 1, remove: false) == 0;
        }
        catch
        {
            return false; // undecodable here: refuse rather than let the renderer try
        }
    }

    private static bool IsLocalDrivePath(string value) =>
        value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/') &&
        value.IndexOf("\\\\", 2, StringComparison.Ordinal) < 0 && value.IndexOf("//", 2, StringComparison.Ordinal) < 0;

    private static unsafe Bitmap? Render(SKPicture picture, int width, int height, float intrinsicWidth, float intrinsicHeight)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        // The picture is recorded at the document's own size: scale it to the requested size (5.2.0: it was drawn 1:1 into the
        // requested frame, so a preview of a different size than the document showed a cropped or tiny picture).
        if (width != (int)Math.Round(intrinsicWidth) || height != (int)Math.Round(intrinsicHeight))
            canvas.Scale(width / Math.Max(1f, intrinsicWidth), height / Math.Max(1f, intrinsicHeight));
        canvas.DrawPicture(picture);
        canvas.Flush();
        using var image = surface.Snapshot();
        using var skBitmap = SKBitmap.FromImage(image);
        if (skBitmap is null) return null;
        var sourceInfo = skBitmap.Info;
        var source = skBitmap.GetPixels();
        if (source == IntPtr.Zero) return null;

        var target = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using (var fb = target.Lock())
            {
                var rowBytes = (long)width * 4;
                if (fb.RowBytes < rowBytes) return null;
                for (var y = 0; y < height; y++)
                {
                    var src = (byte*)source + (long)y * sourceInfo.RowBytes;
                    var dst = (byte*)fb.Address + (long)y * fb.RowBytes;
                    Buffer.MemoryCopy(src, dst, fb.RowBytes, rowBytes);
                }
            }
            return target;
        }
        catch
        {
            target.Dispose();
            return null;
        }
    }
}
