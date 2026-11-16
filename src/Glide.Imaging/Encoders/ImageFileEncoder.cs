using System.Runtime.InteropServices;
using Glide.Core;

namespace Glide.Imaging.Encoders;

/// <summary>What to carry over from the original file.</summary>
public enum SaveMetadataScope
{
    None,
    /// <summary>Only the ICC colour profile (Save selection: a crop is a new picture, but its colours must not shift).</summary>
    IccOnly,
    /// <summary>ICC profile, EXIF and XMP (Save / Save as). Orientation is reset to 1 because rotation is baked in.</summary>
    All,
}

public sealed record ImageEncodeOptions
{
    /// <summary>The quality every lossy format uses (JPEG, WebP, HEIC, AVIF, JPEG XL, JPEG XR): Glide's long-standing JPEG save quality.</summary>
    public const float DefaultQuality = 0.92f;

    public float Quality { get; init; } = DefaultQuality;
    /// <summary>The original file metadata is copied from (null: none).</summary>
    public string? MetadataSource { get; init; }
    public SaveMetadataScope Metadata { get; init; } = SaveMetadataScope.All;
    /// <summary>Lossless mode where the format has one (WebP, JPEG XR). JPEG 2000 is always lossless.</summary>
    public bool Lossless { get; init; }
    /// <summary>0xAARRGGBB background transparency is composited over for formats without alpha.</summary>
    public uint Background { get; init; } = EncoderPixels.DefaultBackground;
    /// <summary>Base name some text formats embed (XBM/XPM C identifiers, SGI title).</summary>
    public string? Name { get; init; }
}

/// <summary>
/// The one encode path behind Save, Save as and Save selection: validates the size, flattens transparency for
/// formats without alpha, dispatches to WIC / the bundled codec provider / a managed writer, and carries metadata
/// over. Writes only to <c>path</c> (callers write a temp file and replace atomically).
/// </summary>
public static class ImageFileEncoder
{
    /// <param name="premultipliedBgra">Tightly packed premultiplied BGRA (Avalonia's bitmap layout). Not modified.</param>
    public static void Encode(SaveFormat format, byte[] premultipliedBgra, int width, int height, string path, ImageEncodeOptions? options = null)
    {
        options ??= new ImageEncodeOptions();
        if (width <= 0 || height <= 0 || premultipliedBgra.Length < (long)width * height * 4)
            throw new ArgumentException("No image pixels to save.", nameof(premultipliedBgra));
        if (format.ValidateSize(width, height) is { } refusal) throw new ImageSaveRefusedException(refusal);

        var pixels = premultipliedBgra;
        if (!format.Alpha && EncoderPixels.HasTransparency(pixels))
        {
            pixels = (byte[])premultipliedBgra.Clone();
            EncoderPixels.CompositeOverInPlace(pixels, options.Background);
        }
        var quality = Math.Clamp(options.Quality, 0.05f, 1f);
        var source = options.Metadata == SaveMetadataScope.None ? null : options.MetadataSource;

        switch (format.Backend)
        {
            case SaveFormatBackend.Wic:
                EncodeWic(format, pixels, width, height, path, quality, options, source);
                break;
            case SaveFormatBackend.CodecProvider:
            {
                var image = new StraightImage(width, height, EncoderPixels.Unpremultiply(pixels));
                var lossless = format.Id != SaveFormatCatalog.WebP.Id || options.Lossless;
                if (!CodecProviderEncoder.TryEncode(format.CodecName!, image, path, lossless ? 1f : quality))
                    throw new IOException($"Encoding {format.Name} failed (the bundled codec provider is missing or rejected the image).");
                if (format.Id == SaveFormatCatalog.WebP.Id && source is not null)
                {
                    var metadata = MetadataBlobs.Read(source).ForSavedImage(width, height, options.Metadata == SaveMetadataScope.IccOnly);
                    if (!metadata.IsEmpty) File.WriteAllBytes(path, WebPContainer.AddMetadata(File.ReadAllBytes(path), metadata, width, height));
                }
                break;
            }
            default:
            {
                var image = new StraightImage(width, height, EncoderPixels.Unpremultiply(pixels));
                File.WriteAllBytes(path, EncodeManaged(format, image, options));
                break;
            }
        }
    }

