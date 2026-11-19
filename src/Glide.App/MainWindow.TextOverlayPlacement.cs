using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Glide.Core.Settings;

namespace Glide.App;

/// <summary>
/// "Text overlay with vertical tabs" (5.1.2): with the side tab pane showing, the text overlay can sit at the top or
/// bottom of the tab pane (the pane reserves that space, so no tab is ever under it) or in the title bar. Over the
/// picture (default) keeps the original place. The overlay is moved between the picture grid and the window root.
/// </summary>
public partial class MainWindow
{
    private const double SideTabHostBasePadding = 6;
    private bool _textOverlaySizeHooked;

    /// <summary>The effective placement: the vertical-tabs choice applies only while the side tab pane is active.</summary>
    internal string EffectiveTextOverlayPlacement =>
        SideTabsActive ? TextOverlayPlacements.Normalize(_settings.PictureCounterSideTabsPlacement) : TextOverlayPlacements.OverPicture;

    /// <summary>Moves the text overlay to its place; returns true when it is over the picture (normal layout applies).</summary>
    private bool PlaceTextOverlay()
    {
        if (CornerCounterHost is null || SideTabHost is null) return true;
        var placement = EffectiveTextOverlayPlacement;
        var overPicture = placement == TextOverlayPlacements.OverPicture;
        Panel target = overPicture ? ImageView : MainRoot;
        if (!ReferenceEquals(CornerCounterHost.Parent, target))
        {
            (CornerCounterHost.Parent as Panel)?.Children.Remove(CornerCounterHost);
            target.Children.Add(CornerCounterHost);
        }
        if (!_textOverlaySizeHooked)
        {
            _textOverlaySizeHooked = true;
            CornerCounterHost.SizeChanged += (_, _) => ReserveSideTabSpaceForTextOverlay();
        }
        if (!overPicture)
        {
            CornerCounterHost.RenderTransform = null;
            CornerCounterHost.ZIndex = 902;
            if (placement == TextOverlayPlacements.TitleBar)
            {
                Grid.SetRow(CornerCounterHost, 0);
                CornerCounterHost.HorizontalAlignment = HorizontalAlignment.Center;
                CornerCounterHost.VerticalAlignment = VerticalAlignment.Center;
                CornerCounterHost.Margin = default;
            }
            else
            {
                Grid.SetRow(CornerCounterHost, 1);
                CornerCounterHost.HorizontalAlignment = HorizontalAlignment.Left;
                CornerCounterHost.VerticalAlignment = placement == TextOverlayPlacements.TabBarTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                CornerCounterHost.Width = Math.Max(20, SideTabPaneWidth - 2 * SideTabHostBasePadding - 1);
                CornerCounterHost.Margin = new Thickness(SideTabHostBasePadding);
            }
        }
        else
        {
            Grid.SetRow(CornerCounterHost, 0);
            CornerCounterHost.ZIndex = 0;
            CornerCounterHost.Width = double.NaN;
        }
        ReserveSideTabSpaceForTextOverlay();
        return overPicture;
    }

    /// <summary>The side tab pane leaves room for the text overlay at its top or bottom; also follows pane visibility (auto-hide).</summary>
    private void ReserveSideTabSpaceForTextOverlay()
    {
        if (SideTabHost is null || CornerCounterHost is null) return;
        var placement = EffectiveTextOverlayPlacement;
        var inPane = placement is TextOverlayPlacements.TabBarTop or TextOverlayPlacements.TabBarBottom;
        var reserve = inPane && CornerCounterHost.IsVisible ? CornerCounterHost.Bounds.Height + SideTabHostBasePadding : 0;
        var b = SideTabHostBasePadding;
        var wanted = new Thickness(b, b + (placement == TextOverlayPlacements.TabBarTop ? reserve : 0), b, b + (placement == TextOverlayPlacements.TabBarBottom ? reserve : 0));
        if (SideTabHost.Padding != wanted) SideTabHost.Padding = wanted;
        if (inPane) CornerCounterHost.Opacity = SideTabHost.IsVisible ? 1 : 0;
        else CornerCounterHost.Opacity = 1;
    }
}
