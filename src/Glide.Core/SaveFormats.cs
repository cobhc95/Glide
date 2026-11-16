using System.Globalization;

namespace Glide.Core;

/// <summary>Which encoder writes a <see cref="SaveFormat"/>.</summary>
public enum SaveFormatBackend
{
    /// <summary>Windows Imaging Component through Glide.Native (<see cref="SaveFormat.NativeId"/> is the bridge's format id).</summary>
    Wic,
    /// <summary>The bundled codec provider (codecs\glidecodecs.dll, ABI v2 encode extension).</summary>
    CodecProvider,
    /// <summary>A small pure-C# writer in Glide.Imaging/Encoders.</summary>
    Managed,
}

/// <summary>
/// One writable image format. Pure data: availability on this PC (Windows codec extensions, the codec provider)
/// is decided by the caller, so the Save as list and its tests stay UI- and platform-free.
/// </summary>
public sealed record SaveFormat(
    string Id,
    string Name,
    IReadOnlyList<string> Extensions,
    SaveFormatBackend Backend,
    int Tier,
    bool Alpha,
    bool Lossy,
    int MaxWidth = SaveFormat.Unlimited,
    int MaxHeight = SaveFormat.Unlimited,
    bool InPlaceSafe = true,
    bool PreservesMetadata = false,
    int NativeId = -1,
    string? CodecName = null,
    int ScaleToFit = 0,
    string? SizeAdvice = null,
    long MaxPixels = long.MaxValue)
{
    public const int Unlimited = int.MaxValue;

    public string PrimaryExtension => Extensions[0];

    /// <summary>Tier 2 formats exist only when a Windows Store codec extension registers an encoder.</summary>
    public bool NeedsWindowsExtension => Tier == 2;

    public bool HasExtension(string extension)
        => Extensions.Any(e => string.Equals(e, NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase));

    /// <summary>"JPEG (*.jpg; *.jpeg)" — the Save as file-type label.</summary>
    public string DialogLabel => Name + " (" + string.Join("; ", Extensions.Select(e => "*" + e)) + ")";

    public IReadOnlyList<string> Patterns => Extensions.Select(e => "*" + e).ToArray();

    /// <summary>Null when <paramref name="width"/> x <paramref name="height"/> fits this format; otherwise the message shown to the user.</summary>
    public string? ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return "There are no image pixels to save.";
        if (ScaleToFit > 0) return null; // the encoder scales down to fit (icons)
        if ((long)width * height > MaxPixels)
            return string.Format(CultureInfo.InvariantCulture, "{0} images can hold at most {1:N0} pixels; this image is {2:N0} × {3:N0}.",
                Name, MaxPixels, width, height);
        if (width <= MaxWidth && height <= MaxHeight) return null;
        var limit = MaxWidth == MaxHeight
            ? string.Format(CultureInfo.InvariantCulture, "{0:N0} × {0:N0}", MaxWidth)
            : string.Format(CultureInfo.InvariantCulture, "{0:N0} × {1:N0}", MaxWidth, MaxHeight);
        return string.Format(CultureInfo.InvariantCulture, "{0} images can be at most {1} pixels; this image is {2:N0} × {3:N0}.{4}",
            Name, limit, width, height, SizeAdvice is null ? "" : " " + SizeAdvice);
    }

    internal static string NormalizeExtension(string extension)
        => string.IsNullOrEmpty(extension) ? "" : extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
}

/// <summary>
/// Every format Save / Save as / Save selection can write, in Save as order (common formats first, then
/// HDR/professional, editor documents, icons/textures and legacy formats).
/// </summary>
public static class SaveFormatCatalog
{
    // WIC format ids shared with GlideEncodeImage2W (native/Glide.Native/GlideNative.cpp).
    public const int WicPng = 0, WicJpeg = 1, WicBmp = 2, WicTiff = 3, WicGif = 4, WicJxr = 5, WicHeif = 6, WicAvif = 7, WicJxl = 8;

