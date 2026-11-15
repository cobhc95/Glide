using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Glide.App.Services;

namespace Glide.App;

/// <summary>
/// Vertical tabs. "Tab position" = Side moves the whole tab strip (TabOverflowHost: tabs and New tab) out of
/// the title bar into a resizable pane on the left of the window; the tabs stack vertically and the pane scrolls.
/// The pane is switched from the tab / title-bar context menus ("Place tabs vertically" / "Place tabs on top"),
/// the Settings "Tab position" option or the bindable "Cycle: Tab position" action. "Auto-hide the side tab panel"
/// hides it until the pointer touches the window's left edge (it then overlays the image, like the auto-hide
/// status bar). "Auto-hide the top tab strip" does the same for the title row at the top edge.
/// </summary>
public partial class MainWindow
{
    internal const double SideTabPanelWidth = 220;
    internal const double SideTabPanelMinWidth = 150;
    internal const double SideTabPanelMaxWidth = 480;
    private bool _sideTabsRevealed;
    private bool _sideTabResizeWired;
    private IPointer? _sideTabResizePointer;

    /// <summary>The pane width the user chose (dragging its right edge), clamped to the allowed range.</summary>
    internal double SideTabPaneWidth => ClampSideTabPaneWidth(_settings.TabSidePanelWidth);

    internal static double ClampSideTabPaneWidth(double width) =>
        double.IsFinite(width) && width > 0 ? Math.Clamp(width, SideTabPanelMinWidth, SideTabPanelMaxWidth) : SideTabPanelWidth;

    /// <summary>True when the tabs are shown in the vertical side panel.</summary>
    internal bool SideTabsActive => _settings.TabsEnabled && string.Equals(_settings.TabStripPosition, "Side", StringComparison.OrdinalIgnoreCase)
                                    && WindowState != WindowState.FullScreen && !_wholeAppOverlayMode;

    /// <summary>True when the windowed top title/tab row hides until the pointer reaches the top edge.</summary>
    internal bool TopTabsAutoHideActive => _settings.TabsEnabled && _settings.TabTopAutoHide && !SideTabsActive && !_wholeAppOverlayMode;

    /// <summary>Puts the tab strip where the settings say (top title row or side panel) and lays the window out for it.</summary>
    private void ApplyTabPosition()
    {
        if (SideTabHost is null || TabOverflowHost is null) return;
        WireSideTabResizeHandle();
        var side = SideTabsActive;
        var currentlySide = ReferenceEquals(TabOverflowHost.Parent, SideTabHost);
        if (side != currentlySide)
        {
            if (TabOverflowHost.Parent is Panel oldPanel) oldPanel.Children.Remove(TabOverflowHost);
            else if (TabOverflowHost.Parent is Decorator oldDecorator) oldDecorator.Child = null;
            if (side) SideTabHost.Child = TabOverflowHost;
            else ChromeLayout.Children.Insert(0, TabOverflowHost);
            // Tab widths come from the strip's measured width, which still describes the old place until the next
            // layout pass (switching to the top left tabs squashed at the side-pane width). Re-run the same
            // tab-width pass as a resize once the moved strip has been measured.
            Dispatcher.UIThread.Post(() =>
            {
                UpdateLayout();
                RebuildTabStrip();
                UpdateTabOverflowButtons();
            }, DispatcherPriority.Loaded);
        }

        var orientation = side ? Orientation.Vertical : Orientation.Horizontal;
        TabStripHost.Orientation = orientation;
        TabStripPanel.Orientation = orientation;
        TabStripHost.VerticalAlignment = side ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        TabStripScroll.HorizontalScrollBarVisibility = side ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        TabStripScroll.VerticalScrollBarVisibility = side ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        if (side)
        {
            TabOverflowLeftButton.IsVisible = false;
            TabOverflowRightButton.IsVisible = false;
        }

        var width = SideTabPaneWidth;
        ApplyNewTabButtonShape(side, width);
        SideTabHost.Width = width;
        var autoHide = side && _settings.TabSideAutoHide;
        SideTabHost.IsVisible = side && (!autoHide || _sideTabsRevealed);
        if (!side) _sideTabsRevealed = false;
        SideTabResizeHandle.IsVisible = SideTabHost.IsVisible;
        SideTabResizeHandle.Margin = new Thickness(Math.Max(0, width - 3), 0, 0, 0);
        // A pinned side panel takes its own column of space; an auto-hide one overlays the image when revealed.
        WorkspaceLayer.Margin = new Thickness(side && !autoHide ? width : 0, 0, 0, 0);
    }

