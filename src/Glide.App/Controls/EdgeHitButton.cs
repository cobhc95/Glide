using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering;

namespace Glide.App.Controls;

/// <summary>
/// A Button whose pointer target is its full layout rectangle, regardless of the CornerRadius its template draws.
///
/// Avalonia hit-tests a templated Button through the rounded background its ContentPresenter paints, so the
/// corner pixels outside the arc belong to whatever is underneath. For the title-bar "+" (44 DIP tall and
/// top-aligned when the window is maximized / fullscreen) those were the very top-row pixels a user throws the
/// pointer at: they fell through to the empty title bar, which dropped the hover highlight (flicker while moving
/// along the edge) and armed a window drag instead of a click. Browsers make edge-touching tab-strip controls
/// clickable to the last physical pixel (Fitts's law); this does the same while keeping the rounded visuals.
///
/// The control keeps every Button style and the Fluent template (StyleKeyOverride), so <c>Button.tabPlus</c> etc.
/// selectors still apply.
/// </summary>
public class EdgeHitButton : Button, ICustomHitTest
{
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Point is in this control's local coordinates (Avalonia's composition hit test transforms it).</summary>
    public bool HitTest(Point point) => IsRectangularHit(point, Bounds.Size);

    /// <summary>Inclusive rectangle test (the far edge counts, like Avalonia's own ClipToBounds hit test).</summary>
    internal static bool IsRectangularHit(Point point, Size size) =>
        size.Width > 0 && size.Height > 0 &&
        point.X >= 0 && point.Y >= 0 && point.X <= size.Width && point.Y <= size.Height;
}
