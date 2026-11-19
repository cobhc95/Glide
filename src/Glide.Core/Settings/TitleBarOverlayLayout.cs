namespace Glide.Core.Settings;

/// <summary>
/// Where a title-bar text overlay goes and how much room the tab strip gives up for it (5.1.3). The overlay never overlaps
/// tabs, the New tab button or the title-bar/caption buttons: "left" pushes the tabs right, "right" reserves space just before
/// the right-hand button group, "center" sits centred in the window when the free space after the tabs allows it, otherwise
/// it takes the reserved space before the button group (the tabs shrink or scroll). All values are in chrome-row coordinates.
/// </summary>
public readonly record struct TitleBarOverlayLayout(double OverlayX, double TabsLeftReserve, double TabsRightReserve)
{
    public const double Padding = 8;

    /// <param name="placement">A title-bar placement (<see cref="TextOverlayPlacements.IsTitleBar"/>).</param>
    /// <param name="overlayWidth">Measured overlay width.</param>
    /// <param name="chromeWidth">Width of the whole title-bar row.</param>
    /// <param name="buttonGroupWidth">Width of the right-hand group (title-bar actions and caption buttons).</param>
    /// <param name="tabsWidth">Width the tabs and New tab button want when unconstrained (0 with side tabs / no tabs).</param>
    public static TitleBarOverlayLayout Compute(string placement, double overlayWidth, double chromeWidth, double buttonGroupWidth, double tabsWidth)
    {
        var w = Math.Max(0, overlayWidth);
        var tabsArea = Math.Max(0, chromeWidth - Math.Max(0, buttonGroupWidth));
        var reserve = w + Padding;
        var rightX = Math.Max(0, tabsArea - w - Padding / 2);
        switch (placement)
        {
            case TextOverlayPlacements.TitleBarLeft:
                return new(0, reserve, 0);
            case TextOverlayPlacements.TitleBarRight:
                return new(rightX, 0, reserve);
            default:
                var centerX = (chromeWidth - w) / 2;
                var tabsEnd = Math.Min(Math.Max(0, tabsWidth), tabsArea);
                if (centerX >= tabsEnd + Padding && centerX + w + Padding <= tabsArea)
                    return new(centerX, 0, 0);
                return new(rightX, 0, reserve);
        }
    }
}