    /// <summary>In the side pane the New tab button is a full-width row like a tab (icon left, "New tab" label).</summary>
    private void ApplyNewTabButtonShape(bool side, double paneWidth)
    {
        NewTabLabel.IsVisible = side;
        NewTabButton.HorizontalContentAlignment = side ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        NewTabButton.Padding = side ? new Thickness(11, 0, 8, 0) : new Thickness(0);
        if (side)
        {
            NewTabButton.Width = paneWidth - 14;
            NewTabButton.Height = 36;
            NewTabButton.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            NewTabButton.Width = 36;
            NewTabButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    /// <summary>Auto-hide side panel: reveal at the left edge, hide shortly after the pointer leaves it.</summary>
    private void UpdateSideTabsAutoHide(Point pointer)
    {
        if (!SideTabsActive || !_settings.TabSideAutoHide || SideTabHost is null || _sideTabResizePointer is not null) return;
        var top = MainRoot.RowDefinitions.Count > 0 ? MainRoot.RowDefinitions[0].ActualHeight : 44;
        if (!_sideTabsRevealed)
        {
            if (pointer.X <= 6 && pointer.Y > top)
            {
                _sideTabsRevealed = true;
                SideTabHost.IsVisible = true;
                SideTabResizeHandle.IsVisible = true;
            }
            return;
        }
        if (_tabDrag.IsActive) return;
        if (pointer.X > SideTabPaneWidth + 24 || pointer.Y < top - 4)
        {
            _sideTabsRevealed = false;
            SideTabHost.IsVisible = false;
            SideTabResizeHandle.IsVisible = false;
        }
    }

    // ---- Resizable pane -----------------------------------------------------------------------------------

    private void WireSideTabResizeHandle()
    {
        if (_sideTabResizeWired || SideTabResizeHandle is null) return;
        _sideTabResizeWired = true;
        // Right-click on the empty part of the pane (tabs open their own menu first and mark it handled).
        SideTabHost.ContextRequested += (_, e) =>
        {
            if (e.Handled) return;
            ShowTabAreaContextMenu(SideTabHost);
            e.Handled = true;
        };
        SideTabResizeHandle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(SideTabResizeHandle).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            _sideTabResizePointer = e.Pointer;
            e.Pointer.Capture(SideTabResizeHandle);
            e.Handled = true;
        };
        SideTabResizeHandle.PointerMoved += (_, e) =>
        {
            if (_sideTabResizePointer is null || e.Pointer != _sideTabResizePointer) return;
            var x = e.GetPosition(MainRoot).X;
            SetSideTabPaneWidth(x, save: false);
            e.Handled = true;
        };
        SideTabResizeHandle.PointerReleased += (_, e) =>
        {
            if (_sideTabResizePointer is null) return;
            _sideTabResizePointer = null;
            e.Pointer.Capture(null);
            SaveSettingsAndPublish();
            e.Handled = true;
        };
        SideTabResizeHandle.PointerCaptureLost += (_, _) => _sideTabResizePointer = null;
        SideTabResizeHandle.DoubleTapped += (_, e) =>
        {
            SetSideTabPaneWidth(SideTabPanelWidth, save: true);
            e.Handled = true;
        };
    }

    /// <summary>Sets the pane width (clamped), re-lays out the pane and its tabs and, when asked, saves it.</summary>
    internal void SetSideTabPaneWidth(double width, bool save)
    {
        var clamped = (int)Math.Round(ClampSideTabPaneWidth(width));
        if (clamped == _settings.TabSidePanelWidth && !save) return;
        _settings.TabSidePanelWidth = clamped;
        ApplyTabPosition();
        RebuildTabStrip();
        if (save) SaveSettingsAndPublish();
    }

    // ---- Switching (context menus + the "Cycle: Tab position" action) ------------------------------------

    /// <summary>"Place tabs vertically" / "Place tabs on top": tab context menu and the empty title-bar / tab-pane menu.</summary>
    private MenuItem BuildTabPlacementMenuItem()
    {
        var side = string.Equals(_settings.TabStripPosition, "Side", StringComparison.OrdinalIgnoreCase);
        var item = MenuActions.Tag(new MenuItem
        {
            Header = MenuActions.Header(side ? "Place tabs on top" : "Place tabs vertically", ShortcutForAction("setting.tabs.position")),
            Icon = MenuIcon(side ? "Up" : "Tabs")
        }, "setting.tabs.position");
        item.Click += (_, _) => ToggleTabPlacement();
        return item;
    }

    internal void ToggleTabPlacement()
    {
        _settings.TabStripPosition = string.Equals(_settings.TabStripPosition, "Side", StringComparison.OrdinalIgnoreCase) ? "Top" : "Side";
        SaveSettingsAndPublish();
        ApplyChromeLayoutForWindowState();
        // Measure the moved strip in its new place first, so the tab widths are those of a fresh layout.
        UpdateLayout();
        RebuildTabStrip();
    }

    /// <summary>Right-click on the empty part of the title bar or of the side tab pane.</summary>
    private void ShowTabAreaContextMenu(Control anchor)
    {
        var menu = new ContextMenu { ItemsSource = new List<object> { BuildTabPlacementMenuItem() } };
        PrepareOwnedContextMenu(menu).Open(anchor);
    }
}
