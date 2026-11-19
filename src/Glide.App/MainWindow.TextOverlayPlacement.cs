using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Glide.Core.Settings;

namespace Glide.App;

/// <summary>
/// Text overlay placement (5.1.3): each tab layout has its own position and alignment. With tabs at the side the overlay
/// can also sit at the top or bottom of the tab pane (the pane reserves that space, so no tab is ever under it); with
/// either layout it can sit in the title bar or over the picture. The overlay is moved between the picture grid and the window root.
/// </summary>
public partial class MainWindow
{
    private const double SideTabHostBasePadding = 6;
    private bool _textOverlaySizeHooked;

    /// <summary>The position of the settings card matching the current tab layout (tabs along the top / at the side).</summary>
    internal string EffectiveTextOverlayPlacement => OverlayOptionsView.PictureCounterPosition(SideTabsActive);

    /// <summary>The alignment of the settings card matching the current tab layout.</summary>
    internal string EffectiveTextOverlayAlignment => OverlayOptionsView.PictureCounterAlignment(SideTabsActive);

    /// <summary>Moves the text overlay to its place; returns true when it is over the picture (normal layout applies).</summary>
    private bool PlaceTextOverlay()
    {
        if (CornerCounterHost is null || SideTabHost is null) return true;
        var placement = EffectiveTextOverlayPlacement;
        var overPicture = TextOverlayPlacements.IsOverPicture(placement);
        Panel target = overPicture ? ImageView : MainRoot;
        if (!ReferenceEquals(CornerCounterHost.Parent, target))
        {
            (CornerCounterHost.Parent as Panel)?.Children.Remove(CornerCounterHost);
            target.Children.Add(CornerCounterHost);
        }
        if (!_textOverlaySizeHooked)
        {
            _textOverlaySizeHooked = true;
            CornerCounterHost.SizeChanged += (_, _) => { ReserveSideTabSpaceForTextOverlay(); LayoutTitleBarTextOverlay(); };
            if (ChromeLayout is not null) ChromeLayout.SizeChanged += (_, _) => LayoutTitleBarTextOverlay();
        }
        if (!overPicture)
        {
            CornerCounterHost.RenderTransform = null;
            CornerCounterHost.ZIndex = 902;
            if (TextOverlayPlacements.IsTitleBar(placement))
            {
                Grid.SetRow(CornerCounterHost, 0);
                CornerCounterHost.Width = double.NaN;
                CornerCounterHost.HorizontalAlignment = HorizontalAlignment.Left;
                CornerCounterHost.VerticalAlignment = VerticalAlignment.Center;
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
        LayoutTitleBarTextOverlay();
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

    /// <summary>
    /// Title-bar placements: the overlay never overlaps tabs, the New tab button or the right-hand buttons. The tab strip gives
    /// up room for it (<see cref="TitleBarOverlayLayout"/>); re-run when the overlay, the title bar or the tab count changes size.
    /// </summary>
    private void LayoutTitleBarTextOverlay()
    {
        if (CornerCounterHost is null || ChromeLayout is null || TabOverflowHost is null) return;
        var placement = EffectiveTextOverlayPlacement;
        var topTabsInChrome = ReferenceEquals(TabOverflowHost.Parent, ChromeLayout);
        var tabsMargin = default(Thickness);
        if (TextOverlayPlacements.IsTitleBar(placement) && CornerCounterHost.IsVisible)
        {
            var overlayWidth = CornerCounterHost.DesiredSize.Width > 0 ? CornerCounterHost.DesiredSize.Width : CornerCounterHost.Bounds.Width;
            var chromeWidth = ChromeLayout.Bounds.Width > 0 ? ChromeLayout.Bounds.Width : Math.Max(0, Bounds.Width - 8);
            var groupWidth = 0.0;
            foreach (var child in ChromeLayout.Children)
                if (!ReferenceEquals(child, TabOverflowHost) && child.IsVisible && Grid.GetColumn(child) > 0)
                    groupWidth += child.Bounds.Width + child.Margin.Left + child.Margin.Right;
            var tabsWidth = 0.0;
            if (topTabsInChrome && _settings.TabsEnabled)
            {
                var count = Math.Max(1, _workspace.Tabs.Count);
                var max = Math.Clamp(_settings.TabMaxWidth, 120, 400);
                tabsWidth = count * (max + 3) + (NewTabButton.IsVisible ? 43 : 0);
            }
            var layout = TitleBarOverlayLayout.Compute(placement, overlayWidth, chromeWidth, groupWidth, tabsWidth);
            var chromeLeft = ChromeLayout.Margin.Left;
            var wanted = new Thickness(chromeLeft + layout.OverlayX, 0, 0, 0);
            if (CornerCounterHost.Margin != wanted) CornerCounterHost.Margin = wanted;
            if (topTabsInChrome) tabsMargin = new Thickness(layout.TabsLeftReserve, 0, layout.TabsRightReserve, 0);
        }
        if (TabOverflowHost.Margin != tabsMargin)
        {
            TabOverflowHost.Margin = tabsMargin;
            // Tab widths follow the strip width: rebuild once the new margin has been laid out.
            Avalonia.Threading.Dispatcher.UIThread.Post(RebuildTabStrip, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
