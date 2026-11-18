namespace Glide.Core.Settings;

/// <summary>
/// Pure size math for the 5.1.1 RAM work (docs/audits/ram-audit-5.1.1.md). Every frame size it returns is at least the
/// physical fit size (<see cref="DisplayFrameSufficiency.FitNeeded"/>) in either quarter-turn orientation, so a kept
/// frame can never be undersized at Fit; zoom detail still comes from the existing full-resolution refine.
/// </summary>
public static class FrameMemoryBudget
{
    /// <summary>A frame is only shrunk when the result is at most this fraction of its pixels (otherwise the work is not worth it).</summary>
    public const double MinShrinkSavingRatio = 0.7;

    /// <summary>
    /// Size a frame of <paramref name="frameWidth"/> x <paramref name="frameHeight"/> can be reduced to while staying at least
    /// the physical fit size inside the <paramref name="boxWidth"/> x <paramref name="boxHeight"/> box, also when the view is
    /// turned a quarter (the box is then used transposed). (0, 0) when the box is unknown or the saving is too small.
    /// </summary>
    public static (int Width, int Height) ShrinkTarget(int frameWidth, int frameHeight, int boxWidth, int boxHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || boxWidth <= 0 || boxHeight <= 0) return (0, 0);
        var upright = DisplayFrameSufficiency.FitNeeded(boxWidth, boxHeight, 1.0, frameWidth, frameHeight);
        var turned = DisplayFrameSufficiency.FitNeeded(boxHeight, boxWidth, 1.0, frameWidth, frameHeight);
        // Both fits share the frame's aspect, so the larger of each side is the larger fit.
        var w = Math.Min(frameWidth, Math.Max(upright.Width, turned.Width));
        var h = Math.Min(frameHeight, Math.Max(upright.Height, turned.Height));
        if ((double)w * h > (double)frameWidth * frameHeight * MinShrinkSavingRatio) return (0, 0);
        return (w, h);
    }

    /// <summary>
    /// Number of reduced (fit-size, not full) frames the decoded cache keeps: the prefetch window on both sides
    /// (2 x depth) plus the current image and one spare. Counted in frames, not bytes, so a decoder scale that comes back
    /// a little above the fit box can never push a frame of the window out. Older reduced frames beyond it are evicted
    /// (they would only be reached by a jump, which re-prefetches anyway).
    /// </summary>
    public static int NeighbourFrameLimit(int prefetchDepth) => 2 * Math.Max(1, prefetchDepth) + 2;
}
