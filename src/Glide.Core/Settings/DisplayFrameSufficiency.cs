namespace Glide.Core.Settings;

/// <summary>
/// Pure size math for "is the shown frame big enough": everything in physical pixels. A frame is sufficient when it
/// carries at least the pixels it is drawn at (fit size x render scaling, or the zoomed destination), capped at the
/// source size. 5.1.0 showed prefetch/DCT-scaled frames stretched at Fit and never refined them.
/// </summary>
public static class DisplayFrameSufficiency
{
    /// <summary>Frames within this fraction of the needed width are treated as sufficient (rounding of DCT scales).</summary>
    public const double Tolerance = 0.97;

    /// <summary>
    /// Decode route of a bounded progressive JPEG: WIC's staged first-scans level, full pixel count but DC-only
    /// detail. Such a frame is never sufficient, even when its size matches the fit box or the source.
    /// </summary>
    public const string ProgressiveStagedRoute = "wic-native-progressive-staged";

    /// <summary>
    /// Route of a cached frame reduced after decode (left image shrunk to fit, warm tab copy, staged level): good
    /// enough to show at once, never final when it becomes current again.
    /// </summary>
    public const string ReducedFrameRoute = "prepared-reduced";

    /// <summary>True when a frame from this decode route carries its pixel count but not its detail (always refine).</summary>
    public static bool IsDetailIncompleteRoute(string? route) =>
        string.Equals(route, ProgressiveStagedRoute, StringComparison.Ordinal) ||
        string.Equals(route, ReducedFrameRoute, StringComparison.Ordinal);

    /// <summary>Route reported for a prepared-cache hit: a detail-incomplete cached frame keeps the staged route.</summary>
    public static string PreparedHitRoute(bool detailComplete) => detailComplete ? "prepared-cache" : ReducedFrameRoute;

    /// <summary>
    /// True when the shown frame must still be refined: visibly undersized, or a preview whose decode route left its
    /// detail incomplete (a staged progressive level matches the fit size but is blurry).
    /// </summary>
    public static bool NeedsRefinement(int frameWidth, int neededWidth, bool isPreview, string? route) =>
        IsUndersized(frameWidth, neededWidth) || (isPreview && IsDetailIncompleteRoute(route));

    /// <summary>DIPs x render scaling (at least 1) -> physical pixels, rounded up.</summary>
    public static int ToPhysical(double dips, double renderScaling) =>
        double.IsFinite(dips) && dips > 0 ? (int)Math.Ceiling(dips * Math.Max(1.0, double.IsFinite(renderScaling) ? renderScaling : 1.0) - 1e-9) : 0;

    /// <summary>Physical size of a source fitted into the viewport (downscale only; never above the source).</summary>
    public static (int Width, int Height) FitNeeded(double viewportDipWidth, double viewportDipHeight, double renderScaling, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0);
        var vw = ToPhysical(viewportDipWidth, renderScaling);
        var vh = ToPhysical(viewportDipHeight, renderScaling);
        if (vw <= 0 || vh <= 0) return (sourceWidth, sourceHeight); // not laid out yet: only the source is safe
        var scale = Math.Min(1.0, Math.Min(vw / (double)sourceWidth, vh / (double)sourceHeight));
        return (Math.Max(1, (int)Math.Ceiling(sourceWidth * scale - 1e-9)), Math.Max(1, (int)Math.Ceiling(sourceHeight * scale - 1e-9)));
    }

    /// <summary>Physical width needed for an image drawn <paramref name="shownDipWidth"/> wide (manual zoom), capped at the source.</summary>
    public static int ShownNeededWidth(double shownDipWidth, double renderScaling, int sourceWidth) =>
        Math.Min(sourceWidth > 0 ? sourceWidth : int.MaxValue, ToPhysical(shownDipWidth, renderScaling));

    /// <summary>True when a frame <paramref name="frameWidth"/> px wide is visibly smaller than <paramref name="neededWidth"/>.</summary>
    public static bool IsUndersized(int frameWidth, int neededWidth) =>
        neededWidth > 0 && frameWidth > 0 && frameWidth < neededWidth * Tolerance;

    /// <summary>
    /// True when a decoder-scaled prefetch frame is smaller than the source fitted into the physical
    /// <paramref name="boxWidth"/> x <paramref name="boxHeight"/> box (a progressive JPEG or MPF/WIC preview can
    /// come back at 1/2 or 1/4 of it). Such a neighbour must be decoded at source size instead.
    /// </summary>
    public static bool PrefetchFrameTooSmall(int frameWidth, int frameHeight, int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || boxWidth <= 0 || boxHeight <= 0) return false;
        var fit = FitNeeded(boxWidth, boxHeight, 1.0, sourceWidth, sourceHeight);
        return IsUndersized(frameWidth, fit.Width) || IsUndersized(frameHeight, fit.Height);
    }
}
