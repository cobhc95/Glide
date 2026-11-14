namespace Glide.Core.Windows;

/// <summary>How much of a window's geometry Glide remembers between openings.</summary>
public enum WindowPlacementMode
{
    /// <summary>Resizable windows: size, position and the maximized state.</summary>
    SizeAndPosition,
    /// <summary>Fixed-size or content-sized dialogs: only where the window was; its size always comes from its content.</summary>
    PositionOnly,
}

/// <summary>
/// A remembered window geometry. Position is in device pixels (the outer window's top-left), size in DIPs (the client
/// size when not maximized), exactly like the main window's persisted placement. A non-positive size means "no size saved";
/// <see cref="int.MinValue"/> coordinates mean "no position saved".
/// </summary>
public readonly record struct SavedWindowPlacement(int X, int Y, double Width, double Height, bool Maximized)
{
    public static SavedWindowPlacement None { get; } = new(int.MinValue, int.MinValue, 0, 0, false);
    public bool HasSize => Width > 0 && Height > 0 && !double.IsNaN(Width) && !double.IsNaN(Height) && !double.IsInfinity(Width) && !double.IsInfinity(Height);
    public bool HasPosition => X != int.MinValue && Y != int.MinValue;
}

/// <summary>A monitor's usable area in device pixels and its DPI scale (1.0 = 96 DPI).</summary>
public readonly record struct ScreenWorkArea(int X, int Y, int Width, int Height, double Scale)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// What to apply before the window is shown. <see cref="Width"/>/<see cref="Height"/> are null when the size must not be
/// touched (position-only windows). <see cref="X"/>/<see cref="Y"/> are null when the window must keep its own default
/// startup location (normally centred on its owner).
/// </summary>
public readonly record struct ResolvedWindowPlacement(double? Width, double? Height, double MinWidth, double MinHeight, int? X, int? Y, bool Maximized)
{
    public bool HasPosition => X.HasValue && Y.HasValue;
}

/// <summary>
/// Pure validation of a remembered window geometry against the monitors that exist now (shared by every Glide window
/// that remembers its placement). Rules:
/// <list type="bullet">
/// <item>The monitor the window was on is the one its saved rectangle overlaps most. A window whose title bar is not
/// at least a grabbable sliver on any monitor (monitor gone, moved off-screen) gets no position and opens with its
/// default startup location (centred on its owner).</item>
/// <item>The size is clamped to that monitor's working area (DPI-aware: DIPs times the monitor scale), and never below
/// the window's minimum size where the area allows it. A tiny or garbage saved size therefore becomes the minimum.</item>
/// <item>The position is moved so the whole window lies inside the working area.</item>
/// <item>Maximized is only restored for windows that remember their size.</item>
/// </list>
/// </summary>
public static class WindowPlacementResolver
{
    /// <summary>Window chrome (title bar and borders) outside the client size, as an allowance when the frame size is not known yet.</summary>
    public const double ChromeAllowanceWidth = 16;
    public const double ChromeAllowanceHeight = 40;

