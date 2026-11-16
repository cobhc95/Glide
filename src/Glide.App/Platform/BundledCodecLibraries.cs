using System.Text.Json;

namespace Glide.App.Platform;

/// <summary>
/// The codec libraries Glide ships in <c>&lt;app&gt;\codecs</c> that the Explorer thumbnail provider can load
/// (native/Glide.ShellThumbnail/CodecProviders.cpp): <c>glidecodecs.dll</c> (AVIF/HEIF/JPEG XL/JPEG 2000) and
/// <c>glidemanaged.dll</c> (camera RAW, EXR, scientific, layered-editor and GPU-texture formats, ZIP PSD).
/// Registration uses this to know which formats Glide's provider can really thumbnail on this install, so a
/// build without a library (or a -NoHevc codec build without HEIF) never claims formats it cannot render.
/// </summary>
internal static class BundledCodecLibraries
{
    public const string FolderName = "codecs";
    public const string BundledDllName = "glidecodecs.dll";
    public const string ManagedDllName = "glidemanaged.dll";
    private const string BundledManifestName = "glidecodecs.glidecodec.json";

    /// <summary>Suffixes glidecodecs.dll decodes in a full build (the manifest is authoritative when present).</summary>
    public static IReadOnlyList<string> BundledExtensions { get; } = new[]
    {
        ".avif", ".avifs", ".heic", ".heif", ".heics", ".heifs", ".hif", ".jxl",
        ".jp2", ".j2k", ".j2c", ".jpc", ".jpx", ".jpf", ".jpm", ".mj2",
    };

    /// <summary>
    /// Suffixes glidemanaged.dll decodes (its GlideCodecProvider_GetInfo table in src/Glide.ThumbnailCodecs/Exports.cs;
    /// ThumbnailCoverageTests checks the two agree).
    /// </summary>
    public static IReadOnlyList<string> ManagedExtensions { get; } = new[]
    {
        ".exr", ".pfm", ".hdr.gz", ".dcm", ".dicom", ".ima", ".nii", ".nii.gz", ".fits", ".fit", ".fts", ".fts.gz",
        ".ora", ".kra", ".pdn", ".xcf", ".ktx", ".ktx2", ".pvr", ".astc", ".psd", ".psb", ".pdd",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".ori", ".rw2", ".rwl",
        ".pef", ".ptx", ".3fr", ".fff", ".iiq", ".cap", ".eip", ".mef", ".mos", ".mrw", ".x3f", ".erf", ".kdc", ".dcr",
        ".k25", ".bay", ".srw", ".rwz", ".gpr", ".mdc", ".raw", ".dcs", ".drf", ".dsc", ".r2d", ".rw1",
    };

    /// <summary><c>&lt;directory of Glide.exe&gt;\codecs</c>, where the installer and the portable ZIP put them.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>
    /// The suffixes the thumbnail provider can decode through a codec library present in
    /// <paramref name="codecDirectory"/> (default: next to Glide.exe). Read-only file checks; never throws.
    /// </summary>
    public static IReadOnlyList<string> AvailableExtensions(string? codecDirectory = null)
    {
        var directory = codecDirectory ?? DefaultDirectory;
        var result = new List<string>();
        try
        {
            if (File.Exists(Path.Combine(directory, BundledDllName)))
                result.AddRange(ReadManifestExtensions(Path.Combine(directory, BundledManifestName)) ?? BundledExtensions);
            if (File.Exists(Path.Combine(directory, ManagedDllName)))
                result.AddRange(ManagedExtensions);
        }
        catch
        {
            // Unreadable folder: claim nothing extra.
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The manifest's "extensions" array (lower-case, dot-prefixed), or null when it cannot be read.</summary>
    internal static IReadOnlyList<string>? ReadManifestExtensions(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 64 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (!document.RootElement.TryGetProperty("extensions", out var array) || array.ValueKind != JsonValueKind.Array) return null;
            var extensions = array.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim().ToLowerInvariant())
                .Where(x => x.Length is > 1 and < 16 && x[0] == '.')
                .ToArray();
            return extensions.Length > 0 ? extensions : null;
        }
        catch
        {
            return null;
        }
    }
}