    public static SaveFormat Jpeg { get; } = new("jpeg", "JPEG", [".jpg", ".jpeg", ".jpe", ".jfif"], SaveFormatBackend.Wic, 1, Alpha: false, Lossy: true,
        65535, 65535, PreservesMetadata: true, NativeId: WicJpeg);
    public static SaveFormat Png { get; } = new("png", "PNG", [".png"], SaveFormatBackend.Wic, 1, Alpha: true, Lossy: false,
        PreservesMetadata: true, NativeId: WicPng);
    public static SaveFormat WebP { get; } = new("webp", "WebP", [".webp"], SaveFormatBackend.CodecProvider, 7, Alpha: true, Lossy: true,
        16383, 16383, PreservesMetadata: true, CodecName: "webp");
    public static SaveFormat Heic { get; } = new("heic", "HEIC", [".heic", ".heif", ".hif"], SaveFormatBackend.Wic, 2, Alpha: false, Lossy: true,
        PreservesMetadata: true, NativeId: WicHeif);
    public static SaveFormat Avif { get; } = new("avif", "AVIF", [".avif"], SaveFormatBackend.Wic, 2, Alpha: false, Lossy: true,
        PreservesMetadata: true, NativeId: WicAvif);
    public static SaveFormat Gif { get; } = new("gif", "GIF", [".gif"], SaveFormatBackend.Wic, 1, Alpha: true, Lossy: false,
        65535, 65535, NativeId: WicGif);
    public static SaveFormat Bmp { get; } = new("bmp", "BMP", [".bmp", ".dib"], SaveFormatBackend.Wic, 1, Alpha: false, Lossy: false,
        NativeId: WicBmp);
    public static SaveFormat Tiff { get; } = new("tiff", "TIFF", [".tif", ".tiff"], SaveFormatBackend.Wic, 1, Alpha: true, Lossy: false,
        PreservesMetadata: true, NativeId: WicTiff);
    public static SaveFormat JpegXl { get; } = new("jxl", "JPEG XL", [".jxl"], SaveFormatBackend.Wic, 2, Alpha: false, Lossy: true,
        PreservesMetadata: true, NativeId: WicJxl);
    public static SaveFormat JpegXr { get; } = new("jxr", "JPEG XR", [".jxr", ".wdp", ".hdp"], SaveFormatBackend.Wic, 1, Alpha: true, Lossy: true,
        65535, 65535, PreservesMetadata: true, NativeId: WicJxr);
    public static SaveFormat Jp2 { get; } = new("jp2", "JPEG 2000", [".jp2"], SaveFormatBackend.CodecProvider, 6, Alpha: true, Lossy: false,
        CodecName: "jp2");
    public static SaveFormat J2k { get; } = new("j2k", "JPEG 2000 codestream", [".j2k", ".j2c", ".jpc"], SaveFormatBackend.CodecProvider, 6, Alpha: true, Lossy: false,
        CodecName: "j2k");
    public static SaveFormat Exr { get; } = new("exr", "OpenEXR", [".exr"], SaveFormatBackend.Managed, 5, Alpha: true, Lossy: false, InPlaceSafe: false);
    public static SaveFormat Hdr { get; } = new("hdr", "Radiance HDR", [".hdr"], SaveFormatBackend.Managed, 5, Alpha: false, Lossy: false,
        65535, 65535, InPlaceSafe: false);
    public static SaveFormat Pfm { get; } = new("pfm", "PFM (portable float map)", [".pfm"], SaveFormatBackend.Managed, 4, Alpha: false, Lossy: false, InPlaceSafe: false);
    public static SaveFormat Psd { get; } = new("psd", "Photoshop", [".psd"], SaveFormatBackend.Managed, 5, Alpha: true, Lossy: false,
        30000, 30000, InPlaceSafe: false, SizeAdvice: "Save as Photoshop large document (PSB) instead.");
    public static SaveFormat Psb { get; } = new("psb", "Photoshop large document", [".psb"], SaveFormatBackend.Managed, 5, Alpha: true, Lossy: false,
        300000, 300000, InPlaceSafe: false);
    public static SaveFormat OpenRaster { get; } = new("ora", "OpenRaster", [".ora"], SaveFormatBackend.Managed, 5, Alpha: true, Lossy: false, InPlaceSafe: false);
    public static SaveFormat Ico { get; } = new("ico", "Icon", [".ico"], SaveFormatBackend.Managed, 4, Alpha: true, Lossy: false,
        256, 256, InPlaceSafe: false, ScaleToFit: 256);
    public static SaveFormat Dds { get; } = new("dds", "DirectDraw Surface", [".dds"], SaveFormatBackend.Managed, 5, Alpha: true, Lossy: false,
        16384, 16384, InPlaceSafe: false, SizeAdvice: "That is the Direct3D texture limit.");
    public static SaveFormat Ktx { get; } = new("ktx", "KTX texture", [".ktx"], SaveFormatBackend.Managed, 5, Alpha: true, Lossy: false,
        16384, 16384, InPlaceSafe: false, SizeAdvice: "That is the common GPU texture limit.");
    public static SaveFormat Tga { get; } = new("tga", "TGA", [".tga"], SaveFormatBackend.Managed, 4, Alpha: true, Lossy: false, 65535, 65535);
    public static SaveFormat Qoi { get; } = new("qoi", "QOI", [".qoi"], SaveFormatBackend.Managed, 3, Alpha: true, Lossy: false,
        MaxPixels: 400_000_000);
    public static SaveFormat Ppm { get; } = new("ppm", "PPM (colour)", [".ppm", ".pnm"], SaveFormatBackend.Managed, 4, Alpha: false, Lossy: false);
    public static SaveFormat Pgm { get; } = new("pgm", "PGM (greyscale)", [".pgm"], SaveFormatBackend.Managed, 4, Alpha: false, Lossy: false);
    public static SaveFormat Pbm { get; } = new("pbm", "PBM (black and white)", [".pbm"], SaveFormatBackend.Managed, 4, Alpha: false, Lossy: false);
    public static SaveFormat Pam { get; } = new("pam", "PAM (with transparency)", [".pam"], SaveFormatBackend.Managed, 4, Alpha: true, Lossy: false);
    public static SaveFormat Sgi { get; } = new("sgi", "SGI", [".sgi", ".rgb", ".rgba"], SaveFormatBackend.Managed, 4, Alpha: true, Lossy: false, 65535, 65535);
    public static SaveFormat Xpm { get; } = new("xpm", "XPM", [".xpm"], SaveFormatBackend.Managed, 4, Alpha: true, Lossy: false);
    public static SaveFormat Xbm { get; } = new("xbm", "XBM (black and white)", [".xbm"], SaveFormatBackend.Managed, 4, Alpha: false, Lossy: false);
    public static SaveFormat Wbmp { get; } = new("wbmp", "WBMP (black and white)", [".wbmp"], SaveFormatBackend.Managed, 4, Alpha: false, Lossy: false,
        65535, 65535);

