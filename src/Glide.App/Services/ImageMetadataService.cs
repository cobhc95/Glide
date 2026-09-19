using Glide.Imaging;
using Glide.App.Diagnostics;

namespace Glide.App.Services;

/// <summary>Coordinates optional provider metadata and the bounded built-in fallback.</summary>
public static class ImageMetadataService
{
    public static bool BypassNavigationMetadata { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("GLIDE_DIAG_BYPASS_METADATA"), "1", StringComparison.OrdinalIgnoreCase);

    public static async Task<ImageMetadata> ReadAsync(
        IImageDecoderBackend backend,
        string path,
        CancellationToken cancellationToken = default)
    {
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataRequested, detail: Path.GetExtension(path));
        if (BypassNavigationMetadata)
        {
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataBypassed, detail: Path.GetExtension(path));
            return new ImageMetadata();
        }

        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataProviderStart, detail: Path.GetExtension(path));
        var providerResult = await backend.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataProviderEnd,
            a: providerResult is null ? 0 : 1,
            b: providerResult?.HasExif == true ? 1 : 0,
            detail: Path.GetExtension(path));
        var metadata = providerResult ?? new ImageMetadata();
        if (!metadata.HasExif && metadata.FrameCount is null && metadata.Width == 0)
        {
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataFallbackStart, detail: Path.GetExtension(path));
            metadata = await ImageMetadataReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataFallbackEnd,
                a: metadata.HasExif ? 1 : 0, b: metadata.Width, detail: Path.GetExtension(path));
        }
        if (metadata.FrameCount is null)
        {
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataFramesStart, detail: Path.GetExtension(path));
            var frames = await backend.ReadFramesAsync(path, cancellationToken).ConfigureAwait(false);
            OverlayNavigationTrace.Mark(OverlayNavigationTrace.Kind.MetadataFramesEnd,
                a: frames?.Count ?? 0, detail: Path.GetExtension(path));
            if (frames is { Count: > 0 }) metadata = metadata with { FrameCount = frames.Count };
        }
        return metadata;
    }
}
