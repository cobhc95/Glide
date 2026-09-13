using Glide.Imaging;

namespace Glide.App.Services;

/// <summary>Coordinates optional provider metadata and the bounded built-in fallback.</summary>
public static class ImageMetadataService
{
    public static async Task<ImageMetadata> ReadAsync(
        IImageDecoderBackend backend,
        string path,
        CancellationToken cancellationToken = default)
    {
        var metadata = await backend.ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false) ?? new ImageMetadata();
        if (!metadata.HasExif && metadata.FrameCount is null && metadata.Width == 0)
            metadata = await ImageMetadataReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata.FrameCount is null)
        {
            var frames = await backend.ReadFramesAsync(path, cancellationToken).ConfigureAwait(false);
            if (frames is { Count: > 0 }) metadata = metadata with { FrameCount = frames.Count };
        }
        return metadata;
    }
}