    private static void EncodeWic(SaveFormat format, byte[] pixels, int width, int height, string path, float quality, ImageEncodeOptions options, string? source)
    {
        var flags = NativeImageEncoder.EncodeFlags.None;
        if (source is not null)
        {
            flags |= NativeImageEncoder.EncodeFlags.CopyIcc;
            // PNG EXIF/XMP is added below by the managed chunk writer (WIC's PNG metadata support varies by Windows build).
            if (options.Metadata == SaveMetadataScope.All && format.Id != SaveFormatCatalog.Png.Id) flags |= NativeImageEncoder.EncodeFlags.CopyExifXmp;
        }
        if (options.Lossless) flags |= NativeImageEncoder.EncodeFlags.Lossless;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        bool ok;
        try
        {
            ok = NativeImageEncoder.TryEncodeFile(path, (uint)width, (uint)height, (uint)(width * 4), handle.AddrOfPinnedObject(),
                format.NativeId, quality, flags, source);
        }
        finally { handle.Free(); }
        if (!ok || !File.Exists(path) || new FileInfo(path).Length == 0)
            throw new IOException($"Encoding {format.Name} failed (Windows WIC bridge unavailable or rejected the image).");
        if (format.Id == SaveFormatCatalog.Png.Id && source is not null && options.Metadata == SaveMetadataScope.All)
        {
            var metadata = MetadataBlobs.Read(source).ForSavedImage(width, height);
            if (metadata.Exif is not null || metadata.Xmp is not null)
                File.WriteAllBytes(path, PngMetadataWriter.Insert(File.ReadAllBytes(path), metadata with { Icc = null }));
        }
    }

    /// <summary>The pure-C# writers (tier 3-5). Input is straight alpha, already flattened when the format has no alpha.</summary>
    public static byte[] EncodeManaged(SaveFormat format, StraightImage image, ImageEncodeOptions? options = null)
    {
        options ??= new ImageEncodeOptions();
        var name = string.IsNullOrWhiteSpace(options.Name) ? "image" : options.Name!;
        return format.Id switch
        {
            "qoi" => SimpleRasterEncoders.Qoi(image),
            "ppm" or "pgm" or "pbm" or "pam" => SimpleRasterEncoders.Pnm(image, format),
            "pfm" => SimpleRasterEncoders.Pfm(image),
            "tga" => SimpleRasterEncoders.Tga(image),
            "wbmp" => SimpleRasterEncoders.Wbmp(image),
            "xbm" => SimpleRasterEncoders.Xbm(image, name),
            "hdr" => SimpleRasterEncoders.Hdr(image),
            "sgi" => SimpleRasterEncoders.Sgi(image, name),
            "xpm" => SimpleRasterEncoders.Xpm(image, name, options.Background),
            "dds" => SimpleRasterEncoders.Dds(image),
            "ktx" => SimpleRasterEncoders.Ktx(image),
            "exr" => DocumentEncoders.Exr(image),
            "psd" => DocumentEncoders.Psd(image, large: false),
            "psb" => DocumentEncoders.Psd(image, large: true),
            "ico" => DocumentEncoders.Ico(image),
            "ora" => DocumentEncoders.OpenRaster(image),
            _ => throw new NotSupportedException($"Glide has no writer for {format.Name}."),
        };
    }
}

/// <summary>
/// Which <see cref="SaveFormatCatalog"/> formats this PC can write. Managed writers always; WIC formats when the
/// native bridge has an encoder (HEIC/AVIF/JPEG XL only with the matching Windows codec extension); JPEG 2000 and
/// WebP when the bundled codec provider exports the encode extension. Every probe is lazy and cached: nothing
/// runs at startup, only when a Save as list is first built or a save needs it.
/// </summary>
public static class SaveFormatAvailability
{
    public static bool IsWritable(SaveFormat format) => format.Backend switch
    {
        SaveFormatBackend.Managed => format.Id != SaveFormatCatalog.Ico.Id && format.Id != SaveFormatCatalog.OpenRaster.Id
            || NativeImageEncoder.IsFormatAvailable(SaveFormatCatalog.WicPng), // ICO/ORA embed WIC-encoded PNGs
        SaveFormatBackend.Wic => NativeImageEncoder.IsFormatAvailable(format.NativeId),
        SaveFormatBackend.CodecProvider => format.CodecName is { } codec && CodecProviderEncoder.IsAvailable(codec),
        _ => false,
    };

    /// <summary>The Save as list for this PC (may load the codec provider; call off the UI thread the first time).</summary>
    public static IReadOnlyList<SaveFormat> Writable() => SaveAsDialogPolicy.Choices(IsWritable);

    /// <summary>The format plain Save rewrites <paramref name="path"/> in, or null when Save must fall through to Save as.</summary>
    public static SaveFormat? InPlaceFormat(string path)
    {
        var format = SaveFormatCatalog.FromPath(path);
        return format is { InPlaceSafe: true } && IsWritable(format) ? format : null;
    }

    public static bool CanSaveInPlace(string extension)
    {
        var format = SaveFormatCatalog.FromExtension(extension);
        return format is { InPlaceSafe: true } && IsWritable(format);
    }
}
