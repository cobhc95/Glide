using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Glide.App.Services;

/// <summary>
/// Tooltip placement and wheel behaviour. Avalonia opens tooltips at the pointer by default, which put the popup on
/// top of the hovered toolbar button (it could no longer be clicked and a neighbouring button never closed it), so
/// tooltips are anchored to their control instead, opening away from the window edge the control sits on.
/// It also keeps tooltips from getting in the way of the mouse wheel. A tooltip is its own popup window: while the
/// pointer is over it, the wheel goes to the popup instead of the list or page underneath, so scrolling
/// (for example the keyboard shortcut list in Settings) stopped working once a tooltip had appeared under
/// the cursor. Three measures work together:
/// <list type="bullet">
/// <item>every wheel tick that reaches a control first closes any tooltip open on it or its parents;</item>
/// <item>tooltips for rows of a scrolling list open beside the pointer, away from it, instead of hugging the row;</item>
/// <item>the tooltip popup itself is not hit-testable (see the ToolTip style in GlideTheme.axaml).</item>
/// </list>
/// </summary>
internal static class ToolTipGuard
{
    /// <summary>Offset (in DIPs) from the pointer for tooltips of scrolling-list rows: clear of the cursor glyph.</summary>
    internal const double PointerOffsetX = 18;
    internal const double PointerOffsetY = 30;

    private static bool _registered;

    /// <summary>Anchoring for a tooltip: the control's edge, or beside the pointer for rows of a scrolling list.</summary>
    internal static (PlacementMode Mode, double HorizontalOffset, double VerticalOffset) Placement(bool insideScrollingList, bool lowerHalf) =>
        insideScrollingList ? (PlacementMode.Pointer, PointerOffsetX, PointerOffsetY)
        : lowerHalf ? (PlacementMode.Top, 0, -6)
        : (PlacementMode.Bottom, 0, 6);

    internal static bool IsInsideScrollingList(Control control)
    {
        for (var current = control as Visual; current is not null; current = current.GetVisualParent())
            if (current is ListBox or ListBoxItem) return true;
        return false;
    }

    /// <summary>Idempotent; called once from application start-up (and by headless tests).</summary>
    internal static void Register()
    {
        if (_registered) return;
        _registered = true;

        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, _) =>
        {
            if (TopLevel.GetTopLevel(control) is not { } top) return;
            var origin = control.TranslatePoint(default, top);
            if (origin is null) return;
            var lowerHalf = origin.Value.Y + control.Bounds.Height / 2 > top.Bounds.Height / 2;
            var (mode, x, y) = Placement(IsInsideScrollingList(control), lowerHalf);
            ToolTip.SetPlacement(control, mode);
            ToolTip.SetHorizontalOffset(control, x);
            ToolTip.SetVerticalOffset(control, y);
        }, handledEventsToo: true);

        // Tunnel + handledEventsToo: runs before the list/scroll viewer consumes the tick, so a tooltip that
        // is open when the wheel turns is gone before the content moves underneath it.
        InputElement.PointerWheelChangedEvent.AddClassHandler<Control>((control, _) => CloseOpenToolTips(control),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Closes the tooltip of <paramref name="source"/> and of every visual parent that has one open.</summary>
    internal static void CloseOpenToolTips(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
            if (current is Control control && ToolTip.GetIsOpen(control))
                ToolTip.SetIsOpen(control, false);
    }
}