    /// <summary>
    /// Validates <paramref name="saved"/>. Returns null when nothing usable is saved (the window keeps its own defaults).
    /// For <see cref="WindowPlacementMode.PositionOnly"/> windows <paramref name="currentWidth"/>/<paramref name="currentHeight"/>
    /// (DIPs, the window's own default size or a content estimate) are used to keep the whole window on screen.
    /// </summary>
    public static ResolvedWindowPlacement? Resolve(
        SavedWindowPlacement saved,
        IReadOnlyList<ScreenWorkArea> screens,
        ScreenWorkArea primary,
        WindowPlacementMode mode,
        double minWidth,
        double minHeight,
        double currentWidth = 0,
        double currentHeight = 0)
    {
        var usable = (screens ?? Array.Empty<ScreenWorkArea>()).Where(s => !s.IsEmpty).ToList();
        if (usable.Count == 0 && primary.IsEmpty) return null;
        var candidates = usable.Count > 0 ? (IReadOnlyList<ScreenWorkArea>)usable : new[] { primary };
        minWidth = Finite(minWidth);
        minHeight = Finite(minHeight);

        double width, height;
        if (mode == WindowPlacementMode.SizeAndPosition)
        {
            if (!saved.HasSize) return null;
            width = saved.Width;
            height = saved.Height;
        }
        else
        {
            if (!saved.HasPosition) return null;
            width = Math.Max(Finite(currentWidth), Math.Max(minWidth, 1));
            height = Math.Max(Finite(currentHeight), Math.Max(minHeight, 1));
        }

        ScreenWorkArea? home = null;
        if (saved.HasPosition)
        {
            long best = 0;
            foreach (var screen in candidates)
            {
                var scale = ScaleOf(screen);
                var outerW = (long)Math.Min(int.MaxValue / 4, Math.Ceiling((width + ChromeAllowanceWidth) * scale));
                var outerH = (long)Math.Min(int.MaxValue / 4, Math.Ceiling((height + ChromeAllowanceHeight) * scale));
                var area = Overlap(saved.X, saved.Y, outerW, outerH, screen);
                if (area > best) { best = area; home = screen; }
            }
            // Mostly off-screen (less than a sliver of the title bar visible) counts as off-screen.
            if (home is { } h)
            {
                var scale = ScaleOf(h);
                var titleW = (long)Math.Min(int.MaxValue / 4, Math.Ceiling(width * scale));
                var titleH = (long)Math.Ceiling(32 * scale);
                var (visibleW, visibleH) = OverlapSize(saved.X, saved.Y, titleW, titleH, h);
                if (visibleW < 80 * scale || visibleH < 16 * scale) home = null;
            }
        }

        var target = home ?? (!primary.IsEmpty ? primary : candidates[0]);
        var s = ScaleOf(target);
        var maxWidth = Math.Max(1, Math.Floor(target.Width / s - ChromeAllowanceWidth));
        var maxHeight = Math.Max(1, Math.Floor(target.Height / s - ChromeAllowanceHeight));

        double? resolvedWidth = null, resolvedHeight = null;
        var resolvedMinWidth = minWidth;
        var resolvedMinHeight = minHeight;
        if (mode == WindowPlacementMode.SizeAndPosition)
        {
            width = Math.Min(width, maxWidth);
            height = Math.Min(height, maxHeight);
            width = Math.Max(width, Math.Min(minWidth, maxWidth));
            height = Math.Max(height, Math.Min(minHeight, maxHeight));
            resolvedWidth = width;
            resolvedHeight = height;
            resolvedMinWidth = Math.Min(minWidth, width);
            resolvedMinHeight = Math.Min(minHeight, height);
        }
        else
        {
            width = Math.Min(width, maxWidth);
            height = Math.Min(height, maxHeight);
        }

        int? x = null, y = null;
        if (home is not null)
        {
            var outerW = (int)Math.Ceiling((width + ChromeAllowanceWidth) * s);
            var outerH = (int)Math.Ceiling((height + ChromeAllowanceHeight) * s);
            x = Math.Max(target.X, Math.Min(saved.X, target.Right - outerW));
            y = Math.Max(target.Y, Math.Min(saved.Y, target.Bottom - outerH));
        }
        if (mode == WindowPlacementMode.PositionOnly && x is null) return null;
        return new ResolvedWindowPlacement(resolvedWidth, resolvedHeight, resolvedMinWidth, resolvedMinHeight, x, y,
            mode == WindowPlacementMode.SizeAndPosition && saved.Maximized);
    }

    private static double ScaleOf(ScreenWorkArea screen) => screen.Scale > 0 && !double.IsNaN(screen.Scale) ? screen.Scale : 1;

    private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;

    private static (long Width, long Height) OverlapSize(long x, long y, long w, long h, ScreenWorkArea screen)
    {
        var left = Math.Max(x, screen.X);
        var top = Math.Max(y, screen.Y);
        var right = Math.Min(x + w, (long)screen.Right);
        var bottom = Math.Min(y + h, (long)screen.Bottom);
        return (Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static long Overlap(long x, long y, long w, long h, ScreenWorkArea screen)
    {
        var (ow, oh) = OverlapSize(x, y, w, h, screen);
        return ow * oh;
    }
}
