namespace Glide.Core;

public enum CodecTier { NativeOs, BuiltIn, ModularNative, ExternalProvider, RoutedOnDemand, Unsupported }

/// <summary>
/// One auditable capability row. DecodeEnabled remains deliberately strict: it means a decoder is
/// known in-process now (core or verified external provider). Routed/recognised suffixes may still
/// resolve through WIC, Shell thumbnails, or a lazy provider when the file is first opened.
/// </summary>
public sealed record CodecCapability(string Extension, CodecTier Tier, string Provider, bool DecodeEnabled, bool Progressive, bool Animated, bool RawMetadata);

public static class CodecCapabilityRegistry
{
    private static readonly HashSet<string> Core = new(ImageFormatRegistry.CoreFastPathExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Suffixes Glide can decode in-process with a bundled decoder (no optional codec required):
    /// the SVG vector rasteriser and the compact built-in raster decoders.
    /// </summary>
    private static readonly HashSet<string> BuiltInDecoded = new(new[]
    {
        ".svg", ".svgz",
        ".tga", ".targa", ".icb", ".vda", ".vst",
        ".pcx", ".pnm", ".ppm", ".pgm", ".pbm", ".pam",
        ".qoi", ".hdr", ".rgbe", ".wbmp", ".xbm", ".xpm",
        ".sgi", ".rgb", ".rgba", ".bw"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly object ExternalGate = new();
    private static readonly Dictionary<string, string> ExternalProviders = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> Extensions { get; } = ImageFormatRegistry.Extensions;
    public static IReadOnlyList<CodecCapability> All => Extensions.Select(Create).ToArray();
    public static IReadOnlyList<string> DecodeEnabledExtensions => All.Where(x => x.DecodeEnabled).Select(x => x.Extension).ToArray();
    public static IReadOnlyList<string> RoutedExtensions => Extensions;
    public static CodecCapability Describe(string extensionOrPath) => Create(Normalize(extensionOrPath));
    public static bool IsDeclared(string path) => Extensions.Contains(Normalize(path), StringComparer.OrdinalIgnoreCase);
    public static string? Match(string path) => ImageFormatRegistry.Match(path);
    public static string GetLongestExtension(string path) => ImageFormatRegistry.GetLongestExtension(path);

    /// <summary>Registers only a verified, live provider. Registry state is process-local and replaceable.</summary>
    public static void RegisterExternalProvider(string providerId, IEnumerable<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider ID is required.", nameof(providerId));
        lock (ExternalGate)
        {
            foreach (var extension in extensions.Select(Normalize).Where(IsKnownExtension))
                ExternalProviders[extension] = providerId;
        }
    }

    public static void ClearExternalProviders()
    {
        lock (ExternalGate) ExternalProviders.Clear();
    }

    public static IReadOnlyDictionary<string, string> ExternalProviderSnapshot()
    {
        lock (ExternalGate) return new Dictionary<string, string>(ExternalProviders, StringComparer.OrdinalIgnoreCase);
    }

    private static CodecCapability Create(string ext)
    {
        string? externalProvider;
        lock (ExternalGate) ExternalProviders.TryGetValue(ext, out externalProvider);

        var native = ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".jif" or ".jfi" or ".pjpeg" or ".pjpg"
            or ".png" or ".apng" or ".bmp" or ".dib" or ".rle" or ".gif" or ".ico" or ".cur"
            or ".tif" or ".tiff" or ".webp" or ".jxr" or ".wdp" or ".hdp" or ".dds";
        var builtInCandidate = ext is ".pnm" or ".ppm" or ".pgm" or ".pbm" or ".pam" or ".pfm"
            or ".qoi" or ".tga" or ".targa" or ".icb" or ".vda" or ".vst" or ".pcx"
            or ".hdr" or ".rgbe" or ".dds" or ".wbmp" or ".xbm" or ".xpm";
        var modular = ext is ".avif" or ".avifs" or ".heic" or ".heif" or ".heics" or ".heifs" or ".hif"
            or ".jxl" or ".jp2" or ".j2k" or ".j2c" or ".jpc" or ".jpx" or ".jpf" or ".jpm" or ".mj2"
            or ".svg" or ".svgz" or ".exr";

        var builtInDecoded = BuiltInDecoded.Contains(ext);
        var tier = externalProvider is not null ? CodecTier.ExternalProvider
            : Core.Contains(ext) || native ? CodecTier.NativeOs
            : builtInDecoded ? CodecTier.BuiltIn
            : builtInCandidate ? CodecTier.BuiltIn
            : modular ? CodecTier.ModularNative
            : IsKnownExtension(ext) ? CodecTier.RoutedOnDemand
            : CodecTier.Unsupported;

        // Strict truth: the preserved core, a verified/loaded provider, or a bundled built-in decoder.
        // RoutedOnDemand remains recognised/navigable but is not advertised here as pre-verified.
        var enabled = Core.Contains(ext) || externalProvider is not null || builtInDecoded;
        return new CodecCapability(ext, tier,
            externalProvider ?? (Core.Contains(ext) ? "Glide core (Avalonia/WIC)"
                : native ? "Windows WIC/Avalonia on-demand"
                : builtInDecoded ? (ext is ".svg" or ".svgz" ? "Glide vector (Svg.Skia)" : "Glide built-in raster decoder")
                : builtInCandidate ? "Glide compact/provider route"
                : modular ? "Optional modular provider"
                : IsKnownExtension(ext) ? "Adaptive lazy route"
                : "Unavailable"),
            enabled,
            ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".webp" or ".jxl",
            ext is ".gif" or ".webp" or ".apng" or ".ani" or ".avifs" or ".heifs",
            ext is ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".raf" or ".orf" or ".rw2");
    }

    private static bool IsKnownExtension(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    private static string Normalize(string value) => Match(value) ?? (value.StartsWith('.') ? value.ToLowerInvariant() : Path.GetExtension(value).ToLowerInvariant());
}
