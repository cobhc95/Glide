namespace Glide.Imaging;

public enum ImageNavigationActivity { Idle, NormalBrowse, RapidBrowse }

/// <summary>Pixels the viewport can use for the current image.</summary>
public readonly record struct ImageViewportDemand(int DisplayPixelWidth, int DisplayPixelHeight, double Zoom = 1, bool SelectionZoom = false, bool ExactPixelDemand = false)
{
    public static ImageViewportDemand Unspecified => new(0, 0, 1, false);
    public int RequiredLongestSide(int fallback)
    {
        var display = Math.Max(DisplayPixelWidth, DisplayPixelHeight);
        var scale = double.IsFinite(Zoom) && Zoom > 0 ? Zoom : 1;
        var requested = display > 0 ? display * scale : fallback;
        if (SelectionZoom) requested = Math.Max(requested, fallback);
        return (int)Math.Clamp(Math.Ceiling(requested), 480, 16384);
    }
}

public readonly record struct ImageRefinementPlan(bool Allowed, int LongestSide, bool FullResolution, ImageNavigationActivity Activity);

/// <summary>Decides whether detail is useful before the decoder is asked to allocate it.</summary>
public sealed class ImageRefinementScheduler
{
    public const int RapidBrowseWindowMs = 250;
    public static int BoundLongestSide(ImageDimensions source, int requestedLongestSide, long availableBytes)
    {
        if (!source.IsValid || availableBytes <= 0) return 1;
        var ratio = (double)Math.Min(source.Width, source.Height) / source.LongestSide;
        var max = Math.Sqrt(availableBytes / Math.Max(0.0001, ratio) / 4d);
        var floor = availableBytes >= 480L * 480 * 4 * ratio ? 480 : 1;
        return Math.Max(floor, Math.Min(requestedLongestSide, (int)Math.Min(16384, max)));
    }
    public ImageRefinementPlan Plan(ImageDimensions source, ImageViewportDemand demand, ImageNavigationActivity activity, ImagePerformancePolicy policy)
    {
        if (!source.IsValid || !policy.BackgroundRefinement || activity == ImageNavigationActivity.RapidBrowse)
            return new(false, 0, false, activity);
        var target = demand.RequiredLongestSide(policy.PreviewLongestSide * 2);
        var withinBudget = EstimatedBytes(source.Width, source.Height) <= policy.DecodedCacheMegabytes * 1024L * 1024L;
        // A committed selection zoom is an explicit request for source detail. It may still be
        // bounded by the cache budget; extreme sources remain on the display-resolution fallback.
        var full = (demand.ExactPixelDemand || demand.SelectionZoom || source.LongestSide <= target) && withinBudget;
        return new(true, Math.Min(source.LongestSide, target), full, activity);
    }
    private static long EstimatedBytes(int width, int height)
    {
        try { return checked((long)width * height * 4L); }
        catch (OverflowException) { return long.MaxValue; }
    }
}
