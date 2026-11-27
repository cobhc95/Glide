using Avalonia;

namespace Glide.App.Controls;

/// <summary>
/// Fixed transparent HWND box ("stage") held for the duration of an Overlay mode resize drag.
/// Resizing a Win32 RedirectionSurface HWND via <c>SetWindowPos</c> on every pointer-move frame makes DWM composite
/// the new HWND bounds against the previous frame's redirection bitmap before Avalonia's async render thread presents
/// the new size (clipping the image and chrome on every step). Holding one transparent stage HWND that covers the
/// reachable drag range lets every drag frame resize only <c>ImageView</c> and the chrome inside Avalonia's single
/// atomic render pass, with one <c>SetWindowPos</c> when the drag ends.
/// </summary>
internal readonly record struct OverlayResizeStage(PixelPoint Origin, Size Size, bool Active)
{
    /// <summary>
    /// Computes the transparent stage HWND box for a resize drag starting at <paramref name="startOrigin"/> /
    /// <paramref name="startSize"/> with upper bounds <paramref name="maxWidth"/> and <paramref name="maxHeight"/> (DIPs).
    /// Returns <see cref="Active"/> = false when the upper bounds are not finite (e.g. headless tests without a screen).
    /// </summary>
    public static OverlayResizeStage Compute(OverlayResizeMode mode, PixelPoint startOrigin, Size startSize,
        double maxWidth, double maxHeight, double scale)
    {
        if (scale <= 0 || !double.IsFinite(scale) || startSize.Width <= 0 || startSize.Height <= 0)
            return new OverlayResizeStage(startOrigin, startSize, Active: false);

        return mode switch
        {
            OverlayResizeMode.WidthOnly when double.IsFinite(maxWidth) =>
                new OverlayResizeStage(startOrigin, new Size(Math.Max(startSize.Width, maxWidth), startSize.Height), Active: true),

            OverlayResizeMode.HeightOnly when double.IsFinite(maxHeight) => ComputeHeightOnly(startOrigin, startSize, maxHeight, scale),

            OverlayResizeMode.Proportional when double.IsFinite(maxWidth) && double.IsFinite(maxHeight) =>
                new OverlayResizeStage(startOrigin,
                    new Size(Math.Max(startSize.Width, maxWidth), Math.Max(startSize.Height, maxHeight)),
                    Active: true),

            _ => new OverlayResizeStage(startOrigin, startSize, Active: false)
        };
    }

    private static OverlayResizeStage ComputeHeightOnly(PixelPoint startOrigin, Size startSize, double maxHeight, double scale)
    {
        var stageHeight = Math.Max(startSize.Height, maxHeight);
        var topGrowthPx = (int)Math.Round(Math.Max(0, stageHeight - startSize.Height) * scale);
        return new OverlayResizeStage(
            new PixelPoint(startOrigin.X, startOrigin.Y - topGrowthPx),
            new Size(startSize.Width, stageHeight),
            Active: true);
    }

    /// <summary>Local DIP offset of a target window <paramref name="targetOrigin"/> inside this stage HWND.</summary>
    public Thickness ContentMargin(PixelPoint targetOrigin, double scale)
    {
        var safeScale = scale > 0 ? scale : 1.0;
        var dx = Math.Max(0, (targetOrigin.X - Origin.X) / safeScale);
        var dy = Math.Max(0, (targetOrigin.Y - Origin.Y) / safeScale);
        return new Thickness(dx, dy, 0, 0);
    }
}
