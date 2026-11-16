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

    /// <summary>
    /// The insertion index (pre-removal, as <c>WorkspaceState.Move</c> expects) that an in-strip reorder preview
    /// shows for a dragged tab offset by <paramref name="offset"/> along the strip. A neighbour counts as swapped
    /// once the dragged tab is half a slot across it, which is also where the live animation is half way. Release
    /// commits exactly this value, so the drop always matches the last preview.
    /// </summary>
    public static int PreviewInsertIndex(int originalIndex, int tabCount, double offset, double slot)
    {
        if (tabCount <= 0 || originalIndex < 0 || originalIndex >= tabCount) return -1;
        if (double.IsNaN(offset) || double.IsInfinity(offset)) offset = 0;
        if (!(slot > 0)) slot = 1;
        var position = Math.Clamp(originalIndex + (int)Math.Round(offset / slot, MidpointRounding.AwayFromZero), 0, tabCount - 1);
        return position > originalIndex ? position + 1 : position;
    }

    /// <summary>Insertion index for a pointer at <paramref name="pointer"/> along a strip of tabs (start, length): before the first tab whose midpoint is right of it.</summary>
    public static int InsertIndexFromPointer(IReadOnlyList<(double Start, double Length)> tabs, double pointer)
    {
        for (var i = 0; i < tabs.Count; i++)
            if (pointer < tabs[i].Start + tabs[i].Length / 2) return i;
        return tabs.Count;
    }

    /// <summary>Strip coordinate of the insertion marker for <paramref name="index"/>: centred in the gap between tabs, or just outside the ends.</summary>
    public static double InsertMarkerPosition(IReadOnlyList<(double Start, double Length)> tabs, int index, double spacing)
    {
        if (tabs.Count == 0) return 0;
        index = Math.Clamp(index, 0, tabs.Count);
        var half = Math.Max(0, spacing) / 2;
        if (index == 0) return Math.Max(0, tabs[0].Start - half);
        var previousEnd = tabs[index - 1].Start + tabs[index - 1].Length;
        if (index == tabs.Count) return previousEnd + half;
        return (previousEnd + tabs[index].Start) / 2;
    }
}
