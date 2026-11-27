namespace Glide.Core.Settings;

/// <summary>
/// 5.2.0 "Thumbnails loaded at once" (performance.thumbnailLanes): the Explorer tab's thumbnail decode lanes. Automatic keeps the
/// hardware-tier value; a fixed number is used as given. Either way the result is clamped by free memory: each lane may hold one
/// decoded frame plus the file bytes waiting for it (a 24 MP PNG is 96 MB, a JPEG up to 48 MB of bytes), so a lane is budgeted at
/// <see cref="BytesPerLane"/> and the lanes may use at most a quarter of the available memory.
/// </summary>
public static class ThumbnailLanesSetting
{
    public const string Automatic = "Automatic";
    public const int MaxLanes = 32;
    public const long BytesPerLane = 160L * 1024 * 1024;

    /// <summary>The combo box's choices, in order.</summary>
    public static readonly string[] Choices = [Automatic, "4", "8", "12", "16", "24", "32"];

    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return Choices.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase)) ?? Automatic;
    }

    /// <summary>Automatic lanes: three quarters of the logical processors on a high-end PC (2..12), half elsewhere (2..8).</summary>
    public static int AutomaticLanes(bool highEnd, int logicalProcessors) => highEnd
        ? Math.Clamp(logicalProcessors * 3 / 4, 2, 12)
        : Math.Clamp(logicalProcessors / 2, 2, 8);

    /// <summary>Most lanes the free memory allows (at least 2): a quarter of <paramref name="availableBytes"/> / <see cref="BytesPerLane"/>.</summary>
    public static int MemoryCap(long availableBytes) => availableBytes <= 0 ? MaxLanes : (int)Math.Clamp(availableBytes / 4 / BytesPerLane, 2, MaxLanes);

    /// <summary>The decode lanes for a setting value on this machine.</summary>
    public static int Resolve(string? value, bool highEnd, int logicalProcessors, long availableBytes)
    {
        var choice = Normalize(value);
        var wanted = choice == Automatic ? AutomaticLanes(highEnd, logicalProcessors) : int.Parse(choice, System.Globalization.CultureInfo.InvariantCulture);
        return Math.Clamp(Math.Min(wanted, MemoryCap(availableBytes)), 1, MaxLanes);
    }

    /// <summary>I/O lanes for a number of decode lanes: twice as many, 4..32 (a slow disk is latency bound, not CPU bound).</summary>
    public static int IoLanesFor(int decodeLanes) => Math.Clamp(decodeLanes * 2, 4, 32);
}
