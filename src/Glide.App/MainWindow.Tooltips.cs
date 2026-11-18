using Avalonia.Controls;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.Core.Settings;

namespace Glide.App;

/// <summary>
/// 5.1.0 Tooltips: each area's show delay comes from the master list (<see cref="TooltipAreas"/>) and is applied as one
/// style per area root: the window (Main interface, or Overlay mode while it is on) and the Explorer item list. Called on
/// every settings apply (also when Overlay mode starts or ends), so changes apply live.
/// </summary>
public partial class MainWindow
{
    private void ApplyTooltipDelays()
    {
        TooltipDelayStyle.Apply(this, TooltipAreas.DelayMs(_settings, _wholeAppOverlayMode ? TooltipAreas.Overlay : TooltipAreas.Main));
        if (_browserSurface is not { } surface) return;
        TooltipDelayStyle.Apply(surface.Repeater, TooltipAreas.DelayMs(_settings, TooltipAreas.Explorer));
        // "Show info tooltip when hovering items": realized tiles follow at once; others when they are realized.
        foreach (var tile in RealizedExplorerTiles())
            if (tile.Tag is ExplorerEntry entry) ToolTip.SetTip(tile, ExplorerOptionsView.ItemInfoTooltip ? BrowserTooltipText(entry) : null);
    }
}