    /// <summary>Save as order: common, HDR/professional, editor documents, icons/textures, legacy.</summary>
    public static IReadOnlyList<SaveFormat> All { get; } =
    [
        Jpeg, Png, WebP, Heic, Avif, Gif, Bmp, Tiff,
        JpegXl, JpegXr, Jp2, J2k, Exr, Hdr, Pfm,
        Psd, Psb, OpenRaster,
        Ico, Dds, Ktx, Tga,
        Qoi, Ppm, Pgm, Pbm, Pam, Sgi, Xpm, Xbm, Wbmp,
    ];

    public static SaveFormat? FromId(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public static SaveFormat? FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        return All.FirstOrDefault(f => f.HasExtension(extension));
    }

    public static SaveFormat? FromPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : FromExtension(Path.GetExtension(path));
}

/// <summary>
/// Save as dialog decisions, kept UI-free: the type list, the preselected type, the suggested name, and how the
/// picked name and type become the destination (the name's extension always matches the format written).
/// </summary>
public static class SaveAsDialogPolicy
{
    /// <summary>The writable formats in Save as order (<paramref name="isWritable"/> gates tier 2 and the codec provider).</summary>
    public static IReadOnlyList<SaveFormat> Choices(Func<SaveFormat, bool> isWritable)
        => SaveFormatCatalog.All.Where(isWritable).ToArray();

