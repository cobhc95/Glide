namespace Glide.Core;

/// <summary>Pure pointer classification shared by tab-origin drag handlers and tests.</summary>
public static class TabDragPolicy
{
    public const double DefaultThreshold = 4;

    public static bool CrossedThreshold(double deltaX, double deltaY, double threshold = DefaultThreshold)
    {
        if (double.IsNaN(deltaX) || double.IsNaN(deltaY) || double.IsNaN(threshold) || threshold < 0) return false;
        return Math.Abs(deltaX) >= threshold || Math.Abs(deltaY) >= threshold;
    }
}
