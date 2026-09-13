namespace Glide.Imaging;

/// <summary>Versioned, size-checked ABI contract for optional decoder-only providers.</summary>
public readonly record struct CodecProviderInfo(
    uint AbiVersion, uint StructSize, string Id, string Version, string License,
    IReadOnlyList<string> Extensions, bool Preview, bool Full, bool Metadata, bool Frames);

public readonly record struct CodecProbeResult(bool Supported, ImageDimensions Dimensions, int FrameCount, bool Animated, string? Error);
public readonly record struct CodecFrameInfo(int Index, int Width, int Height, TimeSpan? Duration, string? Label = null);

/// <summary>
/// ABI-v1/provider-neutral contract. Registration transfers ownership only when explicitly stated by
/// the backend; the process-wide lazy runtime owns providers it resolves for multiple windows.
/// </summary>
public interface ICodecProvider : IDisposable
{
    CodecProviderInfo Info { get; }
    CodecProbeResult Probe(string path, CancellationToken cancellationToken = default);
    Task<Stream?> DecodePreviewAsync(string path, int longestSide, CancellationToken cancellationToken = default);
    Task<Stream?> DecodeFullAsync(string path, CancellationToken cancellationToken = default);
    Task<ImageMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CodecFrameInfo>> ReadFramesAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CodecFrameInfo>>(Array.Empty<CodecFrameInfo>());
}

/// <summary>
/// ABI-v2 fast path. Providers return decoded premultiplied BGRA8 rather than encoded interchange
/// bytes. The v1 stream methods remain for compatibility/fallback while providers migrate.
/// </summary>
public interface ICodecSurfaceProvider : ICodecProvider
{
    Task<CodecPixelSurface?> DecodePreviewSurfaceAsync(string path, int longestSide, CancellationToken cancellationToken = default);
    Task<CodecPixelSurface?> DecodeFullSurfaceAsync(string path, CancellationToken cancellationToken = default);
}

public static class CodecProviderAbi
{
    public const uint LegacyVersion = 1;
    public const uint CurrentVersion = 2;
    public static uint MinimumStructSize => checked((uint)(16 + IntPtr.Size * 4));
    public const uint SupportsPreview = 1;
    public const uint SupportsFull = 2;
    public const uint SupportsMetadata = 4;
    public const uint SupportsFrames = 8;

    public static bool IsSupportedVersion(uint version) => version is LegacyVersion or CurrentVersion;

    public static bool IsSafe(CodecProviderInfo info) =>
        IsSupportedVersion(info.AbiVersion) && info.StructSize >= MinimumStructSize &&
        !string.IsNullOrWhiteSpace(info.Id) && !string.IsNullOrWhiteSpace(info.Version) &&
        !string.IsNullOrWhiteSpace(info.License) && info.Extensions is not null && info.Extensions.Count > 0 &&
        info.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == info.Extensions.Count &&
        info.Extensions.All(x => x.StartsWith('.') && x.Length <= 32 && x.IndexOfAny(new[] { '/', '\\', '\0' }) < 0);

    public static bool CapabilitiesMatch(CodecProviderInfo info, uint flags) =>
        info.Preview == ((flags & SupportsPreview) != 0) &&
        info.Full == ((flags & SupportsFull) != 0) &&
        info.Metadata == ((flags & SupportsMetadata) != 0) &&
        info.Frames == ((flags & SupportsFrames) != 0);
}
