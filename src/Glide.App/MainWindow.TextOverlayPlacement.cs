using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.Core.Settings;
using Glide.Core.Workspace;

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

    /// <summary>
    /// Whether the text overlay shows now: "Show text overlay" is on, Overlay mode is off, and it describes a picture that is on
    /// screen. Over the picture it lives in the picture view, which is hidden in an Explorer or Welcome tab; in the title bar or
    /// the tab pane it would stay on screen with the last picture's text (5.1.7 UI test, point 13), so it hides there too.
    /// </summary>
    internal bool TextOverlayWanted => OverlayOptionsView.PictureCounterEnabled && !_wholeAppOverlayMode
        && (TextOverlayPlacements.IsOverPicture(EffectiveTextOverlayPlacement) || _workspace.Active is ImageTabState);

    /// <summary>Re-evaluates <see cref="TextOverlayWanted"/>; called when the active tab or surface changes.</summary>
    private void UpdateTextOverlayVisibility()
    {
        if (CornerCounterHost is null) return;
        var wanted = TextOverlayWanted;
        if (CornerCounterHost.IsVisible == wanted) return;
        CornerCounterHost.IsVisible = wanted;
        ReserveSideTabSpaceForTextOverlay();
        LayoutTitleBarTextOverlay();
    }

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
            // 5.1.4: bottom positions sit above the status bar; follow it as it shows/hides or changes size.
            ViewerStatusSurface.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty || e.Property == Visual.BoundsProperty || e.Property == Visual.OpacityProperty)
                    UpdateTextOverlayPictureMargin();
            };
            ImageView.SizeChanged += (_, _) => UpdateTextOverlayPictureMargin();
            CornerCounterHost.SizeChanged += (_, _) => ApplyTextOverlayPictureOffset();
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

    private const double TextOverlayEdgeGap = 16;

    /// <summary>
    /// Margin of the text overlay over the picture (5.1.4). Bottom positions are measured from the top edge of the status
    /// bar when it is shown (and from the window bottom when hidden), so the offset (setting or Alt+drag, a translation from
    /// this place) is always relative to that reference and the overlay moves with the status bar.
    /// </summary>
    private Thickness TextOverlayPictureMargin()
    {
        if (CornerCounterHost.VerticalAlignment != VerticalAlignment.Bottom) return new Thickness(TextOverlayPictureSideGap, TextOverlayPictureTopGap, TextOverlayPictureSideGap, 0);
        return new Thickness(TextOverlayPictureSideGap, 0, TextOverlayPictureSideGap, TextOverlayBottomInset());
    }

    /// <summary>The overlay never rests closer than this to the picture area's edge when clamped or dragged: flush against the window edge its last glyph was cut off.</summary>
    internal const double TextOverlayClampInset = 4;

    private const double TextOverlayPictureSideGap = 15;
    private const double TextOverlayPictureTopGap = 28;

    private double TextOverlayBottomInset() => TextOverlayLayout.BottomInset(ImageView.Bounds.Height,
        ViewerStatusSurface.IsVisible && ViewerStatusSurface.Opacity > 0, ViewerStatusSurface.Bounds.Top, TextOverlayEdgeGap);

    private void UpdateTextOverlayPictureMargin()
    {
        if (CornerCounterHost is null || !ReferenceEquals(CornerCounterHost.Parent, ImageView) || _textOverlayDragPointer is not null) return;
        var wanted = TextOverlayPictureMargin();
        if (CornerCounterHost.Margin != wanted) CornerCounterHost.Margin = wanted;
        ApplyTextOverlayPictureOffset();
    }

    /// <summary>Round 4 B3: the saved offset, limited so the overlay stays inside the picture area.</summary>
    private void ApplyTextOverlayPictureOffset()
    {
        if (CornerCounterHost is null || !ReferenceEquals(CornerCounterHost.Parent, ImageView) || _textOverlayDragPointer is not null) return;
        var (x, y) = OverlayOptionsView.PictureCounterOffset(SideTabsActive);
        (x, y) = TextOverlayLayout.ClampOffset(OverlayOptionsView.PictureCounterAnchor(SideTabsActive), ImageView.Bounds.Width, ImageView.Bounds.Height,
            CornerCounterHost.Bounds.Width, CornerCounterHost.Bounds.Height, x, y, TextOverlayPictureSideGap, TextOverlayPictureTopGap, TextOverlayBottomInset(), TextOverlayClampInset);
        var current = CornerCounterHost.RenderTransform as TranslateTransform;
        if (x == 0 && y == 0) { if (current is not null) CornerCounterHost.RenderTransform = null; return; }
        if (current is null || current.X != x || current.Y != y) CornerCounterHost.RenderTransform = new TranslateTransform(x, y);
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
            // DesiredSize includes the margin this method sets (feedback loop -> flicker in 5.1.3); Bounds does not.
            var overlayWidth = CornerCounterHost.DesiredSize.Width > 0
                ? TitleBarOverlayLayout.ContentWidth(CornerCounterHost.DesiredSize.Width, CornerCounterHost.Margin.Left, CornerCounterHost.Margin.Right)
                : CornerCounterHost.Bounds.Width;
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
        var old = TabOverflowHost.Margin;
        if (Math.Abs(old.Left - tabsMargin.Left) >= 1 || Math.Abs(old.Right - tabsMargin.Right) >= 1 || (old != tabsMargin && tabsMargin == default))
        {
            TabOverflowHost.Margin = tabsMargin;
            // Tab widths follow the strip width: rebuild once the new margin has been laid out.
            Avalonia.Threading.Dispatcher.UIThread.Post(RebuildTabStrip, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
