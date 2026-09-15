namespace Glide.Imaging;

public enum GlideHardwareTier { Constrained, Mainstream, HighEnd }

public sealed record ImageHardwareProfile(GlideHardwareTier Tier, int LogicalProcessors, long AvailableMemoryBytes)
{
    public int ForegroundDecodeLanes => Tier == GlideHardwareTier.Constrained ? 1 : 2;
    public int CompressedWarmLanes => Tier == GlideHardwareTier.Constrained ? 1 : Tier == GlideHardwareTier.HighEnd ? 4 : 2;
    public int SpeculativeDecodeLanes => Tier == GlideHardwareTier.Constrained ? 1 : Tier == GlideHardwareTier.HighEnd ? 3 : 2;
    public long MaxSpeculativeCompressedFileBytes => Tier switch
    {
        GlideHardwareTier.Constrained => 32L * 1024 * 1024,
        GlideHardwareTier.HighEnd => 128L * 1024 * 1024,
        _ => 64L * 1024 * 1024
    };
    public double ViewportPreviewScale => Tier switch
    {
        // A first frame only needs enough pixels for the physical viewport plus a small guard for
        // fractional Fit/DPI interpolation. Legacy proved 1.12x is visually crisp; 1.50x needlessly
        // crossed JPEG DCT reduction thresholds on large portraits and made first paint much slower.
        GlideHardwareTier.Constrained => 1.02,
        GlideHardwareTier.HighEnd => 1.12,
        _ => 1.08
    };
}

/// <summary>
/// Extremely cheap, process-stable hardware classification. It never enumerates devices or storage
/// during startup. The governor uses coarse CPU/memory signals only to avoid overscheduling weak
/// hardware while allowing a large viewport preview on high-end systems.
/// </summary>
public static class ImagePerformanceGovernor
{
    private static readonly Lazy<ImageHardwareProfile> CurrentProfile = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);
    public static ImageHardwareProfile Current => CurrentProfile.Value;

    /// <summary>
    /// Returns the largest file eligible for speculative managed-byte caching. The limit is tied
    /// to the active cache budget and current memory pressure so a single neighbour cannot consume
    /// the cache or force a large duplicate of the OS file cache.
    /// </summary>
    public static long GetMaxSpeculativeCompressedFileBytes(ImagePerformancePolicy policy)
    {
        policy = policy.Normalize();
        var budgetLimit = policy.CompressedCacheMegabytes * 1024L * 1024L / 4;
        var availableLimit = long.MaxValue;
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
            {
                // Keep speculative managed bytes below 1/16 of available memory; this is a
                // conservative guard against duplicating the OS cache under memory pressure.
                availableLimit = Math.Max(8L * 1024 * 1024, info.TotalAvailableMemoryBytes / 16);
            }
        }
        catch { }
        return Math.Max(8L * 1024 * 1024, Math.Min(Current.MaxSpeculativeCompressedFileBytes, Math.Min(budgetLimit, availableLimit)));
    }

    public static int ChoosePreviewLongestSide(int configuredMaximum, int viewportLongestPhysicalPixels)
    {
        configuredMaximum = Math.Clamp(configuredMaximum, 480, 4096);
        if (viewportLongestPhysicalPixels <= 0) return configuredMaximum;
        var target = checked((int)Math.Ceiling(viewportLongestPhysicalPixels * Current.ViewportPreviewScale));
        return Math.Clamp(Math.Min(configuredMaximum, target), 480, configuredMaximum);
    }

    public static (int Width, int Height) ChoosePreviewBounds(int configuredMaximum, int viewportWidthPhysicalPixels, int viewportHeightPhysicalPixels)
    {
        configuredMaximum = Math.Clamp(configuredMaximum, 480, 4096);
        if (viewportWidthPhysicalPixels <= 0 || viewportHeightPhysicalPixels <= 0)
            return (configuredMaximum, configuredMaximum);

        var width = Math.Max(1, (int)Math.Ceiling(viewportWidthPhysicalPixels * Current.ViewportPreviewScale));
        var height = Math.Max(1, (int)Math.Ceiling(viewportHeightPhysicalPixels * Current.ViewportPreviewScale));
        var longest = Math.Max(width, height);
        if (longest > configuredMaximum)
        {
            var scale = configuredMaximum / (double)longest;
            width = Math.Max(1, (int)Math.Ceiling(width * scale));
            height = Math.Max(1, (int)Math.Ceiling(height * scale));
        }
        return (Math.Clamp(width, 240, 4096), Math.Clamp(height, 240, 4096));
    }

    private static ImageHardwareProfile Detect()
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        long memory;
        try { memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { memory = 0; }

        var gib = memory > 0 ? memory / (1024d * 1024 * 1024) : 0;
        var tier = cores <= 4 || (gib > 0 && gib < 8)
            ? GlideHardwareTier.Constrained
            : cores >= 12 && (gib <= 0 || gib >= 20)
                ? GlideHardwareTier.HighEnd
                : GlideHardwareTier.Mainstream;
        return new ImageHardwareProfile(tier, cores, memory);
    }
}
