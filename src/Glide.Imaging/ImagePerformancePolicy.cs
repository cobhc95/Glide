using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Glide.Imaging;

/// <summary>
/// Runtime decode/browse policy. Profiles provide coordinated defaults, while Settings may
/// override the advanced fields before assigning the policy to ImageLoadCoordinator.
/// </summary>
public sealed record ImagePerformancePolicy
{
    public string Profile { get; init; } = "Balanced";
    public bool DecoderScaledFirstFrame { get; init; } = true;
    public int PreviewLongestSide { get; init; } = 1800;
    // Physical first-paint box. Zero means use PreviewLongestSide as a square fallback. Glide 2.6
    // keeps the box aspect-aware so portrait photos in wide windows are not decoded to the window width.
    public int PreviewMaxWidth { get; init; }
    public int PreviewMaxHeight { get; init; }
    public BitmapInterpolationMode PreviewInterpolation { get; init; } = BitmapInterpolationMode.MediumQuality;
    // When true, bounded progressive JPEG first paint skips the luminance-only level-0 flash and
    // requests an early level that normally contains Y + Cb + Cr. Maximum speed may disable this
    // for absolute latency; the setting remains independently user-overridable after any preset.
    public bool ProgressiveColorFirstPreview { get; init; } = true;
    public bool BackgroundRefinement { get; init; } = true;
    public int RefinementDelayMs { get; init; } = 20;
    public bool SequentialForegroundReads { get; init; } = true;
    public bool PredictivePrefetch { get; init; } = true;
    public int PrefetchDepth { get; init; } = 5;
    public int CompressedCacheItems { get; init; } = 16;
    public int CompressedCacheMegabytes { get; init; } = 256;
    public int DecodedCacheMegabytes { get; init; } = 256;
    // Balanced browsing keeps neighbours as lightweight presentation-ready previews. Full neighbour
    // refinement competes with the user's foreground decode on large-photo folders.
    public int FullNeighbourPredecodeCount { get; init; } = 0;

    public static ImagePerformancePolicy ForProfile(string? profile) => profile switch
    {
        "Maximum speed" => new ImagePerformancePolicy
        {
            Profile = "Maximum speed",
            PreviewLongestSide = 1280,
            PreviewInterpolation = BitmapInterpolationMode.LowQuality,
            ProgressiveColorFirstPreview = false,
            RefinementDelayMs = 60,
            PrefetchDepth = 3,
            CompressedCacheItems = 28,
            CompressedCacheMegabytes = 384,
            DecodedCacheMegabytes = 192,
            FullNeighbourPredecodeCount = 0
        },
        "Maximum quality" => new ImagePerformancePolicy
        {
            Profile = "Maximum quality",
            PreviewLongestSide = 1800,
            PreviewInterpolation = BitmapInterpolationMode.HighQuality,
            ProgressiveColorFirstPreview = true,
            RefinementDelayMs = 5,
            PrefetchDepth = 15,
            CompressedCacheItems = 20,
            CompressedCacheMegabytes = 999,
            DecodedCacheMegabytes = 999,
            FullNeighbourPredecodeCount = 2
        },
        _ => new ImagePerformancePolicy()
    };

    public ImagePerformancePolicy Normalize() => this with
    {
        PreviewLongestSide = Math.Clamp(PreviewLongestSide, 480, 4096),
        PreviewMaxWidth = PreviewMaxWidth <= 0 ? Math.Clamp(PreviewLongestSide, 480, 4096) : Math.Clamp(PreviewMaxWidth, 240, 4096),
        PreviewMaxHeight = PreviewMaxHeight <= 0 ? Math.Clamp(PreviewLongestSide, 480, 4096) : Math.Clamp(PreviewMaxHeight, 240, 4096),
        RefinementDelayMs = Math.Clamp(RefinementDelayMs, 5, 500),
        PrefetchDepth = Math.Clamp(PrefetchDepth, 1, 30),
        CompressedCacheItems = Math.Clamp(CompressedCacheItems, 2, 64),
        CompressedCacheMegabytes = Math.Clamp(CompressedCacheMegabytes, 32, 2048),
        DecodedCacheMegabytes = Math.Clamp(DecodedCacheMegabytes, 32, 2048),
        FullNeighbourPredecodeCount = Math.Clamp(FullNeighbourPredecodeCount, 0, 2)
    };
}