    /// <summary>The source's own format when it is writable, otherwise PNG (lossless), otherwise the first choice.</summary>
    public static SaveFormat DefaultFormat(string? sourcePath, IReadOnlyList<SaveFormat> choices)
    {
        if (choices.Count == 0) throw new InvalidOperationException("No writable image formats.");
        var source = SaveFormatCatalog.FromPath(sourcePath);
        if (source is not null && choices.Contains(source)) return source;
        return choices.Contains(SaveFormatCatalog.Png) ? SaveFormatCatalog.Png : choices[0];
    }

    /// <summary>The extension a new file of <paramref name="format"/> gets: the source's own spelling when it belongs to the format (.jpeg stays .jpeg).</summary>
    public static string ExtensionFor(SaveFormat format, string? sourcePath)
    {
        var sourceExtension = string.IsNullOrWhiteSpace(sourcePath) ? "" : Path.GetExtension(sourcePath);
        return format.HasExtension(sourceExtension) ? sourceExtension.ToLowerInvariant() : format.PrimaryExtension;
    }

    /// <summary>"stem.ext" with the extension of the preselected type, so the name and the type list always agree.</summary>
    public static string SuggestedFileName(string? sourcePath, SaveFormat format, string? stem = null)
    {
        stem ??= string.IsNullOrWhiteSpace(sourcePath) ? "image" : Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(stem)) stem = "image";
        return stem + ExtensionFor(format, sourcePath);
    }

    /// <summary>
    /// Final destination and format for a picked path. A typed extension of a writable format wins (typing
    /// "photo.png" while JPEG is selected writes a PNG); otherwise the selected type's extension is appended, so
    /// the written format and the file name never disagree.
    /// </summary>
    public static (string Path, SaveFormat Format) ResolveDestination(string pickedPath, SaveFormat? selected, IReadOnlyList<SaveFormat> choices, string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pickedPath);
        if (choices.Count == 0) throw new InvalidOperationException("No writable image formats.");
        selected = selected is not null && choices.Contains(selected) ? selected : null;
        var extension = Path.GetExtension(pickedPath);
        if (!string.IsNullOrEmpty(extension))
        {
            // The selected type wins when it owns the typed extension; otherwise the first writable owner.
            if (selected is not null && selected.HasExtension(extension)) return (pickedPath, selected);
            var typed = choices.FirstOrDefault(f => f.HasExtension(extension));
            if (typed is not null) return (pickedPath, typed);
        }
        var format = selected ?? DefaultFormat(sourcePath, choices);
        var trimmed = pickedPath.TrimEnd('.');
        return (trimmed + format.PrimaryExtension, format);
    }

    /// <summary>The format whose dialog label is <paramref name="label"/> (the picker reports the selected type by name).</summary>
    public static SaveFormat? FromDialogLabel(string? label, IReadOnlyList<SaveFormat> choices)
        => string.IsNullOrEmpty(label) ? null : choices.FirstOrDefault(f => string.Equals(f.DialogLabel, label, StringComparison.Ordinal));
}

/// <summary>A save the encoder refused before writing anything (size limits, palette limits); the message is shown to the user.</summary>
public sealed class ImageSaveRefusedException(string message) : InvalidOperationException(message);
